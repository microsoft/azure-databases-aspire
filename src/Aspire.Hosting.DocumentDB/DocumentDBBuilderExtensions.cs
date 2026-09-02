// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.DocumentDB;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding DocumentDB resources to an <see cref="IDistributedApplicationBuilder"/>.
/// </summary>
public static class DocumentDBBuilderExtensions
{
    private sealed class OpenTelemetryGatewayConfigurationAnnotation : IResourceAnnotation
    {
        public bool ServiceNameConfigured { get; set; }
        public bool ServiceVersionConfigured { get; set; }
        public bool EntrypointOwned { get; set; }
    }

    private enum GatewayConfigurationRequirement
    {
        NotConfigured,
        NotApplicable,
        Required,
    }

    /// <summary>
    /// The callbacks this package is allowed to own on a resource, and the ordered steps each one
    /// runs. See <see cref="EnsureTerminalGuard"/> for the contract.
    /// </summary>
    private sealed class TerminalGuardAnnotation : IResourceAnnotation
    {
        private readonly List<(int Rank, int Sequence, Action<TerminalCommandLineState> Step)> _commandLineSteps = [];
        private readonly List<Action<TerminalCommandLineState>> _commandLineValidations = [];
        private int _sequence;

        /// <summary>
        /// The command-line callback whose position in the annotation collection is what makes this
        /// guard terminal. The instance never changes, so Aspire's per-callback result cache
        /// survives every move.
        /// </summary>
        public CommandLineArgsCallbackAnnotation CommandLineCallback { get; set; } = null!;

        /// <summary>
        /// The container-runtime-arguments callback. It contributes nothing and exists only to be
        /// a checkpoint: Aspire never caches these, so it is the one piece of this package's code
        /// that is guaranteed to run on every container creation.
        /// </summary>
        public ContainerRuntimeArgsCallbackAnnotation RuntimeCheckpoint { get; set; } = null!;

        /// <summary>
        /// The manifest publishing callback. It is the publish counterpart of
        /// <see cref="RuntimeCheckpoint"/>: it verifies the cached terminal state while the
        /// resource is actually being serialized, then hands writing on to the callback it
        /// displaced.
        /// </summary>
        public ManifestPublishingCallbackAnnotation ManifestCheckpoint { get; set; } = null!;

        /// <summary>
        /// What the resource looked like when the command-line callback last produced a result, or
        /// <see langword="null"/> if it has not run yet.
        /// </summary>
        public TerminalConfigurationSeal? Seal { get; set; }

        public void AddCommandLineStep(int rank, Action<TerminalCommandLineState> step) =>
            _commandLineSteps.Add((rank, _sequence++, step));

        public void AddCommandLineValidation(Action<TerminalCommandLineState> validation) =>
            _commandLineValidations.Add(validation);

        public TerminalCommandLineState RunCommandLine(CommandLineArgsCallbackContext context)
        {
            var state = new TerminalCommandLineState(context);

            foreach (var (_, _, step) in _commandLineSteps.OrderBy(step => step.Rank).ThenBy(step => step.Sequence))
            {
                step(state);
            }

            foreach (var validation in _commandLineValidations)
            {
                validation(state);
            }

            return state;
        }

    }

    /// <summary>
    /// Everything the container's command depends on, as it stood when the command-line callback
    /// produced the result Aspire cached.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Aspire evaluates each callback annotation at most once per run and reuses the recorded
    /// result afterwards, so a callback that validated a configuration cannot notice the
    /// configuration changing behind it. Worse, the gatherer takes the <em>last</em> annotation's
    /// recorded result as the final argument list, so an annotation appended after a first
    /// evaluation does not merely reorder the command line - it replaces it, because the earlier
    /// annotations no longer write into the shared list at all.
    /// </para>
    /// <para>
    /// The seal is what makes that detectable. It is compared at the checkpoints that Aspire never
    /// caches, so the answer the wrapper gave is either still the answer or the resource is failed.
    /// </para>
    /// </remarks>
    private sealed record TerminalConfigurationSeal(
        System.Collections.Immutable.ImmutableArray<CommandLineArgsCallbackAnnotation> CommandLineCallbacks,
        System.Collections.Immutable.ImmutableArray<EnvironmentCallbackAnnotation> EnvironmentCallbacks,
        System.Collections.Immutable.ImmutableArray<ContainerRuntimeArgsCallbackAnnotation> RuntimeCallbacks,
        string? Entrypoint,
        DocumentDBEffectiveImage Image,
        TerminalCommandSeal Command);

    /// <summary>
    /// The fixed, non-secret-bearing part of the command result the terminal callback returned to
    /// Aspire's immutable cache.
    /// </summary>
    private sealed record TerminalCommandSeal(
        GatewayConfigurationRequirement GatewayRequirement,
        string? WrapperScript,
        string? ShellOption,
        bool ScriptIsSecondArgument,
        string? Delimiter,
        bool HasDuplicateWrapperScript);

    /// <summary>
    /// What one evaluation of the terminal command-line guard produced. Scoped to the evaluation
    /// rather than stored on an annotation, so a validation always judges the arguments its own
    /// steps built.
    /// </summary>
    private sealed class TerminalCommandLineState(CommandLineArgsCallbackContext context)
    {
        public CommandLineArgsCallbackContext Context { get; } = context;

        public IList<object> Args => Context.Args;

        /// <summary>
        /// The exact OpenTelemetry wrapper script instance this evaluation inserted, or
        /// <see langword="null"/> when the wrapper was not applied.
        /// </summary>
        public string? WrapperScript { get; set; }
    }

    /// <summary>
    /// Rank of the data-storage rules among the terminal guard's steps. Lowest in the package: the
    /// rule models the container entrypoint's own <c>--option value</c> grammar, so it has to see
    /// the caller's arguments as the entrypoint will, before the OpenTelemetry wrapper turns the
    /// list into a <c>/bin/bash -c</c> command line.
    /// </summary>
    private const int TerminalCommandLineDataStorageRank = 0;

    /// <summary>
    /// Rank of the OpenTelemetry gateway wrapper among the terminal guard's steps. It is the
    /// highest-ranked step in this package on purpose: the wrapper turns the argument list into a
    /// <c>/bin/bash -c</c> command line, so every step that reads arguments as the container
    /// entrypoint's own <c>--option value</c> grammar has to have run first.
    /// </summary>
    private const int TerminalCommandLineOpenTelemetryWrapperRank = 100;
    private const string ManifestPublishingPipelineStepName = "publish-manifest";
    private const string TerminalManifestCheckpointPipelineStepName =
        "documentdb-terminal-manifest-checkpoint";

    // default internal port is 10260.
    private const int DefaultContainerPort = 10260;
    // default PostgreSQL coordinator port inside the documentdb-local container.
    private const int DefaultPostgresContainerPort = 9712;
    private const string DefaultHealthCheckDatabaseName = "admin";
    private static readonly Version FirstGatewayTelemetryConfigurationVersion = new(0, 116, 0);

    private const string UserEnvVarName = "USERNAME";
    private const string PasswordEnvVarName = "PASSWORD";
    private const string LogLevelEnvVarName = "DOCUMENTDB_LOG_LEVEL";
    private const string LegacyLogLevelEnvVarName = "LOG_LEVEL";
    private const string InitDataEnvVarName = "INIT_DATA";
    private const string InitDataPathEnvVarName = "INIT_DATA_PATH";
    private const string SkipInitDataEnvVarName = "SKIP_INIT_DATA";
    private const string CertPathEnvVarName = "CERT_PATH";
    private const string KeyFileEnvVarName = "KEY_FILE";
    private const string EnableTelemetryEnvVarName = "ENABLE_TELEMETRY";
    private const string OtelMetricsEnabledEnvVarName = "OTEL_METRICS_ENABLED";
    private const string OtelExporterOtlpMetricsEndpointEnvVarName = "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT";
    private const string OtelExporterOtlpMetricsTimeoutEnvVarName = "OTEL_EXPORTER_OTLP_METRICS_TIMEOUT";
    private const string OtelMetricExportIntervalEnvVarName = "OTEL_METRIC_EXPORT_INTERVAL";
    private const string OtelServiceNameEnvVarName = "OTEL_SERVICE_NAME";
    private const string OtelServiceVersionEnvVarName = "OTEL_SERVICE_VERSION";
    private const string OwnerEnvVarName = "OWNER";
    private const string DataPathEnvVarName = "DATA_PATH";
    private const string DisableExtendedRumEnvVarName = "DISABLE_EXTENDED_RUM";
    private const string CreateUserEnvVarName = "CREATE_USER";
    private const string AllowExternalConnectionsEnvVarName = "ALLOW_EXTERNAL_CONNECTIONS";

    private const string PostgresEndpointLoggerCategory = "Aspire.Hosting.DocumentDB.WithPostgresEndpoint";
    private const string StorageLoggerCategory = "Aspire.Hosting.DocumentDB.Storage";

    private const string DefaultMountedDataPath = "/data";
    private const string InitDataMountPath = "/init_doc_db.d";
    private const string DefaultGatewayHome = "/home/documentdb/gateway";
    private const string PackagedGatewayConfigurationDirectory = "/etc/documentdb/gateway";
    private const string PackagedLayoutProbeScript = "/usr/share/documentdb/scripts/start_oss_server.sh";
    private const string PackagedLayoutProbeUtils = "/usr/share/documentdb/scripts/utils.sh";
    private const string GatewayEntrypointScriptPath = "/home/documentdb/gateway/scripts/emulator_entrypoint.sh";
    private const string GatewayConfigurationShell = "/bin/bash";
    private const string GatewayConfigurationShellCommandOption = "-c";
    private const string GatewayConfigurationShellArgumentZero = "--";
    private const string GatewayValueTakingOptionsShellPattern =
        "--allow-external-connections|--cert-path|--create-user|--documentdb-port|--enable-telemetry|" +
        "--init-data|--init-data-path|--key-file|--log-level|--owner|--password|--pg-port|--start-pg|" +
        "--tlsMode|--toast-compression|--username";

    /// <summary>
    /// Builds the wrapper script that makes the OpenTelemetry environment variables this package
    /// writes authoritative over the gateway's <c>SetupConfiguration.json</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The configuration directory is resolved exactly the way the image's own entrypoint resolves
    /// it: an explicit <c>CONFIG_DIR</c> first, then the packaged layout at
    /// <c>/etc/documentdb/gateway</c> when the scripts that layout is keyed on are present, then
    /// <c>$GATEWAY_HOME/pg_documentdb_gw</c> with the upstream <c>GATEWAY_HOME</c> default. Any
    /// other resolution would sanitize a file the gateway does not read.
    /// </para>
    /// <para>
    /// Single-line and brace-free on purpose. Publishers post-process container arguments: azd
    /// evaluates <c>{...}</c> in every argument as a manifest binding expression, so a shell
    /// <c>${VAR:-default}</c> is either passed through by luck or rejected outright, and a newline
    /// turns the rendered YAML scalar into a block scalar.
    /// </para>
    /// </remarks>
    private static string BuildOpenTelemetryGatewayConfigurationScript(
        OpenTelemetryGatewayConfigurationAnnotation configuration) =>
        "set -e; " +
        "c=\"$CONFIG_DIR\"; " +
        "if [ -z \"$c\" ]; then " +
            $"if [ -f \"{PackagedLayoutProbeScript}\" ] && [ -f \"{PackagedLayoutProbeUtils}\" ]; then " +
                $"c=\"{PackagedGatewayConfigurationDirectory}\"; " +
            "else " +
                "g=\"$GATEWAY_HOME\"; " +
                $"if [ -z \"$g\" ]; then g=\"{DefaultGatewayHome}\"; fi; " +
                "c=\"$g/pg_documentdb_gw\"; " +
            "fi; " +
        "fi; " +
        "s=\"$c/SetupConfiguration.json\"; " +
        "if [ ! -r \"$s\" ]; then echo \"aspire-documentdb -- gateway configuration $s is missing or unreadable\" >&2; exit 1; fi; " +
        "if ! command -v jq >/dev/null 2>&1; then echo \"aspire-documentdb -- jq is required to make the OpenTelemetry environment variables authoritative\" >&2; exit 1; fi; " +
        "if ! command -v realpath >/dev/null 2>&1; then echo \"aspire-documentdb -- realpath is required to keep the telemetry configuration outside DATA_PATH\" >&2; exit 1; fi; " +
        "d=\"$DATA_PATH\"; q=\"\"; " +
        "for a in \"$@\"; do " +
            "if [ \"$q\" = \"d\" ]; then d=\"$a\"; q=\"\"; continue; fi; " +
            "if [ \"$q\" = \"v\" ]; then q=\"\"; continue; fi; " +
            "case \"$a\" in " +
                "-d|--data-path) q=\"d\";; " +
                $"{GatewayValueTakingOptionsShellPattern}) q=\"v\";; " +
            "esac; " +
        "done; " +
        "if [ \"$q\" = \"d\" ]; then echo \"aspire-documentdb -- --data-path requires a value before telemetry configuration can be prepared\" >&2; exit 1; fi; " +
        $"if [ -z \"$d\" ]; then d=\"{DefaultMountedDataPath}\"; fi; " +
        "if ! d=\"$(realpath -m -- \"$d\" 2>/dev/null)\"; then echo \"aspire-documentdb -- DATA_PATH could not be canonicalized for the telemetry configuration\" >&2; exit 1; fi; " +
        "if [ \"$d\" = \"/\" ]; then echo \"aspire-documentdb -- no temporary directory can be safely separated from a root DATA_PATH\" >&2; exit 1; fi; " +
        "r=\"\"; " +
        "for x in /tmp /var/tmp /dev/shm; do " +
            "if ! x=\"$(realpath -m -- \"$x\" 2>/dev/null)\"; then continue; fi; " +
            "if [ ! -d \"$x\" ] || [ ! -w \"$x\" ]; then continue; fi; " +
            "case \"$x\" in \"$d\"|\"$d\"/*) continue;; esac; " +
            "case \"$d\" in \"$x\"|\"$x\"/*) continue;; esac; " +
            "r=\"$x\"; break; " +
        "done; " +
        "if [ -z \"$r\" ]; then echo \"aspire-documentdb -- no writable temporary directory is safely separated from DATA_PATH\" >&2; exit 1; fi; " +
        "if ! o=\"$(mktemp -d \"$r/aspire-documentdb-otel.XXXXXX\")\"; then echo \"aspire-documentdb -- could not create the temporary gateway configuration\" >&2; exit 1; fi; " +
        $"jq '{BuildOpenTelemetryGatewayConfigurationFilter(configuration)}' \"$s\" >\"$o/SetupConfiguration.json\"; " +
        "export CONFIG_DIR=\"$o\"; " +
        $"exec {GatewayEntrypointScriptPath} \"$@\"";

    /// <summary>
    /// Builds the <c>jq</c> filter that removes exactly the <c>SetupConfiguration.json</c> keys
    /// this package's environment variables have to win over, and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>TelemetryOptions.Metrics</c> object is removed whole, not key by key, because this
    /// API owns the metrics signal end to end. Any surviving key re-pins that setting ahead of
    /// the documented environment precedence - the shipped
    /// <c>OtlpEndpoint: http://localhost:4317</c> would beat
    /// <c>OTEL_EXPORTER_OTLP_METRICS_ENDPOINT</c> and <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> and
    /// export metrics into the container itself - and enumerating the keys individually would
    /// silently leave any field a later gateway release adds authoritative over the environment.
    /// Removing the object costs nothing on the stock image: the values it ships are the
    /// gateway's own compiled-in defaults.
    /// </para>
    /// <para>
    /// The identity keys are different: they are shared with tracing, and the shipped
    /// <c>ServiceName</c> is not the gateway's compiled-in default, so removing it would silently
    /// rename every signal. They are removed only when the caller explicitly supplied the
    /// corresponding parameter. <c>TelemetryOptions.Tracing</c> is never touched.
    /// </para>
    /// </remarks>
    private static string BuildOpenTelemetryGatewayConfigurationFilter(
        OpenTelemetryGatewayConfigurationAnnotation configuration)
    {
        var paths = new List<string> { ".TelemetryOptions.Metrics" };

        if (configuration.ServiceNameConfigured)
        {
            paths.Add(".TelemetryOptions.ServiceName");
        }

        if (configuration.ServiceVersionConfigured)
        {
            paths.Add(".TelemetryOptions.ServiceVersion");
        }

        return $"del({string.Join(", ", paths)})";
    }

    /// <summary>
    /// Adds a DocumentDB resource to the application model. A container is used for local development.
    /// </summary>
    /// <remarks>
    /// This resource includes a built-in health check. When this resource is referenced as a dependency
    /// using the <see cref="ResourceBuilderExtensions.WaitFor{T}(IResourceBuilder{T}, IResourceBuilder{IResource})"/>
    /// extension method then the dependent resource will wait until the DocumentDB server responds to ping.
    /// This version of the package defaults to the <inheritdoc cref="DocumentDBContainerImageTags.Tag"/> tag of the <inheritdoc cref="DocumentDBContainerImageTags.Image"/> container image.
    /// </remarks>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
    /// <param name="port">The host port for DocumentDB.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <example>
    /// <code>
    /// var server = builder.AddDocumentDB("documentdb", port: 10260);
    /// </code>
    /// </example>
    public static IResourceBuilder<DocumentDBServerResource> AddDocumentDB(this IDistributedApplicationBuilder builder, [ResourceName] string name, int? port)
        => AddDocumentDB(builder, name, port, null, null);

    /// <summary>
    /// <inheritdoc cref="AddDocumentDB(IDistributedApplicationBuilder, string, int?)"/>
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
    /// <param name="port">The host port for DocumentDB.</param>
    /// <param name="userName">A parameter that contains the DocumentDB server user name, or <see langword="null"/> to use a default value.</param>
    /// <param name="password">A parameter that contains the DocumentDB server password, or <see langword="null"/> to use a generated password.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <example>
    /// <code>
    /// // Minimal usage with generated credentials:
    /// var server = builder.AddDocumentDB("documentdb");
    /// var database = server.AddDatabase("mydb");
    ///
    /// // With custom credentials:
    /// var user = builder.AddParameter("db-user");
    /// var pass = builder.AddParameter("db-pass", secret: true);
    /// var securedServer = builder.AddDocumentDB("documentdb", userName: user, password: pass);
    /// </code>
    /// </example>
    public static IResourceBuilder<DocumentDBServerResource> AddDocumentDB(this IDistributedApplicationBuilder builder,
        string name,
        int? port = null,
        IResourceBuilder<ParameterResource>? userName = null,
        IResourceBuilder<ParameterResource>? password = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var passwordParameter = password?.Resource ?? ParameterResourceBuilderExtensions.CreateDefaultPasswordParameter(builder, $"{name}-password", special: false);

        var DocumentDBContainer = new DocumentDBServerResource(name, userName?.Resource, passwordParameter);

        string? connectionString = null;

        builder.Eventing.Subscribe<ConnectionStringAvailableEvent>(DocumentDBContainer, async (@event, ct) =>
        {
            connectionString = await DocumentDBContainer.ConnectionStringExpression.GetValueAsync(ct).ConfigureAwait(false)
                ?? throw new DistributedApplicationException($"ConnectionStringAvailableEvent was published for the '{DocumentDBContainer.Name}' resource but the connection string was null.");
        });

        var healthCheckKey = $"{name}_check";
        // Use a database-scoped check so the MongoDB health check package executes a ping command.
        IMongoDatabase? database = null;
        builder.Services.AddHealthChecks()
            .AddMongoDb(
                _ => database ??=
                    new MongoClient(connectionString ?? throw new InvalidOperationException("Connection string is unavailable"))
                        .GetDatabase(DefaultHealthCheckDatabaseName),
                name: healthCheckKey);

        return builder
            .AddResource(DocumentDBContainer)
            .WithEndpoint(port: port, targetPort: DefaultContainerPort, name: DocumentDBServerResource.PrimaryEndpointName)
            .WithImage(DocumentDBContainerImageTags.Image, DocumentDBContainerImageTags.Tag)
            .WithImageRegistry(DocumentDBContainerImageTags.Registry)
            .WithEnvironment(context =>
            {
                context.EnvironmentVariables[UserEnvVarName] = DocumentDBContainer.UserNameReference;
                context.EnvironmentVariables[PasswordEnvVarName] = DocumentDBContainer.PasswordParameter!;
            })
            .WithHealthCheck(healthCheckKey)
            .SubscribeMinimumPgVariantImageGuard()
            .SubscribeDataStorageGuard();
    }

    /// <summary>
    /// Subscribes a <see cref="BeforeResourceStartedEvent"/> handler that throws
    /// <see cref="InvalidOperationException"/> when the resource's effective image tag names a
    /// PostgreSQL backend variant upstream does not publish for that DocumentDB version — see
    /// <see cref="DocumentDBContainerImageTags.MinimumVersionByPgVariant"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Subscribed from <c>AddDocumentDB</c> rather than from <see cref="WithPostgresVersion"/>,
    /// because neither half of the tag is a problem on its own and the documented precedence is
    /// "last call wins": selecting <c>Pg18</c> before <c>V0_114_0</c> is perfectly legitimate,
    /// so only the effective tag at start time can be judged.
    /// </para>
    /// <para>
    /// Same carve-outs as <see cref="SubscribeMinimumPostgresImageGuard"/>: custom images, tags
    /// outside the strict <c>pg{NN}-X.Y.Z</c> grammar and caller-owned Dockerfile builds are
    /// exempt. Unlike that guard this one is always subscribed, so the exempt paths stay silent
    /// rather than warning on every app that pins a custom image.
    /// </para>
    /// <para>
    /// The subscription reports the failure while the resource is being started, which is where a
    /// caller sees it; it is not what makes the floor hold. A subscriber registered after this
    /// package runs afterwards and can replace the image it cleared, so the same rule — stated
    /// once in <see cref="DescribeUnpublishedPostgresVariant"/> — is applied again at the
    /// checkpoints Aspire never caches. See <see cref="DescribeIncompatibleImage"/>.
    /// </para>
    /// </remarks>
    private static IResourceBuilder<DocumentDBServerResource> SubscribeMinimumPgVariantImageGuard(
        this IResourceBuilder<DocumentDBServerResource> builder)
    {
        builder.ApplicationBuilder.Eventing.Subscribe<BeforeResourceStartedEvent>(
            builder.Resource,
            (evt, ct) =>
            {
                if (DescribeUnpublishedPostgresVariant(evt.Resource, ResolveEffectiveImage(evt.Resource)) is { } incompatible)
                {
                    throw incompatible;
                }

                return Task.CompletedTask;
            });

        return builder;
    }

