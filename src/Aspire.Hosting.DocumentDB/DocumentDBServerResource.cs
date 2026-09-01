// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Linq;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// A resource that represents a DocumentDB container.
/// </summary>
/// <param name="name">The name of the resource.</param>
public class DocumentDBServerResource(string name) : ContainerResource(name), IResourceWithConnectionString
{
    internal const string PrimaryEndpointName = "tcp";
    internal const string PostgresEndpointName = "postgres";
    private const string DefaultUserName = "admin";
    private const string DefaultAuthenticationDatabase = "admin";
    private const string DefaultAuthenticationMechanism = "SCRAM-SHA-256";
    private const string DefaultPostgresDatabase = "postgres";

    private EndpointReference? _primaryEndpoint;
    private EndpointReference? _postgresEndpoint;

    /// <summary>
    /// Initialize a resource that represents a DocumentDB container.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="userNameParameter">A parameter that contains the DocumentDB server user name, or <see langword="null"/> to use a default value.</param>
    /// <param name="passwordParameter">A parameter that contains the DocumentDB server password.</param>
    public DocumentDBServerResource(string name, ParameterResource? userNameParameter, ParameterResource? passwordParameter) : this(name)
    {
        UserNameParameter = userNameParameter;
        PasswordParameter = passwordParameter;
    }

    /// <summary>
    /// Gets the primary endpoint for the DocumentDB server.
    /// </summary>
    public EndpointReference PrimaryEndpoint => _primaryEndpoint ??= new(this, PrimaryEndpointName);

    /// <summary>
    /// Gets the PostgreSQL backend endpoint for the DocumentDB server.
    /// </summary>
    /// <remarks>
    /// This endpoint is only allocated when
    /// <see cref="Aspire.Hosting.DocumentDBBuilderExtensions.WithPostgresEndpoint"/>
    /// has been called on the resource builder. Accessing
    /// <see cref="PostgresConnectionStringExpression"/> without first opting in
    /// throws an <see cref="System.InvalidOperationException"/>.
    /// </remarks>
    public EndpointReference PostgresEndpoint => _postgresEndpoint ??= new(this, PostgresEndpointName);

    /// <summary>
    /// Gets the parameter that contains the DocumentDB server password.
    /// </summary>
    public ParameterResource? PasswordParameter { get; }

    /// <summary>
    /// Gets the parameter that contains the DocumentDB server username.
    /// </summary>
    public ParameterResource? UserNameParameter { get; }

    /// <summary>
    /// Gets a value indicating whether to use TLS.
    /// </summary>
    internal bool TLS { get; set; } = true;

    /// <summary>
    /// Gets a value indicating whether to allow invalid certificates.
    /// </summary>
    internal bool AllowInsecureTls { get; set; } = true;

    /// <summary>
    /// Selected DocumentDB version, or <see langword="null"/> when the default
    /// (<see cref="DocumentDBVersions.Latest"/>) should be used. Internal-only because consumers
    /// must not rely on this as the source of truth: a free-form
    /// <c>WithImageTag(...)</c>/<c>WithImage(...)</c> call can override the actual container tag
    /// without touching this field.
    /// </summary>
    internal DocumentDBVersion? Version { get; private set; }

    /// <summary>
    /// Selected PostgreSQL backend variant. Defaults to <see cref="DocumentDBPostgresVersion.Pg17"/>.
    /// Internal-only for the same reason as <see cref="Version"/>.
    /// </summary>
    internal DocumentDBPostgresVersion PgVersion { get; private set; } = DocumentDBPostgresVersion.Pg17;

    /// <summary>
    /// The raw, unencoded user name. This is the form the container expects in its
    /// <c>USERNAME</c> environment variable, so it must never be percent-encoded.
    /// Use <see cref="AppendUserInfo"/> for the URI forms.
    /// </summary>
    internal ReferenceExpression UserNameReference =>
        UserNameParameter is not null ?
            ReferenceExpression.Create($"{UserNameParameter}") :
            ReferenceExpression.Create($"{DefaultUserName}");

