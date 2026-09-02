// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json.Nodes;
using System.Runtime.CompilerServices;
using System.Net.Sockets;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    // The official image with the registry folded into the image annotation instead of kept
    // beside it. Aspire joins the two and never re-splits them, so this resolves to exactly the
    // reference the default spelling resolves to.
    private const string QualifiedOfficialImage =
        DocumentDBContainerImageTags.Registry + "/" + DocumentDBContainerImageTags.Image;

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

        var expectedManifest = $$"""
            {
              "type": "container.v0",
              "connectionString": "mongodb://admin:{DocumentDB-password.value}@{DocumentDB.bindings.tcp.host}:{DocumentDB.bindings.tcp.port}?authSource=admin\u0026authMechanism=SCRAM-SHA-256\u0026tls=true\u0026tlsInsecure=true",
              "image": "{{DocumentDBContainerImageTags.Registry}}/{{DocumentDBContainerImageTags.Image}}:{{DocumentDBContainerImageTags.Tag}}",
              "env": {
                "USERNAME": "admin",
                "PASSWORD": "{DocumentDB-password.value}",
                "DATA_PATH": "/data"
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
              "connectionString": "mongodb://admin:{DocumentDB-password.value}@{DocumentDB.bindings.tcp.host}:{DocumentDB.bindings.tcp.port}/mydb?authSource=admin\u0026authMechanism=SCRAM-SHA-256\u0026tls=true\u0026tlsInsecure=true"
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

    /// <summary>
    /// A builder whose DCP paths are stubbed. Tests that drive the real configuration pipeline
    /// publish <see cref="BeforeStartEvent"/> — the event a real start publishes, and the one that
    /// installs the storage guard's callbacks — which also runs Aspire's own DCP subscriber, and
    /// that one insists on a DCP installation no unit test has.
    /// </summary>
    private static IDistributedApplicationBuilder CreateAppBuilder(CapturingLoggerSink? sink = null)
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.Configuration["DcpPublisher:CliPath"] = "/aspire-unit-tests/dcp";
        appBuilder.Configuration["DcpPublisher:DashboardPath"] = "/aspire-unit-tests/dashboard";

        if (sink is not null)
        {
            // The guard's advisory warnings go to the AppHost's own logging, not to the callback
            // context, because the context's logger is not wired up during the pass that actually
            // evaluates the callback.
            appBuilder.Services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider(sink));
        }

        return appBuilder;
    }

    // ---------------------------------------------------------------------
    // Data storage rules
    //
    // Every test below drives the resource's real configuration pipeline —
    // the same ExecutionConfigurationBuilder Aspire's container creator uses —
    // rather than invoking the guard's callbacks by hand. That is the point of
    // the guard: it is part of the pipeline, so what it judged and what the
    // container receives cannot be two different things.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ReadOnlyVolumeOnDefaultDataPathThrowsAtStart()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithVolume("raw-data", "/data", isReadOnly: true);

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "DocumentDB"));

        Assert.Contains("mounts its data directory ('/data') read-only", exception.Message, StringComparison.Ordinal);
        Assert.Contains("volume 'raw-data'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Read-only file system", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadOnlyBindMountOnCustomDataPathThrowsAtStart()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithDataVolume(name: "writable", targetPath: "/pgdata")
            .WithBindMount("/host/data", "/pgdata", isReadOnly: true);

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "DocumentDB"));

        Assert.Contains("data directory ('/pgdata') read-only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WritableDataMountStartsWithoutStorageWarnings()
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = CreateAppBuilder(sink);
        appBuilder.AddDocumentDB("DocumentDB").WithDataVolume();

        using var app = appBuilder.Build();

        await ConfigureResourceAsync(app, "DocumentDB", sink);

        Assert.DoesNotContain(sink.LogEntries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task DuplicateDataMountsThrowAtStart()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithDataVolume(name: "first")
            .WithDataVolume(name: "second");

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "DocumentDB"));

        Assert.Contains("2 mounts on '/data'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("duplicate mount targets", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The second resource to reach the shared directory is the one that fails, because it is the
    /// one whose container the image would refuse (or, without an interlock, the one that would
    /// corrupt the cluster). The first is left alone: its configuration is fine on its own.
    /// </summary>
    [Fact]
    public async Task DataVolumeSharedWithAnotherDocumentDBResourceThrowsAtStart()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("primary").WithImageTag(InterlockedTag).WithDataVolume(name: "shared-data");
        appBuilder.AddDocumentDB("secondary").WithImageTag(InterlockedTag).WithDataVolume(name: "shared-data");

        using var app = appBuilder.Build();

        await ConfigureResourceAsync(app, "primary");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "secondary"));

        Assert.Contains("'secondary' and DocumentDB resource 'primary'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("volume 'shared-data'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("exclusive lock", exception.Message, StringComparison.Ordinal);
        Assert.Contains("another DocumentDB container is already using the data directory", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DataVolumeSharedOnTheDefaultImageDescribesThatImagesActualBehaviour()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("primary").WithDataVolume(name: "shared-data");
        appBuilder.AddDocumentDB("secondary").WithDataVolume(name: "shared-data");

        using var app = appBuilder.Build();

        await ConfigureResourceAsync(app, "primary");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "secondary"));

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
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("primary").WithDataBindMount("./shared");
        appBuilder.AddDocumentDB("secondary").WithDataBindMount("./shared/");

        using var app = appBuilder.Build();

        await ConfigureResourceAsync(app, "primary");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "secondary"));

        Assert.Contains("host directory", exception.Message, StringComparison.Ordinal);
        Assert.Contains("give each resource its own storage", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Aspire records a rooted bind source exactly as written, so two spellings of one host
    /// directory reach the model unequal. They are still one directory to the host, and on an
    /// image with no interlock nothing at runtime would notice.
    /// </summary>
    [Theory]
    [InlineData("/srv/documentdb", "/srv/documentdb/.")]
    [InlineData("/srv/documentdb", "/srv/documentdb/../documentdb")]
    [InlineData("/srv/documentdb", "/srv/./documentdb//")]
    [InlineData("/srv/documentdb/.", "/srv/documentdb/../documentdb/")]
    public async Task SharedBindMountIsDetectedThroughHostPathAliases(string first, string second)
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("primary").WithImageTag("pg17-0.114.0").WithDataBindMount(first);
        appBuilder.AddDocumentDB("secondary").WithImageTag("pg17-0.114.0").WithDataBindMount(second);

        using var app = appBuilder.Build();

        await ConfigureResourceAsync(app, "primary");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "secondary"));

        Assert.Contains("host directory", exception.Message, StringComparison.Ordinal);
        Assert.Contains("no data-directory interlock", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DistinctBindMountsUnderACommonParentAreNotShared()
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = CreateAppBuilder(sink);
        appBuilder.AddDocumentDB("primary").WithDataBindMount("/srv/documentdb/primary");
        appBuilder.AddDocumentDB("secondary").WithDataBindMount("/srv/documentdb/./secondary");

        using var app = appBuilder.Build();

        await ConfigureResourceAsync(app, "primary", sink);
        await ConfigureResourceAsync(app, "secondary", sink);

        Assert.DoesNotContain(sink.LogEntries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task DataBindMountSharedWithANonDocumentDBResourceStartsWithoutWarnings()
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = CreateAppBuilder(sink);
        appBuilder.AddDocumentDB("DocumentDB").WithDataBindMount("./shared");
        appBuilder.AddContainer("backup", "alpine").WithBindMount("./shared", "/backup", isReadOnly: true);

        using var app = appBuilder.Build();

        await ConfigureResourceAsync(app, "DocumentDB", sink);

        Assert.DoesNotContain(sink.LogEntries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task DistinctDataVolumesStartWithoutStorageWarnings()
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = CreateAppBuilder(sink);
        appBuilder.AddDocumentDB("primary").WithDataVolume(name: "primary-data");
        appBuilder.AddDocumentDB("secondary").WithDataVolume(name: "secondary-data");

        using var app = appBuilder.Build();

        await ConfigureResourceAsync(app, "primary", sink);
        await ConfigureResourceAsync(app, "secondary", sink);

        Assert.DoesNotContain(sink.LogEntries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task SharedDataVolumeWithExplicitlyStartedResourceWarnsInsteadOfThrowing()
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = CreateAppBuilder(sink);
        appBuilder.AddDocumentDB("primary").WithImageTag(InterlockedTag).WithDataVolume(name: "shared-data");
        appBuilder.AddDocumentDB("standby").WithImageTag(InterlockedTag).WithDataVolume(name: "shared-data").WithExplicitStart();

        using var app = appBuilder.Build();

        await ConfigureResourceAsync(app, "primary", sink);
        await ConfigureResourceAsync(app, "standby", sink);

        var (_, _, message) = Assert.Single(sink.LogEntries.Where(e => e.Level == LogLevel.Warning));
        Assert.Contains("'standby' and DocumentDB resource 'primary'", message, StringComparison.Ordinal);
        Assert.Contains("volume 'shared-data'", message, StringComparison.Ordinal);
        Assert.Contains("exclusive lock", message, StringComparison.Ordinal);
        Assert.Contains("WithExplicitStart()", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CustomDataTargetPathWarnsAboutTheDeclaredImageVolume()
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = CreateAppBuilder(sink);
        appBuilder.AddDocumentDB("DocumentDB").WithImageTag(InterlockedTag).WithDataVolume(targetPath: "/pgdata");

        using var app = appBuilder.Build();

        await ConfigureResourceAsync(app, "DocumentDB", sink);

        var (_, _, message) = Assert.Single(sink.LogEntries.Where(e => e.Level == LogLevel.Warning));
        Assert.Contains("/pgdata", message, StringComparison.Ordinal);
        Assert.Contains("anonymous volume", message, StringComparison.Ordinal);
        Assert.Contains("/data", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Aspire discovers a container's dependencies before it builds its configuration, and that
    /// pass evaluates the environment callbacks — through the same one-shot cache — with no logger
    /// attached. A guard that wrote its advisory warnings to the callback context's logger would
    /// have them silently discarded on every real run. The harness reproduces that pass, so this
    /// asserts the warning survives it.
    /// </summary>
    [Fact]
    public async Task TheDeclaredImageVolumeWarningSurvivesTheDependencyDiscoveryPass()
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = CreateAppBuilder(sink);
        appBuilder.AddDocumentDB("DocumentDB").WithImageTag(InterlockedTag).WithDataVolume(targetPath: "/pgdata");

        using var app = appBuilder.Build();
        var resource = app.Services.GetRequiredService<DistributedApplicationModel>()
            .Resources.OfType<DocumentDBServerResource>().Single(r => r.Name == "DocumentDB");

        await PublishBeforeStartAsync(app);

        // The discovery pass, with no logger, exactly as Aspire runs it first.
        await ExecutionConfigurationBuilder.Create(resource)
            .WithEnvironmentVariablesConfig()
            .BuildAsync(new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run), NullLogger.Instance, CancellationToken.None);

        var (_, category, message) = Assert.Single(sink.LogEntries.Where(e => e.Level == LogLevel.Warning));
        Assert.Equal("Aspire.Hosting.DocumentDB.Storage", category);
        Assert.Contains("anonymous volume", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CustomDataTargetPathDoesNotWarnWhenTheImageVolumeIsAlsoMounted()
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = CreateAppBuilder(sink);
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithDataVolume(name: "pgdata", targetPath: "/pgdata")
            .WithVolume("declared-image-volume", "/data");

        using var app = appBuilder.Build();

        await ConfigureResourceAsync(app, "DocumentDB", sink);

        Assert.DoesNotContain(sink.LogEntries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task TheDefaultImageWarnsAboutADeclaredVolumeOnlyIfItDeclaresOne()
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = CreateAppBuilder(sink);
        appBuilder.AddDocumentDB("DocumentDB").WithDataVolume(targetPath: "/pgdata");

        using var app = appBuilder.Build();

        await ConfigureResourceAsync(app, "DocumentDB", sink);

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
        var appBuilder = CreateAppBuilder(sink);
        appBuilder.AddDocumentDB("DocumentDB").WithImageTag(tag).WithDataVolume(targetPath: "/pgdata");

        using var app = appBuilder.Build();

        await ConfigureResourceAsync(app, "DocumentDB", sink);

        Assert.DoesNotContain(sink.LogEntries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task CustomDataTargetPathDoesNotWarnOnACustomImage()
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = CreateAppBuilder(sink);
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImage("contoso/documentdb-fork", "pg17-0.116.0")
            .WithDataVolume(targetPath: "/pgdata");

        using var app = appBuilder.Build();

        await ConfigureResourceAsync(app, "DocumentDB", sink);

        Assert.DoesNotContain(sink.LogEntries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task CustomDataTargetPathDoesNotWarnOnAnUnrecognisedTag()
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = CreateAppBuilder(sink);
        appBuilder.AddDocumentDB("DocumentDB").WithImageTag("latest").WithDataVolume(targetPath: "/pgdata");

        using var app = appBuilder.Build();

        await ConfigureResourceAsync(app, "DocumentDB", sink);

        Assert.DoesNotContain(sink.LogEntries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task SharedDataVolumeOnPre116ImagesThrowsEvenWithExplicitStart()
    {
        var appBuilder = CreateAppBuilder();

        // 0.114.0 has no data-directory lock, so an explicitly started peer is not a safety net:
        // if both ever run, nothing refuses the second one.
        appBuilder.AddDocumentDB("primary").WithImageTag("pg17-0.114.0").WithDataVolume(name: "shared-data");
        appBuilder.AddDocumentDB("standby").WithImageTag("pg17-0.114.0").WithDataVolume(name: "shared-data").WithExplicitStart();

        using var app = appBuilder.Build();

        await ConfigureResourceAsync(app, "primary");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "standby"));

        Assert.Contains("no data-directory interlock", exception.Message, StringComparison.Ordinal);
        Assert.Contains("corrupt it silently", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("WithExplicitStart()", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SharedDataVolumeWithACustomImagePeerThrowsEvenWithExplicitStart()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("primary").WithImageTag(InterlockedTag).WithDataVolume(name: "shared-data");
        appBuilder.AddDocumentDB("fork")
            .WithImage("contoso/documentdb-fork", "pg17-0.116.0")
            .WithDataVolume(name: "shared-data")
            .WithExplicitStart();

        using var app = appBuilder.Build();

        await ConfigureResourceAsync(app, "primary");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "fork"));

        Assert.Contains("no data-directory interlock", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The warning downgrade is a promise about the official image's <c>flock</c>: the pair may
    /// share a directory because whichever container starts second refuses to start. A resource
    /// built from the caller's Dockerfile carries the official image annotation and even builds
    /// <c>FROM</c> the official image, but what starts is the build output, and nothing has
    /// established that it still takes the lock. So the pair stays a hard failure.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SharedDataVolumeWithADockerfileBuiltPeerThrowsEvenWithExplicitStart(bool buildIsTheHolder)
    {
        var contextPath = CreateOfficialLookingDockerfileContext();

        var appBuilder = CreateAppBuilder();

        var primary = appBuilder.AddDocumentDB("primary")
            .WithImageTag(InterlockedTag)
            .WithDataVolume(name: "shared-data");

        var standby = appBuilder.AddDocumentDB("standby")
            .WithImageTag(InterlockedTag)
            .WithDataVolume(name: "shared-data")
            .WithExplicitStart();

        (buildIsTheHolder ? primary : standby).WithDockerfile(contextPath);

        using var app = appBuilder.Build();

        await ConfigureResourceAsync(app, "primary");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "standby"));

        Assert.Contains("no data-directory interlock", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Dockerfile build", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("WithExplicitStart()", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The declared-volume warning is advice about the official image's <c>VOLUME /data</c>
    /// declaration. A caller's Dockerfile decides its own volumes, so repeating that advice would
    /// be a guess.
    /// </summary>
    [Fact]
    public async Task ADockerfileBuildDoesNotWarnAboutTheDeclaredImageVolume()
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = CreateAppBuilder(sink);
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithDockerfile(CreateOfficialLookingDockerfileContext())
            .WithDataVolume(targetPath: "/pgdata");

        using var app = appBuilder.Build();

        await ConfigureResourceAsync(app, "DocumentDB", sink);

        Assert.DoesNotContain(sink.LogEntries, e => e.Level == LogLevel.Warning);

        // Control: the identical configuration without the Dockerfile build does warn, so the
        // silence above is the classification and not some unrelated difference.
        var controlSink = new CapturingLoggerSink();
        var controlBuilder = CreateAppBuilder(controlSink);
        controlBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithDataVolume(targetPath: "/pgdata");

        using var controlApp = controlBuilder.Build();

        await ConfigureResourceAsync(controlApp, "DocumentDB", controlSink);

        Assert.Contains(controlSink.LogEntries, e => e.Level == LogLevel.Warning);
    }

    /// <summary>
    /// The published manifest is where the image annotation stops being what runs: a Dockerfile
    /// build ships a <c>build</c> instruction and no <c>image</c> at all. The storage guard still
    /// applies — only the version-dependent half of it is withheld.
    /// </summary>
    [Fact]
    public async Task ADockerfileBuildPublishesItsBuildRatherThanTheOfficialImage()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithDockerfile(CreateOfficialLookingDockerfileContext())
            .WithDataVolume(targetPath: "/pgdata");

        using var app = appBuilder.Build();

        var manifest = await PublishManifestAsync(app, "DocumentDB");

        Assert.Null(manifest["image"]);
        Assert.NotNull(manifest["build"]);
        Assert.Equal("/pgdata", manifest["env"]?["DATA_PATH"]?.GetValue<string>());
    }

    /// <summary>
    /// Only the version-dependent half of the storage guard is withheld. The image-independent
    /// rules — here, a read-only data directory PostgreSQL cannot initialise — still apply to a
    /// Dockerfile build.
    /// </summary>
    [Fact]
    public async Task ADockerfileBuildIsStillSubjectToTheImageIndependentStorageRules()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithDockerfile(CreateOfficialLookingDockerfileContext())
            .WithVolume("built-data", "/data", isReadOnly: true);

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "DocumentDB"));

        Assert.Contains("mounts its data directory ('/data') read-only", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The review reproduction, on the capability that is most visible: the declared-volume
    /// warning is raised only for images known to declare <c>/data</c>, and this resource runs one.
    /// </summary>
    [Fact]
    public async Task AQualifiedOfficialImageStillDeclaresItsDataVolume()
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = CreateAppBuilder(sink);
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImage(QualifiedOfficialImage, InterlockedTag)
            .WithImageRegistry(null!)
            .WithDataVolume(targetPath: "/pgdata");

        using var app = appBuilder.Build();

        await ConfigureResourceAsync(app, "DocumentDB", sink);

        var (_, _, message) = Assert.Single(sink.LogEntries.Where(e => e.Level == LogLevel.Warning));
        Assert.Contains("declares '/data' as a container volume", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// And on the capability that is a safety decision: the pair may share a directory only
    /// because this image refuses the second start.
    /// </summary>
    [Fact]
    public async Task AQualifiedOfficialImagePairIsStillInterlocked()
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = CreateAppBuilder(sink);
        appBuilder.AddDocumentDB("primary")
            .WithImage(QualifiedOfficialImage, InterlockedTag)
            .WithImageRegistry(null!)
            .WithDataVolume(name: "shared-data");
        appBuilder.AddDocumentDB("standby")
            .WithImage(QualifiedOfficialImage, InterlockedTag)
            .WithImageRegistry(null!)
            .WithDataVolume(name: "shared-data")
            .WithExplicitStart();

        using var app = appBuilder.Build();

        await ConfigureResourceAsync(app, "primary", sink);
        await ConfigureResourceAsync(app, "standby", sink);

        var (_, _, message) = Assert.Single(sink.LogEntries.Where(e => e.Level == LogLevel.Warning));
        Assert.Contains("exclusive lock", message, StringComparison.Ordinal);
        Assert.Contains("WithExplicitStart()", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A private mirror spelled the same way keeps the same treatment: only the registry differs.
    /// </summary>
    [Fact]
    public async Task AQualifiedPrivateMirrorStillDeclaresItsDataVolume()
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = CreateAppBuilder(sink);
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImage($"contoso.azurecr.io/{DocumentDBContainerImageTags.Image}", InterlockedTag)
            .WithImageRegistry(null!)
            .WithDataVolume(targetPath: "/pgdata");

        using var app = appBuilder.Build();

        await ConfigureResourceAsync(app, "DocumentDB", sink);

        Assert.Contains(sink.LogEntries, e => e.Level == LogLevel.Warning);
    }

    /// <summary>
    /// A path in front of the repository that is not a registry is part of the repository, so it
    /// is a different image and keeps the custom-image treatment.
    /// </summary>
    [Theory]
    // Inline, and split across the two annotation fields: the composed reference is the same.
    [InlineData(null, "evil/documentdb/documentdb-local")]
    [InlineData(null, "ghcr.io/evil/documentdb/documentdb-local")]
    [InlineData("ghcr.io/evil", "documentdb/documentdb-local")]
    [InlineData(null, "contoso.azurecr.io/mirrors/documentdb/documentdb-local")]
    [InlineData("contoso.azurecr.io/mirrors", "documentdb/documentdb-local")]
    [InlineData(null, "harbor.corp.local/library/documentdb/documentdb-local")]
    [InlineData("harbor.corp.local/library", "documentdb/documentdb-local")]
    public async Task AnExtraPathSegmentIsNotTheOfficialImage(string? registry, string image)
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = CreateAppBuilder(sink);
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImage(image, InterlockedTag)
            .WithImageRegistry(registry!)
            .WithDataVolume(targetPath: "/pgdata");

        using var app = appBuilder.Build();

        await ConfigureResourceAsync(app, "DocumentDB", sink);

        Assert.DoesNotContain(sink.LogEntries, e => e.Level == LogLevel.Warning);
    }

    /// <summary>
    /// Writing the registry into the image without clearing the registry annotation composes
    /// <c>ghcr.io/documentdb/ghcr.io/...</c>, which resolves to nothing. It is not the official
    /// image and must not be treated as one.
    /// </summary>
    [Fact]
    public async Task ADoubledRegistryPrefixIsNotTheOfficialImage()
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = CreateAppBuilder(sink);
        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithImage(QualifiedOfficialImage, InterlockedTag)
            .WithDataVolume(targetPath: "/pgdata");

        using var app = appBuilder.Build();

        await ConfigureResourceAsync(app, "DocumentDB", sink);

        Assert.DoesNotContain(sink.LogEntries, e => e.Level == LogLevel.Warning);

        var manifest = await ManifestUtils.GetManifest(documentDB.Resource);
        Assert.Equal(
            $"{DocumentDBContainerImageTags.Registry}/{QualifiedOfficialImage}:{InterlockedTag}",
            manifest["image"]?.GetValue<string>());
    }

    /// <summary>
    /// What the manifest ships is the point: the rearranged spelling publishes the same reference
    /// as the default one, which is why it has to classify the same way.
    /// </summary>
    [Fact]
    public async Task AQualifiedOfficialImagePublishesTheOfficialReference()
    {
        var appBuilder = CreateAppBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithImage(QualifiedOfficialImage, InterlockedTag)
            .WithImageRegistry(null!);

        var defaultBuilder = CreateAppBuilder();
        var byDefault = defaultBuilder.AddDocumentDB("DocumentDB").WithImageTag(InterlockedTag);

        Assert.Equal(
            (await ManifestUtils.GetManifest(byDefault.Resource))["image"]?.GetValue<string>(),
            (await ManifestUtils.GetManifest(documentDB.Resource))["image"]?.GetValue<string>());
    }

    /// <summary>
    /// A namespace in front of the repository names a different image, so the pair keeps the
    /// hard failure: nothing establishes that whatever runs there claims the directory.
    /// </summary>
    [Theory]
    [InlineData(null, "ghcr.io/evil/documentdb/documentdb-local")]
    [InlineData("ghcr.io/evil", "documentdb/documentdb-local")]
    [InlineData(null, "contoso.azurecr.io/mirrors/documentdb/documentdb-local")]
    [InlineData("contoso.azurecr.io/mirrors", "documentdb/documentdb-local")]
    public async Task AnExtraPathSegmentDoesNotDowngradeASharedDataDirectory(string? registry, string image)
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("primary")
            .WithImage(image, InterlockedTag)
            .WithImageRegistry(registry!)
            .WithDataVolume(name: "shared-data");
        appBuilder.AddDocumentDB("standby")
            .WithImage(image, InterlockedTag)
            .WithImageRegistry(registry!)
            .WithDataVolume(name: "shared-data")
            .WithExplicitStart();

        using var app = appBuilder.Build();

        await ConfigureResourceAsync(app, "primary");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "standby"));

        Assert.Contains("no data-directory interlock", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("WithExplicitStart()", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the manifest shows why: what ships is the reference the caller actually composed, not
    /// the official one.
    /// </summary>
    [Theory]
    [InlineData(null, "ghcr.io/evil/documentdb/documentdb-local", "ghcr.io/evil/documentdb/documentdb-local")]
    [InlineData("ghcr.io/evil", "documentdb/documentdb-local", "ghcr.io/evil/documentdb/documentdb-local")]
    [InlineData("contoso.azurecr.io/mirrors", "documentdb/documentdb-local", "contoso.azurecr.io/mirrors/documentdb/documentdb-local")]
    [InlineData("harbor.corp.local/library", "documentdb/documentdb-local", "harbor.corp.local/library/documentdb/documentdb-local")]
    public async Task AnExtraPathSegmentPublishesItsOwnReference(string? registry, string image, string expected)
    {
        var appBuilder = CreateAppBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithImage(image, InterlockedTag)
            .WithImageRegistry(registry!);

        var manifest = await ManifestUtils.GetManifest(documentDB.Resource);

        Assert.Equal($"{expected}:{InterlockedTag}", manifest["image"]?.GetValue<string>());
    }

    /// <summary>
    /// A true private mirror is a registry host and the exact repository beneath it, however the
    /// caller splits the two fields.
    /// </summary>
    [Theory]
    [InlineData("contoso.azurecr.io", "documentdb/documentdb-local")]
    [InlineData(null, "contoso.azurecr.io/documentdb/documentdb-local")]
    [InlineData("localhost:5000", "documentdb/documentdb-local")]
    [InlineData(null, "localhost:5000/documentdb/documentdb-local")]
    public async Task ATrueMirrorIsAHostAndTheExactRepository(string? registry, string image)
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = CreateAppBuilder(sink);
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImage(image, InterlockedTag)
            .WithImageRegistry(registry!)
            .WithDataVolume(targetPath: "/pgdata");

        using var app = appBuilder.Build();

        await ConfigureResourceAsync(app, "DocumentDB", sink);

        Assert.Contains(sink.LogEntries, e => e.Level == LogLevel.Warning);
    }

    /// <summary>
    /// A digest resolves to one image and a tag beside it to whatever the caller last typed. The
    /// declared-volume advice is a property of the release the digest names, which is not knowable,
    /// so it is withheld — even though the tag reads <c>pg17-0.116.0</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(DigestBehindACuratedTag))]
    public async Task ADigestBehindACuratedTagDoesNotDeclareADataVolume(string image, string? tag, string? sha256)
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = CreateAppBuilder(sink);
        var documentDB = appBuilder.AddDocumentDB("DocumentDB").WithDataVolume(targetPath: "/pgdata");
        SetImageAnnotation(documentDB, image, tag, sha256);

        using var app = appBuilder.Build();

        await ConfigureResourceAsync(app, "DocumentDB", sink);

        Assert.DoesNotContain(sink.LogEntries, e => e.Level == LogLevel.Warning);
    }

    /// <summary>
    /// And the interlock is the same promise: the digest may name a release that never took the
    /// lock, so the pair stays a hard failure instead of being downgraded by
    /// <c>WithExplicitStart()</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(DigestBehindACuratedTag))]
    public async Task ADigestBehindACuratedTagDoesNotDowngradeASharedDataDirectory(
        string image,
        string? tag,
        string? sha256)
    {
        var appBuilder = CreateAppBuilder();
        var primary = appBuilder.AddDocumentDB("primary").WithDataVolume(name: "shared-data");
        var standby = appBuilder.AddDocumentDB("standby").WithDataVolume(name: "shared-data").WithExplicitStart();
        SetImageAnnotation(primary, image, tag, sha256);
        SetImageAnnotation(standby, image, tag, sha256);

        using var app = appBuilder.Build();

        await ConfigureResourceAsync(app, "primary");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "standby"));

        Assert.Contains("no data-directory interlock", exception.Message, StringComparison.Ordinal);
        Assert.Contains("an image pinned by digest", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("WithExplicitStart()", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The manifest shows why: what ships is resolved by digest, whatever tag rides along.
    /// </summary>
    [Theory]
    [MemberData(nameof(DigestBehindACuratedTag))]
    public async Task ADigestBehindACuratedTagPublishesTheDigest(string image, string? tag, string? sha256)
    {
        var appBuilder = CreateAppBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB");
        SetImageAnnotation(documentDB, image, tag, sha256);

        var manifest = await ManifestUtils.GetManifest(documentDB.Resource);

        Assert.Contains(
            $"@sha256:{OlderReleaseDigest}",
            manifest["image"]?.GetValue<string>(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A pair that only warrants a warning must not consume the storage registration: a third
    /// resource on the same volume still has to fail.
    /// </summary>
    [Fact]
    public async Task AnExplicitlyStartedPeerDoesNotHideALaterHardConflict()
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = CreateAppBuilder(sink);
        appBuilder.AddDocumentDB("primary").WithImageTag(InterlockedTag).WithDataVolume(name: "shared-data");
        appBuilder.AddDocumentDB("manual").WithImageTag(InterlockedTag).WithDataVolume(name: "shared-data").WithExplicitStart();
        appBuilder.AddDocumentDB("always-on").WithImageTag(InterlockedTag).WithDataVolume(name: "shared-data");

        using var app = appBuilder.Build();

        await ConfigureResourceAsync(app, "primary", sink);
        await ConfigureResourceAsync(app, "manual", sink);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "always-on", sink));

        Assert.Contains("'always-on' and DocumentDB resource 'primary'", exception.Message, StringComparison.Ordinal);
        Assert.Single(sink.LogEntries.Where(e => e.Level == LogLevel.Warning));
    }

    /// <summary>
    /// The same three resources, with the explicitly started one reaching the volume first. Which
    /// pipeline runs first is not the caller's choice — in publish mode it follows declaration
    /// order — so the verdict must not depend on it: two always-on resources on one data directory
    /// are a failure either way.
    /// </summary>
    [Fact]
    public async Task AnExplicitlyStartedPeerThatRegistersFirstStillDoesNotHideAHardConflict()
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = CreateAppBuilder(sink);
        appBuilder.AddDocumentDB("manual").WithImageTag(InterlockedTag).WithDataVolume(name: "shared-data").WithExplicitStart();
        appBuilder.AddDocumentDB("primary").WithImageTag(InterlockedTag).WithDataVolume(name: "shared-data");
        appBuilder.AddDocumentDB("always-on").WithImageTag(InterlockedTag).WithDataVolume(name: "shared-data");

        using var app = appBuilder.Build();

        await ConfigureResourceAsync(app, "manual", sink);

        // 'primary' pairs only with the explicitly started 'manual', so it is a warning.
        await ConfigureResourceAsync(app, "primary", sink);

        // 'always-on' pairs with 'primary' as well, and neither of those two is started by hand.
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "always-on", sink));

        Assert.Contains("'always-on' and DocumentDB resource 'primary'", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The warning downgrade itself must stay order-independent: an explicitly started peer that
    /// registers before the always-on one is still only a warning.
    /// </summary>
    [Fact]
    public async Task SharedDataVolumeWithAnExplicitlyStartedResourceWarnsInEitherOrder()
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = CreateAppBuilder(sink);
        appBuilder.AddDocumentDB("standby").WithImageTag(InterlockedTag).WithDataVolume(name: "shared-data").WithExplicitStart();
        appBuilder.AddDocumentDB("primary").WithImageTag(InterlockedTag).WithDataVolume(name: "shared-data");

        using var app = appBuilder.Build();

        await ConfigureResourceAsync(app, "standby", sink);
        await ConfigureResourceAsync(app, "primary", sink);

        var (_, _, message) = Assert.Single(sink.LogEntries.Where(e => e.Level == LogLevel.Warning));
        Assert.Contains("'primary' and DocumentDB resource 'standby'", message, StringComparison.Ordinal);
        Assert.Contains("WithExplicitStart()", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StorageSharedWithAPeersInitDataIsNotADataDirectoryConflict()
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = CreateAppBuilder(sink);

        // The peer reads the same host directory as seed scripts and TLS material. That is a
        // read-only input mount on a different container path, not a second cluster on the files.
        appBuilder.AddDocumentDB("primary").WithImageTag(InterlockedTag).WithDataBindMount("./shared");
        appBuilder.AddDocumentDB("secondary")
            .WithImageTag(InterlockedTag)
            .WithDataVolume(name: "secondary-data")
            .WithInitData("./shared")
            .WithTlsCertificate("./shared/tls.crt", "./shared/tls.key");

        using var app = appBuilder.Build();

        await ConfigureResourceAsync(app, "primary", sink);
        await ConfigureResourceAsync(app, "secondary", sink);

        Assert.DoesNotContain(sink.LogEntries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task SharedStorageOnDifferentDataPathsIsStillADataDirectoryConflict()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("primary").WithImageTag(InterlockedTag).WithDataVolume(name: "shared-data");
        appBuilder.AddDocumentDB("secondary")
            .WithImageTag(InterlockedTag)
            .WithDataVolume(name: "shared-data", targetPath: "/pgdata");

        using var app = appBuilder.Build();

        await ConfigureResourceAsync(app, "primary");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "secondary"));

        Assert.Contains("volume 'shared-data'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithDataVolumeReadOnlyMessageNamesTheRequestedTargetPath()
    {
        var appBuilder = CreateAppBuilder();
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
        var appBuilder = CreateAppBuilder();
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
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithVolume("raw-data", "/tmp/../data", isReadOnly: true);

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "DocumentDB"));

        Assert.Contains("mounts its data directory ('/data') read-only", exception.Message, StringComparison.Ordinal);
        Assert.Contains("volume 'raw-data'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicateDataMountsAreDetectedThroughDotSegmentAliases()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithDataVolume(name: "first")
            .WithVolume("second", "//data/./");

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "DocumentDB"));

        Assert.Contains("2 mounts on '/data'", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The case the aliasing bug made reachable: two resources on images with no data-directory
    /// interlock, mounting one volume at two spellings of the same container path. Nothing in the
    /// container would refuse the second start, so the model has to.
    /// </summary>
    [Fact]
    public async Task SharedStorageIsDetectedThroughDotSegmentAliasesOnUninterlockedImages()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("primary").WithImageTag("pg17-0.114.0").WithDataVolume(name: "shared-data");
        appBuilder.AddDocumentDB("secondary")
            .WithImageTag("pg17-0.114.0")
            .WithVolume("shared-data", "/foo/../data")
            .WithEnvironment("DATA_PATH", "/foo/../data");

        using var app = appBuilder.Build();

        await ConfigureResourceAsync(app, "primary");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "secondary"));

        Assert.Contains("volume 'shared-data'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("no data-directory interlock", exception.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------
    // Above-root mount targets
    //
    // Docker does not refuse '/../data'; it clamps the destination to '/data'
    // and mounts there. Verified against the daemon in
    // DocumentDBStorageBehaviorTests. Treating the mount as "somewhere else"
    // would let it take over — or collide with — the data directory unseen.
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("/../data")]
    [InlineData("/../../data")]
    [InlineData("/../data/../data")]
    public async Task RawMountTargetsThatEscapeTheContainerRootAreRejected(string target)
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB").WithVolume("raw-data", target);

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "DocumentDB"));

        Assert.Contains($"at '{target}'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("reaches above the container root", exception.Message, StringComparison.Ordinal);
        Assert.Contains("clamps the target and mounts on '/data'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadOnlyRawMountAboveTheContainerRootIsRejected()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB").WithVolume("raw-data", "/../data", isReadOnly: true);

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "DocumentDB"));

        Assert.Contains("reaches above the container root", exception.Message, StringComparison.Ordinal);
        Assert.Contains("volume 'raw-data'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicateRawMountAboveTheContainerRootIsRejected()
    {
        // Docker's own answer to this pair is 'Duplicate mount point: /data'; the spelling that
        // caused it is refused first, because it is the one that is not what it looks like.
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithDataVolume(name: "first")
            .WithVolume("second", "/../data");

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "DocumentDB"));

        Assert.Contains("reaches above the container root", exception.Message, StringComparison.Ordinal);
        Assert.Contains("volume 'second'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SharedSourceMountedAboveTheContainerRootIsRejected()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("primary").WithImageTag("pg17-0.114.0").WithDataVolume(name: "shared-data");
        appBuilder.AddDocumentDB("secondary")
            .WithImageTag("pg17-0.114.0")
            .WithVolume("shared-data", "/../data");

        using var app = appBuilder.Build();

        await ConfigureResourceAsync(app, "primary");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "secondary"));

        Assert.Contains("reaches above the container root", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BindMountTargetThatEscapesTheContainerRootIsRejected()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB").WithBindMount("/host/data", "/../data");

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "DocumentDB"));

        Assert.Contains("bind mount of '/host/data'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("reaches above the container root", exception.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------
    // A mount on an ancestor of DATA_PATH still backs it
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ReadOnlyAncestorMountOfTheDataPathThrowsAtStart()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithVolume("raw-data", "/data", isReadOnly: true)
            .WithEnvironment("DATA_PATH", "/data/cluster");

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "DocumentDB"));

        Assert.Contains("mounts '/data', the directory that backs its data directory ('/data/cluster') read-only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadOnlyAncestorBindMountOfTheDataPathThrowsAtStart()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithBindMount("/srv/documentdb", "/data", isReadOnly: true)
            .WithEnvironment("DATA_PATH", "/data/cluster/./");

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "DocumentDB"));

        Assert.Contains("backs its data directory ('/data/cluster')", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'/srv/documentdb'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicateAncestorMountsOfTheDataPathThrowAtStart()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithVolume("first", "/data")
            .WithVolume("second", "//data/.")
            .WithEnvironment("DATA_PATH", "/data/cluster");

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "DocumentDB"));

        Assert.Contains("2 mounts on '/data'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("data directory ('/data/cluster')", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The most specific mount is the one the kernel resolves the path through, so it — and not
    /// the ancestor — is the mount the rules apply to.
    /// </summary>
    [Fact]
    public async Task TheMostSpecificMountBacksTheDataPath()
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = CreateAppBuilder(sink);
        appBuilder.AddDocumentDB("DocumentDB")
            .WithVolume("outer", "/data", isReadOnly: true)
            .WithVolume("inner", "/data/cluster")
            .WithEnvironment("DATA_PATH", "/data/cluster");

        using var app = appBuilder.Build();

        var environment = await ConfigureResourceAsync(app, "DocumentDB", sink);

        Assert.Equal("/data/cluster", environment["DATA_PATH"]);
    }

    /// <summary>
    /// Two resources sharing one volume conflict only when their data directories land on the same
    /// place inside it.
    /// </summary>
    [Fact]
    public async Task SharedVolumeWithTheSameSubdirectoryIsADataDirectoryConflict()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("primary")
            .WithImageTag(InterlockedTag)
            .WithVolume("shared-data", "/data")
            .WithEnvironment("DATA_PATH", "/data/cluster");
        appBuilder.AddDocumentDB("secondary")
            .WithImageTag(InterlockedTag)
            .WithVolume("shared-data", "/data")
            .WithEnvironment("DATA_PATH", "/data/./cluster");

        using var app = appBuilder.Build();

        await ConfigureResourceAsync(app, "primary");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "secondary"));

        Assert.Contains("volume 'shared-data' (subdirectory 'cluster')", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SharedVolumeWithDistinctSubdirectoriesIsNotAConflict()
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = CreateAppBuilder(sink);
        appBuilder.AddDocumentDB("primary")
            .WithImageTag(InterlockedTag)
            .WithVolume("shared-data", "/data")
            .WithEnvironment("DATA_PATH", "/data/alpha");
        appBuilder.AddDocumentDB("secondary")
            .WithImageTag(InterlockedTag)
            .WithVolume("shared-data", "/data")
            .WithEnvironment("DATA_PATH", "/data/beta");

        using var app = appBuilder.Build();

        await ConfigureResourceAsync(app, "primary", sink);
        await ConfigureResourceAsync(app, "secondary", sink);

        Assert.DoesNotContain(sink.LogEntries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task SharedBindMountWithTheSameSubdirectoryIsADataDirectoryConflict()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("primary")
            .WithImageTag("pg17-0.114.0")
            .WithBindMount("/srv/documentdb", "/data")
            .WithEnvironment("DATA_PATH", "/data/cluster");
        appBuilder.AddDocumentDB("secondary")
            .WithImageTag("pg17-0.114.0")
            .WithBindMount("/srv/documentdb/.", "/data")
            .WithEnvironment("DATA_PATH", "/data/cluster");

        using var app = appBuilder.Build();

        await ConfigureResourceAsync(app, "primary");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "secondary"));

        // The message names the host directory the cluster really occupies, not the pair of strings
        // it was spelled with.
        Assert.Contains(Path.GetFullPath("/srv/documentdb/cluster"), exception.Message, StringComparison.Ordinal);
        Assert.Contains("no data-directory interlock", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASiblingDirectoryIsNotBackedByTheDataMount()
    {
        // '/database' starts with '/data' as a string, but is not below it as a path.
        var sink = new CapturingLoggerSink();
        var appBuilder = CreateAppBuilder(sink);
        appBuilder.AddDocumentDB("DocumentDB")
            .WithVolume("raw-data", "/data", isReadOnly: true)
            .WithEnvironment("DATA_PATH", "/database");

        using var app = appBuilder.Build();

        var environment = await ConfigureResourceAsync(app, "DocumentDB", sink);

        Assert.Equal("/database", environment["DATA_PATH"]);
    }

    // ---------------------------------------------------------------------
    // DATA_PATH participates in every rule, once
    // ---------------------------------------------------------------------

    [Fact]
    public async Task RawDataPathEnvironmentOverrideParticipatesInTheReadOnlyCheck()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithVolume("raw-data", "/pgdata", isReadOnly: true)
            .WithEnvironment("DATA_PATH", "/pgdata");

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "DocumentDB"));

        Assert.Contains("mounts its data directory ('/pgdata') read-only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RawDataPathEnvironmentOverrideParticipatesInTheDuplicateCheck()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithVolume("first", "/pgdata")
            .WithVolume("second", "/pgdata/")
            .WithEnvironment("DATA_PATH", "/var/../pgdata");

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "DocumentDB"));

        Assert.Contains("2 mounts on '/pgdata'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RawDataPathEnvironmentOverrideParticipatesInTheSharedStorageCheck()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("primary").WithImageTag(InterlockedTag).WithDataVolume(name: "shared-data");
        appBuilder.AddDocumentDB("secondary")
            .WithImageTag(InterlockedTag)
            .WithVolume("shared-data", "/pgdata")
            .WithEnvironment("DATA_PATH", "/pgdata");

        using var app = appBuilder.Build();

        await ConfigureResourceAsync(app, "primary");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "secondary"));

        Assert.Contains("volume 'shared-data'", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ordering matters and is the container's, not the package's: the environment callback that
    /// runs last decides where DocumentDB writes.
    /// </summary>
    [Fact]
    public async Task DataPathEnvironmentOverrideAfterTheStorageHelperWins()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithDataVolume()
            .WithBindMount("/host/read-only", "/pgdata", isReadOnly: true)
            .WithEnvironment("DATA_PATH", "/pgdata");

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "DocumentDB"));

        Assert.Contains("mounts its data directory ('/pgdata') read-only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DataPathEnvironmentOverrideBeforeTheStorageHelperLoses()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithEnvironment("DATA_PATH", "/pgdata")
            .WithDataVolume()
            .WithBindMount("/host/read-only", "/pgdata", isReadOnly: true);

        using var app = appBuilder.Build();

        // The read-only mount is not on the effective data directory, so it is somebody else's
        // read-only input and no concern of this guard.
        var environment = await ConfigureResourceAsync(app, "DocumentDB");

        Assert.Equal("/data", environment["DATA_PATH"]);
    }

    [Fact]
    public async Task CallbackSuppliedDataPathParticipatesInStorageChecks()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithVolume("raw-data", "/pgdata", isReadOnly: true)
            .WithEnvironment(context => context.EnvironmentVariables["DATA_PATH"] = "/var/../pgdata/");

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "DocumentDB"));

        Assert.Contains("mounts its data directory ('/pgdata') read-only", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The guard has to see the same <c>DATA_PATH</c> the container gets, and there is only one way
    /// to be sure of that: read it from the pipeline that produces it. A callback that answers
    /// differently every time it runs makes any second evaluation visible — Aspire evaluates each
    /// callback once per run, so the guard, the environment and the container all read
    /// <c>/pgdata-1</c>, and the read-only mount there is found.
    /// </summary>
    [Fact]
    public async Task AStatefulDataPathCallbackIsEvaluatedOnceAndGuardedOnThatValue()
    {
        var evaluations = 0;

        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithVolume("first", "/pgdata-1", isReadOnly: true)
            .WithVolume("second", "/pgdata-2")
            .WithEnvironment(context =>
                context.EnvironmentVariables["DATA_PATH"] =
                    string.Create(CultureInfo.InvariantCulture, $"/pgdata-{Interlocked.Increment(ref evaluations)}"));

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "DocumentDB"));

        Assert.Equal(1, evaluations);
        Assert.Contains("mounts its data directory ('/pgdata-1') read-only", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same callback, without a rule to break: the value the guard canonicalized is the value
    /// the pipeline hands the container, and repeating the build does not re-run the callback.
    /// </summary>
    [Fact]
    public async Task AStatefulDataPathCallbackProducesOneValueForBothTheGuardAndTheContainer()
    {
        var evaluations = 0;

        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithEnvironment(context =>
                context.EnvironmentVariables["DATA_PATH"] =
                    string.Create(CultureInfo.InvariantCulture, $"/srv/./pgdata-{Interlocked.Increment(ref evaluations)}"));

        using var app = appBuilder.Build();

        var first = await ConfigureResourceAsync(app, "DocumentDB");
        var second = await ConfigureResourceAsync(app, "DocumentDB");

        Assert.Equal(1, evaluations);
        Assert.Equal("/srv/pgdata-1", first["DATA_PATH"]);
        Assert.Equal("/srv/pgdata-1", second["DATA_PATH"]);
    }

    /// <summary>
    /// A <c>DATA_PATH</c> supplied as a parameter is a value provider, not a string, so it is
    /// resolved once — here, with the context Aspire's own environment resolution uses — and
    /// replaced by the canonical result, which is then what the container is given.
    /// </summary>
    [Fact]
    public async Task ParameterSuppliedDataPathParticipatesInStorageChecks()
    {
        var appBuilder = CreateAppBuilder();
        var dataPath = appBuilder.AddParameter("datapath", "/pgdata");
        appBuilder.AddDocumentDB("DocumentDB")
            .WithVolume("raw-data", "/pgdata", isReadOnly: true)
            .WithEnvironment("DATA_PATH", dataPath);

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "DocumentDB"));

        Assert.Contains("mounts its data directory ('/pgdata') read-only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParameterSuppliedDataPathIsResolvedExactlyOnceAndCanonicalized()
    {
        var provider = new RecordingValueProvider("/srv/../srv/pgdata/");

        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithEnvironment(context => context.EnvironmentVariables["DATA_PATH"] = provider);

        using var app = appBuilder.Build();

        var environment = await ConfigureResourceAsync(app, "DocumentDB");

        Assert.Equal("/srv/pgdata", environment["DATA_PATH"]);
        Assert.Equal(1, provider.ResolutionCount);

        // The value was replaced by the canonical string, so the provider is never consulted again.
        Assert.Equal("/srv/pgdata", (await ConfigureResourceAsync(app, "DocumentDB"))["DATA_PATH"]);
        Assert.Equal(1, provider.ResolutionCount);
    }

    /// <summary>
    /// Aspire resolves an environment value with a <see cref="ValueProviderContext"/> naming the
    /// resource that wants it; the guard's single resolution has to carry the same context, or a
    /// context-sensitive provider would answer the guard and the container differently.
    /// </summary>
    [Fact]
    public async Task ADataPathValueProviderSeesTheResourceThatAsksForIt()
    {
        var provider = new RecordingValueProvider("/unused");

        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithEnvironment(context => context.EnvironmentVariables["DATA_PATH"] = provider);

        using var app = appBuilder.Build();
        var resource = app.Services.GetRequiredService<DistributedApplicationModel>()
            .Resources.OfType<DocumentDBServerResource>().Single(r => r.Name == "DocumentDB");

        await ConfigureResourceAsync(app, "DocumentDB");

        Assert.Same(resource, provider.LastContext?.Caller);
        Assert.Equal(DistributedApplicationOperation.Run, provider.LastContext?.ExecutionContext?.Operation);
    }

    /// <summary>
    /// Running the guard must not turn a question about a filesystem path into a secret lookup.
    /// Aspire resolves the password because the container needs it; the guard adds no resolution
    /// of its own, so the count is exactly one.
    /// </summary>
    [Fact]
    public async Task StorageGuardResolvesOnlyTheDataPathValue()
    {
        var recordingProvider = new RecordingValueProvider("unused");

        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithDataVolume()
            .WithEnvironment(context => context.EnvironmentVariables["SOME_SECRET"] = recordingProvider);

        using var app = appBuilder.Build();

        await ConfigureResourceAsync(app, "DocumentDB");

        // Twice, and both from Aspire: it resolves a container's values once while discovering its
        // dependencies and once while building its configuration, and the harness reproduces both.
        // The guard adds nothing — it never looks at a value other than DATA_PATH.
        Assert.Equal(2, recordingProvider.ResolutionCount);
    }

    [Theory]
    [InlineData("/data/..", "resolves to the container root")]
    [InlineData("//", "resolves to the container root")]
    [InlineData("/data/../..", "resolves to the container root")]
    [InlineData("/../data", "reaches above the container root")]
    [InlineData("/../../pgdata", "reaches above the container root")]
    [InlineData("pgdata", "is not an absolute path inside the container")]
    [InlineData("   ", "is not an absolute path inside the container")]
    public async Task UnusableDataPathIsRejectedAtStart(string dataPath, string expected)
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithEnvironment("DATA_PATH", dataPath);

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "DocumentDB"));

        Assert.Contains($"sets DATA_PATH to '{dataPath}'", exception.Message, StringComparison.Ordinal);
        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The entrypoint applies <c>DATA_PATH=${DATA_PATH:-/data}</c>, which treats an empty value
    /// exactly like an unset one. The guard follows the image rather than inventing a failure, and
    /// writes the path the container will really use so the dashboard shows it too. Whitespace is
    /// not empty to the shell — it would be a relative directory name — and is rejected instead.
    /// </summary>
    [Fact]
    public async Task AnEmptyDataPathFollowsTheImageDefault()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithVolume("raw-data", "/data", isReadOnly: true)
            .WithEnvironment("DATA_PATH", string.Empty);

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "DocumentDB"));

        Assert.Contains("mounts its data directory ('/data') read-only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyDataPathIsReplacedByTheImageDefault()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB").WithEnvironment("DATA_PATH", string.Empty);

        using var app = appBuilder.Build();

        Assert.Equal("/data", (await ConfigureResourceAsync(app, "DocumentDB"))["DATA_PATH"]);
    }

    /// <summary>
    /// The declared image volume is covered by any mount the runtime resolves onto <c>/data</c>,
    /// however it was spelled, so an alias suppresses the anonymous-volume warning.
    /// </summary>
    [Fact]
    public async Task ADotSegmentAliasOfTheDeclaredImageVolumeSuppressesTheWarning()
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = CreateAppBuilder(sink);
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithDataVolume(name: "pgdata", targetPath: "/pgdata")
            .WithVolume("declared-image-volume", "/tmp/../data/");

        using var app = appBuilder.Build();

        await ConfigureResourceAsync(app, "DocumentDB", sink);

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
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithDataVolume(targetPath: "/var/lib/../lib/documentdb")
            .WithInitData("./seed")
            .WithTlsCertificate("./certs/server.crt", "./certs/server.key");

        using var app = appBuilder.Build();
        var resource = app.Services.GetRequiredService<DistributedApplicationModel>()
            .Resources.OfType<DocumentDBServerResource>().Single(r => r.Name == "DocumentDB");

        Assert.Equal("/var/lib/documentdb", (await ConfigureResourceAsync(app, "DocumentDB"))["DATA_PATH"]);
        Assert.All(
            resource.Annotations.OfType<ContainerMountAnnotation>().Where(mount => mount.IsReadOnly),
            mount => Assert.NotEqual("/var/lib/documentdb", mount.Target));
    }

    // ---------------------------------------------------------------------
    // The entrypoint's own data-path argument
    //
    // '--data-path' is documented by the image as "Overrides DATA_PATH
    // environment variable", and the entrypoint exports it while parsing
    // arguments. A resource that passes it would move its data directory past
    // every rule above.
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("--data-path")]
    [InlineData("-d")]
    [InlineData("--data-path=/pgdata")]
    [InlineData("-d=/pgdata")]
    public async Task ReservedDataPathArgumentsAreRejected(string argument)
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB").WithArgs(argument, "/pgdata");

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "DocumentDB"));

        Assert.Contains($"passes the command-line argument '{argument}'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("WithDataVolume()", exception.Message, StringComparison.Ordinal);
        Assert.Contains("WithDataBindMount(...)", exception.Message, StringComparison.Ordinal);
        Assert.Contains("WithEnvironment(\"DATA_PATH\", ...)", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReservedDataPathArgumentAddedByACallbackIsRejected()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithArgs(context =>
            {
                context.Args.Add("--data-path");
                context.Args.Add("/pgdata");
            });

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "DocumentDB"));

        Assert.Contains("passes the command-line argument '--data-path'", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The guard's callback is appended when the application starts, so it is last whatever order
    /// the arguments were configured in — including a callback that removes the storage helpers'
    /// own configuration and adds the argument afterwards.
    /// </summary>
    [Fact]
    public async Task AReservedDataPathArgumentIsRejectedWhicheverOrderItWasAddedIn()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithArgs("--data-path", "/pgdata")
            .WithDataVolume()
            .WithArgs("--log-level", "debug");

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "DocumentDB"));

        Assert.Contains("passes the command-line argument '--data-path'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADataPathArgumentRemovedBeforeStartIsNotRejected()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithArgs("--data-path", "/pgdata")
            .WithArgs(context =>
            {
                context.Args.Remove("--data-path");
                context.Args.Remove("/pgdata");
            });

        using var app = appBuilder.Build();

        var arguments = await ConfigureArgumentsAsync(app, "DocumentDB");

        Assert.Empty(arguments);
    }

    [Fact]
    public async Task UnrelatedArgumentsThatLookSimilarAreNotRejected()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithArgs("--init-data-path", "/seed", "--data-paths", "-dd", "--log-level", "debug");

        using var app = appBuilder.Build();

        var arguments = await ConfigureArgumentsAsync(app, "DocumentDB");

        Assert.Contains("--init-data-path", arguments);
        Assert.Contains("--data-paths", arguments);
    }

    // ---------------------------------------------------------------------
    // Publish mode
    //
    // The same rules apply while a manifest is produced, where no container
    // is started at all: a configuration that would corrupt a data directory
    // is refused before it can be deployed. A deferred DATA_PATH is the one
    // exception — in publish mode it stays a manifest expression, and there
    // is no path to compare with a mount target.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task AReadOnlyDataMountIsRejectedInPublishMode()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB").WithVolume("raw-data", "/data", isReadOnly: true);

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "DocumentDB", operation: DistributedApplicationOperation.Publish));

        Assert.Contains("mounts its data directory ('/data') read-only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASharedDataDirectoryIsRejectedInPublishMode()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("primary").WithDataVolume(name: "shared-data");
        appBuilder.AddDocumentDB("secondary").WithDataVolume(name: "shared-data");

        using var app = appBuilder.Build();

        await ConfigureResourceAsync(app, "primary", operation: DistributedApplicationOperation.Publish);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "secondary", operation: DistributedApplicationOperation.Publish));

        Assert.Contains("both use the same volume 'shared-data'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReservedDataPathArgumentIsRejectedInPublishMode()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB").WithDataVolume().WithArgs("--data-path", "/pgdata");

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureArgumentsAsync(app, "DocumentDB", operation: DistributedApplicationOperation.Publish));

        Assert.Contains("passes the command-line argument '--data-path'", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A parameter is a manifest expression in publish mode, not a path. Resolving it is not an
    /// option — the value belongs to the deployment, and a parameter may be a secret — so a
    /// resource that also mounts storage is refused rather than published with every storage rule
    /// silently skipped.
    /// </summary>
    [Fact]
    public async Task ADeferredDataPathWithStorageIsRejectedInPublishMode()
    {
        var appBuilder = CreateAppBuilder();
        var dataPath = appBuilder.AddParameter("datapath", "/pgdata/");
        appBuilder.AddDocumentDB("DocumentDB")
            .WithVolume("raw-data", "/pgdata", isReadOnly: true)
            .WithEnvironment("DATA_PATH", dataPath);

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "DocumentDB", operation: DistributedApplicationOperation.Publish));

        Assert.Contains("only known at deployment time", exception.Message, StringComparison.Ordinal);
        Assert.Contains("also mounts storage", exception.Message, StringComparison.Ordinal);

        // The parameter's value is never read, so it cannot reach the message.
        Assert.DoesNotContain("/pgdata", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same value on a resource that mounts nothing is harmless: there is no storage for it to
    /// be wrong about, so the manifest keeps the expression.
    /// </summary>
    [Fact]
    public async Task ADeferredDataPathWithoutStorageIsLeftAloneInPublishMode()
    {
        var appBuilder = CreateAppBuilder();
        var dataPath = appBuilder.AddParameter("datapath", "/pgdata/");
        appBuilder.AddDocumentDB("DocumentDB").WithEnvironment("DATA_PATH", dataPath);

        using var app = appBuilder.Build();

        var environment = await ConfigureResourceAsync(app, "DocumentDB", operation: DistributedApplicationOperation.Publish);

        Assert.Equal("{datapath.value}", environment["DATA_PATH"]);
    }

    /// <summary>
    /// In run mode the same configuration is checked properly: the value is resolved once, here,
    /// and the read-only mount underneath it is found.
    /// </summary>
    [Fact]
    public async Task ADeferredDataPathWithStorageIsCheckedInRunMode()
    {
        var appBuilder = CreateAppBuilder();
        var dataPath = appBuilder.AddParameter("datapath", "/pgdata/");
        appBuilder.AddDocumentDB("DocumentDB")
            .WithVolume("raw-data", "/pgdata", isReadOnly: true)
            .WithEnvironment("DATA_PATH", dataPath);

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "DocumentDB"));

        Assert.Contains("mounts its data directory ('/pgdata') read-only", exception.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------
    // The guard has to be the last word
    //
    // Being appended at BeforeStartEvent is not enough: lifecycle hooks and
    // later BeforeStartEvent subscribers both run afterwards, and either can
    // append another environment or command-line callback. The guard retakes
    // the last position at BeforeResourceStartedEvent, and refuses to answer
    // at all if something got in after that.
    // ---------------------------------------------------------------------

    /// <summary>
    /// A <see cref="BeforeStartEvent"/> subscriber registered after <c>AddDocumentDB</c> runs after
    /// the guard installs itself. The guard retakes the last position before the resource starts,
    /// so the override is seen and judged rather than missed.
    /// </summary>
    [Fact]
    public async Task ADataPathSetByALaterBeforeStartSubscriberIsStillJudged()
    {
        var appBuilder = CreateAppBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithDataVolume()
            .WithVolume("late", "/pgdata", isReadOnly: true);

        appBuilder.Eventing.Subscribe<BeforeStartEvent>((_, _) =>
        {
            documentDB.WithEnvironment("DATA_PATH", "/pgdata");
            return Task.CompletedTask;
        });

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "DocumentDB"));

        Assert.Contains("mounts its data directory ('/pgdata') read-only", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Lifecycle hooks run after <see cref="BeforeStartEvent"/> too, and are the documented way to
    /// mutate the model late. The same must hold for them.
    /// </summary>
    [Fact]
    public async Task ADataPathSetByALifecycleHookIsStillJudged()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithDataVolume()
            .WithVolume("late", "/pgdata", isReadOnly: true);

#pragma warning disable CS0618 // Type or member is obsolete
        appBuilder.Services.AddSingleton<IDistributedApplicationLifecycleHook>(
            new DataPathLifecycleHook("DocumentDB", "/pgdata"));
#pragma warning restore CS0618

        using var app = appBuilder.Build();

        // The hook runs between BeforeStartEvent and the orchestrator, exactly as Aspire runs it.
        await PublishBeforeStartAsync(app);
#pragma warning disable CS0618 // Type or member is obsolete
        foreach (var hook in app.Services.GetServices<IDistributedApplicationLifecycleHook>())
        {
            await hook.BeforeStartAsync(app.Services.GetRequiredService<DistributedApplicationModel>(), CancellationToken.None);
        }
#pragma warning restore CS0618

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "DocumentDB"));

        Assert.Contains("mounts its data directory ('/pgdata') read-only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArgumentsAddedByALifecycleHookAreStillJudged()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB").WithDataVolume();

#pragma warning disable CS0618 // Type or member is obsolete
        appBuilder.Services.AddSingleton<IDistributedApplicationLifecycleHook>(
            new DataPathArgumentLifecycleHook("DocumentDB"));
#pragma warning restore CS0618

        using var app = appBuilder.Build();

        await PublishBeforeStartAsync(app);
#pragma warning disable CS0618 // Type or member is obsolete
        foreach (var hook in app.Services.GetServices<IDistributedApplicationLifecycleHook>())
        {
            await hook.BeforeStartAsync(app.Services.GetRequiredService<DistributedApplicationModel>(), CancellationToken.None);
        }
#pragma warning restore CS0618

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureArgumentsAsync(app, "DocumentDB"));

        Assert.Contains("passes the command-line argument '--data-path'", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nothing runs between <see cref="BeforeResourceStartedEvent"/> and the container's
    /// configuration except another subscriber to that same event. One registered after
    /// <c>AddDocumentDB</c> can still append a callback, and the guard would then be validating a
    /// configuration something else had already changed. It fails the resource instead.
    /// </summary>
    [Fact]
    public async Task AnEnvironmentCallbackAddedAfterTheGuardFailsClosed()
    {
        var appBuilder = CreateAppBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB").WithDataVolume();

        appBuilder.Eventing.Subscribe<BeforeResourceStartedEvent>(documentDB.Resource, (_, _) =>
        {
            documentDB.WithEnvironment("DATA_PATH", "/pgdata");
            return Task.CompletedTask;
        });

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "DocumentDB"));

        Assert.Contains("has a later environment callback registered after", exception.Message, StringComparison.Ordinal);
        Assert.Contains("unchecked data directory", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACommandLineCallbackAddedAfterTheGuardFailsClosed()
    {
        var appBuilder = CreateAppBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB").WithDataVolume();

        appBuilder.Eventing.Subscribe<BeforeResourceStartedEvent>(documentDB.Resource, (_, _) =>
        {
            documentDB.WithArgs("--data-path", "/pgdata");
            return Task.CompletedTask;
        });

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureArgumentsAsync(app, "DocumentDB"));

        Assert.Contains("has a later command-line callback registered after", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Publish mode publishes no per-resource event, so a lifecycle hook is already "after the
    /// guard" there. The fail-closed check is what carries the same safety across.
    /// </summary>
    [Fact]
    public async Task AnEnvironmentCallbackAddedAfterTheGuardFailsClosedInPublishMode()
    {
        var appBuilder = CreateAppBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB").WithDataVolume();

        using var app = appBuilder.Build();

        await PublishBeforeStartAsync(app);
        documentDB.WithEnvironment("DATA_PATH", "/pgdata");

        var resource = app.Services.GetRequiredService<DistributedApplicationModel>()
            .Resources.OfType<DocumentDBServerResource>().Single(r => r.Name == "DocumentDB");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BuildExecutionConfigurationAsync(resource, null, DistributedApplicationOperation.Publish, includeArguments: true, throwOnResolutionFailure: true));

        Assert.Contains("has a later environment callback registered after", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The failure has to describe the shape of the configuration and nothing else: the callback
    /// that displaced the guard may well be the one carrying a secret.
    /// </summary>
    [Fact]
    public async Task TheFailClosedMessageDoesNotRepeatTheValueThatDisplacedTheGuard()
    {
        var appBuilder = CreateAppBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB").WithDataVolume();

        appBuilder.Eventing.Subscribe<BeforeResourceStartedEvent>(documentDB.Resource, (_, _) =>
        {
            documentDB.WithEnvironment("SOME_SECRET", "hunter2-should-not-appear");
            return Task.CompletedTask;
        });

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "DocumentDB"));

        Assert.DoesNotContain("hunter2", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SOME_SECRET", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Retaking the last position must not cost a second evaluation: the annotation is moved, not
    /// rebuilt, so Aspire's cached result for it survives.
    /// </summary>
    [Fact]
    public async Task RetakingTheLastPositionKeepsTheGuardsSingleEvaluation()
    {
        var evaluations = 0;

        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithEnvironment(context =>
                context.EnvironmentVariables["DATA_PATH"] =
                    string.Create(CultureInfo.InvariantCulture, $"/srv/pgdata-{Interlocked.Increment(ref evaluations)}"));

        using var app = appBuilder.Build();

        var first = await ConfigureResourceAsync(app, "DocumentDB");

        // A second start attempt re-publishes both events and rebuilds the configuration.
        var second = await ConfigureResourceAsync(app, "DocumentDB");

        Assert.Equal(1, evaluations);
        Assert.Equal("/srv/pgdata-1", first["DATA_PATH"]);
        Assert.Equal("/srv/pgdata-1", second["DATA_PATH"]);
    }

    // ---------------------------------------------------------------------
    // Being last is not enough on its own
    //
    // Aspire records each callback's result the first time it runs and reuses
    // it for the rest of the run. Anything that builds the resource's
    // configuration early — ExecutionConfigurationBuilder from a lifecycle
    // hook or an event subscriber, which is the same public API Aspire itself
    // uses — freezes the storage verdict. Storage is the sharpest form of the
    // problem, because a volume or bind mount is a plain annotation: adding
    // one afterwards changes what the container really mounts without running
    // a single line of the guard again.
    //
    // The verdict is therefore recorded and compared at the two checkpoints
    // Aspire never caches: the container-runtime-arguments callback in a run,
    // and the manifest publishing callback — which runs while the resource is
    // being serialized, later than any model event — in a publish.
    // ---------------------------------------------------------------------

    /// <summary>
    /// The reported bypass, in a run: a lifecycle hook gathers the configuration and only then
    /// mounts <c>/data</c> read-only. The environment the container receives is the one recorded
    /// before the mount existed, so nothing would re-check it; DocumentDB 0.116 chowns and chmods
    /// its data directory on start and cannot come up on a read-only one.
    /// </summary>
    [Fact]
    public async Task AReadOnlyDataMountAddedAfterAGatherFailsTheRun()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB").WithImageTag(InterlockedTag);

#pragma warning disable CS0618 // Type or member is obsolete
        appBuilder.Services.AddSingleton<IDistributedApplicationLifecycleHook>(
            new GatherThenMutateLifecycleHook(
                ["DocumentDB"],
                model => SingleServerResource(model, "DocumentDB").Annotations.Add(
                    new ContainerMountAnnotation("late-data", "/data", ContainerMountType.Volume, isReadOnly: true)),
                DistributedApplicationOperation.Run));
#pragma warning restore CS0618

        using var app = appBuilder.Build();

        await PublishBeforeStartAsync(app);
        await RunLifecycleHooksAsync(app);
        await PublishBeforeResourceStartedAsync(app, "DocumentDB");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunContainerRuntimeArgsAsync(SingleServerResource(app, "DocumentDB")));

        Assert.Contains("was changed after its data directory ('/data') had already been checked", exception.Message, StringComparison.Ordinal);
        Assert.Contains("a volume or bind mount was added, removed or changed", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same thing in a publish, through the real publisher. Publish raises no per-resource
    /// event, and a subscriber registered after this package could run after
    /// <c>BeforePublishEvent</c> too, so the check has to live where the resource is actually
    /// serialized.
    /// </summary>
    [Fact]
    public async Task AReadOnlyDataMountAddedAfterAGatherFailsRealManifestPublication()
    {
        var log = await ManifestUtils.PublishManifestExpectingFailureAsync(appBuilder =>
        {
            appBuilder.AddDocumentDB("DocumentDB").WithImageTag(InterlockedTag);

#pragma warning disable CS0618 // Type or member is obsolete
            appBuilder.Services.AddSingleton<IDistributedApplicationLifecycleHook>(
                new GatherThenMutateLifecycleHook(
                    ["DocumentDB"],
                    model => SingleServerResource(model, "DocumentDB").Annotations.Add(
                        new ContainerMountAnnotation("late-data", "/data", ContainerMountType.Volume, isReadOnly: true))));
#pragma warning restore CS0618
        });

        Assert.Contains("was changed after its data directory ('/data') had already been checked", log, StringComparison.Ordinal);
        Assert.Contains("a volume or bind mount was added, removed or changed", log, StringComparison.Ordinal);
    }

    /// <summary>
    /// A subscriber registered after <c>AddDocumentDB</c> gets the last <c>BeforePublishEvent</c>,
    /// which is after every retake this package can arrange. Replacing the judged data volume with
    /// a read-only bind mount there still has to be caught.
    /// </summary>
    [Fact]
    public async Task ReplacingTheDataMountFromALateBeforePublishSubscriberFailsPublication()
    {
        var log = await ManifestUtils.PublishManifestExpectingFailureAsync(appBuilder =>
        {
            var documentDB = appBuilder.AddDocumentDB("DocumentDB")
                .WithImageTag(InterlockedTag)
                .WithDataVolume(name: "documentdb-data");

            appBuilder.Eventing.Subscribe<Publishing.BeforePublishEvent>(async (evt, token) =>
            {
                var resource = SingleServerResource(
                    evt.Services.GetRequiredService<DistributedApplicationModel>(), "DocumentDB");

                await ExecutionConfigurationBuilder.Create(resource)
                    .WithArgumentsConfig()
                    .WithEnvironmentVariablesConfig()
                    .BuildAsync(
                        new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish),
                        NullLogger.Instance,
                        token);

                foreach (var mount in resource.Annotations.OfType<ContainerMountAnnotation>().ToList())
                {
                    resource.Annotations.Remove(mount);
                }

                resource.Annotations.Add(
                    new ContainerMountAnnotation("/srv/readonly", "/data", ContainerMountType.BindMount, isReadOnly: true));
            });

            _ = documentDB;
        });

        Assert.Contains("was changed after its data directory ('/data') had already been checked", log, StringComparison.Ordinal);
    }

    /// <summary>
    /// The corruption guard's own bypass: two resources on an image that predates the data
    /// directory's exclusive lock gather with no storage at all, and only then are put on one
    /// named volume. Nothing at runtime would refuse the pairing on those images, so the check has
    /// to.
    /// </summary>
    [Fact]
    public async Task TwoOldImagesPutOnOneVolumeAfterAGatherFailPublication()
    {
        var log = await ManifestUtils.PublishManifestExpectingFailureAsync(appBuilder =>
        {
            appBuilder.AddDocumentDB("primary").WithImageTag("pg17-0.114.0");
            appBuilder.AddDocumentDB("secondary").WithImageTag("pg17-0.114.0");

#pragma warning disable CS0618 // Type or member is obsolete
            appBuilder.Services.AddSingleton<IDistributedApplicationLifecycleHook>(
                new GatherThenMutateLifecycleHook(
                    ["primary", "secondary"],
                    model =>
                    {
                        foreach (var name in new[] { "primary", "secondary" })
                        {
                            SingleServerResource(model, name).Annotations.Add(
                                new ContainerMountAnnotation("shared-late", "/data", ContainerMountType.Volume, isReadOnly: false));
                        }
                    }));
#pragma warning restore CS0618
        });

        Assert.Contains("was changed after its data directory ('/data') had already been checked", log, StringComparison.Ordinal);
        Assert.Contains("a volume or bind mount was added, removed or changed", log, StringComparison.Ordinal);
        Assert.Contains("a shared one puts two clusters on one directory", log, StringComparison.Ordinal);
    }

    /// <summary>
    /// Storage that is re-declared rather than changed is the same storage. The mounts are
    /// recorded by value and in a fixed order, so replacing an annotation with an identical one —
    /// or shuffling the collection — leaves the verdict standing and the manifest is published.
    /// </summary>
    [Fact]
    public async Task ReDeclaringTheSameStorageAfterAGatherIsAccepted()
    {
        var manifest = await ManifestUtils.PublishManifestAsync(appBuilder =>
        {
            appBuilder.AddDocumentDB("DocumentDB")
                .WithImageTag(InterlockedTag)
                .WithDataVolume(name: "documentdb-data")
                .WithBindMount("./certs", "/certs", isReadOnly: true);

#pragma warning disable CS0618 // Type or member is obsolete
            appBuilder.Services.AddSingleton<IDistributedApplicationLifecycleHook>(
                new GatherThenMutateLifecycleHook(
                    ["DocumentDB"],
                    model =>
                    {
                        var resource = SingleServerResource(model, "DocumentDB");
                        var mounts = resource.Annotations.OfType<ContainerMountAnnotation>().ToList();

                        foreach (var mount in mounts)
                        {
                            resource.Annotations.Remove(mount);
                        }

                        // Fresh instances, reversed, describing exactly the same storage.
                        mounts.Reverse();
                        foreach (var mount in mounts)
                        {
                            resource.Annotations.Add(
                                new ContainerMountAnnotation(mount.Source, mount.Target, mount.Type, mount.IsReadOnly));
                        }
                    }));
#pragma warning restore CS0618
        });

        var resource = manifest["resources"]?["DocumentDB"];
        Assert.NotNull(resource);
        Assert.Equal("/data", resource!["env"]?["DATA_PATH"]?.GetValue<string>());

        var volume = Assert.Single(resource["volumes"]!.AsArray());
        Assert.Equal("documentdb-data", volume!["name"]?.GetValue<string>());
        Assert.Equal("/data", volume["target"]?.GetValue<string>());
    }

    /// <summary>
    /// The publish checkpoint has to be invisible when nothing is wrong: it writes exactly what
    /// the publisher would have written for the resource on its own.
    /// </summary>
    [Fact]
    public async Task ThePublishCheckpointLeavesTheManifestUnchanged()
    {
        var manifest = await ManifestUtils.PublishManifestAsync(appBuilder =>
            appBuilder.AddDocumentDB("DocumentDB")
                .WithImageTag(InterlockedTag)
                .WithDataBindMount("./pgdata"));

        var resource = manifest["resources"]?["DocumentDB"];
        Assert.NotNull(resource);
        Assert.Equal("container.v0", resource!["type"]?.GetValue<string>());
        Assert.Equal(
            $"{DocumentDBContainerImageTags.Registry}/{DocumentDBContainerImageTags.Image}:{InterlockedTag}",
            resource["image"]?.GetValue<string>());
        Assert.Equal("/data", resource["env"]?["DATA_PATH"]?.GetValue<string>());
        Assert.NotNull(resource["connectionString"]);
        Assert.NotNull(resource["bindings"]);

        var bindMount = Assert.Single(resource["bindMounts"]!.AsArray());
        Assert.Equal("/data", bindMount!["target"]?.GetValue<string>());
        Assert.False(bindMount["readOnly"]?.GetValue<bool>());
    }

    /// <summary>
    /// A resource the caller excluded is not written at all, checkpoint or no checkpoint: there is
    /// no published configuration to be wrong about, and taking the last manifest position from
    /// the exclusion would publish a resource the caller removed.
    /// </summary>
    [Fact]
    public async Task AnExcludedResourceIsStillAbsentFromTheManifest()
    {
        var manifest = await ManifestUtils.PublishManifestAsync(appBuilder =>
            appBuilder.AddDocumentDB("DocumentDB")
                .WithImageTag(InterlockedTag)
                .WithDataVolume()
                .ExcludeFromManifest());

        Assert.Null(manifest["resources"]?["DocumentDB"]);
    }

    /// <summary>
    /// The one shape where the storage rules legitimately decline to judge: publish mode with a
    /// <c>DATA_PATH</c> that is a manifest expression and no storage at all. "Mounts nothing" is
    /// still an observation, and a mount added after it has been made contradicts it.
    /// </summary>
    [Fact]
    public async Task StorageAddedAfterADeferredDataPathWasAcceptedFailsPublication()
    {
        var log = await ManifestUtils.PublishManifestExpectingFailureAsync(appBuilder =>
        {
            var dataPath = appBuilder.AddParameter("datapath", "/pgdata");
            appBuilder.AddDocumentDB("DocumentDB")
                .WithImageTag(InterlockedTag)
                .WithEnvironment("DATA_PATH", dataPath);

#pragma warning disable CS0618 // Type or member is obsolete
            appBuilder.Services.AddSingleton<IDistributedApplicationLifecycleHook>(
                new GatherThenMutateLifecycleHook(
                    ["DocumentDB"],
                    model => SingleServerResource(model, "DocumentDB").Annotations.Add(
                        new ContainerMountAnnotation("late-data", "/pgdata", ContainerMountType.Volume, isReadOnly: true))));
#pragma warning restore CS0618
        });

        Assert.Contains("was changed after its storage (it mounted none) had already been checked", log, StringComparison.Ordinal);
    }

    /// <summary>
    /// The window between the run's checkpoint and the gather it protects. Aspire invokes every
    /// container-runtime-argument callback, in annotation order, and only then builds the
    /// container's environment, so a callback appended after the checkpoint runs once the verdict
    /// has already been re-checked — and can still append an environment callback that moves
    /// <c>DATA_PATH</c> onto read-only storage, because the guard's own environment callback is
    /// cached and no longer re-checks anything.
    /// </summary>
    [Fact]
    public async Task ARuntimeArgumentCallbackAddedAfterAGatherFailsTheRun()
    {
        var appBuilder = CreateAppBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithDataVolume(name: "documentdb-data")
            .WithVolume("documentdb-archive", "/archive", isReadOnly: true);

        appBuilder.Eventing.Subscribe<BeforeResourceStartedEvent>(documentDB.Resource, async (_, token) =>
        {
            var resource = documentDB.Resource;

            await ExecutionConfigurationBuilder.Create(resource)
                .WithArgumentsConfig()
                .WithEnvironmentVariablesConfig()
                .BuildAsync(
                    new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
                    NullLogger.Instance,
                    token);

            resource.Annotations.Add(new ContainerRuntimeArgsCallbackAnnotation(_ =>
            {
                resource.Annotations.Add(new EnvironmentCallbackAnnotation(
                    (Func<EnvironmentCallbackContext, Task>)(context =>
                    {
                        context.EnvironmentVariables["DATA_PATH"] = "/archive";
                        return Task.CompletedTask;
                    })));

                return Task.CompletedTask;
            }));
        });

        using var app = appBuilder.Build();

        await PublishBeforeStartAsync(app);
        await PublishBeforeResourceStartedAsync(app, "DocumentDB");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunContainerRuntimeArgsAsync(SingleServerResource(app, "DocumentDB")));

        Assert.Contains("has a later container-runtime-arguments callback registered after", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A lifecycle hook that moves <c>DATA_PATH</c> without reading the configuration first is
    /// repairable rather than fatal. Publish raises no per-resource event, so
    /// <c>BeforePublishEvent</c> is where the guard takes the last position in the environment
    /// pipeline back; the value it then reads is judged on its own terms — here, onto a read-only
    /// mount — instead of being reported as an ordering problem.
    /// </summary>
    [Fact]
    public async Task ADataPathSetByALifecycleHookIsJudgedOnItsOwnTermsWhenPublishing()
    {
        var log = await ManifestUtils.PublishManifestExpectingFailureAsync(appBuilder =>
        {
            appBuilder.AddDocumentDB("DocumentDB")
                .WithImageTag(InterlockedTag)
                .WithDataVolume(name: "documentdb-data")
                .WithVolume("documentdb-archive", "/pgdata", isReadOnly: true);

#pragma warning disable CS0618 // Type or member is obsolete
            appBuilder.Services.AddSingleton<IDistributedApplicationLifecycleHook>(
                new DataPathLifecycleHook("DocumentDB", "/pgdata"));
#pragma warning restore CS0618
        });

        Assert.Contains("mounts its data directory ('/pgdata') read-only", log, StringComparison.Ordinal);
    }

    /// <summary>
    /// Invokes the resource's container-runtime-argument callbacks the way Aspire's container
    /// creator does: in annotation order, over one shared list, with no result cache — which is
    /// what makes this the run's unconditional checkpoint.
    /// </summary>
    private static async Task<string[]> RunContainerRuntimeArgsAsync(DocumentDBServerResource resource)
    {
        var args = new List<object>();
        var context = new ContainerRuntimeArgsCallbackContext(args, CancellationToken.None);

        foreach (var annotation in resource.Annotations.OfType<ContainerRuntimeArgsCallbackAnnotation>())
        {
            await annotation.Callback(context);
        }

        return [.. args.Select(argument => argument as string ?? argument.ToString()!)];
    }

    private static DocumentDBServerResource SingleServerResource(DistributedApplication app, string name) =>
        SingleServerResource(app.Services.GetRequiredService<DistributedApplicationModel>(), name);

    private static DocumentDBServerResource SingleServerResource(DistributedApplicationModel model, string name) =>
        model.Resources.OfType<DocumentDBServerResource>().Single(resource => resource.Name == name);

#pragma warning disable CS0618 // Type or member is obsolete
    private static async Task RunLifecycleHooksAsync(DistributedApplication app)
    {
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        foreach (var hook in app.Services.GetServices<IDistributedApplicationLifecycleHook>())
        {
            await hook.BeforeStartAsync(model, CancellationToken.None);
        }
    }

    /// <summary>
    /// A lifecycle hook that builds the named resources' configuration through the same public API
    /// Aspire uses, and only then changes the model — the shape that made a recorded storage
    /// verdict stale.
    /// </summary>
    private sealed class GatherThenMutateLifecycleHook(
        string[] resourceNames,
        Action<DistributedApplicationModel> mutate,
        DistributedApplicationOperation operation = DistributedApplicationOperation.Publish)
        : IDistributedApplicationLifecycleHook
    {
        public async Task BeforeStartAsync(DistributedApplicationModel appModel, CancellationToken cancellationToken = default)
        {
            foreach (var name in resourceNames)
            {
                await ExecutionConfigurationBuilder.Create(SingleServerResource(appModel, name))
                    .WithArgumentsConfig()
                    .WithEnvironmentVariablesConfig()
                    .BuildAsync(
                        new DistributedApplicationExecutionContext(operation),
                        NullLogger.Instance,
                        cancellationToken);
            }

            mutate(appModel);
        }
    }
#pragma warning restore CS0618

    // The lifecycle-hook interface is obsolete in favour of eventing subscribers, but Aspire still
    // runs registered hooks — after BeforeStartEvent — so it remains a way to mutate the model late
    // and the guard has to hold against it.
#pragma warning disable CS0618 // Type or member is obsolete
    private sealed class DataPathLifecycleHook(string resourceName, string dataPath) : IDistributedApplicationLifecycleHook
    {
        public Task BeforeStartAsync(DistributedApplicationModel appModel, CancellationToken cancellationToken = default)
        {
            var resource = appModel.Resources.OfType<DocumentDBServerResource>().Single(r => r.Name == resourceName);
            resource.Annotations.Add(new EnvironmentCallbackAnnotation((Func<EnvironmentCallbackContext, Task>)(context =>
            {
                context.EnvironmentVariables["DATA_PATH"] = dataPath;
                return Task.CompletedTask;
            })));
            return Task.CompletedTask;
        }
    }

    private sealed class DataPathArgumentLifecycleHook(string resourceName) : IDistributedApplicationLifecycleHook
    {
        public Task BeforeStartAsync(DistributedApplicationModel appModel, CancellationToken cancellationToken = default)
        {
            var resource = appModel.Resources.OfType<DocumentDBServerResource>().Single(r => r.Name == resourceName);
            resource.Annotations.Add(new CommandLineArgsCallbackAnnotation((Func<CommandLineArgsCallbackContext, Task>)(context =>
            {
                context.Args.Add("--data-path");
                context.Args.Add("/pgdata");
                return Task.CompletedTask;
            })));
            return Task.CompletedTask;
        }
    }
#pragma warning restore CS0618

    // ---------------------------------------------------------------------
    // Bind identity is the host directory, not the pair of strings
    // ---------------------------------------------------------------------

    /// <summary>
    /// One resource binds the parent and writes to a subdirectory; the other binds that
    /// subdirectory and writes to the mount target. Two spellings, one host directory, two
    /// PostgreSQL clusters — and on a pre-<c>0.116.0</c> image nothing at runtime would notice.
    /// </summary>
    [Fact]
    public async Task ABindParentAndItsNestedSourceAreTheSameDataDirectory()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("parent")
            .WithImageTag("pg17-0.114.0")
            .WithBindMount("/srv/documentdb", "/data")
            .WithEnvironment("DATA_PATH", "/data/cluster");
        appBuilder.AddDocumentDB("nested")
            .WithImageTag("pg17-0.114.0")
            .WithBindMount("/srv/documentdb/cluster", "/data");

        using var app = appBuilder.Build();

        await ConfigureResourceAsync(app, "parent");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "nested"));

        Assert.Contains(Path.GetFullPath("/srv/documentdb/cluster"), exception.Message, StringComparison.Ordinal);
        Assert.Contains("no data-directory interlock", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>The same pair in the other registration order.</summary>
    [Fact]
    public async Task ANestedBindSourceAndItsParentAreTheSameDataDirectory()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("nested")
            .WithImageTag("pg17-0.114.0")
            .WithBindMount("/srv/documentdb/cluster", "/data");
        appBuilder.AddDocumentDB("parent")
            .WithImageTag("pg17-0.114.0")
            .WithBindMount("/srv/documentdb", "/data")
            .WithEnvironment("DATA_PATH", "/data/./cluster");

        using var app = appBuilder.Build();

        await ConfigureResourceAsync(app, "nested");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "parent"));

        Assert.Contains(Path.GetFullPath("/srv/documentdb/cluster"), exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A deeper nesting, and one where the subdirectory itself has several segments.
    /// </summary>
    [Fact]
    public async Task ABindSourceIsCombinedWithTheWholeDataPathSubdirectory()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("parent")
            .WithImageTag("pg17-0.114.0")
            .WithBindMount("/srv", "/data")
            .WithEnvironment("DATA_PATH", "/data/documentdb/cluster");
        appBuilder.AddDocumentDB("nested")
            .WithImageTag("pg17-0.114.0")
            .WithBindMount("/srv/documentdb", "/data")
            .WithEnvironment("DATA_PATH", "/data/cluster");

        using var app = appBuilder.Build();

        await ConfigureResourceAsync(app, "parent");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "nested"));

        Assert.Contains(Path.GetFullPath("/srv/documentdb/cluster"), exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Sibling subdirectories of one bind source are still two directories.
    /// </summary>
    [Fact]
    public async Task DistinctSubdirectoriesOfOneBindSourceAreNotShared()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("alpha")
            .WithBindMount("/srv/documentdb", "/data")
            .WithEnvironment("DATA_PATH", "/data/alpha");
        appBuilder.AddDocumentDB("beta")
            .WithBindMount("/srv/documentdb/beta", "/data");

        using var app = appBuilder.Build();
        var sink = new CapturingLoggerSink();

        await ConfigureResourceAsync(app, "alpha", sink);
        await ConfigureResourceAsync(app, "beta", sink);

        Assert.DoesNotContain(sink.LogEntries, e => e.Level == LogLevel.Warning);
    }

    /// <summary>
    /// The subdirectory of a bind mount is a host path segment once the container writes through
    /// the mount, so whether <c>Cluster</c> and <c>cluster</c> are one directory is the host's
    /// answer, not Linux's. The assertion follows the platform the test runs on, which is the same
    /// rule the guard applies to bind sources themselves.
    /// </summary>
    [Fact]
    public async Task ABindSubdirectoryFollowsTheHostsCaseRules()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("lower")
            .WithImageTag("pg17-0.114.0")
            .WithBindMount("/srv/documentdb", "/data")
            .WithEnvironment("DATA_PATH", "/data/cluster");
        appBuilder.AddDocumentDB("upper")
            .WithImageTag("pg17-0.114.0")
            .WithBindMount("/srv/documentdb", "/data")
            .WithEnvironment("DATA_PATH", "/data/Cluster");

        using var app = appBuilder.Build();
        var sink = new CapturingLoggerSink();

        await ConfigureResourceAsync(app, "lower", sink);

        if (OperatingSystem.IsLinux())
        {
            // Case-sensitive host: two directories.
            await ConfigureResourceAsync(app, "upper", sink);
            Assert.DoesNotContain(sink.LogEntries, e => e.Level == LogLevel.Warning);
            return;
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureResourceAsync(app, "upper", sink));

        Assert.Contains("no data-directory interlock", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A volume name is not a host path, so its subdirectory is read by the container on its own
    /// case-sensitive filesystem. That answer is the same on every host.
    /// </summary>
    [Fact]
    public async Task AVolumeSubdirectoryIsAlwaysCaseSensitive()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("lower")
            .WithVolume("shared-data", "/data")
            .WithEnvironment("DATA_PATH", "/data/cluster");
        appBuilder.AddDocumentDB("upper")
            .WithVolume("shared-data", "/data")
            .WithEnvironment("DATA_PATH", "/data/Cluster");

        using var app = appBuilder.Build();
        var sink = new CapturingLoggerSink();

        await ConfigureResourceAsync(app, "lower", sink);
        await ConfigureResourceAsync(app, "upper", sink);

        Assert.DoesNotContain(sink.LogEntries, e => e.Level == LogLevel.Warning);
    }

    // ---------------------------------------------------------------------
    // Deferred command-line tokens
    // ---------------------------------------------------------------------

    /// <summary>
    /// A token whose value arrives later sits where the entrypoint reads an option name. It could
    /// resolve to <c>--data-path</c>, and the only way to know would be to resolve it a second
    /// time, so the resource is failed instead.
    /// </summary>
    [Theory]
    [InlineData(DistributedApplicationOperation.Run)]
    [InlineData(DistributedApplicationOperation.Publish)]
    public async Task AParameterInAnOptionNamePositionIsRejected(DistributedApplicationOperation operation)
    {
        var appBuilder = CreateAppBuilder();
        // The value is what makes this dangerous, and it is deliberately distinctive so the
        // assertion below can prove the guard never read it.
        var flag = appBuilder.AddParameter("flag", "--data-path=/secret-cluster-location");
        appBuilder.AddDocumentDB("DocumentDB").WithArgs(flag, "/pgdata");

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureArgumentsAsync(app, "DocumentDB", operation: operation));

        Assert.Contains("only known later", exception.Message, StringComparison.Ordinal);
        Assert.Contains("reads an option name", exception.Message, StringComparison.Ordinal);

        // The token is never resolved, so nothing it holds can reach the message.
        Assert.DoesNotContain("secret-cluster-location", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DistributedApplicationOperation.Run)]
    [InlineData(DistributedApplicationOperation.Publish)]
    public async Task AReferenceExpressionInAnOptionNamePositionIsRejected(DistributedApplicationOperation operation)
    {
        var appBuilder = CreateAppBuilder();
        var prefix = appBuilder.AddParameter("prefix", "--data");
        appBuilder.AddDocumentDB("DocumentDB")
            .WithArgs(ReferenceExpression.Create($"{prefix}-path"), "/pgdata");

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureArgumentsAsync(app, "DocumentDB", operation: operation));

        Assert.Contains("reads an option name", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeferredTokenAsTheFirstArgumentIsRejected()
    {
        var appBuilder = CreateAppBuilder();
        var flag = appBuilder.AddParameter("flag", "--log-level");
        appBuilder.AddDocumentDB("DocumentDB").WithArgs(flag);

        using var app = appBuilder.Build();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureArgumentsAsync(app, "DocumentDB"));
    }

    /// <summary>
    /// The entrypoint's grammar is <c>--option value</c>, so a token directly after an option that
    /// takes a value is that option's operand and can never be read as an option name. Those are
    /// left alone — a deferred value is the normal way to pass a password or a port.
    /// </summary>
    [Theory]
    [InlineData(DistributedApplicationOperation.Run)]
    [InlineData(DistributedApplicationOperation.Publish)]
    public async Task ADeferredTokenInAnOperandPositionIsAllowed(DistributedApplicationOperation operation)
    {
        var appBuilder = CreateAppBuilder();
        var level = appBuilder.AddParameter("level", "debug");
        appBuilder.AddDocumentDB("DocumentDB").WithArgs("--log-level", level);

        using var app = appBuilder.Build();

        var arguments = await ConfigureArgumentsAsync(app, "DocumentDB", operation: operation);

        Assert.Equal("--log-level", arguments[0]);
        Assert.Equal(2, arguments.Length);
    }

    /// <summary>
    /// The entrypoint consumes exactly one token after a value-taking option, whatever that token
    /// looks like. <c>--username --owner X</c> therefore feeds <c>--owner</c> to <c>--username</c>
    /// and reads <c>X</c> as an option name — verified against the image, which answers
    /// <c>Using username: --owner</c> and then honours <c>--data-path</c>. A model that only
    /// tracked operands for deferred tokens would think <c>X</c> was sheltered by <c>--owner</c>.
    /// </summary>
    [Fact]
    public async Task ADeferredTokenAfterAnOptionThatWasItselfAnOperandIsRejected()
    {
        var appBuilder = CreateAppBuilder();
        var flag = appBuilder.AddParameter("flag", "--data-path");
        appBuilder.AddDocumentDB("DocumentDB").WithArgs("--username", "--owner", flag, "/pwned");

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureArgumentsAsync(app, "DocumentDB"));

        Assert.Contains("reads an option name", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same rule the other way round: a literal <c>--data-path</c> sitting in an operand
    /// position is a value, not an option, and the entrypoint treats it as one
    /// (<c>LOG_LEVEL=--data-path</c>). Failing it would be a false positive.
    /// </summary>
    [Fact]
    public async Task AReservedNameInAnOperandPositionIsNotAnOption()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB").WithArgs("--log-level", "--data-path");

        using var app = appBuilder.Build();

        var arguments = await ConfigureArgumentsAsync(app, "DocumentDB");

        Assert.Equal(["--log-level", "--data-path"], arguments);
    }

    /// <summary>
    /// A deferred token as the operand of an option that itself sat in an operand position is back
    /// in an option-name slot.
    /// </summary>
    [Fact]
    public async Task OperandTrackingResumesAfterEachConsumedToken()
    {
        var appBuilder = CreateAppBuilder();
        var level = appBuilder.AddParameter("level", "debug");
        appBuilder.AddDocumentDB("DocumentDB").WithArgs("--log-level", level, "--owner", "documentdb");

        using var app = appBuilder.Build();

        var arguments = await ConfigureArgumentsAsync(app, "DocumentDB");

        Assert.Equal(4, arguments.Length);
    }

    /// <summary>
    /// An option that takes no operand does not shelter the token after it: the entrypoint reads
    /// that one as the next option name.
    /// </summary>
    [Fact]
    public async Task ADeferredTokenAfterAValuelessOptionIsRejected()
    {
        var appBuilder = CreateAppBuilder();
        var flag = appBuilder.AddParameter("flag", "--data-path");
        appBuilder.AddDocumentDB("DocumentDB").WithArgs("--skip-init-data", flag, "/pgdata");

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureArgumentsAsync(app, "DocumentDB"));

        Assert.Contains("reads an option name", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An option that carries its own operand (<c>--option=value</c>) does not shelter the next
    /// token either.
    /// </summary>
    [Fact]
    public async Task ADeferredTokenAfterAJoinedOptionIsRejected()
    {
        var appBuilder = CreateAppBuilder();
        var flag = appBuilder.AddParameter("flag", "--data-path");
        appBuilder.AddDocumentDB("DocumentDB").WithArgs("--log-level=debug", flag);

        using var app = appBuilder.Build();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureArgumentsAsync(app, "DocumentDB"));
    }

    /// <summary>
    /// Two operands in a row: the second is not sheltered by the first.
    /// </summary>
    [Fact]
    public async Task ADeferredTokenAfterAnOperandIsRejected()
    {
        var appBuilder = CreateAppBuilder();
        var flag = appBuilder.AddParameter("flag", "--data-path");
        appBuilder.AddDocumentDB("DocumentDB").WithArgs("--log-level", "debug", flag);

        using var app = appBuilder.Build();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigureArgumentsAsync(app, "DocumentDB"));
    }

    // ---------------------------------------------------------------------
    // Manifest publishing
    // ---------------------------------------------------------------------

    /// <summary>
    /// The manifest writer builds environment and arguments through the same pipeline the container
    /// creator uses, so the guard applies to <c>aspire publish</c> as well: a manifest that would
    /// deploy two DocumentDB resources onto one data directory is refused rather than written.
    /// </summary>
    [Fact]
    public async Task PublishingAManifestRefusesTwoResourcesOnOneDataDirectory()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("primary").WithDataVolume(name: "shared-data");
        appBuilder.AddDocumentDB("secondary").WithDataVolume(name: "shared-data");

        using var app = appBuilder.Build();

        var primary = await PublishManifestAsync(app, "primary");
        Assert.Equal("/data", primary["env"]?["DATA_PATH"]?.ToString());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PublishManifestAsync(app, "secondary"));

        Assert.Contains("both use the same volume 'shared-data'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishingAManifestRefusesTwoResourcesOnOneHostDirectory()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("parent")
            .WithBindMount("/srv/documentdb", "/data")
            .WithEnvironment("DATA_PATH", "/data/cluster");
        appBuilder.AddDocumentDB("nested").WithBindMount("/srv/documentdb/cluster", "/data");

        using var app = appBuilder.Build();

        await PublishManifestAsync(app, "parent");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PublishManifestAsync(app, "nested"));

        Assert.Contains(Path.GetFullPath("/srv/documentdb/cluster"), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishingAManifestRefusesTheEntrypointsDataPathArgument()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB").WithDataVolume().WithArgs("--data-path", "/pgdata");

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PublishManifestAsync(app, "DocumentDB"));

        Assert.Contains("passes the command-line argument '--data-path'", exception.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------
    // The guard is part of the model before anything can read it
    //
    // Aspire records each callback's result the first time a pipeline is
    // gathered and then takes the last annotation's recording as the answer
    // for the rest of the run. A callback that appears after a gather does not
    // add to that answer, it replaces it - so a guard installed by a lifecycle
    // event would drop every value produced before that event.
    // ---------------------------------------------------------------------

    /// <summary>
    /// The guard participates in the very first gather, before any event is published. This is the
    /// negative control for eager installation: with the guard introduced by a lifecycle event
    /// instead, this gather produces no <c>DATA_PATH</c> at all.
    /// </summary>
    [Fact]
    public async Task TheStorageGuardIsPartOfTheEnvironmentPipelineBeforeAnyEventIsPublished()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB").WithDataVolume();

        using var app = appBuilder.Build();
        var resource = app.Services.GetRequiredService<DistributedApplicationModel>()
            .Resources.OfType<DocumentDBServerResource>().Single();

        var environment = await BuildEnvironmentVariablesAsync(resource);

        Assert.Equal("/data", environment["DATA_PATH"]);
        Assert.True(environment.ContainsKey("USERNAME"));
        Assert.True(environment.ContainsKey("PASSWORD"));
    }

    /// <summary>
    /// A subscriber registered before <c>AddDocumentDB</c> runs before this package's own
    /// <see cref="BeforeStartEvent"/> subscriber, and building the configuration through the public
    /// <see cref="ExecutionConfigurationBuilder"/> is exactly what Aspire itself does. Every value
    /// the resource had already produced has to survive that.
    /// </summary>
    [Fact]
    public async Task AnEarlyGatherFromASubscriberRegisteredBeforeAddDocumentDBKeepsEveryValue()
    {
        var appBuilder = CreateAppBuilder();

        var gathered = 0;
        appBuilder.Eventing.Subscribe<BeforeStartEvent>(async (evt, ct) =>
        {
            var early = evt.Model.Resources.OfType<DocumentDBServerResource>().Single();
            await ExecutionConfigurationBuilder.Create(early)
                .WithEnvironmentVariablesConfig()
                .BuildAsync(
                    new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
                    NullLogger.Instance,
                    ct);
            Interlocked.Increment(ref gathered);
        });

        appBuilder.AddDocumentDB("DocumentDB")
            .WithDataVolume(targetPath: "/pgdata")
            .WithLogLevel(DocumentDBLogLevel.Debug)
            .WithOwner("contoso");

        using var app = appBuilder.Build();

        var environment = await ConfigureResourceAsync(app, "DocumentDB");

        Assert.Equal(1, gathered);
        Assert.Equal("admin", environment["USERNAME"]);
        Assert.False(string.IsNullOrEmpty(environment["PASSWORD"]));
        Assert.Equal("debug", environment["LOG_LEVEL"]);
        Assert.Equal("contoso", environment["OWNER"]);
        Assert.Equal("/pgdata", environment["DATA_PATH"]);
    }

    /// <summary>
    /// The publish side of the same thing, through Aspire's own manifest writer.
    /// </summary>
    [Fact]
    public async Task AnEarlyGatherFromASubscriberRegisteredBeforeAddDocumentDBKeepsEveryValueInTheManifest()
    {
        var appBuilder = CreateAppBuilder();

        appBuilder.Eventing.Subscribe<BeforeStartEvent>(async (evt, ct) =>
        {
            var early = evt.Model.Resources.OfType<DocumentDBServerResource>().Single();
            await ExecutionConfigurationBuilder.Create(early)
                .WithEnvironmentVariablesConfig()
                .BuildAsync(
                    new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish),
                    NullLogger.Instance,
                    ct);
        });

        appBuilder.AddDocumentDB("DocumentDB")
            .WithDataVolume()
            .WithoutSampleData();

        using var app = appBuilder.Build();

        var manifest = await PublishManifestAsync(app, "DocumentDB");

        Assert.Equal("admin", manifest["env"]?["USERNAME"]?.ToString());
        Assert.Equal("{DocumentDB-password.value}", manifest["env"]?["PASSWORD"]?.ToString());
        Assert.Equal("true", manifest["env"]?["SKIP_INIT_DATA"]?.ToString());
        Assert.Equal("/data", manifest["env"]?["DATA_PATH"]?.ToString());
    }

    /// <summary>
    /// Installing the guard before <c>AddDocumentDB</c> returns has to leave the storage rules
    /// exactly as sharp: this is the same read-only mount the guard rejects on a later phase.
    /// </summary>
    [Fact]
    public async Task AnEarlyGatherStillAppliesTheStorageRules()
    {
        var appBuilder = CreateAppBuilder();

        appBuilder.Eventing.Subscribe<BeforeStartEvent>(async (evt, ct) =>
        {
            var resource = evt.Model.Resources.OfType<DocumentDBServerResource>().Single();
            await ExecutionConfigurationBuilder.Create(resource)
                .WithEnvironmentVariablesConfig()
                .BuildAsync(
                    new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
                    NullLogger.Instance,
                    ct);
        });

        appBuilder.AddDocumentDB("DocumentDB").WithVolume("raw-data", "/data", isReadOnly: true);

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => PublishBeforeStartAsync(app));

        Assert.Contains("mounts its data directory ('/data') read-only", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The one shape an early gather cannot be made to work for: a raw Aspire
    /// <c>WithEnvironment(...)</c> added after <c>AddDocumentDB</c> sits behind the guard until the
    /// first lifecycle phase moves the guard back, and a subscriber registered before
    /// <c>AddDocumentDB</c> reads the configuration before that phase. Aspire would then answer with
    /// a recording made in the wrong position, so the resource is failed instead of being started on
    /// an environment nobody checked.
    /// </summary>
    [Fact]
    public async Task AnEarlyGatherBehindACallerCallbackAddedAfterAddDocumentDBIsRefused()
    {
        var appBuilder = CreateAppBuilder();

        appBuilder.Eventing.Subscribe<BeforeStartEvent>(async (evt, ct) =>
        {
            var resource = evt.Model.Resources.OfType<DocumentDBServerResource>().Single();
            await ExecutionConfigurationBuilder.Create(resource)
                .WithEnvironmentVariablesConfig()
                .BuildAsync(
                    new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
                    NullLogger.Instance,
                    ct);
        });

        appBuilder.AddDocumentDB("DocumentDB")
            .WithDataVolume()
            .WithEnvironment("CONTOSO", "value");

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => PublishBeforeStartAsync(app));

        Assert.Contains("has a later environment callback registered after its data-storage guard", exception.Message, StringComparison.Ordinal);
        Assert.Contains("register a subscriber that does read it after AddDocumentDB", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the recovery the message names actually works: registering the same subscriber after
    /// <c>AddDocumentDB</c> lets this package take the last position first.
    /// </summary>
    [Fact]
    public async Task AnEarlyGatherFromASubscriberRegisteredAfterAddDocumentDBKeepsCallerValues()
    {
        var appBuilder = CreateAppBuilder();

        appBuilder.AddDocumentDB("DocumentDB")
            .WithDataVolume()
            .WithEnvironment("CONTOSO", "value");

        appBuilder.Eventing.Subscribe<BeforeStartEvent>(async (evt, ct) =>
        {
            var resource = evt.Model.Resources.OfType<DocumentDBServerResource>().Single();
            await ExecutionConfigurationBuilder.Create(resource)
                .WithEnvironmentVariablesConfig()
                .BuildAsync(
                    new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
                    NullLogger.Instance,
                    ct);
        });

        using var app = appBuilder.Build();

        var environment = await ConfigureResourceAsync(app, "DocumentDB");

        Assert.Equal("admin", environment["USERNAME"]);
        Assert.Equal("value", environment["CONTOSO"]);
        Assert.Equal("/data", environment["DATA_PATH"]);
    }

    // ---------------------------------------------------------------------
    // The manifest entry and the model it was checked against
    //
    // Aspire writes a container's image, entrypoint and mounts before it
    // evaluates the environment callbacks and its bindings after them, so a
    // supported WithEnvironment(...) callback can change the resource while it
    // is being serialized. Every test below drives the real
    // ManifestPublishingContext.WriteResourceAsync.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task AMountAddedWhileTheManifestIsWrittenIsRefused()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithEnvironment(context => context.Resource.Annotations.Add(
                new ContainerMountAnnotation("late-data", "/data", ContainerMountType.Volume, isReadOnly: false)));

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PublishManifestAsync(app, "DocumentDB"));

        Assert.Contains("while its manifest entry was being written", exception.Message, StringComparison.Ordinal);
        Assert.Contains("a volume or bind mount was added, removed or changed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMountRemovedWhileTheManifestIsWrittenIsRefused()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithDataVolume(name: "documentdb-data")
            .WithEnvironment(context =>
            {
                var mount = context.Resource.Annotations.OfType<ContainerMountAnnotation>().Single();
                context.Resource.Annotations.Remove(mount);
            });

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PublishManifestAsync(app, "DocumentDB"));

        Assert.Contains("a volume or bind mount was added, removed or changed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEndpointChangedWhileTheManifestIsWrittenIsRefused()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithEnvironment(context =>
            {
                var endpoint = context.Resource.Annotations.OfType<EndpointAnnotation>().First();
                endpoint.TargetPort = 15000;
            });

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PublishManifestAsync(app, "DocumentDB"));

        Assert.Contains("an endpoint was added, removed, reordered or re-pointed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnImageChangedWhileTheManifestIsWrittenIsRefused()
    {
        var appBuilder = CreateAppBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB");
        documentDB.WithEnvironment(_ => documentDB.WithImageTag("pg17-0.116.0"));

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PublishManifestAsync(app, "DocumentDB"));

        Assert.Contains("the image it will run changed", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A registry swapped from inside the environment evaluation names the same release, so
    /// nothing this package knows about the image changes: the repository is still the curated
    /// one, the tag is still the same version, and every floor still answers the same. What does
    /// change is the reference the entry carries — and Aspire wrote that reference before the
    /// callback ran, so the published manifest would send every deployment to the registry the
    /// resource was configured with rather than the one it ended up naming.
    /// </summary>
    [Fact]
    public async Task ARegistryChangedWhileTheManifestIsWrittenIsRefusedAfterTheOldOneWasAlreadyEmitted()
    {
        const string Mirror = "mirror.example";

        var written = $"{QualifiedOfficialImage}:{DocumentDBContainerImageTags.Tag}";
        var modelled = $"{Mirror}/{DocumentDBContainerImageTags.Image}:{DocumentDBContainerImageTags.Tag}";

        var appBuilder = CreateAppBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB");
        documentDB.WithEnvironment(_ => documentDB.WithImageRegistry(Mirror));

        using var app = appBuilder.Build();

        var (failure, entry) = await WriteManifestExpectingFailureAsync(app, "DocumentDB");

        // The writer had already emitted the registry the resource was configured with.
        Assert.Contains($"\"image\":\"{written}\"", entry, StringComparison.Ordinal);
        Assert.DoesNotContain(Mirror, entry, StringComparison.Ordinal);

        // The model, by then, names the mirror instead.
        Assert.True(documentDB.Resource.TryGetContainerImageName(out var reference));
        Assert.Equal(modelled, reference);

        // And the checkpoint refuses the publish rather than shipping the entry that disagrees.
        Assert.Contains("while its manifest entry was being written", failure.Message, StringComparison.Ordinal);
        Assert.Contains("the exact image reference it publishes changed", failure.Message, StringComparison.Ordinal);

        // The reference is configuration, not a credential, but neither reference is needed to say
        // what went wrong and neither is put in the message.
        Assert.DoesNotContain(written, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(modelled, failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The refusal is a refusal of the whole publish, exactly as it is for every other structural
    /// change: <c>aspire publish</c> exits non-zero and leaves no usable manifest behind.
    /// </summary>
    [Fact]
    public async Task APublishThatChangesTheRegistryWhileWritingFailsClosed()
    {
        var log = await ManifestUtils.PublishManifestExpectingFailureAsync(appBuilder =>
        {
            var documentDB = appBuilder.AddDocumentDB("DocumentDB");
            documentDB.WithEnvironment(_ => documentDB.WithImageRegistry("mirror.example"));
        });

        Assert.Contains("while its manifest entry was being written", log, StringComparison.Ordinal);
        Assert.Contains("the exact image reference it publishes changed", log, StringComparison.Ordinal);
    }

    /// <summary>
    /// A caller's own writer is delegated to rather than displaced, so it is just as able to change
    /// the model between the field it wrote and the checkpoint that judges it — and it is judged
    /// the same way.
    /// </summary>
    [Fact]
    public async Task ARegistryChangedByADelegatedCustomWriterIsRefused()
    {
        var appBuilder = CreateAppBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB");
        documentDB.WithManifestPublishingCallback(async context =>
        {
            await context.WriteContainerAsync(documentDB.Resource);
            documentDB.WithImageRegistry("mirror.example");
        });

        using var app = appBuilder.Build();

        var (failure, entry) = await WriteManifestExpectingFailureAsync(app, "DocumentDB");

        Assert.Contains(
            $"\"image\":\"{QualifiedOfficialImage}:{DocumentDBContainerImageTags.Tag}\"",
            entry,
            StringComparison.Ordinal);
        Assert.Contains("the exact image reference it publishes changed", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Negative control: a registry re-declared as the one it already was changes no byte of the
    /// entry, so the publish is the one an unmutated model produces.
    /// </summary>
    [Fact]
    public async Task ARegistryReDeclaredIdenticallyWhileTheManifestIsWrittenIsPublishedUnchanged()
    {
        var expected = await PublishOnceAsync(_ => { });

        var reDeclared = await PublishOnceAsync(documentDB => documentDB.WithEnvironment(
            _ => documentDB.WithImageRegistry(DocumentDBContainerImageTags.Registry)));

        Assert.Equal(
            $"{QualifiedOfficialImage}:{DocumentDBContainerImageTags.Tag}",
            expected["image"]?.ToString());
        Assert.Equal(expected.ToJsonString(), reDeclared.ToJsonString());

        static async Task<JsonNode> PublishOnceAsync(Action<IResourceBuilder<DocumentDBServerResource>> configure)
        {
            var appBuilder = CreateAppBuilder();
            var documentDB = appBuilder.AddDocumentDB("DocumentDB").WithDataVolume(name: "documentdb-data");
            configure(documentDB);

            using var app = appBuilder.Build();
            return await PublishManifestAsync(app, "DocumentDB");
        }
    }

    /// <summary>
    /// Negative control: a container build the caller owns publishes <c>build</c> and no
    /// <c>image</c> at all, so the composed reference is not what the entry carries and a registry
    /// set on the annotation beside the build falsifies nothing that was written. What protects
    /// such a resource is the build-definition snapshot, which records the whole <c>build</c>
    /// object — and which the other tests in this section drive.
    /// </summary>
    [Fact]
    public async Task ARegistryChangedOnADockerfileBuildWhileTheManifestIsWrittenIsPublishedUnchanged()
    {
        var expected = await PublishOnceAsync(_ => { });

        var mutated = await PublishOnceAsync(documentDB => documentDB.WithEnvironment(
            _ => documentDB.WithImageRegistry("mirror.example")));

        Assert.Null(expected["image"]);
        Assert.Equal("final", expected["build"]?["stage"]?.ToString());
        Assert.Equal(expected.ToJsonString(), mutated.ToJsonString());

        static async Task<JsonNode> PublishOnceAsync(Action<IResourceBuilder<DocumentDBServerResource>> configure)
        {
            var appBuilder = CreateAppBuilder();
            var documentDB = appBuilder.AddDocumentDB("DocumentDB");
            documentDB.WithAnnotation(new DockerfileBuildAnnotation("/src/context", "/src/context/Dockerfile", "final"));
            configure(documentDB);

            using var app = appBuilder.Build();
            return await PublishManifestAsync(app, "DocumentDB");
        }
    }

    [Fact]
    public async Task AnEntrypointChangedWhileTheManifestIsWrittenIsRefused()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithEnvironment(context =>
            {
                if (context.Resource is ContainerResource container)
                {
                    container.Entrypoint = "/bin/sh";
                }
            });

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PublishManifestAsync(app, "DocumentDB"));

        Assert.Contains("its container entrypoint changed", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The refusal has to be a refusal of the whole operation, not just of one entry: the fields
    /// already handed to the writer cannot be taken back, so the only safe outcome is a publish
    /// that fails and leaves no usable manifest.
    /// </summary>
    [Fact]
    public async Task APublishThatMutatesTheModelWhileWritingFailsClosed()
    {
        var log = await ManifestUtils.PublishManifestExpectingFailureAsync(appBuilder =>
            appBuilder.AddDocumentDB("DocumentDB")
                .WithEnvironment(context => context.Resource.Annotations.Add(
                    new ContainerMountAnnotation("late-data", "/data", ContainerMountType.Volume, isReadOnly: false))));

        Assert.Contains("while its manifest entry was being written", log, StringComparison.Ordinal);
    }

    /// <summary>
    /// Writing environment values is what environment callbacks are for, and a callback that only
    /// does that changes nothing structural.
    /// </summary>
    [Fact]
    public async Task EnvironmentValuesWrittenWhileTheManifestIsWrittenArePublishedUnchanged()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithDataVolume(name: "documentdb-data")
            .WithEnvironment(context => context.EnvironmentVariables["CONTOSO"] = "value");

        using var app = appBuilder.Build();

        var manifest = await PublishManifestAsync(app, "DocumentDB");

        Assert.Equal("value", manifest["env"]?["CONTOSO"]?.ToString());
        Assert.Equal("documentdb-data", manifest["volumes"]?[0]?["name"]?.ToString());
        Assert.Equal("/data", manifest["env"]?["DATA_PATH"]?.ToString());
    }

    /// <summary>
    /// Re-declaring the same storage, or merely reordering it, is a no-op: the mounts were written
    /// before the callback ran, so the entry is the one an unmutated model produces, byte for byte.
    /// </summary>
    [Fact]
    public async Task AMountReorderedOrReDeclaredIdenticallyWhileTheManifestIsWrittenIsPublishedUnchanged()
    {
        var expected = await PublishOnceAsync(_ => { });

        var reordered = await PublishOnceAsync(builder => builder.WithEnvironment(context =>
        {
            var mounts = context.Resource.Annotations.OfType<ContainerMountAnnotation>().ToList();
            foreach (var mount in mounts)
            {
                context.Resource.Annotations.Remove(mount);
            }

            foreach (var mount in Enumerable.Reverse(mounts))
            {
                context.Resource.Annotations.Add(mount);
            }
        }));

        var reDeclared = await PublishOnceAsync(builder => builder.WithEnvironment(context =>
        {
            var mount = context.Resource.Annotations.OfType<ContainerMountAnnotation>()
                .Single(annotation => annotation.Target == "/data");
            context.Resource.Annotations.Remove(mount);
            context.Resource.Annotations.Add(
                new ContainerMountAnnotation(mount.Source, mount.Target, mount.Type, mount.IsReadOnly));
        }));

        Assert.Equal(expected, reordered);
        Assert.Equal(expected, reDeclared);

        static async Task<string> PublishOnceAsync(Action<IResourceBuilder<DocumentDBServerResource>> configure)
        {
            var appBuilder = CreateAppBuilder();
            var documentDB = appBuilder.AddDocumentDB("DocumentDB")
                .WithDataVolume(name: "documentdb-data")
                .WithInitData("./seed");
            configure(documentDB);

            using var app = appBuilder.Build();
            return (await PublishManifestAsync(app, "DocumentDB")).ToString();
        }
    }

    /// <summary>
    /// Every build definition resolves to the same effective image — "this image is built from a
    /// Dockerfile the caller owns" — so the image alone cannot tell one from another. Aspire writes
    /// the whole <c>build</c> object before it evaluates a single environment callback, which is
    /// what makes swapping the definition from inside one invisible in the entry that was written.
    /// </summary>
    [Fact]
    public async Task ABuildDefinitionReplacedWhileTheManifestIsWrittenIsRefused()
    {
        var appBuilder = CreateAppBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB");
        documentDB.WithAnnotation(new DockerfileBuildAnnotation("/src/context", "/src/context/Dockerfile", "final"));
        documentDB.WithEnvironment(context =>
        {
            var build = context.Resource.Annotations.OfType<DockerfileBuildAnnotation>().Single();
            context.Resource.Annotations.Remove(build);
            context.Resource.Annotations.Add(
                new DockerfileBuildAnnotation("/elsewhere", "/elsewhere/Dockerfile", "release"));
        });

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PublishManifestAsync(app, "DocumentDB"));

        Assert.Contains("while its manifest entry was being written", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            "the container build definition it publishes was added, removed or replaced",
            exception.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Replacement is judged by identity even when the replacement carries the same values, because
    /// <see cref="DockerfileBuildAnnotation.DockerfileFactory"/> generates the Dockerfile's content
    /// and cannot be compared by value: "the same values" does not mean "the same build".
    /// </summary>
    [Fact]
    public async Task ABuildDefinitionReplacedByAnIdenticallyValuedOneIsStillRefused()
    {
        var appBuilder = CreateAppBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB");
        documentDB.WithAnnotation(new DockerfileBuildAnnotation("/src/context", "/src/context/Dockerfile", "final"));
        documentDB.WithEnvironment(context =>
        {
            var build = context.Resource.Annotations.OfType<DockerfileBuildAnnotation>().Single();
            context.Resource.Annotations.Remove(build);
            context.Resource.Annotations.Add(
                new DockerfileBuildAnnotation(build.ContextPath, build.DockerfilePath, build.Stage));
        });

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PublishManifestAsync(app, "DocumentDB"));

        Assert.Contains(
            "the container build definition it publishes was added, removed or replaced",
            exception.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The build arguments, the image name and tag, the entry-point flag and the generated
    /// <c>.dockerignore</c> are settable on the annotation that is already there, so replacing it
    /// is not the only way to change what the entry should have said.
    /// </summary>
    [Theory]
    [InlineData("argument")]
    [InlineData("image-name")]
    [InlineData("build-only")]
    public async Task ABuildDefinitionMutatedInPlaceWhileTheManifestIsWrittenIsRefused(string mutation)
    {
        var appBuilder = CreateAppBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB");
        var build = new DockerfileBuildAnnotation("/src/context", "/src/context/Dockerfile", stage: null);
        build.BuildArguments["ARG_ONE"] = "one";
        documentDB.WithAnnotation(build);

        documentDB.WithEnvironment(_ =>
        {
            switch (mutation)
            {
                case "argument":
                    build.BuildArguments["ARG_TWO"] = "two";
                    break;
                case "image-name":
                    build.ImageName = "contoso/documentdb-fork";
                    break;
                default:
                    build.HasEntrypoint = false;
                    break;
            }
        });

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PublishManifestAsync(app, "DocumentDB"));

        Assert.Contains(
            "the container build definition it publishes changed",
            exception.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A build secret changed during serialization is caught like any other part of the definition,
    /// and neither the value that was recorded nor the one that replaced it appears anywhere in the
    /// diagnostic.
    /// </summary>
    [Fact]
    public async Task ABuildSecretChangedWhileTheManifestIsWrittenIsRefusedWithoutNamingIt()
    {
        const string RecordedSecret = "recorded-build-secret-value";
        const string ReplacementSecret = "replacement-build-secret-value";

        var appBuilder = CreateAppBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB");
        var build = new DockerfileBuildAnnotation("/src/context", "/src/context/Dockerfile", stage: null);
        build.BuildSecrets["REGISTRY_TOKEN"] = RecordedSecret;
        documentDB.WithAnnotation(build);
        documentDB.WithEnvironment(_ => build.BuildSecrets["REGISTRY_TOKEN"] = ReplacementSecret);

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PublishManifestAsync(app, "DocumentDB"));

        Assert.Contains(
            "the container build definition it publishes changed",
            exception.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain(RecordedSecret, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ReplacementSecret, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("REGISTRY_TOKEN", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ABuildDefinitionAddedOrRemovedWhileTheManifestIsWrittenIsRefused(bool startsWithBuild)
    {
        var appBuilder = CreateAppBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB");

        if (startsWithBuild)
        {
            documentDB.WithAnnotation(new DockerfileBuildAnnotation("/src/context", "/src/context/Dockerfile", stage: null));
        }

        documentDB.WithEnvironment(context =>
        {
            if (context.Resource.Annotations.OfType<DockerfileBuildAnnotation>().SingleOrDefault() is { } build)
            {
                context.Resource.Annotations.Remove(build);
                return;
            }

            context.Resource.Annotations.Add(
                new DockerfileBuildAnnotation("/src/context", "/src/context/Dockerfile", stage: null));
        });

        using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PublishManifestAsync(app, "DocumentDB"));

        Assert.Contains("while its manifest entry was being written", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A build definition nothing touches is published exactly as it would be with no callback at
    /// all — including its arguments and secrets — and writing environment values alongside it is
    /// not a structural change.
    /// </summary>
    [Fact]
    public async Task ABuildDefinitionLeftAloneWhileTheManifestIsWrittenIsPublishedUnchanged()
    {
        var expected = await PublishOnceAsync(_ => { });
        var withEnvironmentValues = await PublishOnceAsync(builder =>
            builder.WithEnvironment(context => context.EnvironmentVariables["CONTOSO"] = "value"));

        Assert.Equal("final", expected["build"]?["stage"]?.ToString());
        Assert.Equal("one", expected["build"]?["args"]?["ARG_ONE"]?.ToString());
        Assert.Equal("env", expected["build"]?["secrets"]?["REGISTRY_TOKEN"]?["type"]?.ToString());
        Assert.Equal(expected["build"]?.ToJsonString(), withEnvironmentValues["build"]?.ToJsonString());
        Assert.Equal("value", withEnvironmentValues["env"]?["CONTOSO"]?.ToString());

        static async Task<JsonNode> PublishOnceAsync(Action<IResourceBuilder<DocumentDBServerResource>> configure)
        {
            var appBuilder = CreateAppBuilder();
            var documentDB = appBuilder.AddDocumentDB("DocumentDB").WithDataVolume(name: "documentdb-data");
            var build = new DockerfileBuildAnnotation("/src/context", "/src/context/Dockerfile", "final");
            build.BuildArguments["ARG_ONE"] = "one";
            build.BuildSecrets["REGISTRY_TOKEN"] = "recorded-build-secret-value";
            documentDB.WithAnnotation(build);
            configure(documentDB);

            using var app = appBuilder.Build();
            return await PublishManifestAsync(app, "DocumentDB");
        }
    }

    /// <summary>
    /// A resource taken out of the manifest has no published entry to check, and a caller's own
    /// writer still writes the entry it would have written.
    /// </summary>
    [Fact]
    public async Task ManifestExclusionsAndCustomWritersAreLeftAlone()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("excluded")
            .WithDataVolume()
            .ExcludeFromManifest();
        appBuilder.AddDocumentDB("custom")
            .WithDataVolume(name: "custom-data")
            .WithManifestPublishingCallback(context =>
            {
                context.Writer.WriteString("type", "custom.v0");
                return Task.CompletedTask;
            });

        using var app = appBuilder.Build();
        await PublishBeforeStartAsync(app);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        Assert.Null(await ManifestUtils.GetManifestOrNull(
            model.Resources.OfType<DocumentDBServerResource>().Single(r => r.Name == "excluded")));

        var custom = await ManifestUtils.GetManifest(
            model.Resources.OfType<DocumentDBServerResource>().Single(r => r.Name == "custom"));

        Assert.Equal("custom.v0", custom["type"]?.ToString());
        Assert.Null(custom["volumes"]);
    }

    // ---------------------------------------------------------------------
    // DATA_PATH is always stated
    // ---------------------------------------------------------------------

    /// <summary>
    /// The rules above are about <c>/data</c> whenever nothing else names a directory, so
    /// <c>/data</c> is what the container is told to use. Leaving it unset would let an image whose
    /// own default is somewhere else write to a directory the guard never looked at.
    /// </summary>
    [Fact]
    public async Task AnAbsentDataPathIsWrittenAsTheCanonicalDefault()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB");

        using var app = appBuilder.Build();

        Assert.Equal("/data", (await ConfigureResourceAsync(app, "DocumentDB"))["DATA_PATH"]);
    }

    [Fact]
    public async Task AnAbsentDataPathIsWrittenOnACustomImageToo()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB").WithImage("contoso/documentdb-fork", "pg17-0.116.0");

        using var app = appBuilder.Build();

        Assert.Equal("/data", (await ConfigureResourceAsync(app, "DocumentDB"))["DATA_PATH"]);
    }

    [Fact]
    public async Task AnAbsentDataPathIsWrittenIntoTheManifest()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB");

        using var app = appBuilder.Build();

        var manifest = await PublishManifestAsync(app, "DocumentDB");

        Assert.Equal("/data", manifest["env"]?["DATA_PATH"]?.ToString());
    }

    /// <summary>
    /// A callback that removes <c>DATA_PATH</c> after a storage helper set it does not leave the
    /// container to its image default either.
    /// </summary>
    [Fact]
    public async Task ADataPathRemovedByALaterCallbackFallsBackToTheCanonicalDefault()
    {
        var appBuilder = CreateAppBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithDataVolume()
            .WithEnvironment(context => context.EnvironmentVariables.Remove("DATA_PATH"));

        using var app = appBuilder.Build();

        Assert.Equal("/data", (await ConfigureResourceAsync(app, "DocumentDB"))["DATA_PATH"]);
    }

    /// <summary>
    /// An <see cref="IValueProvider"/> that records what asked it for its value, so a test can
    /// assert how many times — and with what context — the pipeline resolved it.
    /// </summary>
    private sealed class RecordingValueProvider(string value) : IValueProvider
    {
        private int _resolutionCount;

        public int ResolutionCount => Volatile.Read(ref _resolutionCount);

        public ValueProviderContext? LastContext { get; private set; }

        public ValueTask<string?> GetValueAsync(CancellationToken cancellationToken = default) =>
            GetValueAsync(new ValueProviderContext(), cancellationToken);

        public ValueTask<string?> GetValueAsync(ValueProviderContext context, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _resolutionCount);
            LastContext = context;
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
        // Given values so the real pipeline can resolve them; the assertions below are about the
        // unresolved objects the callbacks placed in the environment, which the values do not change.
        var userName = appBuilder.AddParameter("user", "documentdb-user", secret: false);
        var password = appBuilder.AddParameter("pass", "documentdb-password", secret: true);
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
    public async Task WithLogLevelAddsEnvironmentVariable(DocumentDBLogLevel logLevel, string expectedValue)
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithLogLevel(logLevel);

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());

        var env = await BuildEnvironmentVariablesAsync(containerResource);

        Assert.Equal(expectedValue, env["LOG_LEVEL"]);
    }

    [Fact]
    public async Task WithInitDataAddsReadOnlyBindMountAndDisablesSampleData()
    {
        var source = Path.GetFullPath(Path.Combine("TestData", "init"));

        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
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
        Assert.Equal("/init_doc_db.d", env["INIT_DATA_PATH"]);
        Assert.Equal("true", env["SKIP_INIT_DATA"]);
    }

    [Fact]
    public async Task WithoutSampleDataAddsEnvironmentVariable()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithoutSampleData();

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());

        var env = await BuildEnvironmentVariablesAsync(containerResource);
        Assert.Equal("true", env["SKIP_INIT_DATA"]);
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
    public async Task WithOpenTelemetryMetricsSetsEnabledEnvironmentVariable()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
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
    public async Task WithOpenTelemetryMetricsInjectsEnvironmentAuthoritativeConfigurationOnce()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag("pg17-0.116.0")
            .WithOpenTelemetryMetrics(endpoint: "http://first:4317")
            .WithOpenTelemetryMetrics(serviceName: "documentdb-local");

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());
        var configuration = Assert.Single(
            containerResource.Annotations
                .OfType<ContainerFileSystemCallbackAnnotation>()
                .Where(annotation =>
                    annotation.DestinationPath == "/home/documentdb/gateway/pg_documentdb_gw"));
        var entries = await InvokeContainerFileCallbackAsync(configuration, containerResource, app.Services);
        var file = Assert.IsType<ContainerFile>(Assert.Single(entries));

        var env = await BuildEnvironmentVariablesAsync(containerResource);
        Assert.False(env.ContainsKey("CONFIG_DIR"));
        Assert.Equal("SetupConfiguration.json", file.Name);
        Assert.Contains("\"BlockedRolePrefixes\"", file.Contents, StringComparison.Ordinal);
        Assert.DoesNotContain("\"TelemetryOptions\"", file.Contents, StringComparison.Ordinal);
        Assert.Equal(
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.GroupRead |
            UnixFileMode.OtherRead,
            file.Mode);
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsDoesNotReplaceCustomConfigDirectory()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag("pg17-0.116.0")
            .WithEnvironment("CONFIG_DIR", "/custom/documentdb/config")
            .WithOpenTelemetryMetrics();

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());
        var env = await BuildEnvironmentVariablesAsync(containerResource);

        Assert.Equal("/custom/documentdb/config", env["CONFIG_DIR"]);
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsAppliesCompatibilityConfigurationToPrivateMirrors()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageRegistry("registry.example.com")
            .WithImageTag("pg17-0.116.0")
            .WithOpenTelemetryMetrics();

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());
        var configuration = Assert.Single(
            containerResource.Annotations
                .OfType<ContainerFileSystemCallbackAnnotation>()
                .Where(annotation =>
                    annotation.DestinationPath == "/home/documentdb/gateway/pg_documentdb_gw"));
        var entries = await InvokeContainerFileCallbackAsync(configuration, containerResource, app.Services);
        Assert.IsType<ContainerFile>(Assert.Single(entries));
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsDoesNotInjectCompatibilityConfigurationFor0114()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag("pg17-0.114.0")
            .WithOpenTelemetryMetrics();

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());
        var configuration = Assert.Single(containerResource.Annotations.OfType<ContainerFileSystemCallbackAnnotation>());
        var entries = await InvokeContainerFileCallbackAsync(configuration, containerResource, app.Services);

        Assert.Empty(entries);
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsDoesNotInjectCompatibilityConfigurationForCustomImages()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImage("contoso/documentdb-local", "pg17-0.116.0")
            .WithOpenTelemetryMetrics();

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());
        var configuration = Assert.Single(containerResource.Annotations.OfType<ContainerFileSystemCallbackAnnotation>());
        var entries = await InvokeContainerFileCallbackAsync(configuration, containerResource, app.Services);

        Assert.Empty(entries);
    }

    /// <summary>
    /// The compatibility file is written to a path that is a property of the official image, and
    /// the publish rejection exists because that file cannot be shipped by every publisher. A
    /// Dockerfile build is neither: it keeps the stock behaviour and stays publishable.
    /// </summary>
    [Fact]
    public async Task WithOpenTelemetryMetricsDoesNotInjectCompatibilityConfigurationForDockerfileBuilds()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithDockerfile(CreateOfficialLookingDockerfileContext())
            .WithOpenTelemetryMetrics();

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());
        var configuration = Assert.Single(containerResource.Annotations.OfType<ContainerFileSystemCallbackAnnotation>());

        Assert.Empty(await InvokeContainerFileCallbackAsync(configuration, containerResource, app.Services));
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsAllowsPublishingADockerfileBuild()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithDockerfile(CreateOfficialLookingDockerfileContext())
            .WithOpenTelemetryMetrics();

        var manifest = await ManifestUtils.GetManifest(documentDB.Resource);

        Assert.Null(manifest["image"]);
        Assert.NotNull(manifest["build"]);
        Assert.Equal("true", manifest["env"]?["OTEL_METRICS_ENABLED"]?.GetValue<string>());
    }

    /// <summary>
    /// The compatibility file and the publish rejection follow the same classification, so the
    /// rearranged spelling of the official image gets both.
    /// </summary>
    [Fact]
    public async Task WithOpenTelemetryMetricsInjectsCompatibilityConfigurationForAQualifiedOfficialImage()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImage(QualifiedOfficialImage, InterlockedTag)
            .WithImageRegistry(null!)
            .WithOpenTelemetryMetrics();

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());
        var configuration = Assert.Single(containerResource.Annotations.OfType<ContainerFileSystemCallbackAnnotation>());

        var entry = Assert.Single(await InvokeContainerFileCallbackAsync(configuration, containerResource, app.Services));
        Assert.Equal("SetupConfiguration.json", Assert.IsType<ContainerFile>(entry).Name);
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsRejectsPublishingAQualifiedOfficialImage()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithImage(QualifiedOfficialImage, InterlockedTag)
            .WithImageRegistry(null!)
            .WithOpenTelemetryMetrics();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ManifestUtils.GetManifest(documentDB.Resource));

        Assert.Contains("SetupConfiguration.json", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "ghcr.io/evil/documentdb/documentdb-local")]
    [InlineData("ghcr.io/evil", "documentdb/documentdb-local")]
    [InlineData("contoso.azurecr.io/mirrors", "documentdb/documentdb-local")]
    public async Task WithOpenTelemetryMetricsLeavesAnExtraPathSegmentAlone(string? registry, string image)
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithImage(image, InterlockedTag)
            .WithImageRegistry(registry!)
            .WithOpenTelemetryMetrics();

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());
        var configuration = Assert.Single(containerResource.Annotations.OfType<ContainerFileSystemCallbackAnnotation>());

        Assert.Empty(await InvokeContainerFileCallbackAsync(configuration, containerResource, app.Services));

        // The publish rejection follows the same classification, so this stays publishable.
        Assert.NotNull(await ManifestUtils.GetManifest(documentDB.Resource));
    }

    /// <summary>
    /// Withholding the compatibility override on an image whose version cannot be determined is
    /// the conservative choice, but withholding it silently would leave a caller who asked for
    /// metrics with a container exporting none and nothing said about it.
    /// </summary>
    [Theory]
    [MemberData(nameof(DigestBehindACuratedTag))]
    public async Task WithOpenTelemetryMetricsWarnsOnceAboutADigestPinnedImage(
        string image,
        string? tag,
        string? sha256)
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = CreateAppBuilder(sink);
        var documentDB = appBuilder.AddDocumentDB("DocumentDB").WithOpenTelemetryMetrics();
        SetImageAnnotation(documentDB, image, tag, sha256);

        using var app = appBuilder.Build();

        var environment = await ConfigureResourceAsync(app, "DocumentDB", sink);

        var (_, _, message) = Assert.Single(sink.LogEntries.Where(e => e.Level == LogLevel.Warning));
        Assert.Contains("pins its container image by digest", message, StringComparison.Ordinal);
        Assert.Contains("digest supersedes the tag", message, StringComparison.Ordinal);
        Assert.Contains("cannot be determined", message, StringComparison.Ordinal);
        Assert.Contains("NOT applied", message, StringComparison.Ordinal);
        Assert.Contains("SetupConfiguration.json takes precedence over the OTEL_* environment variables", message, StringComparison.Ordinal);
        Assert.Contains("select the image by tag instead of by digest", message, StringComparison.Ordinal);
        Assert.Contains("configure telemetry inside the image", message, StringComparison.Ordinal);

        // Nothing about the pin itself is repeated: the resource name already identifies it.
        Assert.DoesNotContain(OlderReleaseDigest, message, StringComparison.Ordinal);

        // The environment half of the API still applies.
        Assert.Equal("true", environment["OTEL_METRICS_ENABLED"]);
    }

    /// <summary>
    /// The advisory is about the resource, so repeated calls do not repeat it.
    /// </summary>
    [Fact]
    public async Task WithOpenTelemetryMetricsWarnsOnlyOnceAcrossRepeatedCalls()
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = CreateAppBuilder(sink);
        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithOpenTelemetryMetrics(endpoint: "http://collector:4317")
            .WithOpenTelemetryMetrics(enabled: false);
        SetImageAnnotation(
            documentDB,
            $"{DocumentDBContainerImageTags.Image}:{InterlockedTag}",
            tag: null,
            sha256: OlderReleaseDigest);

        using var app = appBuilder.Build();

        await ConfigureResourceAsync(app, "DocumentDB", sink);

        Assert.Single(sink.LogEntries.Where(e => e.Level == LogLevel.Warning));
    }

    /// <summary>
    /// A digest pin is publishable on this branch — the override it would need is withheld, so
    /// there is nothing for a publisher to carry — and the advisory reaches publish mode too.
    /// </summary>
    [Fact]
    public async Task WithOpenTelemetryMetricsPublishesADigestPinnedImageAndWarns()
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = CreateAppBuilder(sink);
        var documentDB = appBuilder.AddDocumentDB("DocumentDB").WithOpenTelemetryMetrics();
        SetImageAnnotation(
            documentDB,
            $"{DocumentDBContainerImageTags.Image}:{InterlockedTag}",
            tag: null,
            sha256: OlderReleaseDigest);

        using var app = appBuilder.Build();

        var manifest = await PublishManifestAsync(app, "DocumentDB");

        Assert.Contains($"@sha256:{OlderReleaseDigest}", manifest["image"]?.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("true", manifest["env"]?["OTEL_METRICS_ENABLED"]?.GetValue<string>());

        var (_, category, message) = Assert.Single(sink.LogEntries.Where(e => e.Level == LogLevel.Warning));
        Assert.Equal("Aspire.Hosting.DocumentDB.WithOpenTelemetryMetrics", category);
        Assert.Contains("pins its container image by digest", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An image selected by tag answers the question, so there is nothing to advise about.
    /// </summary>
    [Theory]
    [InlineData(InterlockedTag)]
    [InlineData("pg17-0.114.0")]
    public async Task WithOpenTelemetryMetricsDoesNotWarnWhenTheImageIsSelectedByTag(string tag)
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = CreateAppBuilder(sink);
        appBuilder.AddDocumentDB("DocumentDB").WithImageTag(tag).WithOpenTelemetryMetrics();

        using var app = appBuilder.Build();

        await ConfigureResourceAsync(app, "DocumentDB", sink);

        Assert.DoesNotContain(sink.LogEntries, e => e.Level == LogLevel.Warning);
    }

    /// <summary>
    /// A digest on a repository this package does not publish was never going to get the override,
    /// so there is nothing withheld and nothing to report.
    /// </summary>
    [Fact]
    public async Task WithOpenTelemetryMetricsDoesNotWarnAboutADigestPinnedCustomImage()
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = CreateAppBuilder(sink);
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImage($"contoso/documentdb-local:{InterlockedTag}")
            .WithImageSHA256(OlderReleaseDigest)
            .WithOpenTelemetryMetrics();

        using var app = appBuilder.Build();

        await ConfigureResourceAsync(app, "DocumentDB", sink);

        Assert.DoesNotContain(sink.LogEntries, e => e.Level == LogLevel.Warning);
    }

    [Theory]
    [MemberData(nameof(DigestBehindACuratedTag))]
    public async Task WithOpenTelemetryMetricsLeavesADigestBehindACuratedTagAlone(
        string image,
        string? tag,
        string? sha256)
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB").WithOpenTelemetryMetrics();
        SetImageAnnotation(documentDB, image, tag, sha256);

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());
        var configuration = Assert.Single(containerResource.Annotations.OfType<ContainerFileSystemCallbackAnnotation>());

        // The override rewrites a file at a path known only for the release the tag names, which
        // is not what the digest resolves to, so it is withheld and publishing stays open.
        Assert.Empty(await InvokeContainerFileCallbackAsync(configuration, containerResource, app.Services));
        Assert.NotNull(await ManifestUtils.GetManifest(documentDB.Resource));
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsPreserves0116DefaultServiceName()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag("pg17-0.116.0")
            .WithOpenTelemetryMetrics();

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());
        var env = await BuildEnvironmentVariablesAsync(containerResource);

        Assert.Equal("documentdb_gateway", env["OTEL_SERVICE_NAME"]);
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsPreservesCallerProvided0116ServiceName()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag("pg17-0.116.0")
            .WithEnvironment("OTEL_SERVICE_NAME", "caller-service")
            .WithOpenTelemetryMetrics();

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());
        var env = await BuildEnvironmentVariablesAsync(containerResource);

        Assert.Equal("caller-service", env["OTEL_SERVICE_NAME"]);
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
    public async Task WithOpenTelemetryMetricsAddsAllEnvironmentVariablesInManifest()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
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
    public async Task WithOpenTelemetryMetricsRejects0116PublishMode()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag("pg17-0.116.0")
            .WithOpenTelemetryMetrics();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ManifestUtils.GetManifest(documentDB.Resource));

        Assert.Contains("v0.116.0", exception.Message, StringComparison.Ordinal);
        Assert.Contains("SetupConfiguration.json", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsAllows0116PublishModeWhenDisabled()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag("pg17-0.116.0")
            .WithOpenTelemetryMetrics()
            .WithOpenTelemetryMetrics(enabled: false);

        var manifest = await ManifestUtils.GetManifest(documentDB.Resource);

        Assert.Equal("false", manifest["env"]?["OTEL_METRICS_ENABLED"]?.GetValue<string>());

        using var app = appBuilder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());
        var configuration = Assert.Single(containerResource.Annotations.OfType<ContainerFileSystemCallbackAnnotation>());
        var entries = await InvokeContainerFileCallbackAsync(configuration, containerResource, app.Services);

        Assert.Empty(entries);
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsRejects0116PublishModeWhenFinalCallEnablesMetrics()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag("pg17-0.116.0")
            .WithOpenTelemetryMetrics(enabled: false)
            .WithOpenTelemetryMetrics();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ManifestUtils.GetManifest(documentDB.Resource));

        Assert.Contains("v0.116.0", exception.Message, StringComparison.Ordinal);
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
            .WithInitData(initDataPath)
            .WithTlsCertificate(certPath, keyPath)
            .WithTelemetry(enabled: false)
            .WithOwner("contoso")
            .WithoutExtendedRum()
            .WithoutUserCreation();
#pragma warning restore ASPIREDOCDB0001

        var manifest = await ManifestUtils.GetManifest(documentDB.Resource);

        Assert.Equal("debug", manifest["env"]?["LOG_LEVEL"]?.GetValue<string>());
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

    [Fact]
    public async Task WithPostgresEndpointGuardSkippedForDockerfileBuilds()
    {
        // The floor is a property of the published release. The tag on a Dockerfile build names
        // the build's starting point at best, so it is exempt on the same terms as a custom image
        // — with a warning that says the enforcement was skipped, not a hard failure keyed on a
        // tag that is not what runs.
        var sink = new CapturingLoggerSink();
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.Services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider(sink));

        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag("pg17-0.110.0")
            .WithDockerfile(CreateOfficialLookingDockerfileContext())
            .WithPostgresEndpoint();

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        // Must NOT throw, even though the annotated tag is < v0.112.0.
        await PublishBeforeResourceStartedAsync(app, resource, useEmptyServices: true);
        await PublishBeforeResourceStartedAsync(app, resource, useEmptyServices: true);

        var warnings = sink.LogEntries.Where(e => e.Level == LogLevel.Warning).ToList();
        Assert.Single(warnings);
        Assert.Contains("Dockerfile", warnings[0].Message, StringComparison.Ordinal);
        Assert.Contains("NOT enforced", warnings[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithPostgresEndpointGuardStillEnforcedWhenOnlyTheBaseImageIsOverridden()
    {
        // WithDockerfileBaseImage() selects base images for a *generated* Dockerfile. There is no
        // build here to generate one, so it changes nothing about the image this resource pulls
        // and must not be mistaken for a caller-owned build.
        var appBuilder = DistributedApplication.CreateBuilder();

#pragma warning disable ASPIREDOCKERFILEBUILDER001
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag("pg17-0.110.0")
            .WithDockerfileBaseImage($"{DocumentDBContainerImageTags.Registry}/{DocumentDBContainerImageTags.Image}:{InterlockedTag}")
            .WithPostgresEndpoint();
#pragma warning restore ASPIREDOCKERFILEBUILDER001

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PublishBeforeResourceStartedAsync(app, resource, useEmptyServices: true));

        Assert.Contains("requires DocumentDB", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PgVariantFloorIsNotEnforcedOnDockerfileBuilds()
    {
        // pg18-0.113.0 does not exist upstream, but a Dockerfile build never asks the registry
        // for it: the failure this floor converts into an actionable message cannot happen, so
        // the guard has nothing to say and — like the custom-image path — says it silently.
        var sink = new CapturingLoggerSink();
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.Services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider(sink));

        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag("pg18-0.113.0")
            .WithDockerfile(CreateOfficialLookingDockerfileContext());

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        await PublishBeforeResourceStartedAsync(app, resource, useEmptyServices: true);

        Assert.DoesNotContain(sink.LogEntries, e => e.Level == LogLevel.Warning);

        // Control: the same tag without the build is still a hard failure.
        var controlBuilder = DistributedApplication.CreateBuilder();
        controlBuilder.AddDocumentDB("DocumentDB").WithImageTag("pg18-0.113.0");

        using var controlApp = controlBuilder.Build();
        var controlResource = Assert.Single(controlApp.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => PublishBeforeResourceStartedAsync(controlApp, controlResource, useEmptyServices: true));
    }

    [Fact]
    public async Task WithPostgresEndpointGuardEnforcedForAQualifiedOfficialImage()
    {
        // The floor is a property of the published release, and this is the published release —
        // written with the registry inside the image annotation.
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImage(QualifiedOfficialImage, "pg17-0.110.0")
            .WithImageRegistry(null!)
            .WithPostgresEndpoint();

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PublishBeforeResourceStartedAsync(app, resource, useEmptyServices: true));

        Assert.Contains("requires DocumentDB", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithPostgresEndpointGuardSkippedForAnExtraPathSegment()
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.Services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider(sink));

        appBuilder.AddDocumentDB("DocumentDB")
            .WithImage("evil/documentdb/documentdb-local", "pg17-0.110.0")
            .WithImageRegistry(null!)
            .WithPostgresEndpoint();

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        await PublishBeforeResourceStartedAsync(app, resource, useEmptyServices: true);

        var warnings = sink.LogEntries.Where(e => e.Level == LogLevel.Warning).ToList();
        Assert.Single(warnings);
        Assert.Contains("custom image", warnings[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PgVariantFloorIsEnforcedForAQualifiedOfficialImage()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImage(QualifiedOfficialImage, "pg18-0.113.0")
            .WithImageRegistry(null!);

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PublishBeforeResourceStartedAsync(app, resource, useEmptyServices: true));

        Assert.Contains("only publishes pg18 images", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A Dockerfile build is decided before the reference is read, so no spelling of the image
    /// annotation brings the version-dependent behaviour back.
    /// </summary>
    [Fact]
    public async Task ADockerfileBuildStaysCustomEvenWithAQualifiedOfficialImage()
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.Services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider(sink));

        appBuilder.AddDocumentDB("DocumentDB")
            .WithImage(QualifiedOfficialImage, "pg17-0.110.0")
            .WithImageRegistry(null!)
            .WithDockerfile(CreateOfficialLookingDockerfileContext())
            .WithPostgresEndpoint();

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        await PublishBeforeResourceStartedAsync(app, resource, useEmptyServices: true);

        var warnings = sink.LogEntries.Where(e => e.Level == LogLevel.Warning).ToList();
        Assert.Single(warnings);
        Assert.Contains("Dockerfile", warnings[0].Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "ghcr.io/evil/documentdb/documentdb-local")]
    [InlineData("ghcr.io/evil", "documentdb/documentdb-local")]
    [InlineData(null, "contoso.azurecr.io/mirrors/documentdb/documentdb-local")]
    [InlineData("contoso.azurecr.io/mirrors", "documentdb/documentdb-local")]
    [InlineData(null, "harbor.corp.local/library/documentdb/documentdb-local")]
    [InlineData("harbor.corp.local/library", "documentdb/documentdb-local")]
    public async Task WithPostgresEndpointGuardSkippedForAnExtraPathSegmentInEitherField(
        string? registry,
        string image)
    {
        // The version behind a repository this package does not publish is unknown, so the floor
        // is not enforced against the tag -- in either spelling of the same reference.
        var sink = new CapturingLoggerSink();
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.Services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider(sink));

        appBuilder.AddDocumentDB("DocumentDB")
            .WithImage(image, "pg17-0.110.0")
            .WithImageRegistry(registry!)
            .WithPostgresEndpoint();

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        await PublishBeforeResourceStartedAsync(app, resource, useEmptyServices: true);

        var warnings = sink.LogEntries.Where(e => e.Level == LogLevel.Warning).ToList();
        Assert.Single(warnings);
        Assert.Contains("custom image", warnings[0].Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "ghcr.io/evil/documentdb/documentdb-local")]
    [InlineData("ghcr.io/evil", "documentdb/documentdb-local")]
    [InlineData("contoso.azurecr.io/mirrors", "documentdb/documentdb-local")]
    public async Task PgVariantFloorIsNotEnforcedForAnExtraPathSegment(string? registry, string image)
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.Services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider(sink));

        appBuilder.AddDocumentDB("DocumentDB")
            .WithImage(image, "pg18-0.113.0")
            .WithImageRegistry(registry!);

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        await PublishBeforeResourceStartedAsync(app, resource, useEmptyServices: true);

        Assert.DoesNotContain(sink.LogEntries, e => e.Level == LogLevel.Warning);
    }

    [Theory]
    [MemberData(nameof(DigestBehindACuratedTag))]
    public async Task WithPostgresEndpointGuardSkippedForADigestBehindACuratedTag(
        string image,
        string? tag,
        string? sha256)
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.Services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider(sink));

        var documentDB = appBuilder.AddDocumentDB("DocumentDB").WithPostgresEndpoint();
        SetImageAnnotation(documentDB, image, tag, sha256);

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        await PublishBeforeResourceStartedAsync(app, resource, useEmptyServices: true);

        var warnings = sink.LogEntries.Where(e => e.Level == LogLevel.Warning).ToList();
        Assert.Single(warnings);
        Assert.Contains("digest", warnings[0].Message, StringComparison.Ordinal);
        Assert.Contains(OlderReleaseDigest, warnings[0].Message, StringComparison.Ordinal);
        Assert.Contains("NOT enforced", warnings[0].Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The tag is not trusted in the other direction either: one that reads below the floor is
    /// not grounds for failing a resource whose digest may name a release above it.
    /// </summary>
    [Fact]
    public async Task WithPostgresEndpointGuardDoesNotFailOnATagSupersededByADigest()
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.Services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider(sink));

        var documentDB = appBuilder.AddDocumentDB("DocumentDB").WithPostgresEndpoint();
        SetImageAnnotation(
            documentDB,
            $"{DocumentDBContainerImageTags.Image}:pg17-0.110.0",
            tag: null,
            sha256: OlderReleaseDigest);

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        await PublishBeforeResourceStartedAsync(app, resource, useEmptyServices: true);

        Assert.Contains("digest", Assert.Single(sink.LogEntries.Where(e => e.Level == LogLevel.Warning)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PgVariantFloorIsNotEnforcedForADigestBehindACuratedTag()
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.Services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider(sink));

        var documentDB = appBuilder.AddDocumentDB("DocumentDB");
        SetImageAnnotation(
            documentDB,
            $"{DocumentDBContainerImageTags.Image}:pg18-0.113.0",
            tag: null,
            sha256: OlderReleaseDigest);

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        await PublishBeforeResourceStartedAsync(app, resource, useEmptyServices: true);

        Assert.DoesNotContain(sink.LogEntries, e => e.Level == LogLevel.Warning);
    }

    /// <summary>
    /// A Dockerfile build is decided before the reference is read at all, so a digest behind a
    /// curated tag changes nothing about it.
    /// </summary>
    [Fact]
    public async Task ADockerfileBuildWithADigestBehindACuratedTagStaysABuild()
    {
        var sink = new CapturingLoggerSink();
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.Services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider(sink));

        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithDockerfile(CreateOfficialLookingDockerfileContext())
            .WithPostgresEndpoint();
        SetImageAnnotation(
            documentDB,
            $"{DocumentDBContainerImageTags.Image}:{InterlockedTag}",
            tag: null,
            sha256: OlderReleaseDigest);

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        await PublishBeforeResourceStartedAsync(app, resource, useEmptyServices: true);

        Assert.Contains("Dockerfile", Assert.Single(sink.LogEntries.Where(e => e.Level == LogLevel.Warning)).Message, StringComparison.Ordinal);
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

    /// <summary>
    /// Runs the resource's real environment pipeline — the same
    /// <see cref="ExecutionConfigurationBuilder"/> the container creator uses — and returns the
    /// unprocessed values the callbacks produced.
    /// </summary>
    /// <remarks>
    /// Nothing here invokes a callback directly. Aspire evaluates each callback once per run and
    /// caches the result, so a test that drives this helper sees exactly what a container would,
    /// and a guard that participates in the pipeline cannot be tested against a value the
    /// container never receives.
    /// </remarks>
    private static async Task<Dictionary<string, object>> BuildEnvironmentVariablesAsync(
        DocumentDBServerResource resource,
        ILogger? logger = null,
        DistributedApplicationOperation operation = DistributedApplicationOperation.Run)
    {
        // Resolution failures are not this helper's business: it reports what the callbacks
        // produced, and a value that could not be resolved (a parameter with no value in a unit
        // test, say) is still a value they produced.
        var result = await BuildExecutionConfigurationAsync(resource, logger, operation, includeArguments: false, throwOnResolutionFailure: false);

        return result.EnvironmentVariablesWithUnprocessed.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.Unprocessed,
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Publishes <see cref="BeforeStartEvent"/> (which is what installs the storage guard's
    /// callbacks, exactly as a real start does) and then builds the named resource's environment
    /// and arguments through the real pipeline, returning the environment the container would be
    /// given.
    /// </summary>
    private static async Task<Dictionary<string, string>> ConfigureResourceAsync(
        DistributedApplication app,
        string resourceName,
        CapturingLoggerSink? sink = null,
        DistributedApplicationOperation operation = DistributedApplicationOperation.Run)
    {
        var result = await ConfigureResourceCoreAsync(app, resourceName, sink, operation);

        return result.EnvironmentVariables.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
    }

    /// <summary>
    /// The argument-pipeline counterpart of <see cref="ConfigureResourceAsync"/>.
    /// </summary>
    private static async Task<string[]> ConfigureArgumentsAsync(
        DistributedApplication app,
        string resourceName,
        CapturingLoggerSink? sink = null,
        DistributedApplicationOperation operation = DistributedApplicationOperation.Run)
    {
        var result = await ConfigureResourceCoreAsync(app, resourceName, sink, operation);

        return result.Arguments.Select(argument => argument.Value).ToArray();
    }

    private static async Task<IExecutionConfigurationResult> ConfigureResourceCoreAsync(
        DistributedApplication app,
        string resourceName,
        CapturingLoggerSink? sink,
        DistributedApplicationOperation operation)
    {
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        await PublishBeforeStartAsync(app);

        if (operation == DistributedApplicationOperation.Run)
        {
            // Run mode publishes this per resource just before the orchestrator builds the
            // container's environment and arguments; the guard uses it to take the last position
            // in both pipelines again.
            await PublishBeforeResourceStartedAsync(app, resourceName);
        }

        var resource = model.Resources.OfType<DocumentDBServerResource>().Single(r => r.Name == resourceName);
        var logger = sink is null ? null : new CapturingLoggerProvider(sink).CreateLogger(resourceName);

        return await BuildExecutionConfigurationAsync(resource, logger, operation, includeArguments: true, throwOnResolutionFailure: true);
    }

    /// <summary>Publishes the event that installs the storage guard's callbacks.</summary>
    private static Task PublishBeforeStartAsync(DistributedApplication app)
    {
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        return app.Services.GetRequiredService<IDistributedApplicationEventing>().PublishAsync(
            new BeforeStartEvent(app.Services, model),
            EventDispatchBehavior.BlockingSequential,
            CancellationToken.None);
    }

    /// <summary>
    /// Publishes the per-resource event the orchestrator publishes immediately before it builds a
    /// container's configuration, which is where the guard re-takes the last position.
    /// </summary>
    private static Task PublishBeforeResourceStartedAsync(DistributedApplication app, string resourceName)
    {
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource = model.Resources.OfType<DocumentDBServerResource>().Single(r => r.Name == resourceName);

        return app.Services.GetRequiredService<IDistributedApplicationEventing>().PublishAsync(
            new BeforeResourceStartedEvent(resource, app.Services),
            EventDispatchBehavior.BlockingSequential,
            CancellationToken.None);
    }

    /// <summary>
    /// Writes the resource's manifest entry through Aspire's real manifest writer, after the event
    /// that installs the guard — so the guard participates exactly as it does in <c>aspire
    /// publish</c>.
    /// </summary>
    private static async Task<JsonNode> PublishManifestAsync(DistributedApplication app, string resourceName)
    {
        await PublishBeforeStartAsync(app);

        var resource = app.Services.GetRequiredService<DistributedApplicationModel>()
            .Resources.OfType<DocumentDBServerResource>().Single(r => r.Name == resourceName);

        return await ManifestUtils.GetManifest(resource);
    }

    /// <summary>
    /// Writes the resource's manifest entry through Aspire's real writer expecting the checkpoint
    /// to refuse it, and returns the refusal together with the entry text the writer had already
    /// produced.
    /// </summary>
    private static async Task<(InvalidOperationException Failure, string Entry)> WriteManifestExpectingFailureAsync(
        DistributedApplication app,
        string resourceName)
    {
        await PublishBeforeStartAsync(app);

        var resource = app.Services.GetRequiredService<DistributedApplicationModel>()
            .Resources.OfType<DocumentDBServerResource>().Single(r => r.Name == resourceName);

        return await ManifestUtils.WriteResourceExpectingFailureAsync(resource);
    }

    private static async Task<IExecutionConfigurationResult> BuildExecutionConfigurationAsync(
        DocumentDBServerResource resource,
        ILogger? logger,
        DistributedApplicationOperation operation,
        bool includeArguments,
        bool throwOnResolutionFailure)
    {
        var executionContext = new DistributedApplicationExecutionContext(operation);

        // Aspire discovers a container's dependencies before it builds its configuration, and that
        // pass runs the same callbacks through the same one-shot cache with no logger attached. The
        // harness reproduces it, so a guard that wrote its warnings to the callback context's
        // logger would be seen to lose them here exactly as it does in a real run. It covers the
        // same pipelines the caller asked for, so an environment-only assertion is not made to
        // depend on the argument pipeline as well.
        var discovery = ExecutionConfigurationBuilder.Create(resource);
        if (includeArguments)
        {
            discovery = discovery.WithArgumentsConfig();
        }

        await discovery
            .WithEnvironmentVariablesConfig()
            .BuildAsync(executionContext, NullLogger.Instance, CancellationToken.None);

        var builder = ExecutionConfigurationBuilder.Create(resource);
        if (includeArguments)
        {
            builder = builder.WithArgumentsConfig();
        }

        var result = await builder
            .WithEnvironmentVariablesConfig()
            .BuildAsync(
                executionContext,
                logger ?? NullLogger.Instance,
                CancellationToken.None);

        // The container creator refuses to start a resource whose configuration failed to resolve;
        // surfacing the cause keeps a test from asserting against a half-built environment.
        if (throwOnResolutionFailure && result.Exception is not null)
        {
            throw result.Exception is AggregateException { InnerExceptions.Count: 1 } aggregate
                ? aggregate.InnerException!
                : result.Exception;
        }

        return result;
    }

    private static async Task<IReadOnlyList<ContainerFileSystemItem>> InvokeContainerFileCallbackAsync(
        ContainerFileSystemCallbackAnnotation annotation,
        DocumentDBServerResource resource,
        IServiceProvider services)
    {
        var entries = await annotation.Callback(
            new ContainerFileSystemCallbackContext
            {
                Model = resource,
                Services = services,
            },
            CancellationToken.None);

        return entries.ToList();
    }

    // A real digest of the published pg17-0.114.0 image, embedded as a literal so nothing here
    // depends on the registry or on a tag upstream can move. That release predates the /data
    // VOLUME declaration and the data-directory flock, which is what makes it the right thing to
    // find behind a "pg17-0.116.0" tag.
    private const string OlderReleaseDigest =
        "8c8a716e27f398b03c397424c4ddd901bddbc22b9f910b17096b0b246c7c9011";

    /// <summary>
    /// The three shapes in which one reference carries both a parseable curated tag and a digest,
    /// written as the (image, tag, sha256) they land on the annotation as.
    /// </summary>
    /// <remarks>
    /// All three publish a reference the runtime resolves by digest, so none of them runs the
    /// release the tag names.
    /// </remarks>
    public static TheoryData<string, string?, string?> DigestBehindACuratedTag => new()
    {
        { $"{DocumentDBContainerImageTags.Image}:{InterlockedTag}@sha256:{OlderReleaseDigest}", null, null },
        { $"{DocumentDBContainerImageTags.Image}:{InterlockedTag}", null, OlderReleaseDigest },
        { $"{DocumentDBContainerImageTags.Image}@sha256:{OlderReleaseDigest}", InterlockedTag, null },
    };

    /// <summary>
    /// Writes an image annotation spelled exactly as given, replacing the one AddDocumentDB
    /// installed.
    /// </summary>
    /// <remarks>
    /// Aspire's <c>WithImage</c> / <c>WithImageTag</c> / <c>WithImageSHA256</c> keep the
    /// annotation's tag and digest mutually exclusive and drop an inline tag when the same string
    /// also carries a digest, so a reference holding both is written on directly. It is still
    /// exactly what the manifest emits.
    /// </remarks>
    private static void SetImageAnnotation(
        IResourceBuilder<DocumentDBServerResource> builder,
        string image,
        string? tag,
        string? sha256)
    {
        foreach (var existing in builder.Resource.Annotations.OfType<ContainerImageAnnotation>().ToList())
        {
            builder.Resource.Annotations.Remove(existing);
        }

        var annotation = new ContainerImageAnnotation
        {
            Registry = DocumentDBContainerImageTags.Registry,
            Image = image,
        };

        if (tag is not null)
        {
            annotation.Tag = tag;
        }

        if (sha256 is not null)
        {
            annotation.SHA256 = sha256;
        }

        builder.Resource.Annotations.Add(annotation);
    }

    /// <summary>
    /// Creates a real Dockerfile build context: <c>WithDockerfile(...)</c> resolves and validates
    /// both paths eagerly.
    /// </summary>
    /// <remarks>
    /// The Dockerfile starts <c>FROM</c> the official image on purpose. Together with the image
    /// annotation <c>AddDocumentDB</c> installs — which Aspire keeps, and which a caller can
    /// re-point at the official repository and tag afterwards — it makes the resource look
    /// official from every angle the package can inspect, which is exactly the case these tests
    /// exist for.
    /// </remarks>
    private static string CreateOfficialLookingDockerfileContext(
        [CallerMemberName] string? testName = null)
    {
        var contextPath = Path.Combine(
            AppContext.BaseDirectory,
            "dockerfile-contexts",
            $"{testName ?? "context"}-{Guid.NewGuid():N}");

        Directory.CreateDirectory(contextPath);
        File.WriteAllText(
            Path.Combine(contextPath, "Dockerfile"),
            $"FROM {DocumentDBContainerImageTags.Registry}/{DocumentDBContainerImageTags.Image}:{InterlockedTag}\n" +
            "RUN echo caller-owned-layer\n");

        return contextPath;
    }
}
