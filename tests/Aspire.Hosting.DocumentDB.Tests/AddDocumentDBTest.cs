// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECONTAINERSHELLEXECUTION001

using System.Runtime.CompilerServices;
using System.Net.Sockets;
using System.Text.Json.Nodes;
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
    // The first curated tag whose gateway gives SetupConfiguration.json precedence over the
    // OTEL_* environment variables. Pinned explicitly so these tests describe that image's
    // behaviour regardless of which version the package currently defaults to.
    private const string InterlockedTag = "pg17-0.116.0";
    // The official image with the registry folded into the image annotation instead of kept
    // beside it. Aspire joins the two and never re-splits them, so this resolves to exactly the
    // reference the default spelling resolves to.
    private const string QualifiedOfficialImage =
        DocumentDBContainerImageTags.Registry + "/" + DocumentDBContainerImageTags.Image;

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

    [Fact]
    public async Task WithDataVolumeSupportsReadOnlyVolumes()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder
            .AddDocumentDB("DocumentDB")
            .WithDataVolume(isReadOnly: true);

        using var app = appBuilder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());
        var volumeAnnotation = Assert.Single(containerResource.Annotations.OfType<ContainerMountAnnotation>().Where(a => a.Type == ContainerMountType.Volume));
        Assert.Equal("/data", volumeAnnotation.Target);
        Assert.True(volumeAnnotation.IsReadOnly);

        var env = await BuildEnvironmentVariablesAsync(containerResource);
        var dataPath = Assert.Single(env.Where(entry => entry.Key == "DATA_PATH"));
        Assert.Equal("/data", dataPath.Value);
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
    public async Task WithDataBindMountSupportsReadOnlyBindMounts()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder
            .AddDocumentDB("DocumentDB")
            .WithDataBindMount("/host/data", isReadOnly: true);

        using var app = appBuilder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());
        var bindMountAnnotation = Assert.Single(containerResource.Annotations.OfType<ContainerMountAnnotation>().Where(a => a.Type == ContainerMountType.BindMount));
        Assert.Equal("/host/data", bindMountAnnotation.Source);
        Assert.Equal("/data", bindMountAnnotation.Target);
        Assert.True(bindMountAnnotation.IsReadOnly);

        var env = await BuildEnvironmentVariablesAsync(containerResource);
        var dataPath = Assert.Single(env.Where(entry => entry.Key == "DATA_PATH"));
        Assert.Equal("/data", dataPath.Value);
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
    public async Task WithOpenTelemetryMetricsWrapsTheEntrypointForGatewayTelemetryImages()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag("pg17-0.116.0")
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
            .WithImageTag("pg17-0.116.0")
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
    public async Task WithOpenTelemetryMetricsKeepsItsTemporaryConfigurationOutsideDataPath()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag("pg17-0.116.0")
            .WithEnvironment("DATA_PATH", "/tmp")
            .WithOpenTelemetryMetrics();

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);

        var script = await GetWrapperScriptAsync(SingleServerResource(app));

        Assert.Contains("d=\"$DATA_PATH\"", script, StringComparison.Ordinal);
        Assert.Contains("for a in \"$@\"", script, StringComparison.Ordinal);
        Assert.Contains("-d|--data-path) q=\"d\"", script, StringComparison.Ordinal);
        Assert.Contains("--username) q=\"v\"", script, StringComparison.Ordinal);
        Assert.Contains("realpath -m -- \"$d\"", script, StringComparison.Ordinal);
        Assert.Contains("if [ \"$d\" = \"/\" ]", script, StringComparison.Ordinal);
        Assert.Contains("for y in /tmp /var/tmp /dev/shm", script, StringComparison.Ordinal);
        Assert.Contains("realpath -m -- \"$y\"", script, StringComparison.Ordinal);
        Assert.Contains("case \"$x\" in \"$d\"|\"$d\"/*) continue;; esac", script, StringComparison.Ordinal);
        Assert.Contains("case \"$d\" in \"$x\"|\"$x\"/*) continue;; esac", script, StringComparison.Ordinal);
        Assert.Contains("mktemp -d \"$r/aspire-documentdb-otel.XXXXXX\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("o=\"$(mktemp -d)\"", script, StringComparison.Ordinal);

        // Nothing here can alias the data directory, so nothing about the backing storage is
        // emitted: this is the script every resource without mounts gets.
        Assert.DoesNotContain("$y|$d", script, StringComparison.Ordinal);
        Assert.Contains(
            "if [ -z \"$r\" ]; then echo \"aspire-documentdb -- no writable temporary directory is safely separated from DATA_PATH\" >&2; exit 1; fi",
            script,
            StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------
    // The scratch root and the storage that backs it
    //
    // Two container paths that do not contain one another can still be one
    // directory: a bind mount of the same host directory at /tmp and at /data,
    // or one named volume mounted twice. A scratch directory created through
    // the second window appears inside DATA_PATH, and DocumentDB 0.116.0
    // refuses to initialise a data directory that is not empty.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ATemporaryRootBoundToTheSameHostDirectoryAsDataPathIsNotUsed()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag("pg17-0.116.0")
            .WithDataBindMount("/srv/documentdb")
            .WithBindMount("/srv/documentdb", "/tmp")
            .WithOpenTelemetryMetrics();

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);

        var script = await GetWrapperScriptAsync(SingleServerResource(app));

        Assert.Contains("case \"$y|$d\" in ", script, StringComparison.Ordinal);
        Assert.Contains("\"/tmp|/data\"", script, StringComparison.Ordinal);
        Assert.Contains("\"/tmp|/data\"/*", script, StringComparison.Ordinal);

        // The other two candidates are in the container's own filesystem, so they are left alone
        // and one of them is what the wrapper falls back to.
        Assert.DoesNotContain("\"/var/tmp|", script, StringComparison.Ordinal);
        Assert.DoesNotContain("\"/dev/shm|", script, StringComparison.Ordinal);
        Assert.Contains("aliases DATA_PATH storage or cannot be proven independent", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATemporaryRootOnTheSameNamedVolumeAsDataPathIsNotUsed()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag("pg17-0.116.0")
            .WithDataVolume(name: "documentdb-data")
            .WithVolume("documentdb-data", "/tmp")
            .WithOpenTelemetryMetrics();

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);

        var script = await GetWrapperScriptAsync(SingleServerResource(app));

        Assert.Contains("\"/tmp|/data\"", script, StringComparison.Ordinal);
        Assert.Contains("\"/tmp|/data\"/*", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// A mount target that is an ancestor of the data directory's host directory backs it just as
    /// surely as one that is exactly it.
    /// </summary>
    [Fact]
    public async Task ATemporaryRootBoundToAnAncestorOfTheDataDirectoryIsNotUsed()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag("pg17-0.116.0")
            .WithDataBindMount("/srv/documentdb")
            .WithBindMount("/srv", "/tmp")
            .WithOpenTelemetryMetrics();

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);

        var script = await GetWrapperScriptAsync(SingleServerResource(app));

        Assert.Contains("\"/tmp|/data\"", script, StringComparison.Ordinal);
        Assert.Contains("\"/tmp|/data\"/*", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// DATA_PATH can be above the mount that exposes the candidate's physical alias. Writing
    /// through the candidate still creates an entry inside the data directory.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ATemporaryRootAliasedBelowDataPathRejectsTheContainingDirectory(bool useBindMounts)
    {
        var appBuilder = CreateLifecycleTestBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag("pg17-0.116.0")
            .WithEnvironment("DATA_PATH", "/data");

        if (useBindMounts)
        {
            documentDB
                .WithBindMount("/srv/documentdb/cluster", "/data/cluster")
                .WithBindMount("/srv/scratch", "/tmp");
        }
        else
        {
            documentDB
                .WithVolume("shared", "/data/cluster")
                .WithVolume("shared", "/tmp");
        }

        documentDB.WithOpenTelemetryMetrics();

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);

        var script = await GetWrapperScriptAsync(SingleServerResource(app));

        Assert.Contains("\"/tmp|/data\"", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other direction: the scratch root is a subdirectory of the data directory's host tree.
    /// Because both paths are bind-backed, every DATA_PATH under that bind target is conservatively
    /// incompatible; a daemon-side symbolic link could make even a sibling spelling an alias.
    /// </summary>
    [Fact]
    public async Task ATemporaryRootBoundBelowTheDataDirectoryAliasesOnlyTheDirectoriesThatContainIt()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag("pg17-0.116.0")
            .WithBindMount("/srv/documentdb", "/data")
            .WithBindMount("/srv/documentdb/scratch", "/tmp")
            .WithOpenTelemetryMetrics();

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);

        var script = await GetWrapperScriptAsync(SingleServerResource(app));

        // The whole bind-backed data region is rejected, which includes both '/data' and every
        // descendant such as '/data/scratch'.
        Assert.Contains("\"/tmp|/data\"", script, StringComparison.Ordinal);
        Assert.Contains("\"/tmp|/data\"/*", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two lexically different bind sources may resolve through daemon-side symbolic links to one
    /// directory. The model cannot prove otherwise without depending on the local filesystem, so a
    /// bind-backed candidate is conservatively skipped whenever DATA_PATH may also be bind-backed.
    /// </summary>
    [Fact]
    public async Task ATemporaryRootOnAnotherBindIsConservativelySkipped()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag("pg17-0.116.0")
            .WithDataBindMount("/srv/documentdb")
            .WithBindMount("/srv/scratch", "/tmp")
            .WithOpenTelemetryMetrics();

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);

        var script = await GetWrapperScriptAsync(SingleServerResource(app));

        Assert.Contains("\"/tmp|/data\"", script, StringComparison.Ordinal);
        Assert.Contains("\"/tmp|/data\"/*", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// A bind mount and a named volume are distinct storage types, so no daemon-side path alias can
    /// make them the same backing region.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DifferentStorageTypesLeaveTheTemporaryRootUsable(bool dataIsBind)
    {
        var appBuilder = CreateLifecycleTestBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag("pg17-0.116.0");

        if (dataIsBind)
        {
            documentDB
                .WithDataBindMount("/srv/documentdb")
                .WithVolume("scratch-volume", "/tmp");
        }
        else
        {
            documentDB
                .WithDataVolume("documentdb-data")
                .WithBindMount("/srv/scratch", "/tmp");
        }

        documentDB.WithOpenTelemetryMetrics();

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);

        var script = await GetWrapperScriptAsync(SingleServerResource(app));

        Assert.DoesNotContain("\"/tmp|/data\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DifferentNamedVolumesLeaveTheTemporaryRootUsable()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag("pg17-0.116.0")
            .WithDataVolume("documentdb-data")
            .WithVolume("scratch-volume", "/tmp")
            .WithOpenTelemetryMetrics();

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);

        var script = await GetWrapperScriptAsync(SingleServerResource(app));

        Assert.DoesNotContain("\"/tmp|/data\"", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// With every candidate on the data directory's storage there is nowhere safe left, and the
    /// wrapper says so rather than writing into the data directory.
    /// </summary>
    [Fact]
    public async Task EveryTemporaryRootAliasingTheDataDirectoryIsReportedByTheWrapper()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag("pg17-0.116.0")
            .WithDataBindMount("/srv/documentdb")
            .WithBindMount("/srv/documentdb", "/tmp")
            .WithBindMount("/srv/documentdb", "/var/tmp")
            .WithBindMount("/srv/documentdb", "/dev/shm")
            .WithOpenTelemetryMetrics();

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);

        var script = await GetWrapperScriptAsync(SingleServerResource(app));

        Assert.Contains("\"/tmp|/data\"", script, StringComparison.Ordinal);
        Assert.Contains("\"/var/tmp|/data\"", script, StringComparison.Ordinal);
        Assert.Contains("\"/dev/shm|/data\"", script, StringComparison.Ordinal);
        Assert.Contains(
            "if [ -n \"$w\" ]; then echo \"aspire-documentdb -- every temporary directory aliases DATA_PATH storage or cannot be proven independent of it, so the telemetry configuration cannot be kept out of it\" >&2; ",
            script,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Even three unrelated-looking bind source strings cannot prove physical independence from a
    /// bind-backed DATA_PATH on a remote daemon. Every candidate is therefore rejected.
    /// </summary>
    [Fact]
    public async Task EveryBindBackedTemporaryRootIsRejectedWhenDataPathIsBindBacked()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag("pg17-0.116.0")
            .WithDataBindMount("/srv/documentdb")
            .WithBindMount("/srv/scratch-one", "/tmp")
            .WithBindMount("/srv/scratch-two", "/var/tmp")
            .WithBindMount("/srv/scratch-three", "/dev/shm")
            .WithOpenTelemetryMetrics();

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);

        var script = await GetWrapperScriptAsync(SingleServerResource(app));

        Assert.Contains("\"/tmp|/data\"", script, StringComparison.Ordinal);
        Assert.Contains("\"/var/tmp|/data\"", script, StringComparison.Ordinal);
        Assert.Contains("\"/dev/shm|/data\"", script, StringComparison.Ordinal);
        Assert.Contains("cannot be proven independent", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// The wrapper is part of the container command, so the aliasing decision has to survive the
    /// manifest exactly as it survives a run.
    /// </summary>
    [Fact]
    public async Task TheScratchRootAliasingDecisionIsPublishedInTheManifest()
    {
        var manifest = await ManifestUtils.PublishManifestAsync(appBuilder =>
            appBuilder.AddDocumentDB("DocumentDB")
                .WithImageTag("pg17-0.116.0")
                .WithDataBindMount("/srv/documentdb")
                .WithBindMount("/srv/possibly-linked-scratch", "/tmp")
                .WithOpenTelemetryMetrics());

        var script = manifest["resources"]?["DocumentDB"]?["args"]?[1]?.ToString();

        Assert.NotNull(script);
        Assert.Contains("\"/tmp|/data\"", script, StringComparison.Ordinal);
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
            .WithImageTag("pg17-0.116.0")
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
            .WithImageTag("pg17-0.114.0")
            .WithOpenTelemetryMetrics();

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);

        var containerResource = SingleServerResource(app);
        Assert.Null(containerResource.Entrypoint);
        Assert.Empty(await BuildContainerArgsAsync(containerResource));
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsKeepsTheStockEntrypointForTheDefaultImage()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithOpenTelemetryMetrics();

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);

        var containerResource = SingleServerResource(app);
        Assert.Null(containerResource.Entrypoint);
        Assert.Empty(await BuildContainerArgsAsync(containerResource));
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

    /// <summary>
    /// The wrapper is built entirely out of official-image facts: the entrypoint script path, the
    /// packaged configuration layout, bash and jq. A resource built from the caller's Dockerfile
    /// keeps the official image annotation and even builds <c>FROM</c> the official image, but
    /// none of those facts has been established for the build output, so it keeps the stock
    /// entrypoint.
    /// </summary>
    [Fact]
    public async Task WithOpenTelemetryMetricsKeepsTheStockEntrypointForDockerfileBuilds()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithDockerfile(CreateOfficialLookingDockerfileContext())
            .WithOpenTelemetryMetrics();

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);

        var containerResource = SingleServerResource(app);
        Assert.Null(containerResource.Entrypoint);
        Assert.Empty(await BuildContainerArgsAsync(containerResource));

        // The environment half of the API is unconditional and still applies.
        var environment = await BuildEnvironmentVariablesAsync(containerResource);
        Assert.Equal("true", environment["OTEL_METRICS_ENABLED"]);
    }

    /// <summary>
    /// The same, for a caller who re-points the image annotation at the official repository and
    /// tag explicitly after adding the build — the shape in which an annotation is most
    /// convincingly official and least informative.
    /// </summary>
    [Fact]
    public async Task WithOpenTelemetryMetricsKeepsTheStockEntrypointWhenABuildIsAnnotatedAsOfficial()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithDockerfile(CreateOfficialLookingDockerfileContext())
            .WithImage(DocumentDBContainerImageTags.Image, InterlockedTag)
            .WithImageRegistry(DocumentDBContainerImageTags.Registry)
            .WithOpenTelemetryMetrics();

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);

        var containerResource = SingleServerResource(app);
        Assert.Null(containerResource.Entrypoint);
        Assert.Empty(await BuildContainerArgsAsync(containerResource));
    }

    /// <summary>
    /// A digest pin throws for the official image because the digest makes the version opaque and
    /// guessing is silently wrong either way. On a Dockerfile build there is nothing to guess: the
    /// digest is no more what runs than the tag is, so the resource is simply left alone.
    /// </summary>
    [Fact]
    public async Task WithOpenTelemetryMetricsAllowsADigestPinnedDockerfileBuild()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithImageSHA256("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")
            .WithDockerfile(CreateOfficialLookingDockerfileContext())
            .WithOpenTelemetryMetrics();

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);

        var containerResource = SingleServerResource(app);
        Assert.Null(containerResource.Entrypoint);
        Assert.Empty(await BuildContainerArgsAsync(containerResource));
    }

    /// <summary>
    /// <c>WithDockerfileBaseImage()</c> selects base images for a <em>generated</em> Dockerfile.
    /// With no build to generate one it changes nothing about the image this resource pulls, so it
    /// must not be mistaken for a caller-owned build and suppress the wrapper.
    /// </summary>
    [Fact]
    public async Task WithOpenTelemetryMetricsWrapsTheEntrypointWhenOnlyTheBaseImageIsOverridden()
    {
        var appBuilder = CreateLifecycleTestBuilder();

#pragma warning disable ASPIREDOCKERFILEBUILDER001
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithDockerfileBaseImage("contoso/documentdb-local:pg17-0.116.0")
            .WithOpenTelemetryMetrics();
#pragma warning restore ASPIREDOCKERFILEBUILDER001

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);

        var containerResource = SingleServerResource(app);
        Assert.Equal(GatewayConfigurationShell, containerResource.Entrypoint);
        Assert.Equal(3, (await BuildContainerArgsAsync(containerResource)).Count);
    }

    /// <summary>
    /// Once the wrapper owns the entrypoint it cannot be uninstalled, so a build introduced after
    /// it took over is reported rather than silently left with a <c>/bin/bash</c> that starts
    /// nothing.
    /// </summary>
    [Fact]
    public async Task WithOpenTelemetryMetricsRejectsADockerfileBuildAddedAfterTheWrapperWasInstalled()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithOpenTelemetryMetrics();

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);
        Assert.Equal(GatewayConfigurationShell, SingleServerResource(app).Entrypoint);

        documentDB.WithDockerfile(CreateOfficialLookingDockerfileContext());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => RaiseBeforeStartAsync(app));
        Assert.Contains("Dockerfile build", exception.Message, StringComparison.Ordinal);
        Assert.Contains("entrypoint", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The review reproduction: the official image with its registry folded into the image
    /// annotation resolves to exactly the reference the default spelling resolves to, so it needs
    /// the same wrapper.
    /// </summary>
    [Fact]
    public async Task WithOpenTelemetryMetricsWrapsTheEntrypointForAQualifiedOfficialImage()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImage(QualifiedOfficialImage, InterlockedTag)
            .WithImageRegistry(null!)
            .WithOpenTelemetryMetrics();

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);

        var containerResource = SingleServerResource(app);
        Assert.Equal(GatewayConfigurationShell, containerResource.Entrypoint);
        Assert.Equal(3, (await BuildContainerArgsAsync(containerResource)).Count);
    }

    [Theory]
    // A registry host and the exact repository beneath it, in every spelling of the boundary.
    [InlineData("contoso.azurecr.io/documentdb/documentdb-local", null)]
    [InlineData("documentdb/documentdb-local", "contoso.azurecr.io")]
    [InlineData("localhost:5000/documentdb/documentdb-local", null)]
    [InlineData("documentdb/documentdb-local", "localhost:5000")]
    [InlineData("[::1]:5000/documentdb/documentdb-local", null)]
    [InlineData("documentdb/documentdb/documentdb-local", "ghcr.io")]
    [InlineData("documentdb-local", "ghcr.io/documentdb/documentdb")]
    public async Task WithOpenTelemetryMetricsWrapsTheEntrypointForQualifiedPrivateMirrors(
        string image,
        string? registry)
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImage(image, InterlockedTag)
            .WithImageRegistry(registry!)
            .WithOpenTelemetryMetrics();

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);

        Assert.Equal(GatewayConfigurationShell, SingleServerResource(app).Entrypoint);
    }

    [Theory]
    // A namespace, project or mirror path in front of the repository is part of the repository,
    // whether the caller writes it inline or into the registry field.
    [InlineData("evil/documentdb/documentdb-local", null)]
    [InlineData("ghcr.io/evil/documentdb/documentdb-local", null)]
    [InlineData("documentdb/documentdb-local", "ghcr.io/evil")]
    [InlineData("contoso.azurecr.io/mirrors/documentdb/documentdb-local", null)]
    [InlineData("documentdb/documentdb-local", "contoso.azurecr.io/mirrors")]
    [InlineData("harbor.corp.local/library/documentdb/documentdb-local", null)]
    [InlineData("documentdb/documentdb-local", "harbor.corp.local/library")]
    // A bare name is a host only when it carries a port.
    [InlineData("documentdb/documentdb-local", "myregistry")]
    // Leaving the registry annotation in place composes a doubled, unresolvable reference.
    [InlineData("ghcr.io/documentdb/documentdb/documentdb-local", "ghcr.io/documentdb")]
    public async Task WithOpenTelemetryMetricsKeepsTheStockEntrypointForLookalikeReferences(
        string image,
        string? registry)
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImage(image, InterlockedTag)
            .WithImageRegistry(registry!)
            .WithOpenTelemetryMetrics();

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);

        var containerResource = SingleServerResource(app);
        Assert.Null(containerResource.Entrypoint);
        Assert.Empty(await BuildContainerArgsAsync(containerResource));
    }

    /// <summary>
    /// A digest pin makes the version opaque whichever way the reference is spelled, and whether
    /// the digest arrives through <c>WithImageSHA256</c> or inside the reference itself.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WithOpenTelemetryMetricsRejectsADigestPinnedQualifiedOfficialImage(bool digestInline)
    {
        const string Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        var appBuilder = CreateLifecycleTestBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB");

        if (digestInline)
        {
            documentDB.WithImage($"{QualifiedOfficialImage}@sha256:{Digest}");
        }
        else
        {
            documentDB.WithImage(QualifiedOfficialImage, InterlockedTag).WithImageSHA256(Digest);
        }

        documentDB.WithImageRegistry(null!).WithOpenTelemetryMetrics();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BuildAndRaiseBeforeStartAsync(appBuilder));

        Assert.Contains("digest", exception.Message, StringComparison.Ordinal);
        Assert.Contains(Digest, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The digest rejection has to survive a tag standing next to the digest: the repository is
    /// still the curated one, so this is the case the message exists for.
    /// </summary>
    [Theory]
    [MemberData(nameof(DigestBehindACuratedTag))]
    public async Task WithOpenTelemetryMetricsRejectsADigestBehindACuratedTag(
        string image,
        string? tag,
        string? sha256)
    {
        var appBuilder = CreateLifecycleTestBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB").WithOpenTelemetryMetrics();
        SetImageAnnotation(documentDB, image, tag, sha256);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BuildAndRaiseBeforeStartAsync(appBuilder));

        Assert.Contains("digest", exception.Message, StringComparison.Ordinal);
        Assert.Contains(OlderReleaseDigest, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A digest on a repository this package does not publish stays untouched, tag or no tag:
    /// there is nothing to reject, because the wrapper was never going to be applied.
    /// </summary>
    [Fact]
    public async Task WithOpenTelemetryMetricsAllowsADigestBehindACuratedTagOnACustomRepository()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImage($"contoso/documentdb-local:{InterlockedTag}")
            .WithImageSHA256(OlderReleaseDigest)
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

    // ---------------------------------------------------------------------
    // The wrapper has to be the last word on the command line
    //
    // Aspire evaluates command-line callbacks in annotation order over one
    // shared list, so a callback registered after WithOpenTelemetryMetrics()
    // used to run after the wrapper and could put anything in front of it.
    // /bin/bash reads its command from the first arguments, so one value
    // inserted at the front turns '-c <script> --' into operands and the
    // container starts nothing. The wrapper's callback retakes the last
    // position at every phase a run offers, and refuses to answer at all if
    // something got in after that.
    // ---------------------------------------------------------------------

    /// <summary>
    /// The reported case: a callback registered after <c>WithOpenTelemetryMetrics()</c> that
    /// prepends to the argument list. It now runs before the wrapper, so what it prepends becomes
    /// an argument of the image entrypoint - which is what it was asking for - instead of
    /// displacing the shell command.
    /// </summary>
    [Fact]
    public async Task WithOpenTelemetryMetricsKeepsTheWrapperFirstWhenALaterCallbackPrepends()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithOpenTelemetryMetrics()
            .WithArgs(context => context.Args.Insert(0, "--help"));

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);

        var args = await BuildContainerArgsAsync(SingleServerResource(app));

        Assert.Equal(GatewayConfigurationShell, SingleServerResource(app).Entrypoint);
        Assert.Equal(4, args.Count);
        Assert.Equal("-c", args[0]);
        Assert.Equal(GatewayConfigurationShellArgumentZero, args[2]);
        Assert.Equal("--help", args[3]);
    }

    /// <summary>
    /// A callback that rewrites the list wholesale - clearing it, or reordering what is already
    /// there - cannot take the wrapper apart either, because it runs before the wrapper is
    /// written.
    /// </summary>
    [Theory]
    [InlineData("clear")]
    [InlineData("reverse")]
    [InlineData("replace")]
    public async Task WithOpenTelemetryMetricsKeepsTheWrapperFirstWhenALaterCallbackRewritesTheList(string mutation)
    {
        var appBuilder = CreateLifecycleTestBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithArgs("--log-level", "trace")
            .WithOpenTelemetryMetrics();

        documentDB.WithArgs(context =>
        {
            switch (mutation)
            {
                case "clear":
                    context.Args.Clear();
                    break;
                case "reverse":
                    var reversed = context.Args.Reverse().ToList();
                    context.Args.Clear();
                    foreach (var argument in reversed)
                    {
                        context.Args.Add(argument);
                    }

                    break;
                default:
                    context.Args.Clear();
                    context.Args.Add("--owner");
                    context.Args.Add("documentdb");
                    break;
            }
        });

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);

        var args = await BuildContainerArgsAsync(SingleServerResource(app));

        Assert.Equal("-c", args[0]);
        Assert.Equal(GatewayConfigurationShellArgumentZero, args[2]);

        var expectedTail = mutation switch
        {
            "clear" => Array.Empty<string>(),
            "reverse" => ["trace", "--log-level"],
            _ => new[] { "--owner", "documentdb" },
        };

        Assert.Equal(expectedTail, args.Skip(3).Cast<string>());
    }

    /// <summary>
    /// The control: a callback that only appends is exactly what <c>WithArgs</c> is for, and its
    /// arguments still reach the image entrypoint in the order they were written.
    /// </summary>
    [Fact]
    public async Task WithOpenTelemetryMetricsKeepsAnAppendOnlyCallbackValid()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithOpenTelemetryMetrics()
            .WithArgs(context =>
            {
                context.Args.Add("--owner");
                context.Args.Add("documentdb");
            });

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);

        var args = await BuildContainerArgsAsync(SingleServerResource(app));

        Assert.Equal(5, args.Count);
        Assert.Equal("-c", args[0]);
        Assert.Equal(GatewayConfigurationShellArgumentZero, args[2]);
        Assert.Equal("--owner", args[3]);
        Assert.Equal("documentdb", args[4]);
    }

    /// <summary>
    /// A <see cref="BeforeStartEvent"/> subscriber registered after the DocumentDB APIs runs after
    /// the wrapper retakes the last position there. The per-resource phases a run publishes
    /// afterwards are what put the wrapper back in front of it.
    /// </summary>
    [Fact]
    public async Task WithOpenTelemetryMetricsKeepsTheWrapperFirstWhenALaterBeforeStartSubscriberPrepends()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithOpenTelemetryMetrics();

        appBuilder.Eventing.Subscribe<BeforeStartEvent>((_, _) =>
        {
            documentDB.WithArgs(context => context.Args.Insert(0, "--help"));
            return Task.CompletedTask;
        });

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);
        await RaiseResourceStartPhasesAsync(app);

        var args = await BuildContainerArgsAsync(SingleServerResource(app));

        Assert.Equal(4, args.Count);
        Assert.Equal("-c", args[0]);
        Assert.Equal(GatewayConfigurationShellArgumentZero, args[2]);
        Assert.Equal("--help", args[3]);
    }

    /// <summary>
    /// Lifecycle hooks run after <see cref="BeforeStartEvent"/> too, and are the documented way to
    /// mutate the model late. In a run the per-resource phases still come after them.
    /// </summary>
    [Fact]
    public async Task WithOpenTelemetryMetricsKeepsTheWrapperFirstWhenALifecycleHookPrepends()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithOpenTelemetryMetrics();

