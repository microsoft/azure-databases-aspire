// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Sockets;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Aspire.TestUtilities;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;
using Xunit.Sdk;

namespace Aspire.Hosting.DocumentDB.Tests;

[Trait("Category", "Integration")]
public class DocumentDBIntegrationTests
{
    private const string EndToEndTimeoutEnvironmentVariable = "DOCUMENTDB_E2E_TIMEOUT_SECONDS";
    private static readonly TimeSpan DefaultEndToEndTimeout = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task EndToEndAppCanInsertAndDeleteDocument()
    {
        if (!RequiresDockerAttribute.IsSupported)
        {
            throw SkipException.ForSkip("Docker is required for DocumentDB end-to-end validation.");
        }

        using var cts = CreateEndToEndTimeoutSource();

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Aspire.Hosting.DocumentDB.EndToEndApp.Program>(cts.Token);
        await using var app = await appHost.BuildAsync(cts.Token);

        await app.StartAsync(cts.Token);

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

    [Fact]
    public async Task ConfiguredEndToEndAppCanInsertAndDeleteDocument()
    {
        if (!RequiresDockerAttribute.IsSupported)
        {
            throw SkipException.ForSkip("Docker is required for DocumentDB end-to-end validation.");
        }

        using var cts = CreateEndToEndTimeoutSource();

        var configuredHostPort = GetAvailableTcpPort();
        var previousConfiguredHostPort = Environment.GetEnvironmentVariable(Aspire.Hosting.DocumentDB.ConfiguredEndToEndApp.Program.HostPortEnvironmentVariable);
        Environment.SetEnvironmentVariable(
            Aspire.Hosting.DocumentDB.ConfiguredEndToEndApp.Program.HostPortEnvironmentVariable,
            configuredHostPort.ToString());

        try
        {
            var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Aspire.Hosting.DocumentDB.ConfiguredEndToEndApp.Program>(cts.Token);
            await using var app = await appHost.BuildAsync(cts.Token);

            var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
            var serverResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());
            var endpoint = Assert.Single(serverResource.Annotations.OfType<EndpointAnnotation>());
            Assert.Equal(configuredHostPort, endpoint.Port);

            await app.StartAsync(cts.Token);

            var connectionString = await app.GetConnectionStringAsync("configureddb", cts.Token);
            Assert.False(string.IsNullOrWhiteSpace(connectionString));

            const string databaseName = "configureddb";
            var database = await ConnectAsync(connectionString!, databaseName, cts.Token);
            var collection = database.GetCollection<BsonDocument>("items");

            var id = ObjectId.GenerateNewId();
            var filter = Builders<BsonDocument>.Filter.Eq("_id", id);
            var document = new BsonDocument
            {
                ["_id"] = id,
                ["name"] = "configured-item"
            };

            await collection.InsertOneAsync(document, cancellationToken: cts.Token);
            Assert.Equal(1, await collection.CountDocumentsAsync(filter, cancellationToken: cts.Token));

            var deleteResult = await collection.DeleteOneAsync(filter, cancellationToken: cts.Token);
            Assert.Equal(1, deleteResult.DeletedCount);
            Assert.Equal(0, await collection.CountDocumentsAsync(filter, cancellationToken: cts.Token));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                Aspire.Hosting.DocumentDB.ConfiguredEndToEndApp.Program.HostPortEnvironmentVariable,
                previousConfiguredHostPort);
        }
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

    private static int GetAvailableTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static CancellationTokenSource CreateEndToEndTimeoutSource() => new(GetEndToEndTimeout());

    private static TimeSpan GetEndToEndTimeout()
    {
        var configuredTimeout = Environment.GetEnvironmentVariable(EndToEndTimeoutEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredTimeout))
        {
            return DefaultEndToEndTimeout;
        }

        if (!int.TryParse(configuredTimeout, out var timeoutSeconds) || timeoutSeconds <= 0)
        {
            throw new InvalidOperationException(
                $"{EndToEndTimeoutEnvironmentVariable} must be a positive integer number of seconds, but was '{configuredTimeout}'.");
        }

        return TimeSpan.FromSeconds(timeoutSeconds);
    }
}
