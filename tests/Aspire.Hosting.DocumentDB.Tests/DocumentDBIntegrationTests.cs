// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Testing;
using Aspire.TestUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;
using Xunit.Sdk;

namespace Aspire.Hosting.DocumentDB.Tests;

public class DocumentDBIntegrationTests
{
    [Fact]
    public async Task EndToEndAppCanInsertAndDeleteDocument()
    {
        if (!RequiresDockerAttribute.IsSupported)
        {
            throw SkipException.ForSkip("Docker is required for DocumentDB end-to-end validation.");
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Aspire.Hosting.DocumentDB.EndToEndApp.Program>(cts.Token);
        await using var app = await appHost.BuildAsync(cts.Token);

        await app.StartAsync(cts.Token);

        var healthCheckService = app.Services.GetRequiredService<HealthCheckService>();
        await WaitForHealthCheckAsync(healthCheckService, "documentdb_check", cts.Token);
        await WaitForHealthCheckAsync(healthCheckService, "appdb_check", cts.Token);

        var connectionString = await app.GetConnectionStringAsync("appdb", cts.Token);
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        const string databaseName = "appdb";
        var database = await ConnectAsync(connectionString!, databaseName, cts.Token);
        var collection = database.GetCollection<BsonDocument>("widgets");

        var id = ObjectId.GenerateNewId();
        var filter = Builders<BsonDocument>.Filter.Eq("_id", id);
        var document = new BsonDocument
        {
            ["_id"] = id,
            ["name"] = "widget"
        };

        await collection.InsertOneAsync(document, cancellationToken: cts.Token);
        Assert.Equal(1, await collection.CountDocumentsAsync(filter, cancellationToken: cts.Token));

        var deleteResult = await collection.DeleteOneAsync(filter, cancellationToken: cts.Token);
        Assert.Equal(1, deleteResult.DeletedCount);
        Assert.Equal(0, await collection.CountDocumentsAsync(filter, cancellationToken: cts.Token));
    }

    private static async Task<IMongoDatabase> ConnectAsync(string connectionString, string databaseName, CancellationToken cancellationToken)
    {
        var settings = MongoClientSettings.FromConnectionString(connectionString);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
        settings.ConnectTimeout = TimeSpan.FromSeconds(5);
        settings.SocketTimeout = TimeSpan.FromSeconds(5);

        var client = new MongoClient(settings);
        var database = client.GetDatabase(databaseName);

        Exception? lastException = null;

        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                await database.RunCommandAsync((Command<BsonDocument>)"{ ping: 1 }", cancellationToken: cancellationToken);
                return database;
            }
            catch (TimeoutException ex)
            {
                lastException = ex;
            }
            catch (MongoException ex)
            {
                lastException = ex;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new InvalidOperationException("DocumentDB did not become reachable in time.", lastException);
    }

    private static async Task WaitForHealthCheckAsync(HealthCheckService healthCheckService, string healthCheckKey, CancellationToken cancellationToken)
    {
        HealthReport? lastReport = null;

        for (var attempt = 0; attempt < 20; attempt++)
        {
            lastReport = await healthCheckService.CheckHealthAsync(
                registration => registration.Name == healthCheckKey,
                cancellationToken);

            if (lastReport.Entries.TryGetValue(healthCheckKey, out var entry) && entry.Status == HealthStatus.Healthy)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        var lastMessage = "The health check registration was not found.";
        if (lastReport is not null && lastReport.Entries.TryGetValue(healthCheckKey, out var lastEntry))
        {
            lastMessage = $"{lastEntry.Status}: {lastEntry.Description}";
        }

        throw new InvalidOperationException($"Health check '{healthCheckKey}' did not become healthy in time. Last result: {lastMessage}");
    }
}
