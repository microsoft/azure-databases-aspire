// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Aspire.TestUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Bson;
using MongoDB.Driver;
using Npgsql;
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

    [Fact]
    public async Task AdvancedConfigEndToEndAppAppliesAdditionalContainerOptions()
    {
        if (!RequiresDockerAttribute.IsSupported)
        {
            throw SkipException.ForSkip("Docker is required for DocumentDB end-to-end validation.");
        }

        using var cts = CreateEndToEndTimeoutSource();

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Aspire.Hosting.DocumentDB.AdvancedConfigEndToEndApp.Program>(cts.Token);
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

        var seededDocument = await WaitForDocumentAsync(
            collection,
            Builders<BsonDocument>.Filter.Eq("_id", "seeded-widget"),
            cts.Token);
        Assert.NotNull(seededDocument);
        Assert.Equal("custom-init", seededDocument!["source"].AsString);

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

    [Fact]
    public async Task PostgresConnectionStringResolvesAndAuthenticatesAgainstContainer()
    {
        // Regression for issue #71: prior to v0.112-0, the documentdb-local entrypoint
        // hard-coded the PostgreSQL admin role to docdb_admin/Admin100, so the
        // postgresql:// connection string Aspire produces (which uses the gateway's
        // USERNAME/PASSWORD) silently failed authentication. v0.112-0 makes the
        // entrypoint honour USERNAME/PASSWORD on the PG admin role. This test pins
        // the fix end-to-end: opening an Npgsql connection with the Aspire-resolved
        // credentials must succeed, and current_user must report 'admin' (NOT the
        // legacy docdb_admin).
        //
        // The AppHost (Aspire.Hosting.DocumentDB.PostgresEndToEndApp) pins the
        // image to pg17-0.112.0 explicitly so the test does not depend on
        // DocumentDBVersions.Latest being bumped to >= 0.112.0 (tracked by #70).
        if (!RequiresDockerAttribute.IsSupported)
        {
            throw SkipException.ForSkip("Docker is required for DocumentDB end-to-end validation.");
        }

        using var cts = CreateEndToEndTimeoutSource();

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Aspire.Hosting.DocumentDB.PostgresEndToEndApp.Program>(cts.Token);
        await using var app = await appHost.BuildAsync(cts.Token);
        await app.StartAsync(cts.Token);

        var healthCheckService = app.Services.GetRequiredService<HealthCheckService>();
        await WaitForHealthCheckAsync(healthCheckService, "documentdb_check", cts.Token);

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var documentDB = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());
        var connectionString = await documentDB.PostgresConnectionStringExpression.GetValueAsync(cts.Token);
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        // PostgreSQL backend may take a moment to come up after the MongoDB gateway
        // is healthy; retry mirroring the Mongo ConnectAsync pattern above.
        var (selectOneResult, currentUser) = await OpenPostgresAndRunSmokeTestsAsync(connectionString!, cts.Token);

        Assert.Equal(1, selectOneResult);
        Assert.Equal("admin", currentUser);
    }

    private static async Task<(int SelectOneResult, string CurrentUser)> OpenPostgresAndRunSmokeTestsAsync(
        string postgresqlUri,
        CancellationToken cancellationToken)
    {
        // Npgsql (verified against 9.0.5) does NOT parse postgresql:// URIs: both
        // NpgsqlConnectionStringBuilder(uri) and new NpgsqlConnection(uri) throw
        // ArgumentException at construct time. Convert the URI to key-value form.
        var keyValueConnectionString = ConvertPostgresUriToKeyValue(postgresqlUri);

        Exception? lastException = null;

        for (var attempt = 0; attempt < 15; attempt++)
        {
            try
            {
                await using var conn = new NpgsqlConnection(keyValueConnectionString);
                await conn.OpenAsync(cancellationToken);

                await using var selectOne = new NpgsqlCommand("SELECT 1", conn);
                var selectOneResult = Convert.ToInt32(await selectOne.ExecuteScalarAsync(cancellationToken));

                await using var currentUser = new NpgsqlCommand("SELECT current_user", conn);
                var currentUserResult = (string)(await currentUser.ExecuteScalarAsync(cancellationToken))!;

                return (selectOneResult, currentUserResult);
            }
            catch (NpgsqlException ex)
            {
                lastException = ex;
            }
            catch (SocketException ex)
            {
                lastException = ex;
            }
            catch (TimeoutException ex)
            {
                lastException = ex;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new InvalidOperationException(
            "PostgreSQL did not become reachable / did not authenticate the Aspire-resolved credentials in time. " +
            "If this fails with '28P01: password authentication failed', the documentdb-local image is older " +
            "than v0.112-0, in which case WithPostgresEndpoint() should have blocked startup.",
            lastException);
    }

    /// <summary>
    /// Converts a <c>postgresql://user:password@host:port/database</c> URI (the form
    /// emitted by <see cref="DocumentDBServerResource.PostgresConnectionStringExpression"/>)
    /// into the key=value form that Npgsql 9.x requires.
    /// </summary>
    private static string ConvertPostgresUriToKeyValue(string postgresqlUri)
    {
        var uri = new Uri(postgresqlUri);
        var userInfo = uri.UserInfo.Split(':', 2);
        var user = Uri.UnescapeDataString(userInfo[0]);
        var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : string.Empty;
        var database = uri.AbsolutePath.TrimStart('/');

        return new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port,
            Username = user,
            Password = password,
            Database = string.IsNullOrEmpty(database) ? "postgres" : database,
            Timeout = 5,
            CommandTimeout = 5,
        }.ConnectionString;
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
