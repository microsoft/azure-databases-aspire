using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

// Simple DocumentDB server
var documentDB = builder.AddDocumentDB("documentdb", port: 10260);

Console.WriteLine("🚀 DocumentDB Playground");
Console.WriteLine("========================");
Console.WriteLine();
Console.WriteLine("Starting DocumentDB server on port 10260");
Console.WriteLine("Check the Aspire dashboard for connection details");
Console.WriteLine();

var app = builder.Build();

await app.RunAsync();
