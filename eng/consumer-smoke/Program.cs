// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// A consumer of the packed package, not the project: start one DocumentDB and round-trip a document.
using Aspire.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;

using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = args, DisableDashboard = true });
var database = builder.AddDocumentDB("documentdb").AddDatabase("smokedb");

await using var app = builder.Build();
await app.StartAsync(cts.Token);
await app.ResourceNotifications.WaitForResourceHealthyAsync(database.Resource.Name, cts.Token);

var connectionString = await database.Resource.ConnectionStringExpression.GetValueAsync(cts.Token)
    ?? throw new InvalidOperationException("The database resource has no connection string.");

// MongoDB.Driver arrives through the package's own dependencies, as it would for any consumer.
var collection = new MongoClient(connectionString).GetDatabase("smokedb").GetCollection<BsonDocument>("smoke");
var id = ObjectId.GenerateNewId();
await collection.InsertOneAsync(new BsonDocument("_id", id), cancellationToken: cts.Token);
var found = await collection.CountDocumentsAsync(new BsonDocument("_id", id), cancellationToken: cts.Token);
if (found != 1)
{
    throw new InvalidOperationException($"Expected to read back 1 document, found {found}.");
}

await app.StopAsync(cts.Token);
Console.WriteLine("Consumer smoke passed.");
