# Aspire.Hosting.DocumentDB library

[DocumentDB](https://github.com/documentdb/documentdb) is a MongoDB compatible open source document database built on PostgreSQL. This provides extension methods and resource definitions for a .NET Aspire AppHost to configure a DocumentDB resource.

## Getting started

### Install the package

In your AppHost project, install the .NET Aspire DocumentDB Hosting library with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.Hosting.DocumentDB
```

## Usage example

Then, in the _AppHost.cs_ file of `AppHost`, add a DocumentDB resource and consume the connection using the following methods:

```csharp
var db = builder.AddDocumentDB("DocumentDB").AddDatabase("mydb");

var myService = builder.AddProject<Projects.MyService>()
                       .WithReference(db);
```

For local development, the generated DocumentDB connection strings enable TLS and allow the self-signed local certificate automatically so client applications can connect without extra manual connection string settings.

## Additional container configuration

The hosting integration also exposes several DocumentDB Local container options for debugging, seeding, and closer-to-production local setups:

```csharp
var documentdb = builder.AddDocumentDB("documentdb")
    .WithLogLevel(DocumentDBLogLevel.Debug)
    .WithInitData("../seed")
    .WithTlsCertificate("../certs/documentdb.pem", "../certs/documentdb.key")
    .WithTelemetry(enabled: false)
    .WithoutExtendedRum()
    .WithOwner("documentdb");

var db = documentdb.AddDatabase("mydb");
```

Use `WithoutSampleData()` when you want to disable the built-in sample collections without mounting custom initialization scripts:

```csharp
var documentdb = builder.AddDocumentDB("documentdb")
    .WithoutSampleData();
```

`WithInitData(...)` mounts a host directory into `/init_doc_db.d` and also disables the built-in sample data so your custom scripts are the only initialization source.

## Connecting from client applications

To connect to DocumentDB from your application services, you'll need to install the MongoDB client integration package:

### Install the client package

In your service project, install the .NET Aspire MongoDB Driver component with [NuGet](https://www.nuget.org):

```dotnetcli
dotnet add package Aspire.MongoDB.Driver
```

### Register the MongoDB client

In your service's `Program.cs` file, register the MongoDB client:

```csharp
builder.AddMongoDBClient("DocumentDB");
```

### Use the MongoDB client

Inject and use the MongoDB client in your services:

```csharp
public class MyService
{
    private readonly IMongoDatabase _database;

    public MyService(IMongoClient mongoClient)
    {
        _database = mongoClient.GetDatabase("mydb");
    }

    public async Task<List<MyDocument>> GetDocumentsAsync()
    {
        var collection = _database.GetCollection<MyDocument>("mycollection");
        return await collection.Find(FilterDefinition<MyDocument>.Empty).ToListAsync();
    }

    public async Task InsertDocumentAsync(MyDocument document)
    {
        var collection = _database.GetCollection<MyDocument>("mycollection");
        await collection.InsertOneAsync(document);
    }
}

public class MyDocument
{
    public ObjectId Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
```

The client integration handles connection string resolution and provides features like health checks, logging, and telemetry automatically.

## Feedback & contributing

https://github.com/dotnet/aspire
