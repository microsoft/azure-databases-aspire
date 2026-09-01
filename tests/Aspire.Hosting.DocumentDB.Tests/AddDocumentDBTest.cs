// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aspire.Hosting.DocumentDB.Tests;

[Trait("Category", "Unit")]
public class AddDocumentDBTests
{
    // The first curated tag whose image declares /data as a container VOLUME and whose entrypoint
    // claims the data directory with an exclusive lock. Pinned explicitly so these tests describe
    // that image's behaviour regardless of which version the package currently defaults to.
    private const string InterlockedTag = "pg17-0.116.0";
    private const string GatewayConfigurationShell = "/bin/bash";
    private const string GatewayConfigurationShellArgumentZero = "--";
    private const string GatewayEntrypointScriptPath = "/home/documentdb/gateway/scripts/emulator_entrypoint.sh";

    /// <summary>
    /// The metrics object the wrapper always removes whole: this package owns the metrics signal,
    /// and any surviving key - including one a later gateway release adds - would re-pin a setting
    /// ahead of the documented environment precedence.
    /// </summary>
    private const string MetricsBlockFilter = "del(.TelemetryOptions.Metrics";

    [Fact]
    public void CombineStandardOutputAndErrorPreservesStreamBoundary()
    {
        Assert.Equal(
            $"stdout{Environment.NewLine}stderr",
            DocumentDBEndToEndSupport.CombineStandardOutputAndError("stdout", "stderr"));
        Assert.Equal(
            $"stdout{Environment.NewLine}stderr",
            DocumentDBEndToEndSupport.CombineStandardOutputAndError($"stdout{Environment.NewLine}", "stderr"));
        Assert.Equal(
            "stdout",
            DocumentDBEndToEndSupport.CombineStandardOutputAndError("stdout", string.Empty));
        Assert.Equal(
            "stderr",
            DocumentDBEndToEndSupport.CombineStandardOutputAndError(string.Empty, "stderr"));
    }

    [Fact]
    public void NormalizeContainerLogsRemovesAnsiEscapeSequences()
    {
        var logs = "\u001B[2m2026-09-01T20:00:00Z\u001B[0m \u001B[34mDEBUG\u001B[0m documentdb_api.insert";

        Assert.Equal(
            "2026-09-01T20:00:00Z DEBUG documentdb_api.insert",
            DocumentDBEndToEndSupport.NormalizeContainerLogs(logs));
    }

    [Fact]
    public void AddDocumentDBAddsHealthCheckAnnotationToResource()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var documentDB = appBuilder.AddDocumentDB("documentdb");