    /// <summary>
    /// The unpublished-variant floor, stated once and evaluated against one effective image.
    /// </summary>
    /// <remarks>
    /// Only a curated image is pulled by tag from the upstream registry. A fork publishing its own
    /// images decides its own variant matrix, a resource built from the caller's own Dockerfile
    /// never resolves that tag at all, and a digest pin resolves something the tag does not name —
    /// so the floor applies to exactly the origin that carries a known version.
    /// </remarks>
    /// <returns>
    /// The failure to raise, or <see langword="null"/> when the image satisfies the floor or its
    /// version is not known.
    /// </returns>
    private static InvalidOperationException? DescribeUnpublishedPostgresVariant(
        IResource resource,
        DocumentDBEffectiveImage image)
    {
        if (image is not { Origin: DocumentDBImageOrigin.Curated, KnownVersion: { } docVersion })
        {
            return null;
        }

        if (!DocumentDBContainerImageTags.MinimumVersionByPgVariant.TryGetValue(image.PostgresVariant, out var minimum) ||
            docVersion >= minimum)
        {
            return null;
        }

        return new InvalidOperationException(
            $"DocumentDB resource '{resource.Name}' resolves to image tag " +
            $"'{image.Tag}', but upstream only publishes pg{image.PostgresVariant} images " +
            $"from DocumentDB v{minimum} onwards. That tag does not exist on " +
            $"{DocumentDBContainerImageTags.Registry}/{DocumentDBContainerImageTags.Image}, " +
            $"so starting the resource would fail with an opaque manifest-not-found error. " +
            $"Recovery: pair " +
            $"'.WithPostgresVersion(DocumentDBPostgresVersion.Pg{image.PostgresVariant})' " +
            $"with DocumentDB v{minimum} or newer, or choose a PostgreSQL variant that " +
            $"exists for v{docVersion}.");
    }

    /// <summary>
    /// The <see cref="WithPostgresEndpoint"/> credential floor, stated once and evaluated against
    /// one effective image.
    /// </summary>
    /// <remarks>
    /// Whether the floor applies is read from the model rather than remembered at
    /// <see cref="WithPostgresEndpoint"/> time, so a checkpoint that judges the final image judges
    /// the final endpoint set with it. The carve-outs are the same ones
    /// <see cref="DescribeUnpublishedPostgresVariant"/> gets from the origin; the early guard warns
    /// about them, and this states only the failure.
    /// </remarks>
    private static InvalidOperationException? DescribePostgresEndpointFloor(
        IResource resource,
        DocumentDBEffectiveImage image)
    {
        if (image is not { Origin: DocumentDBImageOrigin.Curated, KnownVersion: { } docVersion } ||
            docVersion >= DocumentDBContainerImageTags.MinimumPostgresEndpointVersion ||
            !PublishesPostgresEndpoint(resource))
        {
            return null;
        }

        return new InvalidOperationException(
            $"DocumentDB resource '{resource.Name}' is configured with image tag " +
            $"'{image.Tag}', but WithPostgresEndpoint() requires DocumentDB " +
            $"v{DocumentDBContainerImageTags.MinimumPostgresEndpointVersion} or later. " +
            $"Earlier images hard-code the PostgreSQL admin credentials to " +
            $"'docdb_admin'/'Admin100', so the Aspire-generated postgresql:// connection " +
            $"string would silently fail to authenticate. Recovery: chain " +
            $"'.WithImageTag(\"pg{{NN}}-{DocumentDBContainerImageTags.MinimumPostgresEndpointVersion}\")' " +
            $"(or newer) after AddDocumentDB(...). See " +
            $"https://github.com/microsoft/azure-databases-aspire/issues/71.");
    }

    /// <summary>
    /// Every hard image-compatibility floor this package enforces, evaluated together against the
    /// image the resource will actually run as the model now stands.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The floors are also subscribed as <see cref="BeforeResourceStartedEvent"/> handlers, which
    /// is what reports them while the resource is still being started rather than at the moment
    /// the container is created. That is not on its own sufficient: a subscriber registered after
    /// this package runs after those handlers, so an image they cleared can still be replaced
    /// before anything reads it. This is therefore called from the checkpoints Aspire never
    /// caches, where the image being judged is the last word on the subject.
    /// </para>
    /// <para>
    /// The variant floor is judged first, matching the order the two subscriptions are registered
    /// in, so a tag that breaks both rules reports the same failure it always has.
    /// </para>
    /// </remarks>
    private static InvalidOperationException? DescribeIncompatibleImage(IResource resource)
    {
        var image = ResolveEffectiveImage(resource);

        return DescribeUnpublishedPostgresVariant(resource, image)
            ?? DescribePostgresEndpointFloor(resource, image);
    }

    /// <summary>
    /// Whether the resource publishes the PostgreSQL coordinator endpoint, which is what makes the
    /// credential floor apply to it.
    /// </summary>
    private static bool PublishesPostgresEndpoint(IResource resource) =>
        resource.Annotations.OfType<EndpointAnnotation>()
            .Any(endpoint => endpoint.Name == DocumentDBServerResource.PostgresEndpointName);

    /// <summary>
    /// How much this package can know about the container image a DocumentDB resource will
    /// actually run.
    /// </summary>
    private enum DocumentDBImageOrigin
    {
        /// <summary>The resource carries no <see cref="ContainerImageAnnotation"/> at all.</summary>
        None,

        /// <summary>
        /// The image is the output of a container build the caller owns, so nothing this package
        /// documents about a published DocumentDB release has been established for it.
        /// </summary>
        DockerfileBuild,

        /// <summary>A repository other than the curated <c>documentdb-local</c> one.</summary>
        CustomRepository,

        /// <summary>
        /// The curated repository, with a tag outside the strict <c>pg{NN}-X.Y.Z</c> grammar.
        /// </summary>
        UnrecognizedTag,

        /// <summary>
        /// The curated repository, pinned by digest. The digest is what the runtime resolves, so
        /// the version is unknown whatever tag the reference also carries.
        /// </summary>
        DigestPinned,

        /// <summary>
        /// The curated repository with a tag this build recognises — the only origin that carries
        /// a DocumentDB version.
        /// </summary>
        Curated,
    }

    /// <summary>
    /// What this package knows about the container image a DocumentDB resource will run.
    /// </summary>
    /// <remarks>
    /// <see cref="KnownVersion"/> and <see cref="PostgresVariant"/> are populated only when
    /// <see cref="Origin"/> is <see cref="DocumentDBImageOrigin.Curated"/>, which in particular
    /// means never when a <see cref="Digest"/> is present. Every version-dependent decision in
    /// this package is therefore gated on <c>KnownVersion is { } version</c>: one place decides
    /// what is known, and no caller can conclude a version from an image whose version is not
    /// known.
    /// </remarks>
    /// <param name="Origin">How much is known about the image.</param>
    /// <param name="Image">
    /// The repository as the annotation spells it, for diagnostics. It is not the identity: see
    /// <see cref="DocumentDBContainerImageTags.NamesCuratedRepository"/>.
    /// </param>
    /// <param name="Tag">The effective tag, whether the annotation or the reference carried it.</param>
    /// <param name="Digest">
    /// The effective digest, whether the annotation or the reference carried it, without its
    /// algorithm prefix.
    /// </param>
    /// <param name="PostgresVariant">The PostgreSQL major version of a curated tag.</param>
    /// <param name="KnownVersion">The DocumentDB version of a curated tag.</param>
    private readonly record struct DocumentDBEffectiveImage(
        DocumentDBImageOrigin Origin,
        string? Image,
        string? Tag,
        string? Digest,
        int PostgresVariant,
        Version? KnownVersion);

    /// <summary>
    /// Resolves what this package knows about the container image <paramref name="resource"/>
    /// will actually run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A container build the caller owns is decided first and unconditionally, because for such a
    /// resource the image annotation does not describe what runs. Aspire keeps the
    /// <see cref="ContainerImageAnnotation"/> that <c>AddDocumentDB</c> installs when
    /// <c>WithDockerfile(...)</c> is chained onto the resource, and a caller may also point that
    /// annotation at the official repository and tag afterwards, so a Dockerfile-built resource
    /// can be annotated indistinguishably from an official release while running an image built
    /// from an arbitrary <c>Dockerfile</c> — one that merely inherits the official image as its
    /// base, or does not use it at all. What runs is the build output: the manifest emits
    /// <c>build</c> instead of <c>image</c>, and in run mode the orchestrator builds the context
    /// before starting the container. None of the release properties this package acts on — the
    /// <c>/data</c> volume declaration, the data-directory <c>flock</c>, the PostgreSQL credential
    /// pass-through, the gateway configuration layout and entrypoint — has been proven for such an
    /// image, so it is classified as unknown rather than granted them on the strength of a label.
    /// </para>
    /// <para>
    /// <see cref="DockerfileBuildAnnotation"/> is the single authoritative signal, and one check
    /// covers every entry point: <c>WithDockerfile</c>, <c>WithDockerfileFactory</c> and
    /// <c>WithDockerfileBuilder</c> all add it — the last adds a
    /// <c>DockerfileBuilderCallbackAnnotation</c> beside it, not instead of it.
    /// <c>DockerfileBaseImageAnnotation</c> on its own is not a build: it selects base images for
    /// a <em>generated</em> Dockerfile, and with no build to generate one it changes neither the
    /// image a container resource pulls nor the manifest it publishes.
    /// </para>
    /// <para>
    /// Everything else is decided on the reference Aspire composes rather than on
    /// <see cref="ContainerImageAnnotation.Image"/> alone, because the boundary between registry
    /// and repository is the caller's to move: see
    /// <see cref="DocumentDBContainerImageTags.NamesCuratedRepository"/>. A tag or digest the
    /// annotation supplies wins over one written into the reference, and two tags in one reference
    /// are contradictory — Aspire emits <c>repo:a:b</c>, which resolves to nothing — so neither is
    /// trusted.
    /// </para>
    /// <para>
    /// A digest beats every tag. A reference may carry both — <c>repo:pg17-0.116.0@sha256:...</c>,
    /// or an inline tag beside an annotation <c>SHA256</c>, or the reverse — and the runtime
    /// resolves the digest and ignores the tag, so the tag names whichever release the caller
    /// last typed rather than the image that starts. Reading a version out of it would let an
    /// image predating the <c>/data</c> volume, the data-directory <c>flock</c> or a credential
    /// floor inherit the promises of a release it is not. So any digest at all classifies the
    /// resource as <see cref="DocumentDBImageOrigin.DigestPinned"/>, which carries no version;
    /// the repository is still known, which is what lets the telemetry API reject the pin with a
    /// message instead of silently skipping it.
    /// </para>
    /// </remarks>
    private static DocumentDBEffectiveImage ResolveEffectiveImage(IResource resource)
    {
        var image = resource.Annotations.OfType<ContainerImageAnnotation>().LastOrDefault();

        if (resource.Annotations.OfType<DockerfileBuildAnnotation>().Any())
        {
            return new(DocumentDBImageOrigin.DockerfileBuild, image?.Image, image?.Tag, image?.SHA256, 0, null);
        }

        if (image is null)
        {
            return new(DocumentDBImageOrigin.None, null, null, null, 0, null);
        }

        var curated = DocumentDBContainerImageTags.NamesCuratedRepository(
            image.Registry, image.Image, out var inlineTag, out var inlineDigest);

        var tag = string.IsNullOrEmpty(image.Tag) ? inlineTag : image.Tag;
        var digest = string.IsNullOrEmpty(image.SHA256) ? inlineDigest : image.SHA256;

        // Two tags in one reference contradict each other. A digest given twice needs no such
        // rule: any digest at all already forces the version unknown.
        var ambiguousTag = !string.IsNullOrEmpty(image.Tag) && inlineTag is not null;

        if (!curated)
        {
            return new(DocumentDBImageOrigin.CustomRepository, image.Image, tag, digest, 0, null);
        }

        if (!string.IsNullOrEmpty(digest))
        {
            return new(DocumentDBImageOrigin.DigestPinned, image.Image, tag, digest, 0, null);
        }

        if (ambiguousTag ||
            !DocumentDBContainerImageTags.TryParseDocumentDBTag(tag, out var pg, out var version))
        {
            return new(DocumentDBImageOrigin.UnrecognizedTag, image.Image, tag, digest, 0, null);
        }

        return new(DocumentDBImageOrigin.Curated, image.Image, tag, digest, pg, version);
    }

    /// <summary>
    /// Adds a DocumentDB database to the application model.
    /// </summary>
    /// <remarks>
    /// The database resource inherits the parent server's connection string and appends the database name.
    /// Services should reference the database resource (not the server) via <c>.WithReference(db)</c>.
    /// This resource includes a built-in health check. When this resource is referenced as a dependency
    /// using the <see cref="ResourceBuilderExtensions.WaitFor{T}(IResourceBuilder{T}, IResourceBuilder{IResource})"/>
    /// extension method then the dependent resource will wait until the DocumentDB database responds to ping.
    /// </remarks>
    /// <param name="builder">The DocumentDB server resource builder.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
    /// <param name="databaseName">The name of the database. If not provided, this defaults to the same value as <paramref name="name"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <example>
    /// <code>
    /// var server = builder.AddDocumentDB("documentdb");
    /// var ordersDb = server.AddDatabase("orders");
    /// var usersDb = server.AddDatabase("users");
    /// </code>
    /// </example>
    public static IResourceBuilder<DocumentDBDatabaseResource> AddDatabase(this IResourceBuilder<DocumentDBServerResource> builder, [ResourceName] string name, string? databaseName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        // Use the resource name as the database name if it's not provided
        databaseName ??= name;

        var DocumentDBDatabase = new DocumentDBDatabaseResource(name, databaseName, builder.Resource);
        builder.Resource.AddDatabase(DocumentDBDatabase);

        string? connectionString = null;

        builder.ApplicationBuilder.Eventing.Subscribe<ConnectionStringAvailableEvent>(DocumentDBDatabase, async (@event, ct) =>
        {
            connectionString = await DocumentDBDatabase.ConnectionStringExpression.GetValueAsync(ct).ConfigureAwait(false)
                ?? throw new DistributedApplicationException($"ConnectionStringAvailableEvent was published for the '{DocumentDBDatabase.Name}' resource but the connection string was null.");
        });

        var healthCheckKey = $"{name}_check";
        // cache the database instance so it is reused on subsequent calls to the health check
        IMongoDatabase? database = null;
        builder.ApplicationBuilder.Services.AddHealthChecks()
            .AddMongoDb(
                _ => database ??=
                    new MongoClient(connectionString ?? throw new InvalidOperationException("Connection string is unavailable"))
                        .GetDatabase(databaseName),
                name: healthCheckKey);

        return builder.ApplicationBuilder
            .AddResource(DocumentDBDatabase)
            .WithHealthCheck(healthCheckKey);
    }

