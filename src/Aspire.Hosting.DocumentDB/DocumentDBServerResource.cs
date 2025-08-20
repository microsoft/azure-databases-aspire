// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// A resource that represents a DocumentDB container.
/// </summary>
/// <param name="name">The name of the resource.</param>
public class DocumentDBServerResource(string name) : ContainerResource(name), IResourceWithConnectionString
{
    internal const string PrimaryEndpointName = "tcp";
    private const string DefaultUserName = "admin";
    private const string DefaultAuthenticationDatabase = "admin";
    private const string DefaultAuthenticationMechanism = "SCRAM-SHA-256";

    private EndpointReference? _primaryEndpoint;

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
    internal bool TLS { get; set; }

    /// <summary>
    /// Gets a value indicating whether to allow invalid certificates.
    /// </summary>
    internal bool AllowInsecureTls { get; set; }

    internal ReferenceExpression UserNameReference =>
        UserNameParameter is not null ?
            ReferenceExpression.Create($"{UserNameParameter}") :
            ReferenceExpression.Create($"{DefaultUserName}");

    /// <summary>
    /// Gets the connection string for the DocumentDB server.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression => BuildConnectionString();

    internal ReferenceExpression BuildConnectionString(string? databaseName = null)
    {
        var builder = new ReferenceExpressionBuilder();
        builder.AppendLiteral("mongodb://");

        if (PasswordParameter is not null)
        {
            builder.Append($"{UserNameReference}:{PasswordParameter}@");
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

        // Conditionally add TLS and AllowInsecureTls
        if (TLS)
        {
            builder.Append($"{(hasQuery ? "&" : "?")}tls=true");
            hasQuery = true;
        }

        if (AllowInsecureTls)
        {
            builder.Append($"{(hasQuery ? "&" : "?")}tlsInsecure=true");
        }

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
    {
        TLS = useTls;
    }

    internal void SetAllowInsecureTls(bool allowInsecureTls)
    {
        AllowInsecureTls = allowInsecureTls;
    }
}
