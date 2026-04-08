// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Testing;
using Aspire.TestUtilities;
using MongoDB.Bson;
using MongoDB.Driver;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
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
    public async Task ConfiguredEndToEndAppAppliesAdditionalContainerOptions()
    {
        if (!RequiresDockerAttribute.IsSupported)
        {
            throw SkipException.ForSkip("Docker is required for DocumentDB end-to-end validation.");
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Aspire.Hosting.DocumentDB.ConfiguredEndToEndApp.Program>(cts.Token);
        await using var app = await appHost.BuildAsync(cts.Token);

        await app.StartAsync(cts.Token);

        var connectionString = await app.GetConnectionStringAsync("appdb", cts.Token);
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        const string databaseName = "appdb";
        var database = await ConnectAsync(connectionString!, databaseName, cts.Token);
        var collection = database.GetCollection<BsonDocument>("widgets");

        var seededDocument = await WaitForDocumentAsync(
            collection,
            Builders<BsonDocument>.Filter.Eq("_id", "seeded-widget"),
            cts.Token);
        Assert.NotNull(seededDocument);
        Assert.Equal("custom-init", seededDocument["source"].AsString);

        using var databaseNamesCursor = await database.Client.ListDatabaseNamesAsync(cancellationToken: cts.Token);
        var databaseNames = await databaseNamesCursor.ToListAsync(cts.Token);
        Assert.DoesNotContain("sampledb", databaseNames);

        using var certificate = await GetRemoteCertificateAsync(connectionString!, cts.Token);
        Assert.Contains("CN=Aspire.Hosting.DocumentDB.E2E", certificate.Subject, StringComparison.Ordinal);

        var id = ObjectId.GenerateNewId();
        var filter = Builders<BsonDocument>.Filter.Eq("_id", id);
        var document = new BsonDocument
        {
            ["_id"] = id,
            ["name"] = "configured-widget"
        };

        await collection.InsertOneAsync(document, cancellationToken: cts.Token);
        Assert.Equal(1, await collection.CountDocumentsAsync(filter, cancellationToken: cts.Token));
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

    private static async Task<X509Certificate2> GetRemoteCertificateAsync(string connectionString, CancellationToken cancellationToken)
    {
        var mongoUrl = MongoUrl.Create(connectionString);
        var host = mongoUrl.Server.Host;
        var port = mongoUrl.Server.Port;

        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(host, port, cancellationToken);

        using var sslStream = new SslStream(
            tcpClient.GetStream(),
            leaveInnerStreamOpen: false,
            userCertificateValidationCallback: static (_, _, _, _) => true);

        await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = host
        }, cancellationToken);

        return new X509Certificate2(sslStream.RemoteCertificate ?? throw new InvalidOperationException("Remote certificate was not available."));
    }

    private static async Task<BsonDocument?> WaitForDocumentAsync(
        IMongoCollection<BsonDocument> collection,
        FilterDefinition<BsonDocument> filter,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var document = await collection.Find(filter).SingleOrDefaultAsync(cancellationToken);
            if (document is not null)
            {
                return document;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        return null;
    }
}
