// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting;

namespace Aspire.Hosting.DocumentDB.PostgresEndToEndApp;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        // Pinned deliberately: this end-to-end app gates the NuGet publish workflow, so it must
        // run against a deterministic, known-good tag instead of floating on
        // DocumentDBVersions.Latest (a mutable GHCR tag). Bump this pin as part of adopting each
        // new DocumentDB version. The v0.112-0 floor is enforced by WithPostgresEndpoint()
        // itself; see https://github.com/microsoft/azure-databases-aspire/issues/71.
        builder.AddDocumentDB("documentdb")
            .WithImageTag("pg17-0.114.0")
            .WithPostgresEndpoint();

        var app = builder.Build();

        await app.RunAsync();
    }
}
