// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using MongoDB.Driver;
using Xunit;

namespace Aspire.Hosting.DocumentDB.Tests;

/// <summary>
/// Coverage for percent-encoding of arbitrary user names and passwords in the generated
/// <c>mongodb://</c> and <c>postgresql://</c> URIs.
/// </summary>
/// <remarks>
/// Kept in a dedicated file rather than in <c>AddDocumentDBTest.cs</c> because these tests pin a
/// single cross-cutting concern — the userinfo component of both connection-string surfaces —
/// and deliberately assert the exact encoded output rather than the URI shape.
/// </remarks>
[Trait("Category", "Unit")]
public class DocumentDBCredentialEncodingTests
{
    private const string ResourceName = "documentdb";
    private const string UserParameterName = "docdb-user";
    private const string PasswordParameterName = "docdb-pass";
    private const string TestHost = "localhost";
    private const int MongoPort = 10260;
    private const int PostgresPort = 25432;
    private const string MongoQuery = "?authSource=admin&authMechanism=SCRAM-SHA-256&tls=true&tlsInsecure=true";

    /// <summary>
    /// Credential values that are not URI-safe, paired with their required percent-encoded form.
    /// The expected values are written out literally so the tests cannot drift with the encoder.
    /// </summary>
    private static readonly (string Raw, string Encoded)[] s_uriUnsafeCredentials =
    [
        // Userinfo separator: an unencoded colon splits the user name from the password.
        ("us:er", "us%3Aer"),
        // Authority separator: an unencoded '@' terminates the userinfo component early.
        ("user@example.com", "user%40example.com"),
        // Path, query and fragment delimiters.
        ("a/b?c#d", "a%2Fb%3Fc%23d"),
        // A bare percent sign is not a valid escape sequence.
        ("100%", "100%25"),
        // Text that already looks percent-encoded must survive a single decode unchanged.
        ("%41", "%2541"),
        ("p a s s", "p%20a%20s%20s"),
        // RFC 3986 sub-delimiters.
        ("sub!$&'()*+,;=delims", "sub%21%24%26%27%28%29%2A%2B%2C%3B%3Ddelims"),
        // Connection-option injection through the MongoDB query string.
        ("tls=true&authSource=evil", "tls%3Dtrue%26authSource%3Devil"),
        // IPv6 literal delimiters.
        ("[bracket]", "%5Bbracket%5D"),
        // Non-ASCII: CJK, Cyrillic, Latin-1 supplement, and a surrogate pair.
        ("\u5bc6\u7801", "%E5%AF%86%E7%A0%81"),
        ("\u043f\u0430\u0440\u043e\u043b\u044c", "%D0%BF%D0%B0%D1%80%D0%BE%D0%BB%D1%8C"),
        ("caf\u00e9", "caf%C3%A9"),
        ("pass\U0001F512word", "pass%F0%9F%94%92word"),
        // Characters rejected outright by the MongoDB and libpq URI grammars.
        ("a\\b|c^d", "a%5Cb%7Cc%5Ed"),
    ];

    public static TheoryData<string, string> UriUnsafeCredentials()
    {
        var data = new TheoryData<string, string>();
        foreach (var (raw, encoded) in s_uriUnsafeCredentials)
        {
            data.Add(raw, encoded);
        }

        return data;
    }

    public static TheoryData<string> UriUnsafeCredentialValues()
    {
        var data = new TheoryData<string>();
        foreach (var (raw, _) in s_uriUnsafeCredentials)
        {
            data.Add(raw);
        }

        return data;
    }

    /// <summary>
    /// Credential values made only of RFC 3986 unreserved characters, which must be emitted verbatim.
    /// </summary>
    public static TheoryData<string> UriSafeCredentials() =>
    [
        "admin",
        "unreserved-._~",
        "Aspire123",
    ];

    [Theory]
    [MemberData(nameof(UriUnsafeCredentials))]
    public async Task MongoConnectionStringPercentEncodesUserName(string userName, string encoded)
    {
        var connectionString = await GetMongoConnectionStringAsync(userName, "simplepassword");

        Assert.Equal(
            $"mongodb://{encoded}:simplepassword@{TestHost}:{MongoPort}{MongoQuery}",
            connectionString);
    }

    [Theory]
    [MemberData(nameof(UriUnsafeCredentials))]
    public async Task MongoConnectionStringPercentEncodesPassword(string password, string encoded)
    {
        var connectionString = await GetMongoConnectionStringAsync("simpleuser", password);

        Assert.Equal(
            $"mongodb://simpleuser:{encoded}@{TestHost}:{MongoPort}{MongoQuery}",
            connectionString);
    }

