// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.DocumentDB;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding DocumentDB resources to an <see cref="IDistributedApplicationBuilder"/>.
/// </summary>
public static class DocumentDBBuilderExtensions
{
    // default internal port is 10260.
    private const int DefaultContainerPort = 10260;
    // default PostgreSQL coordinator port inside the documentdb-local container.
    private const int DefaultPostgresContainerPort = 9712;
    private const string DefaultHealthCheckDatabaseName = "admin";

    private const string UserEnvVarName = "USERNAME";
    private const string PasswordEnvVarName = "PASSWORD";
    private const string LogLevelEnvVarName = "LOG_LEVEL";
    private const string InitDataPathEnvVarName = "INIT_DATA_PATH";
    private const string SkipInitDataEnvVarName = "SKIP_INIT_DATA";
    private const string CertPathEnvVarName = "CERT_PATH";
    private const string KeyFileEnvVarName = "KEY_FILE";
    private const string EnableTelemetryEnvVarName = "ENABLE_TELEMETRY";
    private const string OtelMetricsEnabledEnvVarName = "OTEL_METRICS_ENABLED";
    private const string OtelExporterOtlpMetricsEndpointEnvVarName = "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT";
    private const string OtelExporterOtlpMetricsTimeoutEnvVarName = "OTEL_EXPORTER_OTLP_METRICS_TIMEOUT";
    private const string OtelMetricExportIntervalEnvVarName = "OTEL_METRIC_EXPORT_INTERVAL";
    private const string OtelServiceNameEnvVarName = "OTEL_SERVICE_NAME";
    private const string OtelServiceVersionEnvVarName = "OTEL_SERVICE_VERSION";
    private const string OwnerEnvVarName = "OWNER";
    private const string DataPathEnvVarName = "DATA_PATH";
    private const string DisableExtendedRumEnvVarName = "DISABLE_EXTENDED_RUM";
    private const string CreateUserEnvVarName = "CREATE_USER";
    private const string AllowExternalConnectionsEnvVarName = "ALLOW_EXTERNAL_CONNECTIONS";

    private const string DefaultMountedDataPath = "/data";
    private const string InitDataMountPath = "/init_doc_db.d";

    /// <summary>
    /// Adds a DocumentDB resource to the application model. A container is used for local development.
    /// </summary>
    /// <remarks>
    /// This resource includes a built-in health check. When this resource is referenced as a dependency
    /// using the <see cref="ResourceBuilderExtensions.WaitFor{T}(IResourceBuilder{T}, IResourceBuilder{IResource})"/>
    /// extension method then the dependent resource will wait until the DocumentDB server responds to ping.
    /// This version of the package defaults to the <inheritdoc cref="DocumentDBContainerImageTags.Tag"/> tag of the <inheritdoc cref="DocumentDBContainerImageTags.Image"/> container image.
    /// </remarks>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
    /// <param name="port">The host port for DocumentDB.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <example>
    /// <code>
    /// var server = builder.AddDocumentDB("documentdb", port: 10260);
    /// </code>
    /// </example>
    public static IResourceBuilder<DocumentDBServerResource> AddDocumentDB(this IDistributedApplicationBuilder builder, [ResourceName] string name, int? port)
        => AddDocumentDB(builder, name, port, null, null);

