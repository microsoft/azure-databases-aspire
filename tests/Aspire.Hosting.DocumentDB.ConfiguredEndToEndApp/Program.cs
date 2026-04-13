// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting;

namespace Aspire.Hosting.DocumentDB.ConfiguredEndToEndApp;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        builder.AddDocumentDB("documentdb")
            .WithDataVolume()
            .AddDatabase("configureddb");

        var app = builder.Build();

        await app.RunAsync();
    }
}
