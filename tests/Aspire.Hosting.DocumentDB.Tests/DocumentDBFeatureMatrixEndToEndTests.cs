// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using Npgsql;
using Xunit;
using static Aspire.Hosting.DocumentDB.Tests.DocumentDBEndToEndSupport;
using AppHost = Aspire.Hosting.DocumentDB.FeatureMatrixEndToEndApp.Program;

namespace Aspire.Hosting.DocumentDB.Tests;

/// <summary>
/// Container-backed coverage for the parts of the public API that only had model-level tests:
/// data persistence, credential parameters, multi-database topologies, every PostgreSQL backend
/// variant, the observable container-configuration knobs, and the negative TLS and image-floor
/// paths.
/// </summary>
/// <remarks>
/// Model-level tests prove the resource graph is shaped correctly. These prove the container
/// actually honours it — which is a different claim, and the one that breaks when an image
/// changes underneath the package.
/// </remarks>
[Trait("Category", "Integration")]
public class DocumentDBFeatureMatrixEndToEndTests
{
    private const string ReleasedVersion = DocumentDBVersions.V0_116_0;
    private const string QuietWindowControlMarker = "[ASPIRE-TEST] quiet-window-control";
    private static readonly Regex GatewayDebugInsertRegex = new(
        @"DEBUG[^\r\n]*documentdb_api\.insert",
        RegexOptions.CultureInvariant);
    private static readonly Regex GatewaySeverityLineRegex = new(
        @"(?m)^(?:\[GATEWAY-FILE\]\s+)?\d{4}-\d{2}-\d{2}T[^\r\n]*\s(?:INFO|WARN|ERROR|DEBUG)\s",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// The container path <see cref="DocumentDBBuilderExtensions.WithDataBindMount"/> mounts the
    /// host directory at, and the value it puts in <c>DATA_PATH</c>. PostgreSQL names it when it
    /// rejects the directory's owner.
    /// </summary>
    private const string MountedDataPath = "/data";

    // ------------------------------------------------------------------
    // Credentials, multiple databases, database naming
    // ------------------------------------------------------------------

    [Fact]
    public async Task CustomCredentialParametersAuthenticateAndBothDatabasesWork()
    {
        RequireDocker();

        using var cts = CreateEndToEndTimeoutSource();
        using var scenario = new EnvironmentScope(
            (AppHost.ScenarioEnvironmentVariable, AppHost.CustomCredentialsMultiDbScenario));

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<AppHost>(cts.Token);
        await using var app = await appHost.BuildAsync(cts.Token);

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var server = Assert.Single(Snapshot<DocumentDBServerResource>(appModel.Resources));

        // Model-level facts that the container run then has to honour.
        Assert.Equal("docdbuser", server.UserNameParameter?.Name);
        Assert.Equal("docdbpass", server.PasswordParameter?.Name);
        Assert.Equal(2, server.Databases.Count);

        var primary = server.Databases.Single(d => d.Name == "primary");
        var orders = server.Databases.Single(d => d.Name == "orders");
        Assert.Equal("primary", primary.DatabaseName);
        Assert.Equal("orders_db", orders.DatabaseName);   // resource name != database name
        Assert.Same(server, orders.Parent);

        await app.StartAsync(cts.Token);

        var healthCheckService = app.Services.GetRequiredService<HealthCheckService>();
        await WaitForHealthCheckAsync(healthCheckService, "documentdb_check", cts.Token);

        var primaryConnectionString = await app.GetConnectionStringAsync("primary", cts.Token);
        var ordersConnectionString = await app.GetConnectionStringAsync("orders", cts.Token);

        // The custom credentials must be the ones in the connection string AND the ones the
        // container provisioned - a mismatch shows up as an auth failure below.
        Assert.Contains($"{AppHost.CustomUserName}:{AppHost.CustomPassword}@", primaryConnectionString!, StringComparison.Ordinal);
        Assert.Contains("/orders_db", ordersConnectionString!, StringComparison.Ordinal);

        await AssertRoundTripAsync(primaryConnectionString!, "primary", "widgets", "primary-widget", cts.Token);
        await AssertRoundTripAsync(ordersConnectionString!, "orders_db", "orders", "order-1", cts.Token);

        // The two databases must be genuinely distinct on the server.
        var database = await ConnectAsync(primaryConnectionString!, "primary", cts.Token);
        await database.GetCollection<BsonDocument>("marker").InsertOneAsync(
            new BsonDocument { ["_id"] = "only-in-primary" }, cancellationToken: cts.Token);

        var ordersDatabase = await ConnectAsync(ordersConnectionString!, "orders_db", cts.Token);
        var strayCount = await ordersDatabase.GetCollection<BsonDocument>("marker")
            .CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("_id", "only-in-primary"), cancellationToken: cts.Token);
        Assert.Equal(0, strayCount);
    }

    // ------------------------------------------------------------------
    // Data persistence
    // ------------------------------------------------------------------

    [Fact]
    public async Task DataVolumeSurvivesTheContainerBeingReplaced()
    {
        RequireDocker();

        using var cts = CreateEndToEndTimeoutSource();
        var volumeName = $"aspire-documentdb-e2e-{Guid.NewGuid():N}";

        using var scenario = new EnvironmentScope(
            (AppHost.ScenarioEnvironmentVariable, AppHost.DataVolumeScenario),
            (AppHost.VolumeNameEnvironmentVariable, volumeName));

        try
        {
            // First run: write a document, then tear the whole application down.
            await using (var app = await BuildAndStartAsync(cts.Token))
            {
                var connectionString = await app.GetConnectionStringAsync("appdb", cts.Token);
                var database = await ConnectAsync(connectionString!, "appdb", cts.Token);

                await database.GetCollection<BsonDocument>("persisted").InsertOneAsync(
                    new BsonDocument { ["_id"] = "survivor", ["run"] = 1 },
                    cancellationToken: cts.Token);

                await app.StopAsync(cts.Token);
            }

            // Second run: a brand new container, same named volume.
            await using (var app = await BuildAndStartAsync(cts.Token))
            {
                var connectionString = await app.GetConnectionStringAsync("appdb", cts.Token);
                var database = await ConnectAsync(connectionString!, "appdb", cts.Token);

                var survivor = await database.GetCollection<BsonDocument>("persisted")
                    .Find(Builders<BsonDocument>.Filter.Eq("_id", "survivor"))
                    .SingleOrDefaultAsync(cts.Token);

                Assert.NotNull(survivor);
                Assert.Equal(1, survivor!["run"].AsInt32);

                await app.StopAsync(cts.Token);
            }
        }
        finally
        {
            await RemoveVolumeAsync(volumeName);
        }
    }

