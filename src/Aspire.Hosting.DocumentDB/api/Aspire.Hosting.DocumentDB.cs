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

        public static ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> WithLogLevel(this ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> builder, DocumentDBLogLevel logLevel) { throw null; }

        public static ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> WithInitData(this ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> builder, string source) { throw null; }

        public static ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> WithoutSampleData(this ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> builder) { throw null; }

        public static ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> WithTlsCertificate(this ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> builder, string certPath, string keyPath) { throw null; }

        public static ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> WithTelemetry(this ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> builder, bool enabled = true) { throw null; }

        public static ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> WithoutExtendedRum(this ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> builder) { throw null; }

        public static ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> WithOwner(this ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> builder, string owner) { throw null; }

        public static ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> WithDataBindMount(this ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> builder, string source, bool isReadOnly = false) { throw null; }

        public static ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> WithDataVolume(this ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> builder, string? name = null, bool isReadOnly = false, string? targetPath = null) { throw null; }

        public static ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> UseTls(this ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> builder, bool useTls = true) { throw null; }

        public static ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> AllowInsecureTls(this ApplicationModel.IResourceBuilder<ApplicationModel.DocumentDBServerResource> builder, bool allowInsecureTls = true) { throw null; }

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
        public DocumentDBServerResource(string name) : base(default!, default) { }

        public DocumentDBServerResource(string name, ParameterResource? userNameParameter, ParameterResource? passwordParameter) : base(default!, default) { }

        public ReferenceExpression ConnectionStringExpression { get { throw null; } }

        public System.Collections.Generic.IReadOnlyList<ApplicationModel.DocumentDBDatabaseResource> Databases { get { throw null; } }

        public ParameterResource? PasswordParameter { get { throw null; } }

        public EndpointReference PrimaryEndpoint { get { throw null; } }

        public ParameterResource? UserNameParameter { get { throw null; } }
    }
}
