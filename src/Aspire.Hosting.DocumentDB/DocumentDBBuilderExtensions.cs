// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.DocumentDB;
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
    private const string GatewayConfigurationShellArgumentZero = "--";

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
        "o=\"$(mktemp -d)\"; " +
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
    /// Same carve-outs as <see cref="SubscribeMinimumPostgresImageGuard"/>: custom images and
    /// tags outside the strict <c>pg{NN}-X.Y.Z</c> grammar are exempt, and the guard is run-mode
    /// only, so manifest generation is unaffected. Unlike that guard this one is always
    /// subscribed, so the exempt paths stay silent rather than warning on every app that pins a
    /// custom image.
    /// </para>
    /// </remarks>
    private static IResourceBuilder<DocumentDBServerResource> SubscribeMinimumPgVariantImageGuard(
        this IResourceBuilder<DocumentDBServerResource> builder)
    {
        builder.ApplicationBuilder.Eventing.Subscribe<BeforeResourceStartedEvent>(
            builder.Resource,
            (evt, ct) =>
            {
                var imageAnnotation = evt.Resource.Annotations.OfType<ContainerImageAnnotation>().LastOrDefault();
                if (imageAnnotation is null)
                {
                    // Defensive: AddDocumentDB sets ContainerImageAnnotation eagerly via WithImage.
                    return Task.CompletedTask;
                }

                // A fork publishing its own images decides its own variant matrix.
                if (!string.Equals(imageAnnotation.Image, DocumentDBContainerImageTags.Image, StringComparison.Ordinal))
                {
                    return Task.CompletedTask;
                }

                if (!DocumentDBContainerImageTags.TryParseDocumentDBTag(imageAnnotation.Tag, out var pg, out var docVersion))
                {
                    return Task.CompletedTask;
                }

                if (!DocumentDBContainerImageTags.MinimumVersionByPgVariant.TryGetValue(pg, out var minimum) ||
                    docVersion >= minimum)
                {
                    return Task.CompletedTask;
                }

                throw new InvalidOperationException(
                    $"DocumentDB resource '{evt.Resource.Name}' resolves to image tag " +
                    $"'{imageAnnotation.Tag}', but upstream only publishes pg{pg} images from " +
                    $"DocumentDB v{minimum} onwards. That tag does not exist on " +
                    $"{DocumentDBContainerImageTags.Registry}/{DocumentDBContainerImageTags.Image}, " +
                    $"so starting the resource would fail with an opaque manifest-not-found error. " +
                    $"Recovery: pair '.WithPostgresVersion(DocumentDBPostgresVersion.Pg{pg})' with " +
                    $"DocumentDB v{minimum} or newer, or choose a PostgreSQL variant that exists " +
                    $"for v{docVersion}.");
            });

        return builder;
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
    /// The guard is run-mode only. <see cref="BeforeResourceStartedEvent"/> is not published
    /// during manifest generation, so <c>azd publish</c> / <c>--publisher manifest</c> flows
    /// are unaffected — that is intentional, because no container is started in those modes.
    /// </para>
    /// <para>
    /// Custom images (anything whose <see cref="ContainerImageAnnotation.Image"/> is not
    /// the curated <see cref="DocumentDBContainerImageTags.Image"/>) are exempt with a
    /// single warning. Tags that do not match the strict <c>pg{NN}-X.Y.Z</c> pattern
    /// (e.g., <c>nightly</c>, <c>pg17-0.112.0-rc.1</c>) are also exempt with a single
    /// warning, so callers pinning custom builds or pre-releases are not surprised by an
    /// unactionable hard failure.
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
                var imageAnnotation = evt.Resource.Annotations.OfType<ContainerImageAnnotation>().LastOrDefault();
                if (imageAnnotation is null)
                {
                    // Defensive: AddDocumentDB sets ContainerImageAnnotation eagerly via WithImage.
                    return Task.CompletedTask;
                }

                var logger = TryGetResourceLogger(evt, PostgresEndpointLoggerCategory);

                // Custom-image carve-out: only enforce the floor on the curated
                // documentdb-local image. A fork using a different image name
                // (regardless of registry) is assumed to know what it is doing.
                if (!string.Equals(imageAnnotation.Image, DocumentDBContainerImageTags.Image, StringComparison.Ordinal))
                {
                    if (Interlocked.CompareExchange(ref warningLogged, 1, 0) == 0)
                    {
                        logger?.LogWarning(
                            "DocumentDB resource '{ResourceName}' uses custom image '{Image}:{Tag}'. " +
                            "The v{MinVersion} minimum required by WithPostgresEndpoint() for credential parity " +
                            "is NOT enforced on custom images.",
                            evt.Resource.Name,
                            imageAnnotation.Image,
                            imageAnnotation.Tag,
                            DocumentDBContainerImageTags.MinimumPostgresEndpointVersion);
                    }
                    return Task.CompletedTask;
                }

                if (!DocumentDBContainerImageTags.TryParseDocumentDBTag(imageAnnotation.Tag, out _, out var docVersion))
                {
                    if (Interlocked.CompareExchange(ref warningLogged, 1, 0) == 0)
                    {
                        logger?.LogWarning(
                            "DocumentDB resource '{ResourceName}' uses image tag '{Tag}', which does not match " +
                            "the curated 'pg{{NN}}-X.Y.Z' pattern. The v{MinVersion} minimum required by " +
                            "WithPostgresEndpoint() for credential parity is NOT enforced on unrecognised tags.",
                            evt.Resource.Name,
                            imageAnnotation.Tag,
                            DocumentDBContainerImageTags.MinimumPostgresEndpointVersion);
                    }
                    return Task.CompletedTask;
                }

                if (docVersion < DocumentDBContainerImageTags.MinimumPostgresEndpointVersion)
                {
                    throw new InvalidOperationException(
                        $"DocumentDB resource '{evt.Resource.Name}' is configured with image tag " +
                        $"'{imageAnnotation.Tag}', but WithPostgresEndpoint() requires DocumentDB " +
                        $"v{DocumentDBContainerImageTags.MinimumPostgresEndpointVersion} or later. " +
                        $"Earlier images hard-code the PostgreSQL admin credentials to " +
                        $"'docdb_admin'/'Admin100', so the Aspire-generated postgresql:// connection " +
                        $"string would silently fail to authenticate. Recovery: chain " +
                        $"'.WithImageTag(\"pg{{NN}}-{DocumentDBContainerImageTags.MinimumPostgresEndpointVersion}\")' " +
                        $"(or newer) after AddDocumentDB(...). See " +
                        $"https://github.com/microsoft/azure-databases-aspire/issues/71.");
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
    /// <exception cref="ArgumentException"><paramref name="isReadOnly"/> is <see langword="true"/>, or <paramref name="targetPath"/> is not an absolute container path below the root — including one that only resolves to the root, such as <c>/data/..</c>.</exception>
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

        return TryCanonicalizeContainerPath(targetPath, out var canonical) switch
        {
            ContainerPathProblem.None => canonical,

            ContainerPathProblem.NotAbsolute => throw new ArgumentException(
                $"The DocumentDB data target path '{targetPath}' must be an absolute path inside the " +
                $"container (starting with '/'), because it is used both as the mount target and as the " +
                $"container's DATA_PATH. Omit the argument to use the container default " +
                $"'{DefaultMountedDataPath}'.",
                parameterName),

            ContainerPathProblem.EscapesRoot => throw new ArgumentException(
                $"The DocumentDB data target path '{targetPath}' escapes above the container root: the " +
                $"container runtime resolves '..' segments before mounting, and there is nothing above " +
                $"'/'. Omit the argument to use the container default '{DefaultMountedDataPath}'.",
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
    /// Why an absolute container path could not be canonicalized into a usable mount target.
    /// </summary>
    private enum ContainerPathProblem
    {
        /// <summary>The path canonicalized successfully.</summary>
        None,

        /// <summary>The path is empty or does not start with '/'.</summary>
        NotAbsolute,

        /// <summary>The path's '..' segments reach above the container root.</summary>
        EscapesRoot,

        /// <summary>The path canonicalizes to the container root '/'.</summary>
        IsRoot,
    }

    /// <summary>
    /// Canonicalizes an absolute Linux container path the way the container runtime resolves one
    /// before mounting: repeated separators collapse, <c>.</c> segments drop out, and <c>..</c>
    /// segments remove the preceding one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every storage comparison runs on canonical paths, because Docker compares the resolved
    /// path and not the string the caller wrote: <c>/data</c>, <c>//data/</c> and
    /// <c>/foo/../data</c> are one and the same mount target, and comparing them as written would
    /// let an alias slip past the duplicate, read-only and shared-storage rules.
    /// </para>
    /// <para>
    /// A path whose <c>..</c> segments reach above the root and a path that collapses to the root
    /// are both rejected rather than clamped: neither can be a DocumentDB data directory, and
    /// silently treating either as <c>/</c> would put the guard's model at odds with what the
    /// runtime does.
    /// </para>
    /// <para>
    /// Only <c>/</c> separates segments. A backslash is an ordinary character in a Linux file
    /// name, so a Windows-style path is not absolute here and is reported as such.
    /// </para>
    /// </remarks>
    private static ContainerPathProblem TryCanonicalizeContainerPath(string? path, out string canonical)
    {
        canonical = string.Empty;

        if (string.IsNullOrEmpty(path) || path[0] != '/')
        {
            return ContainerPathProblem.NotAbsolute;
        }

        var segments = new List<string>();

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
                    return ContainerPathProblem.EscapesRoot;
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
        return ContainerPathProblem.None;
    }

    /// <summary>
    /// Whether a mount lands on <paramref name="canonicalPath"/> once the container runtime has
    /// resolved the raw target. A target that cannot be canonicalized — relative, or collapsing to
    /// the root — cannot be a DocumentDB data directory, so it matches nothing.
    /// </summary>
    private static bool TargetsContainerPath(ContainerMountAnnotation mount, string canonicalPath) =>
        TryCanonicalizeContainerPath(mount.Target, out var canonical) == ContainerPathProblem.None &&
        string.Equals(canonical, canonicalPath, StringComparison.Ordinal);

    // Host paths are compared case-sensitively only where the platform's filesystem is,
    // so a bind mount reused as "C:\Data" and "c:\data" is still recognised as shared.
    private static readonly StringComparison HostPathComparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>
    /// Subscribes a <see cref="BeforeResourceStartedEvent"/> handler that rejects data-directory
    /// mounts the DocumentDB Local container cannot use, before the container is started and fails
    /// with a misleading message.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Covers three unsupported shapes, including mounts added with the raw Aspire APIs
    /// (<c>WithVolume</c> / <c>WithBindMount</c>) rather than <see cref="WithDataVolume"/> or
    /// <see cref="WithDataBindMount"/>:
    /// </para>
    /// <list type="bullet">
    /// <item><description>a read-only mount on the data path — PostgreSQL cannot initialise or
    /// write there, and the container burns 60 seconds before reporting an unrelated-looking
    /// timeout;</description></item>
    /// <item><description>two mounts on the same data path — the container runtime rejects
    /// duplicate mount targets;</description></item>
    /// <item><description>a data directory shared with another DocumentDB resource's data
    /// directory — two PostgreSQL instances on one data directory corrupt it.</description></item>
    /// </list>
    /// <para>
    /// The shared-directory rule is version-sensitive. From
    /// <see cref="DocumentDBContainerImageTags.MinimumDeclaredDataVolumeVersion"/> the entrypoint
    /// claims the directory with an exclusive <c>flock</c>, so the container that starts second
    /// refuses to start; when one of the two resources is started by hand
    /// (<see cref="ResourceBuilderExtensions.WithExplicitStart{T}"/>) the pair may never overlap
    /// and the combination is reported as a warning. Older, unrecognised, and custom images have
    /// no such interlock — simultaneous access is not refused, it silently corrupts — so there the
    /// combination stays a hard failure regardless of explicit start.
    /// </para>
    /// <para>
    /// It also warns once when the data mount does not cover <c>/data</c> on an image known to
    /// declare that path as a container <c>VOLUME</c>: neither Docker nor Aspire can un-declare an
    /// image volume, so the only way to suppress the anonymous volume the runtime would otherwise
    /// create is to mount the caller's storage on that exact path. Images that declare no volume
    /// (at or below <c>0.114.0</c>), unrecognised tags, and custom images produce no such warning,
    /// because for them an unmounted <c>/data</c> is just a directory in the container layer.
    /// </para>
    /// <para>
    /// The data directory the rules are applied to is the one the container will really use: the
    /// effective <c>DATA_PATH</c> the environment pipeline produces at start, canonicalized the
    /// way the container runtime resolves a path. A raw
    /// <c>WithEnvironment("DATA_PATH", ...)</c> therefore participates with the documented "last
    /// call wins" precedence, and an alias such as <c>/foo/../data</c> cannot slip past a rule by
    /// spelling <c>/data</c> differently.
    /// </para>
    /// <para>
    /// Like the image guards, this is run-mode only: <see cref="BeforeResourceStartedEvent"/> is
    /// not published during manifest generation, where no container is started.
    /// </para>
    /// </remarks>
    private static IResourceBuilder<DocumentDBServerResource> SubscribeDataStorageGuard(
        this IResourceBuilder<DocumentDBServerResource> builder)
    {
        // One-shot so restart attempts don't repeat advisory warnings. Hard failures are
        // deterministic and intentionally re-thrown on every start attempt.
        var sharedStorageWarningLogged = 0;
        var declaredVolumeWarningLogged = 0;

        // Captured from the builder rather than resolved from the event's services: the execution
        // context is what the environment callbacks branch on, and it is the same instance Aspire
        // hands them when it builds the container.
        var executionContext = builder.ApplicationBuilder.ExecutionContext;

        builder.ApplicationBuilder.Eventing.Subscribe<BeforeResourceStartedEvent>(
            builder.Resource,
            async (evt, ct) =>
            {
                var resource = evt.Resource;
                var dataPath = await ResolveEffectiveDataPathAsync(resource, executionContext, ct).ConfigureAwait(false);

                var mounts = resource.Annotations.OfType<ContainerMountAnnotation>().ToList();
                var dataMounts = mounts
                    .Where(mount => TargetsContainerPath(mount, dataPath))
                    .ToList();

                if (dataMounts.FirstOrDefault(mount => mount.IsReadOnly) is { } readOnlyMount)
                {
                    throw new InvalidOperationException(
                        $"DocumentDB resource '{resource.Name}' mounts its data directory " +
                        $"('{dataPath}') read-only. DocumentDB requires a writable data directory: " +
                        $"the container entrypoint takes ownership of it and PostgreSQL initialises " +
                        $"and writes WAL there. The container would run for about a minute and then " +
                        $"fail with the misleading banner 'PostgreSQL failed to start within 60 " +
                        $"seconds', hiding the real cause ('initdb: error: could not change " +
                        $"permissions of directory \"{dataPath}\": Read-only file system'). " +
                        $"Recovery: mount " +
                        $"{(readOnlyMount.Type == ContainerMountType.BindMount ? $"'{readOnlyMount.Source}'" : $"volume '{readOnlyMount.Source}'")} " +
                        $"writable, or use WithDataVolume()/WithDataBindMount(...), which reject " +
                        $"read-only data storage up front.");
                }

                if (dataMounts.Count > 1)
                {
                    throw new InvalidOperationException(
                        $"DocumentDB resource '{resource.Name}' has {dataMounts.Count} mounts on its " +
                        $"data directory ('{dataPath}'). The container runtime rejects duplicate mount " +
                        $"targets, so the container would fail to be created. Recovery: configure the " +
                        $"data directory once — call either WithDataVolume(...) or " +
                        $"WithDataBindMount(...), not both, and do not add a second volume or bind " +
                        $"mount on the same path.");
                }

                var dataMount = dataMounts.Count == 1 ? dataMounts[0] : null;
                var model = evt.Services.GetService<DistributedApplicationModel>();

                if (dataMount is not null && dataMount.Source is not null && model is not null)
                {
                    // Every conflicting peer is inspected before anything is reported: a peer that
                    // only warrants a warning must not mask a later peer that warrants a failure.
                    var thisInterlocked = ResolvesToDataVolumeAwareImage(resource);
                    List<string>? warnings = null;

                    foreach (var other in model.Resources)
                    {
                        // Only another DocumentDB server contends for the same data directory. A
                        // different container mounting the same storage (a backup or inspection
                        // sidecar, say) is a different question this guard cannot answer.
                        if (ReferenceEquals(other, resource) || other is not DocumentDBServerResource otherServer)
                        {
                            continue;
                        }

                        // Compare data directory against data directory. The peer reusing this
                        // volume or host directory as read-only *input* (seed scripts, TLS
                        // material) is not a second cluster on the same files.
                        if (!other.TryGetAnnotationsOfType<ContainerMountAnnotation>(out var otherMounts))
                        {
                            continue;
                        }

                        // Sharing the storage is the cheap half of the test and the necessary
                        // condition, so it is applied first: a peer that mounts none of this
                        // resource's storage cannot contend for its data directory, and its
                        // environment is left alone.
                        var sharedMounts = otherMounts.Where(mount => IsSameStorage(dataMount, mount)).ToList();
                        if (sharedMounts.Count == 0)
                        {
                            continue;
                        }

                        var otherDataPath = await ResolveEffectiveDataPathAsync(otherServer, executionContext, ct).ConfigureAwait(false);
                        var conflict = sharedMounts.FirstOrDefault(mount => TargetsContainerPath(mount, otherDataPath));

                        if (conflict is null)
                        {
                            continue;
                        }

                        var description = dataMount.Type == ContainerMountType.BindMount
                            ? $"host directory '{dataMount.Source}'"
                            : $"volume '{dataMount.Source}'";

                        // The refusal is a feature of the image, not of Aspire: only a pair that
                        // both hold the lock can be trusted to fail loudly instead of corrupting
                        // the cluster, so only such a pair is eligible for the warning downgrade.
                        var bothInterlocked = thisInterlocked && ResolvesToDataVolumeAwareImage(otherServer);
                        var explicitlyStarted =
                            other.Annotations.OfType<ExplicitStartupAnnotation>().Any() ||
                            resource.Annotations.OfType<ExplicitStartupAnnotation>().Any();

                        if (bothInterlocked && explicitlyStarted)
                        {
                            (warnings ??= []).Add(SharedDataDirectoryMessage(
                                resource, other, description, interlocked: true, explicitStartNote: true));
                            continue;
                        }

                        throw new InvalidOperationException(SharedDataDirectoryMessage(
                            resource, other, description, bothInterlocked, explicitStartNote: false));
                    }

                    if (warnings is not null &&
                        Interlocked.CompareExchange(ref sharedStorageWarningLogged, 1, 0) == 0)
                    {
                        var logger = TryGetResourceLogger(evt, StorageLoggerCategory);
                        foreach (var warning in warnings)
                        {
                            logger?.LogWarning("{Message}", warning);
                        }
                    }
                }

                if (!string.Equals(dataPath, DefaultMountedDataPath, StringComparison.Ordinal) &&
                    ResolvesToDataVolumeAwareImage(resource) &&
                    !mounts.Any(mount => TargetsContainerPath(mount, DefaultMountedDataPath)) &&
                    Interlocked.CompareExchange(ref declaredVolumeWarningLogged, 1, 0) == 0)
                {
                    TryGetResourceLogger(evt, StorageLoggerCategory)?.LogWarning(
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
            });

        return builder;
    }

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
              ", an unrecognised tag, or a custom image), so nothing refuses the second start: two " +
              "PostgreSQL instances would open the same data directory and corrupt it silently.";

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

    /// <summary>
    /// The container path DocumentDB will really write to on this run: the effective
    /// <c>DATA_PATH</c> the resource's environment pipeline produces, canonicalized the way the
    /// container runtime resolves a path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>DATA_PATH</c> is an ordinary environment variable, so the value that reaches the
    /// container is whatever the environment callbacks leave behind — the storage helpers'
    /// writes, a raw <c>WithEnvironment("DATA_PATH", ...)</c>, or a callback that computes one —
    /// with the last writer winning. Running that pipeline here is what makes the guard validate
    /// the directory the container actually uses instead of the one the helpers intended.
    /// </para>
    /// <para>
    /// Only the <c>DATA_PATH</c> entry is resolved. Every other value the callbacks produced —
    /// the password parameter among them — is left as the unresolved object it was gathered as,
    /// so answering a question about a filesystem path never materialises a secret.
    /// </para>
    /// <para>
    /// A <c>DATA_PATH</c> that resolves to <see langword="null"/> is dropped by Aspire rather than
    /// passed to the container, which leaves the image's own default in place.
    /// </para>
    /// </remarks>
    private static async ValueTask<string> ResolveEffectiveDataPathAsync(
        IResource resource,
        DistributedApplicationExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        if (!resource.TryGetEnvironmentVariables(out var callbacks))
        {
            return DefaultMountedDataPath;
        }

        var environment = new Dictionary<string, object>(StringComparer.Ordinal);
        var context = new EnvironmentCallbackContext(executionContext, resource, environment, cancellationToken);

        foreach (var callback in callbacks)
        {
            await callback.Callback(context).ConfigureAwait(false);
        }

        if (!environment.TryGetValue(DataPathEnvVarName, out var value) || value is null)
        {
            return DefaultMountedDataPath;
        }

        string? dataPath;
        if (value is string literal)
        {
            dataPath = literal;
        }
        else if (value is IValueProvider provider)
        {
            dataPath = await provider.GetValueAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // Matches how Aspire renders a value that is neither a string nor a value provider.
            dataPath = value.ToString();
        }

        if (dataPath is null)
        {
            return DefaultMountedDataPath;
        }

        return TryCanonicalizeContainerPath(dataPath, out var canonical) switch
        {
            ContainerPathProblem.None => canonical,

            ContainerPathProblem.NotAbsolute => throw new InvalidOperationException(
                InvalidDataPathMessage(
                    resource,
                    dataPath,
                    "is not an absolute path inside the container (it does not start with '/')")),

            ContainerPathProblem.EscapesRoot => throw new InvalidOperationException(
                InvalidDataPathMessage(
                    resource,
                    dataPath,
                    "escapes above the container root: the runtime resolves '..' segments before " +
                    "mounting, and there is nothing above '/'")),

            _ => throw new InvalidOperationException(
                InvalidDataPathMessage(
                    resource,
                    dataPath,
                    "resolves to the container root '/': the runtime collapses '.' and '..' " +
                    "segments before mounting, so an alias such as '/data/..' is the root itself, " +
                    "which cannot hold a PostgreSQL cluster")),
        };
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
    /// exclusive lock. Custom images and unrecognised tags resolve to <see langword="false"/>:
    /// the package cannot know what they do, so it neither promises the interlock nor warns
    /// about an image volume that may not exist.
    /// </summary>
    private static bool ResolvesToDataVolumeAwareImage(IResource resource)
    {
        var image = resource.Annotations.OfType<ContainerImageAnnotation>().LastOrDefault();

        return image is not null &&
            string.Equals(image.Image, DocumentDBContainerImageTags.Image, StringComparison.Ordinal) &&
            DocumentDBContainerImageTags.TryParseDocumentDBTag(image.Tag, out _, out var version) &&
            version >= DocumentDBContainerImageTags.MinimumDeclaredDataVolumeVersion;
    }

    private static bool IsSameStorage(ContainerMountAnnotation left, ContainerMountAnnotation right)
    {
        if (left.Type != right.Type || left.Source is null || right.Source is null)
        {
            return false;
        }

        return left.Type == ContainerMountType.BindMount
            ? string.Equals(left.Source.TrimEnd('/', '\\'), right.Source.TrimEnd('/', '\\'), HostPathComparison)
            : string.Equals(left.Source, right.Source, StringComparison.Ordinal);
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
    /// mode all execute the same thing. Custom images and tags outside the <c>pg{NN}-X.Y.Z</c>
    /// grammar are left completely untouched; private mirrors of the official image are not,
    /// because only the registry differs. Pinning the official image by digest throws, because the
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
    /// serializes the resource, and the arguments from a command-line-arguments callback. That
    /// callback re-checks that the wrapper still owns the entrypoint, because it runs after every
    /// event subscriber and is therefore the last chance to catch one that replaced the entrypoint
    /// later in the same startup.
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

        builder.WithArgs(context =>
        {
            if (ResolveOpenTelemetryGatewayConfigurationRequirement(builder.Resource) !=
                GatewayConfigurationRequirement.Required)
            {
                return;
            }

            // The arguments are only meaningful to the entrypoint this wrapper installs. Resolving
            // them happens after every BeforeStartEvent subscriber has run, so this is the last
            // point at which a subscriber or lifecycle hook that replaced the entrypoint later in
            // the same startup can still be caught - after which these arguments would be spliced
            // into someone else's command line.
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

            context.Args.Insert(0, GatewayConfigurationShellArgumentZero);
            context.Args.Insert(0, BuildOpenTelemetryGatewayConfigurationScript(configuration));
            context.Args.Insert(0, "-c");
        });
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
    /// behaviour. Private mirrors of the official image are not exempt, because only the registry
    /// differs.
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

        var image = resource.Annotations.OfType<ContainerImageAnnotation>().LastOrDefault();
        if (image is null ||
            !string.Equals(image.Image, DocumentDBContainerImageTags.Image, StringComparison.Ordinal))
        {
            return NotRequired(configuration, resource, image?.Tag);
        }

        if (!string.IsNullOrEmpty(image.SHA256))
        {
            throw new InvalidOperationException(
                $"DocumentDB resource '{resource.Name}' pins " +
                $"{DocumentDBContainerImageTags.Image} by digest '{image.SHA256}', so its " +
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

        if (!DocumentDBContainerImageTags.TryParseDocumentDBTag(image.Tag, out _, out var version))
        {
            return NotRequired(configuration, resource, image.Tag);
        }

        return version >= FirstGatewayTelemetryConfigurationVersion
            ? GatewayConfigurationRequirement.Required
            : NotRequired(configuration, resource, image.Tag);

        // The wrapper cannot be uninstalled once the entrypoint carries it: an image swapped in
        // after installation would leave /bin/bash with no arguments, which starts nothing.
        static GatewayConfigurationRequirement NotRequired(
            OpenTelemetryGatewayConfigurationAnnotation configuration,
            DocumentDBServerResource resource,
            string? tag)
        {
            if (configuration.EntrypointOwned)
            {
                throw new InvalidOperationException(
                    $"DocumentDB resource '{resource.Name}' changed to an image " +
                    $"('{tag ?? "<none>"}') that does not need the WithOpenTelemetryMetrics() " +
                    $"compatibility wrapper after that wrapper had already taken over the " +
                    $"container entrypoint. Select the image before configuring metrics.");
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
