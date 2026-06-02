namespace Aspire.Hosting
{
    public enum DocumentDBLogLevel
    {
        Quiet = 0,
        Error = 1,
        Warn = 2,
        Info = 3,
        Debug = 4,
        Trace = 5,
    }

    public static partial class DocumentDBBuilderExtensions
    {
        public static ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBDatabaseResource> AddDatabase(this ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> builder, string name, string? databaseName = null) { throw null; }

        public static ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> AddDocumentDB(this IDistributedApplicationBuilder builder, string name, int? port = null, ApplicationModel.IResourceBuilder<ApplicationModel.ParameterResource>? userName = null, ApplicationModel.IResourceBuilder<ApplicationModel.ParameterResource>? password = null) { throw null; }

        public static ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> AddDocumentDB(this IDistributedApplicationBuilder builder, string name, int? port) { throw null; }

        public static ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> WithDataBindMount(this ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> builder, string source, bool isReadOnly = false) { throw null; }

        public static ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> WithDataVolume(this ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> builder, string? name = null, bool isReadOnly = false, string? targetPath = null) { throw null; }

        public static ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> WithHostPort(this ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> builder, int? port) { throw null; }

        public static ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> WithLogLevel(this ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> builder, DocumentDBLogLevel logLevel) { throw null; }

        public static ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> WithInitData(this ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> builder, string source) { throw null; }

        public static ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> WithoutSampleData(this ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> builder) { throw null; }

        public static ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> WithoutExtendedRum(this ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> builder) { throw null; }

        public static ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> WithoutUserCreation(this ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> builder) { throw null; }

        public static ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> WithTlsCertificate(this ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> builder, string certPath, string keyPath) { throw null; }

        [Obsolete("ENABLE_TELEMETRY is not consumed by the DocumentDB gateway in container image v0.112-0 or later, so this method has no observable effect on those images. Use WithOpenTelemetryMetrics(...) for OTLP metrics. This member is kept for binary compatibility and may be removed in a future release.", error: false, DiagnosticId = "ASPIREDOCDB0001", UrlFormat = "https://github.com/microsoft/azure-databases-aspire/blob/main/docs/configuration.md#withtelemetry-obsolete")]
        public static ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> WithTelemetry(this ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> builder, bool enabled = true) { throw null; }

        public static ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> WithOpenTelemetryMetrics(this ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> builder, string? endpoint = null, bool enabled = true, System.TimeSpan? exportInterval = null, System.TimeSpan? timeout = null, string? serviceName = null, string? serviceVersion = null) { throw null; }

        public static ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> WithOwner(this ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> builder, string owner) { throw null; }

        public static ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> UseTls(this ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> builder, bool useTls = true) { throw null; }

        public static ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> AllowInsecureTls(this ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> builder, bool allowInsecureTls = true) { throw null; }

        public static ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> WithDocumentDBVersion(this ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> builder, ApplicationModel.DocumentDBVersion version) { throw null; }

        public static ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> WithPostgresVersion(this ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> builder, ApplicationModel.DocumentDBPostgresVersion pgVersion) { throw null; }

        public static ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> WithPostgresEndpoint(this ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> builder, int? port = null) { throw null; }

    }
}

namespace Aspire.Hosting.ApplicationModel
{
    public partial class DocumentDBDatabaseResource : Resource, IResourceWithParent<DocumentDBServerResource>, IResourceWithParent, IResource, IResourceWithConnectionString, IManifestExpressionProvider, IValueProvider, IValueWithReferences
    {
        public DocumentDBDatabaseResource(string name, string databaseName, DocumentDBServerResource parent) : base(default!) { }

        public ReferenceExpression ConnectionStringExpression { get { throw null; } }

        public string DatabaseName { get { throw null; } }

        public DocumentDBServerResource Parent { get { throw null; } }
    }

    public partial class DocumentDBServerResource : ContainerResource, IResourceWithConnectionString, IResource, IManifestExpressionProvider, IValueProvider, IValueWithReferences
    {
        public DocumentDBServerResource(string name, ParameterResource? userNameParameter, ParameterResource? passwordParameter) : base(default!, default) { }

        public ReferenceExpression ConnectionStringExpression { get { throw null; } }

        public ReferenceExpression PostgresConnectionStringExpression { get { throw null; } }

    public System.Collections.Generic.IReadOnlyList<ApplicationModel.DocumentDBDatabaseResource> Databases { get { throw null; } }

        public ParameterResource? PasswordParameter { get { throw null; } }

        public EndpointReference PrimaryEndpoint { get { throw null; } }

        public EndpointReference PostgresEndpoint { get { throw null; } }

        public ParameterResource? UserNameParameter { get { throw null; } }
    }

    public enum DocumentDBVersion
    {
        // <auto-generated-versions-start>
        V0_109_0 = 1,
        V0_110_0 = 2,
        V0_111_0 = 3,
        // <auto-generated-versions-end>
    }

    public enum DocumentDBPostgresVersion
    {
        Pg15 = 15,
        Pg16 = 16,
        Pg17 = 17,
    }

    public static partial class DocumentDBVersions
    {
        // <auto-generated-versions-start>
        public const string V0_109_0 = "0.109.0";
        public const string V0_110_0 = "0.110.0";
        public const string V0_111_0 = "0.111.0";
        // <auto-generated-versions-end>

        public static System.Collections.Generic.IReadOnlyList<string> All { get { throw null; } }
        public static string Latest { get { throw null; } }
    }
}
