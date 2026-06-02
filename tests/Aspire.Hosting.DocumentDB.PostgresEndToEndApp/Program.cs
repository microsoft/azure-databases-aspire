// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting;

namespace Aspire.Hosting.DocumentDB.PostgresEndToEndApp;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        // Pin to pg17-0.112.0 explicitly so this AppHost does not depend on
        // DocumentDBVersions.Latest being bumped to >= 0.112.0 (tracked by issue
        // #70). The v0.112-0 floor is enforced by WithPostgresEndpoint() itself;
        // see https://github.com/microsoft/azure-databases-aspire/issues/71.
        builder.AddDocumentDB("documentdb")
            .WithImageTag("pg17-0.112.0")
            .WithPostgresEndpoint();

        var app = builder.Build();

        await app.RunAsync();
    }
}
