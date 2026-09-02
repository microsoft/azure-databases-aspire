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
| `.WithDataBindMount(source, isReadOnly?)` | Persist data with a host directory mount (`isReadOnly: true` is rejected). Does **not** reliably survive a restart on Docker Desktop — see [Bind-mounted data fails to restart on Docker Desktop](https://github.com/microsoft/azure-databases-aspire/blob/main/docs/troubleshooting.md#bind-mounted-data-fails-to-restart-on-docker-desktop) |
| `.WithLogLevel(level)` | Set gateway `DOCUMENTDB_LOG_LEVEL` and entrypoint-contract `LOG_LEVEL` (`Quiet`, `Error`, `Warn`, `Info`, `Debug`, `Trace`) |
| `.WithInitData(source)` | Bind-mount initialization scripts to `/init_doc_db.d` and disable built-in sample data |
| `.WithoutSampleData()` | Disable the built-in sample data initialization |
| `.WithoutExtendedRum()` | Disable the `extended_rum` index access method (DocumentDB v0.111.0+) |
| `.WithoutUserCreation()` | Skip automatic user creation on container startup |
| `.WithTlsCertificate(certPath, keyPath)` | Mount a custom TLS certificate and key into the container |
| `.WithTelemetry(enabled?)` | **Obsolete.** No-op against gateway v0.112-0+; use `.WithOpenTelemetryMetrics(...)` instead. Kept for binary compatibility (`ASPIREDOCDB0001`). |
| `.WithOpenTelemetryMetrics(endpoint?, enabled?, exportInterval?, timeout?, serviceName?, serviceVersion?)` | Enable OpenTelemetry metrics export from the gateway via OTLP/gRPC (container v0.112-0+). On v0.116-0 and later it also wraps the container entrypoint so the `OTEL_*` variables stay authoritative for the settings you supply; the wrapper is carried in `entrypoint`/`args` so published manifests stay deployable. |
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

`WithLogLevel(...)` becomes observably effective on DocumentDB `0.114.0` and later through
`DOCUMENTDB_LOG_LEVEL`, including the current default image. On images through `0.113.0`,
neither variable controls gateway verbosity; `LOG_LEVEL` is retained because the Local entrypoint
validates its six-value contract, not because a Local image uses it to select gateway verbosity.
`Quiet` remains mapped to `quiet` for API compatibility and becomes newly effective on `0.114.0`
and later. Because upstream tracing has no `quiet` level, current gateways suppress output by parsing
it as an unmatched tracing target; that behavior depends on upstream filter semantics.

`WithInitData(...)` mounts a host directory into `/init_doc_db.d` and also
disables the built-in sample data so your custom scripts are the only
initialization source. Use `WithoutSampleData()` when you want to disable the
built-in sample collections. It does not disable custom initialization scripts
configured through `WithInitData(...)`.

For curated images 0.112.0 and older, built-in sample initialization is enabled
by default, so `WithoutUserCreation()` must be paired with
`WithoutSampleData()` on a fresh container. From 0.113.0 onward, including
0.116.0, built-in sample initialization is opt-in and a fresh container can
remain running without creating the user when no initialization requiring those
credentials is requested. Generated connection strings still will not
authenticate unless that user already exists, typically in persisted storage
from an earlier run. On every version, requested built-in or custom
initialization can fail if the skipped user does not already exist.
`WithoutSampleData()` does not disable custom initialization.

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

Up to and including `0.114.0` an unmounted `/data` is a directory in the container's writable
layer, discarded with the container. From `0.116.0` the image declares `/data` as a container
volume, so a run that mounts nothing there instead gets an anonymous volume whose lifetime the
container runtime controls and which container removal can strand. `WithDataVolume()` and
`WithDataBindMount(...)` mount on that same path, which both makes persistence intentional and,
on those images, suppresses the anonymous volume. Pair either with stable credential parameters.

The data directory must be writable, and must be empty or hold an existing DocumentDB cluster:
`isReadOnly: true` is rejected, and a directory holding anything else is refused (not cleaned)
by the container. From `0.116.0` it is also exclusive — only one running container at a time may
use a given volume or host directory — and initialization (sample data or `WithInitData(...)`)
runs once per data directory and is not retried after a failed attempt. On `0.114.0` and earlier
there is no such marker: the requested initialization runs on every container start, so seed
scripts used with persisted storage must be idempotent. See the
[configuration reference](https://github.com/microsoft/azure-databases-aspire/blob/main/docs/configuration.md#storage-requirements)
for the full rules.

`WithDataBindMount()` is not an equivalent choice on Docker Desktop. Its host file sharing does
not apply ownership changes to the mounted path in time for PostgreSQL's data-directory check, so
the first run works and every later run fails to start with
`FATAL: data directory "/data" has wrong ownership`, leaving the data on the host but unreadable.
This was measured on macOS (VirtioFS) and is expected on Docker Desktop's other hosts, which share
that design. See
[Bind-mounted data fails to restart on Docker Desktop](https://github.com/microsoft/azure-databases-aspire/blob/main/docs/troubleshooting.md#bind-mounted-data-fails-to-restart-on-docker-desktop).

## More information

- [Getting started guide](https://github.com/microsoft/azure-databases-aspire/blob/main/docs/getting-started.md) — detailed step-by-step setup
- [Configuration reference](https://github.com/microsoft/azure-databases-aspire/blob/main/docs/configuration.md) — all methods, parameters, defaults, and connection string details
- [Troubleshooting](https://github.com/microsoft/azure-databases-aspire/blob/main/docs/troubleshooting.md) — TLS errors, Docker issues, connection failures, debugging
- [Changelog](https://github.com/microsoft/azure-databases-aspire/blob/main/CHANGELOG.md) — release history
- [License](https://github.com/microsoft/azure-databases-aspire/blob/main/LICENSE) — package license
- [DocumentDB project](https://github.com/documentdb/documentdb) — the database itself
- [Aspire documentation](https://learn.microsoft.com/en-us/dotnet/aspire/) — Aspire framework docs