    [Theory]
    [MemberData(nameof(UriUnsafeCredentials))]
    public async Task PostgresConnectionStringPercentEncodesUserName(string userName, string encoded)
    {
        var connectionString = await GetPostgresConnectionStringAsync(userName, "simplepassword");

        Assert.Equal(
            $"postgresql://{encoded}:simplepassword@{TestHost}:{PostgresPort}/postgres",
            connectionString);
    }

    [Theory]
    [MemberData(nameof(UriUnsafeCredentials))]
    public async Task PostgresConnectionStringPercentEncodesPassword(string password, string encoded)
    {
        var connectionString = await GetPostgresConnectionStringAsync("simpleuser", password);

        Assert.Equal(
            $"postgresql://simpleuser:{encoded}@{TestHost}:{PostgresPort}/postgres",
            connectionString);
    }

    [Theory]
    [MemberData(nameof(UriUnsafeCredentials))]
    public async Task DatabaseConnectionStringPercentEncodesBothCredentials(string credential, string encoded)
    {
        var connectionString = await GetMongoConnectionStringAsync(credential, credential, databaseName: "mydb");

        Assert.Equal(
            $"mongodb://{encoded}:{encoded}@{TestHost}:{MongoPort}/mydb{MongoQuery}",
            connectionString);
    }

    [Theory]
    [MemberData(nameof(UriSafeCredentials))]
    public async Task UriSafeCredentialsAreEmittedVerbatim(string credential)
    {
        var mongoConnectionString = await GetMongoConnectionStringAsync(credential, credential);
        var postgresConnectionString = await GetPostgresConnectionStringAsync(credential, credential);

        Assert.Equal(
            $"mongodb://{credential}:{credential}@{TestHost}:{MongoPort}{MongoQuery}",
            mongoConnectionString);
        Assert.Equal(
            $"postgresql://{credential}:{credential}@{TestHost}:{PostgresPort}/postgres",
            postgresConnectionString);
    }

