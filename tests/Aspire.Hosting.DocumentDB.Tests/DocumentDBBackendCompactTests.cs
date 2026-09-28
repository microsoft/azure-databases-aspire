// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;
using static Aspire.Hosting.DocumentDB.Tests.DocumentDBEndToEndSupport;
using AppHost = Aspire.Hosting.DocumentDB.FeatureMatrixEndToEndApp.Program;
using FeatureMatrix = Aspire.Hosting.DocumentDB.Tests.DocumentDBFeatureMatrixEndToEndTests;

namespace Aspire.Hosting.DocumentDB.Tests;

/// <summary>
/// The latest version on each non-default PostgreSQL backend, with the features that depend on
/// the backend at once: PostgreSQL endpoint, named-volume persistence, initialization and OTLP
/// metrics. The integration suite exercises these on PG17 only.
/// </summary>
/// <remarks>CI runs one backend per leg through <see cref="PostgresEnvironmentVariable"/>; unset, all run.</remarks>
[Trait("Category", "Backend")]
[Collection(DocumentDBFeatureMatrixAppHostCollection.Name)]
public class DocumentDBBackendCompactTests
{
    internal const string PostgresEnvironmentVariable = "DOCUMENTDB_BACKEND_PG";

    internal static readonly DocumentDBPostgresVersion[] Backends =
        [DocumentDBPostgresVersion.Pg15, DocumentDBPostgresVersion.Pg16, DocumentDBPostgresVersion.Pg18];

    public static TheoryData<DocumentDBPostgresVersion> SelectedBackends()
    {
        var selected = Environment.GetEnvironmentVariable(PostgresEnvironmentVariable);
        var backends = string.IsNullOrWhiteSpace(selected)
            ? Backends
            : Backends.Where(backend => ((int)backend).ToString() == selected).ToArray();

        return backends.Length > 0
            ? [.. backends]
            : throw new InvalidOperationException(
                $"{PostgresEnvironmentVariable} must be one of {string.Join(", ", Backends.Select(b => (int)b))}, but was '{selected}'.");
    }

    [Theory]
    [MemberData(nameof(SelectedBackends))]
    public async Task LatestVersionPersistsInitializesAndExportsMetrics(DocumentDBPostgresVersion postgres)
    {
        RequireDocker();

        using var cts = CreateEndToEndTimeoutSource();
        var run = Guid.NewGuid().ToString("N");
        var volumeName = $"aspire-documentdb-backend-{run}";
        var initDataPath = Path.Combine(Path.GetTempPath(), "aspire-documentdb-backend-init", run);
        var otelOutputPath = Path.Combine(Path.GetTempPath(), "aspire-documentdb-backend-otel", run);
        Directory.CreateDirectory(initDataPath);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(initDataPath, "01-seed.js"), """
                db = db.getSiblingDB("appdb");
                db.seed.insertOne({ _id: "seeded" });
                """, cts.Token);

            using var scenario = new EnvironmentScope(
                (AppHost.ScenarioEnvironmentVariable, AppHost.BackendCompactScenario),
                (AppHost.PostgresVersionEnvironmentVariable, postgres.ToString()),
                (AppHost.VolumeNameEnvironmentVariable, volumeName),
                (AppHost.InitDataPathEnvironmentVariable, initDataPath),
                (AppHost.OtelOutputPathEnvironmentVariable, otelOutputPath));

            await using (var app = await FeatureMatrix.BuildAndStartAsync(cts.Token))
            {
                var server = await WaitUntilHealthyAsync(app, postgres, cts.Token);

                var connectionString = await app.GetConnectionStringAsync("appdb", cts.Token);
                var database = await ConnectAsync(connectionString!, "appdb", cts.Token);
                await FeatureMatrix.WaitForDocumentAsync(database, "seed", "seeded", cts.Token);
                await database.GetCollection<BsonDocument>("persisted").InsertOneAsync(
                    new BsonDocument { ["_id"] = "survivor" },
                    cancellationToken: cts.Token);

                var postgresUri = await server.PostgresConnectionStringExpression.GetValueAsync(cts.Token);
                var serverVersion = await FeatureMatrix.QueryPostgresAsync(postgresUri!, "SHOW server_version_num", cts.Token);
                Assert.Equal((int)postgres, int.Parse(Convert.ToString(serverVersion)!) / 10000);

                var metrics = await FeatureMatrix.WaitForFileContainingAsync(
                    Path.Combine(otelOutputPath, "metrics.json"),
                    "aspire-documentdb-backend",
                    cts.Token);
                Assert.Contains("gateway", metrics, StringComparison.OrdinalIgnoreCase);

                await app.StopAsync(cts.Token);
            }

            // A new container on the same volume serves what the first one wrote.
            await using (var app = await FeatureMatrix.BuildAndStartAsync(cts.Token))
            {
                await WaitUntilHealthyAsync(app, postgres, cts.Token);

                var connectionString = await app.GetConnectionStringAsync("appdb", cts.Token);
                var database = await ConnectAsync(connectionString!, "appdb", cts.Token);
                foreach (var (collection, id) in new[] { ("persisted", "survivor"), ("seed", "seeded") })
                {
                    Assert.NotNull(await database.GetCollection<BsonDocument>(collection)
                        .Find(Builders<BsonDocument>.Filter.Eq("_id", id))
                        .SingleOrDefaultAsync(cts.Token));
                }

                await app.StopAsync(cts.Token);
            }
        }
        finally
        {
            await RemoveVolumeAsync(volumeName);
            FeatureMatrix.TryDeleteDirectory(initDataPath);
            FeatureMatrix.TryDeleteDirectory(otelOutputPath);
        }
    }

    private static async Task<DocumentDBServerResource> WaitUntilHealthyAsync(
        DistributedApplication app,
        DocumentDBPostgresVersion postgres,
        CancellationToken cancellationToken)
    {
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var server = Assert.Single(Snapshot<DocumentDBServerResource>(appModel.Resources));
        var image = Assert.Single(Snapshot<ContainerImageAnnotation>(server.Annotations));
        Assert.Equal($"pg{(int)postgres}-{DocumentDBVersions.Latest}", image.Tag);

        await WaitForHealthCheckAsync(app.Services.GetRequiredService<HealthCheckService>(), "documentdb_check", cancellationToken);
        return server;
    }
}
