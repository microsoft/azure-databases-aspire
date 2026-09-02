// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging.Abstractions;

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

    /// <summary>
    /// A command-line token whose value only arrives later, sitting where the entrypoint reads an
    /// option name. It could resolve to <c>--data-path</c>.
    /// </summary>
    public const string DeferredDataPathArgumentScenario = "deferred-data-path-argument";

    /// <summary>
    /// A lifecycle hook — which Aspire runs *after* BeforeStartEvent — moves DATA_PATH onto a
    /// read-only mount. The guard has to still be the last word on the environment.
    /// </summary>
    public const string LateDataPathOverrideScenario = "late-data-path-override";

    /// <summary>
    /// A usable configuration that the guard only warns about: the data mount does not cover the
    /// path the image declares as a volume. The warning has to actually reach the operator.
    /// </summary>
    public const string DeclaredVolumeWarningScenario = "declared-volume-warning";

    /// <summary>
    /// A subscriber registered after <c>AddDocumentDB</c> builds the resource's configuration
    /// through the public <see cref="ExecutionConfigurationBuilder"/> — which records the storage
    /// verdict for the rest of the run — and only then mounts the data directory read-only. No
    /// rule runs again, so the recorded verdict has to be re-checked.
    /// </summary>
    public const string LateReadOnlyDataMountScenario = "late-read-only-data-mount";

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

            case DeferredDataPathArgumentScenario:
                var flag = builder.AddParameter("documentdb-flag", "--data-path");
                builder.AddDocumentDB("documentdb")
                    .WithDataVolume()
                    .WithArgs(flag, "/pgdata");
                break;

            case LateDataPathOverrideScenario:
                var documentDB = builder.AddDocumentDB("documentdb")
                    .WithDataVolume()
                    .WithVolume("documentdb-late-read-only", "/pgdata", isReadOnly: true);

                // Registered after AddDocumentDB, so it runs after the guard installs itself. The
                // guard retakes the last position before the resource starts, so this is still seen.
                builder.Eventing.Subscribe<BeforeStartEvent>((_, _) =>
                {
                    documentDB.WithEnvironment("DATA_PATH", "/pgdata");
                    return Task.CompletedTask;
                });
                break;

            case DeclaredVolumeWarningScenario:
                // Pinned to the first tag whose image declares /data as a volume, so the warning
                // does not depend on which version the package currently defaults to.
                builder.AddDocumentDB("documentdb")
                    .WithImageTag("pg17-0.116.0")
                    .WithDataVolume(targetPath: "/pgdata");
                break;

            case LateReadOnlyDataMountScenario:
                var gathered = builder.AddDocumentDB("documentdb");

                builder.Eventing.Subscribe<BeforeStartEvent>(async (_, token) =>
                {
                    await ExecutionConfigurationBuilder.Create(gathered.Resource)
                        .WithArgumentsConfig()
                        .WithEnvironmentVariablesConfig()
                        .BuildAsync(
                            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
                            NullLogger.Instance,
                            token);

                    gathered.Resource.Annotations.Add(new ContainerMountAnnotation(
                        "documentdb-late-read-only-data", "/data", ContainerMountType.Volume, isReadOnly: true));
                });
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
