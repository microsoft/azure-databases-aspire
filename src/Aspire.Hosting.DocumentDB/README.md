# Aspire.Hosting.DocumentDB

[DocumentDB](https://github.com/documentdb/documentdb) is an open-source, MongoDB-compatible document database built on PostgreSQL. This package provides .NET Aspire hosting integration to configure and run a DocumentDB container as part of your distributed application.

## Getting started

### Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download) or later with the Aspire workload: `dotnet workload install aspire`
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
| `.WithDataVolume(name?, isReadOnly?, targetPath?)` | Persist data with a Docker volume |
| `.WithDataBindMount(source, isReadOnly?)` | Persist data with a host directory mount |
| `.UseTls(useTls?)` | Enable/disable TLS (default: enabled) |
| `.AllowInsecureTls(allow?)` | Allow self-signed certs (default: enabled) |

### Connection strings

The extension generates MongoDB connection strings automatically:

```
mongodb://admin:<password>@<host>:<port>[/<database>]?authSource=admin&authMechanism=SCRAM-SHA-256&tls=true&tlsInsecure=true
```

TLS and insecure TLS are enabled by default so the .NET MongoDB driver can connect to the self-signed certificate used by the DocumentDB Local container.

### Data persistence

By default, DocumentDB stores data inside the container, and restarting the container removes it. Use `WithDataVolume()` to persist it:

```csharp
builder.AddDocumentDB("documentdb")
       .WithDataVolume()
       .AddDatabase("mydb");
```

## More information

- [Getting started guide](https://github.com/microsoft/azure-databases-aspire/blob/main/docs/getting-started.md) — detailed step-by-step setup
- [Configuration reference](https://github.com/microsoft/azure-databases-aspire/blob/main/docs/configuration.md) — all methods, parameters, defaults, and connection string details
- [Troubleshooting](https://github.com/microsoft/azure-databases-aspire/blob/main/docs/troubleshooting.md) — TLS errors, Docker issues, connection failures, debugging
- [Changelog](https://github.com/microsoft/azure-databases-aspire/blob/main/CHANGELOG.md) — release history
- [License](https://github.com/microsoft/azure-databases-aspire/blob/main/LICENSE) — package license
- [DocumentDB project](https://github.com/documentdb/documentdb) — the database itself
- [.NET Aspire documentation](https://learn.microsoft.com/en-us/dotnet/aspire/) — Aspire framework docs