    /// <summary>
    /// Configures the host port that the DocumentDB resource is exposed on instead of using randomly assigned port.
    /// </summary>
    /// <param name="builder">The resource builder for DocumentDB.</param>
    /// <param name="port">The port to bind on the host. If <see langword="null"/> is used random port will be assigned.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <example>
    /// <code>
    /// var server = builder.AddDocumentDB("documentdb")
    ///                     .WithHostPort(10260);
    /// </code>
    /// </example>
    public static IResourceBuilder<DocumentDBServerResource> WithHostPort(this IResourceBuilder<DocumentDBServerResource> builder, int? port)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithEndpoint(DocumentDBServerResource.PrimaryEndpointName, endpoint =>
        {
            endpoint.Port = port;
        });
    }

    /// <summary>
    /// Exposes the PostgreSQL backend coordinator port of the DocumentDB Local container
    /// (default container port <c>9712</c>) as a second endpoint on the resource.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>documentdb-local</c> container bundles a MongoDB-compatible gateway and a
    /// PostgreSQL coordinator listening on separate ports. By default this integration only
    /// publishes the gateway port (<c>10260</c>) and surfaces a <c>mongodb://</c> connection
    /// string. Calling <see cref="WithPostgresEndpoint"/> additionally publishes the
    /// PostgreSQL port so consumers can use psql/Npgsql/etc. directly, and enables
    /// <see cref="DocumentDBServerResource.PostgresConnectionStringExpression"/>.
    /// </para>
    /// <para>
    /// The endpoint uses the same <c>userName</c>/<c>password</c> parameters as the gateway
    /// because the container provisions a single admin user shared by both surfaces.
    /// The default database in the resulting URI is <c>postgres</c>, matching the upstream
    /// entrypoint, which connects with <c>-d postgres</c>.
    /// </para>
    /// </remarks>
    /// <param name="builder">The resource builder for DocumentDB.</param>
    /// <param name="port">
    /// The host port to bind to. If <see langword="null"/> a random port is assigned.
    /// </param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <example>
    /// <code>
    /// var documentDB = builder.AddDocumentDB("documentdb")
    ///                         .WithPostgresEndpoint();
    ///
    /// builder.AddProject&lt;Projects.Worker&gt;("worker")
    ///        .WithEnvironment("ConnectionStrings__pg", documentDB.Resource.PostgresConnectionStringExpression);
    /// </code>
    /// </example>
    public static IResourceBuilder<DocumentDBServerResource> WithPostgresEndpoint(
        this IResourceBuilder<DocumentDBServerResource> builder,
        int? port = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.Resource.Annotations.OfType<EndpointAnnotation>()
                .Any(e => e.Name == DocumentDBServerResource.PostgresEndpointName))
        {
            throw new InvalidOperationException(
                $"A PostgreSQL endpoint has already been added to resource '{builder.Resource.Name}'. " +
                $"Call '{nameof(WithPostgresEndpoint)}()' at most once per DocumentDB resource.");
        }

        return builder
            .WithEndpoint(
                port: port,
                targetPort: DefaultPostgresContainerPort,
                scheme: "postgresql",
                name: DocumentDBServerResource.PostgresEndpointName)
            .WithEnvironment(context =>
            {
                // Explicitly opt the upstream entrypoint into accepting external PostgreSQL
                // connections (sets PGOPTIONS=-e -> listen_addresses='*' + permissive pg_hba.conf).
                // Setting this is required so publishing the host port produces a reachable
                // server even on upstream container builds where the entrypoint's default
                // ALLOW_EXTERNAL_CONNECTIONS handling is corrected.
                context.EnvironmentVariables[AllowExternalConnectionsEnvVarName] = "true";
            })
            .SubscribeMinimumPostgresImageGuard();
    }

    /// <summary>
    /// Subscribes a <see cref="BeforeResourceStartedEvent"/> handler that throws
    /// <see cref="InvalidOperationException"/> if the resource's effective container image
    /// tag is older than <see cref="DocumentDBContainerImageTags.MinimumPostgresEndpointVersion"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The handler is registered AFTER endpoint and environment configuration have run,
    /// but executes at run-time via the orchestrator, which honours the documented
    /// "last call wins" precedence: a <c>WithImageTag(...)</c> chained after
    /// <see cref="WithPostgresEndpoint"/> still affects the tag the guard sees.
    /// </para>
    /// <para>
    /// The subscription is what warns about the exempt cases and what reports the failure while
    /// the resource is being started. It is not what makes the floor hold: a subscriber registered
    /// after this package runs afterwards, so the rule — stated once in
    /// <see cref="DescribePostgresEndpointFloor"/> — is applied again at the checkpoints Aspire
    /// never caches, in a run and while a publish serializes the resource alike. See
    /// <see cref="DescribeIncompatibleImage"/>.
    /// </para>
    /// <para>
    /// Custom images (anything whose <see cref="ContainerImageAnnotation.Image"/> is not
    /// the curated <see cref="DocumentDBContainerImageTags.Image"/>) are exempt with a
    /// single warning. Tags that do not match the strict <c>pg{NN}-X.Y.Z</c> pattern
    /// (e.g., <c>nightly</c>, <c>pg17-0.112.0-rc.1</c>) are also exempt with a single
    /// warning, so callers pinning custom builds or pre-releases are not surprised by an
    /// unactionable hard failure. A resource built from the caller's own Dockerfile is exempt
    /// on the same terms even when its image annotation names the curated image and a
    /// recognised tag, because the tag describes the build's starting point at best and the
    /// floor is a property of the published release. So is a digest-pinned reference, whose tag
    /// the runtime discards in favour of the digest.
    /// </para>
    /// </remarks>
    private static IResourceBuilder<DocumentDBServerResource> SubscribeMinimumPostgresImageGuard(
        this IResourceBuilder<DocumentDBServerResource> builder)
    {
        // Captured per-resource one-shot flag so unknown-tag / custom-image warnings
        // don't spam on every restart attempt. Hard-failure exceptions are deterministic
        // and intentionally re-thrown on each start attempt. Interlocked guard makes
        // the at-most-once property memory-safe even if a future Aspire orchestrator
        // dispatches BeforeResourceStartedEvent concurrently for the same resource.
        var warningLogged = 0;

        builder.ApplicationBuilder.Eventing.Subscribe<BeforeResourceStartedEvent>(
            builder.Resource,
            (evt, ct) =>
            {
                var image = ResolveEffectiveImage(evt.Resource);
                if (image.Origin == DocumentDBImageOrigin.None)
                {
                    // Defensive: AddDocumentDB sets ContainerImageAnnotation eagerly via WithImage.
                    return Task.CompletedTask;
                }

                var logger = TryGetResourceLogger(evt, PostgresEndpointLoggerCategory);

                // Caller-built carve-out, judged before the repository because a Dockerfile build
                // may carry the curated repository and tag verbatim: what starts is the output of
                // that build, so the tag says nothing about whether the credential fix is in it.
                if (image.Origin == DocumentDBImageOrigin.DockerfileBuild)
                {
                    if (Interlocked.CompareExchange(ref warningLogged, 1, 0) == 0)
                    {
                        logger?.LogWarning(
                            "DocumentDB resource '{ResourceName}' builds its container image from a Dockerfile, " +
                            "so what it runs is not a published DocumentDB release however its image annotation " +
                            "'{Image}:{Tag}' reads. The v{MinVersion} minimum required by WithPostgresEndpoint() " +
                            "for credential parity is NOT enforced on Dockerfile builds.",
                            evt.Resource.Name,
                            image.Image,
                            image.Tag,
                            DocumentDBContainerImageTags.MinimumPostgresEndpointVersion);
                    }
                    return Task.CompletedTask;
                }

                // Custom-image carve-out: only enforce the floor on the curated
                // documentdb-local image. A fork using a different image name
                // (regardless of registry) is assumed to know what it is doing.
                if (image.Origin == DocumentDBImageOrigin.CustomRepository)
                {
                    if (Interlocked.CompareExchange(ref warningLogged, 1, 0) == 0)
                    {
                        logger?.LogWarning(
                            "DocumentDB resource '{ResourceName}' uses custom image '{Image}:{Tag}'. " +
                            "The v{MinVersion} minimum required by WithPostgresEndpoint() for credential parity " +
                            "is NOT enforced on custom images.",
                            evt.Resource.Name,
                            image.Image,
                            image.Tag,
                            DocumentDBContainerImageTags.MinimumPostgresEndpointVersion);
                    }
                    return Task.CompletedTask;
                }

                // A digest supersedes the tag at the runtime, so the tag says nothing about the
                // image that starts and the floor has nothing to check it against.
                if (image.Origin == DocumentDBImageOrigin.DigestPinned)
                {
                    if (Interlocked.CompareExchange(ref warningLogged, 1, 0) == 0)
                    {
                        logger?.LogWarning(
                            "DocumentDB resource '{ResourceName}' pins its image by digest '{Digest}', so the " +
                            "DocumentDB version it runs is not the one tag '{Tag}' names. The v{MinVersion} " +
                            "minimum required by WithPostgresEndpoint() for credential parity is NOT enforced " +
                            "on digest-pinned images.",
                            evt.Resource.Name,
                            image.Digest,
                            image.Tag ?? "<none>",
                            DocumentDBContainerImageTags.MinimumPostgresEndpointVersion);
                    }
                    return Task.CompletedTask;
                }

                if (image.KnownVersion is null)
                {
                    if (Interlocked.CompareExchange(ref warningLogged, 1, 0) == 0)
                    {
                        logger?.LogWarning(
                            "DocumentDB resource '{ResourceName}' uses image tag '{Tag}', which does not match " +
                            "the curated 'pg{{NN}}-X.Y.Z' pattern. The v{MinVersion} minimum required by " +
                            "WithPostgresEndpoint() for credential parity is NOT enforced on unrecognised tags.",
                            evt.Resource.Name,
                            image.Tag,
                            DocumentDBContainerImageTags.MinimumPostgresEndpointVersion);
                    }
                    return Task.CompletedTask;
                }

                if (DescribePostgresEndpointFloor(evt.Resource, image) is { } incompatible)
                {
                    throw incompatible;
                }

                return Task.CompletedTask;
            });

        return builder;
    }

    private static ILogger? TryGetResourceLogger(BeforeResourceStartedEvent evt, string fallbackCategoryName)
    {
        // Prefer per-resource logger so the message shows in the Aspire dashboard's
        // resource log pane. Fall back to a general host logger if the service is
        // not registered (shouldn't happen in 13.3.5, but defensive). The fallback
        // category is per-caller so a message routed through it is still attributable
        // to the feature that produced it.
        var resourceLoggerService = evt.Services.GetService<ResourceLoggerService>();
        if (resourceLoggerService is not null)
        {
            return resourceLoggerService.GetLogger(evt.Resource);
        }

        var loggerFactory = evt.Services.GetService<ILoggerFactory>();
        return loggerFactory?.CreateLogger(fallbackCategoryName);
    }

    /// <summary>
    /// Adds a named volume for the data folder to a DocumentDB container resource.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bare DocumentDB container defaults <c>DATA_PATH</c> to <c>/data</c>. Up to and
    /// including DocumentDB v0.114-0 an unmounted <c>/data</c> is an ordinary directory in the
    /// container's writable layer, discarded when the container is removed. From v0.116-0 the
    /// image declares that path as a container <c>VOLUME</c>, so a run that mounts nothing there
    /// instead gets a fresh anonymous volume whose lifetime the container runtime controls (and
    /// which container removal can strand). On those images, mounting at the default
    /// <paramref name="targetPath"/> is what suppresses the anonymous volume: neither Docker nor
    /// Aspire can un-declare an image volume, so a non-default <paramref name="targetPath"/>
    /// leaves an unused anonymous volume behind at <c>/data</c> and the resource logs a warning
    /// at start.
    /// </para>
    /// <para>
    /// This helper mounts the volume at <paramref name="targetPath"/> and sets <c>DATA_PATH</c>
    /// to the same value so DocumentDB writes to the mounted directory. From v0.116-0 the
    /// container claims the directory with an exclusive <c>flock</c>, so a persisted data
    /// directory may back only one running container at a time; a second container that mounts it
    /// exits immediately with an explicit refusal instead of corrupting the directory. Earlier
    /// images have no such interlock, so concurrent use has to be avoided by construction.
    /// </para>
    /// <para>
    /// The data directory must be writable. The entrypoint takes ownership of it (the container's
    /// <c>documentdb</c> runtime user) and PostgreSQL initialises and writes WAL there, so
    /// <paramref name="isReadOnly"/> is rejected rather than being allowed to fail a minute into
    /// startup behind a misleading "PostgreSQL failed to start within 60 seconds" banner.
    /// </para>
    /// </remarks>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the volume. Defaults to an auto-generated name based on the application and resource names.</param>
    /// <param name="isReadOnly">Unsupported. DocumentDB requires a writable data directory; passing <see langword="true"/> throws.</param>
    /// <param name="targetPath">The target path inside the container. Defaults to /data to match the container default (and the path the image declares as a volume) when this helper is used. Canonicalized the way the container runtime resolves a path, so repeated separators and <c>.</c>/<c>..</c> segments are collapsed before the mount is created.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="isReadOnly"/> is <see langword="true"/>, or <paramref name="targetPath"/> is not an absolute container path below the root — including one that only resolves to the root, such as <c>/data/..</c>, and one that reaches above it, such as <c>/../data</c>, which the container runtime silently clamps onto <c>/data</c>.</exception>
    /// <example>
    /// <code>
    /// var server = builder.AddDocumentDB("documentdb")
    ///                     .WithDataVolume();
    /// </code>
    /// </example>
    public static IResourceBuilder<DocumentDBServerResource> WithDataVolume(
        this IResourceBuilder<DocumentDBServerResource> builder,
        string? name = null,
        bool isReadOnly = false,
        string? targetPath = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Normalized first so a rejection message names the path the caller actually asked for,
        // and so an unusable path is reported as such rather than as a read-only complaint.
        targetPath = NormalizeDataTargetPath(targetPath, nameof(targetPath));

        if (isReadOnly)
        {
            throw new ArgumentException(ReadOnlyDataMountMessage(nameof(WithDataVolume), targetPath), nameof(isReadOnly));
        }

        return builder
            .WithVolume(name ?? VolumeNameGenerator.Generate(builder, "data"), targetPath, isReadOnly: false)
            .WithEnvironment(context =>
            {
                context.EnvironmentVariables[DataPathEnvVarName] = targetPath;
            });
    }

    /// <summary>
    /// Adds a bind mount for the data folder to a DocumentDB container resource.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Prefer <see cref="WithDataVolume"/> for most cases. Bind mounts are useful when you need
    /// direct access to the data files on the host filesystem, but the host directory has to be
    /// writable by the container's <c>documentdb</c> runtime user, which the entrypoint enforces
    /// by taking ownership of the directory's contents.
    /// </para>
    /// <para>
    /// The bare DocumentDB container defaults <c>DATA_PATH</c> to <c>/data</c>. This helper mounts
    /// the directory at <c>/data</c> — the container default, and the path DocumentDB v0.116-0 and
    /// later declare as an image volume, so no anonymous volume is created — and sets
    /// <c>DATA_PATH</c> to the same value so DocumentDB writes to the mounted directory. On those
    /// images the directory is also claimed with an exclusive <c>flock</c> and can back only one
    /// running container at a time.
    /// </para>
    /// <para>
    /// Point the mount at a directory that is empty or holds an existing DocumentDB cluster.
    /// A directory that contains anything else (a stray <c>.gitkeep</c> or <c>.DS_Store</c> is
    /// enough) is refused, not cleaned: the container logs <c>Directory /data exists but doesn't
    /// appear to contain a valid PostgreSQL data directory</c>, never starts PostgreSQL, and exits
    /// non-zero a minute later behind the "PostgreSQL failed to start within 60 seconds" banner.
    /// </para>
    /// <para>
    /// <paramref name="isReadOnly"/> is rejected: PostgreSQL cannot initialise or run against a
    /// read-only data directory, and the container would otherwise spend a minute failing with a
    /// misleading "PostgreSQL failed to start within 60 seconds" banner.
    /// </para>
    /// <para>
    /// A bind mount only carries a PostgreSQL data directory on a container runtime that applies
    /// ownership changes to the mounted host path immediately. PostgreSQL refuses to start unless
    /// the data directory is already owned by the user starting the postmaster, and the container
    /// entrypoint establishes that by running <c>chown</c> on <c>DATA_PATH</c> milliseconds before
    /// starting it. Docker Desktop applies that <c>chown</c> asynchronously — measured on macOS
    /// with VirtioFS, and expected on its Windows and Linux hosts, which share the same
    /// file-sharing design — so the postmaster reads the previous owner and aborts with
    /// <c>data directory "/data" has wrong ownership</c>. A first run hides this behind the seconds
    /// <c>initdb</c> spends between the two steps, so the container comes up once and then fails
    /// every restart, with the data intact on the host but unreadable. Nothing in the application
    /// model can order that runtime's <c>chown</c>; use <see cref="WithDataVolume"/> there, whose
    /// storage lives inside the runtime's own filesystem and is unaffected. A bind mount on a
    /// native container engine is an ordinary mount and restarts normally.
    /// </para>
    /// </remarks>
    /// <param name="builder">The resource builder.</param>
    /// <param name="source">The source directory on the host to mount into the container.</param>
    /// <param name="isReadOnly">Unsupported. DocumentDB requires a writable data directory; passing <see langword="true"/> throws.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="isReadOnly"/> is <see langword="true"/>.</exception>
    /// <example>
    /// <code>
    /// var server = builder.AddDocumentDB("documentdb")
    ///                     .WithDataBindMount("./data/documentdb");
    /// </code>
    /// </example>
    public static IResourceBuilder<DocumentDBServerResource> WithDataBindMount(this IResourceBuilder<DocumentDBServerResource> builder, string source, bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(source);

        const string targetPath = DefaultMountedDataPath;

        if (isReadOnly)
        {
            throw new ArgumentException(ReadOnlyDataMountMessage(nameof(WithDataBindMount), targetPath), nameof(isReadOnly));
        }

        return builder
            .WithBindMount(source, targetPath, isReadOnly: false)
            .WithEnvironment(context =>
            {
                context.EnvironmentVariables[DataPathEnvVarName] = targetPath;
            });
    }

    private static string ReadOnlyDataMountMessage(string methodName, string targetPath) =>
        $"{methodName}(isReadOnly: true) is not supported: DocumentDB requires a writable data " +
        $"directory. The container entrypoint takes ownership of '{targetPath}' and lets " +
        $"PostgreSQL initialise and write WAL there, so a read-only mount fails about a minute " +
        $"into startup behind the misleading banner 'PostgreSQL failed to start within 60 " +
        $"seconds' (the real cause, 'initdb: error: could not change permissions of directory " +
        $"\"{targetPath}\": Read-only file system', is buried in interleaved container logs). " +
        $"Recovery: mount the data directory writable, and mount read-only content elsewhere — " +
        $"for example WithInitData(...), which mounts seed scripts read-only at " +
        $"'{InitDataMountPath}'.";

    /// <summary>
    /// Validates and canonicalizes the container path a data mount targets. Container paths are
    /// always Linux-style absolute paths; a relative or empty path would be rejected by the
    /// container runtime with an opaque mount error, and the container root cannot be a mount
    /// target at all.
    /// </summary>
    private static string NormalizeDataTargetPath(string? targetPath, string parameterName)
    {
        if (targetPath is null)
        {
            return DefaultMountedDataPath;
        }

        if (string.IsNullOrWhiteSpace(targetPath))
        {
            throw new ArgumentException(
                "The DocumentDB data target path cannot be empty or whitespace. Omit the argument to " +
                $"use the container default '{DefaultMountedDataPath}'.",
                parameterName);
        }

        return ClassifyContainerPath(targetPath, out var canonical) switch
        {
            ContainerPathProblem.None => canonical,

            ContainerPathProblem.NotAbsolute => throw new ArgumentException(
                $"The DocumentDB data target path '{targetPath}' must be an absolute path inside the " +
                $"container (starting with '/'), because it is used both as the mount target and as the " +
                $"container's DATA_PATH. Omit the argument to use the container default " +
                $"'{DefaultMountedDataPath}'.",
                parameterName),

            ContainerPathProblem.EscapesRoot => throw new ArgumentException(
                $"The DocumentDB data target path '{targetPath}' reaches above the container root. " +
                $"There is nothing above '/', so the container runtime silently clamps the target to " +
                $"'{canonical}' — the mount would land somewhere other than the path written here. " +
                $"Write the resolved path instead, or omit the argument to use the container default " +
                $"'{DefaultMountedDataPath}'.",
                parameterName),

            _ => throw new ArgumentException(
                $"The DocumentDB data target path '{targetPath}' resolves to the container root '/', " +
                $"which cannot be a mount target. The container runtime collapses '.' and '..' segments " +
                $"before mounting, so an alias such as '/data/..' is the root itself. Omit the argument " +
                $"to use the container default '{DefaultMountedDataPath}'.",
                parameterName),
        };
    }

    /// <summary>
    /// Why an absolute container path could not be used as a mount target or data directory as
    /// written.
    /// </summary>
    private enum ContainerPathProblem
    {
        /// <summary>The path canonicalized to itself.</summary>
        None,

        /// <summary>The path is empty or does not start with '/'.</summary>
        NotAbsolute,

        /// <summary>
        /// The path's '..' segments reached above the container root and were clamped there. The
        /// canonical path is still produced, because that is what the runtime mounts.
        /// </summary>
        EscapesRoot,

        /// <summary>The path canonicalizes to the container root '/'.</summary>
        IsRoot,
    }

    /// <summary>
    /// Canonicalizes an absolute Linux container path the way the container runtime resolves one
    /// before mounting: repeated separators collapse, <c>.</c> segments drop out, <c>..</c>
    /// segments remove the preceding one, and a <c>..</c> at the root is clamped to the root.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every storage comparison runs on canonical paths, because Docker compares the resolved
    /// path and not the string the caller wrote: <c>/data</c>, <c>//data/</c>, <c>/foo/../data</c>
    /// and <c>/../data</c> are one and the same mount destination — verified against the daemon,
    /// which reports <c>Duplicate mount point: /data</c> for any two of them and inspects all of
    /// them back as <c>/data</c>. Comparing them as written would let an alias slip past the
    /// duplicate, read-only and shared-storage rules.
    /// </para>
    /// <para>
    /// Clamping is reported rather than hidden. A caller who writes <c>/../data</c> and a runtime
    /// that mounts <c>/data</c> disagree about where the storage lands, so the canonical path is
    /// produced (it is what really happens) <em>and</em> <see cref="ContainerPathProblem.EscapesRoot"/>
    /// is returned so the caller can refuse the spelling.
    /// </para>
    /// <para>
    /// A path that collapses to the root is reported separately: the daemon refuses it outright
    /// (<c>invalid mount config for type "volume": invalid specification: destination can't be
    /// '/'</c>), and the root cannot hold a PostgreSQL cluster.
    /// </para>
    /// <para>
    /// Only <c>/</c> separates segments. A backslash is an ordinary character in a Linux file
    /// name, so a Windows-style path is not absolute here and is reported as such.
    /// </para>
    /// </remarks>
    private static ContainerPathProblem ClassifyContainerPath(string? path, out string canonical)
    {
        canonical = string.Empty;

        if (string.IsNullOrEmpty(path) || path[0] != '/')
        {
            return ContainerPathProblem.NotAbsolute;
        }

        var segments = new List<string>();
        var escapedRoot = false;

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || string.Equals(segment, ".", StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(segment, "..", StringComparison.Ordinal))
            {
                if (segments.Count == 0)
                {
                    // The runtime clamps here rather than failing, so canonicalization continues.
                    escapedRoot = true;
                    continue;
                }

                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        if (segments.Count == 0)
        {
            return ContainerPathProblem.IsRoot;
        }

        canonical = "/" + string.Join('/', segments);
        return escapedRoot ? ContainerPathProblem.EscapesRoot : ContainerPathProblem.None;
    }

    /// <summary>
    /// The container path a mount really lands on, or <see langword="null"/> when the runtime
    /// would refuse the target outright (relative, or collapsing to the root). A target that only
    /// needed clamping resolves to the clamped path, because that is the destination the runtime
    /// creates.
    /// </summary>
    private static string? ResolveMountTarget(ContainerMountAnnotation mount) =>
        ClassifyContainerPath(mount.Target, out var canonical) is ContainerPathProblem.None or ContainerPathProblem.EscapesRoot
            ? canonical
            : null;

    /// <summary>
    /// Whether a mount lands on <paramref name="canonicalPath"/> once the container runtime has
    /// resolved the raw target.
    /// </summary>
    private static bool TargetsContainerPath(ContainerMountAnnotation mount, string canonicalPath) =>
        string.Equals(ResolveMountTarget(mount), canonicalPath, StringComparison.Ordinal);

    /// <summary>
    /// Whether <paramref name="canonicalDataPath"/> is inside — or is — the directory a mount on
    /// <paramref name="canonicalTarget"/> supplies. Both paths are canonical, and the comparison
    /// is made on segment boundaries so <c>/datastore</c> is not treated as living under
    /// <c>/data</c>.
    /// </summary>
    private static bool BacksContainerPath(string canonicalTarget, string canonicalDataPath) =>
        string.Equals(canonicalTarget, canonicalDataPath, StringComparison.Ordinal) ||
        (canonicalDataPath.Length > canonicalTarget.Length &&
         canonicalDataPath[canonicalTarget.Length] == '/' &&
         canonicalDataPath.StartsWith(canonicalTarget, StringComparison.Ordinal));

    // Host paths are compared case-sensitively only where the platform's filesystem is,
    // so a bind mount reused as "C:\Data" and "c:\data" is still recognised as shared.
    private static readonly StringComparison HostPathComparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>
    /// Canonicalizes a bind mount's host path with host semantics, so aliases of one directory
    /// compare equal: <c>/srv/documentdb</c>, <c>/srv/documentdb/</c>, <c>/srv/documentdb/.</c>
    /// and <c>/srv/documentdb/../documentdb</c> are the same host directory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Aspire fully qualifies a relative bind source against the AppHost directory but leaves a
    /// rooted one exactly as written, so <c>.</c> and <c>..</c> segments survive into the model
    /// and have to be resolved here. <see cref="Path.GetFullPath(string)"/> is the host's own
    /// resolution and needs no filesystem access.
    /// </para>
    /// <para>
    /// Symbolic links are deliberately <em>not</em> resolved. Doing so requires the directory to
    /// exist at model-build time, and would make the answer depend on the state of the host
    /// filesystem at a moment that has nothing to do with the run — two spellings that resolve to
    /// one directory only after a link is created would be judged differently before and after.
    /// Two DocumentDB resources aimed at one directory through different symlinks are therefore
    /// not detected; the <c>0.116.0</c> and later interlock still refuses the overlap at runtime.
    /// </para>
    /// </remarks>
    private static string CanonicalizeHostPath(string source)
    {
        try
        {
            var trimmed = source.TrimEnd('/', '\\');
            // Trimming a rooted path down to nothing ("/" or "C:\") leaves no path to resolve.
            return Path.GetFullPath(trimmed.Length == 0 ? source : trimmed);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            // An unresolvable spelling is compared as written rather than crashing the guard.
            return source;
        }
    }

    /// <summary>
    /// The host directory a bind-mounted data directory really occupies: the mount source with the
    /// part of <c>DATA_PATH</c> that falls below the mount target appended, canonicalized with host
    /// semantics.
    /// </summary>
    /// <remarks>
    /// A bind mount is a window onto the host filesystem, so where the cluster lands is a single
    /// host path and not the (source, subdirectory) pair it was written as. Binding
    /// <c>/srv/documentdb</c> at <c>/data</c> with <c>DATA_PATH=/data/cluster</c> and binding
    /// <c>/srv/documentdb/cluster</c> at <c>/data</c> put their clusters in the same place, and
    /// comparing the pairs rather than the path would miss it.
    /// </remarks>
    private static string CanonicalizeHostDataDirectory(string source, string containerSubpath)
    {
        if (containerSubpath.Length == 0)
        {
            return CanonicalizeHostPath(source);
        }

        // The subpath is a container path; its separators become the host's on the way through.
        var hostSubpath = containerSubpath.Replace('/', Path.DirectorySeparatorChar);

        try
        {
            return CanonicalizeHostPath(Path.Combine(CanonicalizeHostPath(source), hostSubpath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            // Same fallback as CanonicalizeHostPath: compare what was written rather than crash.
            return source + Path.DirectorySeparatorChar + hostSubpath;
        }
    }

    /// <summary>
    /// Installs the data-storage guard: a final environment callback and a final command-line
    /// callback that reject data-directory configurations the DocumentDB Local container cannot
    /// use, before the container is created and fails with a misleading message.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Covers these unsupported shapes, including mounts added with the raw Aspire APIs
    /// (<c>WithVolume</c> / <c>WithBindMount</c>) rather than <see cref="WithDataVolume"/> or
    /// <see cref="WithDataBindMount"/>:
    /// </para>
    /// <list type="bullet">
    /// <item><description>a mount target that reaches above the container root — the runtime
    /// silently clamps it, so the mount lands somewhere other than the path that was
    /// written;</description></item>
    /// <item><description>a read-only mount backing the data path — PostgreSQL cannot initialise
    /// or write there, and the container burns 60 seconds before reporting an unrelated-looking
    /// timeout;</description></item>
    /// <item><description>two mounts on the same backing target — the container runtime rejects
    /// duplicate mount targets;</description></item>
    /// <item><description>a data directory shared with another DocumentDB resource's data
    /// directory — two PostgreSQL instances on one data directory corrupt it;</description></item>
    /// <item><description>a <c>-d</c> / <c>--data-path</c> command-line argument, which is a
    /// second, unmodelled channel for the same setting.</description></item>
    /// </list>
    /// <para>
    /// The shared-directory rule is version-sensitive. From
    /// <see cref="DocumentDBContainerImageTags.MinimumDeclaredDataVolumeVersion"/> the entrypoint
    /// claims the directory with an exclusive <c>flock</c>, so the container that starts second
    /// refuses to start; when one of the two resources is started by hand
    /// (<see cref="ResourceBuilderExtensions.WithExplicitStart{T}"/>) the pair may never overlap
    /// and the combination is reported as a warning. Older, unrecognised, and custom images — and
    /// images built from the caller's own Dockerfile, whatever their annotations say — are not
    /// known to hold that lock: simultaneous access is not refused, it silently corrupts, so there
    /// the combination stays a hard failure regardless of explicit start.
    /// </para>
    /// <para>
    /// It also warns once when the data mount does not cover <c>/data</c> on an image known to
    /// declare that path as a container <c>VOLUME</c>: neither Docker nor Aspire can un-declare an
    /// image volume, so the only way to suppress the anonymous volume the runtime would otherwise
    /// create is to mount the caller's storage on that exact path. Images that declare no volume
    /// (at or below <c>0.114.0</c>), unrecognised tags, custom images and caller-owned Dockerfile
    /// builds produce no such warning, because for them an unmounted <c>/data</c> is just a
    /// directory in the container layer.
    /// </para>
    /// <para>
    /// The guard runs <em>inside</em> the resource's real configuration pipeline rather than
    /// beside it. Both callbacks observe the final <c>DATA_PATH</c> and the final argument list,
    /// including values produced by dynamic callbacks, whatever the call order. The environment
    /// callback replaces <c>DATA_PATH</c> with the single canonical string it validated — and sets
    /// it even when nothing else did — so the container is guaranteed to receive exactly the
    /// directory that was judged, whatever default its image would otherwise have applied. Aspire
    /// caches each callback's result for the lifetime of a run and re-evaluates it on restart, so a
    /// stateful callback is evaluated once and the guard and the container cannot disagree.
    /// </para>
    /// <para>
    /// Being last is established and then verified, in two different ways for the two pipelines.
    /// The environment callback is appended when <see cref="BeforeStartEvent"/> is published, which
    /// is before Aspire gathers anything but before
    /// <see cref="Lifecycle.IDistributedApplicationLifecycleHook.BeforeStartAsync"/> and before any
    /// later <see cref="BeforeStartEvent"/> subscriber, either of which may append more callbacks.
    /// It is therefore moved back to the end of its pipeline when
    /// <see cref="BeforeResourceStartedEvent"/> is published, the last phase Aspire offers before
    /// the container's environment is gathered. Anything that appends after <em>that</em> — a
    /// <see cref="BeforeResourceStartedEvent"/> subscriber registered after <c>AddDocumentDB</c>,
    /// or a lifecycle hook in publish mode, where no per-resource event is published — is caught by
    /// the callback itself, which checks that it is still the last of its kind and fails the
    /// resource rather than validating a configuration something else can still change.
    /// </para>
    /// <para>
    /// The command-line rule is a step of the resource's terminal command-line guard
    /// (<see cref="EnsureTerminalGuard"/>) rather than a callback of its own, and gets
    /// the same three-phase treatment and the same fail-closed check from it. One callback for the
    /// whole package is what makes that expressible: the OpenTelemetry gateway wrapper also has to
    /// be the last word on the command line, and two callbacks that each demanded the last position
    /// would be an impossible requirement rather than a check. The storage rule runs first among
    /// the steps, on the caller's own arguments, because it models the container entrypoint's
    /// <c>--option value</c> grammar and the wrapper turns the list into a <c>/bin/bash -c</c>
    /// command line; it is then re-checked on the finished list, past the wrapper prefix, so the
    /// last thing that runs has judged both rules.
    /// </para>
    /// </remarks>
    private static IResourceBuilder<DocumentDBServerResource> SubscribeDataStorageGuard(
        this IResourceBuilder<DocumentDBServerResource> builder)
    {
        var resource = builder.Resource;
        var coordinator = DataStorageCoordinator.For(builder.ApplicationBuilder);

        var commandLineGuard = EnsureTerminalGuard(builder);
        commandLineGuard.AddCommandLineStep(
            TerminalCommandLineDataStorageRank,
            state => RejectReservedDataPathArguments(resource, state.Args));
        commandLineGuard.AddCommandLineValidation(
            state => RejectReservedDataPathArguments(resource, CallerArguments(state)));

        builder.ApplicationBuilder.Eventing.Subscribe<BeforeStartEvent>((evt, _) =>
        {
            InstallDataStorageGuard(resource, coordinator, evt.Services);
            return Task.CompletedTask;
        });

        // Two later phases, because no single one covers every shape of resource. Endpoints are
        // allocated before any container object is created, on every path including the delayed
        // creation an explicitly started container with a persistent lifetime takes; the
        // per-resource start event is the last phase before an ordinary container's configuration
        // is built. Re-appending at either puts the guard after anything a lifecycle hook or a
        // later BeforeStartEvent subscriber added.
        builder.ApplicationBuilder.Eventing.Subscribe<ResourceEndpointsAllocatedEvent>(resource, (evt, _) =>
        {
            InstallDataStorageGuard(resource, coordinator, evt.Services);
            return Task.CompletedTask;
        });

        builder.ApplicationBuilder.Eventing.Subscribe<BeforeResourceStartedEvent>(resource, (evt, _) =>
        {
            InstallDataStorageGuard(resource, coordinator, evt.Services);
            return Task.CompletedTask;
        });

        return builder;
    }

    /// <summary>
    /// The arguments the container entrypoint itself receives: everything past the OpenTelemetry
    /// wrapper prefix when that wrapper is in place, and the whole list otherwise.
    /// </summary>
    /// <remarks>
    /// The prefix is identified by the exact script instance this evaluation inserted, so a list
    /// the wrapper does not actually own is scanned whole and left for
    /// <see cref="ValidateOpenTelemetryGatewayCommand"/> to report — no rule is skipped on the
    /// strength of a shape that is already wrong.
    /// </remarks>
    private static IEnumerable<object> CallerArguments(TerminalCommandLineState state) =>
        state.WrapperScript is { } script &&
        state.Args.Count >= 3 &&
        ReferenceEquals(state.Args[1], script)
            ? state.Args.Skip(3)
            : state.Args;

    /// <summary>
    /// Appends the guard's environment callback to <paramref name="resource"/>, or moves the one
    /// already installed back to the end of its pipeline.
    /// </summary>
    /// <remarks>
    /// Moving re-uses the same annotation instance, so Aspire's per-callback result cache is
    /// untouched and the guard is still evaluated exactly once per run. The command-line half is
    /// owned by the terminal command-line guard, which moves itself at the same phases.
    /// </remarks>
    private static void InstallDataStorageGuard(
        DocumentDBServerResource resource,
        DataStorageCoordinator coordinator,
        IServiceProvider? services)
    {
        if (resource.Annotations.OfType<DataStorageGuardAnnotation>().LastOrDefault() is { } installed)
        {
            installed.Services ??= services;
            MoveToEnd(resource, installed.Environment);
            return;
        }

        // One-shot so restart attempts don't repeat advisory warnings. Hard failures are
        // deterministic and intentionally re-thrown on every start attempt.
        var sharedStorageWarningLogged = 0;
        var declaredVolumeWarningLogged = 0;

        DataStorageGuardAnnotation? guard = null;
        EnvironmentCallbackAnnotation? environmentCallback = null;

        environmentCallback = new EnvironmentCallbackAnnotation(async context =>
        {
            EnsureGuardRunsLast(resource, environmentCallback!, "environment");

            RejectMountTargetsThatEscapeContainerRoot(resource);

            var dataPath = await CanonicalizeEffectiveDataPathAsync(resource, context).ConfigureAwait(false);
            if (dataPath is null)
            {
                // Publish mode with a deferred DATA_PATH: the manifest carries an expression, not
                // a path, and there is nothing here that can be compared with a mount target. The
                // seal is still recorded, because "this resource mounts nothing" is exactly the
                // observation a mount added afterwards would contradict.
                guard!.Seal = CaptureDataStorageSeal(resource, dataPath: null);
                return;
            }

            var (backingTarget, backingMounts) = SelectBackingMount(resource, dataPath);

            if (backingMounts.FirstOrDefault(mount => mount.IsReadOnly) is { } readOnlyMount)
            {
                throw new InvalidOperationException(ReadOnlyDataStorageMessage(resource, dataPath, backingTarget!, readOnlyMount));
            }

            if (backingMounts.Count > 1)
            {
                throw new InvalidOperationException(
                    $"DocumentDB resource '{resource.Name}' has {backingMounts.Count} mounts on " +
                    $"'{backingTarget}', the directory that backs its data directory " +
                    $"('{dataPath}'). The container runtime rejects duplicate mount targets — it " +
                    $"resolves '.', '..' and repeated separators first, so two different spellings " +
                    $"of one path collide — and the container would fail to be created. Recovery: " +
                    $"configure the data directory once — call either WithDataVolume(...) or " +
                    $"WithDataBindMount(...), not both, and do not add a second volume or bind " +
                    $"mount on the same path.");
            }

            var dataMount = backingMounts.Count == 1 ? backingMounts[0] : null;

            if (dataMount is not null && dataMount.Source is not null)
            {
                ClaimDataStorage(coordinator, resource, dataMount, backingTarget!, dataPath, context, ref sharedStorageWarningLogged);
            }

            if (!string.Equals(dataPath, DefaultMountedDataPath, StringComparison.Ordinal) &&
                ResolvesToDataVolumeAwareImage(resource) &&
                !resource.Annotations.OfType<ContainerMountAnnotation>().Any(mount => TargetsContainerPath(mount, DefaultMountedDataPath)) &&
                Interlocked.CompareExchange(ref declaredVolumeWarningLogged, 1, 0) == 0)
            {
                TryGetStorageLogger(resource, context)?.LogWarning(
                    "DocumentDB resource '{ResourceName}' stores data at '{DataPath}', but its " +
                    "image (v{Version} or later) declares '{DefaultDataPath}' as a container " +
                    "volume. Neither Docker nor Aspire can un-declare an image volume, so the " +
                    "runtime creates an unused anonymous volume on that declared path on every " +
                    "run, and container removal can strand it. Leave the data target path at its " +
                    "default to avoid this.",
                    resource.Name,
                    dataPath,
                    DocumentDBContainerImageTags.MinimumDeclaredDataVolumeVersion,
                    DefaultMountedDataPath);
            }

            guard!.Seal = CaptureDataStorageSeal(resource, dataPath);
        });

        guard = new DataStorageGuardAnnotation(environmentCallback) { Services = services };
        resource.Annotations.Add(guard);
        resource.Annotations.Add(environmentCallback);
    }

    /// <summary>
    /// Fails the resource when something appended an environment callback after the guard's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The guard's answer is only worth anything if nothing can change the configuration after it.
    /// It is appended at <see cref="BeforeStartEvent"/> and moved back to the end at
    /// <see cref="BeforeResourceStartedEvent"/>, which covers lifecycle hooks and later
    /// <see cref="BeforeStartEvent"/> subscribers; a subscriber that appends later still — or, in
    /// publish mode, any lifecycle hook, because no per-resource event is published there — would
    /// otherwise be able to move the data directory past every rule.
    /// </para>
    /// <para>
    /// The command-line pipeline has the same rule, enforced once for the whole package by
    /// <see cref="EnsureTerminalCallbackRunsLast{TAnnotation}"/>, because the storage rules and the
    /// OpenTelemetry gateway wrapper share one terminal callback there.
    /// </para>
    /// <para>
    /// That is reported rather than tolerated. No value is included in the message: the point is
    /// the shape of the configuration, and the callback that ran last may well be carrying a
    /// secret.
    /// </para>
    /// </remarks>
    private static void EnsureGuardRunsLast(
        DocumentDBServerResource resource,
        EnvironmentCallbackAnnotation guardCallback,
        string pipeline)
    {
        var last = resource.Annotations.OfType<EnvironmentCallbackAnnotation>().LastOrDefault();
        if (ReferenceEquals(last, guardCallback))
        {
            return;
        }

        throw new InvalidOperationException(
            $"DocumentDB resource '{resource.Name}' has a later {pipeline} callback registered after " +
            $"its data-storage guard, so the guard cannot be sure the configuration it checked is the " +
            $"one the container receives. The guard is appended when the application starts, and " +
            $"moved back to the end of its pipeline at the latest phase the run offers; a callback " +
            $"added after that usually comes from a subscriber registered after AddDocumentDB, or " +
            $"from an IDistributedApplicationLifecycleHook where no later phase is published. The " +
            $"resource is failed instead of being started on an unchecked data directory. " +
            $"Recovery: make that configuration part of the " +
            $"application model (WithDataVolume(), WithDataBindMount(...), " +
            $"WithEnvironment(\"{DataPathEnvVarName}\", ...), WithArgs(...)) rather than adding it " +
            $"after the model is built, or register the subscriber before AddDocumentDB.");
    }

    /// <summary>
    /// Carries the guard's own environment callback so a later phase can move it back to the end
    /// of its pipeline without re-creating it.
    /// </summary>
    private sealed class DataStorageGuardAnnotation(
        EnvironmentCallbackAnnotation environment) : IResourceAnnotation
    {
        public EnvironmentCallbackAnnotation Environment { get; } = environment;

        /// <summary>
        /// What the storage rules judged, or <see langword="null"/> before they have run.
        /// </summary>
        public DataStorageSeal? Seal { get; set; }

        /// <summary>
        /// The AppHost's services, captured from whichever event installed or re-established the
        /// guard, so its advisory warnings can reach a logger that is really wired up.
        /// </summary>
        public IServiceProvider? Services { get; set; }
    }

    /// <summary>
    /// Everything the data-storage rules read, as it stood when they produced the verdict Aspire
    /// recorded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The storage counterpart of <see cref="TerminalConfigurationSeal"/>, and it exists for the
    /// same reason: Aspire evaluates each callback annotation at most once per run and reuses the
    /// recorded result afterwards. Storage is the sharpest form of that problem, because a volume
    /// or bind mount is a plain annotation: adding one after the environment pipeline has been
    /// gathered — from an <see cref="Lifecycle.IDistributedApplicationLifecycleHook"/>, or from any
    /// subscriber that builds the configuration through the public
    /// <see cref="ExecutionConfigurationBuilder"/> first — changes what the container really
    /// mounts without running a single line of this package's code again. A read-only mount over
    /// the data directory added that way would start a container DocumentDB cannot initialise, and
    /// a shared volume added that way would put two clusters on one directory.
    /// </para>
    /// <para>
    /// It is a second recording rather than more fields on the command-line seal because the two
    /// are made at different moments — the storage rules answer in the environment pipeline, which
    /// a caller can gather on its own — but there is still one authority: both are compared by
    /// <see cref="VerifyTerminalConfigurationSeal"/>, from the same two uncached checkpoints.
    /// </para>
    /// <para>
    /// Everything the verdict depends on is recorded: the mounts themselves, by value rather than
    /// by instance so that re-declaring the same storage is not reported as a change and so that a
    /// bare reordering is not either; the membership of the two callback pipelines that can still
    /// set <c>DATA_PATH</c> or pass a reserved data-path argument, and of the
    /// container-runtime-argument pipeline, whose callbacks run between the run's checkpoint and
    /// the gather it protects; the explicit-start setting, which is what decides whether sharing a
    /// directory is a warning or a failure; and the image, because holding the data directory's
    /// lock is a property of the release.
    /// </para>
    /// </remarks>
    private sealed record DataStorageSeal(
        System.Collections.Immutable.ImmutableArray<string> Mounts,
        System.Collections.Immutable.ImmutableArray<EnvironmentCallbackAnnotation> EnvironmentCallbacks,
        System.Collections.Immutable.ImmutableArray<CommandLineArgsCallbackAnnotation> CommandLineCallbacks,
        System.Collections.Immutable.ImmutableArray<ContainerRuntimeArgsCallbackAnnotation> RuntimeCallbacks,
        bool ExplicitlyStarted,
        DocumentDBEffectiveImage Image,
        string? DataPath);

    private static DataStorageSeal CaptureDataStorageSeal(DocumentDBServerResource resource, string? dataPath) =>
        new(
            CaptureDataStorageMounts(resource),
            [.. resource.Annotations.OfType<EnvironmentCallbackAnnotation>()],
            [.. resource.Annotations.OfType<CommandLineArgsCallbackAnnotation>()],
            [.. resource.Annotations.OfType<ContainerRuntimeArgsCallbackAnnotation>()],
            resource.Annotations.OfType<ExplicitStartupAnnotation>().Any(),
            ResolveEffectiveImage(resource),
            dataPath);

    /// <summary>
    /// The resource's mounts as the storage rules see them: what each one is, where it comes from,
    /// where it lands and whether it is writable, in a fixed order.
    /// </summary>
    /// <remarks>
    /// By value, because a mount annotation carries no identity worth comparing: replacing one
    /// with an identical one leaves the container mounting exactly the same storage, and none of
    /// the rules reads the order they were declared in. Sorting is what makes that true of a
    /// reordering as well, while still distinguishing a duplicate from a single mount — two mounts
    /// on one target is itself a failure the rules report.
    /// </remarks>
    private static System.Collections.Immutable.ImmutableArray<string> CaptureDataStorageMounts(IResource resource) =>
        [.. resource.Annotations.OfType<ContainerMountAnnotation>()
            .Select(DescribeMountForSeal)
            .OrderBy(description => description, StringComparer.Ordinal)];

    private static string DescribeMountForSeal(ContainerMountAnnotation mount) =>
        string.Create(
            CultureInfo.InvariantCulture,
            // An anonymous volume has no source at all, which is not the same as an empty one.
            $"{(int)mount.Type}\u0000{(mount.Source is null ? "-" : "+" + mount.Source)}\u0000{mount.Target}\u0000{(mount.IsReadOnly ? "ro" : "rw")}");

    /// <summary>
    /// Fails the resource when anything the storage verdict rests on changed after the verdict was
    /// recorded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called from <see cref="VerifyTerminalConfigurationSeal"/>, and so from the two checkpoints
    /// Aspire never caches: the container-runtime-arguments callback, which a run re-invokes on
    /// every container creation after the last opportunity a caller has to change anything, and
    /// the manifest publishing callback, which the publishing pipeline restores after every model
    /// event and Aspire runs while it serializes the resource.
    /// </para>
    /// <para>
    /// Nothing is repaired and nothing is re-judged. Re-running the rules is not an option: Aspire
    /// would keep the recorded environment anyway, so a second verdict would describe a container
    /// that is not the one being created. Starting or publishing on a verdict that has since been
    /// contradicted is the failure this exists to prevent.
    /// </para>
    /// <para>
    /// No value is reported, only what kind of thing changed: the callback that changed the model
    /// may well be the one carrying a secret.
    /// </para>
    /// </remarks>
    private static void VerifyDataStorageSeal(DocumentDBServerResource resource)
    {
        if (resource.Annotations.OfType<DataStorageGuardAnnotation>().LastOrDefault() is not { } guard ||
            guard.Seal is not { } seal)
        {
            return;
        }

        // Membership catches a pipeline that grew or shrank; being last catches one whose existing
        // callbacks were reordered around the guard, which changes the last writer just as surely.
        // The command-line pipeline gets the same treatment from the terminal guard.
        EnsureGuardRunsLast(resource, guard.Environment, "environment");

        var current = CaptureDataStorageSeal(resource, seal.DataPath);

        if (!current.Mounts.SequenceEqual(seal.Mounts, StringComparer.Ordinal))
        {
            throw StaleDataStorageConfiguration(resource, seal, "a volume or bind mount was added, removed or changed");
        }

        if (!SameCallbacks(current.EnvironmentCallbacks, seal.EnvironmentCallbacks))
        {
            throw StaleDataStorageConfiguration(
                resource, seal, $"an environment callback was added or removed, so {DataPathEnvVarName} is no longer known to be the value that was judged");
        }

        if (!SameCallbacks(current.CommandLineCallbacks, seal.CommandLineCallbacks))
        {
            throw StaleDataStorageConfiguration(
                resource, seal, "a command-line callback was added or removed, so the reserved data-path arguments were not all scanned");
        }

        if (!SameCallbacks(current.RuntimeCallbacks, seal.RuntimeCallbacks))
        {
            throw StaleDataStorageConfiguration(
                resource, seal, "a container-runtime-argument callback was added or removed, and those run after this check and before the container's environment is gathered");
        }

        if (current.ExplicitlyStarted != seal.ExplicitlyStarted)
        {
            throw StaleDataStorageConfiguration(
                resource, seal, "its explicit-start setting changed, which is what decides whether sharing a data directory is a warning or a failure");
        }

        if (!current.Image.Equals(seal.Image))
        {
            throw StaleDataStorageConfiguration(
                resource, seal, "the image it will run changed, and whether the data directory is interlocked is a property of the release");
        }
    }

    private static InvalidOperationException StaleDataStorageConfiguration(
        DocumentDBServerResource resource,
        DataStorageSeal seal,
        string change)
    {
        var judged = seal.DataPath is { } dataPath
            ? $"its data directory ('{dataPath}')"
            : "its storage (it mounted none)";

        return new InvalidOperationException(
            $"DocumentDB resource '{resource.Name}' was changed after {judged} had already been " +
            $"checked: {change}. Aspire records each callback's result the first time it runs and " +
            $"reuses it for the rest of the run, so the storage rules cannot be applied again to " +
            $"the configuration the container or the manifest would actually receive. This usually " +
            $"comes from building the resource's configuration early — ExecutionConfigurationBuilder " +
            $"from an IDistributedApplicationLifecycleHook or an event subscriber — and then " +
            $"changing the resource. The resource is failed instead of being started or published " +
            $"on an unchecked data directory: a read-only mount there stops DocumentDB from taking " +
            $"ownership of it, and a shared one puts two clusters on one directory. Recovery: " +
            $"finish configuring the resource before anything reads its configuration, or make the " +
            $"change part of the application model (WithDataVolume(), WithDataBindMount(...), " +
            $"WithEnvironment(\"{DataPathEnvVarName}\", ...)) while it is being built.");
    }

    /// <summary>
    /// A logger for the guard's advisory warnings.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not <see cref="EnvironmentCallbackContext.Logger"/>: Aspire discovers a container's
    /// dependencies before it builds its configuration, and that pass runs the environment
    /// callbacks — through the same one-shot cache — with no logger attached. The guard's callback
    /// is therefore evaluated once, usually by that pass, and anything written to the context's
    /// logger is discarded. The AppHost's own services are captured when the guard is installed so
    /// there is a logger to write to whichever pass gets there first.
    /// </para>
    /// <para>
    /// The destination is the AppHost's log, under
    /// <see cref="StorageLoggerCategory"/>, rather than the resource's log pane. These warnings are
    /// about how the resource was configured and are produced before the container exists, so they
    /// belong with the orchestration diagnostics; the resource pane carries the container's own
    /// output, whose stream is not yet being replayed when the configuration is built.
    /// </para>
    /// </remarks>
    private static ILogger? TryGetStorageLogger(
        DocumentDBServerResource resource,
        EnvironmentCallbackContext context)
    {
        var services = resource.Annotations.OfType<DataStorageGuardAnnotation>().LastOrDefault()?.Services
            ?? context.ExecutionContext.Services;

        return services?.GetService<ILoggerFactory>()?.CreateLogger(StorageLoggerCategory)
            ?? context.Logger;
    }

    /// <summary>
    /// Refuses a mount target whose <c>..</c> segments reach above the container root. Docker does
    /// not refuse one: it clamps the target and mounts on the clamped path, so <c>/../data</c>
    /// becomes <c>/data</c> and the caller's storage lands on a directory they did not name. That
    /// is unsafe in both directions — an unrelated mount can take over the data directory, and a
    /// mount meant for the data directory can collide with another — so the spelling is refused
    /// before the container is created.
    /// </summary>
    private static void RejectMountTargetsThatEscapeContainerRoot(IResource resource)
    {
        foreach (var mount in resource.Annotations.OfType<ContainerMountAnnotation>())
        {
            if (ClassifyContainerPath(mount.Target, out var canonical) != ContainerPathProblem.EscapesRoot)
            {
                continue;
            }

            var description = mount.Type == ContainerMountType.BindMount
                ? $"bind mount of '{mount.Source}'"
                : mount.Source is null ? "anonymous volume" : $"volume '{mount.Source}'";

            throw new InvalidOperationException(
                $"DocumentDB resource '{resource.Name}' mounts a {description} at '{mount.Target}', " +
                $"which reaches above the container root. The container runtime does not refuse " +
                $"that spelling — it clamps the target and mounts on '{canonical}' instead, so the " +
                $"storage lands on a directory the call never named and can silently become, or " +
                $"collide with, the DocumentDB data directory. Recovery: write the resolved target " +
                $"('{canonical}') if that is what was meant, or correct the path.");
        }
    }

    /// <summary>
    /// The container path DocumentDB will really write to on this run, canonicalized and written
    /// back into the environment so the container consumes exactly the value that was judged.
    /// Returns <see langword="null"/> when the value cannot be a path on this run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>DATA_PATH</c> is an ordinary environment variable, so the value that reaches the
    /// container is whatever the environment callbacks leave behind — the storage helpers' writes,
    /// a raw <c>WithEnvironment("DATA_PATH", ...)</c>, or a callback that computes one — with the
    /// last writer winning. This runs as the last callback in that same pipeline, so it reads the
    /// final value rather than re-running anything.
    /// </para>
    /// <para>
    /// A deferred value (a parameter, a reference expression) is resolved once, here, with the
    /// context Aspire itself builds for an environment variable, and the canonical result replaces
    /// it. Aspire then has a string to render and never asks the provider again, so there is
    /// exactly one evaluation and no way for the guard and the container to see different values.
    /// </para>
    /// <para>
    /// In publish mode a deferred value cannot be resolved: it is a manifest expression, and
    /// reading the value behind it would both fail (the value belongs to the deployment, not the
    /// build) and put a secret into a place it does not belong. Such a value is therefore refused
    /// whenever the resource also mounts storage, because then the read-only, duplicate-mount and
    /// shared-data-directory rules would all be answering about a directory nobody can name. A
    /// resource that mounts nothing has no storage to get wrong, so there the deferred value is
    /// left alone.
    /// </para>
    /// <para>
    /// Nothing else is read or resolved: the password and every other environment value are left
    /// exactly as the callbacks produced them.
    /// </para>
    /// <para>
    /// An absent, null or empty <c>DATA_PATH</c> is the image's own default. The entrypoint
    /// applies <c>DATA_PATH=${DATA_PATH:-/data}</c>, which treats empty and unset alike, so an
    /// empty value is judged as <c>/data</c> rather than turned into a failure of this package's
    /// invention — and the canonical <c>/data</c> is then written into the environment, so a custom
    /// image whose own default is somewhere else cannot quietly write to a directory the guard
    /// never looked at.
    /// </para>
    /// </remarks>
    private static async ValueTask<string?> CanonicalizeEffectiveDataPathAsync(
        IResource resource,
        EnvironmentCallbackContext context)
    {
        if (!context.EnvironmentVariables.TryGetValue(DataPathEnvVarName, out var value) || value is null)
        {
            // Written rather than assumed: the checks below are about '/data', so '/data' is what
            // the container must be told to use, whatever its image would have defaulted to.
            context.EnvironmentVariables[DataPathEnvVarName] = DefaultMountedDataPath;
            return DefaultMountedDataPath;
        }

        string? dataPath;
        if (value is string literal)
        {
            dataPath = literal;
        }
        else if (context.ExecutionContext.IsPublishMode)
        {
            if (resource.Annotations.OfType<ContainerMountAnnotation>().Any())
            {
                throw new InvalidOperationException(
                    $"DocumentDB resource '{resource.Name}' sets {DataPathEnvVarName} to a value that " +
                    $"is only known at deployment time, and also mounts storage. In publish mode the " +
                    $"value is a manifest expression, so the data directory cannot be identified: the " +
                    $"read-only, duplicate-mount and shared-data-directory rules would all be silently " +
                    $"skipped, and a manifest that puts two DocumentDB resources on one data directory " +
                    $"would be published without complaint. Resolving it here is not an option either " +
                    $"— the value belongs to the deployment, and a parameter may be a secret. " +
                    $"Recovery: give {DataPathEnvVarName} a literal path (or leave it to " +
                    $"WithDataVolume()/WithDataBindMount(...), which mount the container default " +
                    $"'{DefaultMountedDataPath}'), and use a parameter for the storage source instead " +
                    $"of the container path.");
            }

            // No mounts, so there is no storage this value could be wrong about.
            return null;
        }
        else if (value is IValueProvider provider)
        {
            // The same context Aspire's own environment resolution passes: the resource asking for
            // the value, in this AppHost invocation.
            dataPath = await provider.GetValueAsync(
                new ValueProviderContext { ExecutionContext = context.ExecutionContext, Caller = resource },
                context.CancellationToken).ConfigureAwait(false);
        }
        else
        {
            // Matches how Aspire renders a value that is neither a string nor a value provider.
            dataPath = value.ToString();
        }

        if (string.IsNullOrEmpty(dataPath))
        {
            // Null is dropped by Aspire and empty is the image's own default; both mean '/data'.
            // The canonical string is written back so the dashboard and the container agree.
            context.EnvironmentVariables[DataPathEnvVarName] = DefaultMountedDataPath;
            return DefaultMountedDataPath;
        }

        var canonical = ClassifyContainerPath(dataPath, out var resolved) switch
        {
            ContainerPathProblem.None => resolved,

            ContainerPathProblem.NotAbsolute => throw new InvalidOperationException(
                InvalidDataPathMessage(
                    resource,
                    dataPath,
                    "is not an absolute path inside the container (it does not start with '/')")),

            ContainerPathProblem.EscapesRoot => throw new InvalidOperationException(
                InvalidDataPathMessage(
                    resource,
                    dataPath,
                    $"reaches above the container root. There is nothing above '/', so the path " +
                    $"silently becomes '{resolved}' — and a mount written the same way is clamped " +
                    $"the same way, which is how storage ends up somewhere nobody named")),

            _ => throw new InvalidOperationException(
                InvalidDataPathMessage(
                    resource,
                    dataPath,
                    "resolves to the container root '/': the runtime collapses '.' and '..' " +
                    "segments before mounting, so an alias such as '/data/..' is the root itself, " +
                    "which cannot hold a PostgreSQL cluster")),
        };

        context.EnvironmentVariables[DataPathEnvVarName] = canonical;
        return canonical;
    }

    /// <summary>
    /// The mount that really supplies the data directory, and the container path it lands on.
    /// </summary>
    /// <remarks>
    /// A mount does not have to be <em>on</em> <c>DATA_PATH</c> to back it: a volume mounted at
    /// <c>/data</c> also supplies <c>/data/cluster</c>. The most specific mount wins, exactly as
    /// the kernel resolves it, so a mount on <c>/data/cluster</c> takes precedence over one on
    /// <c>/data</c>. Matching is on segment boundaries, so <c>/database</c> does not back
    /// <c>/data</c>.
    /// </remarks>
    private static (string? Target, List<ContainerMountAnnotation> Mounts) SelectBackingMount(
        IResource resource,
        string canonicalDataPath)
    {
        string? bestTarget = null;
        var mounts = new List<ContainerMountAnnotation>();

        foreach (var mount in resource.Annotations.OfType<ContainerMountAnnotation>())
        {
            if (ResolveMountTarget(mount) is not { } target || !BacksContainerPath(target, canonicalDataPath))
            {
                continue;
            }

            if (bestTarget is null || target.Length > bestTarget.Length)
            {
                bestTarget = target;
                mounts.Clear();
                mounts.Add(mount);
            }
            else if (string.Equals(target, bestTarget, StringComparison.Ordinal))
            {
                mounts.Add(mount);
            }
        }

        return (bestTarget, mounts);
    }

    private static string ReadOnlyDataStorageMessage(
        IResource resource,
        string dataPath,
        string mountTarget,
        ContainerMountAnnotation mount)
    {
        var backing = string.Equals(mountTarget, dataPath, StringComparison.Ordinal)
            ? $"its data directory ('{dataPath}')"
            : $"'{mountTarget}', the directory that backs its data directory ('{dataPath}')";

        var source = mount.Type == ContainerMountType.BindMount
            ? $"'{mount.Source}'"
            : mount.Source is null ? "the anonymous volume" : $"volume '{mount.Source}'";

        return
            $"DocumentDB resource '{resource.Name}' mounts {backing} read-only. DocumentDB requires " +
            $"a writable data directory: the container entrypoint takes ownership of it and " +
            $"PostgreSQL initialises and writes WAL there. The container would run for about a " +
            $"minute and then fail with the misleading banner 'PostgreSQL failed to start within 60 " +
            $"seconds', hiding the real cause ('initdb: error: could not change permissions of " +
            $"directory \"{dataPath}\": Read-only file system'). Recovery: mount {source} writable, " +
            $"or use WithDataVolume()/WithDataBindMount(...), which reject read-only data storage " +
            $"up front.";
    }

    /// <summary>
    /// Registers the host storage this resource's data directory occupies and fails — or warns —
    /// when another DocumentDB resource has already registered the same one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each resource registers while its own configuration pipeline runs, so no peer's callbacks
    /// are ever executed on its behalf and no unrelated value is resolved. Every peer already
    /// registered on the same storage is examined before anything is reported: a pairing that only
    /// warrants a warning must not mask a later peer that warrants a failure, and which resource
    /// reaches the storage first must not change the verdict.
    /// </para>
    /// <para>
    /// What is registered is the directory the cluster really occupies, not the pair of strings it
    /// was spelled with. For a bind mount that is one host path — the mount source with the part of
    /// <c>DATA_PATH</c> that falls below the mount target appended — so a resource that binds
    /// <c>/srv/documentdb</c> and writes to <c>/data/cluster</c> and a resource that binds
    /// <c>/srv/documentdb/cluster</c> and writes to <c>/data</c> are recognised as the one
    /// directory they are. It is compared with the host's own case rules, which is also what makes
    /// <c>/data/Cluster</c> and <c>/data/cluster</c> one directory on a case-insensitive host and
    /// two on Linux.
    /// </para>
    /// <para>
    /// A volume name is not a path and cannot be combined with one, so a volume's identity stays
    /// its name plus the subdirectory, compared exactly: the container reads that subdirectory on
    /// its own case-sensitive filesystem.
    /// </para>
    /// </remarks>
    private static void ClaimDataStorage(
        DataStorageCoordinator coordinator,
        DocumentDBServerResource resource,
        ContainerMountAnnotation dataMount,
        string mountTarget,
        string dataPath,
        EnvironmentCallbackContext context,
        ref int sharedStorageWarningLogged)
    {
        // Two resources contend only when their data directories are the same directory: one
        // volume shared as '/data/alpha' and '/data/beta' is two directories, not one cluster.
        var subpath = dataPath.Length == mountTarget.Length
            ? string.Empty
            : dataPath[(mountTarget.Length + 1)..];

        string key;
        string description;

        if (dataMount.Type == ContainerMountType.BindMount)
        {
            var hostDirectory = CanonicalizeHostDataDirectory(dataMount.Source!, subpath);
            description = $"host directory '{hostDirectory}'";

            key = "bind\u0000" + (HostPathComparison == StringComparison.Ordinal
                ? hostDirectory
                : hostDirectory.ToUpperInvariant());
        }
        else
        {
            description = $"volume '{dataMount.Source}'";
            if (subpath.Length > 0)
            {
                description += $" (subdirectory '{subpath}')";
            }

            key = $"volume\u0000{dataMount.Source}\u0000{subpath}";
        }

        var peers = coordinator.Register(key, resource);
        if (peers.Count == 0)
        {
            return;
        }

        var thisInterlocked = ResolvesToDataVolumeAwareImage(resource);
        var thisExplicitlyStarted = resource.Annotations.OfType<ExplicitStartupAnnotation>().Any();
        List<string>? warnings = null;

        foreach (var other in peers)
        {
            // The refusal is a feature of the image, not of Aspire: only a pair that both hold the
            // lock can be trusted to fail loudly instead of corrupting the cluster, so only such a
            // pair is eligible for the warning downgrade.
            var bothInterlocked = thisInterlocked && ResolvesToDataVolumeAwareImage(other);
            var explicitlyStarted =
                thisExplicitlyStarted || other.Annotations.OfType<ExplicitStartupAnnotation>().Any();

            if (bothInterlocked && explicitlyStarted)
            {
                (warnings ??= []).Add(SharedDataDirectoryMessage(
                    resource, other, description, interlocked: true, explicitStartNote: true));
                continue;
            }

            throw new InvalidOperationException(
                SharedDataDirectoryMessage(resource, other, description, bothInterlocked, explicitStartNote: false));
        }

        if (warnings is not null && Interlocked.CompareExchange(ref sharedStorageWarningLogged, 1, 0) == 0)
        {
            var logger = TryGetStorageLogger(resource, context);
            foreach (var warning in warnings)
            {
                logger?.LogWarning("{Message}", warning);
            }
        }
    }

    /// <summary>
    /// Refuses <c>-d</c> and <c>--data-path</c>, the container entrypoint's own way of setting the
    /// data directory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>--data-path</c> is documented by the image as "Overrides DATA_PATH environment variable",
    /// and the entrypoint does exactly that: <c>export DATA_PATH=$1</c> while parsing arguments,
    /// before <c>DATA_PATH=${DATA_PATH:-/data}</c> applies the default. A resource that passes it
    /// would move its data directory somewhere the environment never mentions, past every rule
    /// this guard applies. <c>-d</c> is not accepted by the images this package supports — the
    /// entrypoint answers <c>Unknown option -d</c> and exits 1 — and is reserved here so that a
    /// future short form cannot become a silent second channel.
    /// </para>
    /// <para>
    /// The argument is refused rather than honoured. Following it would mean resolving the
    /// argument pipeline as well, and the same value can only be trusted if it is resolved exactly
    /// once; the environment variable already has a single, checked path through this guard, so
    /// there is nothing to gain by adding a second.
    /// </para>
    /// <para>
    /// A token that is not a literal string cannot be read without resolving it, and resolving it
    /// here would be a second evaluation of a value Aspire is about to evaluate itself — of a
    /// parameter whose sensitivity would be lost if the resolved string were written back, at that.
    /// So such a token is refused unless its position makes it impossible for it to be an option at
    /// all: the entrypoint's own grammar is <c>--option value</c>, so a token that directly follows
    /// a literal option known to take a value is that option's operand and is left alone. Anywhere
    /// else it could resolve to <c>--data-path</c>, and the resource is failed instead.
    /// </para>
    /// </remarks>
    private static void RejectReservedDataPathArguments(IResource resource, IEnumerable<object> arguments)
    {
        // Mirrors the entrypoint's own cursor: an option that takes a value consumes exactly the
        // next token, whatever that token looks like. Tracking that for literal tokens too is what
        // keeps the two cursors together — '--username --owner X' feeds '--owner' to '--username'
        // and then reads 'X' as an option name, and a model that only tracked deferred tokens would
        // have believed 'X' was sheltered.
        var expectOperand = false;

        foreach (var argument in arguments)
        {
            if (expectOperand)
            {
                expectOperand = false;
                continue;
            }

            if (argument is not string text)
            {
                throw new InvalidOperationException(
                    $"DocumentDB resource '{resource.Name}' passes a command-line argument whose value " +
                    $"is only known later (a parameter or an expression) in a position where the " +
                    $"container entrypoint reads an option name. The entrypoint treats " +
                    $"'--data-path' as an override of the {DataPathEnvVarName} environment variable, " +
                    $"so a token that resolved to it there would move the data directory past the " +
                    $"read-only, duplicate-mount and shared-data-directory checks — and the guard " +
                    $"cannot rule that out without resolving the token a second time, which would " +
                    $"both duplicate Aspire's own evaluation and risk exposing a secret. Recovery: " +
                    $"pass option names as literal strings (a deferred value is fine as the operand " +
                    $"of one, as in WithArgs(\"--log-level\", level)), and set the data directory " +
                    $"through storage — WithDataVolume(), WithDataBindMount(...), or " +
                    $"WithEnvironment(\"{DataPathEnvVarName}\", ...).");
            }

            var name = text;
            var separator = text.IndexOf('=', StringComparison.Ordinal);
            if (separator >= 0)
            {
                name = text[..separator];
            }

            if (string.Equals(name, "--data-path", StringComparison.Ordinal) ||
                string.Equals(name, "-d", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"DocumentDB resource '{resource.Name}' passes the command-line argument '{text}'. " +
                    $"The container entrypoint treats '--data-path' as an override of the DATA_PATH " +
                    $"environment variable ('-d' is reserved for the same setting), so it would move " +
                    $"the data directory to a path the environment never names — past the read-only, " +
                    $"duplicate-mount and shared-data-directory checks, and past the mount that was " +
                    $"supposed to back it. Recovery: set the data directory through storage instead — " +
                    $"WithDataVolume(), WithDataBindMount(...), or WithEnvironment(\"{DataPathEnvVarName}\", ...) " +
                    $"— and remove the argument.");
            }

            // An option written as '--option=value' carries its own operand, so the next token is
            // read as an option name again.
            expectOperand = separator < 0 && s_valueTakingEntrypointOptions.Contains(name);
        }
    }

    /// <summary>
    /// The container entrypoint options that consume the token after them, so that a deferred
    /// token in that position is an operand rather than a possible <c>--data-path</c>.
    /// </summary>
    /// <remarks>
    /// Taken from the entrypoint's own argument loop, which has carried the same set from
    /// <c>0.112.0</c> through <c>0.116.0</c>. Options that take no operand (<c>-h</c>,
    /// <c>--help</c>, <c>--skip-init-data</c>, <c>--disable-extended-rum</c>) are deliberately
    /// absent: the token after one of those is read as the next option name. An option a future
    /// image adds is absent too, which fails the deferred token closed — the safe direction.
    /// </remarks>
    private static readonly HashSet<string> s_valueTakingEntrypointOptions = new(StringComparer.Ordinal)
    {
        "--allow-external-connections",
        "--cert-path",
        "--create-user",
        "--documentdb-port",
        "--enable-telemetry",
        "--init-data",
        "--init-data-path",
        "--key-file",
        "--log-level",
        "--owner",
        "--password",
        "--pg-port",
        "--start-pg",
        "--tlsMode",
        "--toast-compression",
        "--username",
    };

    private static string SharedDataDirectoryMessage(
        IResource resource,
        IResource other,
        string description,
        bool interlocked,
        bool explicitStartNote)
    {
        var consequence = interlocked
            ? "DocumentDB v" + DocumentDBContainerImageTags.MinimumDeclaredDataVolumeVersion +
              " and later claim the data directory with an exclusive lock, because two PostgreSQL " +
              "instances on one data directory would corrupt it, so whichever container starts " +
              "second exits immediately with 'Error: another DocumentDB container is already using " +
              "the data directory'."
            : "At least one of the two runs an image with no data-directory interlock (DocumentDB " +
              "before v" + DocumentDBContainerImageTags.MinimumDeclaredDataVolumeVersion +
              ", an unrecognised tag, a custom image, a Dockerfile build, or an image pinned by " +
              "digest, whose version the tag beside it does not settle), so nothing refuses the " +
              "second start: two PostgreSQL instances would open the same data directory and " +
              "corrupt it silently.";

        var explicitStart = explicitStartNote
            ? " One of the two is started manually (WithExplicitStart()), so this is reported as a " +
              "warning rather than a failure — but only one of them may hold the directory at a time."
            : string.Empty;

        return
            $"DocumentDB resource '{resource.Name}' and DocumentDB resource '{other.Name}' both use " +
            $"the same {description} as their data directory. {consequence}{explicitStart} " +
            $"Recovery: give each resource its own storage (for example " +
            $"WithDataVolume(name: \"{resource.Name}-data\")).";
    }

    private static string InvalidDataPathMessage(IResource resource, string dataPath, string reason) =>
        $"DocumentDB resource '{resource.Name}' sets {DataPathEnvVarName} to '{dataPath}', which " +
        $"{reason}. The container writes its PostgreSQL cluster to that path, so the value is " +
        $"rejected before the container is created rather than left to fail as an opaque mount or " +
        $"permission error. Recovery: set {DataPathEnvVarName} to an absolute path below the " +
        $"container root, or leave it to WithDataVolume()/WithDataBindMount(...), which mount the " +
        $"container default '{DefaultMountedDataPath}'.";

    /// <summary>
    /// Whether the resource resolves to a curated <c>documentdb-local</c> image new enough to
    /// declare <c>/data</c> as a container volume and to claim the data directory with an
    /// exclusive lock. Custom images, unrecognised tags and images built from the caller's own
    /// Dockerfile resolve to <see langword="false"/>: the package cannot know what they do, so it
    /// neither promises the interlock nor warns about an image volume that may not exist.
    /// </summary>
    private static bool ResolvesToDataVolumeAwareImage(IResource resource) =>
        ResolveEffectiveImage(resource).KnownVersion is { } version &&
        version >= DocumentDBContainerImageTags.MinimumDeclaredDataVolumeVersion;

    /// <summary>
    /// Which DocumentDB resources hold which piece of host storage as their data directory, for
    /// one application model.
    /// </summary>
    /// <remarks>
    /// Registration happens from inside each resource's own configuration pipeline, so the answer
    /// is built from what each resource really resolved rather than from a peer's annotations read
    /// from the outside. Every resource that reaches a piece of storage is recorded, including one
    /// whose pairing was only a warning, so a later resource is judged against all of them and the
    /// verdict does not depend on which pipeline ran first. A resource that re-registers (Aspire
    /// re-evaluates callbacks on restart) keeps its registration, and one that moves to different
    /// storage releases the old one.
    /// </remarks>
    private sealed class DataStorageCoordinator
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IDistributedApplicationBuilder, DataStorageCoordinator> s_byApplication = new();

        private readonly object _lock = new();
        private readonly Dictionary<string, List<DocumentDBServerResource>> _holdersByKey = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _keyByResource = new(StringComparer.Ordinal);

        public static DataStorageCoordinator For(IDistributedApplicationBuilder builder) =>
            s_byApplication.GetValue(builder, static _ => new DataStorageCoordinator());

        /// <summary>
        /// Records <paramref name="resource"/> against <paramref name="key"/> and returns the
        /// resources already recorded against it.
        /// </summary>
        public IReadOnlyList<DocumentDBServerResource> Register(string key, DocumentDBServerResource resource)
        {
            lock (_lock)
            {
                if (_keyByResource.TryGetValue(resource.Name, out var previous) &&
                    !string.Equals(previous, key, StringComparison.Ordinal) &&
                    _holdersByKey.TryGetValue(previous, out var previousHolders))
                {
                    previousHolders.RemoveAll(held => ReferenceEquals(held, resource));
                }

                _keyByResource[resource.Name] = key;

                if (!_holdersByKey.TryGetValue(key, out var holders))
                {
                    _holdersByKey[key] = holders = [];
                }

                var peers = holders.Where(held => !ReferenceEquals(held, resource)).ToList();

                if (!holders.Any(held => ReferenceEquals(held, resource)))
                {
                    holders.Add(resource);
                }

                return peers;
            }
        }
    }

    /// <summary>
    /// Configures the DocumentDB Local container log level.
    /// </summary>
    /// <remarks>
    /// Starting with DocumentDB <c>0.114.0</c>, the gateway reads
    /// <c>DOCUMENTDB_LOG_LEVEL</c> as a tracing filter. This makes the API observably effective on
    /// the current default image. The legacy <c>LOG_LEVEL</c> variable is also set
    /// because the Local image entrypoint validates that contract, although no Local image uses it
    /// to select gateway verbosity. Images through <c>0.113.0</c> therefore treat this API as a
    /// verbosity no-op.
    /// <see cref="DocumentDBLogLevel.Quiet"/> remains mapped to <c>quiet</c> for API compatibility.
    /// It is not a tracing level: on <c>0.114.0</c> and later it becomes newly effective because
    /// the gateway parses it as an unmatched target directive, which suppresses gateway output but
    /// depends on upstream filter semantics.
    /// </remarks>
    /// <param name="builder">The resource builder for DocumentDB.</param>
    /// <param name="logLevel">The log level to configure.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<DocumentDBServerResource> WithLogLevel(this IResourceBuilder<DocumentDBServerResource> builder, DocumentDBLogLevel logLevel)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithEnvironment(context =>
        {
            var value = logLevel.ToEnvironmentValue();
            context.EnvironmentVariables[LogLevelEnvVarName] = value;
            context.EnvironmentVariables[LegacyLogLevelEnvVarName] = value;
        });
    }

    /// <summary>
    /// Mounts custom initialization scripts into the DocumentDB Local container.
    /// </summary>
    /// <remarks>
    /// The provided directory is bind-mounted at <c>/init_doc_db.d</c>, and the built-in sample data
    /// initialization is implicitly disabled so the mounted scripts are the only initialization source.
    /// DocumentDB v0.116-0 records initialization attempts in the data directory. Scripts run once
    /// for a new volume and are not reapplied when their contents change. A failed partial attempt is
    /// also not automatically retried; use a fresh or reset volume after correcting the scripts.
    /// </remarks>
    /// <param name="builder">The resource builder for DocumentDB.</param>
    /// <param name="source">The source directory on the host to mount into the container.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<DocumentDBServerResource> WithInitData(this IResourceBuilder<DocumentDBServerResource> builder, string source)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(source);

        return builder
            .WithBindMount(source, InitDataMountPath, isReadOnly: true)
            .WithEnvironment(context =>
            {
                context.EnvironmentVariables[InitDataEnvVarName] = "false";
                context.EnvironmentVariables[InitDataPathEnvVarName] = InitDataMountPath;
                context.EnvironmentVariables[SkipInitDataEnvVarName] = "true";
            });
    }

    /// <summary>
    /// Disables the built-in sample data initialization performed by the DocumentDB Local container.
    /// </summary>
    /// <remarks>
    /// Custom scripts configured through <see cref="WithInitData"/> are unaffected and still run
    /// for a new data volume.
    /// </remarks>
    /// <param name="builder">The resource builder for DocumentDB.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<DocumentDBServerResource> WithoutSampleData(this IResourceBuilder<DocumentDBServerResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithEnvironment(context =>
        {
            context.EnvironmentVariables[InitDataEnvVarName] = "false";
            context.EnvironmentVariables[SkipInitDataEnvVarName] = "true";
        });
    }

    /// <summary>
    /// Disables the <c>extended_rum</c> index access method in the DocumentDB Local container
    /// by setting <c>DISABLE_EXTENDED_RUM=true</c>.
    /// </summary>
    /// <remarks>
    /// Available in DocumentDB <c>v0.111-0</c> and later. On older container images the
    /// environment variable is ignored.
    /// </remarks>
    /// <param name="builder">The resource builder for DocumentDB.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<DocumentDBServerResource> WithoutExtendedRum(this IResourceBuilder<DocumentDBServerResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithEnvironment(context =>
        {
            context.EnvironmentVariables[DisableExtendedRumEnvVarName] = "true";
        });
    }

    /// <summary>
    /// Disables the DocumentDB Local container's automatic user creation by setting the
    /// upstream <c>CREATE_USER=false</c> environment variable.
    /// </summary>
    /// <remarks>
    /// For curated images <c>0.112.0</c> and older, built-in sample initialization is enabled by
    /// default. On a fresh container, this method must be paired with
    /// <see cref="WithoutSampleData"/> so the default initialization does not require the skipped
    /// credentials.
    /// <para>
    /// For images from <c>0.113.0</c> onward, including <c>0.116.0</c>, built-in sample
    /// initialization does not run unless requested. A fresh container can therefore remain
    /// running with user creation disabled when no initialization requiring those credentials is
    /// requested. The generated connection strings still will not authenticate unless the user
    /// already exists, typically in persisted storage created through <see cref="WithDataVolume"/>
    /// or <see cref="WithDataBindMount"/>.
    /// </para>
    /// <para>
    /// On every version, requested built-in sample initialization and custom scripts mounted
    /// through <see cref="WithInitData"/> authenticate using the configured credentials. If the
    /// skipped user does not already exist, that initialization can fail and cause the container
    /// to exit. <see cref="WithoutSampleData"/> disables only the built-in sample data; it does
    /// not disable custom initialization scripts.
    /// </para>
    /// </remarks>
    /// <param name="builder">The resource builder for DocumentDB.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<DocumentDBServerResource> WithoutUserCreation(this IResourceBuilder<DocumentDBServerResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithEnvironment(context =>
        {
            context.EnvironmentVariables[CreateUserEnvVarName] = "false";
        });
    }

    /// <summary>
    /// Mounts a custom TLS certificate and key into the DocumentDB Local container.
    /// </summary>
    /// <remarks>
    /// The certificate and key files are mounted at distinct container paths so that
    /// they do not collide even if their host file names are identical.
    /// </remarks>
    /// <param name="builder">The resource builder for DocumentDB.</param>
    /// <param name="certPath">The certificate file to mount into the container.</param>
    /// <param name="keyPath">The private key file to mount into the container.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<DocumentDBServerResource> WithTlsCertificate(this IResourceBuilder<DocumentDBServerResource> builder, string certPath, string keyPath)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(certPath);
        ArgumentException.ThrowIfNullOrEmpty(keyPath);

        var certTargetPath = GetMountedFilePath(certPath, nameof(certPath), "documentdb-cert-");
        var keyTargetPath = GetMountedFilePath(keyPath, nameof(keyPath), "documentdb-key-");

        return builder
            .WithBindMount(certPath, certTargetPath, isReadOnly: true)
            .WithBindMount(keyPath, keyTargetPath, isReadOnly: true)
            .WithEnvironment(context =>
            {
                context.EnvironmentVariables[CertPathEnvVarName] = certTargetPath;
                context.EnvironmentVariables[KeyFileEnvVarName] = keyTargetPath;
            });
    }

    /// <summary>
    /// Enables or disables DocumentDB Local telemetry by setting the <c>ENABLE_TELEMETRY</c>
    /// environment variable.
    /// </summary>
    /// <param name="builder">The resource builder for DocumentDB.</param>
    /// <param name="enabled">Whether telemetry should be enabled.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// The <c>ENABLE_TELEMETRY</c> environment variable is not consumed by the DocumentDB gateway
    /// in container image v0.112-0 or later. On those images this method has no observable effect
    /// on the running container. Use <see cref="WithOpenTelemetryMetrics(IResourceBuilder{DocumentDBServerResource}, string?, bool, TimeSpan?, TimeSpan?, string?, string?)"/>
    /// to configure OTLP metrics export.
    /// </remarks>
    [Obsolete(
        "ENABLE_TELEMETRY is not consumed by the DocumentDB gateway in container image v0.112-0 " +
        "or later, so this method has no observable effect on those images. Use " +
        "WithOpenTelemetryMetrics(...) for OTLP metrics. This member is kept for binary " +
        "compatibility and may be removed in a future release.",
        error: false,
        DiagnosticId = "ASPIREDOCDB0001",
        UrlFormat = "https://github.com/microsoft/azure-databases-aspire/blob/main/docs/configuration.md#withtelemetry-obsolete")]
    public static IResourceBuilder<DocumentDBServerResource> WithTelemetry(this IResourceBuilder<DocumentDBServerResource> builder, bool enabled = true)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithEnvironment(context =>
        {
            context.EnvironmentVariables[EnableTelemetryEnvVarName] = enabled ? "true" : "false";
        });
    }

    /// <summary>
    /// Enables OpenTelemetry metrics export from the DocumentDB gateway via OTLP/gRPC.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Requires container image v0.112-0 or later. This API configures metrics only. The
    /// gateway also supports tracing in v0.116-0 and later, but this package does not yet
    /// expose a typed tracing API.
    /// </para>
    /// <para>
    /// The container default for <c>OTEL_METRICS_ENABLED</c> is <c>false</c>; calling this method
    /// flips it to <c>true</c> unless <paramref name="enabled"/> is explicitly set to <c>false</c>.
    /// </para>
    /// <para>
    /// Starting with DocumentDB v0.116-0, the gateway resolves telemetry settings as
    /// <em>JSON &gt; environment variable &gt; default</em>, reading them from
    /// <c>SetupConfiguration.json</c>, and the shipped file pins metrics off. Whenever this method
    /// is called against an official <c>documentdb-local</c> image of that version or later, it
    /// therefore wraps the container entrypoint so the container starts from a copy of that
    /// configuration with the shadowing keys removed. The copy is derived from the same directory
    /// the image's own entrypoint would read: an explicit <c>CONFIG_DIR</c>, else the packaged
    /// <c>/etc/documentdb/gateway</c> layout when present, else
    /// <c>$GATEWAY_HOME/pg_documentdb_gw</c>.
    /// </para>
    /// <para>
    /// The wrapper is applied for <c>enabled: false</c> as well, because a caller-supplied
    /// configuration file can turn metrics on from JSON and an explicit
    /// <c>enabled: false</c> has to win. The <c>TelemetryOptions.Metrics</c> object is removed
    /// whole, since this API owns that signal and any surviving key - including one a later
    /// gateway release adds - would re-pin a setting ahead of the environment precedence
    /// documented below, such as the <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> fallback. The shared
    /// identity keys are removed only
    /// when the corresponding parameter was explicitly supplied on some call, and
    /// <c>TelemetryOptions.Tracing</c> is never touched, so the stock image keeps its shipped
    /// service identity and its disabled tracing.
    /// </para>
    /// <para>
    /// Because the gateway builds one OpenTelemetry <c>Resource</c> for all signals, supplying
    /// <paramref name="serviceName"/> or <paramref name="serviceVersion"/> removes the shared
    /// JSON identity and therefore changes the identity of exported traces too, not only metrics.
    /// Omit them to keep the identity the configuration file specifies.
    /// </para>
    /// <para>
    /// The wrapper is expressed purely as the container <c>entrypoint</c> and <c>args</c>, both of
    /// which round-trip through the Aspire manifest, so publish mode, <c>azd</c> and direct run
    /// mode all execute the same thing. Custom images, tags outside the <c>pg{NN}-X.Y.Z</c>
    /// grammar and resources built from your own Dockerfile are left completely untouched — the
    /// last of those even when the resource's image annotation names the official image and a
    /// recognised tag, because what runs is the build output; private mirrors of the official
    /// image are not, because only the registry differs. Pinning the official image by digest throws, because the
    /// digest makes the version opaque and both applying and skipping the wrapper on a guess are
    /// silently wrong. Supplying your own container entrypoint on the same resource also throws,
    /// because the two cannot both own the container command. The wrapper needs <c>bash</c> and
    /// <c>jq</c>, which the official image provides; it fails the container start with a
    /// diagnostic rather than starting without the override if either is missing.
    /// </para>
    /// <para>
    /// Merge semantics across multiple calls on the same builder:
    /// <list type="bullet">
    ///   <item>
    ///     <paramref name="enabled"/> is non-nullable and is therefore written on every call.
    ///     The last call's value wins (defaulting to <c>true</c> when omitted), even if a
    ///     previous call set it to <c>false</c>.
    ///   </item>
    ///   <item>
    ///     All other parameters are nullable; later calls override only the environment variables
    ///     they explicitly set, and values from earlier calls are preserved for parameters left
    ///     at <see langword="null"/>.
    ///   </item>
    /// </list>
    /// </para>
    /// <para>
    /// When <paramref name="endpoint"/> is omitted, the gateway falls back to the standard OTLP/gRPC
    /// default (<c>http://localhost:4317</c>). In an Aspire container scenario, that fallback is
    /// rarely reachable, so an explicit endpoint pointing to your collector is recommended.
    /// </para>
    /// <para>
    /// <paramref name="exportInterval"/> and <paramref name="timeout"/> are written as integer
    /// milliseconds via <see cref="CultureInfo.InvariantCulture"/>. Values smaller than one
    /// millisecond (sub-ms ticks) truncate to <c>0</c>; callers should pass whole-millisecond or
    /// larger granularities.
    /// </para>
    /// </remarks>
    /// <param name="builder">The resource builder for DocumentDB.</param>
    /// <param name="endpoint">
    /// OTLP/gRPC endpoint of the collector that should receive metrics. When provided, sets
    /// <c>OTEL_EXPORTER_OTLP_METRICS_ENDPOINT</c> (which takes precedence over the generic
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> per the OpenTelemetry specification). Defaults to
    /// <see langword="null"/> (leave the environment variable unset; gateway falls back to its
    /// own default).
    /// </param>
    /// <param name="enabled">
    /// Whether metrics export is enabled. Sets <c>OTEL_METRICS_ENABLED</c>. Defaults to
    /// <see langword="true"/>: opting into this method clearly indicates the caller wants metrics on.
    /// </param>
    /// <param name="exportInterval">
    /// How often the gateway flushes accumulated metrics to the collector. When provided, sets
    /// <c>OTEL_METRIC_EXPORT_INTERVAL</c> (milliseconds, integer). Must be non-negative.
    /// </param>
    /// <param name="timeout">
    /// Per-export request timeout. When provided, sets <c>OTEL_EXPORTER_OTLP_METRICS_TIMEOUT</c>
    /// (milliseconds, integer). Must be non-negative.
    /// </param>
    /// <param name="serviceName">
    /// Logical service name attached to the telemetry. When provided, sets
    /// <c>OTEL_SERVICE_NAME</c> and, on affected images, removes the shared
    /// <c>TelemetryOptions.ServiceName</c> so this value wins for every signal.
    /// </param>
    /// <param name="serviceVersion">
    /// Logical service version attached to the telemetry. When provided, sets
    /// <c>OTEL_SERVICE_VERSION</c> and, on affected images, removes the shared
    /// <c>TelemetryOptions.ServiceVersion</c> so this value wins for every signal.
    /// </param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="endpoint"/>, <paramref name="serviceName"/>, or <paramref name="serviceVersion"/>
    /// is provided but is empty or whitespace.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="exportInterval"/> or <paramref name="timeout"/> is negative.
    /// </exception>
    /// <example>
    /// <code>
    /// var server = builder.AddDocumentDB("documentdb")
    ///                     .WithOpenTelemetryMetrics(
    ///                         endpoint: "http://otel-collector:4317",
    ///                         exportInterval: TimeSpan.FromSeconds(30));
    /// </code>
    /// </example>
    public static IResourceBuilder<DocumentDBServerResource> WithOpenTelemetryMetrics(
        this IResourceBuilder<DocumentDBServerResource> builder,
        string? endpoint = null,
        bool enabled = true,
        TimeSpan? exportInterval = null,
        TimeSpan? timeout = null,
        string? serviceName = null,
        string? serviceVersion = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (endpoint is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        }

        if (serviceName is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        }

        if (serviceVersion is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(serviceVersion);
        }

        if (exportInterval is { } ei)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(ei, TimeSpan.Zero, nameof(exportInterval));
        }

        if (timeout is { } to)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(to, TimeSpan.Zero, nameof(timeout));
        }

        EnsureOpenTelemetryGatewayConfiguration(
            builder,
            serviceName is not null,
            serviceVersion is not null);

        return builder.WithEnvironment(context =>
        {
            context.EnvironmentVariables[OtelMetricsEnabledEnvVarName] = enabled ? "true" : "false";

            if (endpoint is not null)
            {
                context.EnvironmentVariables[OtelExporterOtlpMetricsEndpointEnvVarName] = endpoint;
            }

            if (exportInterval is { } interval)
            {
                context.EnvironmentVariables[OtelMetricExportIntervalEnvVarName] =
                    ((long)interval.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
            }

            if (timeout is { } timeoutValue)
            {
                context.EnvironmentVariables[OtelExporterOtlpMetricsTimeoutEnvVarName] =
                    ((long)timeoutValue.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
            }

            if (serviceName is not null)
            {
                context.EnvironmentVariables[OtelServiceNameEnvVarName] = serviceName;
            }

            if (serviceVersion is not null)
            {
                context.EnvironmentVariables[OtelServiceVersionEnvVarName] = serviceVersion;
            }
        });
    }

    /// <summary>
    /// Wires the gateway configuration override that keeps the OpenTelemetry environment
    /// variables authoritative on images whose <c>SetupConfiguration.json</c> pins telemetry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The override is a container entrypoint wrapper rather than an injected container file
    /// because only <c>entrypoint</c> and <c>args</c> round-trip through the Aspire manifest, so
    /// the same mechanism is emitted verbatim for run mode, the manifest/azd path, and every
    /// other publisher. The wrapper derives a copy of the configuration the container would have
    /// used, deletes only the keys this package's environment variables have to win over, points
    /// <c>CONFIG_DIR</c> at the copy, and execs the image's own entrypoint.
    /// </para>
    /// <para>
    /// It is wired whenever the API is called at all, not only when metrics end up enabled: a
    /// caller-supplied configuration file can enable metrics from JSON, and
    /// <c>enabled: false</c> has to beat that.
    /// </para>
    /// <para>
    /// Both halves of the wrapper are resolved lazily against the resource's final image, because
    /// the image tag is routinely selected after this method runs (for example
    /// <c>WithOpenTelemetryMetrics().WithDocumentDBVersion(...)</c>). The entrypoint is applied
    /// from <see cref="BeforeStartEvent"/>, which the manifest publisher raises before it
    /// serializes the resource — and which is the last phase that can still change the entrypoint
    /// a manifest carries, because the publisher writes <c>entrypoint</c> before it evaluates
    /// <c>args</c>. The arguments come from the terminal command-line guard
    /// (<see cref="EnsureTerminalGuard"/>), which is the last command-line callback the
    /// resource has and fails the resource if it is not.
    /// </para>
    /// </remarks>
    private static void EnsureOpenTelemetryGatewayConfiguration(
        IResourceBuilder<DocumentDBServerResource> builder,
        bool serviceNameConfigured,
        bool serviceVersionConfigured)
    {
        var configuration = builder.Resource.Annotations
            .OfType<OpenTelemetryGatewayConfigurationAnnotation>()
            .SingleOrDefault();

        var firstCall = configuration is null;
        configuration ??= new OpenTelemetryGatewayConfigurationAnnotation();

        // The set of explicitly supplied identity parameters accumulates across calls, matching
        // how the environment variables those parameters write merge.
        configuration.ServiceNameConfigured |= serviceNameConfigured;
        configuration.ServiceVersionConfigured |= serviceVersionConfigured;

        if (!firstCall)
        {
            return;
        }

        builder.Resource.Annotations.Add(configuration);

        builder.ApplicationBuilder.Eventing.Subscribe<BeforeStartEvent>((_, _) =>
        {
            if (ResolveOpenTelemetryGatewayConfigurationRequirement(builder.Resource) !=
                GatewayConfigurationRequirement.Required)
            {
                return Task.CompletedTask;
            }

            if (configuration.EntrypointOwned)
            {
                // A later event must find the entrypoint this wrapper installed. Anything else
                // means the resource was re-pointed after the wrapper took ownership.
                if (!string.Equals(builder.Resource.Entrypoint, GatewayConfigurationShell, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"DocumentDB resource '{builder.Resource.Name}' replaced the container " +
                        $"entrypoint installed by WithOpenTelemetryMetrics() with " +
                        $"'{builder.Resource.Entrypoint ?? "<none>"}'. The OpenTelemetry " +
                        $"environment variables would be silently ignored on DocumentDB " +
                        $"v{FirstGatewayTelemetryConfigurationVersion} and later.");
                }

                return Task.CompletedTask;
            }

            if (builder.Resource.Entrypoint is { } callerEntrypoint)
            {
                throw new InvalidOperationException(
                    $"DocumentDB resource '{builder.Resource.Name}' sets the container " +
                    $"entrypoint to '{callerEntrypoint}', but WithOpenTelemetryMetrics() has " +
                    $"to own the entrypoint on DocumentDB " +
                    $"v{FirstGatewayTelemetryConfigurationVersion} and later. Those images " +
                    $"ship a SetupConfiguration.json whose telemetry values take precedence " +
                    $"over OTEL_* environment variables, so the metrics settings would be " +
                    $"silently ignored. Recovery: drop the custom entrypoint, or drop " +
                    $"WithOpenTelemetryMetrics() and configure telemetry from your own " +
                    $"entrypoint.");
            }

            builder.Resource.Entrypoint = GatewayConfigurationShell;
            configuration.EntrypointOwned = true;
            return Task.CompletedTask;
        });

        var guard = EnsureTerminalGuard(builder);

        guard.AddCommandLineStep(TerminalCommandLineOpenTelemetryWrapperRank, state =>
        {
            if (ResolveOpenTelemetryGatewayConfigurationRequirement(builder.Resource) !=
                GatewayConfigurationRequirement.Required)
            {
                return;
            }

            // The arguments are only meaningful to the entrypoint this wrapper installs. This step
            // runs from the resource's last command-line callback, after every event subscriber
            // and lifecycle hook, so it is the last point at which one that replaced the
            // entrypoint later in the same startup can still be caught - after which these
            // arguments would be spliced into someone else's command line.
            if (!configuration.EntrypointOwned ||
                !string.Equals(builder.Resource.Entrypoint, GatewayConfigurationShell, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"DocumentDB resource '{builder.Resource.Name}' resolved container arguments " +
                    $"with the container entrypoint set to " +
                    $"'{builder.Resource.Entrypoint ?? "<image default>"}' instead of the " +
                    $"'{GatewayConfigurationShell}' wrapper WithOpenTelemetryMetrics() installs. " +
                    $"On DocumentDB v{FirstGatewayTelemetryConfigurationVersion} and later that " +
                    $"wrapper is what makes the OTEL_* environment variables authoritative over " +
                    $"SetupConfiguration.json, and its arguments mean nothing to any other " +
                    $"entrypoint. Recovery: stop overriding the entrypoint of this resource - " +
                    $"including from a BeforeStartEvent subscriber or lifecycle hook - or drop " +
                    $"WithOpenTelemetryMetrics() and configure telemetry from your own " +
                    $"entrypoint.");
            }

            var script = BuildOpenTelemetryGatewayConfigurationScript(configuration);

            state.Args.Insert(0, GatewayConfigurationShellArgumentZero);
            state.Args.Insert(0, script);
            state.Args.Insert(0, GatewayConfigurationShellCommandOption);
            state.WrapperScript = script;
        });

        guard.AddCommandLineValidation(state => ValidateOpenTelemetryGatewayCommand(builder.Resource, state));
    }

    /// <summary>
    /// Returns the resource's terminal guard, installing it the first time it is asked for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This package owns exactly one <see cref="CommandLineArgsCallbackAnnotation"/>, one
    /// <see cref="ContainerRuntimeArgsCallbackAnnotation"/>, and one
    /// <see cref="ManifestPublishingCallbackAnnotation"/> per resource. Everything it needs to do
    /// to the container command line is a step of that one command-line callback, ordered by rank,
    /// followed by the validations that judge the finished list. One callback per pipeline is what
    /// makes the contract expressible at all: Aspire evaluates callbacks in annotation order over
    /// one shared value, so "last" is a single position, and two package callbacks that each
    /// demanded it would be an impossible requirement rather than a check.
    /// </para>
    /// <para>
    /// Being last is established at every phase and then verified. The callbacks are appended when
    /// the API that needs them is called, and moved back to the end of their pipelines at
    /// <see cref="BeforeStartEvent"/> — which covers every builder-time <c>WithArgs</c> and
    /// <c>WithEnvironment</c>, whatever the call order, in run and publish mode alike — then again
    /// at <see cref="ResourceEndpointsAllocatedEvent"/> and
    /// <see cref="BeforeResourceStartedEvent"/>, the last per-resource phases a run publishes.
    /// Anything appended after that is caught by the checks themselves.
    /// </para>
    /// <para>
    /// Position alone is not enough, because Aspire caches each callback's result for the run. A
    /// caller who builds the resource's configuration through the public
    /// <see cref="ExecutionConfigurationBuilder"/> — from an
    /// <see cref="Lifecycle.IDistributedApplicationLifecycleHook"/>, say — and only then changes
    /// the model gets a validated answer recorded before the change and reused after it. That is
    /// what the seal is for: the command-line callback records what the resource looked like when
    /// it produced its result, and the two checkpoints Aspire never caches compare it.
    /// </para>
    /// <list type="bullet">
    /// <item><description>Run: the container-runtime-arguments callback. Aspire re-invokes those on
    /// every container creation without caching, and it does so after the last opportunity a
    /// caller has to change anything — a caller's own runtime-arguments callback — and before the
    /// container's command, arguments and environment are read.</description></item>
    /// <item><description>Publish: the manifest callback itself. A publishing-pipeline prerequisite
    /// re-establishes it after every <see cref="Publishing.BeforePublishEvent"/> subscriber has
    /// completed, so a normal model event cannot replace or shadow the checkpoint; the callback
    /// then verifies while Aspire serializes the resource.</description></item>
    /// </list>
    /// <para>
    /// The annotation instances are moved, never re-created, so Aspire's per-callback result cache
    /// is untouched: the steps are evaluated exactly once per run and re-evaluated on restart,
    /// which is what keeps a deferred or secret-bearing value from being resolved twice.
    /// </para>
    /// </remarks>
    private static TerminalGuardAnnotation EnsureTerminalGuard(
        IResourceBuilder<DocumentDBServerResource> builder)
    {
        var resource = builder.Resource;

        if (resource.Annotations.OfType<TerminalGuardAnnotation>().LastOrDefault() is { } installed)
        {
            return installed;
        }

        var guard = new TerminalGuardAnnotation();

        guard.CommandLineCallback = new CommandLineArgsCallbackAnnotation(context =>
        {
            EnsureTerminalCallbackRunsLast(resource, guard.CommandLineCallback, "command-line");
            var state = guard.RunCommandLine(context);
            guard.Seal = CaptureTerminalConfigurationSeal(resource, state);
            return Task.CompletedTask;
        });

        guard.RuntimeCheckpoint = new ContainerRuntimeArgsCallbackAnnotation(_ =>
        {
            EnsureTerminalCallbackRunsLast(resource, guard.RuntimeCheckpoint, "container-runtime-arguments");
            VerifyTerminalCheckpoint(resource, guard);
            return Task.CompletedTask;
        });

        guard.ManifestCheckpoint = new ManifestPublishingCallbackAnnotation(async context =>
        {
            VerifyTerminalCheckpoint(resource, guard);

            // Whatever would have written this resource had the checkpoint not been installed
            // still writes it, so the manifest is byte-for-byte the one Aspire would have
            // produced. A caller's own writer is honoured; with none, this is a container.
            var displaced = resource.Annotations.OfType<ManifestPublishingCallbackAnnotation>()
                .LastOrDefault(annotation => !ReferenceEquals(annotation, guard.ManifestCheckpoint));

            if (displaced is null)
            {
                await context.WriteContainerAsync(resource).ConfigureAwait(false);
            }
            else if (displaced.Callback is { } callback)
            {
                await callback(context).ConfigureAwait(false);
            }
        });

        resource.Annotations.Add(guard);
        resource.Annotations.Add(guard.CommandLineCallback);
        resource.Annotations.Add(guard.RuntimeCheckpoint);
        EstablishManifestCheckpoint(resource, guard);
        RegisterTerminalManifestCheckpoint(builder.ApplicationBuilder, resource, guard);

        void RetakeLastPosition() => RetakeTerminalGuardPositions(resource, guard);

        builder.ApplicationBuilder.Eventing.Subscribe<BeforeStartEvent>((_, _) =>
        {
            RetakeLastPosition();
            return Task.CompletedTask;
        });

        builder.ApplicationBuilder.Eventing.Subscribe<ResourceEndpointsAllocatedEvent>(resource, (_, _) =>
        {
            RetakeLastPosition();
            return Task.CompletedTask;
        });

        builder.ApplicationBuilder.Eventing.Subscribe<BeforeResourceStartedEvent>(resource, (_, _) =>
        {
            RetakeLastPosition();
            return Task.CompletedTask;
        });

        builder.ApplicationBuilder.Eventing.Subscribe<Publishing.BeforePublishEvent>((_, _) =>
        {
            // Retake after every lifecycle hook. A pipeline prerequisite repeats the manifest
            // retake after later subscribers to this same event have completed.
            RetakeLastPosition();
            VerifyTerminalConfigurationSeal(resource, guard);
            return Task.CompletedTask;
        });

        return guard;
    }

    /// <summary>
    /// Moves an annotation the resource already carries to the end of the collection, so it is the
    /// last of its kind when Aspire gathers it. The instance is preserved, so its cached result —
    /// and with it the guarantee of a single evaluation — is preserved too.
    /// </summary>
    private static void MoveToEnd(DocumentDBServerResource resource, IResourceAnnotation annotation)
    {
        if (!resource.Annotations.Remove(annotation))
        {
            return;
        }

        resource.Annotations.Add(annotation);
    }

    /// <summary>
    /// Puts the package's callbacks back at the end of their pipelines.
    /// </summary>
    /// <remarks>
    /// Called at every lifecycle phase, and again by any API that adds a callback of its own after
    /// the guard was installed, so the guard is last from the moment the model is written rather
    /// than only from the moment the application starts. The data-storage guard's environment
    /// callback is moved from here too: it has to be last in its own pipeline for the same reason
    /// and by the same phases, and publish raises no per-resource event, so this is the only place
    /// that can put it back after a lifecycle hook has appended one of its own.
    /// </remarks>
    private static void RetakeTerminalGuardPositions(
        DocumentDBServerResource resource,
        TerminalGuardAnnotation guard)
    {
        MoveToEnd(resource, guard.CommandLineCallback);
        MoveToEnd(resource, guard.RuntimeCheckpoint);

        if (resource.Annotations.OfType<DataStorageGuardAnnotation>().LastOrDefault() is { } storage)
        {
            MoveToEnd(resource, storage.Environment);
        }

        EstablishManifestCheckpoint(resource, guard);
    }

    /// <summary>
    /// Puts the guard's manifest checkpoint in the position the manifest publisher reads — last —
    /// unless the resource is excluded from the manifest, in which case it is taken out again.
    /// </summary>
    /// <remarks>
    /// The publisher runs the <em>last</em> <see cref="ManifestPublishingCallbackAnnotation"/> and
    /// no other, so the checkpoint has to hold that position to run at all, and has to hand the
    /// writing on to whatever it displaced. A callback-less annotation is Aspire's
    /// <c>ExcludeFromManifest()</c>: the resource is not written at all, so there is no published
    /// configuration to check and the exclusion is left in place.
    /// </remarks>
    private static void EstablishManifestCheckpoint(
        DocumentDBServerResource resource,
        TerminalGuardAnnotation guard)
    {
        var displaced = resource.Annotations.OfType<ManifestPublishingCallbackAnnotation>()
            .LastOrDefault(annotation => !ReferenceEquals(annotation, guard.ManifestCheckpoint));

        resource.Annotations.Remove(guard.ManifestCheckpoint);

        if (displaced is not null && displaced.Callback is null)
        {
            return;
        }

        resource.Annotations.Add(guard.ManifestCheckpoint);
    }

    /// <summary>
    /// Registers one application-wide publishing step that restores every DocumentDB manifest
    /// checkpoint after all model events and before Aspire's manifest writer runs.
    /// </summary>
    /// <remarks>
    /// <c>WithManifestPublishingCallback(...)</c> replaces the last callback through a supported
    /// public API. A subscriber registered after this package's
    /// <see cref="Publishing.BeforePublishEvent"/> subscriber can therefore remove the checkpoint
    /// after the event-level retake. Pipeline resolution happens only after every subscriber has
    /// completed, so this prerequisite closes that window without competing with the checker: it
    /// only restores callback ownership, and the single callback still performs both storage and
    /// telemetry verification at serialization.
    /// <see cref="ResourceBuilderExtensions.ExcludeFromManifest{T}"/> remains the deliberate
    /// boundary; a resource that emits nothing has no published configuration to verify.
    /// An application that rewrites Aspire's publishing pipeline itself owns serialization order
    /// and is outside this resource-annotation contract; ordinary model events and manifest
    /// callback replacement are covered.
    /// </remarks>
    private static void RegisterTerminalManifestCheckpoint(
        IDistributedApplicationBuilder appBuilder,
        DocumentDBServerResource resource,
        TerminalGuardAnnotation guard)
    {
        if (!appBuilder.ExecutionContext.IsPublishMode)
        {
            return;
        }

        var registration = appBuilder.Services
            .Where(service => service.ServiceType == typeof(TerminalManifestCheckpointPipelineRegistration))
            .Select(service => service.ImplementationInstance)
            .OfType<TerminalManifestCheckpointPipelineRegistration>()
            .SingleOrDefault();

        if (registration is not null)
        {
            registration.Guards[resource] = guard;
            return;
        }

        registration = new TerminalManifestCheckpointPipelineRegistration();
        registration.Guards.Add(resource, guard);
        appBuilder.Services.AddSingleton(registration);

#pragma warning disable ASPIREPIPELINES001
        appBuilder.Pipeline.AddStep(
            TerminalManifestCheckpointPipelineStepName,
            _ =>
            {
                if (!registration.ManifestPublishing)
                {
                    return Task.CompletedTask;
                }

                foreach (var (registeredResource, registeredGuard) in registration.Guards)
                {
                    EstablishManifestCheckpoint(registeredResource, registeredGuard);
                }

                return Task.CompletedTask;
            });

        appBuilder.Pipeline.AddPipelineConfiguration(context =>
        {
            var manifestStep = context.Steps.SingleOrDefault(step =>
                string.Equals(step.Name, ManifestPublishingPipelineStepName, StringComparison.Ordinal));
            registration.ManifestPublishing = manifestStep is not null;

            if (manifestStep is not null &&
                !manifestStep.DependsOnSteps.Contains(
                    TerminalManifestCheckpointPipelineStepName,
                    StringComparer.Ordinal))
            {
                manifestStep.DependsOn(TerminalManifestCheckpointPipelineStepName);
            }

            return Task.CompletedTask;
        });
#pragma warning restore ASPIREPIPELINES001
    }

    private sealed class TerminalManifestCheckpointPipelineRegistration
    {
        public Dictionary<DocumentDBServerResource, TerminalGuardAnnotation> Guards { get; } =
            new(ReferenceEqualityComparer.Instance);

        public bool ManifestPublishing { get; set; }
    }

    /// <summary>
    /// Records what the container's command depends on, at the moment the command-line callback
    /// produced the result Aspire will reuse for the rest of the run.
    /// </summary>
    private static TerminalConfigurationSeal CaptureTerminalConfigurationSeal(
        DocumentDBServerResource resource,
        TerminalCommandLineState state) =>
        new(
            [.. resource.Annotations.OfType<CommandLineArgsCallbackAnnotation>()],
            [.. resource.Annotations.OfType<EnvironmentCallbackAnnotation>()],
            [.. resource.Annotations.OfType<ContainerRuntimeArgsCallbackAnnotation>()],
            resource.Entrypoint,
            ResolveEffectiveImage(resource),
            CaptureTerminalCommandSeal(resource, state));

    private static TerminalCommandSeal CaptureTerminalCommandSeal(
        DocumentDBServerResource resource,
        TerminalCommandLineState state)
    {
        var requirement = ResolveOpenTelemetryGatewayConfigurationRequirement(resource);

        if (state.WrapperScript is not { } script)
        {
            return new(requirement, null, null, false, null, false);
        }

        var args = state.Args;

        return new(
            requirement,
            script,
            args.Count > 0 ? args[0] as string : null,
            args.Count > 1 && ReferenceEquals(args[1], script),
            args.Count > 2 ? args[2] as string : null,
            args.Skip(3).OfType<string>().Any(argument =>
                string.Equals(argument, script, StringComparison.Ordinal)));
    }

    /// <summary>
    /// Everything the two uncached checkpoints judge, in the order they judge it: the hard image
    /// floors first, then the configuration seal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The floors are re-applied here because the events that report them are ordinary
    /// subscriptions: one registered after this package runs after them, and an image they cleared
    /// can be replaced before anything reads it. They are judged before the seal so an image that
    /// cannot work is reported with the recovery that applies to it rather than only as a change.
    /// </para>
    /// <para>
    /// This is what the container-runtime-arguments callback and the manifest publishing callback
    /// run, and nothing else does: those two are the moments a container is about to be created
    /// and a resource is actually being serialized.
    /// <see cref="ResourceBuilderExtensions.ExcludeFromManifest{T}"/> takes the second one out of
    /// the model, which is what keeps it the deliberate boundary — a resource that publishes
    /// nothing has no published image to judge.
    /// </para>
    /// </remarks>
    private static void VerifyTerminalCheckpoint(
        DocumentDBServerResource resource,
        TerminalGuardAnnotation guard)
    {
        if (DescribeIncompatibleImage(resource) is { } incompatible)
        {
            throw incompatible;
        }

        VerifyTerminalConfigurationSeal(resource, guard);
    }

    /// <summary>
    /// Fails the resource when anything the container's command depends on changed after the
    /// command-line callback produced the answer Aspire cached.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called from the two uncached checkpoints the integration owns — see
    /// <see cref="VerifyTerminalCheckpoint"/> — and from
    /// <see cref="Publishing.BeforePublishEvent"/>, which reports a stale configuration before the
    /// pipeline starts writing. Together they cover every supported way to change the model after
    /// the command line has been decided: appending or inserting a callback in either pipeline,
    /// re-pointing the entrypoint, and swapping the image, tag, digest or Dockerfile.
    /// </para>
    /// <para>
    /// Nothing is repaired. Re-running the wrapper is not an option — Aspire would keep the cached
    /// result anyway — and starting a container on an answer that has since been contradicted is
    /// the failure this exists to prevent.
    /// </para>
    /// <para>
    /// No value is reported, only what kind of thing changed: the callback that changed the model
    /// may well be the one carrying a secret.
    /// </para>
    /// </remarks>
    private static void VerifyTerminalConfigurationSeal(
        DocumentDBServerResource resource,
        TerminalGuardAnnotation guard)
    {
        EnsureTerminalCallbackRunsLast(resource, guard.CommandLineCallback, "command-line");
        EnsureTerminalCallbackRunsLast(resource, guard.RuntimeCheckpoint, "container-runtime-arguments");

        if (guard.Seal is { } seal)
        {
            if (!SameCallbacks(
                [.. resource.Annotations.OfType<CommandLineArgsCallbackAnnotation>()],
                seal.CommandLineCallbacks))
            {
                throw StaleConfiguration(resource, "a command-line callback was added or removed");
            }

            if (!SameCallbacks(
                [.. resource.Annotations.OfType<EnvironmentCallbackAnnotation>()],
                seal.EnvironmentCallbacks))
            {
                throw StaleConfiguration(resource, "an environment callback was added or removed");
            }

            if (!SameCallbacks(
                [.. resource.Annotations.OfType<ContainerRuntimeArgsCallbackAnnotation>()],
                seal.RuntimeCallbacks))
            {
                throw StaleConfiguration(resource, "a container-runtime-argument callback was added or removed");
            }

            if (!string.Equals(resource.Entrypoint, seal.Entrypoint, StringComparison.Ordinal))
            {
                throw StaleConfiguration(resource, "its container entrypoint changed");
            }

            if (!ResolveEffectiveImage(resource).Equals(seal.Image))
            {
                throw StaleConfiguration(resource, "the image it will run changed");
            }

            VerifyTerminalCommandSeal(resource, seal.Command);
        }

        // The storage rules answer in the environment pipeline, which is gathered separately from
        // the command line and can be gathered on its own, so they record their own verdict and
        // are checked from the same two checkpoints rather than from a second set.
        VerifyDataStorageSeal(resource);
    }

    /// <summary>
    /// Re-checks the load-bearing fixed prefix in the immutable command result against the model
    /// that is about to be serialized.
    /// </summary>
    /// <remarks>
    /// Aspire freezes a callback result as an immutable list. Once callback membership is
    /// unchanged, the caller arguments cannot rewrite the recorded prefix behind this check, so
    /// only the fixed tokens and script need to be retained; caller values are neither stored nor
    /// resolved.
    /// </remarks>
    private static void VerifyTerminalCommandSeal(
        DocumentDBServerResource resource,
        TerminalCommandSeal command)
    {
        var requirement = ResolveOpenTelemetryGatewayConfigurationRequirement(resource);

        if (requirement != command.GatewayRequirement)
        {
            throw StaleConfiguration(
                resource,
                "whether its cached command needs the OpenTelemetry compatibility wrapper changed");
        }

        if (requirement != GatewayConfigurationRequirement.Required)
        {
            if (command.WrapperScript is not null)
            {
                throw StaleConfiguration(
                    resource,
                    "its cached command carries an OpenTelemetry compatibility wrapper that is no longer applicable");
            }

            return;
        }

        var configuration = resource.Annotations
            .OfType<OpenTelemetryGatewayConfigurationAnnotation>()
            .Single();
        var expectedScript = BuildOpenTelemetryGatewayConfigurationScript(configuration);

        if (command.WrapperScript is null ||
            !string.Equals(command.ShellOption, GatewayConfigurationShellCommandOption, StringComparison.Ordinal) ||
            !command.ScriptIsSecondArgument ||
            !string.Equals(command.Delimiter, GatewayConfigurationShellArgumentZero, StringComparison.Ordinal) ||
            command.HasDuplicateWrapperScript ||
            !string.Equals(command.WrapperScript, expectedScript, StringComparison.Ordinal))
        {
            throw StaleConfiguration(
                resource,
                "its cached OpenTelemetry wrapper prefix, script or delimiter no longer matches the terminal command");
        }
    }

    /// <summary>
    /// Compares two recordings of one pipeline by membership rather than by order.
    /// </summary>
    /// <remarks>
    /// What the seal has to detect is a callback appearing or disappearing, because the app host
    /// evaluates each one at most once and then reuses the recorded result: a callback added after
    /// the recording runs unrecorded, and for arguments its result replaces the whole list. Order
    /// is deliberately not compared. This package moves its own callbacks to the end of their
    /// pipelines at every phase, so an order comparison would report the guard's own repositioning
    /// as a change; ordering is guaranteed instead by
    /// <see cref="EnsureTerminalCallbackRunsLast{TAnnotation}"/>, which puts this package last, and
    /// with it last the recorded result that Aspire keeps.
    /// </remarks>
    private static bool SameCallbacks<TAnnotation>(
        System.Collections.Immutable.ImmutableArray<TAnnotation> current,
        System.Collections.Immutable.ImmutableArray<TAnnotation> sealed_)
        where TAnnotation : class, IResourceAnnotation
    {
        if (current.Length != sealed_.Length)
        {
            return false;
        }

        // Identity, not equality: two annotations can be indistinguishable by value and still be
        // two separate recordings.
        var recorded = new HashSet<object>(sealed_, ReferenceEqualityComparer.Instance);

        return current.All(annotation => recorded.Contains(annotation));
    }

    private static InvalidOperationException StaleConfiguration(
        DocumentDBServerResource resource,
        string change) =>
        new(
            $"DocumentDB resource '{resource.Name}' was changed after its container command line " +
            $"had already been built: {change}. Aspire records each callback's result the first " +
            $"time it runs and reuses it for the rest of the run, so a configuration built before " +
            $"the change is the one the container would receive, and the checks that ran with it " +
            $"cannot be repeated. This usually comes from building the resource's configuration " +
            $"early — ExecutionConfigurationBuilder or GetArgumentValuesAsync from an " +
            $"IDistributedApplicationLifecycleHook or an event subscriber — and then changing the " +
            $"resource. The resource is failed instead of being started or published on a command " +
            $"line that was decided and then contradicted. Recovery: finish configuring the " +
            $"resource before anything reads its configuration, or make the change part of the " +
            $"application model (WithArgs(...), WithEnvironment(...), WithImageTag(...)) while it " +
            $"is being built.");

    /// <summary>
    /// Fails the resource when something appended a callback after the one this package owns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every rule this package applies is about the configuration the container receives, and a
    /// callback that runs afterwards can prepend to it, clear it, or reorder it. The wrapper is
    /// the sharpest case: <c>/bin/bash</c> reads its command from the first arguments, so a single
    /// value inserted in front turns the whole wrapper into an operand and the container starts
    /// nothing.
    /// </para>
    /// <para>
    /// That is reported rather than tolerated, and no value appears in the message: the point is
    /// the shape of the pipeline, and the callback that ran last may well be carrying a secret.
    /// </para>
    /// </remarks>
    private static void EnsureTerminalCallbackRunsLast<TAnnotation>(
        DocumentDBServerResource resource,
        TAnnotation guardCallback,
        string pipeline)
        where TAnnotation : class, IResourceAnnotation
    {
        var last = resource.Annotations.OfType<TAnnotation>().LastOrDefault();
        if (ReferenceEquals(last, guardCallback))
        {
            return;
        }

        throw new InvalidOperationException(
            $"DocumentDB resource '{resource.Name}' has a later {pipeline} callback registered " +
            $"after the one this package owns, so the configuration it built is not the one the " +
            $"container would receive. That callback is appended when the application starts and " +
            $"moved back to the end of the pipeline at the latest per-resource phase the run " +
            $"offers; a callback added after that usually comes from a subscriber registered " +
            $"after AddDocumentDB, or from an IDistributedApplicationLifecycleHook. The resource " +
            $"is failed instead of being started on a configuration that was checked and then " +
            $"changed. Recovery: make that configuration part of the application model " +
            $"(WithArgs(...), WithEnvironment(...)) while it is being built, or register the " +
            $"subscriber before AddDocumentDB.");
    }

    /// <summary>
    /// Verifies that the finished command line is exactly the one the gateway configuration
    /// wrapper needs, whenever the wrapper is required at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The wrapper is a <c>/bin/bash -c &lt;script&gt; -- &lt;image arguments&gt;</c> command line,
    /// and every part of that shape is load-bearing: bash reads the script from the value after
    /// <c>-c</c> and assigns the values after <c>--</c> to <c>$@</c>, which is what the script
    /// forwards to the image's own entrypoint. Anything in front of <c>-c</c> is a bash option or
    /// operand instead, a different script is a different container, and a second copy of the
    /// prefix would be handed to the image as an argument.
    /// </para>
    /// <para>
    /// The image is classified again here rather than trusted from the step that applied the
    /// wrapper, so an image, tag, digest or Dockerfile selected in between is judged on what the
    /// container will actually run.
    /// </para>
    /// <para>
    /// Nothing but the fixed tokens this package wrote is compared or reported. Caller arguments
    /// are counted, never read: one of them may be a parameter or an expression whose value is a
    /// secret, and resolving it here would both duplicate Aspire's own evaluation and risk putting
    /// it in an exception message.
    /// </para>
    /// </remarks>
    private static void ValidateOpenTelemetryGatewayCommand(
        DocumentDBServerResource resource,
        TerminalCommandLineState state)
    {
        var required = ResolveOpenTelemetryGatewayConfigurationRequirement(resource) ==
            GatewayConfigurationRequirement.Required;

        if (!required)
        {
            if (state.WrapperScript is null)
            {
                return;
            }

            throw new InvalidOperationException(
                $"DocumentDB resource '{resource.Name}' stopped needing the " +
                $"WithOpenTelemetryMetrics() compatibility wrapper while its container command " +
                $"line was being built, after that wrapper had already been written into it. " +
                $"Select the image before configuring metrics.");
        }

        if (state.WrapperScript is not { } script)
        {
            throw new InvalidOperationException(
                $"DocumentDB resource '{resource.Name}' needs the WithOpenTelemetryMetrics() " +
                $"compatibility wrapper on its container command line, but the finished command " +
                $"line does not carry it. On DocumentDB " +
                $"v{FirstGatewayTelemetryConfigurationVersion} and later the OTEL_* environment " +
                $"variables would be silently ignored. Recovery: select the image before " +
                $"configuring metrics.");
        }

        if (!string.Equals(resource.Entrypoint, GatewayConfigurationShell, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"DocumentDB resource '{resource.Name}' finished building its container command " +
                $"line with the entrypoint set to '{resource.Entrypoint ?? "<image default>"}' " +
                $"instead of the '{GatewayConfigurationShell}' wrapper " +
                $"WithOpenTelemetryMetrics() installs. Those arguments mean nothing to any other " +
                $"entrypoint. Recovery: stop overriding the entrypoint of this resource - " +
                $"including from a BeforeStartEvent subscriber or lifecycle hook - or drop " +
                $"WithOpenTelemetryMetrics() and configure telemetry from your own entrypoint.");
        }

        var args = state.Args;

        var intact = args.Count >= 3
            && args[0] is string option
            && string.Equals(option, GatewayConfigurationShellCommandOption, StringComparison.Ordinal)
            && ReferenceEquals(args[1], script)
            && args[2] is string delimiter
            && string.Equals(delimiter, GatewayConfigurationShellArgumentZero, StringComparison.Ordinal);

        if (!intact)
        {
            throw new InvalidOperationException(
                $"DocumentDB resource '{resource.Name}' finished building a container command " +
                $"line that does not start with the " +
                $"'{GatewayConfigurationShellCommandOption} <script> " +
                $"{GatewayConfigurationShellArgumentZero}' prefix " +
                $"WithOpenTelemetryMetrics() installs; {args.Count} argument(s) were built. " +
                $"'{GatewayConfigurationShell}' reads its command from those first arguments, so " +
                $"anything placed in front of them, a cleared or reordered list, or a replaced " +
                $"script or '{GatewayConfigurationShellArgumentZero}' delimiter leaves a " +
                $"container that does not start DocumentDB. Recovery: add container arguments " +
                $"with WithArgs(...), which appends them after the wrapper, rather than by " +
                $"rewriting the argument list in place.");
        }

        for (var index = 3; index < args.Count; index++)
        {
            if (args[index] is string duplicate &&
                string.Equals(duplicate, script, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"DocumentDB resource '{resource.Name}' finished building a container " +
                    $"command line carrying the WithOpenTelemetryMetrics() wrapper script more " +
                    $"than once. Only the first copy is the command " +
                    $"'{GatewayConfigurationShell}' runs; the rest are passed to DocumentDB as " +
                    $"arguments. Recovery: configure metrics on this resource through " +
                    $"WithOpenTelemetryMetrics() alone, and do not copy its arguments.");
            }
        }
    }

    /// <summary>
    /// Classifies whether <paramref name="resource"/> needs the gateway configuration wrapper.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The affected range is open-ended from
    /// <see cref="FirstGatewayTelemetryConfigurationVersion"/>: the JSON-over-environment
    /// precedence arrived in that release, it is the newest published DocumentDB version, and
    /// nothing upstream retracts it. Tags outside the strict <c>pg{NN}-X.Y.Z</c> grammar and
    /// images other than the official one are exempt, so forks and custom images keep the stock
    /// behaviour. So is any resource built from the caller's own Dockerfile, whose image
    /// annotation describes the build at best and may name the official image exactly. Private
    /// mirrors of the official image are not exempt, because only the registry differs.
    /// </para>
    /// <para>
    /// A digest pin on the official image makes the version opaque - the runtime resolves the
    /// image from the digest and ignores the tag, so a tag left over from an earlier call says
    /// nothing about what will actually run. Guessing in either direction is silently wrong, so
    /// this throws instead.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The official image is pinned by digest while the metrics API is configured.
    /// </exception>
    private static GatewayConfigurationRequirement ResolveOpenTelemetryGatewayConfigurationRequirement(
        DocumentDBServerResource resource)
    {
        var configuration = resource.Annotations
            .OfType<OpenTelemetryGatewayConfigurationAnnotation>()
            .SingleOrDefault();

        if (configuration is null)
        {
            return GatewayConfigurationRequirement.NotConfigured;
        }

        var image = ResolveEffectiveImage(resource);

        // A caller-owned Dockerfile build lands here with the custom images: it is not the
        // official image however its annotations read, and every part of the wrapper - the
        // entrypoint script path, the packaged configuration layout, bash and jq - is a property
        // of the official image that this package has not established for a build it did not
        // produce. Stated as the complement so that every origin naming the curated repository -
        // including a digest pin, which has to reach the rejection below rather than be skipped -
        // falls through.
        if (image.Origin is DocumentDBImageOrigin.None
            or DocumentDBImageOrigin.DockerfileBuild
            or DocumentDBImageOrigin.CustomRepository)
        {
            return NotRequired(configuration, resource, image);
        }

        if (!string.IsNullOrEmpty(image.Digest))
        {
            throw new InvalidOperationException(
                $"DocumentDB resource '{resource.Name}' pins " +
                $"{DocumentDBContainerImageTags.Image} by digest '{image.Digest}', so its " +
                $"DocumentDB version cannot be determined and the tag " +
                $"'{image.Tag ?? "<none>"}' is not what the runtime resolves. " +
                $"WithOpenTelemetryMetrics() needs the version because DocumentDB " +
                $"v{FirstGatewayTelemetryConfigurationVersion} and later give " +
                $"SetupConfiguration.json precedence over the OTEL_* environment variables, and " +
                $"applying or skipping the compatibility wrapper on a guess is silently wrong " +
                $"either way. Recovery: select the image by tag instead of by digest, or drop " +
                $"WithOpenTelemetryMetrics() and configure telemetry inside the image the digest " +
                $"names.");
        }

        if (image.KnownVersion is not { } version)
        {
            return NotRequired(configuration, resource, image);
        }

        return version >= FirstGatewayTelemetryConfigurationVersion
            ? GatewayConfigurationRequirement.Required
            : NotRequired(configuration, resource, image);

        // The wrapper cannot be uninstalled once the entrypoint carries it: an image swapped in
        // after installation would leave /bin/bash with no arguments, which starts nothing.
        static GatewayConfigurationRequirement NotRequired(
            OpenTelemetryGatewayConfigurationAnnotation configuration,
            DocumentDBServerResource resource,
            DocumentDBEffectiveImage image)
        {
            if (configuration.EntrypointOwned)
            {
                var description = image.Origin == DocumentDBImageOrigin.DockerfileBuild
                    ? $"a Dockerfile build (annotated '{image.Tag ?? "<none>"}')"
                    : $"an image ('{image.Tag ?? "<none>"}')";

                throw new InvalidOperationException(
                    $"DocumentDB resource '{resource.Name}' changed to {description} that does " +
                    $"not need the WithOpenTelemetryMetrics() compatibility wrapper after that " +
                    $"wrapper had already taken over the container entrypoint. Select the image " +
                    $"before configuring metrics.");
            }

            return GatewayConfigurationRequirement.NotApplicable;
        }
    }


    /// <summary>
    /// Configures the PostgreSQL owner role used by the DocumentDB Local container.
    /// </summary>
    /// <remarks>
    /// The bundled PostgreSQL instance creates the default <c>documentdb</c> role. A custom value
    /// must name a role that already exists, such as the owner of an externally managed
    /// PostgreSQL instance. DocumentDB <c>0.116.0</c> aborts explicitly while creating the
    /// DocumentDB admin user when the configured role does not exist. Earlier images also fail
    /// startup, but only later while waiting for the gateway to start.
    /// </remarks>
    /// <param name="builder">The resource builder for DocumentDB.</param>
    /// <param name="owner">The existing PostgreSQL role used for DocumentDB database operations.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<DocumentDBServerResource> WithOwner(this IResourceBuilder<DocumentDBServerResource> builder, string owner)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(owner);

        return builder.WithEnvironment(context =>
        {
            context.EnvironmentVariables[OwnerEnvVarName] = owner;
        });
    }

    /// <summary>
    /// Enables TLS for the DocumentDB connection string. TLS is enabled by default
    /// because the DocumentDB Local container serves TLS on its gateway port using a
    /// self-signed certificate.
    /// Call <c>UseTls(false)</c> to disable TLS if connecting to a non-TLS endpoint.
    /// </summary>
    /// <remarks>
    /// From DocumentDB <c>0.114.0</c> the container's default <c>TLS_MODE=allowTLS</c> accepts
    /// both plain and TLS connections, so <c>UseTls(false)</c> works against the default image.
    /// Container images up to and including <c>0.113.0</c> rejected plain connections regardless
    /// of that setting. Set <c>.WithEnvironment("TLS_MODE", "requireTLS")</c> to make the
    /// container reject plain connections; combining that with <c>UseTls(false)</c> is
    /// self-contradictory and connections will fail.
    /// </remarks>
    /// <param name="builder">The resource builder for DocumentDB.</param>
    /// <param name="useTls">Whether to enable TLS. Defaults to <see langword="true"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <example>
    /// <code>
    /// // Disable TLS for a non-TLS endpoint:
    /// var server = builder.AddDocumentDB("documentdb")
    ///                     .UseTls(false);
    /// </code>
    /// </example>
    public static IResourceBuilder<DocumentDBServerResource> UseTls(this IResourceBuilder<DocumentDBServerResource> builder, bool useTls = true)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.SetUseTls(useTls);
        return builder;
    }

    /// <summary>
    /// Allows insecure TLS connections by adding <c>tlsInsecure=true</c> to the connection string.
    /// This is enabled by default so the .NET MongoDB driver can connect to the self-signed
    /// certificate used by the DocumentDB Local container.
    /// Call <c>AllowInsecureTls(false)</c> to require valid certificates.
    /// </summary>
    /// <remarks>
    /// The extension uses <c>tlsInsecure=true</c> rather than <c>tlsAllowInvalidCertificates=true</c>
    /// because the .NET MongoDB driver does not fully honor <c>tlsAllowInvalidCertificates</c> for
    /// self-signed certificates and raises <c>UntrustedRoot</c> errors.
    /// </remarks>
    /// <param name="builder">The resource builder for DocumentDB.</param>
    /// <param name="allowInsecureTls">Whether to allow insecure TLS. Defaults to <see langword="true"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <example>
    /// <code>
    /// // Require valid certificates (for example, production with real certs):
    /// var server = builder.AddDocumentDB("documentdb")
    ///                     .AllowInsecureTls(false);
    /// </code>
    /// </example>
    public static IResourceBuilder<DocumentDBServerResource> AllowInsecureTls(this IResourceBuilder<DocumentDBServerResource> builder, bool allowInsecureTls = true)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.SetAllowInsecureTls(allowInsecureTls);
        return builder;
    }

    /// <summary>
    /// Pins the DocumentDB version to a specific release known to this build of the package.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The selected version is combined with the currently selected
    /// <see cref="DocumentDBPostgresVersion"/> (default <see cref="DocumentDBPostgresVersion.Pg17"/>)
    /// to produce the container image tag <c>pgN-X.Y.Z</c>.
    /// </para>
    /// <para>
    /// <b>Precedence:</b> for the image tag, the most recent of <see cref="WithDocumentDBVersion"/>,
    /// <see cref="WithPostgresVersion"/>,
    /// <see cref="ContainerResourceBuilderExtensions.WithImage{T}(IResourceBuilder{T}, string, string?)"/>,
    /// and <see cref="ContainerResourceBuilderExtensions.WithImageTag{T}(IResourceBuilder{T}, string)"/>
    /// wins. They all converge on the same single <see cref="ContainerImageAnnotation"/>.
    /// </para>
    /// <para>
    /// This method updates only the image tag. A custom image name or registry configured with
    /// <see cref="ContainerResourceBuilderExtensions.WithImage{T}(IResourceBuilder{T}, string, string?)"/>
    /// or <see cref="ContainerResourceBuilderExtensions.WithImageRegistry{T}(IResourceBuilder{T}, string)"/>
    /// is preserved.
    /// </para>
    /// <para>
    /// To pin to a version not in <see cref="DocumentDBVersion"/> (for example, a brand-new
    /// upstream release this package has not yet been updated to know about), use
    /// <see cref="ContainerResourceBuilderExtensions.WithImageTag{T}(IResourceBuilder{T}, string)"/>
    /// directly with a tag like <c>"pg17-0.999.0"</c>.
    /// </para>
    /// </remarks>
    /// <param name="builder">The resource builder for DocumentDB.</param>
    /// <param name="version">The DocumentDB version to use.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <example>
    /// <code>
    /// var server = builder.AddDocumentDB("documentdb")
    ///                     .WithDocumentDBVersion(DocumentDBVersion.V0_110_0);
    /// </code>
    /// </example>
    public static IResourceBuilder<DocumentDBServerResource> WithDocumentDBVersion(
        this IResourceBuilder<DocumentDBServerResource> builder,
        DocumentDBVersion version)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.SetVersion(version);
        return builder.WithImageTag(builder.Resource.ComputeImageTag());
    }

    /// <summary>
    /// Selects the PostgreSQL backend variant of the <c>documentdb-local</c> container image.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The selected variant is combined with the currently selected
    /// <see cref="DocumentDBVersion"/> (or <see cref="DocumentDBVersions.Latest"/> by default)
    /// to produce the container image tag <c>pgN-X.Y.Z</c>.
    /// </para>
    /// <para>
    /// <b>Precedence:</b> see <see cref="WithDocumentDBVersion"/> — last call wins.
    /// </para>
    /// </remarks>
    /// <param name="builder">The resource builder for DocumentDB.</param>
    /// <param name="pgVersion">The PostgreSQL backend variant to use.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="pgVersion"/> is not a defined member of
    /// <see cref="DocumentDBPostgresVersion"/>. Use a free-form
    /// <see cref="ContainerResourceBuilderExtensions.WithImageTag{T}(IResourceBuilder{T}, string)"/>
    /// call to target an unsupported PG variant.
    /// </exception>
    /// <example>
    /// <code>
    /// var server = builder.AddDocumentDB("documentdb")
    ///                     .WithPostgresVersion(DocumentDBPostgresVersion.Pg16)
    ///                     .WithDocumentDBVersion(DocumentDBVersion.V0_110_0);
    /// // -&gt; image tag "pg16-0.110.0"
    /// </code>
    /// </example>
    public static IResourceBuilder<DocumentDBServerResource> WithPostgresVersion(
        this IResourceBuilder<DocumentDBServerResource> builder,
        DocumentDBPostgresVersion pgVersion)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (!Enum.IsDefined(pgVersion))
        {
            throw new ArgumentOutOfRangeException(
                nameof(pgVersion),
                pgVersion,
                $"Unsupported PostgreSQL backend variant '{pgVersion}'. " +
                $"Use one of {nameof(DocumentDBPostgresVersion.Pg15)}, " +
                $"{nameof(DocumentDBPostgresVersion.Pg16)}, " +
                $"{nameof(DocumentDBPostgresVersion.Pg17)}, or " +
                $"{nameof(DocumentDBPostgresVersion.Pg18)}, or fall back to a free-form " +
                $"WithImageTag(...) for unsupported variants.");
        }

        builder.Resource.SetPgVersion(pgVersion);
        return builder.WithImageTag(builder.Resource.ComputeImageTag());
    }

    private static string GetMountedFilePath(string source, string paramName, string prefix)
    {
        var fileName = Path.GetFileName(source);

        if (string.IsNullOrEmpty(fileName))
        {
            throw new ArgumentException("The path must include a file name.", paramName);
        }

        return $"/{prefix}{fileName}";
    }
}
