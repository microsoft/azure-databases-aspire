// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting;

namespace Aspire.Hosting.DocumentDB.StorageGuardEndToEndApp;

/// <summary>
/// A scenario-driven AppHost whose only purpose is to be started for real with a data-storage
/// configuration the guard must refuse. The scenario is selected through
/// <see cref="ScenarioEnvironmentVariable"/> so one project serves every case.
/// </summary>
public class Program
{
    public const string ScenarioEnvironmentVariable = "DOCUMENTDB_STORAGE_GUARD_SCENARIO";

    /// <summary>A raw read-only mount on the data directory. PostgreSQL cannot initialise there.</summary>
    public const string ReadOnlyDataMountScenario = "read-only-data-mount";

    /// <summary>
    /// A mount target that reaches above the container root. Docker clamps it onto the data
    /// directory instead of refusing it.
    /// </summary>
    public const string AboveRootMountTargetScenario = "above-root-mount-target";

    /// <summary>The entrypoint's own <c>--data-path</c>, a second channel for the same setting.</summary>
    public const string ReservedDataPathArgumentScenario = "reserved-data-path-argument";

    /// <summary>Two resources whose data directories are one directory.</summary>
    public const string SharedDataDirectoryScenario = "shared-data-directory";

    public static async Task Main(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        switch (GetScenario())
        {
            case AboveRootMountTargetScenario:
                builder.AddDocumentDB("documentdb")
                    .WithVolume("documentdb-above-root-data", "/../data");
                break;

            case ReservedDataPathArgumentScenario:
                builder.AddDocumentDB("documentdb")
                    .WithDataVolume()
                    .WithArgs("--data-path", "/pgdata");
                break;

            case SharedDataDirectoryScenario:
                builder.AddDocumentDB("documentdb").WithDataVolume(name: "documentdb-shared-data");
                builder.AddDocumentDB("documentdb-peer").WithDataVolume(name: "documentdb-shared-data");
                break;

            case ReadOnlyDataMountScenario:
            default:
                builder.AddDocumentDB("documentdb")
                    .WithVolume("documentdb-read-only-data", "/data", isReadOnly: true);
                break;
        }

        using var app = builder.Build();
        await app.RunAsync();
    }

    private static string GetScenario() =>
        Environment.GetEnvironmentVariable(ScenarioEnvironmentVariable) ?? ReadOnlyDataMountScenario;
}
