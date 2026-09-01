# Aspire.Hosting.DocumentDB

[DocumentDB](https://github.com/documentdb/documentdb) is an open-source, MongoDB-compatible document database built on PostgreSQL. This package provides Aspire hosting integration to configure and run a DocumentDB container as part of your distributed application.

## Getting started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) or later with the Aspire workload: `dotnet workload install aspire`
- [Docker](https://www.docker.com/products/docker-desktop/) (DocumentDB runs as a Linux container)

### Install the package

In your AppHost project:

```dotnetcli
dotnet add package Aspire.Hosting.DocumentDB
```

### Add a DocumentDB resource

In the AppHost `Program.cs`:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var db = builder.AddDocumentDB("documentdb")
                .AddDatabase("mydb");

builder.AddProject<Projects.MyService>("myservice")
       .WithReference(db)
       .WaitFor(db);

builder.Build().Run();
```

### Connect from your service

In your service project, install the Aspire MongoDB driver integration:

```dotnetcli
dotnet add package Aspire.MongoDB.Driver
```

Register the client in `Program.cs`:

```csharp
builder.AddMongoDBClient("mydb");
```

Inject and use the MongoDB client:

```csharp
public class MyService(IMongoClient mongoClient)
{
    private readonly IMongoDatabase _database = mongoClient.GetDatabase("mydb");

    public async Task<List<BsonDocument>> GetDocumentsAsync()
    {
        var collection = _database.GetCollection<BsonDocument>("mycollection");
        return await collection.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
    }
}
```

The Aspire integration handles connection string resolution, TLS configuration, and credential management automatically.

## Configuration

| Method | Description |
|---|---|
| `AddDocumentDB(name, port?, userName?, password?)` | Add a DocumentDB server container |
| `.AddDatabase(name, databaseName?)` | Add a named database |
| `.WithHostPort(port)` | Bind to a fixed host port (default: random) |
| `.WithDataVolume(name?, isReadOnly?, targetPath?)` | Persist data with a Docker volume (`isReadOnly: true` is rejected) |
| `.WithDataBindMount(source, isReadOnly?)` | Persist data with a host directory mount (`isReadOnly: true` is rejected) |
| `.WithLogLevel(level)` | Set the container `LOG_LEVEL` (`Quiet`, `Error`, `Warn`, `Info`, `Debug`, `Trace`) |
| `.WithInitData(source)` | Bind-mount initialization scripts to `/init_doc_db.d` and disable built-in sample data |
| `.WithoutSampleData()` | Disable the built-in sample data initialization |
| `.WithoutExtendedRum()` | Disable the `extended_rum` index access method (DocumentDB v0.111.0+) |
| `.WithoutUserCreation()` | Skip automatic user creation on container startup |
| `.WithTlsCertificate(certPath, keyPath)` | Mount a custom TLS certificate and key into the container |
| `.WithTelemetry(enabled?)` | **Obsolete.** No-op against gateway v0.112-0+; use `.WithOpenTelemetryMetrics(...)` instead. Kept for binary compatibility (`ASPIREDOCDB0001`). |
| `.WithOpenTelemetryMetrics(endpoint?, enabled?, exportInterval?, timeout?, serviceName?, serviceVersion?)` | Enable OpenTelemetry metrics export from the gateway via OTLP/gRPC (container v0.112-0+) |
| `.WithOwner(owner)` | Set the existing PostgreSQL owner role used for DocumentDB operations |
| `.UseTls(useTls?)` | Enable/disable TLS (default: enabled) |
| `.AllowInsecureTls(allow?)` | Allow self-signed certs (default: enabled) |
| `.WithDocumentDBVersion(version)` | Pin a curated DocumentDB version (default: latest known to this build) |
| `.WithPostgresVersion(pgVersion)` | Choose PG15/16/17/18 backend variant (default: Pg17) |

### Additional container configuration

For closer-to-production local setups, debugging, or custom data seeding, the
hosting integration exposes additional DocumentDB Local container options:

```csharp
var documentdb = builder.AddDocumentDB("documentdb")
    .WithLogLevel(DocumentDBLogLevel.Debug)
    .WithInitData("../seed")
    .WithTlsCertificate("../certs/documentdb.pem", "../certs/documentdb.key")
    .WithOpenTelemetryMetrics(endpoint: "http://otel-collector:4317")
    .WithOwner("documentdb")
    .WithoutExtendedRum();

var db = documentdb.AddDatabase("mydb");
```

`WithInitData(...)` mounts a host directory into `/init_doc_db.d` and also
disables the built-in sample data so your custom scripts are the only
initialization source. Use `WithoutSampleData()` when you want to disable the
built-in sample collections without mounting custom initialization scripts.

`WithTlsCertificate(...)` mounts the certificate and key files at distinct
container paths, so they can be supplied even when their host file names are
identical.

### Connection strings

The extension generates MongoDB connection strings automatically:

```
mongodb://admin:<password>@<host>:<port>[/<database>]?authSource=admin&authMechanism=SCRAM-SHA-256&tls=true&tlsInsecure=true
```

TLS and insecure TLS are enabled by default so the .NET MongoDB driver can connect to the self-signed certificate used by the DocumentDB Local container.

### Data persistence

Use `WithDataVolume()` when data must persist predictably across container replacement:

```csharp
builder.AddDocumentDB("documentdb")
       .WithDataVolume()
       .AddDatabase("mydb");
```

DocumentDB `0.116.0` declares `/data` as an image volume, so a run that mounts nothing there
gets an anonymous volume whose lifetime the container runtime controls and which container
removal can strand. `WithDataVolume()` and `WithDataBindMount(...)` mount on that same path,
which both makes persistence intentional and suppresses the anonymous volume. Pair either with
stable credential parameters.

The data directory must be writable and is exclusive: `isReadOnly: true` is rejected, and only
one running container at a time may use a given volume or host directory. Initialization
(sample data or `WithInitData(...)`) runs once per data directory and is not retried after a
failed attempt. See the
[configuration reference](https://github.com/microsoft/azure-databases-aspire/blob/main/docs/configuration.md#storage-requirements)
for the full rules.

## More information

- [Getting started guide](https://github.com/microsoft/azure-databases-aspire/blob/main/docs/getting-started.md) — detailed step-by-step setup
- [Configuration reference](https://github.com/microsoft/azure-databases-aspire/blob/main/docs/configuration.md) — all methods, parameters, defaults, and connection string details
- [Troubleshooting](https://github.com/microsoft/azure-databases-aspire/blob/main/docs/troubleshooting.md) — TLS errors, Docker issues, connection failures, debugging
- [Changelog](https://github.com/microsoft/azure-databases-aspire/blob/main/CHANGELOG.md) — release history
- [License](https://github.com/microsoft/azure-databases-aspire/blob/main/LICENSE) — package license
- [DocumentDB project](https://github.com/documentdb/documentdb) — the database itself
- [Aspire documentation](https://learn.microsoft.com/en-us/dotnet/aspire/) — Aspire framework docs
