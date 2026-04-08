// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Hosting.DocumentDB.Tests;

public class AddDocumentDBTests
{
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
        var serverResource = dbResource.Parent as IResourceWithConnectionString;
        var connectionStringResource = dbResource as IResourceWithConnectionString;
        Assert.NotNull(connectionStringResource);
        var passwordParameter = Assert.IsType<ParameterResource>(dbResource.Parent.PasswordParameter);
        var password = await passwordParameter.GetValueAsync(default);
        var connectionString = await connectionStringResource.GetConnectionStringAsync();
        Assert.Equal($"mongodb://admin:{password}@localhost:10260?authSource=admin&authMechanism=SCRAM-SHA-256&tls=true&tlsInsecure=true", await serverResource.GetConnectionStringAsync());
        Assert.Equal("mongodb://admin:{DocumentDB-password.value}@{DocumentDB.bindings.tcp.host}:{DocumentDB.bindings.tcp.port}?authSource=admin&authMechanism=SCRAM-SHA-256&tls=true&tlsInsecure=true", serverResource.ConnectionStringExpression.ValueExpression);
        Assert.Equal($"mongodb://admin:{password}@localhost:10260/mydatabase?authSource=admin&authMechanism=SCRAM-SHA-256&tls=true&tlsInsecure=true", connectionString);
        Assert.Equal("mongodb://admin:{DocumentDB-password.value}@{DocumentDB.bindings.tcp.host}:{DocumentDB.bindings.tcp.port}/mydatabase?authSource=admin&authMechanism=SCRAM-SHA-256&tls=true&tlsInsecure=true", connectionStringResource.ConnectionStringExpression.ValueExpression);
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

        Assert.Equal("mongodb://admin:{DocumentDB1-password.value}@{DocumentDB1.bindings.tcp.host}:{DocumentDB1.bindings.tcp.port}/customers1?authSource=admin&authMechanism=SCRAM-SHA-256&tls=true&tlsInsecure=true", db1.Resource.ConnectionStringExpression.ValueExpression);
        Assert.Equal("mongodb://admin:{DocumentDB1-password.value}@{DocumentDB1.bindings.tcp.host}:{DocumentDB1.bindings.tcp.port}/customers2?authSource=admin&authMechanism=SCRAM-SHA-256&tls=true&tlsInsecure=true", db2.Resource.ConnectionStringExpression.ValueExpression);
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

        Assert.Equal("mongodb://admin:{DocumentDB1-password.value}@{DocumentDB1.bindings.tcp.host}:{DocumentDB1.bindings.tcp.port}/imports?authSource=admin&authMechanism=SCRAM-SHA-256&tls=true&tlsInsecure=true", db1.Resource.ConnectionStringExpression.ValueExpression);
        Assert.Equal("mongodb://admin:{DocumentDB2-password.value}@{DocumentDB2.bindings.tcp.host}:{DocumentDB2.bindings.tcp.port}/imports?authSource=admin&authMechanism=SCRAM-SHA-256&tls=true&tlsInsecure=true", db2.Resource.ConnectionStringExpression.ValueExpression);
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

        var env = await GetEnvironmentVariablesAsync(containerResource);

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

        var env = await GetEnvironmentVariablesAsync(containerResource);
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

        var env = await GetEnvironmentVariablesAsync(containerResource);
        Assert.Equal("true", env["SKIP_INIT_DATA"]);
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

        var env = await GetEnvironmentVariablesAsync(containerResource);
        Assert.Equal(expectedCertTarget, env["CERT_PATH"]);
        Assert.Equal(expectedKeyTarget, env["KEY_FILE"]);
    }

    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    public async Task WithTelemetryAddsEnvironmentVariable(bool enabled, string expectedValue)
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithTelemetry(enabled);

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());

        var env = await GetEnvironmentVariablesAsync(containerResource);
        Assert.Equal(expectedValue, env["ENABLE_TELEMETRY"]);
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

        var env = await GetEnvironmentVariablesAsync(containerResource);
        Assert.Equal("true", env["DISABLE_EXTENDED_RUM"]);
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

        var env = await GetEnvironmentVariablesAsync(containerResource);
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

        var appBuilder = DistributedApplication.CreateBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithLogLevel(DocumentDBLogLevel.Debug)
            .WithInitData(initDataPath)
            .WithTlsCertificate(certPath, keyPath)
            .WithTelemetry(enabled: false)
            .WithoutExtendedRum()
            .WithOwner("contoso");

        var manifest = await ManifestUtils.GetManifest(documentDB.Resource);

        Assert.Equal("debug", manifest["env"]?["LOG_LEVEL"]?.GetValue<string>());
        Assert.Equal("/init_doc_db.d", manifest["env"]?["INIT_DATA_PATH"]?.GetValue<string>());
        Assert.Equal("true", manifest["env"]?["SKIP_INIT_DATA"]?.GetValue<string>());
        Assert.Equal(expectedCertTarget, manifest["env"]?["CERT_PATH"]?.GetValue<string>());
        Assert.Equal(expectedKeyTarget, manifest["env"]?["KEY_FILE"]?.GetValue<string>());
        Assert.Equal("false", manifest["env"]?["ENABLE_TELEMETRY"]?.GetValue<string>());
        Assert.Equal("true", manifest["env"]?["DISABLE_EXTENDED_RUM"]?.GetValue<string>());
        Assert.Equal("contoso", manifest["env"]?["OWNER"]?.GetValue<string>());
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

        var env = await GetEnvironmentVariablesAsync(containerResource);
        Assert.Equal(expectedCertTarget, env["CERT_PATH"]);
        Assert.Equal(expectedKeyTarget, env["KEY_FILE"]);
    }

    private static async Task<Dictionary<string, string>> GetEnvironmentVariablesAsync(DocumentDBServerResource resource)
    {
        var configuration = await ExecutionConfigurationBuilder
            .Create(resource)
            .WithEnvironmentVariablesConfig()
            .BuildAsync(new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish), NullLogger.Instance, default);

        return configuration.EnvironmentVariables.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);
    }
}
