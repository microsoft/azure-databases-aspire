// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Hosting.DocumentDB.Tests;

[Trait("Category", "Unit")]
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
    public void WithHostPortConfiguresPrimaryEndpointPort()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder
            .AddDocumentDB("DocumentDB")
            .WithHostPort(10261);

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var containerResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());
        var endpoint = Assert.Single(containerResource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal("tcp", endpoint.Name);
        Assert.Equal(10260, endpoint.TargetPort);
        Assert.Equal(10261, endpoint.Port);
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
        Assert.Equal("/home/documentdb/postgresql/data", volumeAnnotation.Target);
        Assert.False(volumeAnnotation.IsReadOnly);

        var env = await BuildEnvironmentVariablesAsync(containerResource);
        var dataPath = Assert.Single(env.Where(entry => entry.Key == "DATA_PATH"));
        Assert.Equal("/home/documentdb/postgresql/data", dataPath.Value);
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
        Assert.Equal("/home/documentdb/postgresql/data", volumeAnnotation.Target);
        Assert.True(volumeAnnotation.IsReadOnly);

        var env = await BuildEnvironmentVariablesAsync(containerResource);
        var dataPath = Assert.Single(env.Where(entry => entry.Key == "DATA_PATH"));
        Assert.Equal("/home/documentdb/postgresql/data", dataPath.Value);
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
        Assert.Equal("/home/documentdb/postgresql/data", bindMountAnnotation.Target);
        Assert.False(bindMountAnnotation.IsReadOnly);

        var env = await BuildEnvironmentVariablesAsync(containerResource);
        var dataPath = Assert.Single(env.Where(entry => entry.Key == "DATA_PATH"));
        Assert.Equal("/home/documentdb/postgresql/data", dataPath.Value);
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
        Assert.Equal("/home/documentdb/postgresql/data", bindMountAnnotation.Target);
        Assert.True(bindMountAnnotation.IsReadOnly);

        var env = await BuildEnvironmentVariablesAsync(containerResource);
        var dataPath = Assert.Single(env.Where(entry => entry.Key == "DATA_PATH"));
        Assert.Equal("/home/documentdb/postgresql/data", dataPath.Value);
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
