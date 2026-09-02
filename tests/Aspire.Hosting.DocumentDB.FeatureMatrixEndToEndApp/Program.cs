// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

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

    /// <summary>Custom credential parameters, two databases, one with a distinct database name.</summary>
    public const string CustomCredentialsMultiDbScenario = "custom-credentials-multi-db";

    /// <summary>WithDataVolume: data must survive the container being replaced.</summary>
    public const string DataVolumeScenario = "data-volume";

    /// <summary>WithDataBindMount: data must land on the host path and survive a restart.</summary>
    public const string DataBindMountScenario = "data-bind-mount";

    /// <summary>WithDataVolume + WithInitData: custom initialization is scoped to the volume.</summary>
    public const string InitDataVolumeScenario = "init-data-volume";

    /// <summary>WithoutUserCreation: the container must not provision the admin user.</summary>
    public const string WithoutUserCreationScenario = "without-user-creation";

    /// <summary>WithLogLevel + WithOwner + WithOpenTelemetryMetrics, all observable on the container.</summary>
    public const string ObservableConfigScenario = "observable-config";

    /// <summary>Telemetry wrapper with DATA_PATH set to /tmp.</summary>
    public const string TelemetryTemporaryDataPathScenario = "telemetry-temporary-data-path";

    /// <summary>WithPostgresVersion(Pg15).</summary>
    public const string Pg15Scenario = "pg15";

    /// <summary>WithPostgresVersion(Pg16).</summary>
    public const string Pg16Scenario = "pg16";

    /// <summary>Default PostgreSQL 17 backend with an explicit candidate image.</summary>
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
            ReservedUserNameScenario;

        IResourceBuilder<ParameterResource>? pinnedUser = null;
        IResourceBuilder<ParameterResource>? pinnedPassword = null;

        if (pinnedCredentials)
        {
            builder.Configuration["Parameters:docdbuser"] =
                scenario == ReservedUserNameScenario ? ReservedUserName : CustomUserName;
            builder.Configuration["Parameters:docdbpass"] = CustomPassword;
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
                documentDB.WithoutUserCreation();
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
                    // Retain current-image environment propagation coverage without coupling the
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
                documentDB
                    .WithEnvironment("DATA_PATH", "/tmp")
                    .WithOpenTelemetryMetrics(
                        endpoint: "http://localhost:4317",
                        enabled: bool.Parse(GetRequired(OtelEnabledEnvironmentVariable)),
                        exportInterval: TimeSpan.FromSeconds(1));
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
