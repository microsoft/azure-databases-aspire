// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting;

namespace Aspire.Hosting.DocumentDB.EndToEndApp;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        builder.AddDocumentDB("documentdb")
            .AddDatabase("appdb");

        // Test containers must not report usage telemetry; a CI runner's variables never reach them.
        foreach (var server in builder.Resources.OfType<Aspire.Hosting.ApplicationModel.DocumentDBServerResource>().ToList())
        {
            builder.CreateResourceBuilder(server).WithoutUsageTelemetry();
        }

        var app = builder.Build();

        await app.RunAsync();
    }
}