    [Fact]
    public async Task DataBindMountWritesToTheHostPathAndSurvivesARestartExceptOnDockerDesktop()
    {
        RequireDocker();

        using var cts = CreateEndToEndTimeoutSource();
        var bindMountPath = Path.Combine(Path.GetTempPath(), "aspire-documentdb-e2e", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bindMountPath);

        using var scenario = new EnvironmentScope(
            (AppHost.ScenarioEnvironmentVariable, AppHost.DataBindMountScenario),
            (AppHost.BindMountPathEnvironmentVariable, bindMountPath));

        // Asked of the daemon, not of the host OS: only Docker Desktop is allowed to fail the
        // restart below, and a Linux host can be either.
        var runtime = await DocumentDBContainerRuntime.DescribeAsync();

        try
        {
            await using (var app = await BuildAndStartAsync(cts.Token))
            {
                var connectionString = await app.GetConnectionStringAsync("appdb", cts.Token);
                var database = await ConnectAsync(connectionString!, "appdb", cts.Token);

                await database.GetCollection<BsonDocument>("persisted").InsertOneAsync(
                    new BsonDocument { ["_id"] = "bind-survivor" },
                    cancellationToken: cts.Token);

                await app.StopAsync(cts.Token);
            }

            // The PostgreSQL data directory must be visible on the host side of the mount. It is
            // read back through a container because initdb leaves the directory 0700 owned by the
            // container's uid, which locks this process out of the host path on Linux. Named
            // members rather than a count: a mount that received a stray file would satisfy
            // "not empty" while proving nothing about where DocumentDB put its data.
            AssertIsPostgresDataDirectory(await ListBindMountEntriesAsync(bindMountPath), bindMountPath);

            await using (var app = await BuildAndStartAsync(cts.Token))
            {
                var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
                var server = Assert.Single(Snapshot<DocumentDBServerResource>(appModel.Resources));
                var containerId = await GetContainerIdAsync(app, server.Name, cts.Token);
                var containerStartedAt = await GetContainerStartedAtAsync(containerId, cts.Token);
                var connectionString = await app.GetConnectionStringAsync("appdb", cts.Token);

                var (outcome, containerLog) = await WaitForRestartOutcomeAsync(
                    connectionString!, containerId, containerStartedAt, cts.Token);

                // Reachable is required everywhere; the refusal is tolerated only on the runtime
                // that provably cannot avoid it. Anywhere else this throws with the diagnosis and
                // the container's own log.
                DocumentDBBindMountRestart.AssertOutcomeIsAllowed(outcome, runtime, containerLog);

                if (outcome == BindMountRestartOutcome.Reachable)
                {
                    var database = await ConnectAsync(connectionString!, "appdb", cts.Token);

                    var survivor = await database.GetCollection<BsonDocument>("persisted")
                        .Find(Builders<BsonDocument>.Filter.Eq("_id", "bind-survivor"))
                        .SingleOrDefaultAsync(cts.Token);

                    Assert.NotNull(survivor);
                }
                else
                {
                    // The data is still on the host and intact; it is the handover that failed.
                    // Asserting that separates "this runtime cannot re-attach the directory" from
                    // "the restart destroyed the data", which would be a different bug entirely.
                    AssertIsPostgresDataDirectory(await ListBindMountEntriesAsync(bindMountPath), bindMountPath);
                }

                await app.StopAsync(cts.Token);
            }
        }
        finally
        {
            await TryRelaxBindMountPermissionsAsync(bindMountPath);
            TryDeleteDirectory(bindMountPath);
        }
    }

    /// <summary>
    /// The restart policy above only relaxes for Docker Desktop, so the identification must come
    /// from the daemon whenever there is one. If this ever fell back to the host OS on a machine
    /// with a working Docker, a Linux developer box running Docker Desktop would be held to the
    /// strict rule and a macOS host running a native-semantics runtime would not.
    /// </summary>
    [Fact]
    public async Task TheContainerRuntimeIdentifiesItselfToTheRestartPolicy()
    {
        RequireDocker();

        var runtime = await DocumentDBContainerRuntime.DescribeAsync();

        Assert.True(
            runtime.DaemonAnswered,
            $"Docker is available, so the runtime must be identified from 'docker info' rather than " +
            $"from the host OS, but the description fell back: {runtime}.");
        Assert.False(string.IsNullOrWhiteSpace(runtime.OperatingSystem));
        Assert.Equal(DocumentDBContainerRuntime.IsDockerDesktop(runtime.OperatingSystem), runtime.IsDockerDesktop);
    }

