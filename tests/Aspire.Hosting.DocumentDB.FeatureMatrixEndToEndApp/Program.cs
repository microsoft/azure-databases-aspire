// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECONTAINERSHELLEXECUTION001

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.DocumentDB.FeatureMatrixEndToEndApp;

/// <summary>
/// A scenario-driven AppHost covering the public API surface that had no container-backed
/// coverage: data persistence, credential parameters, multi-database topologies, the PostgreSQL
/// backend variants, the observable container-configuration knobs, and the negative TLS and
/// image-floor paths.
/// </summary>
/// <remarks>
/// One project with a scenario switch rather than a project per permutation: these differ only in
/// how the resource is configured, and a dozen near-identical AppHost projects would obscure that.
/// Scenario inputs that must be unique per run (volume name, bind-mount path, ports) are passed in
/// through environment variables so the test owns their lifetime.
/// </remarks>
public class Program
{
    public const string ScenarioEnvironmentVariable = "DOCUMENTDB_FEATURE_SCENARIO";
    public const string VolumeNameEnvironmentVariable = "DOCUMENTDB_FEATURE_VOLUME";
    public const string BindMountPathEnvironmentVariable = "DOCUMENTDB_FEATURE_BINDMOUNT";
    public const string PostgresPortEnvironmentVariable = "DOCUMENTDB_FEATURE_PG_PORT";
    public const string ImageTagEnvironmentVariable = "DOCUMENTDB_FEATURE_IMAGE_TAG";
    public const string InitDataPathEnvironmentVariable = "DOCUMENTDB_FEATURE_INIT_DATA";
    public const string OtelOutputPathEnvironmentVariable = "DOCUMENTDB_FEATURE_OTEL_OUTPUT";
    public const string OtelEndpointEnvironmentVariable = "DOCUMENTDB_FEATURE_OTEL_ENDPOINT";
    public const string OtelEnabledEnvironmentVariable = "DOCUMENTDB_FEATURE_OTEL_ENABLED";
    public const string DataPathArgumentEnvironmentVariable = "DOCUMENTDB_FEATURE_DATA_PATH_ARGUMENT";
    public const string ScratchBindMountPathEnvironmentVariable = "DOCUMENTDB_FEATURE_SCRATCH_BINDMOUNT";
    public const string ShellExecutionEnvironmentVariable = "DOCUMENTDB_FEATURE_SHELL_EXECUTION";
    public const string RuntimeOperandValueEnvironmentVariable = "DOCUMENTDB_FEATURE_RUNTIME_OPERAND";

    /// <summary>Custom credential parameters, two databases, one with a distinct database name.</summary>
    public const string CustomCredentialsMultiDbScenario = "custom-credentials-multi-db";

    /// <summary>WithDataVolume: data must survive the container being replaced.</summary>
    public const string DataVolumeScenario = "data-volume";

    /// <summary>WithDataBindMount: data must land on the host path and survive a restart.</summary>
    public const string DataBindMountScenario = "data-bind-mount";

    /// <summary>WithDataVolume + WithInitData: custom initialization is scoped to the volume.</summary>
    public const string InitDataVolumeScenario = "init-data-volume";

    /// <summary>WithoutUserCreation + WithoutSampleData: start without provisioning the admin user.</summary>
    public const string WithoutUserCreationScenario = "without-user-creation";

    /// <summary>WithLogLevel + WithOwner + WithOpenTelemetryMetrics, all observable on the container.</summary>
    public const string ObservableConfigScenario = "observable-config";

    /// <summary>Telemetry wrapper with DATA_PATH set to /tmp.</summary>
    public const string TelemetryTemporaryDataPathScenario = "telemetry-temporary-data-path";

    /// <summary>
    /// Telemetry wrapper with a caller argument callback registered afterwards that inserts at the
    /// front of the argument list — the shape that used to displace the wrapper's own command.
    /// </summary>
    public const string TelemetryWrapperArgumentOrderScenario = "telemetry-wrapper-argument-order";

