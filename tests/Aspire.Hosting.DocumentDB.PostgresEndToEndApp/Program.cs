// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting;

namespace Aspire.Hosting.DocumentDB.PostgresEndToEndApp;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        // Exercises the default image tag (DocumentDBVersions.Latest), which is at or above
        // the v0.112-0 floor that WithPostgresEndpoint() enforces; see
        // https://github.com/microsoft/azure-databases-aspire/issues/71.
        builder.AddDocumentDB("documentdb")
            .WithPostgresEndpoint();

        var app = builder.Build();

        await app.RunAsync();
    }
}