    /// <summary>
    /// Watches a restarted DocumentDB container until it either serves the bind-mounted data
    /// directory or shows this run refusing it over its ownership.
    /// </summary>
    /// <remarks>
    /// The outcome is observed rather than predicted. Probing the runtime's <c>chown</c>
    /// visibility ahead of time and branching on the answer was tried first and is not reliable:
    /// the same probe reported the ownership change eagerly on some runs and lazily on others, so
    /// which assertions ran became a coin toss. Both signals are polled here and the first to
    /// arrive decides; the caller then holds the outcome to the standard its runtime is held to.
    /// A container that neither serves the data nor names that failure fails the test with its own
    /// log attached, instead of the three-minute Mongo connect timeout naming neither the data
    /// directory nor the ownership check that this suite used to report.
    /// </remarks>
    private static async Task<(BindMountRestartOutcome Outcome, string ContainerLog)> WaitForRestartOutcomeAsync(
        string connectionString,
        string containerId,
        DateTimeOffset containerStartedAt,
        CancellationToken cancellationToken)
    {
        // Short timeouts: this polls two signals in turn, so a slow-starting server must not
        // starve the log check.
        var database = GetDatabase(connectionString, "appdb", TimeSpan.FromSeconds(2));
        var logs = string.Empty;

        for (var attempt = 0; attempt < 60; attempt++)
        {
            logs = await GetContainerLogsAsync(containerId);

            if (DocumentDBBindMountRestart.IndicatesStaleOwnershipRefusal(logs, MountedDataPath, containerStartedAt))
            {
                return (BindMountRestartOutcome.RefusedForStaleOwnership, logs);
            }

            try
            {
                await database.RunCommandAsync(
                    (Command<BsonDocument>)"{ ping: 1 }",
                    cancellationToken: cancellationToken);
                return (BindMountRestartOutcome.Reachable, logs);
            }
            catch (TimeoutException)
            {
            }
            catch (MongoException)
            {
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new InvalidOperationException(
            "The restarted DocumentDB container neither served the bind-mounted data directory nor " +
            $"reported that it could not take it over. Container log:{Environment.NewLine}{logs}");
    }

    private static void AssertIsPostgresDataDirectory(string[] entries, string hostPath)
    {
        foreach (var expected in new[] { "PG_VERSION", "postgresql.conf", "base" })
        {
            Assert.True(
                entries.Contains(expected, StringComparer.Ordinal),
                $"Expected the DocumentDB data directory materialised under '{hostPath}' to contain " +
                $"'{expected}', but it held: {string.Join(", ", entries)}");
        }
    }

    [Fact]
    public async Task PersistedPg17DataUpgradesFrom0114To0116()
    {
        RequireDocker();

        using var cts = CreateEndToEndTimeoutSource();
        var volumeName = $"aspire-documentdb-upgrade-{Guid.NewGuid():N}";

        try
        {
            using (var scenario = new EnvironmentScope(
                       (AppHost.ScenarioEnvironmentVariable, AppHost.DataVolumeScenario),
                       (AppHost.VolumeNameEnvironmentVariable, volumeName),
                       (AppHost.ImageTagEnvironmentVariable, "pg17-0.114.0")))
            {
                await using var app = await BuildAndStartAsync(cts.Token);
                var connectionString = await app.GetConnectionStringAsync("appdb", cts.Token);
                var database = await ConnectAsync(connectionString!, "appdb", cts.Token);

                await database.GetCollection<BsonDocument>("upgrade").InsertOneAsync(
                    new BsonDocument { ["_id"] = "pre-upgrade", ["version"] = "0.114.0" },
                    cancellationToken: cts.Token);

                await app.StopAsync(cts.Token);
            }

            using (var scenario = new EnvironmentScope(
                       (AppHost.ScenarioEnvironmentVariable, AppHost.DataVolumeScenario),
                       (AppHost.VolumeNameEnvironmentVariable, volumeName),
                       (AppHost.ImageTagEnvironmentVariable, ReleasedTag(17))))
            {
                await using var app = await BuildAndStartAsync(cts.Token);
                var connectionString = await app.GetConnectionStringAsync("appdb", cts.Token);
                var database = await ConnectAsync(connectionString!, "appdb", cts.Token);
                var collection = database.GetCollection<BsonDocument>("upgrade");

                var existing = await collection
                    .Find(Builders<BsonDocument>.Filter.Eq("_id", "pre-upgrade"))
                    .SingleOrDefaultAsync(cts.Token);
                Assert.NotNull(existing);

                await collection.InsertOneAsync(
                    new BsonDocument { ["_id"] = "post-upgrade", ["version"] = ReleasedVersion },
                    cancellationToken: cts.Token);
                Assert.Equal(
                    2,
                    await collection.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: cts.Token));

                await app.StopAsync(cts.Token);
            }
        }
        finally
        {
            await RemoveVolumeAsync(volumeName);
        }
    }

    [Fact]
    public async Task CustomInitializationRunsOnlyOnceForAPersisted0116Volume()
    {
        RequireDocker();

        using var cts = CreateEndToEndTimeoutSource();
        var volumeName = $"aspire-documentdb-init-{Guid.NewGuid():N}";
        var initDataPath = Path.Combine(Path.GetTempPath(), "aspire-documentdb-init", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(initDataPath);
        var scriptPath = Path.Combine(initDataPath, "01-seed.js");

        try
        {
            await File.WriteAllTextAsync(scriptPath, """
                db = db.getSiblingDB("appdb");
                db.seed.insertOne({ _id: "first-run" });
                """, cts.Token);

            using (var scenario = new EnvironmentScope(
                       (AppHost.ScenarioEnvironmentVariable, AppHost.InitDataVolumeScenario),
                       (AppHost.VolumeNameEnvironmentVariable, volumeName),
                       (AppHost.InitDataPathEnvironmentVariable, initDataPath),
                       (AppHost.ImageTagEnvironmentVariable, ReleasedTag(17))))
            {
                await using var app = await BuildAndStartAsync(cts.Token);
                var connectionString = await app.GetConnectionStringAsync("appdb", cts.Token);
                var database = await ConnectAsync(connectionString!, "appdb", cts.Token);
                await WaitForDocumentAsync(database, "seed", "first-run", cts.Token);

                var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
                var server = Assert.Single(Snapshot<DocumentDBServerResource>(appModel.Resources));
                var containerId = await GetContainerIdAsync(app, server.Name, cts.Token);
                var logs = await WaitForContainerLogAsync(
                    containerId,
                    "=== DocumentDB is ready ===",
                    cts.Token);
                Assert.Contains("Custom data initialization completed.", logs, StringComparison.Ordinal);
                Assert.Contains("=== DocumentDB is ready ===", logs, StringComparison.Ordinal);

                await app.StopAsync(cts.Token);
            }

            await File.WriteAllTextAsync(scriptPath, """
                db = db.getSiblingDB("appdb");
                db.seed.insertOne({ _id: "second-run" });
                """, cts.Token);

            using (var scenario = new EnvironmentScope(
                       (AppHost.ScenarioEnvironmentVariable, AppHost.InitDataVolumeScenario),
                       (AppHost.VolumeNameEnvironmentVariable, volumeName),
                       (AppHost.InitDataPathEnvironmentVariable, initDataPath),
                       (AppHost.ImageTagEnvironmentVariable, ReleasedTag(17))))
            {
                await using var app = await BuildAndStartAsync(cts.Token);
                var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
                var server = Assert.Single(Snapshot<DocumentDBServerResource>(appModel.Resources));
                var containerId = await GetContainerIdAsync(app, server.Name, cts.Token);
                var logs = await WaitForContainerLogAsync(
                    containerId,
                    "Custom data already initialized",
                    cts.Token);
                Assert.Contains(
                    "Custom data already initialized",
                    logs,
                    StringComparison.Ordinal);
                logs = await WaitForContainerLogAsync(
                    containerId,
                    "=== DocumentDB is ready ===",
                    cts.Token);
                Assert.Contains("=== DocumentDB is ready ===", logs, StringComparison.Ordinal);

                var connectionString = await app.GetConnectionStringAsync("appdb", cts.Token);
                var database = await ConnectAsync(connectionString!, "appdb", cts.Token);
                var collection = database.GetCollection<BsonDocument>("seed");

                Assert.NotNull(await collection
                    .Find(Builders<BsonDocument>.Filter.Eq("_id", "first-run"))
                    .SingleOrDefaultAsync(cts.Token));
                Assert.Null(await collection
                    .Find(Builders<BsonDocument>.Filter.Eq("_id", "second-run"))
                    .SingleOrDefaultAsync(cts.Token));

                await app.StopAsync(cts.Token);
            }
        }
        finally
        {
            await RemoveVolumeAsync(volumeName);
            TryDeleteDirectory(initDataPath);
        }
    }

    // ------------------------------------------------------------------
    // PostgreSQL backend variants
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(AppHost.Pg15Scenario, "pg15-0.114.0")]
    [InlineData(AppHost.Pg16Scenario, "pg16-0.114.0")]
    public async Task Pg15AndPg16On0114RemainRunnableAsLegacyControls(
        string scenarioName,
        string expectedTag)
    {
        // The pinned 0.116 theory below covers the released PG15-PG18 matrix. These explicit
        // 0.114 pins preserve the legacy controls that existed before 0.116 became the default,
        // proving the older PG15/PG16 images still exist and serve traffic.
        RequireDocker();

        using var cts = CreateEndToEndTimeoutSource();
        using var scenario = new EnvironmentScope(
            (AppHost.ScenarioEnvironmentVariable, scenarioName),
            (AppHost.ImageTagEnvironmentVariable, expectedTag));

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<AppHost>(cts.Token);
        await using var app = await appHost.BuildAsync(cts.Token);

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var server = Assert.Single(Snapshot<DocumentDBServerResource>(appModel.Resources));
        var image = Assert.Single(Snapshot<ContainerImageAnnotation>(server.Annotations));
        Assert.Equal(expectedTag, image.Tag);

        await app.StartAsync(cts.Token);

        var healthCheckService = app.Services.GetRequiredService<HealthCheckService>();
        await WaitForHealthCheckAsync(healthCheckService, "documentdb_check", cts.Token);

        var connectionString = await app.GetConnectionStringAsync("appdb", cts.Token);
        await AssertRoundTripAsync(connectionString!, "appdb", "widgets", $"{scenarioName}-widget", cts.Token);
    }

    [Theory]
    [InlineData(AppHost.Pg15Scenario, 15)]
    [InlineData(AppHost.Pg16Scenario, 16)]
    [InlineData(AppHost.Pg17Scenario, 17)]
    [InlineData(AppHost.Pg18Scenario, 18)]
    public async Task Every0116PostgresVariantResolvesToARealImageAndServesTraffic(string scenarioName, int postgresVersion)
    {
        RequireDocker();

        using var cts = CreateEndToEndTimeoutSource();
        var releasedTag = ReleasedTag(postgresVersion);
        using var scenario = new EnvironmentScope(
            (AppHost.ScenarioEnvironmentVariable, scenarioName),
            (AppHost.ImageTagEnvironmentVariable, releasedTag));

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<AppHost>(cts.Token);
        await using var app = await appHost.BuildAsync(cts.Token);

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var server = Assert.Single(Snapshot<DocumentDBServerResource>(appModel.Resources));
        var image = Assert.Single(Snapshot<ContainerImageAnnotation>(server.Annotations));
        Assert.Equal(releasedTag, image.Tag);

        await app.StartAsync(cts.Token);

        var healthCheckService = app.Services.GetRequiredService<HealthCheckService>();
        await WaitForHealthCheckAsync(healthCheckService, "documentdb_check", cts.Token);

        var connectionString = await app.GetConnectionStringAsync("appdb", cts.Token);
        await AssertRoundTripAsync(connectionString!, "appdb", "widgets", $"{scenarioName}-widget", cts.Token);
    }

    [Fact]
    public async Task AnOlderCuratedDocumentDBVersionStillRuns()
    {
        // WithDocumentDBVersion is only meaningful if the older curated members still resolve to
        // images that work; the curated list is append-only and claims support for all of them.
        RequireDocker();

        using var cts = CreateEndToEndTimeoutSource();
        using var scenario = new EnvironmentScope((AppHost.ScenarioEnvironmentVariable, AppHost.OlderVersionScenario));

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<AppHost>(cts.Token);
        await using var app = await appHost.BuildAsync(cts.Token);

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var server = Assert.Single(Snapshot<DocumentDBServerResource>(appModel.Resources));
        var image = Assert.Single(Snapshot<ContainerImageAnnotation>(server.Annotations));
        Assert.Equal("pg17-0.112.0", image.Tag);

        await app.StartAsync(cts.Token);

        var connectionString = await app.GetConnectionStringAsync("appdb", cts.Token);

        // 0.112.0 predates the TLS_MODE fix, so it rejects plain connections - the connection
        // string keeps TLS on by default, which is exactly why that default exists.
        await AssertRoundTripAsync(connectionString!, "appdb", "widgets", "legacy-widget", cts.Token);
    }

    // ------------------------------------------------------------------
    // Container configuration knobs, asserted against the container
    // ------------------------------------------------------------------

    [Fact]
    public async Task LogLevelOwnerAndOpenTelemetryMetricsReachThe0114ControlContainer()
    {
        RequireDocker();

        using var cts = CreateEndToEndTimeoutSource();
        var otelEndpoint = "http://localhost:4317";

        using var scenario = new EnvironmentScope(
            (AppHost.ScenarioEnvironmentVariable, AppHost.ObservableConfigScenario),
            (AppHost.OtelEndpointEnvironmentVariable, otelEndpoint),
            (AppHost.ImageTagEnvironmentVariable, "pg17-0.114.0"));

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<AppHost>(cts.Token);
        await using var app = await appHost.BuildAsync(cts.Token);

        await app.StartAsync(cts.Token);

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var server = Assert.Single(Snapshot<DocumentDBServerResource>(appModel.Resources));
        var containerId = await GetContainerIdAsync(app, server.Name, cts.Token);
        var environment = await GetContainerEnvironmentAsync(containerId);

        Assert.Equal("true", environment["OTEL_METRICS_ENABLED"]);
        Assert.Equal(otelEndpoint, environment["OTEL_EXPORTER_OTLP_METRICS_ENDPOINT"]);
        Assert.Equal("15000", environment["OTEL_METRIC_EXPORT_INTERVAL"]);
        Assert.Equal("7000", environment["OTEL_EXPORTER_OTLP_METRICS_TIMEOUT"]);
        Assert.Equal("aspire-documentdb-e2e", environment["OTEL_SERVICE_NAME"]);
        Assert.Equal("1.2.3", environment["OTEL_SERVICE_VERSION"]);
        Assert.Equal("debug", environment["DOCUMENTDB_LOG_LEVEL"], ignoreCase: true);
        Assert.Equal("debug", environment["LOG_LEVEL"], ignoreCase: true);
        Assert.Equal("aspireowner", environment["OWNER"]);

        var logs = await WaitForContainerLogAsync(containerId, "Using owner: aspireowner", cts.Token);
        Assert.Contains("Using owner: aspireowner", logs, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pg17-0.114.0", "pg17-0.114.0")]
    [InlineData(null, "pg17-0.116.0")]
    public async Task DebugLogLevelEmitsGatewayOutput(string? imageTag, string expectedImageTag)
    {
        RequireDocker();

        using var cts = CreateEndToEndTimeoutSource();
        var variables = new List<(string Name, string Value)>
        {
            (AppHost.ScenarioEnvironmentVariable, AppHost.DebugLogLevelScenario),
        };
        if (imageTag is not null)
        {
            variables.Add((AppHost.ImageTagEnvironmentVariable, imageTag));
        }

        using var scenario = new EnvironmentScope([.. variables]);

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<AppHost>(cts.Token);
        await using var app = await appHost.BuildAsync(cts.Token);

        await app.StartAsync(cts.Token);

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var server = Assert.Single(Snapshot<DocumentDBServerResource>(appModel.Resources));
        var image = Assert.Single(Snapshot<ContainerImageAnnotation>(server.Annotations));
        Assert.Equal(expectedImageTag, image.Tag);

        var healthCheckService = app.Services.GetRequiredService<HealthCheckService>();
        await WaitForHealthCheckAsync(healthCheckService, "documentdb_check", cts.Token);

        var containerId = await GetContainerIdAsync(app, server.Name, cts.Token);
        var environment = await GetContainerEnvironmentAsync(containerId);

        Assert.Equal("debug", environment["DOCUMENTDB_LOG_LEVEL"], ignoreCase: true);
        Assert.Equal("debug", environment["LOG_LEVEL"], ignoreCase: true);

        var connectionString = await app.GetConnectionStringAsync("appdb", cts.Token);
        await AssertRoundTripAsync(connectionString!, "appdb", "log-level", "debug-source", cts.Token);

        // Match the gateway's Mongo insert translation, tying DEBUG output to the operation above
        // instead of to a dependency's startup or telemetry-internal event.
        var logs = await WaitForContainerLogAsync(
            containerId,
            GatewayDebugInsertRegex,
            cts.Token);
        Assert.Matches(GatewayDebugInsertRegex, logs);
    }

    [Fact]
    public async Task QuietLogLevelSuppresses0116GatewayOutput()
    {
        RequireDocker();

        using var cts = CreateEndToEndTimeoutSource();
        using var scenario = new EnvironmentScope(
            (AppHost.ScenarioEnvironmentVariable, AppHost.QuietLogLevelScenario),
            (AppHost.ImageTagEnvironmentVariable, ReleasedTag(17)));

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<AppHost>(cts.Token);
        await using var app = await appHost.BuildAsync(cts.Token);

        await app.StartAsync(cts.Token);

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var server = Assert.Single(Snapshot<DocumentDBServerResource>(appModel.Resources));
        var healthCheckService = app.Services.GetRequiredService<HealthCheckService>();
        await WaitForHealthCheckAsync(healthCheckService, "documentdb_check", cts.Token);

        var containerId = await GetContainerIdAsync(app, server.Name, cts.Token);
        var environment = await GetContainerEnvironmentAsync(containerId);

        Assert.Equal("quiet", environment["DOCUMENTDB_LOG_LEVEL"], ignoreCase: true);
        Assert.Equal("quiet", environment["LOG_LEVEL"], ignoreCase: true);

        var windowStart = DateTimeOffset.UtcNow;
        var (markerExitCode, _) = await RunDockerAsync(
            "exec",
            containerId,
            "/bin/sh",
            "-c",
            $"printf '%s\\n' '{QuietWindowControlMarker}' > /proc/1/fd/1");
        Assert.Equal(0, markerExitCode);

        var connectionString = await app.GetConnectionStringAsync("appdb", cts.Token);
        await AssertRoundTripAsync(connectionString!, "appdb", "log-level", "quiet-source", cts.Token);
        await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);

        var operationWindowLogs = NormalizeContainerLogs(
            await GetContainerLogsSinceAsync(containerId, windowStart));

        // Scope the absence check to tracing-formatted gateway lines. Entrypoint and PostgreSQL
        // messages use different prefixes/formats and remain visible even when the gateway is quiet.
        Assert.False(string.IsNullOrWhiteSpace(operationWindowLogs));
        Assert.Contains(QuietWindowControlMarker, operationWindowLogs, StringComparison.Ordinal);
        Assert.DoesNotMatch(GatewaySeverityLineRegex, operationWindowLogs);
    }

    [Fact]
    public async Task OpenTelemetryMetricsAreExportedFrom0116()
    {
        RequireDocker();

        using var cts = CreateEndToEndTimeoutSource();
        var otelOutputPath = Path.Combine(Path.GetTempPath(), "aspire-documentdb-otel", Guid.NewGuid().ToString("N"));

        using var scenario = new EnvironmentScope(
            (AppHost.ScenarioEnvironmentVariable, AppHost.ObservableConfigScenario),
            (AppHost.OtelOutputPathEnvironmentVariable, otelOutputPath),
            (AppHost.ImageTagEnvironmentVariable, ReleasedTag(17)));

        try
        {
            var appHost = await DistributedApplicationTestingBuilder.CreateAsync<AppHost>(cts.Token);
            await using var app = await appHost.BuildAsync(cts.Token);

            await app.StartAsync(cts.Token);

            var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
            var server = Assert.Single(Snapshot<DocumentDBServerResource>(appModel.Resources));
            var healthCheckService = app.Services.GetRequiredService<HealthCheckService>();
            await WaitForHealthCheckAsync(healthCheckService, "documentdb_check", cts.Token);

            var connectionString = await app.GetConnectionStringAsync("appdb", cts.Token);
            await AssertRoundTripAsync(connectionString!, "appdb", "otel", "metric-source", cts.Token);

            var containerId = await GetContainerIdAsync(app, server.Name, cts.Token);
            var environment = await GetContainerEnvironmentAsync(containerId);

            Assert.Equal("true", environment["OTEL_METRICS_ENABLED"]);
            Assert.Equal("http://otel-collector:4317", environment["OTEL_EXPORTER_OTLP_METRICS_ENDPOINT"]);
            Assert.Equal("1000", environment["OTEL_METRIC_EXPORT_INTERVAL"]);
            Assert.Equal("7000", environment["OTEL_EXPORTER_OTLP_METRICS_TIMEOUT"]);
            Assert.Equal("aspire-documentdb-e2e", environment["OTEL_SERVICE_NAME"]);
            Assert.Equal("1.2.3", environment["OTEL_SERVICE_VERSION"]);
            Assert.False(environment.ContainsKey("CONFIG_DIR"));
            Assert.Equal("debug", environment["DOCUMENTDB_LOG_LEVEL"], ignoreCase: true);
            Assert.Equal("debug", environment["LOG_LEVEL"], ignoreCase: true);

            // The AppHost applies WithImageTag after WithOpenTelemetryMetrics, so this also proves
            // the compatibility wrapper is resolved against the resource's final image.
            Assert.Equal(["/bin/bash"], await GetContainerConfigListAsync(containerId, "Entrypoint"));
            var command = await GetContainerConfigListAsync(containerId, "Cmd");
            Assert.Equal(3, command.Length);
            Assert.Equal("-c", command[0]);
            Assert.Equal("--", command[2]);
            Assert.Contains("del(.TelemetryOptions.Metrics", command[1], StringComparison.Ordinal);
            Assert.DoesNotContain(".TelemetryOptions.Metrics.", command[1], StringComparison.Ordinal);

            // Without the wrapper the shipped SetupConfiguration.json would still pin metrics off,
            // and the gateway would report telemetry_options carrying a Metrics section.
            var gatewayLogs = await WaitForContainerLogAsync(
                containerId,
                "Starting server with configuration",
                cts.Token);
            // The metrics object is gone from the JSON entirely, so the OTEL_* variables decide;
            // the identity keys go with it because the scenario supplies serviceName and
            // serviceVersion explicitly.
            Assert.Contains(
                "service_name: None, service_version: None, metrics: None",
                gatewayLogs,
                StringComparison.Ordinal);

            var metrics = await WaitForFileContainingAsync(
                Path.Combine(otelOutputPath, "metrics.json"),
                "aspire-documentdb-e2e",
                cts.Token);
            Assert.Contains("gateway", metrics, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(otelOutputPath);
        }
    }

    [Theory]
    // No serviceName override: the shipped TelemetryOptions.ServiceName must survive.
    [InlineData(null, "documentdb_gateway")]
    // Explicit serviceName override: the shared JSON identity is removed so the variable wins.
    [InlineData("aspire-documentdb-publish", "aspire-documentdb-publish")]
    public async Task ThePublishedManifestForOpenTelemetryMetricsExportsMetricsFromTheCandidateImage(
        string? serviceName,
        string expectedServiceName)
    {
        // The manifest is what azd deploys. Running exactly what it names - image, entrypoint,
        // args and environment - against a real collector is the only assertion that proves the
        // published artifact is deployable and that metrics actually leave the gateway. If the
        // wrapper were dropped, the shipped SetupConfiguration.json would keep metrics off and no
        // metric would ever arrive, so this fails rather than passing vacuously.
        RequireDocker();

        using var cts = CreateEndToEndTimeoutSource();

        var suffix = Guid.NewGuid().ToString("N");
        var networkName = $"aspire-documentdb-otel-{suffix}";
        var collectorName = $"otel-collector-{suffix}";
        var containerName = $"aspire-documentdb-publish-{suffix}";
        var outputPath = Path.Combine(AppContext.BaseDirectory, "published-otel", suffix);
        var metricsPath = Path.Combine(outputPath, "metrics.json");
        var password = "Aspire-Publish-Pass1";

        // The collector is addressed by container name on a user-defined network, so the endpoint
        // baked into the manifest is the one the deployed container actually resolves.
        var collectorEndpoint = $"http://{collectorName}:4317";

        var manifest = await ManifestUtils.PublishManifestAsync(appBuilder =>
        {
            var documentDB = appBuilder.AddDocumentDB("documentdb");

            if (serviceName is null)
            {
                // No typed endpoint either: this half also proves the generic
                // OTEL_EXPORTER_OTLP_ENDPOINT fallback still reaches the gateway, which it cannot
                // if the shipped Metrics.OtlpEndpoint survives in the configuration file.
                documentDB
                    .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", collectorEndpoint)
                    .WithEnvironment("OTEL_METRIC_EXPORT_INTERVAL", "1000")
                    .WithOpenTelemetryMetrics();
            }
            else
            {
                documentDB.WithOpenTelemetryMetrics(
                    endpoint: collectorEndpoint,
                    exportInterval: TimeSpan.FromSeconds(1),
                    serviceName: serviceName);
            }

            documentDB.WithImageTag(ReleasedTag(17));
        });

        var resource = manifest["resources"]?["documentdb"];
        Assert.NotNull(resource);

        var image = resource!["image"]?.GetValue<string>();
        Assert.Equal($"ghcr.io/documentdb/documentdb/documentdb-local:{ReleasedTag(17)}", image);

        var entrypoint = resource["entrypoint"]?.GetValue<string>();
        Assert.False(string.IsNullOrEmpty(entrypoint));

        var environment = Assert.IsType<JsonObject>(resource["env"]);
        Assert.Equal("true", environment["OTEL_METRICS_ENABLED"]?.GetValue<string>());
        Assert.Equal(
            collectorEndpoint,
            environment[serviceName is null ? "OTEL_EXPORTER_OTLP_ENDPOINT" : "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT"]
                ?.GetValue<string>());

        var dockerArguments = new List<string>
        {
            "run", "--detach", "--name", containerName, "--network", networkName,
            "--entrypoint", entrypoint!,
        };

        foreach (var (name, value) in environment)
        {
            var text = value?.GetValue<string>() ?? string.Empty;

            if (string.Equals(name, "PASSWORD", StringComparison.Ordinal))
            {
                text = password;
            }

            Assert.DoesNotContain('{', text);
            dockerArguments.Add("--env");
            dockerArguments.Add($"{name}={text}");
        }

        dockerArguments.Add(image!);

        foreach (var argument in Assert.IsType<JsonArray>(resource["args"]))
        {
            dockerArguments.Add(argument!.GetValue<string>());
        }

        var collectorConfigPath = CreateOtelCollectorConfigurationFile(outputPath);

        // Pull explicitly: 'docker run' would pull implicitly, but these images are large and the
        // default per-command budget is sized for fast control-plane calls.
        await PullImageAsync(image!);
        await PullImageAsync(OtelCollectorImage);

        var (networkExitCode, networkOutput) = await RunDockerAsync("network", "create", networkName);
        Assert.True(networkExitCode == 0, $"docker network create failed: {networkOutput}");

        string[] documentDBVolumes = [];
        var removed = false;

        try
        {
            var (collectorExitCode, collectorOutput) = await RunDockerAsync(
                "run", "--detach", "--name", collectorName, "--network", networkName,
                "--user", "0:0",
                "--volume", $"{collectorConfigPath}:/etc/otelcol-contrib/config.yaml:ro",
                "--volume", $"{outputPath}:/var/lib/otel",
                OtelCollectorImage,
                "--config=/etc/otelcol-contrib/config.yaml");
            Assert.True(collectorExitCode == 0, $"docker run (collector) failed: {collectorOutput}");

            var (exitCode, output) = await RunDockerAsync([.. dockerArguments]);
            Assert.True(exitCode == 0, $"docker run failed with exit code {exitCode}: {output}");

            documentDBVolumes = await GetContainerVolumeNamesAsync(containerName);

            var logs = await WaitForContainerLogAsync(containerName, "Starting server with configuration", cts.Token);

            // The published entrypoint has to leave the gateway with no JSON metrics pin, while
            // the tracing block this package does not manage stays exactly as shipped.
            Assert.Contains("metrics: None", logs, StringComparison.Ordinal);
            Assert.Contains("tracing: Some(TracingOptions { enabled: Some(false)", logs, StringComparison.Ordinal);

            // Identity is only taken from the caller when the caller asked for it; otherwise the
            // configuration file keeps the value it shipped with.
            Assert.Contains(
                serviceName is null
                    ? "service_name: Some(\"documentdb_gateway\")"
                    : "service_name: None",
                logs,
                StringComparison.Ordinal);

            // gateway.starts is emitted once the gateway is ready, so it needs no database
            // traffic and cannot be produced by anything other than a live metrics pipeline.
            var metrics = await WaitForFileContainingAsync(metricsPath, "gateway.starts", cts.Token);
            Assert.Contains(expectedServiceName, metrics, StringComparison.Ordinal);

            if (serviceName is null)
            {
                Assert.DoesNotContain("aspire-documentdb-publish", metrics, StringComparison.Ordinal);
            }

            // Teardown is asserted on the success path only: an assertion inside finally would
            // replace whatever the test was actually diagnosing.
            await RemoveDockerResourcesAsync(networkName, containerName, collectorName);
            removed = true;
            await AssertVolumesRemovedAsync(documentDBVolumes);
        }
        finally
        {
            try
            {
                if (!removed)
                {
                    await RemoveDockerResourcesAsync(networkName, containerName, collectorName);
                }
            }
            finally
            {
                TryDeleteDirectory(outputPath);
            }
        }
    }

    [Fact]
    public async Task DisabledOpenTelemetryMetricsBeatAConfigurationFileThatEnablesThem()
    {
        // The gateway resolves telemetry as JSON > environment, so a configuration file the caller
        // points CONFIG_DIR at can switch metrics on and OTEL_METRICS_ENABLED=false alone would
        // lose. The wrapper therefore has to be installed for the disabled case too.
        RequireDocker();

        using var cts = CreateEndToEndTimeoutSource();

        var suffix = Guid.NewGuid().ToString("N");
        var networkName = $"aspire-documentdb-otel-{suffix}";
        var collectorName = $"otel-collector-{suffix}";
        var containerName = $"aspire-documentdb-disabled-{suffix}";
        var outputPath = Path.Combine(AppContext.BaseDirectory, "published-otel", suffix);
        var configPath = Path.Combine(outputPath, "config");
        var metricsPath = Path.Combine(outputPath, "metrics.json");

        var manifest = await ManifestUtils.PublishManifestAsync(appBuilder =>
            appBuilder.AddDocumentDB("documentdb")
                .WithEnvironment("CONFIG_DIR", "/custom-config")
                .WithOpenTelemetryMetrics(enabled: false)
                .WithImageTag(ReleasedTag(17)));

        var resource = manifest["resources"]?["documentdb"];
        Assert.NotNull(resource);

        var image = resource!["image"]?.GetValue<string>();
        var entrypoint = resource["entrypoint"]?.GetValue<string>();
        Assert.False(string.IsNullOrEmpty(entrypoint));

        var environment = Assert.IsType<JsonObject>(resource["env"]);
        Assert.Equal("false", environment["OTEL_METRICS_ENABLED"]?.GetValue<string>());
        Assert.Equal("/custom-config", environment["CONFIG_DIR"]?.GetValue<string>());

        var collectorConfigPath = CreateOtelCollectorConfigurationFile(outputPath);
        Directory.CreateDirectory(configPath);
        await File.WriteAllTextAsync(
            Path.Combine(configPath, "SetupConfiguration.json"),
            $$"""
            {
              "NodeHostName": "localhost",
              "BlockedRolePrefixes": ["documentdb", "citus", "pg", "internal_role"],
              "PostgresPort": 9712,
              "GatewayListenPort": 10260,
              "HostConfigurationWatchIntervalMs": 1000,
              "CertificateOptions": { "CertType": "PemAutoGenerated" },
              "UseLocalHost": false,
              "TelemetryOptions": {
                "ServiceName": "aspire-custom-config",
                "Metrics": {
                  "Enabled": true,
                  "OtlpEndpoint": "http://{{collectorName}}:4317",
                  "ExportIntervalMs": 1000
                },
                "Tracing": {
                  "Enabled": false,
                  "OtlpEndpoint": "http://localhost:4317",
                  "SamplerRatio": 1.0,
                  "ExportTimeoutMs": 10000
                }
              }
            }
            """,
            cts.Token);

        var dockerArguments = new List<string>
        {
            "run", "--detach", "--name", containerName, "--network", networkName,
            "--volume", $"{configPath}:/custom-config:ro",
            "--entrypoint", entrypoint!,
        };

        foreach (var (name, value) in environment)
        {
            var text = value?.GetValue<string>() ?? string.Empty;

            if (string.Equals(name, "PASSWORD", StringComparison.Ordinal))
            {
                text = "Aspire-Disabled-Pass1";
            }

            Assert.DoesNotContain('{', text);
            dockerArguments.Add("--env");
            dockerArguments.Add($"{name}={text}");
        }

        dockerArguments.Add(image!);

        foreach (var argument in Assert.IsType<JsonArray>(resource["args"]))
        {
            dockerArguments.Add(argument!.GetValue<string>());
        }

        await PullImageAsync(image!);
        await PullImageAsync(OtelCollectorImage);

        var (networkExitCode, networkOutput) = await RunDockerAsync("network", "create", networkName);
        Assert.True(networkExitCode == 0, $"docker network create failed: {networkOutput}");

        string[] documentDBVolumes = [];
        var removed = false;

        try
        {
            var (collectorExitCode, collectorOutput) = await RunDockerAsync(
                "run", "--detach", "--name", collectorName, "--network", networkName,
                "--user", "0:0",
                "--volume", $"{collectorConfigPath}:/etc/otelcol-contrib/config.yaml:ro",
                "--volume", $"{outputPath}:/var/lib/otel",
                OtelCollectorImage,
                "--config=/etc/otelcol-contrib/config.yaml");
            Assert.True(collectorExitCode == 0, $"docker run (collector) failed: {collectorOutput}");

            var (exitCode, output) = await RunDockerAsync([.. dockerArguments]);
            Assert.True(exitCode == 0, $"docker run failed with exit code {exitCode}: {output}");

            documentDBVolumes = await GetContainerVolumeNamesAsync(containerName);

            var logs = await WaitForContainerLogAsync(containerName, "Starting server with configuration", cts.Token);

            // The caller's file really is the source the wrapper derived from: its service name
            // survives because no serviceName override was supplied.
            Assert.Contains("service_name: Some(\"aspire-custom-config\")", logs, StringComparison.Ordinal);

            // ...and the Metrics object that declared Enabled: true is gone, so the environment
            // decides.
            Assert.Contains("metrics: None", logs, StringComparison.Ordinal);

            await WaitForContainerLogAsync(containerName, "Gateway is ready", cts.Token);

            // Well past the 1s export interval the file exporter has still written nothing:
            // metrics really are off.
            await Task.Delay(TimeSpan.FromSeconds(20), cts.Token);
            Assert.False(
                File.Exists(metricsPath) &&
                (await File.ReadAllTextAsync(metricsPath, cts.Token)).Contains("gateway.starts", StringComparison.Ordinal),
                "Metrics were exported even though WithOpenTelemetryMetrics(enabled: false) was configured.");

            // Teardown is asserted on the success path only: an assertion inside finally would
            // replace whatever the test was actually diagnosing.
            await RemoveDockerResourcesAsync(networkName, containerName, collectorName);
            removed = true;
            await AssertVolumesRemovedAsync(documentDBVolumes);
        }
        finally
        {
            try
            {
                if (!removed)
                {
                    await RemoveDockerResourcesAsync(networkName, containerName, collectorName);
                }
            }
            finally
            {
                TryDeleteDirectory(outputPath);
            }
        }
    }

    [Fact]
    public async Task WithoutUserCreationKeepsFreshCandidateContainerRunningWhenInitializationIsDisabled()
    {
        RequireDocker();

        using var cts = CreateEndToEndTimeoutSource();
        using var scenario = new EnvironmentScope(
            (AppHost.ScenarioEnvironmentVariable, AppHost.WithoutUserCreationScenario),
            (AppHost.ImageTagEnvironmentVariable, ReleasedTag(17)));

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<AppHost>(cts.Token);
        await using var app = await appHost.BuildAsync(cts.Token);

        await app.StartAsync(cts.Token);

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var server = Assert.Single(Snapshot<DocumentDBServerResource>(appModel.Resources));

        var containerId = await GetContainerIdAsync(app, server.Name, cts.Token);
        var environment = await GetContainerEnvironmentAsync(containerId);
        Assert.Equal("false", environment["CREATE_USER"]);
        Assert.Equal("false", environment["INIT_DATA"]);
        Assert.Equal("true", environment["SKIP_INIT_DATA"]);

        var logs = await WaitForContainerLogAsync(containerId, "No initialization data loaded.", cts.Token);
        Assert.Contains("Gateway is ready on localhost:", logs, StringComparison.Ordinal);
        Assert.Contains("Skipping user creation and starting the gateway...", logs, StringComparison.Ordinal);

        var (inspectExitCode, running) = await RunDockerAsync(
            "inspect", containerId, "--format", "{{.State.Running}}");
        Assert.Equal(0, inspectExitCode);
        Assert.Equal("true", running.Trim(), ignoreCase: true);

        var connectionString = await app.GetConnectionStringAsync("appdb", cts.Token);

        var failure = await Record.ExceptionAsync(
            () => PingOnceAsync(connectionString!, "appdb", CancellationToken.None));

        Assert.NotNull(failure);
        Assert.True(
            failure is TimeoutException or MongoException,
            $"Expected authentication against a container with no provisioned user to fail, but got: {failure}");
    }

    // ------------------------------------------------------------------
    // TLS certificate validation
    // ------------------------------------------------------------------

    [Fact]
    public async Task AllowInsecureTlsFalseRejectsTheContainersSelfSignedCertificate()
    {
        // AllowInsecureTls defaults to true for a reason: the container serves a self-signed
        // certificate. Withdrawing the escape hatch must therefore fail closed, and the same
        // endpoint must still work once validation is relaxed - proving it is the certificate
        // being rejected and not a container that never came up.
        RequireDocker();

        using var cts = CreateEndToEndTimeoutSource();
        using var scenario = new EnvironmentScope((AppHost.ScenarioEnvironmentVariable, AppHost.StrictTlsScenario));

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<AppHost>(cts.Token);
        await using var app = await appHost.BuildAsync(cts.Token);

        await app.StartAsync(cts.Token);

        var strictConnectionString = await app.GetConnectionStringAsync("appdb", cts.Token);
        Assert.Contains("tls=true", strictConnectionString!, StringComparison.Ordinal);
        Assert.DoesNotContain("tlsInsecure", strictConnectionString!, StringComparison.Ordinal);

        // Control first: relax validation on the very same endpoint.
        var relaxed = strictConnectionString!.Replace("tls=true", "tls=true&tlsInsecure=true", StringComparison.Ordinal);
        var database = await ConnectAsync(relaxed, "appdb", cts.Token);
        await database.RunCommandAsync((Command<BsonDocument>)"{ ping: 1 }", cancellationToken: cts.Token);

        var failure = await Record.ExceptionAsync(
            () => PingOnceAsync(strictConnectionString!, "appdb", CancellationToken.None));

        Assert.NotNull(failure);
        Assert.True(
            failure is TimeoutException or MongoException,
            $"Expected strict TLS validation to reject the self-signed certificate, but got: {failure}");
    }

    // ------------------------------------------------------------------
    // PostgreSQL endpoint: explicit port, extended_rum, and the version floor
    // ------------------------------------------------------------------

    [Fact]
    public async Task PostgresEndpointOn0114HonoursAnExplicitPortAndWithoutExtendedRumDisablesTheAccessMethod()
    {
        await AssertPostgresExtrasAsync("pg17-0.114.0", assertLz4: false);
    }

    [Fact]
    public async Task PostgresEndpointOn0116UsesLz4ToastCompression()
    {
        await AssertPostgresExtrasAsync(ReleasedTag(17), assertLz4: true);
    }

    private static async Task AssertPostgresExtrasAsync(string? imageTag, bool assertLz4)
    {
        RequireDocker();

        using var cts = CreateEndToEndTimeoutSource();
        var postgresPort = GetAvailableTcpPort();

        using var scenario = imageTag is null
            ? new EnvironmentScope(
                (AppHost.ScenarioEnvironmentVariable, AppHost.PostgresExtrasScenario),
                (AppHost.PostgresPortEnvironmentVariable, postgresPort.ToString()))
            : new EnvironmentScope(
                (AppHost.ScenarioEnvironmentVariable, AppHost.PostgresExtrasScenario),
                (AppHost.PostgresPortEnvironmentVariable, postgresPort.ToString()),
                (AppHost.ImageTagEnvironmentVariable, imageTag));

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<AppHost>(cts.Token);
        await using var app = await appHost.BuildAsync(cts.Token);

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var server = Assert.Single(Snapshot<DocumentDBServerResource>(appModel.Resources));
        var postgresEndpoint = Assert.Single(
            Snapshot<EndpointAnnotation>(server.Annotations).Where(e => e.Name == "postgres"));
        Assert.Equal(postgresPort, postgresEndpoint.Port);

        await app.StartAsync(cts.Token);

        var healthCheckService = app.Services.GetRequiredService<HealthCheckService>();
        await WaitForHealthCheckAsync(healthCheckService, "documentdb_check", cts.Token);

        // The URI must point at whatever host port the endpoint actually resolved to. Note this
        // is NOT asserted to equal the requested port: DistributedApplicationTestingBuilder
        // reassigns fixed host ports so suites can run in parallel, so only the annotation above
        // reflects the request. A normal `dotnet run` does honour it (verified separately).
        var postgresConnectionString = await server.PostgresConnectionStringExpression.GetValueAsync(cts.Token);
        var allocatedPostgresPort = postgresEndpoint.AllocatedEndpoint!.Port;
        Assert.True(
            postgresConnectionString!.Contains($":{allocatedPostgresPort}/", StringComparison.Ordinal),
            $"Expected the PostgreSQL URI to use the allocated port {allocatedPostgresPort}, but it was '{postgresConnectionString}'.");

        // WithoutExtendedRum: the access method must not be installed in the backend. Asserting
        // this through PostgreSQL is the only way to see the effect of DISABLE_EXTENDED_RUM.
        var accessMethods = await QueryPostgresAsync(
            postgresConnectionString!,
            "SELECT count(*) FROM pg_am WHERE amname = 'extended_rum'",
            cts.Token);

        Assert.Equal(0L, Convert.ToInt64(accessMethods));

        if (assertLz4)
        {
            var toastCompression = await QueryPostgresAsync(
                postgresConnectionString!,
                "SHOW default_toast_compression",
                cts.Token);
            Assert.Equal("lz4", Convert.ToString(toastCompression));
        }
    }

    [Fact]
    public async Task ReservedUserNameFailsBefore0116Starts()
    {
        RequireDocker();

        using var cts = CreateEndToEndTimeoutSource();
        using var scenario = new EnvironmentScope(
            (AppHost.ScenarioEnvironmentVariable, AppHost.ReservedUserNameScenario),
            (AppHost.ImageTagEnvironmentVariable, ReleasedTag(17)));

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<AppHost>(cts.Token);
        await using var app = await appHost.BuildAsync(cts.Token);
        await app.StartAsync(cts.Token);

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var server = Assert.Single(Snapshot<DocumentDBServerResource>(appModel.Resources));
        var containerId = await GetContainerIdAsync(app, server.Name, cts.Token);
        var logs = await WaitForContainerLogAsync(
            containerId,
            $"username '{AppHost.ReservedUserName}' uses reserved prefix 'pg'",
            cts.Token);

        Assert.Contains(
            $"username '{AppHost.ReservedUserName}' uses reserved prefix 'pg'",
            logs,
            StringComparison.Ordinal);
        await WaitForResourceFailureAsync(app, server.Name, cts.Token);
    }

    [Fact]
    public async Task PostgresEndpointOnAnImageBelowTheFloorFailsAtStartup()
    {
        // The 0.112.0 floor for WithPostgresEndpoint, exercised through a real orchestrator run
        // rather than by publishing the event by hand. Nothing is pulled: the guard runs first.
        RequireDocker();

        using var cts = CreateEndToEndTimeoutSource();
        using var scenario = new EnvironmentScope(
            (AppHost.ScenarioEnvironmentVariable, AppHost.PostgresEndpointFloorScenario));

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<AppHost>(cts.Token);

        var hostLog = new LogSink();
        appHost.Services.AddLogging(logging => logging.AddProvider(new LogSinkProvider(hostLog)));

        await using var app = await appHost.BuildAsync(cts.Token);

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var server = Assert.Single(Snapshot<DocumentDBServerResource>(appModel.Resources));

        try
        {
            await app.StartAsync(cts.Token);
        }
        catch (Exception ex)
        {
            hostLog.Append(ex.ToString());
        }

        await WaitForResourceFailureAsync(app, server.Name, cts.Token);

        var diagnostics = hostLog.ToString();
        Assert.Contains("pg17-0.111.0", diagnostics, StringComparison.Ordinal);
        Assert.Contains("0.112.0", diagnostics, StringComparison.Ordinal);
        Assert.Contains("WithPostgresEndpoint", diagnostics, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private const string OtelCollectorImage = "otel/opentelemetry-collector-contrib:0.130.1";

    /// <summary>
    /// Removes the containers and the user-defined network a collector-backed scenario created.
    /// <c>--volumes</c> matters: the DocumentDB image declares <c>/data</c> as a <c>VOLUME</c>, so
    /// a plain <c>rm</c> strands one anonymous volume per run.
    /// </summary>
    private static async Task RemoveDockerResourcesAsync(string networkName, params string[] containerNames)
    {
        foreach (var containerName in containerNames)
        {
            await RunDockerAsync("rm", "--force", "--volumes", containerName);
        }

        await RunDockerAsync("network", "rm", networkName);
    }

    private static async Task AssertVolumesRemovedAsync(string[] volumes)
    {
        foreach (var volume in volumes)
        {
            var (exitCode, _) = await RunDockerAsync("volume", "inspect", volume);
            Assert.True(
                exitCode != 0,
                $"Anonymous volume '{volume}' outlived the container it was created for.");
        }
    }

    private static async Task PullImageAsync(string image)
    {
        var (exitCode, output) = await RunDockerAsync(TimeSpan.FromMinutes(10), "pull", image);
        Assert.True(exitCode == 0, $"docker pull {image} failed with exit code {exitCode}: {output}");
    }

    /// <summary>
    /// Names of the volumes Docker created for a container, which for this image is the anonymous
    /// volume backing the declared <c>/data</c> mount point.
    /// </summary>
    private static async Task<string[]> GetContainerVolumeNamesAsync(string containerId)
    {
        var (exitCode, output) = await RunDockerAsync(
            "inspect", containerId, "--format", "{{range .Mounts}}{{println .Name}}{{end}}");

        Assert.Equal(0, exitCode);

        return [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    private static string CreateOtelCollectorConfigurationFile(string outputPath)
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

    private static async Task<string[]> GetContainerConfigListAsync(string containerId, string field)
    {
        var (exitCode, output) = await RunDockerAsync(
            "inspect", containerId, "--format", $"{{{{json .Config.{field}}}}}");

        Assert.Equal(0, exitCode);

        var parsed = JsonNode.Parse(output.Trim());

        return parsed is JsonArray array
            ? [.. array.Select(item => item!.GetValue<string>())]
            : [];
    }

    private static async Task<string> WaitForContainerLogAsync(
        string containerId,
        string expectedSubstring,
        CancellationToken cancellationToken) =>
        await WaitForContainerLogAsync(
            containerId,
            logs => logs.Contains(expectedSubstring, StringComparison.Ordinal),
            $"'{expectedSubstring}'",
            cancellationToken);

    private static async Task<string> WaitForContainerLogAsync(
        string containerId,
        Regex expectedPattern,
        CancellationToken cancellationToken) =>
        await WaitForContainerLogAsync(
            containerId,
            expectedPattern.IsMatch,
            $"pattern '{expectedPattern}'",
            cancellationToken);

    private static async Task<string> WaitForContainerLogAsync(
        string containerId,
        Func<string, bool> predicate,
        string expectation,
        CancellationToken cancellationToken)
    {
        var logs = string.Empty;

        for (var attempt = 0; attempt < 60; attempt++)
        {
            logs = NormalizeContainerLogs(await GetContainerLogsAsync(containerId));

            if (predicate(logs))
            {
                return logs;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new InvalidOperationException(
            $"Container '{containerId}' did not log {expectation} before the timeout. " +
            $"Last logs:{Environment.NewLine}{logs}");
    }

    private static async Task<DistributedApplication> BuildAndStartAsync(CancellationToken cancellationToken)
    {
        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<AppHost>(cancellationToken);
        var app = await appHost.BuildAsync(cancellationToken);
        await app.StartAsync(cancellationToken);
        return app;
    }

    private static string ReleasedTag(int postgresVersion) => $"pg{postgresVersion}-{ReleasedVersion}";

    private static async Task WaitForDocumentAsync(
        IMongoDatabase database,
        string collectionName,
        string id,
        CancellationToken cancellationToken)
    {
        var collection = database.GetCollection<BsonDocument>(collectionName);

        for (var attempt = 0; attempt < 60; attempt++)
        {
            var document = await collection
                .Find(Builders<BsonDocument>.Filter.Eq("_id", id))
                .SingleOrDefaultAsync(cancellationToken);
            if (document is not null)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new InvalidOperationException(
            $"Document '{id}' was not created in collection '{collectionName}' before the timeout.");
    }

    private static async Task<string> WaitForFileContainingAsync(
        string path,
        string expectedSubstring,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            if (File.Exists(path))
            {
                var content = await File.ReadAllTextAsync(path, cancellationToken);
                if (content.Contains(expectedSubstring, StringComparison.Ordinal))
                {
                    return content;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new InvalidOperationException(
            $"File '{path}' did not contain '{expectedSubstring}' before the timeout.");
    }

    private static async Task WaitForResourceFailureAsync(
        DistributedApplication app,
        string resourceName,
        CancellationToken cancellationToken)
    {
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));

        try
        {
            await foreach (var resourceEvent in notifications.WatchAsync(timeout.Token))
            {
                if (!string.Equals(resourceEvent.Resource.Name, resourceName, StringComparison.Ordinal))
                {
                    continue;
                }

                var stateText = resourceEvent.Snapshot.State?.Text;
                if (string.Equals(stateText, KnownResourceStates.FailedToStart, StringComparison.Ordinal) ||
                    ((string.Equals(stateText, KnownResourceStates.Exited, StringComparison.Ordinal) ||
                      string.Equals(stateText, KnownResourceStates.Finished, StringComparison.Ordinal)) &&
                     resourceEvent.Snapshot.ExitCode is { } exitCode &&
                     exitCode != 0))
                {
                    return;
                }
            }

            throw new InvalidOperationException(
                $"Resource '{resourceName}' stopped reporting state before a failure was observed.");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"Resource '{resourceName}' never reported a failed state.");
        }
    }

    private static async Task<object?> QueryPostgresAsync(string postgresqlUri, string sql, CancellationToken cancellationToken)
    {
        // Npgsql does not parse postgresql:// URIs; convert to key/value form.
        var uri = new Uri(postgresqlUri);
        var userInfo = uri.UserInfo.Split(':', 2);
        var database = uri.AbsolutePath.TrimStart('/');

        var keyValue = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port,
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : string.Empty,
            Database = string.IsNullOrEmpty(database) ? "postgres" : database,
            Timeout = 5,
            CommandTimeout = 5,
        }.ConnectionString;

        Exception? lastException = null;

        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                await using var connection = new NpgsqlConnection(keyValue);
                await connection.OpenAsync(cancellationToken);

                await using var command = new NpgsqlCommand(sql, connection);
                return await command.ExecuteScalarAsync(cancellationToken);
            }
            catch (NpgsqlException ex)
            {
                lastException = ex;
            }
            catch (SocketException ex)
            {
                lastException = ex;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new InvalidOperationException("PostgreSQL did not become reachable in time.", lastException);
    }

    private static int GetAvailableTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // The container may still hold handles on Windows; a temp directory is acceptable leakage.
        }
        catch (UnauthorizedAccessException)
        {
            // Files written by the container as root.
        }
    }
}