    [Fact]
    public async Task DefaultCredentialsAreEmittedVerbatim()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var documentDB = appBuilder
            .AddDocumentDB(ResourceName)
            .WithPostgresEndpoint()
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, TestHost, MongoPort))
            .WithEndpoint("postgres", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, TestHost, PostgresPort));

        using var app = appBuilder.Build();

        var passwordParameter = Assert.IsType<ParameterResource>(documentDB.Resource.PasswordParameter);
        var password = await passwordParameter.GetValueAsync(default);
        Assert.NotNull(password);

        var mongoConnectionString = await documentDB.Resource.ConnectionStringExpression.GetValueAsync(default);
        var postgresConnectionString = await documentDB.Resource.PostgresConnectionStringExpression.GetValueAsync(default);

        // The default user name and the generated password contain only unreserved characters, so
        // introducing encoding must not change the connection strings simple setups already had.
        Assert.Equal(
            $"mongodb://admin:{password}@{TestHost}:{MongoPort}{MongoQuery}",
            mongoConnectionString);
        Assert.Equal(
            $"postgresql://admin:{password}@{TestHost}:{PostgresPort}/postgres",
            postgresConnectionString);
    }

    [Theory]
    [MemberData(nameof(UriUnsafeCredentialValues))]
    public async Task EncodedMongoConnectionStringIsParsedByTheMongoDbDriver(string credential)
    {
        var connectionString = await GetMongoConnectionStringAsync(credential, credential, databaseName: "mydb");

        var url = MongoUrl.Create(connectionString);

        Assert.Equal(credential, url.Username);
        Assert.Equal(credential, url.Password);
        Assert.Equal("mydb", url.DatabaseName);
        Assert.Equal("admin", url.AuthenticationSource);
        Assert.Equal(TestHost, url.Server.Host);
        Assert.Equal(MongoPort, url.Server.Port);
    }

    [Theory]
    [MemberData(nameof(UriUnsafeCredentialValues))]
    public async Task EncodedMongoConnectionStringPreservesUriStructure(string credential)
    {
        var connectionString = await GetMongoConnectionStringAsync(credential, credential, databaseName: "mydb");

        var uri = new Uri(connectionString);
        Assert.Equal("mongodb", uri.Scheme);
        Assert.Equal(TestHost, uri.Host);
        Assert.Equal(MongoPort, uri.Port);
        Assert.Equal("/mydb", uri.AbsolutePath);
        Assert.Equal(string.Empty, uri.Fragment);

        var userInfo = uri.UserInfo.Split(':', 2);
        Assert.Equal(2, userInfo.Length);
        Assert.Equal(credential, Uri.UnescapeDataString(userInfo[0]));
        Assert.Equal(credential, Uri.UnescapeDataString(userInfo[1]));

        var query = uri.Query.TrimStart('?').Split('&');
        Assert.Contains("authSource=admin", query);
        Assert.Contains("authMechanism=SCRAM-SHA-256", query);
        Assert.Contains("tls=true", query);
        Assert.Contains("tlsInsecure=true", query);
    }

    [Theory]
    [MemberData(nameof(UriUnsafeCredentialValues))]
    public async Task EncodedPostgresConnectionStringPreservesUriStructure(string credential)
    {
        var connectionString = await GetPostgresConnectionStringAsync(credential, credential);

        var uri = new Uri(connectionString);
        Assert.Equal("postgresql", uri.Scheme);
        Assert.Equal(TestHost, uri.Host);
        Assert.Equal(PostgresPort, uri.Port);
        Assert.Equal("/postgres", uri.AbsolutePath);
        Assert.Equal(string.Empty, uri.Query);
        Assert.Equal(string.Empty, uri.Fragment);

        var userInfo = uri.UserInfo.Split(':', 2);
        Assert.Equal(2, userInfo.Length);
        Assert.Equal(credential, Uri.UnescapeDataString(userInfo[0]));
        Assert.Equal(credential, Uri.UnescapeDataString(userInfo[1]));
    }

    [Fact]
    public void RunModeExpressionsKeepRawParameterPlaceholders()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var userName = appBuilder.AddParameter(UserParameterName, "us:er@name", secret: true);
        var password = appBuilder.AddParameter(PasswordParameterName, "p@ss/w:rd", secret: true);

        var documentDB = appBuilder
            .AddDocumentDB(ResourceName, port: null, userName: userName, password: password)
            .WithPostgresEndpoint();
        var database = documentDB.AddDatabase("db", "mydb");

        // Encoding is deferred to expression resolution, so the expression shape itself is untouched
        // and never gains a percent-encoded literal.
        Assert.Equal(
            $"mongodb://{{{UserParameterName}.value}}:{{{PasswordParameterName}.value}}@" +
            $"{{{ResourceName}.bindings.tcp.host}}:{{{ResourceName}.bindings.tcp.port}}{MongoQuery}",
            documentDB.Resource.ConnectionStringExpression.ValueExpression);

        Assert.Equal(
            $"mongodb://{{{UserParameterName}.value}}:{{{PasswordParameterName}.value}}@" +
            $"{{{ResourceName}.bindings.tcp.host}}:{{{ResourceName}.bindings.tcp.port}}/mydb{MongoQuery}",
            database.Resource.ConnectionStringExpression.ValueExpression);

        Assert.Equal(
            $"postgresql://{{{UserParameterName}.value}}:{{{PasswordParameterName}.value}}@" +
            $"{{{ResourceName}.bindings.postgres.host}}:{{{ResourceName}.bindings.postgres.port}}/postgres",
            documentDB.Resource.PostgresConnectionStringExpression.ValueExpression);
    }

    [Fact]
    public async Task PublishedManifestRoutesCredentialsThroughUriEncodedCompanions()
    {
        const string UserNameValue = "us:er@name";
        const string PasswordValue = "p@ss/w:rd";

        // Written under the test output directory, which is git-ignored.
        var outputDirectory = Path.Combine(AppContext.BaseDirectory, "manifests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDirectory);

        try
        {
            var manifestPath = Path.Combine(outputDirectory, "manifest.json");
            var appBuilder = DistributedApplication.CreateBuilder(["--publisher", "manifest", "--output-path", manifestPath]);
            var userName = appBuilder.AddParameter(UserParameterName, UserNameValue, secret: true);
            var password = appBuilder.AddParameter(PasswordParameterName, PasswordValue, secret: true);

            appBuilder
                .AddDocumentDB(ResourceName, port: null, userName: userName, password: password)
                .WithPostgresEndpoint();

            using (var app = appBuilder.Build())
            {
                await app.RunAsync();
            }

            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!;
            var resources = manifest["resources"]!;

            // Publishers cannot apply a format themselves, so Aspire materializes a companion
            // resource that carries the encoding and the connection string points at it.
            var connectionString = resources[ResourceName]!["connectionString"]!.GetValue<string>();
            Assert.Contains($"{{{UserParameterName}-uri-encoded.value}}", connectionString, StringComparison.Ordinal);
            Assert.Contains($"{{{PasswordParameterName}-uri-encoded.value}}", connectionString, StringComparison.Ordinal);

            foreach (var parameterName in new[] { UserParameterName, PasswordParameterName })
            {
                var companion = resources[$"{parameterName}-uri-encoded"];
                Assert.NotNull(companion);
                Assert.Equal("annotated.string", companion!["type"]!.GetValue<string>());
                Assert.Equal("uri", companion["filter"]!.GetValue<string>());
                Assert.Equal($"{{{parameterName}.value}}", companion["value"]!.GetValue<string>());
            }

            // The container consumes the credentials directly, so its environment keeps the raw
            // parameters, and no credential value is ever inlined into the manifest.
            var environment = resources[ResourceName]!["env"]!;
            Assert.Equal($"{{{UserParameterName}.value}}", environment["USERNAME"]!.GetValue<string>());
            Assert.Equal($"{{{PasswordParameterName}.value}}", environment["PASSWORD"]!.GetValue<string>());

            var manifestText = manifest.ToJsonString();
            Assert.DoesNotContain(UserNameValue, manifestText, StringComparison.Ordinal);
            Assert.DoesNotContain(PasswordValue, manifestText, StringComparison.Ordinal);
            Assert.DoesNotContain(Uri.EscapeDataString(UserNameValue), manifestText, StringComparison.Ordinal);
            Assert.DoesNotContain(Uri.EscapeDataString(PasswordValue), manifestText, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public void EncodingDoesNotExposeCredentialValues()
    {
        const string UserNameValue = "us:er@name";
        const string PasswordValue = "p@ss/w:rd";

        var appBuilder = DistributedApplication.CreateBuilder();
        var userName = appBuilder.AddParameter(UserParameterName, UserNameValue, secret: true);
        var password = appBuilder.AddParameter(PasswordParameterName, PasswordValue, secret: true);

        var documentDB = appBuilder
            .AddDocumentDB(ResourceName, port: null, userName: userName, password: password)
            .WithPostgresEndpoint();

        foreach (var expression in new[]
        {
            documentDB.Resource.ConnectionStringExpression.ValueExpression,
            documentDB.Resource.PostgresConnectionStringExpression.ValueExpression,
        })
        {
            Assert.DoesNotContain(UserNameValue, expression, StringComparison.Ordinal);
            Assert.DoesNotContain(PasswordValue, expression, StringComparison.Ordinal);
            Assert.DoesNotContain(Uri.EscapeDataString(UserNameValue), expression, StringComparison.Ordinal);
            Assert.DoesNotContain(Uri.EscapeDataString(PasswordValue), expression, StringComparison.Ordinal);
            Assert.DoesNotContain('%', expression);
        }
    }

    [Fact]
    public async Task ContainerCredentialEnvironmentVariablesStayUnencoded()
    {
        const string UserNameValue = "us:er@name";
        const string PasswordValue = "p@ss/w:rd";

        var appBuilder = DistributedApplication.CreateBuilder();
        var userName = appBuilder.AddParameter(UserParameterName, UserNameValue, secret: true);
        var password = appBuilder.AddParameter(PasswordParameterName, PasswordValue, secret: true);

        var documentDB = appBuilder.AddDocumentDB(ResourceName, port: null, userName: userName, password: password);

        using var app = appBuilder.Build();

        var environmentVariables = new Dictionary<string, object>(StringComparer.Ordinal);
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        foreach (var annotation in documentDB.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await annotation.Callback(
                new EnvironmentCallbackContext(executionContext, documentDB.Resource, environmentVariables, CancellationToken.None));
        }

        // The container reads these values directly; percent-encoding them would provision an
        // admin user whose name and password do not match the connection string.
        var userNameValue = await Assert.IsType<ReferenceExpression>(environmentVariables["USERNAME"]).GetValueAsync(default);
        var passwordValue = await Assert.IsType<ParameterResource>(environmentVariables["PASSWORD"]).GetValueAsync(default);

        Assert.Equal(UserNameValue, userNameValue);
        Assert.Equal(PasswordValue, passwordValue);
    }

    private static async Task<string> GetMongoConnectionStringAsync(string userName, string password, string? databaseName = null)
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var userNameParameter = appBuilder.AddParameter(UserParameterName, userName, secret: true);
        var passwordParameter = appBuilder.AddParameter(PasswordParameterName, password, secret: true);

        var documentDB = appBuilder
            .AddDocumentDB(ResourceName, port: null, userName: userNameParameter, password: passwordParameter)
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, TestHost, MongoPort));

        IResourceWithConnectionString target = databaseName is null
            ? documentDB.Resource
            : documentDB.AddDatabase("db", databaseName).Resource;

        using var app = appBuilder.Build();

        var connectionString = await target.ConnectionStringExpression.GetValueAsync(default);
        Assert.NotNull(connectionString);
        return connectionString!;
    }

    private static async Task<string> GetPostgresConnectionStringAsync(string userName, string password)
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var userNameParameter = appBuilder.AddParameter(UserParameterName, userName, secret: true);
        var passwordParameter = appBuilder.AddParameter(PasswordParameterName, password, secret: true);

        var documentDB = appBuilder
            .AddDocumentDB(ResourceName, port: null, userName: userNameParameter, password: passwordParameter)
            .WithPostgresEndpoint()
            .WithEndpoint("postgres", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, TestHost, PostgresPort));

        using var app = appBuilder.Build();

        var connectionString = await documentDB.Resource.PostgresConnectionStringExpression.GetValueAsync(default);
        Assert.NotNull(connectionString);
        return connectionString!;
    }
}
