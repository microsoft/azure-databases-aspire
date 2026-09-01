// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Sockets;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
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
    private const string CandidateVersion = "0.116.0";

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
                var containerStartedAt = await GetContainerStartedAtAsync(containerId);
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
                       (AppHost.ImageTagEnvironmentVariable, CandidateTag(17))))
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
                    new BsonDocument { ["_id"] = "post-upgrade", ["version"] = CandidateVersion },
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
                       (AppHost.ImageTagEnvironmentVariable, CandidateTag(17))))
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
                       (AppHost.ImageTagEnvironmentVariable, CandidateTag(17))))
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
    [InlineData(AppHost.Pg15Scenario, "pg15-")]
    [InlineData(AppHost.Pg16Scenario, "pg16-")]
    public async Task EveryCurrentPostgresVariantResolvesToARealImageAndServesTraffic(
        string scenarioName,
        string expectedTagPrefix)
    {
        // Pg17 (the default) and Pg18 are covered elsewhere; together these four mean every
        // member of DocumentDBPostgresVersion is proven to name an image that exists and runs,
        // not merely a well-formed tag.
        RequireDocker();

        using var cts = CreateEndToEndTimeoutSource();
        using var scenario = new EnvironmentScope((AppHost.ScenarioEnvironmentVariable, scenarioName));

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<AppHost>(cts.Token);
        await using var app = await appHost.BuildAsync(cts.Token);

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var server = Assert.Single(Snapshot<DocumentDBServerResource>(appModel.Resources));
        var image = Assert.Single(Snapshot<ContainerImageAnnotation>(server.Annotations));
        Assert.StartsWith(expectedTagPrefix, image.Tag, StringComparison.Ordinal);

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
        var candidateTag = CandidateTag(postgresVersion);
        using var scenario = new EnvironmentScope(
            (AppHost.ScenarioEnvironmentVariable, scenarioName),
            (AppHost.ImageTagEnvironmentVariable, candidateTag));

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<AppHost>(cts.Token);
        await using var app = await appHost.BuildAsync(cts.Token);

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var server = Assert.Single(Snapshot<DocumentDBServerResource>(appModel.Resources));
        var image = Assert.Single(Snapshot<ContainerImageAnnotation>(server.Annotations));
        Assert.Equal(candidateTag, image.Tag);

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
    public async Task LogLevelOwnerAndOpenTelemetryMetricsReachTheCurrentContainer()
    {
        RequireDocker();

        using var cts = CreateEndToEndTimeoutSource();
        var otelEndpoint = "http://localhost:4317";

        using var scenario = new EnvironmentScope(
            (AppHost.ScenarioEnvironmentVariable, AppHost.ObservableConfigScenario),
            (AppHost.OtelEndpointEnvironmentVariable, otelEndpoint));

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
        Assert.Equal("debug", environment["LOG_LEVEL"], ignoreCase: true);
        Assert.Equal("aspireowner", environment["OWNER"]);

        var logs = await WaitForContainerLogAsync(containerId, "Using owner: aspireowner", cts.Token);
        Assert.Contains("Using owner: aspireowner", logs, StringComparison.Ordinal);
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
            (AppHost.ImageTagEnvironmentVariable, CandidateTag(17)));

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
            Assert.Equal("debug", environment["LOG_LEVEL"], ignoreCase: true);

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

    [Fact]
    public async Task WithoutUserCreationLeavesTheContainerWithoutTheConfiguredUser()
    {
        // The container is told not to provision the user, while the connection string still
        // carries those credentials - so the documented effect is an authentication failure, and
        // the health check never goes healthy.
        RequireDocker();

        using var cts = CreateEndToEndTimeoutSource();
        using var scenario = new EnvironmentScope(
            (AppHost.ScenarioEnvironmentVariable, AppHost.WithoutUserCreationScenario));

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<AppHost>(cts.Token);
        await using var app = await appHost.BuildAsync(cts.Token);

        await app.StartAsync(cts.Token);

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var server = Assert.Single(Snapshot<DocumentDBServerResource>(appModel.Resources));

        var containerId = await GetContainerIdAsync(app, server.Name, cts.Token);
        var environment = await GetContainerEnvironmentAsync(containerId);
        Assert.Equal("false", environment["CREATE_USER"]);

        var connectionString = await app.GetConnectionStringAsync("appdb", cts.Token);

        // Not the end-to-end token: a cancelled run would throw OperationCanceledException and
        // make this assertion pass without the container having refused anything.
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
    public async Task CurrentPostgresEndpointHonoursAnExplicitPortAndWithoutExtendedRumDisablesTheAccessMethod()
    {
        await AssertPostgresExtrasAsync(imageTag: null, assertLz4: false);
    }

    [Fact]
    public async Task PostgresEndpointOn0116UsesLz4ToastCompression()
    {
        await AssertPostgresExtrasAsync(CandidateTag(17), assertLz4: true);
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
            (AppHost.ImageTagEnvironmentVariable, CandidateTag(17)));

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

    private static async Task<string> WaitForContainerLogAsync(
        string containerId,
        string expectedSubstring,
        CancellationToken cancellationToken)
    {
        var logs = string.Empty;

        for (var attempt = 0; attempt < 60; attempt++)
        {
            logs = await GetContainerLogsAsync(containerId);

            if (logs.Contains(expectedSubstring, StringComparison.Ordinal))
            {
                return logs;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new InvalidOperationException(
            $"Container '{containerId}' did not log '{expectedSubstring}' before the timeout. " +
            $"Last logs:{Environment.NewLine}{logs}");
    }

    private static async Task<DistributedApplication> BuildAndStartAsync(CancellationToken cancellationToken)
    {
        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<AppHost>(cancellationToken);
        var app = await appHost.BuildAsync(cancellationToken);
        await app.StartAsync(cancellationToken);
        return app;
    }

    private static string CandidateTag(int postgresVersion) => $"pg{postgresVersion}-{CandidateVersion}";

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
