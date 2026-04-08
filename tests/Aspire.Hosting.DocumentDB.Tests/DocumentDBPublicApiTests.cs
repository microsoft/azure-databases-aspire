using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Xunit;

namespace Aspire.Hosting.DocumentDB.Tests;

public class DocumentDBPublicApiTests
{
    [Fact]
    public void AddDocumentDBShouldThrowWhenBuilderIsNull()
    {
        IDistributedApplicationBuilder builder = null!;
        const string name = "DocumentDB";
        int? port = null;

        var action = () => builder.AddDocumentDB(name, port);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddDocumentDBShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var builder = TestDistributedApplicationBuilder.Create();
        var name = isNull ? null! : string.Empty;
        int? port = null;

        var action = () => builder.AddDocumentDB(name, port);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void AddDocumentDBWithParametersShouldThrowWhenBuilderIsNull()
    {
        IDistributedApplicationBuilder builder = null!;
        const string name = "DocumentDB"; IResourceBuilder<ParameterResource>? userName = null;
        IResourceBuilder<ParameterResource>? password = null;

        var action = () => builder.AddDocumentDB(name, userName: userName, password: password);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddDocumentDBWithParametersShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var builder = TestDistributedApplicationBuilder.Create();
        var name = isNull ? null! : string.Empty;
        IResourceBuilder<ParameterResource>? userName = null;
        IResourceBuilder<ParameterResource>? password = null;

        var action = () => builder.AddDocumentDB(name, userName: userName, password: password);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void AddDatabaseShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<DocumentDBServerResource> builder = null!;
        const string name = "db";

        var action = () => builder.AddDatabase(name);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddDatabaseShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var builder = TestDistributedApplicationBuilder.Create()
            .AddDocumentDB("DocumentDB");
        var name = isNull ? null! : string.Empty;

        var action = () => builder.AddDatabase(name);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void WithDataVolumeShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<DocumentDBServerResource> builder = null!;

        var action = () => builder.WithDataVolume();

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithDataBindMountShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<DocumentDBServerResource> builder = null!;
        const string source = "/DocumentDB/storage";

        var action = () => builder.WithDataBindMount(source);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WithDataBindMountShouldThrowWhenSourceIsNullOrEmpty(bool isNull)
    {
        var builder = TestDistributedApplicationBuilder.Create()
            .AddDocumentDB("DocumentDB");
        var source = isNull ? null! : string.Empty;

        var action = () => builder.WithDataBindMount(source);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(source), exception.ParamName);
    }

    [Fact]
    public void WithLogLevelShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<DocumentDBServerResource> builder = null!;

        var action = () => builder.WithLogLevel(DocumentDBLogLevel.Info);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithInitDataShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<DocumentDBServerResource> builder = null!;
        const string source = "/DocumentDB/init";

        var action = () => builder.WithInitData(source);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WithInitDataShouldThrowWhenSourceIsNullOrEmpty(bool isNull)
    {
        var builder = TestDistributedApplicationBuilder.Create()
            .AddDocumentDB("DocumentDB");
        var source = isNull ? null! : string.Empty;

        var action = () => builder.WithInitData(source);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(source), exception.ParamName);
    }

    [Fact]
    public void WithoutSampleDataShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<DocumentDBServerResource> builder = null!;

        var action = () => builder.WithoutSampleData();

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithTlsCertificateShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<DocumentDBServerResource> builder = null!;
        const string certPath = "/DocumentDB/cert.pem";
        const string keyPath = "/DocumentDB/key.pem";

        var action = () => builder.WithTlsCertificate(certPath, keyPath);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(null, "/DocumentDB/key.pem", "certPath", true)]
    [InlineData("", "/DocumentDB/key.pem", "certPath", false)]
    [InlineData("/DocumentDB/cert.pem", null, "keyPath", true)]
    [InlineData("/DocumentDB/cert.pem", "", "keyPath", false)]
    public void WithTlsCertificateShouldThrowWhenPathIsNullOrEmpty(string? certPath, string? keyPath, string expectedParamName, bool expectNull)
    {
        var builder = TestDistributedApplicationBuilder.Create()
            .AddDocumentDB("DocumentDB");

        var action = () => builder.WithTlsCertificate(certPath!, keyPath!);

        var exception = expectNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(expectedParamName, exception.ParamName);
    }

    [Fact]
    public void WithTelemetryShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<DocumentDBServerResource> builder = null!;

        var action = () => builder.WithTelemetry();

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithoutExtendedRumShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<DocumentDBServerResource> builder = null!;

        var action = () => builder.WithoutExtendedRum();

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithOwnerShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<DocumentDBServerResource> builder = null!;
        const string owner = "documentdb";

        var action = () => builder.WithOwner(owner);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WithOwnerShouldThrowWhenOwnerIsNullOrEmpty(bool isNull)
    {
        var builder = TestDistributedApplicationBuilder.Create()
            .AddDocumentDB("DocumentDB");
        var owner = isNull ? null! : string.Empty;

        var action = () => builder.WithOwner(owner);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(owner), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CtorDocumentDBDatabaseResourceShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var name = isNull ? null! : string.Empty;
        const string databaseName = "db";
        var parent = new DocumentDBServerResource("DocumentDB");

        var action = () => new DocumentDBDatabaseResource(name, databaseName, parent);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CtorDocumentDBDatabaseResourceShouldThrowWhenDatabaseNameIsNullOrEmpty(bool isNull)
    {
        const string name = "DocumentDB";
        var databaseName = isNull ? null! : string.Empty;
        var parent = new DocumentDBServerResource(name);

        var action = () => new DocumentDBDatabaseResource(name, databaseName, parent);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(databaseName), exception.ParamName);
    }

    [Fact]
    public void CtorDocumentDBDatabaseResourceShouldThrowWhenParentIsNull()
    {
        const string name = "DocumentDB";
        const string databaseName = "db";
        DocumentDBServerResource parent = null!;

        var action = () => new DocumentDBDatabaseResource(name, databaseName, parent);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(parent), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CtorDocumentDBServerResourceShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var name = isNull ? null! : string.Empty;

        var action = () => new DocumentDBServerResource(name);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }
}