        Assert.Single(documentDB.Resource.Annotations, a => a is HealthCheckAnnotation hca && hca.Key == "documentdb_check");
    }

    [Fact]
    public void AddDatabaseAddsHealthCheckAnnotationToResource()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var database = appBuilder.AddDocumentDB("documentdb")
            .AddDatabase("appdb");

        Assert.Single(database.Resource.Annotations, a => a is HealthCheckAnnotation hca && hca.Key == "appdb_check");
    }

    [Fact]
    public void AddDocumentDBRegistersServerAndDatabaseHealthChecks()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("documentdb")
            .AddDatabase("appdb");

        using var app = appBuilder.Build();

        var healthCheckOptions = app.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;

        Assert.Contains(healthCheckOptions.Registrations, registration => registration.Name == "documentdb_check");
        Assert.Contains(healthCheckOptions.Registrations, registration => registration.Name == "appdb_check");
        Assert.NotNull(app.Services.GetRequiredService<HealthCheckService>());
    }

    [Fact]
    public void AddDocumentDBContainerWithDefaultsAddsAnnotationMetadata()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.AddDocumentDB("DocumentDB");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());
        Assert.Equal("DocumentDB", containerResource.Name);

        var endpoint = Assert.Single(containerResource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(10260, endpoint.TargetPort);
        Assert.False(endpoint.IsExternal);
        Assert.Equal("tcp", endpoint.Name);
        Assert.Null(endpoint.Port);
        Assert.Equal(ProtocolType.Tcp, endpoint.Protocol);
        Assert.Equal("tcp", endpoint.Transport);
        Assert.Equal("tcp", endpoint.UriScheme);

        var containerAnnotation = Assert.Single(containerResource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal(DocumentDBContainerImageTags.Tag, containerAnnotation.Tag);
        Assert.Equal(DocumentDBContainerImageTags.Image, containerAnnotation.Image);
        Assert.Equal(DocumentDBContainerImageTags.Registry, containerAnnotation.Registry);
    }

    [Fact]
    public void AddDocumentDBContainerAddsAnnotationMetadata()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB", 10261);

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());
        Assert.Equal("DocumentDB", containerResource.Name);

        var endpoint = Assert.Single(containerResource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(10260, endpoint.TargetPort);
        Assert.False(endpoint.IsExternal);
        Assert.Equal("tcp", endpoint.Name);
        Assert.Equal(10261, endpoint.Port);
        Assert.Equal(ProtocolType.Tcp, endpoint.Protocol);
        Assert.Equal("tcp", endpoint.Transport);
        Assert.Equal("tcp", endpoint.UriScheme);

        var containerAnnotation = Assert.Single(containerResource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal(DocumentDBContainerImageTags.Tag, containerAnnotation.Tag);
        Assert.Equal(DocumentDBContainerImageTags.Image, containerAnnotation.Image);
        Assert.Equal(DocumentDBContainerImageTags.Registry, containerAnnotation.Registry);
    }

    [Fact]
    public void WithHostPortUpdatesExistingTcpEndpoint()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithHostPort(10261);

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());
        var endpoint = Assert.Single(containerResource.Annotations.OfType<EndpointAnnotation>());

        Assert.Equal("tcp", endpoint.Name);
        Assert.Equal(10261, endpoint.Port);
        Assert.Equal(10260, endpoint.TargetPort);
    }

    [Fact]
    public async Task DocumentDBCreatesConnectionString()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder
            .AddDocumentDB("DocumentDB")
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 10260))
            .AddDatabase("mydatabase");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var dbResource = Assert.Single(appModel.Resources.OfType<DocumentDBDatabaseResource>());
        var serverResource = Assert.IsAssignableFrom<IResourceWithConnectionString>(dbResource.Parent);
        var connectionStringResource = Assert.IsAssignableFrom<IResourceWithConnectionString>(dbResource);
        var passwordParameter = Assert.IsType<ParameterResource>(dbResource.Parent.PasswordParameter);
        var password = await passwordParameter.GetValueAsync(default);
        var serverConnectionString = await serverResource.GetConnectionStringAsync();
        var connectionString = await connectionStringResource.GetConnectionStringAsync();
        Assert.NotNull(password);
        Assert.NotNull(serverConnectionString);
        Assert.NotNull(connectionString);

        AssertConnectionString(
            serverConnectionString!,
            expectedDatabaseName: null,
            expectedPassword: password!,
            ("authSource", "admin"),
            ("authMechanism", "SCRAM-SHA-256"),
            ("tls", "true"),
            ("tlsInsecure", "true"));
        AssertConnectionStringExpression(
            serverResource.ConnectionStringExpression.ValueExpression,
            resourceName: "DocumentDB",
            expectedDatabaseName: null,
            ("authSource", "admin"),
            ("authMechanism", "SCRAM-SHA-256"),
            ("tls", "true"),
            ("tlsInsecure", "true"));

        AssertConnectionString(
            connectionString!,
            expectedDatabaseName: "mydatabase",
            expectedPassword: password!,
            ("authSource", "admin"),
            ("authMechanism", "SCRAM-SHA-256"),
            ("tls", "true"),
            ("tlsInsecure", "true"));
        AssertConnectionStringExpression(
            connectionStringResource.ConnectionStringExpression.ValueExpression,
            resourceName: "DocumentDB",
            expectedDatabaseName: "mydatabase",
            ("authSource", "admin"),
            ("authMechanism", "SCRAM-SHA-256"),
            ("tls", "true"),
            ("tlsInsecure", "true"));
    }

    [Fact]
    public async Task VerifyManifest()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var DocumentDB = appBuilder.AddDocumentDB("DocumentDB");
        var db = DocumentDB.AddDatabase("mydb");

        var DocumentDBManifest = await ManifestUtils.GetManifest(DocumentDB.Resource);
        var dbManifest = await ManifestUtils.GetManifest(db.Resource);

        // Credentials are percent-encoded for the URI, so the published connection string points at
        // the "-uri-encoded" companion that Aspire derives from the password parameter. The
        // container's PASSWORD environment variable keeps referencing the raw parameter.
        var expectedManifest = $$"""
            {
              "type": "container.v0",
              "connectionString": "mongodb://admin:{DocumentDB-password-uri-encoded.value}@{DocumentDB.bindings.tcp.host}:{DocumentDB.bindings.tcp.port}?authSource=admin\u0026authMechanism=SCRAM-SHA-256\u0026tls=true\u0026tlsInsecure=true",
              "image": "{{DocumentDBContainerImageTags.Registry}}/{{DocumentDBContainerImageTags.Image}}:{{DocumentDBContainerImageTags.Tag}}",
              "env": {
                "USERNAME": "admin",
                "PASSWORD": "{DocumentDB-password.value}"
              },
              "bindings": {
                "tcp": {
                  "scheme": "tcp",
                  "protocol": "tcp",
                  "transport": "tcp",
                  "targetPort": 10260
                }
              }
            }
            """;
        Assert.Equal(expectedManifest, DocumentDBManifest.ToString());

        expectedManifest = """
            {
              "type": "value.v0",
              "connectionString": "mongodb://admin:{DocumentDB-password-uri-encoded.value}@{DocumentDB.bindings.tcp.host}:{DocumentDB.bindings.tcp.port}/mydb?authSource=admin\u0026authMechanism=SCRAM-SHA-256\u0026tls=true\u0026tlsInsecure=true"
            }
            """;
        Assert.Equal(expectedManifest, dbManifest.ToString());
    }

    [Fact]
    public void ThrowsWithIdenticalChildResourceNames()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var db = builder.AddDocumentDB("DocumentDB1");
        db.AddDatabase("db");

        Assert.Throws<DistributedApplicationException>(() => db.AddDatabase("db"));
    }

    [Fact]
    public void ThrowsWithIdenticalChildResourceNamesDifferentParents()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        builder.AddDocumentDB("DocumentDB1")
            .AddDatabase("db");

        var db = builder.AddDocumentDB("DocumentDB2");
        Assert.Throws<DistributedApplicationException>(() => db.AddDatabase("db"));
    }

    [Fact]
    public void CanAddDatabasesWithDifferentNamesOnSingleServer()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var DocumentDB1 = builder.AddDocumentDB("DocumentDB1");

        var db1 = DocumentDB1.AddDatabase("db1", "customers1");
        var db2 = DocumentDB1.AddDatabase("db2", "customers2");

        Assert.Equal("customers1", db1.Resource.DatabaseName);
        Assert.Equal("customers2", db2.Resource.DatabaseName);

        AssertConnectionStringExpression(
            db1.Resource.ConnectionStringExpression.ValueExpression,
            resourceName: "DocumentDB1",
            expectedDatabaseName: "customers1",
            ("authSource", "admin"),
            ("authMechanism", "SCRAM-SHA-256"),
            ("tls", "true"),
            ("tlsInsecure", "true"));
        AssertConnectionStringExpression(
            db2.Resource.ConnectionStringExpression.ValueExpression,
            resourceName: "DocumentDB1",
            expectedDatabaseName: "customers2",
            ("authSource", "admin"),
            ("authMechanism", "SCRAM-SHA-256"),
            ("tls", "true"),
            ("tlsInsecure", "true"));
    }

    [Fact]
    public void CanAddDatabasesWithTheSameNameOnMultipleServers()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var db1 = builder.AddDocumentDB("DocumentDB1")
            .AddDatabase("db1", "imports");

        var db2 = builder.AddDocumentDB("DocumentDB2")
            .AddDatabase("db2", "imports");

        Assert.Equal("imports", db1.Resource.DatabaseName);
        Assert.Equal("imports", db2.Resource.DatabaseName);

        AssertConnectionStringExpression(
            db1.Resource.ConnectionStringExpression.ValueExpression,
            resourceName: "DocumentDB1",
            expectedDatabaseName: "imports",
            ("authSource", "admin"),
            ("authMechanism", "SCRAM-SHA-256"),
            ("tls", "true"),
            ("tlsInsecure", "true"));
        AssertConnectionStringExpression(
            db2.Resource.ConnectionStringExpression.ValueExpression,
            resourceName: "DocumentDB2",
            expectedDatabaseName: "imports",
            ("authSource", "admin"),
            ("authMechanism", "SCRAM-SHA-256"),
            ("tls", "true"),
            ("tlsInsecure", "true"));
    }

    [Fact]
    public async Task ConnectionStringOmitsTlsWhenTlsDisabled()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder
            .AddDocumentDB("DocumentDB")
            .UseTls(false)
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 10260));

        using var app = appBuilder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var serverResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());
        var passwordParameter = Assert.IsType<ParameterResource>(serverResource.PasswordParameter);
        var password = await passwordParameter.GetValueAsync(default);
        var serverConnectionString = await ((IResourceWithConnectionString)serverResource).GetConnectionStringAsync();
        Assert.NotNull(password);
        Assert.NotNull(serverConnectionString);
        var queryParameters = AssertConnectionString(
            serverConnectionString!,
            expectedDatabaseName: null,
            expectedPassword: password!,
            ("authSource", "admin"),
            ("authMechanism", "SCRAM-SHA-256"));

        Assert.False(queryParameters.ContainsKey("tls"));
        Assert.False(queryParameters.ContainsKey("tlsInsecure"));
    }

    [Fact]
    public async Task ConnectionStringOmitsTlsInsecureWhenDisabled()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder
            .AddDocumentDB("DocumentDB")
            .AllowInsecureTls(false)
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 10260));

        using var app = appBuilder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var serverResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());
        var passwordParameter = Assert.IsType<ParameterResource>(serverResource.PasswordParameter);
        var password = await passwordParameter.GetValueAsync(default);
        var serverConnectionString = await ((IResourceWithConnectionString)serverResource).GetConnectionStringAsync();
        Assert.NotNull(password);
        Assert.NotNull(serverConnectionString);
        var queryParameters = AssertConnectionString(
            serverConnectionString!,
            expectedDatabaseName: null,
            expectedPassword: password!,
            ("authSource", "admin"),
            ("authMechanism", "SCRAM-SHA-256"),
            ("tls", "true"));

        Assert.False(queryParameters.ContainsKey("tlsInsecure"));
    }

    [Fact]
    public async Task ConnectionStringWithTlsAndInsecureTlsBothDisabled()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder
            .AddDocumentDB("DocumentDB")
            .UseTls(false)
            .AllowInsecureTls(false)
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 10260));

        using var app = appBuilder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var serverResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());
        var passwordParameter = Assert.IsType<ParameterResource>(serverResource.PasswordParameter);
        var password = await passwordParameter.GetValueAsync(default);
        var serverConnectionString = await ((IResourceWithConnectionString)serverResource).GetConnectionStringAsync();
        Assert.NotNull(password);
        Assert.NotNull(serverConnectionString);
        var queryParameters = AssertConnectionString(
            serverConnectionString!,
            expectedDatabaseName: null,
            expectedPassword: password!,
            ("authSource", "admin"),
            ("authMechanism", "SCRAM-SHA-256"));

        Assert.False(queryParameters.ContainsKey("tls"));
        Assert.False(queryParameters.ContainsKey("tlsInsecure"));
    }

    [Fact]
    public async Task WithDataVolumeAddsVolumeAnnotationAndDataPathEnv()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder
            .AddDocumentDB("DocumentDB")
            .WithDataVolume();

        using var app = appBuilder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());
        var volumeAnnotation = Assert.Single(containerResource.Annotations.OfType<ContainerMountAnnotation>().Where(a => a.Type == ContainerMountType.Volume));
        Assert.Equal("/data", volumeAnnotation.Target);
        Assert.False(volumeAnnotation.IsReadOnly);

        var env = await BuildEnvironmentVariablesAsync(containerResource);
        var dataPath = Assert.Single(env.Where(entry => entry.Key == "DATA_PATH"));
        Assert.Equal("/data", dataPath.Value);
    }

    [Fact]
    public async Task WithDataVolumeUsesCustomTargetPath()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder
            .AddDocumentDB("DocumentDB")
            .WithDataVolume(targetPath: "/custom/data/path");

        using var app = appBuilder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());
        var volumeAnnotation = Assert.Single(containerResource.Annotations.OfType<ContainerMountAnnotation>().Where(a => a.Type == ContainerMountType.Volume));
        Assert.Equal("/custom/data/path", volumeAnnotation.Target);

        var env = await BuildEnvironmentVariablesAsync(containerResource);
        var dataPath = Assert.Single(env.Where(entry => entry.Key == "DATA_PATH"));
        Assert.Equal("/custom/data/path", dataPath.Value);
    }

    // ---------------------------------------------------------------------
    // Data-directory storage safeguards (DocumentDB 0.116.0)
    //
    // 0.116.0 declares /data as an image VOLUME, claims the data directory
    // with an exclusive flock, and lets initdb take ownership of it. Each of
    // those makes a previously "accepted but broken" configuration fail late
    // and confusingly, so the package rejects them up front.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task WithDataVolumeNormalizesTrailingSlashInTargetPath()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder
            .AddDocumentDB("DocumentDB")
            .WithDataVolume(targetPath: "/custom/data/");

        using var app = appBuilder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());
        var volumeAnnotation = Assert.Single(containerResource.Annotations.OfType<ContainerMountAnnotation>());
        Assert.Equal("/custom/data", volumeAnnotation.Target);

        var env = await BuildEnvironmentVariablesAsync(containerResource);
        Assert.Equal("/custom/data", Assert.Single(env.Where(entry => entry.Key == "DATA_PATH")).Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("data")]
    [InlineData("./data")]
    [InlineData("C:\\data")]
    [InlineData("/")]
    [InlineData("///")]
    // The container runtime resolves dot segments before mounting, so these are the container
    // root spelled differently, or a path that reaches above it. Neither can hold a cluster.
    [InlineData("/data/..")]
    [InlineData("/data/./..")]
    [InlineData("/..")]
    [InlineData("/../data")]
    [InlineData("/data/../..")]
    public void WithDataVolumeRejectsInvalidTargetPaths(string targetPath)
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB");

        var exception = Assert.Throws<ArgumentException>(() => documentDB.WithDataVolume(targetPath: targetPath));

        Assert.Equal("targetPath", exception.ParamName);
        Assert.Contains("/data", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A target path is canonicalized the way the container runtime resolves one, so the mount and
    /// <c>DATA_PATH</c> agree with each other and with what Docker will really do.
    /// </summary>
    [Theory]
    [InlineData("//custom///data", "/custom/data")]
    [InlineData("/custom/./data/", "/custom/data")]
    [InlineData("/custom/tmp/../data", "/custom/data")]
    [InlineData("/foo/../data", "/data")]
    public async Task WithDataVolumeCanonicalizesDotSegmentsInTargetPath(string targetPath, string expected)
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder
            .AddDocumentDB("DocumentDB")
            .WithDataVolume(targetPath: targetPath);

        using var app = appBuilder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());
        var volumeAnnotation = Assert.Single(containerResource.Annotations.OfType<ContainerMountAnnotation>());
        Assert.Equal(expected, volumeAnnotation.Target);

        var env = await BuildEnvironmentVariablesAsync(containerResource);
        Assert.Equal(expected, Assert.Single(env.Where(entry => entry.Key == "DATA_PATH")).Value);
    }

    [Fact]
    public void WithDataVolumeRejectsReadOnlyVolumes()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB");

        var exception = Assert.Throws<ArgumentException>(() => documentDB.WithDataVolume(isReadOnly: true));

        Assert.Equal("isReadOnly", exception.ParamName);
        Assert.Contains("WithDataVolume(isReadOnly: true) is not supported", exception.Message, StringComparison.Ordinal);
        Assert.Contains("writable data directory", exception.Message, StringComparison.Ordinal);
        Assert.Contains("PostgreSQL failed to start within 60 seconds", exception.Message, StringComparison.Ordinal);
        Assert.Contains("WithInitData", exception.Message, StringComparison.Ordinal);

        // The rejected call must not leave a half-configured resource behind.
        using var app = appBuilder.Build();
        var containerResource = Assert.Single(
            app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());
        Assert.Empty(containerResource.Annotations.OfType<ContainerMountAnnotation>());
    }

    [Fact]
    public void WithDataBindMountRejectsReadOnlyBindMounts()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB");

        var exception = Assert.Throws<ArgumentException>(() => documentDB.WithDataBindMount("/host/data", isReadOnly: true));

        Assert.Equal("isReadOnly", exception.ParamName);
        Assert.Contains("WithDataBindMount(isReadOnly: true) is not supported", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Read-only file system", exception.Message, StringComparison.Ordinal);

        using var app = appBuilder.Build();
        var containerResource = Assert.Single(
            app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());
        Assert.Empty(containerResource.Annotations.OfType<ContainerMountAnnotation>());
    }

    [Fact]
    public async Task ReadOnlyVolumeOnDefaultDataPathThrowsAtStart()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithVolume("raw-data", "/data", isReadOnly: true);

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PublishBeforeResourceStartedAsync(app, resource));

        Assert.Contains("mounts its data directory ('/data') read-only", exception.Message, StringComparison.Ordinal);
        Assert.Contains("volume 'raw-data'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Read-only file system", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadOnlyBindMountOnCustomDataPathThrowsAtStart()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithDataVolume(name: "writable", targetPath: "/pgdata")
            .WithBindMount("/host/data", "/pgdata", isReadOnly: true);

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PublishBeforeResourceStartedAsync(app, resource));

        Assert.Contains("data directory ('/pgdata') read-only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WritableDataMountStartsWithoutStorageWarnings()
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.Services.AddSingleton(sink);
        appBuilder.Services.AddSingleton<ILoggerProvider>(sp => new CapturingLoggerProvider(sp.GetRequiredService<CapturingLoggerSink>()));
        appBuilder.AddDocumentDB("DocumentDB").WithDataVolume();

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        await PublishBeforeResourceStartedWithModelAsync(app, resource);

        Assert.DoesNotContain(sink.LogEntries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task DuplicateDataMountsThrowAtStart()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithDataVolume(name: "first")
            .WithDataVolume(name: "second");

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PublishBeforeResourceStartedAsync(app, resource));

        Assert.Contains("2 mounts on its data directory ('/data')", exception.Message, StringComparison.Ordinal);
        Assert.Contains("duplicate mount targets", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DataVolumeSharedWithAnotherDocumentDBResourceThrowsAtStart()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("primary").WithImageTag(InterlockedTag).WithDataVolume(name: "shared-data");
        appBuilder.AddDocumentDB("secondary").WithImageTag(InterlockedTag).WithDataVolume(name: "shared-data");

        using var app = appBuilder.Build();
        var resource = app.Services.GetRequiredService<DistributedApplicationModel>()
            .Resources.OfType<DocumentDBServerResource>().Single(r => r.Name == "primary");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PublishBeforeResourceStartedAsync(app, resource));

        Assert.Contains("'primary' and DocumentDB resource 'secondary'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("volume 'shared-data'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("exclusive lock", exception.Message, StringComparison.Ordinal);
        Assert.Contains("another DocumentDB container is already using the data directory", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DataVolumeSharedOnTheDefaultImageDescribesThatImagesActualBehaviour()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("primary").WithDataVolume(name: "shared-data");
        appBuilder.AddDocumentDB("secondary").WithDataVolume(name: "shared-data");

        using var app = appBuilder.Build();
        var resource = app.Services.GetRequiredService<DistributedApplicationModel>()
            .Resources.OfType<DocumentDBServerResource>().Single(r => r.Name == "primary");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PublishBeforeResourceStartedAsync(app, resource));

        // Promising that the container refuses the second start is only honest on an image that
        // actually holds the lock, so the expectation follows the default's own version.
        var interlocked = Version.Parse(DocumentDBVersions.Latest) >=
            DocumentDBContainerImageTags.MinimumDeclaredDataVolumeVersion;

        if (interlocked)
        {
            Assert.Contains("another DocumentDB container is already using the data directory", exception.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("no data-directory interlock", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("another DocumentDB container is already using the data directory", exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task DataBindMountSharedWithAnotherDocumentDBResourceThrowsAtStart()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("primary").WithDataBindMount("./shared");
        appBuilder.AddDocumentDB("secondary").WithDataBindMount("./shared/");

        using var app = appBuilder.Build();
        var resource = app.Services.GetRequiredService<DistributedApplicationModel>()
            .Resources.OfType<DocumentDBServerResource>().Single(r => r.Name == "primary");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PublishBeforeResourceStartedAsync(app, resource));

        Assert.Contains("host directory", exception.Message, StringComparison.Ordinal);
        Assert.Contains("give each resource its own storage", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DataBindMountSharedWithANonDocumentDBResourceStartsWithoutWarnings()
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.Services.AddSingleton(sink);
        appBuilder.Services.AddSingleton<ILoggerProvider>(sp => new CapturingLoggerProvider(sp.GetRequiredService<CapturingLoggerSink>()));
        appBuilder.AddDocumentDB("DocumentDB").WithDataBindMount("./shared");
        appBuilder.AddContainer("backup", "alpine").WithBindMount("./shared", "/backup", isReadOnly: true);

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        await PublishBeforeResourceStartedWithModelAsync(app, resource);

        Assert.DoesNotContain(sink.LogEntries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task DistinctDataVolumesStartWithoutStorageWarnings()
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.Services.AddSingleton(sink);
        appBuilder.Services.AddSingleton<ILoggerProvider>(sp => new CapturingLoggerProvider(sp.GetRequiredService<CapturingLoggerSink>()));
        appBuilder.AddDocumentDB("primary").WithDataVolume(name: "primary-data");
        appBuilder.AddDocumentDB("secondary").WithDataVolume(name: "secondary-data");

        using var app = appBuilder.Build();
        var resource = app.Services.GetRequiredService<DistributedApplicationModel>()
            .Resources.OfType<DocumentDBServerResource>().Single(r => r.Name == "primary");

        await PublishBeforeResourceStartedWithModelAsync(app, resource);

        Assert.DoesNotContain(sink.LogEntries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task SharedDataVolumeWithExplicitlyStartedResourceWarnsInsteadOfThrowing()
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.Services.AddSingleton(sink);
        appBuilder.Services.AddSingleton<ILoggerProvider>(sp => new CapturingLoggerProvider(sp.GetRequiredService<CapturingLoggerSink>()));
        appBuilder.AddDocumentDB("primary").WithImageTag(InterlockedTag).WithDataVolume(name: "shared-data");
        appBuilder.AddDocumentDB("standby").WithImageTag(InterlockedTag).WithDataVolume(name: "shared-data").WithExplicitStart();

        using var app = appBuilder.Build();
        var resource = app.Services.GetRequiredService<DistributedApplicationModel>()
            .Resources.OfType<DocumentDBServerResource>().Single(r => r.Name == "primary");

        await PublishBeforeResourceStartedWithModelAsync(app, resource);

        var (_, _, message) = Assert.Single(sink.LogEntries.Where(e => e.Level == LogLevel.Warning));
        Assert.Contains("'primary' and DocumentDB resource 'standby'", message, StringComparison.Ordinal);
        Assert.Contains("volume 'shared-data'", message, StringComparison.Ordinal);
        Assert.Contains("exclusive lock", message, StringComparison.Ordinal);
        Assert.Contains("WithExplicitStart()", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CustomDataTargetPathWarnsAboutTheDeclaredImageVolume()
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.Services.AddSingleton(sink);
        appBuilder.Services.AddSingleton<ILoggerProvider>(sp => new CapturingLoggerProvider(sp.GetRequiredService<CapturingLoggerSink>()));
        appBuilder.AddDocumentDB("DocumentDB").WithImageTag(InterlockedTag).WithDataVolume(targetPath: "/pgdata");

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        await PublishBeforeResourceStartedAsync(app, resource, useEmptyServices: true);

        var (_, _, message) = Assert.Single(sink.LogEntries.Where(e => e.Level == LogLevel.Warning));
        Assert.Contains("/pgdata", message, StringComparison.Ordinal);
        Assert.Contains("anonymous volume", message, StringComparison.Ordinal);
        Assert.Contains("/data", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CustomDataTargetPathDoesNotWarnWhenTheImageVolumeIsAlsoMounted()
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.Services.AddSingleton(sink);
        appBuilder.Services.AddSingleton<ILoggerProvider>(sp => new CapturingLoggerProvider(sp.GetRequiredService<CapturingLoggerSink>()));
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithDataVolume(name: "pgdata", targetPath: "/pgdata")
            .WithVolume("declared-image-volume", "/data");

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        await PublishBeforeResourceStartedAsync(app, resource, useEmptyServices: true);

        Assert.DoesNotContain(sink.LogEntries, e => e.Level == LogLevel.Warning);
    }


    [Fact]
    public async Task TheDefaultImageWarnsAboutADeclaredVolumeOnlyIfItDeclaresOne()
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.Services.AddSingleton(sink);
        appBuilder.Services.AddSingleton<ILoggerProvider>(sp => new CapturingLoggerProvider(sp.GetRequiredService<CapturingLoggerSink>()));
        appBuilder.AddDocumentDB("DocumentDB").WithDataVolume(targetPath: "/pgdata");

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        await PublishBeforeResourceStartedAsync(app, resource, useEmptyServices: true);

        // Asserted against the default's own version rather than a hard-coded expectation, so this
        // keeps holding when DocumentDBVersions.Latest crosses the floor.
        var declaresVolume = Version.Parse(DocumentDBVersions.Latest) >=
            DocumentDBContainerImageTags.MinimumDeclaredDataVolumeVersion;

        Assert.Equal(declaresVolume, sink.LogEntries.Any(e => e.Level == LogLevel.Warning));
    }

    [Theory]
    [InlineData("pg17-0.114.0")]
    [InlineData("pg17-0.113.0")]
    public async Task CustomDataTargetPathDoesNotWarnOnPre116Tags(string tag)
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.Services.AddSingleton(sink);
        appBuilder.Services.AddSingleton<ILoggerProvider>(sp => new CapturingLoggerProvider(sp.GetRequiredService<CapturingLoggerSink>()));
        appBuilder.AddDocumentDB("DocumentDB").WithImageTag(tag).WithDataVolume(targetPath: "/pgdata");

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        await PublishBeforeResourceStartedAsync(app, resource, useEmptyServices: true);

        Assert.DoesNotContain(sink.LogEntries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task CustomDataTargetPathDoesNotWarnOnACustomImage()
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.Services.AddSingleton(sink);
        appBuilder.Services.AddSingleton<ILoggerProvider>(sp => new CapturingLoggerProvider(sp.GetRequiredService<CapturingLoggerSink>()));
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImage("contoso/documentdb-fork", "pg17-0.116.0")
            .WithDataVolume(targetPath: "/pgdata");

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        await PublishBeforeResourceStartedAsync(app, resource, useEmptyServices: true);

        Assert.DoesNotContain(sink.LogEntries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task CustomDataTargetPathDoesNotWarnOnAnUnrecognisedTag()
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.Services.AddSingleton(sink);
        appBuilder.Services.AddSingleton<ILoggerProvider>(sp => new CapturingLoggerProvider(sp.GetRequiredService<CapturingLoggerSink>()));
        appBuilder.AddDocumentDB("DocumentDB").WithImageTag("latest").WithDataVolume(targetPath: "/pgdata");

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        await PublishBeforeResourceStartedAsync(app, resource, useEmptyServices: true);

        Assert.DoesNotContain(sink.LogEntries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task SharedDataVolumeOnPre116ImagesThrowsEvenWithExplicitStart()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        // 0.114.0 has no data-directory lock, so an explicitly started peer is not a safety net:
        // if both ever run, nothing refuses the second one.
        appBuilder.AddDocumentDB("primary").WithImageTag("pg17-0.114.0").WithDataVolume(name: "shared-data");
        appBuilder.AddDocumentDB("standby").WithImageTag("pg17-0.114.0").WithDataVolume(name: "shared-data").WithExplicitStart();

        using var app = appBuilder.Build();
        var resource = app.Services.GetRequiredService<DistributedApplicationModel>()
            .Resources.OfType<DocumentDBServerResource>().Single(r => r.Name == "primary");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PublishBeforeResourceStartedAsync(app, resource));

        Assert.Contains("no data-directory interlock", exception.Message, StringComparison.Ordinal);
        Assert.Contains("corrupt it silently", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("WithExplicitStart()", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SharedDataVolumeWithACustomImagePeerThrowsEvenWithExplicitStart()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("primary").WithImageTag(InterlockedTag).WithDataVolume(name: "shared-data");
        appBuilder.AddDocumentDB("fork")
            .WithImage("contoso/documentdb-fork", "pg17-0.116.0")
            .WithDataVolume(name: "shared-data")
            .WithExplicitStart();

        using var app = appBuilder.Build();
        var resource = app.Services.GetRequiredService<DistributedApplicationModel>()
            .Resources.OfType<DocumentDBServerResource>().Single(r => r.Name == "primary");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PublishBeforeResourceStartedAsync(app, resource));

        Assert.Contains("no data-directory interlock", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExplicitlyStartedPeerDoesNotHideALaterHardConflict()
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.Services.AddSingleton(sink);
        appBuilder.Services.AddSingleton<ILoggerProvider>(sp => new CapturingLoggerProvider(sp.GetRequiredService<CapturingLoggerSink>()));

        // Order matters: the warning-only peer is scanned first, and must not end the scan before
        // the always-on peer that shares the same volume is seen.
        appBuilder.AddDocumentDB("primary").WithImageTag(InterlockedTag).WithDataVolume(name: "shared-data");
        appBuilder.AddDocumentDB("manual").WithImageTag(InterlockedTag).WithDataVolume(name: "shared-data").WithExplicitStart();
        appBuilder.AddDocumentDB("always-on").WithImageTag(InterlockedTag).WithDataVolume(name: "shared-data");

        using var app = appBuilder.Build();
        var resource = app.Services.GetRequiredService<DistributedApplicationModel>()
            .Resources.OfType<DocumentDBServerResource>().Single(r => r.Name == "primary");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PublishBeforeResourceStartedWithModelAsync(app, resource));

        Assert.Contains("'primary' and DocumentDB resource 'always-on'", exception.Message, StringComparison.Ordinal);

        // A hard conflict is not softened by also having a warning-only one.
        Assert.DoesNotContain(sink.LogEntries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task StorageSharedWithAPeersInitDataIsNotADataDirectoryConflict()
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.Services.AddSingleton(sink);
        appBuilder.Services.AddSingleton<ILoggerProvider>(sp => new CapturingLoggerProvider(sp.GetRequiredService<CapturingLoggerSink>()));

        // The peer reads the same host directory as seed scripts and TLS material. That is a
        // read-only input mount on a different container path, not a second cluster on the files.
        appBuilder.AddDocumentDB("primary").WithImageTag(InterlockedTag).WithDataBindMount("./shared");
        appBuilder.AddDocumentDB("secondary")
            .WithImageTag(InterlockedTag)
            .WithDataVolume(name: "secondary-data")
            .WithInitData("./shared")
            .WithTlsCertificate("./shared/tls.crt", "./shared/tls.key");

        using var app = appBuilder.Build();
        var resource = app.Services.GetRequiredService<DistributedApplicationModel>()
            .Resources.OfType<DocumentDBServerResource>().Single(r => r.Name == "primary");

        await PublishBeforeResourceStartedWithModelAsync(app, resource);

        Assert.DoesNotContain(sink.LogEntries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task SharedStorageOnDifferentDataPathsIsStillADataDirectoryConflict()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("primary").WithImageTag(InterlockedTag).WithDataVolume(name: "shared-data");
        appBuilder.AddDocumentDB("secondary")
            .WithImageTag(InterlockedTag)
            .WithDataVolume(name: "shared-data", targetPath: "/pgdata");

        using var app = appBuilder.Build();
        var resource = app.Services.GetRequiredService<DistributedApplicationModel>()
            .Resources.OfType<DocumentDBServerResource>().Single(r => r.Name == "primary");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PublishBeforeResourceStartedAsync(app, resource));

        Assert.Contains("volume 'shared-data'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithDataVolumeReadOnlyMessageNamesTheRequestedTargetPath()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB");

        var exception = Assert.Throws<ArgumentException>(
            () => documentDB.WithDataVolume(isReadOnly: true, targetPath: "/pgdata/"));

        Assert.Equal("isReadOnly", exception.ParamName);
        Assert.Contains("ownership of '/pgdata'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("permissions of directory \"/pgdata\"", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("/data\"", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithDataVolumeRejectsAnInvalidTargetPathBeforeComplainingAboutReadOnly()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB");

        var exception = Assert.Throws<ArgumentException>(
            () => documentDB.WithDataVolume(isReadOnly: true, targetPath: "relative/path"));

        Assert.Equal("targetPath", exception.ParamName);
    }

    // ---------------------------------------------------------------------
    // Effective DATA_PATH and container-path canonicalization
    //
    // Docker resolves '.', '..' and repeated separators before it mounts, and
    // DATA_PATH is an ordinary environment variable whose last writer wins.
    // The guard has to model both, or an alias of the data directory — or an
    // override that moves it — slips past every rule below.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ReadOnlyRawMountOnADotSegmentAliasOfTheDataPathThrowsAtStart()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithVolume("raw-data", "/tmp/../data", isReadOnly: true);

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PublishBeforeResourceStartedAsync(app, resource));

        Assert.Contains("mounts its data directory ('/data') read-only", exception.Message, StringComparison.Ordinal);
        Assert.Contains("volume 'raw-data'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicateDataMountsAreDetectedThroughDotSegmentAliases()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithDataVolume(name: "first")
            .WithVolume("second", "//data/./");

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PublishBeforeResourceStartedAsync(app, resource));

        Assert.Contains("2 mounts on its data directory ('/data')", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The case the aliasing bug made reachable: two resources on images with no data-directory
    /// interlock, mounting one volume at two spellings of the same container path. Nothing in the
    /// container would refuse the second start, so the model has to.
    /// </summary>
    [Fact]
    public async Task SharedStorageIsDetectedThroughDotSegmentAliasesOnUninterlockedImages()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("primary").WithImageTag("pg17-0.114.0").WithDataVolume(name: "shared-data");
        appBuilder.AddDocumentDB("secondary")
            .WithImageTag("pg17-0.114.0")
            .WithVolume("shared-data", "/foo/../data")
            .WithEnvironment("DATA_PATH", "/foo/../data");

        using var app = appBuilder.Build();
        var resource = app.Services.GetRequiredService<DistributedApplicationModel>()
            .Resources.OfType<DocumentDBServerResource>().Single(r => r.Name == "primary");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PublishBeforeResourceStartedAsync(app, resource));

        Assert.Contains("volume 'shared-data'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("no data-directory interlock", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RawDataPathEnvironmentOverrideParticipatesInTheReadOnlyCheck()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithVolume("raw-data", "/pgdata", isReadOnly: true)
            .WithEnvironment("DATA_PATH", "/pgdata");

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PublishBeforeResourceStartedAsync(app, resource));

        Assert.Contains("mounts its data directory ('/pgdata') read-only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RawDataPathEnvironmentOverrideParticipatesInTheDuplicateCheck()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithVolume("first", "/pgdata")
            .WithVolume("second", "/pgdata/")
            .WithEnvironment("DATA_PATH", "/var/../pgdata");

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PublishBeforeResourceStartedAsync(app, resource));

        Assert.Contains("2 mounts on its data directory ('/pgdata')", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RawDataPathEnvironmentOverrideParticipatesInTheSharedStorageCheck()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("primary").WithImageTag(InterlockedTag).WithDataVolume(name: "shared-data");
        appBuilder.AddDocumentDB("secondary")
            .WithImageTag(InterlockedTag)
            .WithVolume("shared-data", "/pgdata")
            .WithEnvironment("DATA_PATH", "/pgdata");

        using var app = appBuilder.Build();
        var resource = app.Services.GetRequiredService<DistributedApplicationModel>()
            .Resources.OfType<DocumentDBServerResource>().Single(r => r.Name == "primary");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PublishBeforeResourceStartedAsync(app, resource));

        Assert.Contains("volume 'shared-data'", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ordering matters and is the container's, not the package's: the environment callback that
    /// runs last decides where DocumentDB writes.
    /// </summary>
    [Fact]
    public async Task DataPathEnvironmentOverrideAfterTheStorageHelperWins()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithDataVolume()
            .WithBindMount("/host/read-only", "/pgdata", isReadOnly: true)
            .WithEnvironment("DATA_PATH", "/pgdata");

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        Assert.Equal("/pgdata", (await BuildEnvironmentVariablesAsync(resource))["DATA_PATH"]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PublishBeforeResourceStartedAsync(app, resource));

        Assert.Contains("mounts its data directory ('/pgdata') read-only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DataPathEnvironmentOverrideBeforeTheStorageHelperLoses()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithEnvironment("DATA_PATH", "/pgdata")
            .WithDataVolume()
            .WithBindMount("/host/read-only", "/pgdata", isReadOnly: true);

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        Assert.Equal("/data", (await BuildEnvironmentVariablesAsync(resource))["DATA_PATH"]);

        // The read-only mount is not on the effective data directory, so it is somebody else's
        // read-only input and no concern of this guard.
        await PublishBeforeResourceStartedAsync(app, resource);
    }

    [Fact]
    public async Task CallbackSuppliedDataPathParticipatesInStorageChecks()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithVolume("raw-data", "/pgdata", isReadOnly: true)
            .WithEnvironment(context => context.EnvironmentVariables["DATA_PATH"] = "/var/../pgdata/");

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PublishBeforeResourceStartedAsync(app, resource));

        Assert.Contains("mounts its data directory ('/pgdata') read-only", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>DATA_PATH</c> supplied as a parameter is a value provider, not a string, so it has to
    /// be resolved before it can be compared with a mount target.
    /// </summary>
    [Fact]
    public async Task ParameterSuppliedDataPathParticipatesInStorageChecks()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var dataPath = appBuilder.AddParameter("datapath", "/pgdata");
        appBuilder.AddDocumentDB("DocumentDB")
            .WithVolume("raw-data", "/pgdata", isReadOnly: true)
            .WithEnvironment("DATA_PATH", dataPath);

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PublishBeforeResourceStartedAsync(app, resource));

        Assert.Contains("mounts its data directory ('/pgdata') read-only", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Running the environment pipeline must not turn a question about a filesystem path into a
    /// secret lookup: every value other than <c>DATA_PATH</c> stays unresolved.
    /// </summary>
    [Fact]
    public async Task StorageGuardResolvesOnlyTheDataPathValue()
    {
        var recordingProvider = new RecordingValueProvider("unused");

        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithDataVolume()
            .WithEnvironment(context => context.EnvironmentVariables["SOME_SECRET"] = recordingProvider);

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        await PublishBeforeResourceStartedAsync(app, resource);

        Assert.Equal(0, recordingProvider.ResolutionCount);
    }

    [Theory]
    [InlineData("/data/..", "resolves to the container root")]
    [InlineData("//", "resolves to the container root")]
    [InlineData("/data/../..", "escapes above the container root")]
    [InlineData("/../data", "escapes above the container root")]
    [InlineData("pgdata", "is not an absolute path inside the container")]
    public async Task UnusableDataPathIsRejectedAtStart(string dataPath, string expected)
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithEnvironment("DATA_PATH", dataPath);

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PublishBeforeResourceStartedAsync(app, resource));

        Assert.Contains($"sets DATA_PATH to '{dataPath}'", exception.Message, StringComparison.Ordinal);
        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The declared image volume is covered by any mount the runtime resolves onto <c>/data</c>,
    /// however it was spelled, so an alias suppresses the anonymous-volume warning.
    /// </summary>
    [Fact]
    public async Task ADotSegmentAliasOfTheDeclaredImageVolumeSuppressesTheWarning()
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.Services.AddSingleton(sink);
        appBuilder.Services.AddSingleton<ILoggerProvider>(sp => new CapturingLoggerProvider(sp.GetRequiredService<CapturingLoggerSink>()));
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithDataVolume(name: "pgdata", targetPath: "/pgdata")
            .WithVolume("declared-image-volume", "/tmp/../data/");

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        await PublishBeforeResourceStartedAsync(app, resource, useEmptyServices: true);

        Assert.DoesNotContain(sink.LogEntries, e => e.Level == LogLevel.Warning);
    }

    /// <summary>
    /// The read-only input mounts the package adds itself sit on their own container paths and
    /// must stay unaffected by the data-directory rules, including when the data directory is
    /// reached through an alias.
    /// </summary>
    [Fact]
    public async Task ReadOnlyInitDataAndTlsMountsAreNotTreatedAsDataMounts()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithDataVolume(targetPath: "/var/lib/../lib/documentdb")
            .WithInitData("./seed")
            .WithTlsCertificate("./certs/server.crt", "./certs/server.key");

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        Assert.Equal("/var/lib/documentdb", (await BuildEnvironmentVariablesAsync(resource))["DATA_PATH"]);
        Assert.All(
            resource.Annotations.OfType<ContainerMountAnnotation>().Where(mount => mount.IsReadOnly),
            mount => Assert.NotEqual("/var/lib/documentdb", mount.Target));

        await PublishBeforeResourceStartedAsync(app, resource);
    }

    /// <summary>
    /// An <see cref="IValueProvider"/> that records whether anything asked it for its value, so a
    /// test can assert that the storage guard left it alone.
    /// </summary>
    private sealed class RecordingValueProvider(string value) : IValueProvider
    {
        private int _resolutionCount;

        public int ResolutionCount => Volatile.Read(ref _resolutionCount);

        public ValueTask<string?> GetValueAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _resolutionCount);
            return ValueTask.FromResult<string?>(value);
        }
    }

    [Fact]
    public async Task WithInitDataReadOnlyMountIsNotTreatedAsADataMount()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithDataVolume()
            .WithInitData("./seed");

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        var initDataMount = Assert.Single(resource.Annotations.OfType<ContainerMountAnnotation>().Where(m => m.Target == "/init_doc_db.d"));
        Assert.True(initDataMount.IsReadOnly);

        await PublishBeforeResourceStartedAsync(app, resource);
    }

    [Fact]
    public async Task WithDataVolumeIsRepresentedInTheManifest()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB").WithDataVolume(name: "documentdb-data");

        var manifest = await ManifestUtils.GetManifest(documentDB.Resource);

        Assert.Equal("documentdb-data", manifest["volumes"]?[0]?["name"]?.ToString());
        Assert.Equal("/data", manifest["volumes"]?[0]?["target"]?.ToString());
        Assert.Equal("false", manifest["volumes"]?[0]?["readOnly"]?.ToString().ToLowerInvariant());
        Assert.Equal("/data", manifest["env"]?["DATA_PATH"]?.ToString());
    }

    [Fact]
    public async Task WithDataBindMountIsRepresentedInTheManifest()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB").WithDataBindMount("./documentdb-data");

        var manifest = await ManifestUtils.GetManifest(documentDB.Resource);

        Assert.Equal("/data", manifest["bindMounts"]?[0]?["target"]?.ToString());
        Assert.Equal("false", manifest["bindMounts"]?[0]?["readOnly"]?.ToString().ToLowerInvariant());
        Assert.Equal("/data", manifest["env"]?["DATA_PATH"]?.ToString());
    }

    [Fact]
    public async Task WithDataBindMountAddsBindMountAnnotation()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder
            .AddDocumentDB("DocumentDB")
            .WithDataBindMount("/host/data");

        using var app = appBuilder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());
        var bindMountAnnotation = Assert.Single(containerResource.Annotations.OfType<ContainerMountAnnotation>().Where(a => a.Type == ContainerMountType.BindMount));
        Assert.Equal("/host/data", bindMountAnnotation.Source);
        Assert.Equal("/data", bindMountAnnotation.Target);
        Assert.False(bindMountAnnotation.IsReadOnly);

        var env = await BuildEnvironmentVariablesAsync(containerResource);
        var dataPath = Assert.Single(env.Where(entry => entry.Key == "DATA_PATH"));
        Assert.Equal("/data", dataPath.Value);
    }

    [Fact]
    public void WithDataBindMountRejectsReadOnlyBindMountsAtTheApiBoundary()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB");

        Assert.Throws<ArgumentException>(() => documentDB.WithDataBindMount("/host/data", isReadOnly: true));
    }

    [Fact]
    public async Task AddDocumentDBWithCustomUserNameAndPassword()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var userName = appBuilder.AddParameter("user", secret: false);
        var password = appBuilder.AddParameter("pass", secret: true);
        appBuilder
            .AddDocumentDB("DocumentDB", userName: userName, password: password)
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 10260));

        using var app = appBuilder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var serverResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());
        Assert.NotNull(serverResource.UserNameParameter);
        Assert.Equal("user", serverResource.UserNameParameter.Name);
        Assert.NotNull(serverResource.PasswordParameter);
        Assert.Equal("pass", serverResource.PasswordParameter.Name);

        AssertConnectionStringExpression(
            serverResource.ConnectionStringExpression.ValueExpression,
            resourceName: "DocumentDB",
            expectedDatabaseName: null,
            expectedUserExpression: "{user.value}",
            expectedPasswordExpression: "{pass.value}",
            ("authSource", "admin"),
            ("authMechanism", "SCRAM-SHA-256"),
            ("tls", "true"),
            ("tlsInsecure", "true"));

        var env = await BuildEnvironmentVariablesAsync(serverResource);
        Assert.Equal("{user.value}", Assert.IsType<ReferenceExpression>(env["USERNAME"]).ValueExpression);
        Assert.Equal("pass", Assert.IsType<ParameterResource>(env["PASSWORD"]).Name);
    }

    [Fact]
    public void AddDatabaseDefaultsDatabaseNameToResourceName()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var db = appBuilder
            .AddDocumentDB("DocumentDB")
            .AddDatabase("myresource");

        Assert.Equal("myresource", db.Resource.DatabaseName);
    }

    [Fact]
    public void DatabaseResourceHasCorrectParent()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var server = appBuilder.AddDocumentDB("DocumentDB");
        var db = server.AddDatabase("mydb");

        Assert.Same(server.Resource, db.Resource.Parent);
    }

    [Fact]
    public void ServerResourceTracksDatabases()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var server = appBuilder.AddDocumentDB("DocumentDB");
        var db1 = server.AddDatabase("db1");
        var db2 = server.AddDatabase("db2");

        Assert.Equal(2, server.Resource.Databases.Count);
        Assert.Contains(db1.Resource, server.Resource.Databases);
        Assert.Contains(db2.Resource, server.Resource.Databases);
    }

    [Theory]
    [InlineData(DocumentDBLogLevel.Quiet, "quiet")]
    [InlineData(DocumentDBLogLevel.Error, "error")]
    [InlineData(DocumentDBLogLevel.Warn, "warn")]
    [InlineData(DocumentDBLogLevel.Info, "info")]
    [InlineData(DocumentDBLogLevel.Debug, "debug")]
    [InlineData(DocumentDBLogLevel.Trace, "trace")]
    public async Task WithLogLevelAddsCanonicalAndLegacyEnvironmentVariables(DocumentDBLogLevel logLevel, string expectedValue)
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithLogLevel(logLevel);

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());

        var env = await BuildEnvironmentVariablesAsync(containerResource);

        Assert.Equal(expectedValue, env["DOCUMENTDB_LOG_LEVEL"]);
        Assert.Equal(expectedValue, env["LOG_LEVEL"]);
    }

    [Fact]
    public async Task WithLogLevelOverridesEarlierEnvironmentValues()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithEnvironment("DOCUMENTDB_LOG_LEVEL", "warn")
            .WithEnvironment("LOG_LEVEL", "error")
            .WithLogLevel(DocumentDBLogLevel.Debug);

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());
        var env = await BuildEnvironmentVariablesAsync(containerResource);

        Assert.Equal("debug", env["DOCUMENTDB_LOG_LEVEL"]);
        Assert.Equal("debug", env["LOG_LEVEL"]);
    }

    [Fact]
    public async Task LaterEnvironmentValuesOverrideWithLogLevel()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithLogLevel(DocumentDBLogLevel.Debug)
            .WithEnvironment("DOCUMENTDB_LOG_LEVEL", "warn")
            .WithEnvironment("LOG_LEVEL", "error");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());
        var env = await BuildEnvironmentVariablesAsync(containerResource);

        Assert.Equal("warn", env["DOCUMENTDB_LOG_LEVEL"]);
        Assert.Equal("error", env["LOG_LEVEL"]);
    }

    [Fact]
    public async Task WithInitDataOverridesPreExistingInitDataAndAddsReadOnlyBindMount()
    {
        var source = Path.GetFullPath(Path.Combine("TestData", "init"));

        var appBuilder = DistributedApplication.CreateBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithEnvironment("INIT_DATA", "true")
            .WithInitData(source);

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());

        Assert.True(containerResource.TryGetContainerMounts(out var mounts));
        var mount = Assert.Single(mounts);
        Assert.Equal(source, mount.Source);
        Assert.Equal("/init_doc_db.d", mount.Target);
        Assert.Equal(ContainerMountType.BindMount, mount.Type);
        Assert.True(mount.IsReadOnly);

        var env = await BuildEnvironmentVariablesAsync(containerResource);
        Assert.Equal("false", env["INIT_DATA"]);
        Assert.Equal("/init_doc_db.d", env["INIT_DATA_PATH"]);
        Assert.Equal("true", env["SKIP_INIT_DATA"]);

        var manifest = await ManifestUtils.GetManifest(documentDB.Resource);
        Assert.Equal("false", manifest["env"]?["INIT_DATA"]?.GetValue<string>());
        Assert.Equal("true", manifest["env"]?["SKIP_INIT_DATA"]?.GetValue<string>());
    }

    [Fact]
    public async Task WithoutSampleDataOverridesPreExistingInitData()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithEnvironment("INIT_DATA", "true")
            .WithoutSampleData();

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());

        var env = await BuildEnvironmentVariablesAsync(containerResource);
        Assert.Equal("false", env["INIT_DATA"]);
        Assert.Equal("true", env["SKIP_INIT_DATA"]);

        var manifest = await ManifestUtils.GetManifest(documentDB.Resource);
        Assert.Equal("false", manifest["env"]?["INIT_DATA"]?.GetValue<string>());
        Assert.Equal("true", manifest["env"]?["SKIP_INIT_DATA"]?.GetValue<string>());
    }

    [Fact]
    public async Task WithoutExtendedRumAddsEnvironmentVariable()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithoutExtendedRum();

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());

        var env = await BuildEnvironmentVariablesAsync(containerResource);
        Assert.Equal("true", env["DISABLE_EXTENDED_RUM"]);
    }

    [Fact]
    public async Task WithoutUserCreationAddsEnvironmentVariable()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithoutUserCreation();

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());

        var env = await BuildEnvironmentVariablesAsync(containerResource);
        Assert.Equal("false", env["CREATE_USER"]);
    }

    [Fact]
    public async Task WithTlsCertificateAddsReadOnlyBindMountsAndEnvironmentVariables()
    {
        var certPath = Path.GetFullPath(Path.Combine("TestData", "certs", "documentdb.pem"));
        var keyPath = Path.GetFullPath(Path.Combine("TestData", "certs", "documentdb.key"));
        var expectedCertTarget = $"/documentdb-cert-{Path.GetFileName(certPath)}";
        var expectedKeyTarget = $"/documentdb-key-{Path.GetFileName(keyPath)}";

        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithTlsCertificate(certPath, keyPath);

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());

        Assert.True(containerResource.TryGetContainerMounts(out var mounts));
        var certMount = Assert.Single(mounts, mount => mount.Source == certPath);
        Assert.Equal(expectedCertTarget, certMount.Target);
        Assert.Equal(ContainerMountType.BindMount, certMount.Type);
        Assert.True(certMount.IsReadOnly);

        var keyMount = Assert.Single(mounts, mount => mount.Source == keyPath);
        Assert.Equal(expectedKeyTarget, keyMount.Target);
        Assert.Equal(ContainerMountType.BindMount, keyMount.Type);
        Assert.True(keyMount.IsReadOnly);

        var env = await BuildEnvironmentVariablesAsync(containerResource);
        Assert.Equal(expectedCertTarget, env["CERT_PATH"]);
        Assert.Equal(expectedKeyTarget, env["KEY_FILE"]);
    }

    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    public async Task WithTelemetryAddsEnvironmentVariable(bool enabled, string expectedValue)
    {
#pragma warning disable ASPIREDOCDB0001 // WithTelemetry is obsolete; behavior retained for binary compatibility.
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithTelemetry(enabled);
#pragma warning restore ASPIREDOCDB0001

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());

        var env = await BuildEnvironmentVariablesAsync(containerResource);
        Assert.Equal(expectedValue, env["ENABLE_TELEMETRY"]);
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsSetsEnabledEnvironmentVariableFor0114ControlImage()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithDocumentDBVersion(DocumentDBVersion.V0_114_0)
            .WithOpenTelemetryMetrics();

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());

        var env = await BuildEnvironmentVariablesAsync(containerResource);
        Assert.Equal("true", env["OTEL_METRICS_ENABLED"]);
        Assert.False(env.ContainsKey("OTEL_EXPORTER_OTLP_METRICS_ENDPOINT"));
        Assert.False(env.ContainsKey("OTEL_METRIC_EXPORT_INTERVAL"));
        Assert.False(env.ContainsKey("OTEL_EXPORTER_OTLP_METRICS_TIMEOUT"));
        Assert.False(env.ContainsKey("OTEL_SERVICE_NAME"));
        Assert.False(env.ContainsKey("OTEL_SERVICE_VERSION"));
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsWrapsTheEntrypointForGatewayTelemetryImages()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithOpenTelemetryMetrics(endpoint: "http://first:4317")
            .WithOpenTelemetryMetrics(serviceName: "documentdb-local");

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);

        var containerResource = SingleServerResource(app);
        Assert.Equal(GatewayConfigurationShell, containerResource.Entrypoint);

        var args = await BuildContainerArgsAsync(containerResource);
        Assert.Equal(3, args.Count);
        Assert.Equal("-c", args[0]);
        Assert.Equal(GatewayConfigurationShellArgumentZero, args[2]);

        var script = Assert.IsType<string>(args[1]);
        Assert.Contains($"exec {GatewayEntrypointScriptPath} \"$@\"", script, StringComparison.Ordinal);

        // The caller-visible CONFIG_DIR contract is unchanged: the wrapper reads it, it is not
        // written into the resource environment.
        var env = await BuildEnvironmentVariablesAsync(containerResource);
        Assert.False(env.ContainsKey("CONFIG_DIR"));
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsResolvesTheConfigurationDirectoryLikeTheImageEntrypoint()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithEnvironment("CONFIG_DIR", "/custom/documentdb/config")
            .WithEnvironment("GATEWAY_HOME", "/opt/documentdb/gateway")
            .WithOpenTelemetryMetrics();

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);

        var containerResource = SingleServerResource(app);

        // Caller-supplied values reach the container untouched; the wrapper consumes them at
        // runtime rather than second-guessing them at build time.
        var env = await BuildEnvironmentVariablesAsync(containerResource);
        Assert.Equal("/custom/documentdb/config", env["CONFIG_DIR"]);
        Assert.Equal("/opt/documentdb/gateway", env["GATEWAY_HOME"]);

        var script = await GetWrapperScriptAsync(containerResource);

        // 1. An explicit CONFIG_DIR wins.
        Assert.Contains("c=\"$CONFIG_DIR\"", script, StringComparison.Ordinal);

        // 2. Otherwise the packaged layout, detected exactly the way the image entrypoint detects
        //    it (both scripts present, not merely the directory).
        Assert.Contains(
            "if [ -f \"/usr/share/documentdb/scripts/start_oss_server.sh\" ] && " +
            "[ -f \"/usr/share/documentdb/scripts/utils.sh\" ]; then c=\"/etc/documentdb/gateway\";",
            script,
            StringComparison.Ordinal);

        // 3. Otherwise $GATEWAY_HOME/pg_documentdb_gw, with the upstream GATEWAY_HOME default.
        Assert.Contains("g=\"$GATEWAY_HOME\"", script, StringComparison.Ordinal);
        Assert.Contains("if [ -z \"$g\" ]; then g=\"/home/documentdb/gateway\"; fi;", script, StringComparison.Ordinal);
        Assert.Contains("c=\"$g/pg_documentdb_gw\"", script, StringComparison.Ordinal);

        Assert.Contains("s=\"$c/SetupConfiguration.json\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsRemovesTheWholeMetricsBlockButKeepsTheSharedIdentity()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag("pg17-0.116.0")
            .WithOpenTelemetryMetrics();

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);

        var script = await GetWrapperScriptAsync(SingleServerResource(app));

        // The metrics object goes whole, so the shipped OtlpEndpoint cannot beat
        // OTEL_EXPORTER_OTLP_ENDPOINT and quietly export into the container itself, and no
        // individual key is enumerated that a future gateway field could slip past.
        Assert.Contains($"jq '{MetricsBlockFilter})'", script, StringComparison.Ordinal);
        Assert.DoesNotContain(".TelemetryOptions.Metrics.", script, StringComparison.Ordinal);

        // The shared identity and the shipped (disabled) tracing block survive untouched.
        Assert.DoesNotContain(".TelemetryOptions.ServiceName", script, StringComparison.Ordinal);
        Assert.DoesNotContain(".TelemetryOptions.ServiceVersion", script, StringComparison.Ordinal);
        Assert.DoesNotContain(".TelemetryOptions.Tracing", script, StringComparison.Ordinal);

        // The caller did not ask for an identity, so none is invented for them either.
        var env = await BuildEnvironmentVariablesAsync(SingleServerResource(app));
        Assert.False(env.ContainsKey("OTEL_SERVICE_NAME"));
        Assert.False(env.ContainsKey("OTEL_SERVICE_VERSION"));
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsLeavesTheGenericEndpointFallbackReachable()
    {
        // The stock file ships Metrics.OtlpEndpoint = http://localhost:4317. Keeping it would beat
        // both OTEL_EXPORTER_OTLP_METRICS_ENDPOINT and OTEL_EXPORTER_OTLP_ENDPOINT and export
        // metrics into the DocumentDB container itself, which is the silent loss this wrapper
        // exists to prevent.
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag("pg17-0.116.0")
            .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", "http://otel-collector:4317")
            .WithOpenTelemetryMetrics();

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);

        var containerResource = SingleServerResource(app);
        var script = await GetWrapperScriptAsync(containerResource);

        // The object goes whole, so no key inside it - present or future - can survive to beat
        // the environment.
        Assert.Contains($"jq '{MetricsBlockFilter})'", script, StringComparison.Ordinal);
        Assert.DoesNotContain(".TelemetryOptions.Metrics.", script, StringComparison.Ordinal);

        var env = await BuildEnvironmentVariablesAsync(containerResource);
        Assert.Equal("http://otel-collector:4317", env["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        Assert.False(env.ContainsKey("OTEL_EXPORTER_OTLP_METRICS_ENDPOINT"));
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsRemovesTheSharedIdentityOnlyWhenItsOverrideIsSupplied()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag("pg17-0.116.0")
            .WithOpenTelemetryMetrics(
                endpoint: "http://otel-collector:4317",
                exportInterval: TimeSpan.FromSeconds(5),
                timeout: TimeSpan.FromSeconds(7),
                serviceName: "custom-service",
                serviceVersion: "9.9.9");

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);

        var script = await GetWrapperScriptAsync(SingleServerResource(app));

        Assert.Contains(
            $"jq '{MetricsBlockFilter}, .TelemetryOptions.ServiceName, .TelemetryOptions.ServiceVersion)'",
            script,
            StringComparison.Ordinal);

        // Tracing is never this package's to configure, even when the identity is overridden.
        Assert.DoesNotContain(".TelemetryOptions.Tracing", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsAccumulatesSuppliedOverridesAcrossCalls()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag("pg17-0.116.0")
            .WithOpenTelemetryMetrics(serviceName: "custom-service")
            .WithOpenTelemetryMetrics(endpoint: "http://otel-collector:4317");

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);

        var script = await GetWrapperScriptAsync(SingleServerResource(app));

        // The environment variables merge across calls, so the identity keys they have to beat do
        // too - and only those the caller actually supplied.
        Assert.Contains(
            $"jq '{MetricsBlockFilter}, .TelemetryOptions.ServiceName)'",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(".TelemetryOptions.ServiceVersion", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsWrapsTheEntrypointForPrivateMirrors()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageRegistry("registry.example.com")
            .WithOpenTelemetryMetrics();

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);

        var containerResource = SingleServerResource(app);
        Assert.Equal(GatewayConfigurationShell, containerResource.Entrypoint);
        Assert.Equal(3, (await BuildContainerArgsAsync(containerResource)).Count);
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsWrapsTheEntrypointForVersionsAfterTheAffectedFloor()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag("pg17-0.117.0")
            .WithOpenTelemetryMetrics();

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);

        var containerResource = SingleServerResource(app);
        Assert.Equal(GatewayConfigurationShell, containerResource.Entrypoint);
        Assert.Equal(3, (await BuildContainerArgsAsync(containerResource)).Count);
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsWrapsTheEntrypointWhenTheTagIsSelectedAfterTheCall()
    {
        // WithOpenTelemetryMetrics() cannot see the final image, so the decision has to be
        // deferred; the alternative silently drops the override for this ordering.
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithOpenTelemetryMetrics()
            .WithDocumentDBVersion(DocumentDBVersion.V0_114_0)
            .WithImageTag("pg17-0.116.0");

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);

        var containerResource = SingleServerResource(app);
        Assert.Equal(GatewayConfigurationShell, containerResource.Entrypoint);
        Assert.Equal(3, (await BuildContainerArgsAsync(containerResource)).Count);
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsKeepsTheStockEntrypointFor0114()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithDocumentDBVersion(DocumentDBVersion.V0_114_0)
            .WithOpenTelemetryMetrics();

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);

        var containerResource = SingleServerResource(app);
        Assert.Null(containerResource.Entrypoint);
        Assert.Empty(await BuildContainerArgsAsync(containerResource));
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsWrapsTheEntrypointForTheDefaultImage()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithOpenTelemetryMetrics();

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);

        var containerResource = SingleServerResource(app);
        Assert.Equal(GatewayConfigurationShell, containerResource.Entrypoint);
        Assert.Equal(3, (await BuildContainerArgsAsync(containerResource)).Count);
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsKeepsTheStockEntrypointForCustomImages()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImage("contoso/documentdb-local", "pg17-0.116.0")
            .WithOpenTelemetryMetrics();

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);

        var containerResource = SingleServerResource(app);
        Assert.Null(containerResource.Entrypoint);
        Assert.Empty(await BuildContainerArgsAsync(containerResource));
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsKeepsTheStockEntrypointForUnrecognizedTags()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag("latest")
            .WithOpenTelemetryMetrics();

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);

        var containerResource = SingleServerResource(app);
        Assert.Null(containerResource.Entrypoint);
        Assert.Empty(await BuildContainerArgsAsync(containerResource));
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsWrapsTheEntrypointWhenDisabled()
    {
        // A caller-supplied configuration file can enable metrics from JSON, which would beat
        // OTEL_METRICS_ENABLED=false. Explicitly disabling metrics has to win.
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag("pg17-0.116.0")
            .WithOpenTelemetryMetrics(enabled: false);

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);

        var containerResource = SingleServerResource(app);
        Assert.Equal(GatewayConfigurationShell, containerResource.Entrypoint);

        var script = await GetWrapperScriptAsync(containerResource);
        Assert.Contains($"jq '{MetricsBlockFilter})'", script, StringComparison.Ordinal);

        var env = await BuildEnvironmentVariablesAsync(containerResource);
        Assert.Equal("false", env["OTEL_METRICS_ENABLED"]);
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsDisabledStillSanitizesACallerSuppliedConfiguration()
    {
        // The scenario the disabled-case wrapper exists for: the caller points CONFIG_DIR at a
        // file that could enable metrics from JSON, and enabled: false has to beat it.
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag("pg17-0.116.0")
            .WithEnvironment("CONFIG_DIR", "/custom/documentdb/config")
            .WithOpenTelemetryMetrics(enabled: false);

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);

        var containerResource = SingleServerResource(app);
        Assert.Equal(GatewayConfigurationShell, containerResource.Entrypoint);

        var script = await GetWrapperScriptAsync(containerResource);
        Assert.Contains("c=\"$CONFIG_DIR\"", script, StringComparison.Ordinal);
        Assert.Contains($"jq '{MetricsBlockFilter})'", script, StringComparison.Ordinal);

        var env = await BuildEnvironmentVariablesAsync(containerResource);
        Assert.Equal("false", env["OTEL_METRICS_ENABLED"]);
        Assert.Equal("/custom/documentdb/config", env["CONFIG_DIR"]);
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsWrapsTheEntrypointWhenTheLastCallDisablesMetrics()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag("pg17-0.116.0")
            .WithOpenTelemetryMetrics()
            .WithOpenTelemetryMetrics(enabled: false);

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);

        var containerResource = SingleServerResource(app);
        Assert.Equal(GatewayConfigurationShell, containerResource.Entrypoint);

        var script = await GetWrapperScriptAsync(containerResource);
        Assert.Contains($"jq '{MetricsBlockFilter})'", script, StringComparison.Ordinal);

        var env = await BuildEnvironmentVariablesAsync(containerResource);
        Assert.Equal("false", env["OTEL_METRICS_ENABLED"]);
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsWrapsTheEntrypointWhenTheLastCallEnablesMetrics()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag("pg17-0.116.0")
            .WithOpenTelemetryMetrics(enabled: false)
            .WithOpenTelemetryMetrics();

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);

        var containerResource = SingleServerResource(app);
        Assert.Equal(GatewayConfigurationShell, containerResource.Entrypoint);

        var env = await BuildEnvironmentVariablesAsync(containerResource);
        Assert.Equal("true", env["OTEL_METRICS_ENABLED"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WithOpenTelemetryMetricsKeepsCallerSuppliedContainerArgumentsBehindTheWrapper(bool argumentsFirst)
    {
        var appBuilder = CreateLifecycleTestBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB").WithImageTag("pg17-0.116.0");

        if (argumentsFirst)
        {
            documentDB.WithArgs("--log-level", "trace").WithOpenTelemetryMetrics();
        }
        else
        {
            documentDB.WithOpenTelemetryMetrics().WithArgs("--log-level", "trace");
        }

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);

        var args = await BuildContainerArgsAsync(SingleServerResource(app));

        Assert.Equal(5, args.Count);
        Assert.Equal("-c", args[0]);
        Assert.Equal(GatewayConfigurationShellArgumentZero, args[2]);
        Assert.Equal("--log-level", args[3]);
        Assert.Equal("trace", args[4]);
    }

    [Theory]
    [InlineData("/custom/entrypoint.sh")]
    [InlineData("/bin/bash")]
    public async Task WithOpenTelemetryMetricsRejectsAnyCallerSuppliedEntrypoint(string entrypoint)
    {
        // Even /bin/bash is a caller-owned entrypoint: its arguments are the caller's, so
        // accepting it would splice the wrapper arguments into someone else's command line.
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag("pg17-0.116.0")
            .WithEntrypoint(entrypoint)
            .WithArgs("-c", "echo hi")
            .WithOpenTelemetryMetrics();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BuildAndRaiseBeforeStartAsync(appBuilder));

        Assert.Contains(entrypoint, exception.Message, StringComparison.Ordinal);
        Assert.Contains("WithOpenTelemetryMetrics", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsToleratesRepeatedLifecycleEvents()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag("pg17-0.116.0")
            .WithOpenTelemetryMetrics();

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);

        await RaiseBeforeStartAsync(app);
        await RaiseBeforeStartAsync(app);

        var containerResource = SingleServerResource(app);
        Assert.Equal(GatewayConfigurationShell, containerResource.Entrypoint);
        Assert.Equal(3, (await BuildContainerArgsAsync(containerResource)).Count);
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsRejectsArgumentsResolvedWithoutTheWrapperEntrypoint()
    {
        // The arguments only mean anything to the wrapper's own entrypoint. If they are resolved
        // while something else owns the container command, they would be spliced into it.
        var appBuilder = CreateLifecycleTestBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag("pg17-0.116.0")
            .WithOpenTelemetryMetrics();

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BuildContainerArgsAsync(documentDB.Resource));

        Assert.Contains("<image default>", exception.Message, StringComparison.Ordinal);
        Assert.Contains("WithOpenTelemetryMetrics", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsRejectsArgumentsWhenTheEntrypointIsReplacedLaterInTheSameStartup()
    {
        // A BeforeStartEvent subscriber registered after ours runs after ours, so the entrypoint
        // check in the event handler cannot see the replacement. The argument callback runs after
        // every subscriber, which is why it re-checks.
        var appBuilder = CreateLifecycleTestBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag("pg17-0.116.0")
            .WithOpenTelemetryMetrics();

        appBuilder.Eventing.Subscribe<BeforeStartEvent>((_, _) =>
        {
            documentDB.Resource.Entrypoint = "/late/entrypoint.sh";
            return Task.CompletedTask;
        });

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);

        Assert.Equal("/late/entrypoint.sh", SingleServerResource(app).Entrypoint);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BuildContainerArgsAsync(SingleServerResource(app)));

        Assert.Contains("/late/entrypoint.sh", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsFailsPublishWhenTheEntrypointIsReplacedLaterInTheSameStartup()
    {
        // End to end through the real publishing pipeline: no manifest may be written with the
        // wrapper's arguments attached to somebody else's entrypoint. The publisher reports the
        // failed step rather than letting the exception escape RunAsync, which is what makes
        // 'aspire publish' exit non-zero.
        var log = await ManifestUtils.PublishManifestExpectingFailureAsync(appBuilder =>
        {
            var documentDB = appBuilder.AddDocumentDB("DocumentDB")
                .WithImageTag("pg17-0.116.0")
                .WithOpenTelemetryMetrics();

            appBuilder.Eventing.Subscribe<BeforeStartEvent>((_, _) =>
            {
                documentDB.Resource.Entrypoint = "/late/entrypoint.sh";
                return Task.CompletedTask;
            });
        });

        Assert.Contains("publish-manifest' failed", log, StringComparison.Ordinal);
        Assert.Contains("/late/entrypoint.sh", log, StringComparison.Ordinal);
        Assert.Contains("WithOpenTelemetryMetrics", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsRejectsAnEntrypointReplacedAfterTheWrapperTookOwnership()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag("pg17-0.116.0")
            .WithOpenTelemetryMetrics();

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);

        SingleServerResource(app).Entrypoint = "/custom/entrypoint.sh";

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => RaiseBeforeStartAsync(app));
        Assert.Contains("/custom/entrypoint.sh", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pg17-0.114.0")]
    [InlineData("latest")]
    public async Task WithOpenTelemetryMetricsRejectsAnImageDowngradedAfterTheWrapperWasInstalled(string tag)
    {
        // The wrapper cannot be uninstalled once the entrypoint carries it: skipping the
        // arguments would leave /bin/bash with nothing to run, which starts no container at all.
        var appBuilder = CreateLifecycleTestBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag("pg17-0.116.0")
            .WithOpenTelemetryMetrics();

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);
        Assert.Equal(GatewayConfigurationShell, SingleServerResource(app).Entrypoint);

        documentDB.WithImageTag(tag);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => RaiseBeforeStartAsync(app));
        Assert.Contains(tag, exception.Message, StringComparison.Ordinal);
        Assert.Contains("entrypoint", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsRejectsACustomImageSwappedInAfterTheWrapperWasInstalled()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag("pg17-0.116.0")
            .WithOpenTelemetryMetrics();

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);
        Assert.Equal(GatewayConfigurationShell, SingleServerResource(app).Entrypoint);

        documentDB.WithImage("contoso/documentdb-local", "pg17-0.116.0");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => RaiseBeforeStartAsync(app));
        Assert.Contains("entrypoint", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pg17-0.116.0")]
    [InlineData("pg17-0.114.0")]
    public async Task WithOpenTelemetryMetricsRejectsADigestPinnedOfficialImage(string supersededTag)
    {
        // ContainerImageAnnotation makes tag and digest mutually exclusive, so a digest pin leaves
        // no tag to classify from and the DocumentDB version behind it is unknowable. Both stale
        // directions matter: pg17-0.116.0 would have been wrapped and pg17-0.114.0 would have been
        // skipped, and either guess is silently wrong.
        var appBuilder = CreateLifecycleTestBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(supersededTag)
            .WithImageSHA256("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")
            .WithOpenTelemetryMetrics();

        var image = documentDB.Resource.Annotations.OfType<ContainerImageAnnotation>().Last();
        Assert.Null(image.Tag);
        Assert.Equal("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", image.SHA256);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BuildAndRaiseBeforeStartAsync(appBuilder));

        Assert.Contains("digest", exception.Message, StringComparison.Ordinal);
        Assert.Contains("WithOpenTelemetryMetrics", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsClassifiesByTagWhenATagSupersedesAnEarlierDigest()
    {
        // The other direction of the same exclusivity: a later WithImageTag drops the digest, so
        // the version is knowable again and the wrapper must apply rather than keep failing.
        var appBuilder = CreateLifecycleTestBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithImageSHA256("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")
            .WithImageTag("pg17-0.116.0")
            .WithOpenTelemetryMetrics();

        var image = documentDB.Resource.Annotations.OfType<ContainerImageAnnotation>().Last();
        Assert.Null(image.SHA256);
        Assert.Equal("pg17-0.116.0", image.Tag);

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);

        var containerResource = SingleServerResource(app);
        Assert.Equal(GatewayConfigurationShell, containerResource.Entrypoint);
        Assert.Equal(3, (await BuildContainerArgsAsync(containerResource)).Count);
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsRejectsADigestPinnedOfficialImageWhenDisabled()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageSHA256("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")
            .WithOpenTelemetryMetrics(enabled: false);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BuildAndRaiseBeforeStartAsync(appBuilder));

        Assert.Contains("digest", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsRejectsADigestPinnedOfficialImageWhenResolvingArguments()
    {
        // The argument callback resolves independently of the lifecycle event, so it has to reach
        // the same conclusion rather than quietly emitting an unwrapped command line.
        var appBuilder = CreateLifecycleTestBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithImageSHA256("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")
            .WithOpenTelemetryMetrics();

        using var app = appBuilder.Build();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => BuildContainerArgsAsync(documentDB.Resource));
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsAllowsADigestPinnedCustomImage()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImage("contoso/documentdb-local", "pg17-0.116.0")
            .WithImageSHA256("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")
            .WithOpenTelemetryMetrics();

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);

        var containerResource = SingleServerResource(app);
        Assert.Null(containerResource.Entrypoint);
        Assert.Empty(await BuildContainerArgsAsync(containerResource));
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsWrapperScriptSurvivesPublisherArgumentProcessing()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag("pg17-0.116.0")
            .WithOpenTelemetryMetrics(serviceName: "custom-service");

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);

        var script = await GetWrapperScriptAsync(SingleServerResource(app));

        // azd evaluates '{...}' inside every container argument as a manifest binding expression
        // before rendering it, so a shell '${VAR}' or '${VAR:-default}' is at best passed through
        // by accident and at worst a hard publish failure.
        Assert.DoesNotContain('{', script);
        Assert.DoesNotContain('}', script);

        // A newline turns the rendered YAML scalar into a block scalar, which does not survive the
        // per-argument templating publishers use.
        Assert.DoesNotContain('\n', script);
        Assert.DoesNotContain('\r', script);

        Assert.StartsWith("set -e;", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsDoesNotInventAServiceNameOn0116()
    {
        // The shipped TelemetryOptions.ServiceName is left in place instead, so the gateway keeps
        // the identity its own configuration specifies.
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag("pg17-0.116.0")
            .WithOpenTelemetryMetrics();

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);

        var env = await BuildEnvironmentVariablesAsync(SingleServerResource(app));
        Assert.False(env.ContainsKey("OTEL_SERVICE_NAME"));
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsLeavesAnEnvironmentOnlyServiceNameSubordinateToTheConfigurationFile()
    {
        // WithEnvironment is not a WithOpenTelemetryMetrics override, so the JSON identity is not
        // removed and continues to win inside the gateway. The variable is still propagated.
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithEnvironment("OTEL_SERVICE_NAME", "caller-service")
            .WithOpenTelemetryMetrics();

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);

        var containerResource = SingleServerResource(app);
        var env = await BuildEnvironmentVariablesAsync(containerResource);
        Assert.Equal("caller-service", env["OTEL_SERVICE_NAME"]);

        var script = await GetWrapperScriptAsync(containerResource);
        Assert.DoesNotContain(".TelemetryOptions.ServiceName", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsRespectsExplicitFalse()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithOpenTelemetryMetrics(enabled: false);

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());

        var env = await BuildEnvironmentVariablesAsync(containerResource);
        Assert.Equal("false", env["OTEL_METRICS_ENABLED"]);
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsSetsExporterEndpoint()
    {
        const string Endpoint = "http://otel-collector:4317";

        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithOpenTelemetryMetrics(endpoint: Endpoint);

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());

        var env = await BuildEnvironmentVariablesAsync(containerResource);
        Assert.Equal(Endpoint, env["OTEL_EXPORTER_OTLP_METRICS_ENDPOINT"]);
        Assert.Equal("true", env["OTEL_METRICS_ENABLED"]);
        Assert.False(env.ContainsKey("OTEL_EXPORTER_OTLP_ENDPOINT"));
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsSetsExportInterval()
    {
        var interval = TimeSpan.FromSeconds(30);

        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithOpenTelemetryMetrics(exportInterval: interval);

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());

        var env = await BuildEnvironmentVariablesAsync(containerResource);
        Assert.Equal("30000", env["OTEL_METRIC_EXPORT_INTERVAL"]);
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsSetsTimeout()
    {
        var timeout = TimeSpan.FromSeconds(5);

        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithOpenTelemetryMetrics(timeout: timeout);

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());

        var env = await BuildEnvironmentVariablesAsync(containerResource);
        Assert.Equal("5000", env["OTEL_EXPORTER_OTLP_METRICS_TIMEOUT"]);
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsSetsServiceNameAndVersion()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithOpenTelemetryMetrics(serviceName: "documentdb-local", serviceVersion: "0.112.0");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());

        var env = await BuildEnvironmentVariablesAsync(containerResource);
        Assert.Equal("documentdb-local", env["OTEL_SERVICE_NAME"]);
        Assert.Equal("0.112.0", env["OTEL_SERVICE_VERSION"]);
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsFormatsTimeSpanAsInvariantMilliseconds()
    {
        var originalCulture = System.Threading.Thread.CurrentThread.CurrentCulture;

        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture =
                System.Globalization.CultureInfo.GetCultureInfo("tr-TR");

            var appBuilder = DistributedApplication.CreateBuilder();
            appBuilder.AddDocumentDB("DocumentDB")
                .WithOpenTelemetryMetrics(
                    exportInterval: TimeSpan.FromMilliseconds(1234567),
                    timeout: TimeSpan.FromMilliseconds(2500));

            using var app = appBuilder.Build();

            var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
            var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());

            var env = await BuildEnvironmentVariablesAsync(containerResource);
            Assert.Equal("1234567", env["OTEL_METRIC_EXPORT_INTERVAL"]);
            Assert.Equal("2500", env["OTEL_EXPORTER_OTLP_METRICS_TIMEOUT"]);
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsCoexistsWithObsoleteWithTelemetry()
    {
#pragma warning disable ASPIREDOCDB0001 // WithTelemetry is obsolete; coexistence test ensures no aliasing.
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithTelemetry(enabled: false)
            .WithOpenTelemetryMetrics(enabled: true, endpoint: "http://collector:4317");
#pragma warning restore ASPIREDOCDB0001

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());

        var env = await BuildEnvironmentVariablesAsync(containerResource);
        Assert.Equal("false", env["ENABLE_TELEMETRY"]);
        Assert.Equal("true", env["OTEL_METRICS_ENABLED"]);
        Assert.Equal("http://collector:4317", env["OTEL_EXPORTER_OTLP_METRICS_ENDPOINT"]);
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsMergesAcrossMultipleCalls()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithOpenTelemetryMetrics(endpoint: "http://first:4317", serviceName: "first")
            .WithOpenTelemetryMetrics(enabled: false, serviceName: "second");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());

        var env = await BuildEnvironmentVariablesAsync(containerResource);
        Assert.Equal("false", env["OTEL_METRICS_ENABLED"]);
        Assert.Equal("http://first:4317", env["OTEL_EXPORTER_OTLP_METRICS_ENDPOINT"]);
        Assert.Equal("second", env["OTEL_SERVICE_NAME"]);
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsRewritesEnabledOnEveryCall()
    {
        // Documented behavior: `enabled` is non-nullable with default `true`, so every call
        // rewrites OTEL_METRICS_ENABLED. A later call that omits `enabled` re-enables metrics
        // even when an earlier call disabled them. Callers must re-pass `enabled: false` to
        // preserve a disabled state.
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithOpenTelemetryMetrics(enabled: false)
            .WithOpenTelemetryMetrics(endpoint: "http://collector:4317");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());

        var env = await BuildEnvironmentVariablesAsync(containerResource);
        Assert.Equal("true", env["OTEL_METRICS_ENABLED"]);
        Assert.Equal("http://collector:4317", env["OTEL_EXPORTER_OTLP_METRICS_ENDPOINT"]);
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsAllowsZeroTimeSpan()
    {
        // Boundary of the non-negative TimeSpan guard: TimeSpan.Zero is accepted and emitted as "0".
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithOpenTelemetryMetrics(exportInterval: TimeSpan.Zero, timeout: TimeSpan.Zero);

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());

        var env = await BuildEnvironmentVariablesAsync(containerResource);
        Assert.Equal("0", env["OTEL_METRIC_EXPORT_INTERVAL"]);
        Assert.Equal("0", env["OTEL_EXPORTER_OTLP_METRICS_TIMEOUT"]);
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsAddsAllEnvironmentVariablesIn0114Manifest()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithDocumentDBVersion(DocumentDBVersion.V0_114_0)
            .WithOpenTelemetryMetrics(
                endpoint: "http://otel-collector:4317",
                enabled: true,
                exportInterval: TimeSpan.FromSeconds(60),
                timeout: TimeSpan.FromSeconds(10),
                serviceName: "documentdb-local",
                serviceVersion: "0.112.0");

        var manifest = await ManifestUtils.GetManifest(documentDB.Resource);

        Assert.Equal("true", manifest["env"]?["OTEL_METRICS_ENABLED"]?.GetValue<string>());
        Assert.Equal("http://otel-collector:4317", manifest["env"]?["OTEL_EXPORTER_OTLP_METRICS_ENDPOINT"]?.GetValue<string>());
        Assert.Equal("60000", manifest["env"]?["OTEL_METRIC_EXPORT_INTERVAL"]?.GetValue<string>());
        Assert.Equal("10000", manifest["env"]?["OTEL_EXPORTER_OTLP_METRICS_TIMEOUT"]?.GetValue<string>());
        Assert.Equal("documentdb-local", manifest["env"]?["OTEL_SERVICE_NAME"]?.GetValue<string>());
        Assert.Equal("0.112.0", manifest["env"]?["OTEL_SERVICE_VERSION"]?.GetValue<string>());
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsPublishesADeployableManifestFor0116()
    {
        var manifest = await ManifestUtils.PublishManifestAsync(appBuilder =>
            appBuilder.AddDocumentDB("DocumentDB")
                .WithOpenTelemetryMetrics(
                    endpoint: "http://otel-collector:4317",
                    exportInterval: TimeSpan.FromSeconds(30))
                .WithImageTag("pg17-0.116.0"));

        var resource = manifest["resources"]?["DocumentDB"];
        Assert.NotNull(resource);

        // The published resource still names the official image; the compatibility override is
        // carried entirely by manifest fields every publisher understands.
        Assert.Equal(
            "ghcr.io/documentdb/documentdb/documentdb-local:pg17-0.116.0",
            resource!["image"]?.GetValue<string>());
        Assert.Equal(GatewayConfigurationShell, resource["entrypoint"]?.GetValue<string>());

        var args = Assert.IsType<JsonArray>(resource["args"]);
        Assert.Equal(3, args.Count);
        Assert.Equal("-c", args[0]?.GetValue<string>());
        Assert.Equal(GatewayConfigurationShellArgumentZero, args[2]?.GetValue<string>());

        var script = args[1]?.GetValue<string>();
        Assert.NotNull(script);
        Assert.Contains($"jq '{MetricsBlockFilter})'", script!, StringComparison.Ordinal);
        Assert.Contains($"exec {GatewayEntrypointScriptPath} \"$@\"", script!, StringComparison.Ordinal);

        Assert.Equal("true", resource["env"]?["OTEL_METRICS_ENABLED"]?.GetValue<string>());
        Assert.Equal("http://otel-collector:4317", resource["env"]?["OTEL_EXPORTER_OTLP_METRICS_ENDPOINT"]?.GetValue<string>());
        Assert.Equal("30000", resource["env"]?["OTEL_METRIC_EXPORT_INTERVAL"]?.GetValue<string>());
        Assert.Null(resource["env"]?["OTEL_SERVICE_NAME"]);
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsPublishesAWrappedManifestWhenDisabled()
    {
        var manifest = await ManifestUtils.PublishManifestAsync(appBuilder =>
            appBuilder.AddDocumentDB("DocumentDB")
                .WithImageTag("pg17-0.116.0")
                .WithOpenTelemetryMetrics()
                .WithOpenTelemetryMetrics(enabled: false));

        var resource = manifest["resources"]?["DocumentDB"];
        Assert.NotNull(resource);

        Assert.Equal("false", resource!["env"]?["OTEL_METRICS_ENABLED"]?.GetValue<string>());

        // Disabling has to survive a configuration file that enables metrics from JSON, so the
        // wrapper is published for the disabled case too.
        Assert.Equal(GatewayConfigurationShell, resource["entrypoint"]?.GetValue<string>());
        var args = Assert.IsType<JsonArray>(resource["args"]);
        Assert.Contains($"jq '{MetricsBlockFilter})'", args[1]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsPublishesAWrappedManifestForTheDefaultImage()
    {
        var manifest = await ManifestUtils.PublishManifestAsync(appBuilder =>
            appBuilder.AddDocumentDB("DocumentDB").WithOpenTelemetryMetrics());

        var resource = manifest["resources"]?["DocumentDB"];
        Assert.NotNull(resource);

        Assert.Equal("true", resource!["env"]?["OTEL_METRICS_ENABLED"]?.GetValue<string>());
        Assert.Equal(GatewayConfigurationShell, resource["entrypoint"]?.GetValue<string>());
        Assert.Equal(3, Assert.IsType<JsonArray>(resource["args"]).Count);
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsPublishesAnUnwrappedManifestForCustomImages()
    {
        var manifest = await ManifestUtils.PublishManifestAsync(appBuilder =>
            appBuilder.AddDocumentDB("DocumentDB")
                .WithImage("contoso/documentdb-local", "pg17-0.116.0")
                .WithOpenTelemetryMetrics());

        var resource = manifest["resources"]?["DocumentDB"];
        Assert.NotNull(resource);

        Assert.Null(resource!["entrypoint"]);
        Assert.Null(resource["args"]);
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsFailsPublishForADigestPinnedOfficialImage()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ManifestUtils.PublishManifestAsync(appBuilder =>
                appBuilder.AddDocumentDB("DocumentDB")
                    .WithImageTag("pg17-0.114.0")
                    .WithImageSHA256("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")
                    .WithOpenTelemetryMetrics()));

        Assert.Contains("digest", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithOwnerAddsEnvironmentVariable()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithOwner("contoso");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());

        var env = await BuildEnvironmentVariablesAsync(containerResource);
        Assert.Equal("contoso", env["OWNER"]);
    }

    [Fact]
    public async Task VerifyManifestIncludesAdditionalConfigurationOptions()
    {
        var certPath = Path.GetFullPath(Path.Combine("TestData", "certs", "documentdb.pem"));
        var keyPath = Path.GetFullPath(Path.Combine("TestData", "certs", "documentdb.key"));
        var initDataPath = Path.GetFullPath(Path.Combine("TestData", "init"));
        var expectedCertTarget = $"/documentdb-cert-{Path.GetFileName(certPath)}";
        var expectedKeyTarget = $"/documentdb-key-{Path.GetFileName(keyPath)}";

#pragma warning disable ASPIREDOCDB0001 // WithTelemetry is obsolete; covered for back-compat in this manifest test.
        var appBuilder = DistributedApplication.CreateBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithLogLevel(DocumentDBLogLevel.Debug)
            .WithEnvironment("INIT_DATA", "true")
            .WithInitData(initDataPath)
            .WithTlsCertificate(certPath, keyPath)
            .WithTelemetry(enabled: false)
            .WithOwner("contoso")
            .WithoutExtendedRum()
            .WithoutUserCreation();
#pragma warning restore ASPIREDOCDB0001

        var manifest = await ManifestUtils.GetManifest(documentDB.Resource);

        Assert.Equal("debug", manifest["env"]?["DOCUMENTDB_LOG_LEVEL"]?.GetValue<string>());
        Assert.Equal("debug", manifest["env"]?["LOG_LEVEL"]?.GetValue<string>());
        Assert.Equal("false", manifest["env"]?["INIT_DATA"]?.GetValue<string>());
        Assert.Equal("/init_doc_db.d", manifest["env"]?["INIT_DATA_PATH"]?.GetValue<string>());
        Assert.Equal("true", manifest["env"]?["SKIP_INIT_DATA"]?.GetValue<string>());
        Assert.Equal(expectedCertTarget, manifest["env"]?["CERT_PATH"]?.GetValue<string>());
        Assert.Equal(expectedKeyTarget, manifest["env"]?["KEY_FILE"]?.GetValue<string>());
        Assert.Equal("false", manifest["env"]?["ENABLE_TELEMETRY"]?.GetValue<string>());
        Assert.Equal("contoso", manifest["env"]?["OWNER"]?.GetValue<string>());
        Assert.Equal("true", manifest["env"]?["DISABLE_EXTENDED_RUM"]?.GetValue<string>());
        Assert.Equal("false", manifest["env"]?["CREATE_USER"]?.GetValue<string>());
    }

    [Fact]
    public void WithPostgresEndpointAddsSecondEndpointAnnotation()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithPostgresEndpoint();

        using var app = appBuilder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());

        var endpoints = containerResource.Annotations.OfType<EndpointAnnotation>().ToList();
        Assert.Equal(2, endpoints.Count);

        var pgEndpoint = Assert.Single(endpoints, e => e.Name == "postgres");
        Assert.Equal(9712, pgEndpoint.TargetPort);
        Assert.Null(pgEndpoint.Port);
        Assert.Equal(ProtocolType.Tcp, pgEndpoint.Protocol);
        Assert.Equal("postgresql", pgEndpoint.UriScheme);
    }

    [Fact]
    public void WithPostgresEndpointBindsCustomHostPort()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithPostgresEndpoint(15432);

        using var app = appBuilder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());

        var pgEndpoint = Assert.Single(
            containerResource.Annotations.OfType<EndpointAnnotation>(),
            e => e.Name == "postgres");
        Assert.Equal(9712, pgEndpoint.TargetPort);
        Assert.Equal(15432, pgEndpoint.Port);
    }

    [Fact]
    public void AddDocumentDBDoesNotAddPostgresEndpointByDefault()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB");

        using var app = appBuilder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());

        var endpoints = containerResource.Annotations.OfType<EndpointAnnotation>().ToList();
        Assert.Single(endpoints);
        Assert.DoesNotContain(endpoints, e => e.Name == "postgres");
    }

    [Fact]
    public void PostgresConnectionStringExpressionThrowsWhenEndpointNotAdded()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB");

        var exception = Assert.Throws<InvalidOperationException>(
            () => _ = documentDB.Resource.PostgresConnectionStringExpression);

        Assert.Contains("WithPostgresEndpoint", exception.Message);
    }

    [Fact]
    public void PostgresConnectionStringExpressionIncludesCredentialsAndDefaultDatabase()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithPostgresEndpoint();

        var expression = documentDB.Resource.PostgresConnectionStringExpression.ValueExpression;

        Assert.Equal(
            "postgresql://admin:{DocumentDB-password.value}@" +
            "{DocumentDB.bindings.postgres.host}:{DocumentDB.bindings.postgres.port}/postgres",
            expression);
    }

    [Fact]
    public async Task PostgresConnectionStringResolvesToReachableUri()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithPostgresEndpoint()
            .WithEndpoint("postgres", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 25432));

        using var app = appBuilder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());

        var passwordParameter = Assert.IsType<ParameterResource>(containerResource.PasswordParameter);
        var password = await passwordParameter.GetValueAsync(default);
        Assert.NotNull(password);

        var connectionString = await containerResource.PostgresConnectionStringExpression.GetValueAsync(default);
        Assert.NotNull(connectionString);

        var uri = new Uri(connectionString!);
        Assert.Equal("postgresql", uri.Scheme);
        Assert.Equal("localhost", uri.Host);
        Assert.Equal(25432, uri.Port);
        Assert.Equal("/postgres", uri.AbsolutePath);
        var userInfo = uri.UserInfo.Split(':', 2);
        Assert.Equal("admin", userInfo[0]);
        Assert.Equal(password!, userInfo[1]);
        Assert.Equal(string.Empty, uri.Query);
    }

    [Fact]
    public async Task VerifyManifestIncludesPostgresEndpointWhenOptedIn()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithPostgresEndpoint();

        var manifest = await ManifestUtils.GetManifest(documentDB.Resource);

        var bindings = manifest["bindings"];
        Assert.NotNull(bindings);

        var tcpBinding = bindings!["tcp"];
        Assert.NotNull(tcpBinding);
        Assert.Equal(10260, tcpBinding!["targetPort"]?.GetValue<int>());

        var pgBinding = bindings["postgres"];
        Assert.NotNull(pgBinding);
        Assert.Equal(9712, pgBinding!["targetPort"]?.GetValue<int>());
        Assert.Equal("postgresql", pgBinding["scheme"]?.GetValue<string>());
    }

    [Fact]
    public void WithPostgresEndpointCalledTwiceThrowsInvalidOperation()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithPostgresEndpoint();

        var exception = Assert.Throws<InvalidOperationException>(
            () => documentDB.WithPostgresEndpoint());

        Assert.Contains("already been added", exception.Message);
    }

    [Fact]
    public async Task WithPostgresEndpointSetsAllowExternalConnectionsEnvironmentVariable()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithPostgresEndpoint();

        using var app = appBuilder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());

        var env = await BuildEnvironmentVariablesAsync(containerResource);
        Assert.Equal("true", env["ALLOW_EXTERNAL_CONNECTIONS"]);
    }

    [Fact]
    public async Task AddDocumentDBDoesNotSetAllowExternalConnectionsByDefault()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB");

        using var app = appBuilder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());

        var env = await BuildEnvironmentVariablesAsync(containerResource);
        Assert.False(env.ContainsKey("ALLOW_EXTERNAL_CONNECTIONS"));
    }

    // ---------------------------------------------------------------------
    // WithPostgresEndpoint() v0.112.0 floor guard (issue #71)
    //
    // The guard is implemented via a BeforeResourceStartedEvent subscription
    // so that callers chaining WithImageTag(...) AFTER WithPostgresEndpoint()
    // still get validated against the final effective tag. These tests publish
    // BeforeResourceStartedEvent synthetically so they stay pure unit tests
    // (no Docker, no real app StartAsync).
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("pg17-0.112.0")]
    [InlineData("pg17-0.113.0")]
    [InlineData("pg17-1.0.0")]
    [InlineData("pg15-0.112.0")]
    [InlineData("pg16-0.200.0")]
    public async Task WithPostgresEndpointAllowsV0_112_0AndAbove(string tag)
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(tag)
            .WithPostgresEndpoint();

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        await PublishBeforeResourceStartedAsync(app, resource);
    }

    [Fact]
    public async Task WithPostgresEndpointWithImageTagV0_111_0Throws()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag("pg17-0.111.0")
            .WithPostgresEndpoint();

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PublishBeforeResourceStartedAsync(app, resource));

        Assert.Contains("DocumentDB", ex.Message);
        Assert.Contains("pg17-0.111.0", ex.Message);
        Assert.Contains("0.112.0", ex.Message);
        Assert.Contains("WithPostgresEndpoint", ex.Message);
    }

    [Fact]
    public async Task WithPostgresEndpointWithDocumentDBVersionV0_111_0Throws()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithDocumentDBVersion(DocumentDBVersion.V0_111_0)
            .WithPostgresEndpoint();

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PublishBeforeResourceStartedAsync(app, resource));

        Assert.Contains("pg17-0.111.0", ex.Message);
        Assert.Contains("0.112.0", ex.Message);
    }

    [Fact]
    public async Task WithPostgresEndpointWithUnknownTagPatternLogsWarningOnceAndAllows()
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.Services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider(sink));

        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag("nightly")
            .WithPostgresEndpoint();

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        // Publish the event TWICE to prove the warning is one-shot.
        await PublishBeforeResourceStartedAsync(app, resource, useEmptyServices: true);
        await PublishBeforeResourceStartedAsync(app, resource, useEmptyServices: true);

        var warnings = sink.LogEntries.Where(e => e.Level == LogLevel.Warning).ToList();
        Assert.Single(warnings);
        Assert.Contains("nightly", warnings[0].Message);
        Assert.Contains("pg{NN}-X.Y.Z", warnings[0].Message);
    }

    [Fact]
    public async Task WithPostgresEndpointGuardHonoursLastCallWins()
    {
        // WithImageTag chained AFTER WithPostgresEndpoint must still be detected,
        // because the guard reads the effective ContainerImageAnnotation at event time,
        // not at WithPostgresEndpoint() call time.
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithPostgresEndpoint()
            .WithImageTag("pg17-0.111.0");

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PublishBeforeResourceStartedAsync(app, resource));

        Assert.Contains("pg17-0.111.0", ex.Message);
    }

    [Fact]
    public async Task WithPostgresEndpointGuardSkippedForCustomImage()
    {
        // A fork using a non-curated image name is exempt with a warning.
        var sink = new CapturingLoggerSink();
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.Services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider(sink));

        appBuilder.AddDocumentDB("DocumentDB")
            .WithImage("forks/my-build", "pg17-0.110.0")
            .WithPostgresEndpoint();

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        // Must NOT throw, even though the tag is < v0.112.0.
        await PublishBeforeResourceStartedAsync(app, resource, useEmptyServices: true);
        await PublishBeforeResourceStartedAsync(app, resource, useEmptyServices: true);

        var warnings = sink.LogEntries.Where(e => e.Level == LogLevel.Warning).ToList();
        Assert.Single(warnings);
        Assert.Contains("custom image", warnings[0].Message);
        Assert.Contains("forks/my-build", warnings[0].Message);
    }

    [Fact]
    public async Task WithPostgresEndpointGuardSkippedForCustomImageWithCustomRegistry()
    {
        // Confirm that ContainerImageAnnotation.Registry does NOT factor into the
        // image-name carve-out: the curated image hosted on a private registry is
        // still subject to the guard, while a custom image is exempt regardless of
        // registry.
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImage("forks/my-build", "pg17-0.110.0")
            .WithImageRegistry("registry.example.com")
            .WithPostgresEndpoint();

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        // Must NOT throw.
        await PublishBeforeResourceStartedAsync(app, resource);
    }

    [Fact]
    public async Task WithPostgresEndpointGuardEnforcedWhenOnlyRegistryOverridden()
    {
        // Mirror image: a private mirror of the curated documentdb-local image MUST
        // still be guarded - only the image NAME exempts, not the registry.
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageRegistry("registry.example.com")
            .WithImageTag("pg17-0.111.0")
            .WithPostgresEndpoint();

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => PublishBeforeResourceStartedAsync(app, resource));
    }

    [Fact]
    public async Task WithPostgresEndpointDefaultTagStartsOnceLatestMeetsFloor()
    {
        // The WithPostgresEndpoint() guard (issue #71) rejects container tags older than
        // MinimumPostgresEndpointVersion, converting a silent PostgreSQL auth failure into a
        // loud one. DocumentDBVersions.Latest is now at or above that floor, so the default
        // flow - AddDocumentDB().WithPostgresEndpoint() with no explicit tag - must start
        // cleanly. If a future Latest ever regressed below the floor, this test would fail,
        // which is the intent.
        Assert.True(Version.Parse(DocumentDBVersions.Latest) >= DocumentDBContainerImageTags.MinimumPostgresEndpointVersion);

        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithPostgresEndpoint(); // intentionally no WithImageTag

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        await PublishBeforeResourceStartedAsync(app, resource);
    }

    [Fact]
    public async Task WithPostgresEndpointGuardDoesNotFireDuringManifestGeneration()
    {
        // Manifest generation goes through Aspire's publish pipeline, which does NOT
        // publish BeforeResourceStartedEvent (no container is started). The guard must
        // not interfere with `azd publish` / `--publisher manifest` flows, even when
        // pinning a pre-v0.112 tag.
        var appBuilder = DistributedApplication.CreateBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag("pg17-0.111.0")
            .WithPostgresEndpoint();

        // Generating the manifest must not throw.
        var manifest = await ManifestUtils.GetManifest(documentDB.Resource);

        Assert.NotNull(manifest);
        var bindings = manifest["bindings"];
        Assert.NotNull(bindings);
        Assert.NotNull(bindings!["postgres"]);
    }

    [Fact]
    public async Task WithPostgresEndpointGuardReadsLastWithImageTagCall()
    {
        // Multiple WithImageTag calls: the LAST one wins (Aspire mutates the single
        // ContainerImageAnnotation in place). The guard must observe the final value,
        // not an intermediate one.
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithPostgresEndpoint()
            .WithImageTag("pg17-0.111.0")  // would throw...
            .WithImageTag("pg17-0.112.0"); // ...but this overrides.

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        // Must NOT throw - the final tag is >= floor.
        await PublishBeforeResourceStartedAsync(app, resource);
    }

    // ---------------------------------------------------------------------
    // PG-variant availability floor (pg18 exists upstream only from 0.114.0)
    //
    // Every DocumentDBVersion x DocumentDBPostgresVersion pair produces a
    // well-formed pg{NN}-X.Y.Z tag, but not every pair exists on GHCR. Same
    // BeforeResourceStartedEvent mechanism as the WithPostgresEndpoint floor
    // above, because only the effective tag at start time can be judged:
    // "last call wins" means selecting Pg18 before V0_114_0 is legitimate.
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("pg18-0.113.0")]
    [InlineData("pg18-0.112.0")]
    [InlineData("pg18-0.100.0")]
    public async Task Pg18WithADocumentDBVersionBelowV0_114_0Throws(string tag)
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB").WithImageTag(tag);

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PublishBeforeResourceStartedAsync(app, resource));

        Assert.Contains(tag, ex.Message);
        Assert.Contains("pg18", ex.Message);
        Assert.Contains("0.114.0", ex.Message);
    }

    [Fact]
    public async Task Pg18ViaStronglyTypedSelectorsBelowV0_114_0Throws()
    {
        // The combination the strongly-typed API makes easiest to reach: neither call is wrong
        // on its own, and the resulting tag has never existed on GHCR.
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithPostgresVersion(DocumentDBPostgresVersion.Pg18)
            .WithDocumentDBVersion(DocumentDBVersion.V0_113_0);

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PublishBeforeResourceStartedAsync(app, resource));

        Assert.Contains("pg18-0.113.0", ex.Message);
        Assert.Contains("WithPostgresVersion", ex.Message);
    }

    [Theory]
    [InlineData("pg18-0.114.0")]
    [InlineData("pg18-1.0.0")]
    [InlineData("pg17-0.109.0")]
    [InlineData("pg15-0.100.0")]
    public async Task PgVariantFloorAllowsPublishedCombinations(string tag)
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB").WithImageTag(tag);

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        await PublishBeforeResourceStartedAsync(app, resource);
    }

    [Fact]
    public async Task PgVariantFloorHonoursLastCallWins()
    {
        // Pg18 selected before the version that publishes it must NOT throw: the guard reads the
        // effective ContainerImageAnnotation at event time, not at WithPostgresVersion() time.
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithPostgresVersion(DocumentDBPostgresVersion.Pg18)
            .WithDocumentDBVersion(DocumentDBVersion.V0_114_0);

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        await PublishBeforeResourceStartedAsync(app, resource);
    }

    [Theory]
    [InlineData("nightly")]
    [InlineData("pg18-0.113.0-rc.1")]
    public async Task PgVariantFloorIsNotEnforcedOnUnrecognisedTags(string tag)
    {
        // Consistent with the WithPostgresEndpoint floor: a caller pinning a pre-release or a
        // custom build is not surprised by an unactionable hard failure.
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB").WithImageTag(tag);

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        await PublishBeforeResourceStartedAsync(app, resource);
    }

    [Fact]
    public async Task PgVariantFloorIsNotEnforcedOnCustomImages()
    {
        // A fork publishing its own images decides its own variant matrix — and, unlike the
        // WithPostgresEndpoint guard, this one is always subscribed, so it must stay silent
        // rather than warn on every app that pins a custom image.
        var sink = new CapturingLoggerSink();
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.Services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider(sink));

        appBuilder.AddDocumentDB("DocumentDB").WithImage("forks/my-build", "pg18-0.110.0");

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        await PublishBeforeResourceStartedAsync(app, resource, useEmptyServices: true);

        Assert.DoesNotContain(sink.LogEntries, e => e.Level == LogLevel.Warning);
    }

    private static Task PublishBeforeResourceStartedAsync(DistributedApplication app, IResource resource, bool useEmptyServices = false)
    {
        var eventing = app.Services.GetRequiredService<IDistributedApplicationEventing>();
        // useEmptyServices=true forces the guard's logger-resolution fallback path
        // (ILoggerFactory) instead of ResourceLoggerService. This lets tests
        // capture the warning text via a CapturingLoggerProvider registered on the
        // app's service collection, without needing to inspect ResourceLoggerService's
        // internal per-resource log buffer.
        var services = useEmptyServices
            ? new ServiceCollection().AddSingleton(app.Services.GetRequiredService<ILoggerFactory>()).BuildServiceProvider()
            : app.Services;
        var evt = new BeforeResourceStartedEvent(resource, services);
        return eventing.PublishAsync(evt, EventDispatchBehavior.BlockingSequential, CancellationToken.None);
    }

    /// <summary>
    /// Publishes <see cref="BeforeResourceStartedEvent"/> with services that expose both the
    /// capturable <see cref="ILoggerFactory"/> fallback and the application model, which the
    /// storage guard needs to see sibling resources.
    /// </summary>
    private static Task PublishBeforeResourceStartedWithModelAsync(DistributedApplication app, IResource resource)
    {
        var eventing = app.Services.GetRequiredService<IDistributedApplicationEventing>();
        var services = new ServiceCollection()
            .AddSingleton(app.Services.GetRequiredService<ILoggerFactory>())
            .AddSingleton(app.Services.GetRequiredService<DistributedApplicationModel>())
            .BuildServiceProvider();
        var evt = new BeforeResourceStartedEvent(resource, services);
        return eventing.PublishAsync(evt, EventDispatchBehavior.BlockingSequential, CancellationToken.None);
    }

    private sealed class CapturingLoggerSink
    {
        public List<(LogLevel Level, string Category, string Message)> LogEntries { get; } = new();
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly CapturingLoggerSink _sink;
        public CapturingLoggerProvider(CapturingLoggerSink sink) => _sink = sink;
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_sink, categoryName);
        public void Dispose() { }

        private sealed class CapturingLogger : ILogger
        {
            private readonly CapturingLoggerSink _sink;
            private readonly string _category;
            public CapturingLogger(CapturingLoggerSink sink, string category)
            {
                _sink = sink;
                _category = category;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                _sink.LogEntries.Add((logLevel, _category, formatter(state, exception)));
            }
        }
    }

    [Fact]
    public async Task WithTlsCertificateUsesDistinctTargetsWhenFileNamesMatch()
    {
        var certPath = Path.GetFullPath(Path.Combine("TestData", "certs", "shared.pem"));
        var keyPath = Path.GetFullPath(Path.Combine("TestData", "keys", "shared.pem"));
        var expectedCertTarget = "/documentdb-cert-shared.pem";
        var expectedKeyTarget = "/documentdb-key-shared.pem";

        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithTlsCertificate(certPath, keyPath);

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());

        Assert.True(containerResource.TryGetContainerMounts(out var mounts));
        var certMount = Assert.Single(mounts, mount => mount.Source == certPath);
        var keyMount = Assert.Single(mounts, mount => mount.Source == keyPath);

        Assert.Equal(expectedCertTarget, certMount.Target);
        Assert.Equal(expectedKeyTarget, keyMount.Target);
        Assert.NotEqual(certMount.Target, keyMount.Target);

        var env = await BuildEnvironmentVariablesAsync(containerResource);
        Assert.Equal(expectedCertTarget, env["CERT_PATH"]);
        Assert.Equal(expectedKeyTarget, env["KEY_FILE"]);
    }

    [Fact]
    public async Task WithLogLevelThrowsForUndefinedEnumValue()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithLogLevel((DocumentDBLogLevel)99);

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => BuildEnvironmentVariablesAsync(containerResource));
    }

    [Fact]
    public async Task WithTelemetryDefaultsToEnabled()
    {
#pragma warning disable ASPIREDOCDB0001 // WithTelemetry is obsolete; default-value behavior retained for binary compatibility.
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithTelemetry();
#pragma warning restore ASPIREDOCDB0001

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());

        var env = await BuildEnvironmentVariablesAsync(containerResource);
        Assert.Equal("true", env["ENABLE_TELEMETRY"]);
    }

    private static Dictionary<string, string> AssertConnectionString(
        string connectionString,
        string? expectedDatabaseName,
        string expectedPassword,
        params (string Name, string Value)[] expectedQueryParameters)
    {
        var uri = new Uri(connectionString);
        Assert.Equal("mongodb", uri.Scheme);
        Assert.Equal("localhost", uri.Host);
        Assert.Equal(10260, uri.Port);
        Assert.Equal(expectedDatabaseName is null ? "/" : $"/{expectedDatabaseName}", uri.AbsolutePath);

        var userInfo = uri.UserInfo.Split(':', 2);
        Assert.Equal("admin", userInfo[0]);
        Assert.Equal(expectedPassword, userInfo[1]);

        var queryParameters = ParseQueryParameters(uri.Query);
        foreach (var (name, value) in expectedQueryParameters)
        {
            Assert.True(queryParameters.TryGetValue(name, out var actualValue), $"Expected query parameter '{name}' in '{connectionString}'.");
            Assert.Equal(value, actualValue);
        }

        return queryParameters;
    }

    private static Dictionary<string, string> AssertConnectionStringExpression(
        string connectionStringExpression,
        string resourceName,
        string? expectedDatabaseName,
        params (string Name, string Value)[] expectedQueryParameters)
    {
        return AssertConnectionStringExpression(
            connectionStringExpression,
            resourceName,
            expectedDatabaseName,
            expectedUserExpression: "admin",
            expectedPasswordExpression: null,
            expectedQueryParameters);
    }

    private static Dictionary<string, string> AssertConnectionStringExpression(
        string connectionStringExpression,
        string resourceName,
        string? expectedDatabaseName,
        string expectedUserExpression,
        string? expectedPasswordExpression,
        params (string Name, string Value)[] expectedQueryParameters)
    {
        Assert.StartsWith("mongodb://", connectionStringExpression);

        expectedPasswordExpression ??= $"{{{resourceName}-password.value}}";

        var valueWithoutScheme = connectionStringExpression["mongodb://".Length..];
        var querySeparatorIndex = valueWithoutScheme.IndexOf('?');
        var authorityAndPath = querySeparatorIndex >= 0 ? valueWithoutScheme[..querySeparatorIndex] : valueWithoutScheme;
        var query = querySeparatorIndex >= 0 ? valueWithoutScheme[(querySeparatorIndex + 1)..] : string.Empty;

        var userInfoSeparatorIndex = authorityAndPath.IndexOf('@');
        Assert.True(userInfoSeparatorIndex >= 0, $"Expected user info in '{connectionStringExpression}'.");

        var userInfo = authorityAndPath[..userInfoSeparatorIndex];
        var hostAndPath = authorityAndPath[(userInfoSeparatorIndex + 1)..];
        var userInfoSegments = userInfo.Split(':', 2);
        Assert.Equal(2, userInfoSegments.Length);
        Assert.Equal(expectedUserExpression, userInfoSegments[0]);
        Assert.Equal(expectedPasswordExpression, userInfoSegments[1]);

        var pathSeparatorIndex = hostAndPath.IndexOf('/');
        var hostPort = pathSeparatorIndex >= 0 ? hostAndPath[..pathSeparatorIndex] : hostAndPath;
        var databasePath = pathSeparatorIndex >= 0 ? hostAndPath[pathSeparatorIndex..] : string.Empty;
        Assert.Equal($"{{{resourceName}.bindings.tcp.host}}:{{{resourceName}.bindings.tcp.port}}", hostPort);
        Assert.Equal(expectedDatabaseName is null ? string.Empty : $"/{expectedDatabaseName}", databasePath);

        var queryParameters = ParseQueryParameters(query);
        foreach (var (name, value) in expectedQueryParameters)
        {
            Assert.True(queryParameters.TryGetValue(name, out var actualValue), $"Expected query parameter '{name}' in '{connectionStringExpression}'.");
            Assert.Equal(value, actualValue);
        }

        return queryParameters;
    }

    private static Dictionary<string, string> ParseQueryParameters(string query)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var segments = part.Split('=', 2);
            parameters[segments[0]] = segments.Length == 2 ? Uri.UnescapeDataString(segments[1]) : string.Empty;
        }

        return parameters;
    }

    private static async Task<Dictionary<string, object>> BuildEnvironmentVariablesAsync(DocumentDBServerResource resource)
    {
        var environmentVariables = new Dictionary<string, object>(StringComparer.Ordinal);
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);

        foreach (var annotation in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await annotation.Callback(new EnvironmentCallbackContext(executionContext, resource, environmentVariables, CancellationToken.None));
        }

        return environmentVariables;
    }

    /// <summary>
    /// Creates a publish-mode builder for tests that raise <see cref="BeforeStartEvent"/>.
    /// </summary>
    /// <remarks>
    /// Raising that event on a run-mode builder also runs Aspire's built-in DCP subscriber, which
    /// requires a DCP installation these model-level tests deliberately do not have. The gateway
    /// compatibility wrapper resolves identically in both modes, and the Docker suite covers what
    /// run mode actually launches.
    /// </remarks>
    private static IDistributedApplicationBuilder CreateLifecycleTestBuilder() =>
        DistributedApplication.CreateBuilder(["Publishing:Publisher=manifest", "Publishing:OutputPath=./"]);

    /// <summary>
    /// Builds the application and raises <see cref="BeforeStartEvent"/>, which is the point at
    /// which the OpenTelemetry gateway compatibility wrapper resolves the resource's final image.
    /// Both the orchestrator and the manifest publisher raise it before they read the resource.
    /// </summary>
    private static async Task<DistributedApplication> BuildAndRaiseBeforeStartAsync(
        IDistributedApplicationBuilder appBuilder)
    {
        var app = appBuilder.Build();

        try
        {
            await RaiseBeforeStartAsync(app);
        }
        catch
        {
            app.Dispose();
            throw;
        }

        return app;
    }

    private static async Task RaiseBeforeStartAsync(DistributedApplication app)
    {
        var eventing = app.Services.GetRequiredService<IDistributedApplicationEventing>();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await eventing.PublishAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);
    }

    private static DocumentDBServerResource SingleServerResource(DistributedApplication app) =>
        Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

    private static async Task<IReadOnlyList<object>> BuildContainerArgsAsync(DocumentDBServerResource resource)
    {
        var args = new List<object>();

        foreach (var annotation in resource.Annotations.OfType<CommandLineArgsCallbackAnnotation>())
        {
            await annotation.Callback(new CommandLineArgsCallbackContext(args, resource, CancellationToken.None));
        }

        return args;
    }

    private static async Task<string> GetWrapperScriptAsync(DocumentDBServerResource resource)
    {
        var args = await BuildContainerArgsAsync(resource);
        Assert.Equal(3, args.Count);
        return Assert.IsType<string>(args[1]);
    }
}