    /// <summary>
    /// Appends the <c>user:password@</c> userinfo component to <paramref name="builder"/>,
    /// percent-encoding both credentials so that arbitrary values remain unambiguous inside the
    /// <c>mongodb://</c> and <c>postgresql://</c> URI grammars.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Encoding is delegated to Aspire's <c>uri</c> string format, which applies
    /// <see cref="Uri.EscapeDataString(string)"/> when the expression is resolved. The integration
    /// therefore never reads the credential values itself, and
    /// <see cref="ReferenceExpression.ValueExpression"/> keeps the plain <c>{parameter.value}</c>
    /// placeholders. When the model is published, Aspire projects each formatted parameter onto an
    /// <c>annotated.string</c> companion resource (<c>{parameter}-uri-encoded</c>) that carries the
    /// <c>uri</c> filter, so no secret is inlined into the manifest. The publisher consuming that
    /// manifest implements the filter, so the escaping applied at deployment time is its own; see
    /// <see href="https://github.com/microsoft/azure-databases-aspire/blob/main/docs/configuration.md#credential-encoding">the
    /// credential-encoding documentation</see> for the known divergences.
    /// </para>
    /// <para>
    /// Values consisting only of RFC 3986 unreserved characters (<c>A-Z a-z 0-9 - . _ ~</c>) are
    /// emitted verbatim. That covers the default <c>admin</c> user name and the password
    /// <see cref="Aspire.Hosting.DocumentDBBuilderExtensions.AddDocumentDB(IDistributedApplicationBuilder, string, int?, IResourceBuilder{ParameterResource}, IResourceBuilder{ParameterResource})"/>
    /// generates when none is supplied, so simple-credential connection strings are byte-identical
    /// to the ones produced before encoding was introduced.
    /// </para>
    /// <para>
    /// The container's own <c>USERNAME</c> / <c>PASSWORD</c> environment variables are deliberately
    /// left unencoded — see <see cref="UserNameReference"/>.
    /// </para>
    /// </remarks>
    private void AppendUserInfo(ReferenceExpressionBuilder builder, ParameterResource passwordParameter)
    {
        if (UserNameParameter is not null)
        {
            builder.Append($"{UserNameParameter:uri}");
        }
        else
        {
            builder.Append($"{DefaultUserName:uri}");
        }

        builder.Append($":{passwordParameter:uri}@");
    }

    /// <summary>
    /// Gets the connection string for the DocumentDB server.
    /// </summary>
    /// <remarks>
    /// The user name and password are percent-encoded into the URI userinfo component. Aspire
    /// applies RFC 3986 escaping (<see cref="Uri.EscapeDataString(string)"/>) when it resolves this
    /// reference, so MongoDB clients decode the original values back. In a published manifest the
    /// escaping is instead performed by the downstream publisher, which does not necessarily match
    /// RFC 3986; see
    /// <see href="https://github.com/microsoft/azure-databases-aspire/blob/main/docs/configuration.md#credential-encoding">Credential
    /// encoding</see> for publisher-specific behavior, including the <c>azd</c> limitation for
    /// credentials containing spaces.
    /// </remarks>
    public ReferenceExpression ConnectionStringExpression => BuildConnectionString();

    /// <summary>
    /// Gets the PostgreSQL connection string for the DocumentDB server in the standard
    /// <c>postgresql://user:password@host:port/postgres</c> URI form.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The PostgreSQL backend port (<c>9712</c>) must be exposed via
    /// <see cref="Aspire.Hosting.DocumentDBBuilderExtensions.WithPostgresEndpoint"/>
    /// before this property is accessed; otherwise an
    /// <see cref="System.InvalidOperationException"/> is thrown.
    /// </para>
    /// <para>
    /// The default database in the URI is <c>postgres</c>, matching the upstream
    /// <c>documentdb-local</c> entrypoint, which connects with <c>-d postgres</c>.
    /// Credentials come from the same <see cref="UserNameParameter"/> /
    /// <see cref="PasswordParameter"/> used by the MongoDB gateway because the
    /// container creates a single admin user shared by both surfaces, and they are
    /// percent-encoded exactly as they are in
    /// <see cref="ConnectionStringExpression"/>, with the same publisher-specific
    /// behavior when the model is published.
    /// </para>
    /// <para>
    /// No <c>sslmode</c> query parameter is added, because the bundled
    /// PostgreSQL server is started with <c>ssl = off</c>; the
    /// <see cref="TLS"/> field of this resource only controls the MongoDB
    /// gateway. Consumers needing TLS on the PostgreSQL side should append
    /// <c>?sslmode=...</c> themselves.
    /// </para>
    /// </remarks>
    public ReferenceExpression PostgresConnectionStringExpression => BuildPostgresConnectionString();

