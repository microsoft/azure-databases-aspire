# Getting started

This guide walks you through adding [DocumentDB](https://github.com/documentdb/documentdb) to a .NET Aspire application. By the end, you will have a running Aspire app with a DocumentDB container that your services can connect to using the MongoDB driver.

## Prerequisites

| Requirement | Details |
|---|---|
| .NET 9 SDK or later | [Download](https://dotnet.microsoft.com/download) |
| .NET Aspire workload | `dotnet workload install aspire` |
| Docker | DocumentDB runs as a Linux container. [Docker Desktop](https://www.docker.com/products/docker-desktop/) or any Docker-compatible runtime works. |
| IDE (optional) | Visual Studio 2022 17.9+, VS Code with C# Dev Kit, or JetBrains Rider |

## Create or open an Aspire project

If you already have an Aspire project, skip to step 2.

```bash
dotnet new aspire-starter -n MyApp
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
var api = builder.AddProject<Projects.MyApp_ApiService>()
                 .WithReference(db)
                 .WaitFor(db);

builder.Build().Run();
```

This tells Aspire to:

1. Pull and start the `documentdb-local` container image
2. Generate credentials and a connection string
3. Pass the connection string to your service as a named connection

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
- **Data is ephemeral.** By default, data is stored inside the container. When the container stops, data is lost. Use `WithDataVolume()` or `WithDataBindMount()` to persist data across restarts. See [Configuration](configuration.md) for details.

## Next steps

- [Configuration reference](configuration.md) — all available methods and options
- [Troubleshooting](troubleshooting.md) — common issues and solutions
