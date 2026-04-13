# Aspire.Hosting.DocumentDB

[![NuGet](https://img.shields.io/nuget/v/Aspire.Hosting.DocumentDB.svg)](https://www.nuget.org/packages/Aspire.Hosting.DocumentDB)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

A [.NET Aspire](https://learn.microsoft.com/en-us/dotnet/aspire/fundamentals/integrations-overview) hosting integration for [DocumentDB](https://github.com/documentdb/documentdb), the open-source MongoDB-compatible document database built on PostgreSQL.

This package lets you add a DocumentDB container to your Aspire AppHost with a single line of code. Connection strings, credentials, TLS, and container lifecycle are managed automatically.

## Quick start

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) or later
- [.NET Aspire workload](https://learn.microsoft.com/en-us/dotnet/aspire/fundamentals/setup-tooling): `dotnet workload install aspire`
- [Docker](https://www.docker.com/products/docker-desktop/) (DocumentDB runs as a Linux container)

### Install the package

In your Aspire **AppHost** project:

```bash
dotnet add package Aspire.Hosting.DocumentDB
```

### Add DocumentDB to your AppHost

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var db = builder.AddDocumentDB("documentdb")
                .AddDatabase("mydb");

builder.AddProject<Projects.MyService>("myservice")
       .WithReference(db)
       .WaitFor(db);

builder.Build().Run();
```

### Use MongoDB client in your service

In your service project, install and register the MongoDB driver:

```bash
dotnet add package Aspire.MongoDB.Driver
```

```csharp
builder.AddMongoDBClient("mydb");
```

Then inject `IMongoClient` wherever you need it. The connection string is resolved automatically.

## Configuration methods

| Method | Description |
|---|---|
| `AddDocumentDB(name, port?, userName?, password?)` | Add a DocumentDB server resource |
| `.AddDatabase(name, databaseName?)` | Add a database on the server |
| `.WithHostPort(port)` | Bind to a specific host port |
| `.WithDataVolume(name?, isReadOnly?, targetPath?)` | Persist data with a Docker volume |
| `.WithDataBindMount(source, isReadOnly?)` | Persist data with a host directory mount |
| `.UseTls(useTls?)` | Enable/disable TLS (default: enabled) |
| `.AllowInsecureTls(allowInsecureTls?)` | Allow self-signed certificates (default: enabled) |

See the [Configuration Reference](docs/configuration.md) for details, code examples, and default values.

## Documentation

| Document | Description |
|---|---|
| [Getting Started](docs/getting-started.md) | Step-by-step guide to set up DocumentDB in an Aspire app |
| [Configuration Reference](docs/configuration.md) | All methods, parameters, defaults, and connection string format |
| [Troubleshooting](docs/troubleshooting.md) | Common issues and solutions |
| [Changelog](CHANGELOG.md) | Release history |
| [NuGet Package README](src/Aspire.Hosting.DocumentDB/README.md) | Package overview shown on nuget.org |

## About DocumentDB

[DocumentDB](https://github.com/documentdb/documentdb) is an open-source document database that provides MongoDB compatibility on top of PostgreSQL. It supports:

- Full MongoDB CRUD API via the MongoDB wire protocol
- BSON document storage
- Rich query support including aggregation pipelines
- Full-text, geospatial, and vector search
- SCRAM-SHA-256 authentication with TLS

The Aspire integration runs the [`documentdb-local`](https://ghcr.io/documentdb/documentdb/documentdb-local) container image, which bundles the DocumentDB gateway and PostgreSQL backend in a single container.

## Contributing

Contributions are welcome. Please open an issue first to discuss the change you'd like to make.

To build and test locally:

```bash
dotnet build azure-databases-aspire.sln
dotnet test azure-databases-aspire.sln
```

End-to-end tests require a running Docker daemon.

## License

This project is licensed under the [MIT License](LICENSE).