    internal ReferenceExpression BuildConnectionString(string? databaseName = null)
    {
        var builder = new ReferenceExpressionBuilder();
        builder.AppendLiteral("mongodb://");

        if (PasswordParameter is not null)
        {
            AppendUserInfo(builder, PasswordParameter);
        }

        builder.Append($"{PrimaryEndpoint.Property(EndpointProperty.HostAndPort)}");

        if (databaseName is not null)
        {
            builder.Append($"/{databaseName}");
        }

        var hasQuery = false;
        if (PasswordParameter is not null)
        {
            builder.Append($"?authSource={DefaultAuthenticationDatabase}&authMechanism={DefaultAuthenticationMechanism}");
            hasQuery = true;
        }

        // Conditionally add TLS and AllowInsecureTls.
        // NOTE: We use "tlsInsecure=true" rather than the upstream-documented
        // "tlsAllowInvalidCertificates=true" because the .NET MongoDB driver
        // does not fully honour tlsAllowInvalidCertificates for self-signed
        // certificates and raises UntrustedRoot errors. tlsInsecure=true
        // disables both certificate validation and hostname verification,
        // which is the correct setting for local DocumentDB containers.
        if (TLS)
        {
            builder.Append($"{(hasQuery ? "&" : "?")}tls=true");
            hasQuery = true;

            if (AllowInsecureTls)
            {
                builder.Append($"&tlsInsecure=true");
            }
        }

        return builder.Build();
    }

    internal ReferenceExpression BuildPostgresConnectionString()
    {
        if (!Annotations.OfType<EndpointAnnotation>().Any(e => e.Name == PostgresEndpointName))
        {
            throw new InvalidOperationException(
                $"The PostgreSQL endpoint on resource '{Name}' has not been added. " +
                $"Call '{nameof(Aspire.Hosting.DocumentDBBuilderExtensions.WithPostgresEndpoint)}()' on " +
                $"the DocumentDB resource builder before accessing '{nameof(PostgresConnectionStringExpression)}'.");
        }

        var builder = new ReferenceExpressionBuilder();
        builder.AppendLiteral("postgresql://");

        if (PasswordParameter is not null)
        {
            AppendUserInfo(builder, PasswordParameter);
        }

        builder.Append($"{PostgresEndpoint.Property(EndpointProperty.HostAndPort)}");
        builder.AppendLiteral($"/{DefaultPostgresDatabase}");

        return builder.Build();
    }

    private readonly List<DocumentDBDatabaseResource> _databases = new();

    /// <summary>
    /// The databases created under this server.
    /// </summary>
    public IReadOnlyList<DocumentDBDatabaseResource> Databases => _databases;

    internal void AddDatabase(DocumentDBDatabaseResource database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _databases.Add(database);
    }

    internal void SetUseTls(bool useTls)
        => TLS = useTls;

    internal void SetAllowInsecureTls(bool allowInsecureTls)
    {
        AllowInsecureTls = allowInsecureTls;
    }

    internal void SetVersion(DocumentDBVersion version)
    {
        Version = version;
    }

    internal void SetPgVersion(DocumentDBPostgresVersion pgVersion)
    {
        PgVersion = pgVersion;
    }

    /// <summary>
    /// Computes the container image tag (<c>pgN-X.Y.Z</c>) from the current
    /// <see cref="Version"/> and <see cref="PgVersion"/> selections, falling back to
    /// <see cref="DocumentDBVersions.Latest"/> when no DocumentDB version has been explicitly set.
    /// </summary>
    internal string ComputeImageTag()
    {
        var versionString = Version is { } v
            ? DocumentDBVersions.ToVersionString(v)
            : DocumentDBVersions.Latest;
        return $"pg{(int)PgVersion}-{versionString}";
    }
}
