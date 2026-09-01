# Getting started

This guide walks you through adding [DocumentDB](https://github.com/documentdb/documentdb) to an Aspire application. By the end, you will have a running Aspire app with a DocumentDB container that your services can connect to using the MongoDB driver.

## Prerequisites

| Requirement | Details |
|---|---|
| .NET 10 SDK or later | [Download](https://dotnet.microsoft.com/download) |
| Aspire workload | `dotnet workload install aspire` |
| Docker | DocumentDB runs as a Linux container. [Docker Desktop](https://www.docker.com/products/docker-desktop/) or any Docker-compatible runtime works. |
| IDE (optional) | Visual Studio 2022 17.9+, VS Code with C# Dev Kit, or JetBrains Rider |

## Create or open an Aspire project

If you already have an Aspire project, skip to [Install the package](#install-the-package).

```bash
dotnet new aspire-starter -o MyApp
cd MyApp
```

This creates a solution with an AppHost project and a service project.

## Install the package

In your **AppHost** project, add the DocumentDB hosting package:

```bash
cd MyApp.AppHost
dotnet add package Aspire.Hosting.DocumentDB
```

## Configure the AppHost

In your AppHost's `Program.cs`, add a DocumentDB server and a database:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

// Add a DocumentDB server with a database
var db = builder.AddDocumentDB("documentdb")
                .AddDatabase("mydb");

// Wire the database into your service
var api = builder.AddProject<Projects.MyApp_ApiService>("apiservice")
                 .WithReference(db)
                 .WaitFor(db);

builder.Build().Run();
```

This tells Aspire to:

1. Pull and start the `documentdb-local` container image
2. Generate credentials and a connection string
3. Pass the connection string to your service as a named connection

> [!NOTE]
> `WaitFor` waits for DocumentDB's authenticated MongoDB `ping` health check, which confirms that the gateway is reachable. On DocumentDB `0.116.0`, custom one-shot initialization can continue after the gateway becomes healthy, so services that depend on seeded data should still retry until that data is available.

## Install the MongoDB client package

In your **service** project (for example, `MyApp.ApiService`), add the Aspire MongoDB driver integration:

```bash
cd MyApp.ApiService
dotnet add package Aspire.MongoDB.Driver
```

## Register and use the MongoDB client

In your service's `Program.cs`, register the client using the same connection name you used in the AppHost:

```csharp
var builder = WebApplication.CreateBuilder(args);

// Register the MongoDB client -- the connection name must match the AppHost resource name
builder.AddMongoDBClient("mydb");
```

Then inject `IMongoClient` or `IMongoDatabase` in your services:

```csharp
app.MapGet("/documents", async (IMongoClient client) =>
{
    var database = client.GetDatabase("mydb");
    var collection = database.GetCollection<BsonDocument>("items");
    var docs = await collection.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
    return docs;
});
```

## Run the application

```bash
cd MyApp.AppHost
dotnet run
```

Aspire will:

- Start the DocumentDB container (first run pulls the image, which may take a minute)
- Open the Aspire dashboard in your browser
- Show the DocumentDB resource with its connection string, status, and logs

You can find the generated connection string in the Aspire dashboard under the resource details.

## What to expect

- **TLS is enabled by default.** The DocumentDB container uses a self-signed certificate. The extension automatically adds `tls=true&tlsInsecure=true` to the connection string so the .NET MongoDB driver accepts the self-signed certificate.
- **Credentials are auto-generated.** Unless you provide explicit parameters, the extension generates a random password and uses `admin` as the default username.
- **Data is ephemeral.** By default, DocumentDB stores data inside the container. When the container stops, you lose that data. Use `WithDataVolume()` to persist data across restarts. `WithDataBindMount()` also persists data to a host directory, but on Docker Desktop the container only starts against it once — later runs fail with `FATAL: data directory "/data" has wrong ownership` and the data, though intact on the host, is unreadable. Prefer `WithDataVolume()` unless you are on a native container engine and need the files on the host. See [Configuration](configuration.md) and [Bind-mounted data fails to restart on Docker Desktop](troubleshooting.md#bind-mounted-data-fails-to-restart-on-docker-desktop) for details.

## Next steps

- [Configuration reference](configuration.md) — all available methods and options
- [Troubleshooting](troubleshooting.md) — common issues and solutions
