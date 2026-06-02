// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
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
    public async Task WithPostgresEndpointDefaultTagThrowsUntilLatestReachesV0_112_0()
    {
        // Documents the intentional behavior described in issue #71's docs callout:
        // when DocumentDBVersions.Latest is < 0.112.0, AddDocumentDB().WithPostgresEndpoint()
        // (no explicit tag override) will throw at startup. This is the whole point of
        // the issue - converting a silent auth failure into a loud one. Once issue #70
        // bumps Latest to >= 0.112.0, this test's branch becomes the "no-throw" path.
        var latestParsed = Version.Parse(DocumentDBVersions.Latest);
        var floor = DocumentDBContainerImageTags.MinimumPostgresEndpointVersion;

        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithPostgresEndpoint(); // intentionally no WithImageTag

        using var app = appBuilder.Build();
        var resource = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>());

        if (latestParsed < floor)
        {
            // Current branch: Latest is 0.111.0, guard throws.
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => PublishBeforeResourceStartedAsync(app, resource));
            Assert.Contains($"pg17-{DocumentDBVersions.Latest}", ex.Message);
        }
        else
        {
            // After #70 lands: Latest is >= 0.112.0, default flow succeeds.
            await PublishBeforeResourceStartedAsync(app, resource);
        }
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
}