    /// <summary>
    /// Telemetry wrapper with one host directory bind-mounted twice: as the data directory and at
    /// <c>/tmp</c>. The two container paths do not contain one another, so only the backing
    /// storage tells them apart.
    /// </summary>
    public const string TelemetryAliasedTemporaryRootScenario = "telemetry-aliased-temporary-root";

    /// <summary>Telemetry wrapper with a daemon-resolved symlink alias mounted at <c>/tmp</c>.</summary>
    public const string TelemetrySymlinkAliasedTemporaryRootScenario =
        "telemetry-symlink-aliased-temporary-root";

    /// <summary>A raw runtime mount that aliases the named DATA_PATH volume at <c>/tmp</c>.</summary>
    public const string TelemetryRawRuntimeVolumeScenario = "telemetry-raw-runtime-volume";

    /// <summary>Every telemetry scratch candidate is bind-backed and physically unprovable.</summary>
    public const string TelemetryUnprovableTemporaryRootsScenario =
        "telemetry-unprovable-temporary-roots";

    /// <summary>Telemetry wrapper with ShellExecution explicitly selected by the scenario input.</summary>
    public const string TelemetryShellExecutionScenario = "telemetry-shell-execution";

    /// <summary>ShellExecution is enabled after the telemetry command has already been cached.</summary>
    public const string TelemetryShellExecutionMutationScenario = "telemetry-shell-execution-mutation";

    /// <summary>A secret parameter is supplied as a bare container-runtime image operand.</summary>
    public const string TelemetrySecretRuntimeOperandScenario = "telemetry-secret-runtime-operand";

    /// <summary>A credential-bearing connection expression is supplied as a runtime image operand.</summary>
    public const string TelemetryCredentialRuntimeOperandScenario = "telemetry-credential-runtime-operand";

    /// <summary>WithLogLevel(Debug), exercised with normal MongoDB traffic by the test.</summary>
    public const string DebugLogLevelScenario = "debug-log-level";

    /// <summary>WithLogLevel(Quiet), exercised with normal MongoDB traffic by the test.</summary>
    public const string QuietLogLevelScenario = "quiet-log-level";

    /// <summary>WithPostgresVersion(Pg15).</summary>
    public const string Pg15Scenario = "pg15";

    /// <summary>WithPostgresVersion(Pg16).</summary>
    public const string Pg16Scenario = "pg16";

    /// <summary>Default PostgreSQL 17 backend with the explicit released image.</summary>
    public const string Pg17Scenario = "pg17";

    /// <summary>WithPostgresVersion(Pg18).</summary>
    public const string Pg18Scenario = "pg18";

    /// <summary>WithDocumentDBVersion pinned to an older curated version.</summary>
    public const string OlderVersionScenario = "older-version";

    /// <summary>WithoutExtendedRum + WithPostgresEndpoint(explicit port).</summary>
    public const string PostgresExtrasScenario = "postgres-extras";

    /// <summary>AllowInsecureTls(false) against the container's self-signed certificate.</summary>
    public const string StrictTlsScenario = "strict-tls";

    /// <summary>WithPostgresEndpoint on an image older than the 0.112.0 floor.</summary>
    public const string PostgresEndpointFloorScenario = "postgres-endpoint-floor";

    /// <summary>A username rejected by the v0.116 reserved-prefix validation.</summary>
    public const string ReservedUserNameScenario = "reserved-username";

    public const string CustomUserName = "aspireuser";
    public const string CustomPassword = "AspirePass123";
    public const string ReservedUserName = "pgadmin";

    public static async Task Main(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);
        var scenario = GetRequired(ScenarioEnvironmentVariable);

        if (scenario == CustomCredentialsMultiDbScenario)
        {
            // Parameter values come through configuration rather than an AddParameter(value)
            // overload, so this works regardless of which convenience overloads the pinned
            // Aspire version exposes.
            builder.Configuration["Parameters:docdbuser"] = CustomUserName;
            builder.Configuration["Parameters:docdbpass"] = CustomPassword;

            var userName = builder.AddParameter("docdbuser", secret: false);
            var password = builder.AddParameter("docdbpass", secret: true);

            var server = builder.AddDocumentDB("documentdb", port: null, userName: userName, password: password);

            // Resource name == database name.
            server.AddDatabase("primary");

            // Resource name != database name: exercises the databaseName parameter and the
            // DatabaseName property that carries it.
            server.AddDatabase("orders", "orders_db");

            await RunAsync(builder);
            return;
        }