    /// <summary>
    /// <inheritdoc cref="AddDocumentDB(IDistributedApplicationBuilder, string, int?)"/>
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
    /// <param name="port">The host port for DocumentDB.</param>
    /// <param name="userName">A parameter that contains the DocumentDB server user name, or <see langword="null"/> to use a default value.</param>
    /// <param name="password">A parameter that contains the DocumentDB server password, or <see langword="null"/> to use a generated password.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <example>
    /// <code>
    /// // Minimal usage with generated credentials:
    /// var server = builder.AddDocumentDB("documentdb");
    /// var database = server.AddDatabase("mydb");
    ///
    /// // With custom credentials:
    /// var user = builder.AddParameter("db-user");
    /// var pass = builder.AddParameter("db-pass", secret: true);
    /// var securedServer = builder.AddDocumentDB("documentdb", userName: user, password: pass);
    /// </code>
    /// </example>
    public static IResourceBuilder<DocumentDBServerResource> AddDocumentDB(this IDistributedApplicationBuilder builder,
        string name,
        int? port = null,
        IResourceBuilder<ParameterResource>? userName = null,
        IResourceBuilder<ParameterResource>? password = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var passwordParameter = password?.Resource ?? ParameterResourceBuilderExtensions.CreateDefaultPasswordParameter(builder, $"{name}-password", special: false);

        var DocumentDBContainer = new DocumentDBServerResource(name, userName?.Resource, passwordParameter);

        string? connectionString = null;

        builder.Eventing.Subscribe<ConnectionStringAvailableEvent>(DocumentDBContainer, async (@event, ct) =>
        {
            connectionString = await DocumentDBContainer.ConnectionStringExpression.GetValueAsync(ct).ConfigureAwait(false)
                ?? throw new DistributedApplicationException($"ConnectionStringAvailableEvent was published for the '{DocumentDBContainer.Name}' resource but the connection string was null.");
        });

        var healthCheckKey = $"{name}_check";
        // Use a database-scoped check so the MongoDB health check package executes a ping command.
        IMongoDatabase? database = null;
        builder.Services.AddHealthChecks()
            .AddMongoDb(
                _ => database ??=
                    new MongoClient(connectionString ?? throw new InvalidOperationException("Connection string is unavailable"))
                        .GetDatabase(DefaultHealthCheckDatabaseName),
                name: healthCheckKey);

        return builder
            .AddResource(DocumentDBContainer)
            .WithEndpoint(port: port, targetPort: DefaultContainerPort, name: DocumentDBServerResource.PrimaryEndpointName)
            .WithImage(DocumentDBContainerImageTags.Image, DocumentDBContainerImageTags.Tag)
            .WithImageRegistry(DocumentDBContainerImageTags.Registry)
            .WithEnvironment(context =>
            {
                context.EnvironmentVariables[UserEnvVarName] = DocumentDBContainer.UserNameReference;
                context.EnvironmentVariables[PasswordEnvVarName] = DocumentDBContainer.PasswordParameter!;
            })
            .WithHealthCheck(healthCheckKey);
    }

    /// <summary>
    /// Adds a DocumentDB database to the application model.
    /// </summary>
    /// <remarks>
    /// The database resource inherits the parent server's connection string and appends the database name.
    /// Services should reference the database resource (not the server) via <c>.WithReference(db)</c>.
    /// This resource includes a built-in health check. When this resource is referenced as a dependency
    /// using the <see cref="ResourceBuilderExtensions.WaitFor{T}(IResourceBuilder{T}, IResourceBuilder{IResource})"/>
    /// extension method then the dependent resource will wait until the DocumentDB database responds to ping.
    /// </remarks>
    /// <param name="builder">The DocumentDB server resource builder.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
    /// <param name="databaseName">The name of the database. If not provided, this defaults to the same value as <paramref name="name"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <example>
    /// <code>
    /// var server = builder.AddDocumentDB("documentdb");
    /// var ordersDb = server.AddDatabase("orders");
    /// var usersDb = server.AddDatabase("users");
    /// </code>
    /// </example>
    public static IResourceBuilder<DocumentDBDatabaseResource> AddDatabase(this IResourceBuilder<DocumentDBServerResource> builder, [ResourceName] string name, string? databaseName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        // Use the resource name as the database name if it's not provided
        databaseName ??= name;

        var DocumentDBDatabase = new DocumentDBDatabaseResource(name, databaseName, builder.Resource);
        builder.Resource.AddDatabase(DocumentDBDatabase);

        string? connectionString = null;

        builder.ApplicationBuilder.Eventing.Subscribe<ConnectionStringAvailableEvent>(DocumentDBDatabase, async (@event, ct) =>
        {
            connectionString = await DocumentDBDatabase.ConnectionStringExpression.GetValueAsync(ct).ConfigureAwait(false)
                ?? throw new DistributedApplicationException($"ConnectionStringAvailableEvent was published for the '{DocumentDBDatabase.Name}' resource but the connection string was null.");
        });

        var healthCheckKey = $"{name}_check";
        // cache the database instance so it is reused on subsequent calls to the health check
        IMongoDatabase? database = null;
        builder.ApplicationBuilder.Services.AddHealthChecks()
            .AddMongoDb(
                _ => database ??=
                    new MongoClient(connectionString ?? throw new InvalidOperationException("Connection string is unavailable"))
                        .GetDatabase(databaseName),
                name: healthCheckKey);

        return builder.ApplicationBuilder
            .AddResource(DocumentDBDatabase)
            .WithHealthCheck(healthCheckKey);
    }