#pragma warning disable CS0618 // Type or member is obsolete
        appBuilder.Services.AddSingleton<IDistributedApplicationLifecycleHook>(
            new PrependArgumentLifecycleHook("DocumentDB", "--help"));
#pragma warning restore CS0618

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);
        await RunLifecycleHooksAsync(app);
        await RaiseResourceStartPhasesAsync(app);

        var args = await BuildContainerArgsAsync(SingleServerResource(app));

        Assert.Equal(4, args.Count);
        Assert.Equal("-c", args[0]);
        Assert.Equal(GatewayConfigurationShellArgumentZero, args[2]);
        Assert.Equal("--help", args[3]);
    }

    /// <summary>
    /// Nothing runs between <see cref="BeforeResourceStartedEvent"/> and the container's
    /// configuration except another subscriber to that same event. One registered after the
    /// DocumentDB APIs can still append a callback, and the wrapper would then be building a
    /// command line something else takes apart. It fails the resource instead.
    /// </summary>
    [Fact]
    public async Task WithOpenTelemetryMetricsFailsWhenACommandLineCallbackIsAddedAfterTheLastPhase()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithOpenTelemetryMetrics();

        appBuilder.Eventing.Subscribe<BeforeResourceStartedEvent>(documentDB.Resource, (_, _) =>
        {
            documentDB.WithArgs(context => context.Args.Insert(0, "--help"));
            return Task.CompletedTask;
        });

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);
        await RaiseResourceStartPhasesAsync(app);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BuildContainerArgsAsync(SingleServerResource(app)));

        Assert.Contains("has a later command-line callback registered after", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Publish publishes no per-resource event, but it does publish
    /// <see cref="Aspire.Hosting.Publishing.BeforePublishEvent"/> after every lifecycle hook and
    /// before the pipeline serializes anything. A hook that only adds arguments is therefore
    /// repaired rather than rejected: the guard retakes the last position there, so the hook's
    /// arguments land behind the wrapper where they belong.
    /// </summary>
    [Fact]
    public async Task WithOpenTelemetryMetricsPublishesArgumentsALifecycleHookAddedBehindTheWrapper()
    {
        var manifest = await ManifestUtils.PublishManifestAsync(appBuilder =>
        {
            appBuilder.AddDocumentDB("DocumentDB")
                .WithImageTag(InterlockedTag)
                .WithOpenTelemetryMetrics();

#pragma warning disable CS0618 // Type or member is obsolete
            appBuilder.Services.AddSingleton<IDistributedApplicationLifecycleHook>(
                new PrependArgumentLifecycleHook("DocumentDB", "--help"));
#pragma warning restore CS0618
        });

        var resource = manifest["resources"]?["DocumentDB"];
        Assert.NotNull(resource);
        Assert.Equal(GatewayConfigurationShell, resource!["entrypoint"]?.GetValue<string>());

        var args = resource["args"]?.AsArray();
        Assert.NotNull(args);
        Assert.Equal(4, args!.Count);
        Assert.Equal("-c", args[0]?.GetValue<string>());
        Assert.Equal(GatewayConfigurationShellArgumentZero, args[2]?.GetValue<string>());
        Assert.Equal("--help", args[3]?.GetValue<string>());
    }

    /// <summary>
    /// What publish cannot repair: a subscriber to the very event the guard uses to retake the last
    /// position, registered after the DocumentDB APIs so that it runs afterwards. Nothing is
    /// published after that, so the callback itself refuses.
    /// </summary>
    [Fact]
    public async Task WithOpenTelemetryMetricsFailsPublishWhenABeforePublishSubscriberAddsArguments()
    {
        var log = await ManifestUtils.PublishManifestExpectingFailureAsync(appBuilder =>
        {
            var documentDB = appBuilder.AddDocumentDB("DocumentDB")
                .WithImageTag(InterlockedTag)
                .WithOpenTelemetryMetrics();

            appBuilder.Eventing.Subscribe<Aspire.Hosting.Publishing.BeforePublishEvent>((_, _) =>
            {
                documentDB.WithArgs(context => context.Args.Insert(0, "--help"));
                return Task.CompletedTask;
            });
        });

        Assert.Contains("has a later command-line callback registered", log, StringComparison.Ordinal);
    }

    /// <summary>
    /// The failure has to describe the shape of the pipeline and nothing else: the callback that
    /// displaced the wrapper may well be the one carrying a secret.
    /// </summary>
    [Fact]
    public async Task WithOpenTelemetryMetricsFailClosedMessageDoesNotRepeatTheDisplacingValue()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithOpenTelemetryMetrics();

        appBuilder.Eventing.Subscribe<BeforeResourceStartedEvent>(documentDB.Resource, (_, _) =>
        {
            documentDB.WithArgs("--password", "hunter2-should-not-appear");
            return Task.CompletedTask;
        });

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);
        await RaiseResourceStartPhasesAsync(app);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BuildContainerArgsAsync(SingleServerResource(app)));

        Assert.DoesNotContain("hunter2", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Retaking the last position must not cost a second evaluation: the annotation is moved, not
    /// rebuilt, so Aspire's cached result for it - and therefore exactly one wrapper prefix -
    /// survives every phase and every later gather.
    /// </summary>
    [Fact]
    public async Task WithOpenTelemetryMetricsKeepsASingleEvaluationAcrossRepeatedPhases()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithOpenTelemetryMetrics()
            .WithOpenTelemetryMetrics(serviceName: "documentdb-local");

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);
        await RaiseResourceStartPhasesAsync(app);
        await RaiseResourceStartPhasesAsync(app);

        var resource = SingleServerResource(app);
        var first = await BuildContainerArgsAsync(resource);
        var second = await BuildContainerArgsAsync(resource);

        Assert.Equal(3, first.Count);
        Assert.Equal(first, second);
        Assert.Same(first[1], second[1]);
    }

    /// <summary>
    /// A restart re-evaluates the callbacks: Aspire forgets each annotation's cached result and
    /// republishes the per-resource phases before it recreates the container. The wrapper has to
    /// come out of that as one prefix, not two. The same evaluation is what an explicitly started
    /// resource gets, whose configuration is built after those phases rather than at startup.
    /// </summary>
    [Fact]
    public async Task WithOpenTelemetryMetricsRebuildsExactlyOneWrapperPrefixOnRestart()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithOpenTelemetryMetrics()
            .WithArgs("--log-level", "trace");

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);
        await RaiseResourceStartPhasesAsync(app);

        var resource = SingleServerResource(app);
        var first = await BuildContainerArgsAsync(resource);

        ForgetCachedCommandLineResults(resource);
        await RaiseResourceStartPhasesAsync(app);

        var second = await BuildContainerArgsAsync(resource);

        Assert.Equal(5, first.Count);
        Assert.Equal(5, second.Count);
        Assert.Equal("-c", second[0]);
        Assert.Equal(GatewayConfigurationShellArgumentZero, second[2]);
        Assert.Equal("--log-level", second[3]);
        Assert.Equal("trace", second[4]);
    }

    /// <summary>
    /// Drops every command-line callback's cached result, which is what Aspire's DCP executor does
    /// before it recreates a container. The cache is an explicit implementation of an internal
    /// interface, so it is reached by reflection; a failure here means the restart contract this
    /// wrapper relies on has changed shape.
    /// </summary>
    private static void ForgetCachedCommandLineResults(DocumentDBServerResource resource)
    {
        foreach (var annotation in resource.Annotations.OfType<CommandLineArgsCallbackAnnotation>())
        {
            var callbackInterface = annotation.GetType().GetInterfaces().Single(
                candidate => candidate.IsGenericType &&
                    candidate.Name.StartsWith("ICallbackResourceAnnotation", StringComparison.Ordinal));

            var forget = callbackInterface.GetMethod("ForgetCachedResult");
            Assert.NotNull(forget);
            forget!.Invoke(annotation, null);
        }
    }

    /// <summary>
    /// The real publisher, end to end: what azd receives has to carry the wrapper prefix first,
    /// whatever the caller's own argument callbacks do to the list.
    /// </summary>
    [Fact]
    public async Task WithOpenTelemetryMetricsPublishesTheWrapperFirstWhenALaterCallbackPrepends()
    {
        var manifest = await ManifestUtils.PublishManifestAsync(appBuilder =>
            appBuilder.AddDocumentDB("DocumentDB")
                .WithImageTag(InterlockedTag)
                .WithOpenTelemetryMetrics()
                .WithArgs(context => context.Args.Insert(0, "--help")));

        var resource = manifest["resources"]?["DocumentDB"];
        Assert.NotNull(resource);
        Assert.Equal(GatewayConfigurationShell, resource!["entrypoint"]?.GetValue<string>());

        var args = resource["args"]?.AsArray();
        Assert.NotNull(args);
        Assert.Equal(4, args!.Count);
        Assert.Equal("-c", args[0]?.GetValue<string>());
        Assert.Equal(GatewayConfigurationShellArgumentZero, args[2]?.GetValue<string>());
        Assert.Equal("--help", args[3]?.GetValue<string>());

        var script = args[1]?.GetValue<string>();
        Assert.NotNull(script);
        Assert.DoesNotContain("\n", script!, StringComparison.Ordinal);
        Assert.DoesNotContain("{", script!, StringComparison.Ordinal);
        Assert.Contains($"exec {GatewayEntrypointScriptPath} \"$@\"", script!, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------
    // Being last is not enough on its own
    //
    // Aspire records each callback's result the first time it runs and reuses
    // it for the rest of the run. Anything that builds the resource's
    // configuration early - ExecutionConfigurationBuilder from a lifecycle
    // hook, or the dependency discovery a container creation does before it
    // builds its spec - freezes the wrapper's answer. A change made after that
    // is never re-validated, and because the gatherer takes the *last*
    // annotation's recorded result as the whole argument list, a callback
    // appended afterwards does not reorder the command line, it replaces it.
    //
    // The seal is compared at the two phases Aspire never caches: the
    // container-runtime-arguments callback in a run, and BeforePublishEvent in
    // a publish.
    // ---------------------------------------------------------------------

    /// <summary>
    /// The reported bypass: a lifecycle hook builds the resource's configuration through the
    /// public <see cref="ExecutionConfigurationBuilder"/> — which is what Aspire itself uses — and
    /// only then appends an argument callback. Publish has no per-resource phase, so nothing would
    /// re-run; the manifest would carry that callback's list and nothing else.
    /// </summary>
    [Fact]
    public async Task WithOpenTelemetryMetricsFailsPublishWhenAHookGathersArgumentsThenAppends()
    {
        var log = await ManifestUtils.PublishManifestExpectingFailureAsync(appBuilder =>
        {
            appBuilder.AddDocumentDB("DocumentDB")
                .WithImageTag(InterlockedTag)
                .WithOpenTelemetryMetrics();

#pragma warning disable CS0618 // Type or member is obsolete
            appBuilder.Services.AddSingleton<IDistributedApplicationLifecycleHook>(
                new GatherThenMutateLifecycleHook(
                    "DocumentDB",
                    resource => resource.Annotations.Add(
                        new CommandLineArgsCallbackAnnotation(args => args.Insert(0, "--help")))));
#pragma warning restore CS0618
        });

        Assert.Contains("was changed after its container command line had already been built", log, StringComparison.Ordinal);
        Assert.Contains("a command-line callback was added or removed", log, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same hook, re-pointing the entrypoint instead. The manifest writes <c>entrypoint</c>
    /// before it evaluates <c>args</c>, so nothing downstream would notice.
    /// </summary>
    [Fact]
    public async Task WithOpenTelemetryMetricsFailsPublishWhenAHookGathersArgumentsThenChangesTheEntrypoint()
    {
        var log = await ManifestUtils.PublishManifestExpectingFailureAsync(appBuilder =>
        {
            appBuilder.AddDocumentDB("DocumentDB")
                .WithImageTag(InterlockedTag)
                .WithOpenTelemetryMetrics();

#pragma warning disable CS0618 // Type or member is obsolete
            appBuilder.Services.AddSingleton<IDistributedApplicationLifecycleHook>(
                new GatherThenMutateLifecycleHook(
                    "DocumentDB",
                    resource => resource.Entrypoint = "/late/entrypoint.sh"));
#pragma warning restore CS0618
        });

        Assert.Contains("its container entrypoint changed", log, StringComparison.Ordinal);
    }

    /// <summary>
    /// And swapping the image: the wrapper is built out of facts about the official image, so an
    /// image selected after the command line was decided is a different container.
    /// </summary>
    [Fact]
    public async Task WithOpenTelemetryMetricsFailsPublishWhenAHookGathersArgumentsThenChangesTheImage()
    {
        var log = await ManifestUtils.PublishManifestExpectingFailureAsync(appBuilder =>
        {
            appBuilder.AddDocumentDB("DocumentDB")
                .WithImageTag(InterlockedTag)
                .WithOpenTelemetryMetrics();

#pragma warning disable CS0618 // Type or member is obsolete
            appBuilder.Services.AddSingleton<IDistributedApplicationLifecycleHook>(
                new GatherThenMutateLifecycleHook(
                    "DocumentDB",
                    resource => resource.Annotations.OfType<ContainerImageAnnotation>().Last().Tag = "pg17-0.114.0"));
#pragma warning restore CS0618
        });

        Assert.Contains("the image it will run changed", log, StringComparison.Ordinal);
    }

    /// <summary>
    /// The run-mode half. A container creation discovers the resource's dependencies first — which
    /// builds and records its configuration — and only then invokes the container-runtime-argument
    /// callbacks, before it reads the container's command. A caller callback there can re-point the
    /// entrypoint after every argument check has already run.
    /// </summary>
    [Fact]
    public async Task WithOpenTelemetryMetricsFailsWhenARuntimeCallbackChangesTheEntrypointAfterTheGather()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithOpenTelemetryMetrics();

        documentDB.WithContainerRuntimeArgs(context =>
        {
            documentDB.Resource.Entrypoint = "/late/entrypoint.sh";
            context.Args.Add("--label");
            context.Args.Add("late=1");
        });

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);
        await RaiseResourceStartPhasesAsync(app);

        var resource = SingleServerResource(app);

        // What Aspire does first: the dependency pass builds and records the configuration.
        Assert.Equal(3, (await BuildContainerArgsAsync(resource)).Count);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunContainerRuntimeArgsAsync(resource));

        Assert.Contains("its container entrypoint changed", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A runtime-argument callback that appends another argument callback is the run-mode shape of
    /// the publish bypass.
    /// </summary>
    [Fact]
    public async Task WithOpenTelemetryMetricsFailsWhenARuntimeCallbackAppendsArgumentsAfterTheGather()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithOpenTelemetryMetrics();

        documentDB.WithContainerRuntimeArgs(_ =>
            documentDB.Resource.Annotations.Add(
                new CommandLineArgsCallbackAnnotation(args => args.Insert(0, "--help"))));

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);
        await RaiseResourceStartPhasesAsync(app);

        var resource = SingleServerResource(app);
        Assert.Equal(3, (await BuildContainerArgsAsync(resource)).Count);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunContainerRuntimeArgsAsync(resource));

        Assert.Contains("has a later command-line callback registered", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Environment callbacks can re-point the entrypoint too. Aspire's dependency pass evaluates
    /// them before the argument callbacks and records both, so the wrapper's own callback is what
    /// sees the result — and it refuses to build arguments for an entrypoint it does not own.
    /// </summary>
    [Fact]
    public async Task WithOpenTelemetryMetricsFailsWhenAnEnvironmentCallbackChangesTheEntrypoint()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithOpenTelemetryMetrics();

        documentDB.WithEnvironment(context => documentDB.Resource.Entrypoint = "/late/entrypoint.sh");

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);
        await RaiseResourceStartPhasesAsync(app);

        var resource = SingleServerResource(app);

        // The order Aspire's dependency pass uses: environment first, then arguments.
        await BuildEnvironmentVariablesAsync(resource);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BuildContainerArgsAsync(resource));

        Assert.Contains("/late/entrypoint.sh", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// And when the entrypoint moves after the arguments were already recorded, the run's
    /// unconditional checkpoint is what catches it.
    /// </summary>
    [Fact]
    public async Task WithOpenTelemetryMetricsFailsWhenTheEntrypointMovesAfterTheArgumentsWereRecorded()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithOpenTelemetryMetrics();

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);
        await RaiseResourceStartPhasesAsync(app);

        var resource = SingleServerResource(app);
        Assert.Equal(3, (await BuildContainerArgsAsync(resource)).Count);

        resource.Entrypoint = "/late/entrypoint.sh";

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunContainerRuntimeArgsAsync(resource));

        Assert.Contains("its container entrypoint changed", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The guard moves its own callbacks to the end of their pipelines at every phase, so a
    /// recording taken before one of those moves must not be read as somebody else changing the
    /// resource. A hook that adds a container-runtime argument and only then reads the
    /// configuration produces exactly that order, and has to keep working.
    /// </summary>
    [Fact]
    public async Task WithOpenTelemetryMetricsToleratesTheGuardRetakingItsOwnPositionAfterAGather()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithOpenTelemetryMetrics();

        using var app = appBuilder.Build();

        // A lifecycle hook's shape: add a runtime argument and an environment variable, then read
        // the configuration, all before the per-resource phases run.
        await RaiseBeforeStartAsync(app);
        documentDB.WithContainerRuntimeArgs("--label", "team=data");
        documentDB.WithEnvironment("TEAM", "data");

        var resource = SingleServerResource(app);
        Assert.Equal(3, (await BuildContainerArgsAsync(resource)).Count);

        // The phases that move the guard back behind both of those.
        await RaiseResourceStartPhasesAsync(app);

        Assert.Equal(["--label", "team=data"], await RunContainerRuntimeArgsAsync(resource));
        Assert.Equal(3, (await BuildContainerArgsAsync(resource)).Count);
    }

    /// <summary>
    /// The control: a lifecycle hook that reads the configuration and changes nothing still
    /// publishes, and publishes the same command line it read.
    /// </summary>
    [Fact]
    public async Task WithOpenTelemetryMetricsPublishesWhenAHookOnlyReadsTheConfiguration()
    {
        var manifest = await ManifestUtils.PublishManifestAsync(appBuilder =>
        {
            appBuilder.AddDocumentDB("DocumentDB")
                .WithImageTag(InterlockedTag)
                .WithOpenTelemetryMetrics()
                .WithArgs("--log-level", "trace");

#pragma warning disable CS0618 // Type or member is obsolete
            appBuilder.Services.AddSingleton<IDistributedApplicationLifecycleHook>(
                new GatherThenMutateLifecycleHook("DocumentDB", _ => { }));
#pragma warning restore CS0618
        });

        var resource = manifest["resources"]?["DocumentDB"];
        Assert.NotNull(resource);
        Assert.Equal(GatewayConfigurationShell, resource!["entrypoint"]?.GetValue<string>());

        var args = resource["args"]?.AsArray();
        Assert.NotNull(args);
        Assert.Equal(5, args!.Count);
        Assert.Equal("-c", args[0]?.GetValue<string>());
        Assert.Equal(GatewayConfigurationShellArgumentZero, args[2]?.GetValue<string>());
        Assert.Equal("--log-level", args[3]?.GetValue<string>());
        Assert.Equal("trace", args[4]?.GetValue<string>());
    }

    /// <summary>
    /// A subscriber registered after the DocumentDB APIs runs after their
    /// <c>BeforePublishEvent</c> verification. Gathering there freezes the validated wrapper;
    /// changing the entrypoint afterwards must still be rejected by the checkpoint that runs
    /// while the real publisher serializes the resource.
    /// </summary>
    [Fact]
    public async Task WithOpenTelemetryMetricsFailsRealPublicationWhenALateSubscriberChangesTheEntrypointAfterGathering()
    {
        var log = await ManifestUtils.PublishManifestExpectingFailureAsync(appBuilder =>
        {
            var documentDB = appBuilder.AddDocumentDB("DocumentDB")
                .WithImageTag(InterlockedTag)
                .WithOpenTelemetryMetrics();

            appBuilder.Eventing.Subscribe<Publishing.BeforePublishEvent>(async (_, token) =>
            {
                await GatherPublishConfigurationAsync(documentDB.Resource, token);
                documentDB.Resource.Entrypoint = "/late/entrypoint.sh";
            });
        });

        Assert.Contains("its container entrypoint changed", log, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same late gather followed by a command callback changes which immutable argument list
    /// Aspire would publish. Serialization must reject the changed callback membership rather than
    /// trusting the wrapper result cached before the callback existed.
    /// </summary>
    [Fact]
    public async Task WithOpenTelemetryMetricsFailsRealPublicationWhenALateSubscriberAddsArgumentsAfterGathering()
    {
        var log = await ManifestUtils.PublishManifestExpectingFailureAsync(appBuilder =>
        {
            var documentDB = appBuilder.AddDocumentDB("DocumentDB")
                .WithImageTag(InterlockedTag)
                .WithOpenTelemetryMetrics();

            appBuilder.Eventing.Subscribe<Publishing.BeforePublishEvent>(async (_, token) =>
            {
                await GatherPublishConfigurationAsync(documentDB.Resource, token);
                documentDB.WithArgs("--late");
            });
        });

        Assert.Contains("has a later command-line callback registered", log, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every part of the effective image is part of the seal. These mutations use the public
    /// builder APIs after the cached wrapper has been gathered, including the Dockerfile
    /// classification that makes an otherwise official-looking annotation caller-owned.
    /// </summary>
    [Theory]
    [InlineData("image")]
    [InlineData("tag")]
    [InlineData("digest")]
    [InlineData("dockerfile")]
    public async Task WithOpenTelemetryMetricsFailsRealPublicationWhenALateSubscriberChangesTheImageAfterGathering(
        string mutation)
    {
        var log = await ManifestUtils.PublishManifestExpectingFailureAsync(appBuilder =>
        {
            var documentDB = appBuilder.AddDocumentDB("DocumentDB")
                .WithImageTag(InterlockedTag)
                .WithOpenTelemetryMetrics();

            appBuilder.Eventing.Subscribe<Publishing.BeforePublishEvent>(async (_, token) =>
            {
                await GatherPublishConfigurationAsync(documentDB.Resource, token);

                switch (mutation)
                {
                    case "image":
                        documentDB.WithImage("contoso/documentdb-local", InterlockedTag);
                        break;
                    case "tag":
                        documentDB.WithImageTag("pg17-0.114.0");
                        break;
                    case "digest":
                        documentDB.WithImageSHA256(OlderReleaseDigest);
                        break;
                    case "dockerfile":
                        documentDB.WithDockerfile(CreateOfficialLookingDockerfileContext());
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown mutation '{mutation}'.");
                }
            });
        });

        Assert.Contains("the image it will run changed", log, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reading the cached configuration is legal. The serialization checkpoint must be invisible
    /// when the model still matches the seal, including the exact wrapper command and manifest
    /// fields the ordinary container writer emits.
    /// </summary>
    [Fact]
    public async Task WithOpenTelemetryMetricsPublishesAnIdenticalManifestWhenALateSubscriberOnlyGathers()
    {
        var baseline = await ManifestUtils.PublishManifestAsync(
            appBuilder =>
                appBuilder.AddDocumentDB("DocumentDB")
                    .WithImageTag(InterlockedTag)
                    .WithOpenTelemetryMetrics()
                    .WithArgs("--log-level", "trace"),
            $"{nameof(WithOpenTelemetryMetricsPublishesAnIdenticalManifestWhenALateSubscriberOnlyGathers)}-baseline");

        var gathered = await ManifestUtils.PublishManifestAsync(
            appBuilder =>
            {
                var documentDB = appBuilder.AddDocumentDB("DocumentDB")
                    .WithImageTag(InterlockedTag)
                    .WithOpenTelemetryMetrics()
                    .WithArgs("--log-level", "trace");

                appBuilder.Eventing.Subscribe<Publishing.BeforePublishEvent>((_, token) =>
                    GatherPublishConfigurationAsync(documentDB.Resource, token));
            },
            $"{nameof(WithOpenTelemetryMetricsPublishesAnIdenticalManifestWhenALateSubscriberOnlyGathers)}-gathered");

        Assert.True(JsonNode.DeepEquals(baseline, gathered));
    }

    /// <summary>
    /// <c>WithManifestPublishingCallback</c> is a supported replacement API. A late subscriber
    /// cannot use it to shadow the package checkpoint after making the cached command stale; the
    /// checkpoint is restored after model events and rejects the resource before the replacement
    /// writer runs.
    /// </summary>
    [Fact]
    public async Task WithOpenTelemetryMetricsCannotShadowTheSerializationCheckpointWithALateManifestCallback()
    {
        var writerCalls = 0;

        var log = await ManifestUtils.PublishManifestExpectingFailureAsync(appBuilder =>
        {
            var documentDB = appBuilder.AddDocumentDB("DocumentDB")
                .WithImageTag(InterlockedTag)
                .WithOpenTelemetryMetrics();

            appBuilder.Eventing.Subscribe<Publishing.BeforePublishEvent>(async (_, token) =>
            {
                await GatherPublishConfigurationAsync(documentDB.Resource, token);
                documentDB.Resource.Entrypoint = "/late/entrypoint.sh";

                documentDB.WithManifestPublishingCallback(async context =>
                {
                    Interlocked.Increment(ref writerCalls);
                    await context.WriteContainerAsync(documentDB.Resource);
                });
            });
        });

        Assert.Contains("its container entrypoint changed", log, StringComparison.Ordinal);
        Assert.Equal(0, writerCalls);
    }

    /// <summary>
    /// Restoring the checkpoint must still honour a legal replacement writer. It is delegated
    /// exactly once after the seal passes rather than being duplicated by the package.
    /// </summary>
    [Fact]
    public async Task WithOpenTelemetryMetricsDelegatesALateManifestCallbackExactlyOnceWhenTheSealStillMatches()
    {
        var writerCalls = 0;

        var manifest = await ManifestUtils.PublishManifestAsync(appBuilder =>
        {
            var documentDB = appBuilder.AddDocumentDB("DocumentDB")
                .WithImageTag(InterlockedTag)
                .WithOpenTelemetryMetrics();

            appBuilder.Eventing.Subscribe<Publishing.BeforePublishEvent>(async (_, token) =>
            {
                await GatherPublishConfigurationAsync(documentDB.Resource, token);

                documentDB.WithManifestPublishingCallback(async context =>
                {
                    Interlocked.Increment(ref writerCalls);
                    await context.WriteContainerAsync(documentDB.Resource);
                });
            });
        });

        Assert.Equal(1, writerCalls);
        Assert.Equal(
            GatewayConfigurationShell,
            manifest["resources"]?["DocumentDB"]?["entrypoint"]?.GetValue<string>());
    }

    /// <summary>
    /// Exclusion is the supported no-output boundary: the pipeline checkpoint must not displace
    /// Aspire's callback-less manifest annotation and accidentally publish the resource.
    /// </summary>
    [Fact]
    public async Task WithOpenTelemetryMetricsKeepsAnExcludedResourceOutOfTheManifest()
    {
        var manifest = await ManifestUtils.PublishManifestAsync(appBuilder =>
        {
            var documentDB = appBuilder.AddDocumentDB("DocumentDB")
                .WithImageTag(InterlockedTag)
                .WithOpenTelemetryMetrics()
                .ExcludeFromManifest();

            appBuilder.Eventing.Subscribe<Publishing.BeforePublishEvent>((_, token) =>
                GatherPublishConfigurationAsync(documentDB.Resource, token));
        });

        Assert.Null(manifest["resources"]?["DocumentDB"]);
    }

    public static TheoryData<string[], string> StorageChangingRuntimeArgumentCases => new()
    {
        { ["--mount", "type=volume,source=runtime-secret,target=/tmp"], "--mount" },
        { ["--mount=type=volume,source=runtime-secret,target=/tmp"], "--mount" },
        { ["--volume", "runtime-secret:/tmp"], "--volume" },
        { ["--volume=runtime-secret:/tmp"], "--volume" },
        { ["-v", "runtime-secret:/tmp"], "--volume" },
        { ["-vruntime-secret:/tmp"], "--volume" },
        { ["-v=runtime-secret:/tmp"], "--volume" },
        { ["-itvruntime-secret:/tmp"], "--volume" },
        { ["-l=", "--mount", "type=volume,source=runtime-secret,target=/tmp"], "--mount" },
        { ["--tmpfs", "/tmp:size=64m"], "--tmpfs" },
        { ["--tmpfs=/tmp:size=64m"], "--tmpfs" },
        { ["--volume-driver", "runtime-secret"], "--volume-driver" },
        { ["--volumes-from", "runtime-secret"], "--volumes-from" },
        { ["--use-api-socket"], "--use-api-socket" },
        { ["--secret", "runtime-secret,type=mount,target=/tmp"], "--secret" },
        { ["--secret=runtime-secret,type=mount,target=/tmp"], "--secret" },
        { ["--image-volume", "tmpfs"], "--image-volume" },
        { ["--image-volume=tmpfs"], "--image-volume" },
        { ["--ipc", "host"], "--ipc" },
        { ["--ipc=host"], "--ipc" },
        { ["--chrootdirs", "/runtime-secret"], "--chrootdirs" },
        { ["--chrootdirs=/runtime-secret"], "--chrootdirs" },
        { ["--read-only-tmpfs"], "--read-only-tmpfs" },
        { ["--read-only-tmpfs=false"], "--read-only-tmpfs" },
        { ["--systemd"], "--systemd" },
        { ["--systemd=always"], "--systemd" },
        { ["--pod", "runtime-secret"], "--pod" },
        { ["--pod=runtime-secret"], "--pod" },
        { ["--pod-id-file", "/runtime-secret/pod-id"], "--pod-id-file" },
        { ["--pod-id-file=/runtime-secret/pod-id"], "--pod-id-file" },
        { ["--read-only"], "--read-only" },
        { ["--read-only=true"], "--read-only" },
        { ["--read-only=false"], "--read-only" },
        { ["--storage-opt", "overlay2.size=runtime-secret"], "--storage-opt" },
        { ["--storage-opt=overlay2.size=runtime-secret"], "--storage-opt" },
    };

    [Theory]
    [MemberData(nameof(StorageChangingRuntimeArgumentCases))]
    public async Task WithOpenTelemetryMetricsRejectsStorageChangingRuntimeArguments(
        string[] runtimeArguments,
        string expectedOption)
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithOpenTelemetryMetrics()
            .WithContainerRuntimeArgs(runtimeArguments);

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);
        await RaiseResourceStartPhasesAsync(app);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunContainerRuntimeArgsAsync(SingleServerResource(app)));

        Assert.Contains("storage-changing container runtime option", exception.Message, StringComparison.Ordinal);
        Assert.Contains(expectedOption, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("runtime-secret", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--entrypoint", "/runtime-secret/entrypoint")]
    [InlineData("--entrypoint=/runtime-secret/entrypoint")]
    public async Task WithOpenTelemetryMetricsRejectsRawRuntimeEntrypoints(params string[] runtimeArguments)
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithOpenTelemetryMetrics()
            .WithContainerRuntimeArgs(runtimeArguments);

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);
        await RaiseResourceStartPhasesAsync(app);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunContainerRuntimeArgsAsync(SingleServerResource(app)));

        Assert.Contains("'--entrypoint'", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("runtime-secret", exception.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string[]> ProtectedRuntimeEnvironmentCases => new()
    {
        { ["--env", "DATA_PATH=/runtime-secret"] },
        { ["--env=CONFIG_DIR=/runtime-secret"] },
        { ["-e", "GATEWAY_HOME=/runtime-secret"] },
        { ["-eOTEL_METRICS_ENABLED=false"] },
        { ["-e=OTEL_EXPORTER_OTLP_ENDPOINT=http://runtime-secret"] },
        { ["--env-file", "/runtime-secret/environment"] },
        { ["--env-file=/runtime-secret/environment"] },
        { ["--env-host"] },
        { ["--env-host=false"] },
        { ["--env-merge", "DATA_PATH=/runtime-secret"] },
        { ["--env-merge=CONFIG_DIR=/runtime-secret"] },
        { ["--unsetenv", "OTEL_METRICS_ENABLED"] },
        { ["--unsetenv=OTEL_EXPORTER_OTLP_ENDPOINT"] },
        { ["--unsetenv-all"] },
    };

    [Theory]
    [MemberData(nameof(ProtectedRuntimeEnvironmentCases))]
    public async Task WithOpenTelemetryMetricsRejectsProtectedRuntimeEnvironmentOverrides(
        string[] runtimeArguments)
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithOpenTelemetryMetrics()
            .WithContainerRuntimeArgs(runtimeArguments);

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);
        await RaiseResourceStartPhasesAsync(app);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunContainerRuntimeArgsAsync(SingleServerResource(app)));

        Assert.Contains("raw container runtime environment override", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("runtime-secret", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--rootfs", "/runtime-secret/rootfs")]
    [InlineData("--rootfs=/runtime-secret/rootfs")]
    public async Task WithOpenTelemetryMetricsRejectsPodmanRootfsOverrides(params string[] runtimeArguments)
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithOpenTelemetryMetrics()
            .WithContainerRuntimeArgs(runtimeArguments);

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);
        await RaiseResourceStartPhasesAsync(app);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunContainerRuntimeArgsAsync(SingleServerResource(app)));

        Assert.Contains("'--rootfs'", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("runtime-secret", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("registry.example/runtime-secret:latest")]
    [InlineData("--", "registry.example/runtime-secret:latest")]
    public async Task RuntimeArgumentParserRejectsPositionalOperandsWithoutRepeatingThem(
        params string[] runtimeArguments)
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithOpenTelemetryMetrics()
            .WithContainerRuntimeArgs(runtimeArguments);

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);
        await RaiseResourceStartPhasesAsync(app);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunContainerRuntimeArgsAsync(SingleServerResource(app)));

        Assert.Contains("positional container runtime operand", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("registry.example", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("runtime-secret", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--authfile")]
    [InlineData("--env-merge")]
    [InlineData("--sdnotify")]
    public async Task RuntimeArgumentParserEnforcesRequiredPodmanOptionArity(string runtimeArgument)
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithOpenTelemetryMetrics()
            .WithContainerRuntimeArgs(runtimeArgument);

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);
        await RaiseResourceStartPhasesAsync(app);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunContainerRuntimeArgsAsync(SingleServerResource(app)));

        Assert.Contains("does not provide the required value", exception.Message, StringComparison.Ordinal);
        Assert.Contains(runtimeArgument, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--future-runtime-option=runtime-secret")]
    [InlineData("-Zruntime-secret")]
    public async Task RuntimeArgumentParserRejectsUnknownOptionsWithoutRepeatingTheirTokens(
        string runtimeArgument)
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithOpenTelemetryMetrics()
            .WithContainerRuntimeArgs(runtimeArgument);

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);
        await RaiseResourceStartPhasesAsync(app);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunContainerRuntimeArgsAsync(SingleServerResource(app)));

        Assert.Contains("outside the Docker and Podman run grammar", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("future-runtime-option", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("runtime-secret", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("-Z", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimeArgumentParserLeavesHarmlessPodmanOptionsAlone()
    {
        string[] runtimeArguments =
        [
            "--arch", "arm64",
            "--authfile=/work/auth.json",
            "--cgroups", "disabled",
            "--gidmap=0:1000:1",
            "--health-log-destination", "local",
            "--http-proxy=false",
            "--init",
            "--net=bridge",
            "--os", "linux",
            "--retry", "3",
            "--sdnotify=ignore",
            "--tz", "UTC",
            "--variant", "v8",
            "--env-merge", "UNRELATED=prefix",
            "--unsetenv=UNRELATED",
            "--",
        ];

        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithOpenTelemetryMetrics()
            .WithContainerRuntimeArgs(runtimeArguments);

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);
        await RaiseResourceStartPhasesAsync(app);

        Assert.Equal(
            runtimeArguments,
            await RunContainerRuntimeArgsAsync(SingleServerResource(app)));
    }

    [Fact]
    public async Task RuntimeArgumentParserDoesNotTreatUnrelatedOptionValuesAsOptions()
    {
        string[] runtimeArguments =
        [
            "--label", "--mount",
            "-l--volume",
            "--health-cmd", "--entrypoint",
            "--name=--tmpfs",
            "--env", "UNRELATED=DATA_PATH=/runtime-secret",
            "-eUNRELATED=--mount",
            "-it",
            "--label=CONFIG_DIR=/runtime-secret",
        ];

        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithOpenTelemetryMetrics()
            .WithContainerRuntimeArgs(runtimeArguments);

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);
        await RaiseResourceStartPhasesAsync(app);

        Assert.Equal(
            runtimeArguments,
            await RunContainerRuntimeArgsAsync(SingleServerResource(app)));
    }

    [Fact]
    public async Task RuntimeArgumentParserUsesResolvedDeferredValuesExactlyOnce()
    {
        var option = new CountingValueProvider("--label");
        var value = new CountingValueProvider("--mount");

        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithOpenTelemetryMetrics()
            .WithContainerRuntimeArgs(context =>
            {
                context.Args.Add(option);
                context.Args.Add(value);
            });

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);
        await RaiseResourceStartPhasesAsync(app);

        Assert.Equal(
            ["--label", "--mount"],
            await RunContainerRuntimeArgsAsync(SingleServerResource(app)));
        Assert.Equal(1, option.Evaluations);
        Assert.Equal(1, value.Evaluations);
    }

    [Fact]
    public async Task RuntimeArgumentParserRejectsADeferredStorageOptionWithoutRepeatingIt()
    {
        var option = new CountingValueProvider("--mount");

        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithOpenTelemetryMetrics()
            .WithContainerRuntimeArgs(context =>
            {
                context.Args.Add(option);
                context.Args.Add("type=volume,source=runtime-secret,target=/tmp");
            });

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);
        await RaiseResourceStartPhasesAsync(app);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunContainerRuntimeArgsAsync(SingleServerResource(app)));

        Assert.Equal(1, option.Evaluations);
        Assert.Contains("'--mount'", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("runtime-secret", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimeArgumentParserRejectsADeferredProtectedEnvironmentValue()
    {
        var environment = new CountingValueProvider("DATA_PATH=/runtime-secret");

        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithOpenTelemetryMetrics()
            .WithContainerRuntimeArgs(context =>
            {
                context.Args.Add("--env");
                context.Args.Add(environment);
            });

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);
        await RaiseResourceStartPhasesAsync(app);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunContainerRuntimeArgsAsync(SingleServerResource(app)));

        Assert.Equal(1, environment.Evaluations);
        Assert.Contains("raw container runtime environment override", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("runtime-secret", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimeArgumentParserRejectsADeferredPodmanSecretOption()
    {
        var option = new CountingValueProvider("--secret");

        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithOpenTelemetryMetrics()
            .WithContainerRuntimeArgs(context =>
            {
                context.Args.Add(option);
                context.Args.Add("runtime-secret,type=mount,target=/tmp");
            });

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);
        await RaiseResourceStartPhasesAsync(app);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunContainerRuntimeArgsAsync(SingleServerResource(app)));

        Assert.Equal(1, option.Evaluations);
        Assert.Contains("'--secret'", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("runtime-secret", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimeArgumentParserRejectsADeferredPodmanEnvironmentMerge()
    {
        var environment = new CountingValueProvider("DATA_PATH=/runtime-secret");

        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithOpenTelemetryMetrics()
            .WithContainerRuntimeArgs(context =>
            {
                context.Args.Add("--env-merge");
                context.Args.Add(environment);
            });

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);
        await RaiseResourceStartPhasesAsync(app);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunContainerRuntimeArgsAsync(SingleServerResource(app)));

        Assert.Equal(1, environment.Evaluations);
        Assert.Contains("raw container runtime environment override", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("runtime-secret", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimeArgumentParserResolvesADeferredHarmlessPodmanValueOnce()
    {
        var value = new CountingValueProvider("ignore");

        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithOpenTelemetryMetrics()
            .WithContainerRuntimeArgs(context =>
            {
                context.Args.Add("--sdnotify");
                context.Args.Add(value);
            });

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);
        await RaiseResourceStartPhasesAsync(app);

        Assert.Equal(
            ["--sdnotify", "ignore"],
            await RunContainerRuntimeArgsAsync(SingleServerResource(app)));
        Assert.Equal(1, value.Evaluations);
    }

    [Fact]
    public async Task RuntimeArgumentParserDoesNotExposeASecretParameterOperandInPublishMode()
    {
        const string secret = "operand-secret-fragment";

        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.Configuration["Parameters:operand"] = secret;
        var operand = appBuilder.AddParameter("operand", secret: true);

        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithOpenTelemetryMetrics()
            .WithContainerRuntimeArgs(context => context.Args.Add(operand.Resource));

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);
        await RaiseResourceStartPhasesAsync(app);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunContainerRuntimeArgsAsync(SingleServerResource(app)));

        Assert.Contains("positional container runtime operand", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("operand-secret", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimeArgumentParserDoesNotExposeACredentialReferenceOperandInPublishMode()
    {
        const string passwordValue = "ReferenceCredentialSecret987";

        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.Configuration["Parameters:reference-password"] = passwordValue;
        var password = appBuilder.AddParameter("reference-password", secret: true);
        var credential = ReferenceExpression.Create(
            $"mongodb://runtime-user:{password.Resource}@runtime-host:10260/admin");

        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithOpenTelemetryMetrics()
            .WithContainerRuntimeArgs(context => context.Args.Add(credential));

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);
        await RaiseResourceStartPhasesAsync(app);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunContainerRuntimeArgsAsync(SingleServerResource(app)));

        Assert.Contains("positional container runtime operand", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(passwordValue, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("mongodb://", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("runtime-user", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimeArgumentParserDoesNotExposeADeferredResolutionFailure()
    {
        var value = new ThrowingValueProvider("resolution-secret-fragment");

        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithOpenTelemetryMetrics()
            .WithContainerRuntimeArgs(context => context.Args.Add(value));

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);
        await RaiseResourceStartPhasesAsync(app);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunContainerRuntimeArgsAsync(SingleServerResource(app)));

        Assert.Contains("could not be resolved", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("resolution-secret", exception.ToString(), StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task RuntimeArgumentParserResolvesParameterBackedRuntimeArguments()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.Configuration["Parameters:runtime-option"] = "--label";
        var runtimeOption = appBuilder.AddParameter("runtime-option", secret: true);

        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithOpenTelemetryMetrics()
            .WithContainerRuntimeArgs(context =>
            {
                context.Args.Add(runtimeOption.Resource);
                context.Args.Add("team=data");
            });

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);
        await RaiseResourceStartPhasesAsync(app);

        Assert.Equal(
            ["--label", "team=data"],
            await RunContainerRuntimeArgsAsync(SingleServerResource(app)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task WithOpenTelemetryMetricsAllowsNullOrFalseShellExecution(bool? shellExecution)
    {
        var appBuilder = CreateLifecycleTestBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithOpenTelemetryMetrics();
        documentDB.Resource.ShellExecution = shellExecution;

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);
        await RaiseResourceStartPhasesAsync(app);

        var resource = SingleServerResource(app);
        Assert.Equal(3, (await BuildContainerArgsAsync(resource)).Count);
        Assert.Empty(await RunContainerRuntimeArgsAsync(resource));
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsRejectsShellExecutionBeforeDcpRewritesTheCommand()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithOpenTelemetryMetrics();
        documentDB.Resource.ShellExecution = true;

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);
        await RaiseResourceStartPhasesAsync(app);

        var resource = SingleServerResource(app);
        var runtimeException = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunContainerRuntimeArgsAsync(resource));
        var commandException = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BuildContainerArgsAsync(resource));

        Assert.Contains("ShellExecution", runtimeException.Message, StringComparison.Ordinal);
        Assert.Contains("rewrites the already verified wrapper arguments", runtimeException.Message, StringComparison.Ordinal);
        Assert.Equal(runtimeException.Message, commandException.Message);
    }

    [Fact]
    public async Task ShellExecutionMutationAfterCachedCommandGenerationFailsAtRuntimeCheckpoint()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithOpenTelemetryMetrics();

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);
        await RaiseResourceStartPhasesAsync(app);

        var resource = SingleServerResource(app);
        Assert.Equal(3, (await BuildContainerArgsAsync(resource)).Count);

        documentDB.Resource.ShellExecution = true;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunContainerRuntimeArgsAsync(resource));

        Assert.Contains("ShellExecution", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CustomImagesKeepTheirRuntimeAndShellExecutionBehavior()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithImage("contoso/documentdb-custom")
            .WithImageTag("latest")
            .WithOpenTelemetryMetrics()
            .WithContainerRuntimeArgs("--mount", "type=volume,source=custom,target=/tmp");
        documentDB.Resource.ShellExecution = true;

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);
        await RaiseResourceStartPhasesAsync(app);

        var resource = SingleServerResource(app);
        Assert.Null(resource.Entrypoint);
        Assert.Empty(await BuildContainerArgsAsync(resource));
        Assert.Equal(
            ["--mount", "type=volume,source=custom,target=/tmp"],
            await RunContainerRuntimeArgsAsync(resource));
    }

    [Fact]
    public async Task OfficialImagesWithoutTheWrapperKeepShellExecutionBehavior()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag("pg17-0.114.0")
            .WithOpenTelemetryMetrics()
            .WithArgs("echo", "unchanged")
            .WithContainerRuntimeArgs("--mount", "type=volume,source=legacy,target=/tmp");
        documentDB.Resource.ShellExecution = true;

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);
        await RaiseResourceStartPhasesAsync(app);

        var resource = SingleServerResource(app);
        Assert.Null(resource.Entrypoint);
        Assert.Equal(["echo", "unchanged"], await BuildContainerArgsAsync(resource));
        Assert.Equal(
            ["--mount", "type=volume,source=legacy,target=/tmp"],
            await RunContainerRuntimeArgsAsync(resource));
    }

    [Fact]
    public async Task WithOpenTelemetryMetricsFailsPublishWhenShellExecutionIsTrue()
    {
        var log = await ManifestUtils.PublishManifestExpectingFailureAsync(appBuilder =>
        {
            var documentDB = appBuilder.AddDocumentDB("DocumentDB")
                .WithImageTag(InterlockedTag)
                .WithOpenTelemetryMetrics();
            documentDB.Resource.ShellExecution = true;
        });

        Assert.Contains("ShellExecution", log, StringComparison.Ordinal);
        Assert.Contains("false or null", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManifestDiagnosticsDoNotExposeASecretParameterArgument()
    {
        const string secret = "manifest-parameter-secret-fragment";

        var log = await ManifestUtils.PublishManifestExpectingFailureAsync(appBuilder =>
        {
            appBuilder.Configuration["Parameters:operand"] = secret;
            var operand = appBuilder.AddParameter("operand", secret: true);
            var documentDB = appBuilder.AddDocumentDB("DocumentDB")
                .WithImageTag(InterlockedTag)
                .WithOpenTelemetryMetrics()
                .WithArgs(context => context.Args.Add(operand.Resource))
                .WithContainerRuntimeArgs(context => context.Args.Add(operand.Resource));

            appBuilder.Eventing.Subscribe<Publishing.BeforePublishEvent>(async (_, cancellationToken) =>
            {
                await GatherPublishConfigurationAsync(documentDB.Resource, cancellationToken);
                documentDB.Resource.ShellExecution = true;
            });
        });

        Assert.Contains("ShellExecution", log, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, log, StringComparison.Ordinal);
        Assert.DoesNotContain("manifest-parameter-secret", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManifestDiagnosticsDoNotExposeACredentialReferenceExpression()
    {
        const string passwordValue = "ManifestCredentialSecret987";

        var log = await ManifestUtils.PublishManifestExpectingFailureAsync(appBuilder =>
        {
            appBuilder.Configuration["Parameters:manifest-password"] = passwordValue;
            var password = appBuilder.AddParameter("manifest-password", secret: true);
            var documentDB = appBuilder.AddDocumentDB("DocumentDB", password: password)
                .WithImageTag(InterlockedTag)
                .WithOpenTelemetryMetrics();

            documentDB
                .WithArgs(context => context.Args.Add(documentDB.Resource.ConnectionStringExpression))
                .WithContainerRuntimeArgs(context =>
                    context.Args.Add(documentDB.Resource.ConnectionStringExpression));

            appBuilder.Eventing.Subscribe<Publishing.BeforePublishEvent>(async (_, cancellationToken) =>
            {
                await GatherPublishConfigurationAsync(documentDB.Resource, cancellationToken);
                documentDB.Resource.ShellExecution = true;
            });
        });

        Assert.Contains("ShellExecution", log, StringComparison.Ordinal);
        Assert.DoesNotContain(passwordValue, log, StringComparison.Ordinal);
        Assert.DoesNotContain("mongodb://", log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("manifest-password", log, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task WithOpenTelemetryMetricsPublishesWithNullOrFalseShellExecution(bool? shellExecution)
    {
        var manifest = await ManifestUtils.PublishManifestAsync(appBuilder =>
        {
            var documentDB = appBuilder.AddDocumentDB("DocumentDB")
                .WithImageTag(InterlockedTag)
                .WithOpenTelemetryMetrics();
            documentDB.Resource.ShellExecution = shellExecution;
        });

        var resource = manifest["resources"]?["DocumentDB"];
        Assert.Equal(GatewayConfigurationShell, resource?["entrypoint"]?.GetValue<string>());
        Assert.Equal("-c", resource?["args"]?[0]?.GetValue<string>());
        Assert.Equal(GatewayConfigurationShellArgumentZero, resource?["args"]?[2]?.GetValue<string>());
    }

    /// <summary>
    /// The other control: container runtime arguments that only do what they are for still work,
    /// and the checkpoint returns the same resolved values it validated.
    /// </summary>
    [Fact]
    public async Task WithOpenTelemetryMetricsLeavesBenignContainerRuntimeArgumentsAlone()
    {
        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithOpenTelemetryMetrics()
            .WithContainerRuntimeArgs("--label", "team=data");

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);
        await RaiseResourceStartPhasesAsync(app);

        var resource = SingleServerResource(app);
        Assert.Equal(3, (await BuildContainerArgsAsync(resource)).Count);

        Assert.Equal(["--label", "team=data"], await RunContainerRuntimeArgsAsync(resource));
    }

    /// <summary>
    /// A deferred value keeps its single evaluation: the wrapper's callback is moved between
    /// phases, never rebuilt, so Aspire's record of it — and of everything before it — survives,
    /// and a parameter that may be a secret is resolved by Aspire once rather than by this package
    /// a second time.
    /// </summary>
    [Fact]
    public async Task WithOpenTelemetryMetricsResolvesADeferredArgumentExactlyOnce()
    {
        var evaluations = 0;

        var appBuilder = CreateLifecycleTestBuilder();
        appBuilder.Configuration["Parameters:level"] = "trace";
        var level = appBuilder.AddParameter("level", secret: true);

        appBuilder.AddDocumentDB("DocumentDB")
            .WithImageTag(InterlockedTag)
            .WithOpenTelemetryMetrics()
            .WithArgs(context =>
            {
                Interlocked.Increment(ref evaluations);
                context.Args.Add("--log-level");
                context.Args.Add(level.Resource);
            });

        using var app = await BuildAndRaiseBeforeStartAsync(appBuilder);
        await RaiseResourceStartPhasesAsync(app);
        await RaiseResourceStartPhasesAsync(app);

        var resource = SingleServerResource(app);
        var first = await BuildContainerArgsAsync(resource);
        var second = await BuildContainerArgsAsync(resource);

        Assert.Equal(1, evaluations);
        Assert.Equal(5, first.Count);
        Assert.Equal(first, second);
        Assert.Equal("-c", first[0]);
        Assert.Equal(GatewayConfigurationShellArgumentZero, first[2]);
        Assert.Equal("--log-level", first[3]);
        Assert.Same(level.Resource, first[4]);
    }

    /// <summary>
    /// Invokes the resource's container-runtime-argument callbacks the way Aspire's container
    /// creator does: in annotation order, over one shared list, with no result cache — which is
    /// what makes this the run's unconditional checkpoint.
    /// </summary>
    private sealed class CountingValueProvider(string value) : IValueProvider
    {
        public int Evaluations { get; private set; }

        public ValueTask<string?> GetValueAsync(CancellationToken cancellationToken = default)
        {
            Evaluations++;
            return ValueTask.FromResult<string?>(value);
        }
    }

    private sealed class ThrowingValueProvider(string message) : IValueProvider
    {
        public ValueTask<string?> GetValueAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromException<string?>(new InvalidOperationException(message));
    }

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

    private static async Task GatherPublishConfigurationAsync(
        DocumentDBServerResource resource,
        CancellationToken cancellationToken)
    {
        await ExecutionConfigurationBuilder.Create(resource)
            .WithArgumentsConfig()
            .WithEnvironmentVariablesConfig()
            .BuildAsync(
                new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish),
                NullLogger.Instance,
                cancellationToken);
    }

    /// <summary>
    /// A lifecycle hook that builds the resource's configuration through the same public API
    /// Aspire uses, and then changes the resource — the shape that made a validated command line
    /// stale.
    /// </summary>
#pragma warning disable CS0618 // Type or member is obsolete
    private sealed class GatherThenMutateLifecycleHook(
        string resourceName,
        Action<DocumentDBServerResource> mutate) : IDistributedApplicationLifecycleHook
    {
        public async Task BeforeStartAsync(DistributedApplicationModel appModel, CancellationToken cancellationToken = default)
        {
            var resource = appModel.Resources.OfType<DocumentDBServerResource>()
                .Single(candidate => candidate.Name == resourceName);

            await ExecutionConfigurationBuilder.Create(resource)
                .WithArgumentsConfig()
                .WithEnvironmentVariablesConfig()
                .BuildAsync(
                    new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish),
                    NullLogger.Instance,
                    cancellationToken);

            mutate(resource);
        }
    }
#pragma warning restore CS0618

    // Lifecycle hooks are obsolete in favour of eventing subscribers, but Aspire still runs
    // registered hooks - after BeforeStartEvent - so they remain a way to add a command-line
    // callback late, and the wrapper has to hold against it.
#pragma warning disable CS0618 // Type or member is obsolete
    private sealed class PrependArgumentLifecycleHook(string resourceName, string argument) : IDistributedApplicationLifecycleHook
    {
        public Task BeforeStartAsync(DistributedApplicationModel appModel, CancellationToken cancellationToken = default)
        {
            appModel.Resources.OfType<DocumentDBServerResource>().Single(resource => resource.Name == resourceName)
                .Annotations.Add(new CommandLineArgsCallbackAnnotation(args => args.Insert(0, argument)));

            return Task.CompletedTask;
        }
    }

    private static async Task RunLifecycleHooksAsync(DistributedApplication app)
    {
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        foreach (var hook in app.Services.GetServices<IDistributedApplicationLifecycleHook>())
        {
            await hook.BeforeStartAsync(model, CancellationToken.None);
        }
    }
#pragma warning restore CS0618

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
            .WithImageTag("pg17-0.116.0")
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
    public async Task WithOpenTelemetryMetricsPublishesAnUnwrappedManifestForTheDefaultImage()
    {
        var manifest = await ManifestUtils.PublishManifestAsync(appBuilder =>
            appBuilder.AddDocumentDB("DocumentDB").WithOpenTelemetryMetrics());

        var resource = manifest["resources"]?["DocumentDB"];
        Assert.NotNull(resource);

        Assert.Equal("true", resource!["env"]?["OTEL_METRICS_ENABLED"]?.GetValue<string>());
        Assert.Null(resource["entrypoint"]);
        Assert.Null(resource["args"]);
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

    /// <summary>
    /// The real publishing pipeline is where the distinction is decided for <c>azd</c>: the
    /// manifest carries a <c>build</c> instruction rather than an <c>image</c>, and no wrapper.
    /// </summary>
    [Fact]
    public async Task WithOpenTelemetryMetricsPublishesAnUnwrappedManifestForDockerfileBuilds()
    {
        var contextPath = CreateOfficialLookingDockerfileContext();

        var manifest = await ManifestUtils.PublishManifestAsync(appBuilder =>
            appBuilder.AddDocumentDB("DocumentDB")
                .WithImageTag(InterlockedTag)
                .WithDockerfile(contextPath)
                .WithOpenTelemetryMetrics());

        var resource = manifest["resources"]?["DocumentDB"];
        Assert.NotNull(resource);

        Assert.Null(resource!["image"]);
        Assert.NotNull(resource["build"]);
        Assert.Null(resource["entrypoint"]);
        Assert.Null(resource["args"]);
        Assert.Equal("true", resource["env"]?["OTEL_METRICS_ENABLED"]?.GetValue<string>());
    }

    /// <summary>
    /// Through the real publishing pipeline: the rearranged spelling publishes the same image
    /// reference as the default one, and carries the wrapper with it.
    /// </summary>
    [Fact]
    public async Task WithOpenTelemetryMetricsPublishesAWrappedManifestForAQualifiedOfficialImage()
    {
        var manifest = await ManifestUtils.PublishManifestAsync(appBuilder =>
            appBuilder.AddDocumentDB("DocumentDB")
                .WithImage(QualifiedOfficialImage, InterlockedTag)
                .WithImageRegistry(null!)
                .WithOpenTelemetryMetrics());

        var resource = manifest["resources"]?["DocumentDB"];
        Assert.NotNull(resource);

        Assert.Equal($"{QualifiedOfficialImage}:{InterlockedTag}", resource!["image"]?.GetValue<string>());
        Assert.Equal(GatewayConfigurationShell, resource["entrypoint"]?.GetValue<string>());
        Assert.Equal(3, resource["args"]?.AsArray().Count);
    }

    /// <summary>
    /// Through the real publishing pipeline, for a reference whose prefix carries a namespace:
    /// the manifest ships the reference the caller composed, unwrapped, because that repository is
    /// not one this package publishes.
    /// </summary>
    [Theory]
    [InlineData(null, "ghcr.io/evil/documentdb/documentdb-local", "ghcr.io/evil/documentdb/documentdb-local")]
    [InlineData("ghcr.io/evil", "documentdb/documentdb-local", "ghcr.io/evil/documentdb/documentdb-local")]
    [InlineData("contoso.azurecr.io/mirrors", "documentdb/documentdb-local", "contoso.azurecr.io/mirrors/documentdb/documentdb-local")]
    public async Task WithOpenTelemetryMetricsPublishesAnUnwrappedManifestForAnExtraPathSegment(
        string? registry,
        string image,
        string expected)
    {
        var manifest = await ManifestUtils.PublishManifestAsync(appBuilder =>
            appBuilder.AddDocumentDB("DocumentDB")
                .WithImage(image, InterlockedTag)
                .WithImageRegistry(registry!)
                .WithOpenTelemetryMetrics());

        var resource = manifest["resources"]?["DocumentDB"];
        Assert.NotNull(resource);

        Assert.Equal($"{expected}:{InterlockedTag}", resource!["image"]?.GetValue<string>());
        Assert.Null(resource["entrypoint"]);
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

    /// <summary>
    /// Builds the resource's container arguments through Aspire's own argument gatherer, and
    /// returns the values the callbacks produced before resolution.
    /// </summary>
    /// <remarks>
    /// Aspire evaluates every command-line callback in annotation order over one shared list and
    /// caches each callback's result. Invoking the callbacks by hand instead would lose both the
    /// order the container is really configured in and the single evaluation the wrapper depends
    /// on, and a wrapper that has to be the last word cannot be tested against a command line the
    /// container never receives.
    /// </remarks>
    private static async Task<IReadOnlyList<object>> BuildContainerArgsAsync(
        DocumentDBServerResource resource,
        DistributedApplicationOperation operation = DistributedApplicationOperation.Run)
    {
        var result = await ExecutionConfigurationBuilder.Create(resource)
            .WithArgumentsConfig()
            .BuildAsync(
                new DistributedApplicationExecutionContext(operation),
                NullLogger.Instance,
                CancellationToken.None);

        if (result.Exception is not null)
        {
            throw result.Exception is AggregateException { InnerExceptions.Count: 1 } aggregate
                ? aggregate.InnerException!
                : result.Exception;
        }

        return [.. result.ArgumentsWithUnprocessed.Select(argument => argument.Unprocessed)];
    }

    /// <summary>
    /// Publishes the per-resource events a run publishes between <see cref="BeforeStartEvent"/>
    /// and the point at which a container's arguments are gathered, in that order.
    /// </summary>
    private static async Task RaiseResourceStartPhasesAsync(DistributedApplication app)
    {
        var eventing = app.Services.GetRequiredService<IDistributedApplicationEventing>();
        var resource = SingleServerResource(app);

        await eventing.PublishAsync(
            new ResourceEndpointsAllocatedEvent(resource, app.Services),
            EventDispatchBehavior.BlockingSequential,
            CancellationToken.None);

        await eventing.PublishAsync(
            new BeforeResourceStartedEvent(resource, app.Services),
            EventDispatchBehavior.BlockingSequential,
            CancellationToken.None);
    }

    private static async Task<string> GetWrapperScriptAsync(DocumentDBServerResource resource)
    {
        var args = await BuildContainerArgsAsync(resource);
        Assert.Equal(3, args.Count);
        return Assert.IsType<string>(args[1]);
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