        // The persistence scenarios must pin their credentials. The container hashes the
        // password into the PostgreSQL role on first initialisation, so it lives in the persisted
        // data directory; a second run with a freshly generated password cannot authenticate
        // against it and fails with an opaque "saslContinue failed: Invalid key". A real AppHost
        // normally avoids this because the generated parameter is persisted to user secrets, but
        // nothing about persistence should depend on that, so these scenarios are explicit.
        var pinnedCredentials = scenario is
            DataVolumeScenario or
            DataBindMountScenario or
            InitDataVolumeScenario or
            ReservedUserNameScenario or
            TelemetryCredentialRuntimeOperandScenario;

        IResourceBuilder<ParameterResource>? pinnedUser = null;
        IResourceBuilder<ParameterResource>? pinnedPassword = null;

        if (pinnedCredentials)
        {
            builder.Configuration["Parameters:docdbuser"] =
                scenario == ReservedUserNameScenario ? ReservedUserName : CustomUserName;
            builder.Configuration["Parameters:docdbpass"] =
                scenario == TelemetryCredentialRuntimeOperandScenario
                    ? GetRequired(RuntimeOperandValueEnvironmentVariable)
                    : CustomPassword;
            pinnedUser = builder.AddParameter("docdbuser", secret: false);
            pinnedPassword = builder.AddParameter("docdbpass", secret: true);
        }

        var documentDB = builder.AddDocumentDB("documentdb", port: null, userName: pinnedUser, password: pinnedPassword);