    /// <summary>
    /// Configures the host port that the DocumentDB resource is exposed on instead of using randomly assigned port.
    /// </summary>
    /// <param name="builder">The resource builder for DocumentDB.</param>
    /// <param name="port">The port to bind on the host. If <see langword="null"/> is used random port will be assigned.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <example>
    /// <code>
    /// var server = builder.AddDocumentDB("documentdb")
    ///                     .WithHostPort(10260);
    /// </code>
    /// </example>
    public static IResourceBuilder<DocumentDBServerResource> WithHostPort(this IResourceBuilder<DocumentDBServerResource> builder, int? port)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithEndpoint(DocumentDBServerResource.PrimaryEndpointName, endpoint =>
        {
            endpoint.Port = port;
        });
    }

    /// <summary>
    /// Exposes the PostgreSQL backend coordinator port of the DocumentDB Local container
    /// (default container port <c>9712</c>) as a second endpoint on the resource.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>documentdb-local</c> container bundles a MongoDB-compatible gateway and a
    /// PostgreSQL coordinator listening on separate ports. By default this integration only
    /// publishes the gateway port (<c>10260</c>) and surfaces a <c>mongodb://</c> connection
    /// string. Calling <see cref="WithPostgresEndpoint"/> additionally publishes the
    /// PostgreSQL port so consumers can use psql/Npgsql/etc. directly, and enables
    /// <see cref="DocumentDBServerResource.PostgresConnectionStringExpression"/>.
    /// </para>
    /// <para>
    /// The endpoint uses the same <c>userName</c>/<c>password</c> parameters as the gateway
    /// because the container provisions a single admin user shared by both surfaces.
    /// The default database in the resulting URI is <c>postgres</c>, matching the upstream
    /// entrypoint, which connects with <c>-d postgres</c>.
    /// </para>
    /// </remarks>
    /// <param name="builder">The resource builder for DocumentDB.</param>
    /// <param name="port">
    /// The host port to bind to. If <see langword="null"/> a random port is assigned.
    /// </param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <example>
    /// <code>
    /// var documentDB = builder.AddDocumentDB("documentdb")
    ///                         .WithPostgresEndpoint();
    ///
    /// builder.AddProject&lt;Projects.Worker&gt;("worker")
    ///        .WithEnvironment("ConnectionStrings__pg", documentDB.Resource.PostgresConnectionStringExpression);
    /// </code>
    /// </example>
    public static IResourceBuilder<DocumentDBServerResource> WithPostgresEndpoint(
        this IResourceBuilder<DocumentDBServerResource> builder,
        int? port = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.Resource.Annotations.OfType<EndpointAnnotation>()
                .Any(e => e.Name == DocumentDBServerResource.PostgresEndpointName))
        {
            throw new InvalidOperationException(
                $"A PostgreSQL endpoint has already been added to resource '{builder.Resource.Name}'. " +
                $"Call '{nameof(WithPostgresEndpoint)}()' at most once per DocumentDB resource.");
        }

        return builder
            .WithEndpoint(
                port: port,
                targetPort: DefaultPostgresContainerPort,
                scheme: "postgresql",
                name: DocumentDBServerResource.PostgresEndpointName)
            .WithEnvironment(context =>
            {
                // Explicitly opt the upstream entrypoint into accepting external PostgreSQL
                // connections (sets PGOPTIONS=-e -> listen_addresses='*' + permissive pg_hba.conf).
                // Setting this is required so publishing the host port produces a reachable
                // server even on upstream container builds where the entrypoint's default
                // ALLOW_EXTERNAL_CONNECTIONS handling is corrected.
                context.EnvironmentVariables[AllowExternalConnectionsEnvVarName] = "true";
            });
    }

    /// <summary>
    /// Adds a named volume for the data folder to a DocumentDB container resource.
    /// </summary>
    /// <remarks>
    /// Without a volume, all data is stored inside the container and lost when it stops.
    /// The bare DocumentDB container defaults <c>DATA_PATH</c> to <c>/data</c>.
    /// This helper mounts the volume at <paramref name="targetPath"/> and sets
    /// <c>DATA_PATH</c> to the same value so DocumentDB writes to the mounted directory.
    /// </remarks>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the volume. Defaults to an auto-generated name based on the application and resource names.</param>
    /// <param name="isReadOnly">A flag that indicates if this is a read-only volume.</param>
    /// <param name="targetPath">The target path inside the container. Defaults to /data to match the container default when this helper is used.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <example>
    /// <code>
    /// var server = builder.AddDocumentDB("documentdb")
    ///                     .WithDataVolume();
    /// </code>
    /// </example>
    public static IResourceBuilder<DocumentDBServerResource> WithDataVolume(
        this IResourceBuilder<DocumentDBServerResource> builder,
        string? name = null,
        bool isReadOnly = false,
        string? targetPath = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        targetPath ??= DefaultMountedDataPath;

        return builder
            .WithVolume(name ?? VolumeNameGenerator.Generate(builder, "data"), targetPath, isReadOnly)
            .WithEnvironment(context =>
            {
                context.EnvironmentVariables[DataPathEnvVarName] = targetPath;
            });
    }

    /// <summary>
    /// Adds a bind mount for the data folder to a DocumentDB container resource.
    /// </summary>
    /// <remarks>
    /// Prefer <see cref="WithDataVolume"/> for most cases. Bind mounts are useful when you need
    /// direct access to the data files on the host filesystem.
    /// The bare DocumentDB container defaults <c>DATA_PATH</c> to <c>/data</c>.
    /// This helper mounts the directory at <c>/data</c> (the container default) and sets
    /// <c>DATA_PATH</c> to the same value so DocumentDB writes to the mounted directory.
    /// </remarks>
    /// <param name="builder">The resource builder.</param>
    /// <param name="source">The source directory on the host to mount into the container.</param>
    /// <param name="isReadOnly">A flag that indicates if this is a read-only mount.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <example>
    /// <code>
    /// var server = builder.AddDocumentDB("documentdb")
    ///                     .WithDataBindMount("./data/documentdb");
    /// </code>
    /// </example>
    public static IResourceBuilder<DocumentDBServerResource> WithDataBindMount(this IResourceBuilder<DocumentDBServerResource> builder, string source, bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(source);

        const string targetPath = DefaultMountedDataPath;

        return builder
            .WithBindMount(source, targetPath, isReadOnly)
            .WithEnvironment(context =>
            {
                context.EnvironmentVariables[DataPathEnvVarName] = targetPath;
            });
    }

    /// <summary>
    /// Configures the DocumentDB Local container log level.
    /// </summary>
    /// <param name="builder">The resource builder for DocumentDB.</param>
    /// <param name="logLevel">The log level to configure.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<DocumentDBServerResource> WithLogLevel(this IResourceBuilder<DocumentDBServerResource> builder, DocumentDBLogLevel logLevel)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithEnvironment(context =>
        {
            context.EnvironmentVariables[LogLevelEnvVarName] = logLevel.ToEnvironmentValue();
        });
    }

    /// <summary>
    /// Mounts custom initialization scripts into the DocumentDB Local container.
    /// </summary>
    /// <remarks>
    /// The provided directory is bind-mounted at <c>/init_doc_db.d</c>, and the built-in sample data
    /// initialization is implicitly disabled so the mounted scripts are the only initialization source.
    /// </remarks>
    /// <param name="builder">The resource builder for DocumentDB.</param>
    /// <param name="source">The source directory on the host to mount into the container.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<DocumentDBServerResource> WithInitData(this IResourceBuilder<DocumentDBServerResource> builder, string source)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(source);

        return builder
            .WithBindMount(source, InitDataMountPath, isReadOnly: true)
            .WithEnvironment(context =>
            {
                context.EnvironmentVariables[InitDataPathEnvVarName] = InitDataMountPath;
                context.EnvironmentVariables[SkipInitDataEnvVarName] = "true";
            });
    }

    /// <summary>
    /// Disables the built-in sample data initialization performed by the DocumentDB Local container.
    /// </summary>
    /// <param name="builder">The resource builder for DocumentDB.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<DocumentDBServerResource> WithoutSampleData(this IResourceBuilder<DocumentDBServerResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithEnvironment(context =>
        {
            context.EnvironmentVariables[SkipInitDataEnvVarName] = "true";
        });
    }

    /// <summary>
    /// Disables the <c>extended_rum</c> index access method in the DocumentDB Local container
    /// by setting <c>DISABLE_EXTENDED_RUM=true</c>.
    /// </summary>
    /// <remarks>
    /// Available in DocumentDB <c>v0.111-0</c> and later. On older container images the
    /// environment variable is ignored.
    /// </remarks>
    /// <param name="builder">The resource builder for DocumentDB.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<DocumentDBServerResource> WithoutExtendedRum(this IResourceBuilder<DocumentDBServerResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithEnvironment(context =>
        {
            context.EnvironmentVariables[DisableExtendedRumEnvVarName] = "true";
        });
    }

    /// <summary>
    /// Disables the DocumentDB Local container's automatic user creation by setting the
    /// upstream <c>CREATE_USER=false</c> environment variable.
    /// </summary>
    /// <remarks>
    /// Use only after a previous run has already created the user in persisted storage
    /// (<see cref="WithDataVolume"/> / <see cref="WithDataBindMount"/>). Setting
    /// <c>CREATE_USER=false</c> on a fresh container will cause init-data steps to fail
    /// authentication and the container entrypoint to exit non-zero. To avoid spurious
    /// init-data runs on subsequent starts, also call <see cref="WithoutSampleData"/>.
    /// <para>
    /// <strong>Important:</strong> The container's init-data scripts (both built-in sample data
    /// and custom scripts mounted via <see cref="WithInitData"/>) authenticate using the
    /// configured credentials. If the user does not exist because creation was skipped,
    /// these scripts will fail and the container will exit. Always pair this method with
    /// <see cref="WithoutSampleData"/> and ensure the user already exists in the persisted data.
    /// </para>
    /// </remarks>
    /// <param name="builder">The resource builder for DocumentDB.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<DocumentDBServerResource> WithoutUserCreation(this IResourceBuilder<DocumentDBServerResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithEnvironment(context =>
        {
            context.EnvironmentVariables[CreateUserEnvVarName] = "false";
        });
    }

    /// <summary>
    /// Mounts a custom TLS certificate and key into the DocumentDB Local container.
    /// </summary>
    /// <remarks>
    /// The certificate and key files are mounted at distinct container paths so that
    /// they do not collide even if their host file names are identical.
    /// </remarks>
    /// <param name="builder">The resource builder for DocumentDB.</param>
    /// <param name="certPath">The certificate file to mount into the container.</param>
    /// <param name="keyPath">The private key file to mount into the container.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<DocumentDBServerResource> WithTlsCertificate(this IResourceBuilder<DocumentDBServerResource> builder, string certPath, string keyPath)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(certPath);
        ArgumentException.ThrowIfNullOrEmpty(keyPath);

        var certTargetPath = GetMountedFilePath(certPath, nameof(certPath), "documentdb-cert-");
        var keyTargetPath = GetMountedFilePath(keyPath, nameof(keyPath), "documentdb-key-");

        return builder
            .WithBindMount(certPath, certTargetPath, isReadOnly: true)
            .WithBindMount(keyPath, keyTargetPath, isReadOnly: true)
            .WithEnvironment(context =>
            {
                context.EnvironmentVariables[CertPathEnvVarName] = certTargetPath;
                context.EnvironmentVariables[KeyFileEnvVarName] = keyTargetPath;
            });
    }

    /// <summary>
    /// Enables or disables DocumentDB Local telemetry by setting the <c>ENABLE_TELEMETRY</c>
    /// environment variable.
    /// </summary>
    /// <param name="builder">The resource builder for DocumentDB.</param>
    /// <param name="enabled">Whether telemetry should be enabled.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// The <c>ENABLE_TELEMETRY</c> environment variable is not consumed by the DocumentDB gateway
    /// in container image v0.112-0 or later. On those images this method has no observable effect
    /// on the running container. Use <see cref="WithOpenTelemetryMetrics(IResourceBuilder{DocumentDBServerResource}, string?, bool, TimeSpan?, TimeSpan?, string?, string?)"/>
    /// to configure OTLP metrics export.
    /// </remarks>
    [Obsolete(
        "ENABLE_TELEMETRY is not consumed by the DocumentDB gateway in container image v0.112-0 " +
        "or later, so this method has no observable effect on those images. Use " +
        "WithOpenTelemetryMetrics(...) for OTLP metrics. This member is kept for binary " +
        "compatibility and may be removed in a future release.",
        error: false,
        DiagnosticId = "ASPIREDOCDB0001",
        UrlFormat = "https://github.com/microsoft/azure-databases-aspire/blob/main/docs/configuration.md#withtelemetry-obsolete")]
    public static IResourceBuilder<DocumentDBServerResource> WithTelemetry(this IResourceBuilder<DocumentDBServerResource> builder, bool enabled = true)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithEnvironment(context =>
        {
            context.EnvironmentVariables[EnableTelemetryEnvVarName] = enabled ? "true" : "false";
        });
    }

    /// <summary>
    /// Enables OpenTelemetry metrics export from the DocumentDB gateway via OTLP/gRPC.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Requires container image v0.112-0 or later. Only metrics are exported today;
    /// traces and logs are not yet supported by the gateway.
    /// </para>
    /// <para>
    /// The container default for <c>OTEL_METRICS_ENABLED</c> is <c>false</c>; calling this method
    /// flips it to <c>true</c> unless <paramref name="enabled"/> is explicitly set to <c>false</c>.
    /// </para>
    /// <para>
    /// Merge semantics across multiple calls on the same builder:
    /// <list type="bullet">
    ///   <item>
    ///     <paramref name="enabled"/> is non-nullable and is therefore written on every call.
    ///     The last call's value wins (defaulting to <c>true</c> when omitted), even if a
    ///     previous call set it to <c>false</c>.
    ///   </item>
    ///   <item>
    ///     All other parameters are nullable; later calls override only the environment variables
    ///     they explicitly set, and values from earlier calls are preserved for parameters left
    ///     at <see langword="null"/>.
    ///   </item>
    /// </list>
    /// </para>
    /// <para>
    /// When <paramref name="endpoint"/> is omitted, the gateway falls back to the standard OTLP/gRPC
    /// default (<c>http://localhost:4317</c>). In an Aspire container scenario, that fallback is
    /// rarely reachable, so an explicit endpoint pointing to your collector is recommended.
    /// </para>
    /// <para>
    /// <paramref name="exportInterval"/> and <paramref name="timeout"/> are written as integer
    /// milliseconds via <see cref="CultureInfo.InvariantCulture"/>. Values smaller than one
    /// millisecond (sub-ms ticks) truncate to <c>0</c>; callers should pass whole-millisecond or
    /// larger granularities.
    /// </para>
    /// </remarks>
    /// <param name="builder">The resource builder for DocumentDB.</param>
    /// <param name="endpoint">
    /// OTLP/gRPC endpoint of the collector that should receive metrics. When provided, sets
    /// <c>OTEL_EXPORTER_OTLP_METRICS_ENDPOINT</c> (which takes precedence over the generic
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> per the OpenTelemetry specification). Defaults to
    /// <see langword="null"/> (leave the environment variable unset; gateway falls back to its
    /// own default).
    /// </param>
    /// <param name="enabled">
    /// Whether metrics export is enabled. Sets <c>OTEL_METRICS_ENABLED</c>. Defaults to
    /// <see langword="true"/>: opting into this method clearly indicates the caller wants metrics on.
    /// </param>
    /// <param name="exportInterval">
    /// How often the gateway flushes accumulated metrics to the collector. When provided, sets
    /// <c>OTEL_METRIC_EXPORT_INTERVAL</c> (milliseconds, integer). Must be non-negative.
    /// </param>
    /// <param name="timeout">
    /// Per-export request timeout. When provided, sets <c>OTEL_EXPORTER_OTLP_METRICS_TIMEOUT</c>
    /// (milliseconds, integer). Must be non-negative.
    /// </param>
    /// <param name="serviceName">
    /// Logical service name attached to the metrics. When provided, sets <c>OTEL_SERVICE_NAME</c>.
    /// </param>
    /// <param name="serviceVersion">
    /// Logical service version attached to the metrics. When provided, sets
    /// <c>OTEL_SERVICE_VERSION</c>.
    /// </param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="endpoint"/>, <paramref name="serviceName"/>, or <paramref name="serviceVersion"/>
    /// is provided but is empty or whitespace.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="exportInterval"/> or <paramref name="timeout"/> is negative.
    /// </exception>
    /// <example>
    /// <code>
    /// var server = builder.AddDocumentDB("documentdb")
    ///                     .WithOpenTelemetryMetrics(
    ///                         endpoint: "http://otel-collector:4317",
    ///                         exportInterval: TimeSpan.FromSeconds(30));
    /// </code>
    /// </example>
    public static IResourceBuilder<DocumentDBServerResource> WithOpenTelemetryMetrics(
        this IResourceBuilder<DocumentDBServerResource> builder,
        string? endpoint = null,
        bool enabled = true,
        TimeSpan? exportInterval = null,
        TimeSpan? timeout = null,
        string? serviceName = null,
        string? serviceVersion = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (endpoint is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        }

        if (serviceName is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        }

        if (serviceVersion is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(serviceVersion);
        }

        if (exportInterval is { } ei)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(ei, TimeSpan.Zero, nameof(exportInterval));
        }

        if (timeout is { } to)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(to, TimeSpan.Zero, nameof(timeout));
        }

        return builder.WithEnvironment(context =>
        {
            context.EnvironmentVariables[OtelMetricsEnabledEnvVarName] = enabled ? "true" : "false";

            if (endpoint is not null)
            {
                context.EnvironmentVariables[OtelExporterOtlpMetricsEndpointEnvVarName] = endpoint;
            }

            if (exportInterval is { } interval)
            {
                context.EnvironmentVariables[OtelMetricExportIntervalEnvVarName] =
                    ((long)interval.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
            }

            if (timeout is { } timeoutValue)
            {
                context.EnvironmentVariables[OtelExporterOtlpMetricsTimeoutEnvVarName] =
                    ((long)timeoutValue.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
            }

            if (serviceName is not null)
            {
                context.EnvironmentVariables[OtelServiceNameEnvVarName] = serviceName;
            }

            if (serviceVersion is not null)
            {
                context.EnvironmentVariables[OtelServiceVersionEnvVarName] = serviceVersion;
            }
        });
    }


    /// <summary>
    /// Configures the owner used by the DocumentDB Local container.
    /// </summary>
    /// <param name="builder">The resource builder for DocumentDB.</param>
    /// <param name="owner">The owner value to configure.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<DocumentDBServerResource> WithOwner(this IResourceBuilder<DocumentDBServerResource> builder, string owner)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(owner);

        return builder.WithEnvironment(context =>
        {
            context.EnvironmentVariables[OwnerEnvVarName] = owner;
        });
    }

    /// <summary>
    /// Enables TLS for the DocumentDB connection string. TLS is enabled by default
    /// because the DocumentDB Local container requires TLS connections.
    /// Call <c>UseTls(false)</c> to disable TLS if connecting to a non-TLS endpoint.
    /// </summary>
    /// <param name="builder">The resource builder for DocumentDB.</param>
    /// <param name="useTls">Whether to enable TLS. Defaults to <see langword="true"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <example>
    /// <code>
    /// // Disable TLS for a non-TLS endpoint:
    /// var server = builder.AddDocumentDB("documentdb")
    ///                     .UseTls(false);
    /// </code>
    /// </example>
    public static IResourceBuilder<DocumentDBServerResource> UseTls(this IResourceBuilder<DocumentDBServerResource> builder, bool useTls = true)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.SetUseTls(useTls);
        return builder;
    }

    /// <summary>
    /// Allows insecure TLS connections by adding <c>tlsInsecure=true</c> to the connection string.
    /// This is enabled by default so the .NET MongoDB driver can connect to the self-signed
    /// certificate used by the DocumentDB Local container.
    /// Call <c>AllowInsecureTls(false)</c> to require valid certificates.
    /// </summary>
    /// <remarks>
    /// The extension uses <c>tlsInsecure=true</c> rather than <c>tlsAllowInvalidCertificates=true</c>
    /// because the .NET MongoDB driver does not fully honor <c>tlsAllowInvalidCertificates</c> for
    /// self-signed certificates and raises <c>UntrustedRoot</c> errors.
    /// </remarks>
    /// <param name="builder">The resource builder for DocumentDB.</param>
    /// <param name="allowInsecureTls">Whether to allow insecure TLS. Defaults to <see langword="true"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <example>
    /// <code>
    /// // Require valid certificates (for example, production with real certs):
    /// var server = builder.AddDocumentDB("documentdb")
    ///                     .AllowInsecureTls(false);
    /// </code>
    /// </example>
    public static IResourceBuilder<DocumentDBServerResource> AllowInsecureTls(this IResourceBuilder<DocumentDBServerResource> builder, bool allowInsecureTls = true)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.SetAllowInsecureTls(allowInsecureTls);
        return builder;
    }

    /// <summary>
    /// Pins the DocumentDB version to a specific release known to this build of the package.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The selected version is combined with the currently selected
    /// <see cref="DocumentDBPostgresVersion"/> (default <see cref="DocumentDBPostgresVersion.Pg17"/>)
    /// to produce the container image tag <c>pgN-X.Y.Z</c>.
    /// </para>
    /// <para>
    /// <b>Precedence:</b> for the image tag, the most recent of <see cref="WithDocumentDBVersion"/>,
    /// <see cref="WithPostgresVersion"/>,
    /// <see cref="ContainerResourceBuilderExtensions.WithImage{T}(IResourceBuilder{T}, string, string?)"/>,
    /// and <see cref="ContainerResourceBuilderExtensions.WithImageTag{T}(IResourceBuilder{T}, string)"/>
    /// wins. They all converge on the same single <see cref="ContainerImageAnnotation"/>.
    /// </para>
    /// <para>
    /// This method updates only the image tag. A custom image name or registry configured with
    /// <see cref="ContainerResourceBuilderExtensions.WithImage{T}(IResourceBuilder{T}, string, string?)"/>
    /// or <see cref="ContainerResourceBuilderExtensions.WithImageRegistry{T}(IResourceBuilder{T}, string)"/>
    /// is preserved.
    /// </para>
    /// <para>
    /// To pin to a version not in <see cref="DocumentDBVersion"/> (for example, a brand-new
    /// upstream release this package has not yet been updated to know about), use
    /// <see cref="ContainerResourceBuilderExtensions.WithImageTag{T}(IResourceBuilder{T}, string)"/>
    /// directly with a tag like <c>"pg17-0.999.0"</c>.
    /// </para>
    /// </remarks>
    /// <param name="builder">The resource builder for DocumentDB.</param>
    /// <param name="version">The DocumentDB version to use.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <example>
    /// <code>
    /// var server = builder.AddDocumentDB("documentdb")
    ///                     .WithDocumentDBVersion(DocumentDBVersion.V0_110_0);
    /// </code>
    /// </example>
    public static IResourceBuilder<DocumentDBServerResource> WithDocumentDBVersion(
        this IResourceBuilder<DocumentDBServerResource> builder,
        DocumentDBVersion version)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.SetVersion(version);
        return builder.WithImageTag(builder.Resource.ComputeImageTag());
    }

    /// <summary>
    /// Selects the PostgreSQL backend variant of the <c>documentdb-local</c> container image.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The selected variant is combined with the currently selected
    /// <see cref="DocumentDBVersion"/> (or <see cref="DocumentDBVersions.Latest"/> by default)
    /// to produce the container image tag <c>pgN-X.Y.Z</c>.
    /// </para>
    /// <para>
    /// <b>Precedence:</b> see <see cref="WithDocumentDBVersion"/> — last call wins.
    /// </para>
    /// </remarks>
    /// <param name="builder">The resource builder for DocumentDB.</param>
    /// <param name="pgVersion">The PostgreSQL backend variant to use.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="pgVersion"/> is not a defined member of
    /// <see cref="DocumentDBPostgresVersion"/>. Use a free-form
    /// <see cref="ContainerResourceBuilderExtensions.WithImageTag{T}(IResourceBuilder{T}, string)"/>
    /// call to target an unsupported PG variant.
    /// </exception>
    /// <example>
    /// <code>
    /// var server = builder.AddDocumentDB("documentdb")
    ///                     .WithPostgresVersion(DocumentDBPostgresVersion.Pg16)
    ///                     .WithDocumentDBVersion(DocumentDBVersion.V0_110_0);
    /// // -&gt; image tag "pg16-0.110.0"
    /// </code>
    /// </example>
    public static IResourceBuilder<DocumentDBServerResource> WithPostgresVersion(
        this IResourceBuilder<DocumentDBServerResource> builder,
        DocumentDBPostgresVersion pgVersion)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (!Enum.IsDefined(pgVersion))
        {
            throw new ArgumentOutOfRangeException(
                nameof(pgVersion),
                pgVersion,
                $"Unsupported PostgreSQL backend variant '{pgVersion}'. " +
                $"Use one of {nameof(DocumentDBPostgresVersion.Pg15)}, " +
                $"{nameof(DocumentDBPostgresVersion.Pg16)}, or " +
                $"{nameof(DocumentDBPostgresVersion.Pg17)}, or fall back to a free-form " +
                $"WithImageTag(...) for unsupported variants.");
        }

        builder.Resource.SetPgVersion(pgVersion);
        return builder.WithImageTag(builder.Resource.ComputeImageTag());
    }

    private static string GetMountedFilePath(string source, string paramName, string prefix)
    {
        var fileName = Path.GetFileName(source);

        if (string.IsNullOrEmpty(fileName))
        {
            throw new ArgumentException("The path must include a file name.", paramName);
        }

        return $"/{prefix}{fileName}";
    }
}
