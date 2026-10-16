// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting;

namespace Aspire.Hosting.DocumentDB.ConfiguredEndToEndApp;

public class Program
{
    public const string HostPortEnvironmentVariable = "DOCUMENTDB_CONFIGURED_HOST_PORT";
    private const int DefaultHostPort = 10261;

    public static async Task Main(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        builder.AddDocumentDB("documentdb")
            .WithHostPort(GetConfiguredHostPort())
            .AddDatabase("configureddb");

        // Test containers must not report usage telemetry; a CI runner's variables never reach them.
        foreach (var server in builder.Resources.OfType<Aspire.Hosting.ApplicationModel.DocumentDBServerResource>().ToList())
        {
            builder.CreateResourceBuilder(server).WithoutUsageTelemetry();
        }

        var app = builder.Build();

        await app.RunAsync();
    }

    private static int GetConfiguredHostPort()
    {
        var configuredHostPort = Environment.GetEnvironmentVariable(HostPortEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredHostPort))
        {
            return DefaultHostPort;
        }

        if (!int.TryParse(configuredHostPort, out var hostPort) || hostPort <= 0)
        {
            throw new InvalidOperationException(
                $"{HostPortEnvironmentVariable} must be a positive integer TCP port, but was '{configuredHostPort}'.");
        }

        return hostPort;
    }
}