        switch (scenario)
        {
            case DataVolumeScenario:
                documentDB.WithDataVolume(GetRequired(VolumeNameEnvironmentVariable));
                break;

            case DataBindMountScenario:
                documentDB.WithDataBindMount(GetRequired(BindMountPathEnvironmentVariable));
                break;

            case InitDataVolumeScenario:
                documentDB
                    .WithDataVolume(GetRequired(VolumeNameEnvironmentVariable))
                    .WithInitData(GetRequired(InitDataPathEnvironmentVariable));
                break;

            case WithoutUserCreationScenario:
                documentDB
                    .WithEnvironment("INIT_DATA", "true")
                    .WithoutSampleData()
                    .WithoutUserCreation();
                break;

            case ObservableConfigScenario:
                var otelOutputPath = Environment.GetEnvironmentVariable(OtelOutputPathEnvironmentVariable);
                var otelEndpoint = Environment.GetEnvironmentVariable(OtelEndpointEnvironmentVariable);
                IResourceBuilder<ContainerResource>? collector = null;

                if (!string.IsNullOrWhiteSpace(otelOutputPath))
                {
                    var otelConfigPath = CreateOtelCollectorConfiguration(otelOutputPath);
                    collector = builder.AddContainer(
                            "otel-collector",
                            "otel/opentelemetry-collector-contrib",
                            "0.130.1")
                        .WithBindMount(otelConfigPath, "/etc/otelcol-contrib/config.yaml", isReadOnly: true)
                        .WithBindMount(otelOutputPath, "/var/lib/otel")
                        .WithContainerRuntimeArgs("--user", "0:0")
                        .WithArgs("--config=/etc/otelcol-contrib/config.yaml")
                        .WithEndpoint(targetPort: 4317, name: "grpc");
                    otelEndpoint = "http://otel-collector:4317";
                }

                documentDB.WithLogLevel(DocumentDBLogLevel.Debug);

                if (collector is null)
                {
                    // Retain 0.114 control-image environment propagation coverage without coupling the
                    // 0.116 OTLP test to a PostgreSQL owner role that the image does not create.
                    documentDB.WithOwner("aspireowner");
                }

                documentDB.WithOpenTelemetryMetrics(
                        endpoint: string.IsNullOrWhiteSpace(otelEndpoint)
                            ? throw new InvalidOperationException(
                                $"{OtelOutputPathEnvironmentVariable} or {OtelEndpointEnvironmentVariable} must be set.")
                            : otelEndpoint,
                        enabled: true,
                        exportInterval: collector is null ? TimeSpan.FromSeconds(15) : TimeSpan.FromSeconds(1),
                        timeout: TimeSpan.FromSeconds(7),
                        serviceName: "aspire-documentdb-e2e",
                        serviceVersion: "1.2.3");

                if (collector is not null)
                {
                    documentDB.WaitFor(collector);
                }
                break;

            case TelemetryTemporaryDataPathScenario:
                if (bool.TryParse(
                        Environment.GetEnvironmentVariable(DataPathArgumentEnvironmentVariable),
                        out var useDataPathArgument) &&
                    useDataPathArgument)
                {
                    documentDB.WithArgs("--data-path", "/tmp");
                }
                else
                {
                    documentDB.WithEnvironment("DATA_PATH", "/tmp");
                }

                documentDB.WithOpenTelemetryMetrics(
                    endpoint: "http://localhost:4317",
                    enabled: bool.Parse(GetRequired(OtelEnabledEnvironmentVariable)),
                    exportInterval: TimeSpan.FromSeconds(1));
                break;

            case TelemetryWrapperArgumentOrderScenario:
                // '--disable-extended-rum' takes no operand, so it is a complete image-entrypoint
                // argument on its own. Inserting it at the front is exactly what used to produce
                // '/bin/bash --disable-extended-rum -c <script> --', which starts nothing.
                documentDB
                    .WithOpenTelemetryMetrics(endpoint: "http://localhost:4317", enabled: false)
                    .WithArgs(context => context.Args.Insert(0, "--disable-extended-rum"));
                break;

            case TelemetryAliasedTemporaryRootScenario:
                // One host directory, two windows onto it. '/tmp' and '/data' do not contain one
                // another as container paths, so a wrapper that only compared container paths
                // would write its scratch copy straight into the fresh data directory and
                // DocumentDB 0.116.0 would refuse to initialise it.
                documentDB
                    .WithDataBindMount(GetRequired(BindMountPathEnvironmentVariable))
                    .WithBindMount(GetRequired(BindMountPathEnvironmentVariable), "/tmp")
                    .WithOpenTelemetryMetrics(endpoint: "http://localhost:4317", enabled: false);
                break;

            case TelemetrySymlinkAliasedTemporaryRootScenario:
                documentDB
                    .WithDataBindMount(GetRequired(BindMountPathEnvironmentVariable))
                    .WithBindMount(GetRequired(ScratchBindMountPathEnvironmentVariable), "/tmp")
                    .WithOpenTelemetryMetrics(endpoint: "http://localhost:4317", enabled: false);
                break;

            case TelemetryRawRuntimeVolumeScenario:
                var runtimeVolume = GetRequired(VolumeNameEnvironmentVariable);
                documentDB
                    .WithDataVolume(runtimeVolume)
                    .WithContainerRuntimeArgs(
                        "--mount",
                        $"type=volume,source={runtimeVolume},target=/tmp")
                    .WithOpenTelemetryMetrics(endpoint: "http://localhost:4317", enabled: false);
                break;

            case TelemetryUnprovableTemporaryRootsScenario:
                var scratchRoot = GetRequired(ScratchBindMountPathEnvironmentVariable);
                documentDB
                    .WithDataBindMount(GetRequired(BindMountPathEnvironmentVariable))
                    .WithBindMount(Path.Combine(scratchRoot, "tmp"), "/tmp")
                    .WithBindMount(Path.Combine(scratchRoot, "var-tmp"), "/var/tmp")
                    .WithBindMount(Path.Combine(scratchRoot, "dev-shm"), "/dev/shm")
                    .WithOpenTelemetryMetrics(endpoint: "http://localhost:4317", enabled: false);
                break;

            case TelemetryShellExecutionScenario:
                documentDB.WithOpenTelemetryMetrics(endpoint: "http://localhost:4317", enabled: false);
                documentDB.Resource.ShellExecution =
                    GetRequired(ShellExecutionEnvironmentVariable) switch
                    {
                        "null" => null,
                        "false" => false,
                        "true" => true,
                        var value => throw new InvalidOperationException(
                            $"{ShellExecutionEnvironmentVariable} must be null, false, or true, but was '{value}'."),
                    };
                break;

            case TelemetryShellExecutionMutationScenario:
                documentDB.WithOpenTelemetryMetrics(endpoint: "http://localhost:4317", enabled: false);
                builder.Eventing.Subscribe<BeforeStartEvent>(async (_, cancellationToken) =>
                {
                    await ExecutionConfigurationBuilder.Create(documentDB.Resource)
                        .WithArgumentsConfig()
                        .BuildAsync(builder.ExecutionContext, NullLogger.Instance, cancellationToken);
                    documentDB.Resource.ShellExecution = true;
                });
                break;

            case TelemetrySecretRuntimeOperandScenario:
                builder.Configuration["Parameters:runtime-operand"] =
                    GetRequired(RuntimeOperandValueEnvironmentVariable);
                var runtimeOperand = builder.AddParameter("runtime-operand", secret: true);
                documentDB
                    .WithContainerRuntimeArgs(context => context.Args.Add(runtimeOperand.Resource))
                    .WithOpenTelemetryMetrics(endpoint: "http://localhost:4317", enabled: false);
                break;

            case TelemetryCredentialRuntimeOperandScenario:
                documentDB
                    .WithContainerRuntimeArgs(context =>
                        context.Args.Add(documentDB.Resource.ConnectionStringExpression))
                    .WithOpenTelemetryMetrics(endpoint: "http://localhost:4317", enabled: false);
                break;

            case DebugLogLevelScenario:
                documentDB.WithLogLevel(DocumentDBLogLevel.Debug);
                break;

            case QuietLogLevelScenario:
                documentDB.WithLogLevel(DocumentDBLogLevel.Quiet);
                break;

            case Pg15Scenario:
                documentDB.WithPostgresVersion(DocumentDBPostgresVersion.Pg15);
                break;

            case Pg16Scenario:
                documentDB.WithPostgresVersion(DocumentDBPostgresVersion.Pg16);
                break;

            case Pg17Scenario:
                break;

            case Pg18Scenario:
                documentDB.WithPostgresVersion(DocumentDBPostgresVersion.Pg18);
                break;

            case OlderVersionScenario:
                documentDB.WithDocumentDBVersion(DocumentDBVersion.V0_112_0);
                break;

            case PostgresExtrasScenario:
                documentDB
                    .WithoutExtendedRum()
                    .WithPostgresEndpoint(int.Parse(GetRequired(PostgresPortEnvironmentVariable)));
                break;

            case StrictTlsScenario:
                // TLS stays on (the default); only the certificate-validation escape hatch is
                // withdrawn, so the driver must reject the container's self-signed certificate.
                documentDB.AllowInsecureTls(false);
                break;

            case PostgresEndpointFloorScenario:
                documentDB
                    .WithPostgresEndpoint()
                    .WithImageTag("pg17-0.111.0");
                break;

            case ReservedUserNameScenario:
                break;

            default:
                throw new InvalidOperationException(
                    $"{ScenarioEnvironmentVariable} must name a known scenario, but was '{scenario}'.");
        }

        var imageTag = Environment.GetEnvironmentVariable(ImageTagEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(imageTag))
        {
            documentDB.WithImageTag(imageTag);
        }

        documentDB.AddDatabase("appdb");

        await RunAsync(builder);
    }

    private static async Task RunAsync(IDistributedApplicationBuilder builder)
    {
        var app = builder.Build();
        await app.RunAsync();
    }

    private static string GetRequired(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);

        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{variable} must be set.")
            : value;
    }

    private static string CreateOtelCollectorConfiguration(string outputPath)
    {
        Directory.CreateDirectory(outputPath);

        var configPath = Path.Combine(outputPath, "otel-collector.yaml");
        File.WriteAllText(configPath, """
            receivers:
              otlp:
                protocols:
                  grpc:
                    endpoint: 0.0.0.0:4317
            exporters:
              file:
                path: /var/lib/otel/metrics.json
            service:
              pipelines:
                metrics:
                  receivers: [otlp]
                  exporters: [file]
            """);

        return configPath;
    }
}
