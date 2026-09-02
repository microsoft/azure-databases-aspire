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

    /// <summary>
    /// Storage added straight to the container runtime, where no annotation records it and no
    /// storage rule can see it.
    /// </summary>
    public const string RawMountRuntimeArgumentScenario = "raw-mount-runtime-argument";

    /// <summary>The same, written as a read-only <c>--volume=</c> over the data directory.</summary>
    public const string RawReadOnlyVolumeRuntimeArgumentScenario = "raw-read-only-volume-runtime-argument";

    /// <summary>A tmpfs on the data directory: storage that does not survive the container.</summary>
    public const string RawTmpfsRuntimeArgumentScenario = "raw-tmpfs-runtime-argument";

    /// <summary>The whole root filesystem made unwritable behind the storage rules.</summary>
    public const string RawReadOnlyRootRuntimeArgumentScenario = "raw-read-only-root-runtime-argument";

    /// <summary>
    /// The data directory moved by a raw <c>--env</c>, past the one checked path the guard owns.
    /// </summary>
    public const string RawDataPathRuntimeArgumentScenario = "raw-data-path-runtime-argument";

    /// <summary>The image's entry point replaced without touching the model.</summary>
    public const string RawEntrypointRuntimeArgumentScenario = "raw-entrypoint-runtime-argument";

    /// <summary>
    /// A Podman-only option that mounts a file into the container. Which runtime is behind the
    /// arguments is not this package's to know, so the union grammar refuses it either way.
    /// </summary>
    public const string RawPodmanSecretRuntimeArgumentScenario = "raw-podman-secret-runtime-argument";

    /// <summary>The Podman-only import of the entire host environment.</summary>
    public const string RawPodmanEnvHostRuntimeArgumentScenario = "raw-podman-env-host-runtime-argument";

    /// <summary>The Podman-only replacement of the image by a directory.</summary>
    public const string RawPodmanRootfsRuntimeArgumentScenario = "raw-podman-rootfs-runtime-argument";

    /// <summary>
    /// A container-runtime argument whose value only arrives later, where the runtime reads an
    /// option name.
    /// </summary>
    public const string DeferredRuntimeArgumentScenario = "deferred-runtime-argument";

    /// <summary>
    /// Ordinary container-runtime arguments, which have to reach the container runtime untouched.
    /// The label is what the test reads back off the created container.
    /// </summary>
    public const string HarmlessRuntimeArgumentScenario = "harmless-runtime-argument";

    /// <summary>The label the harmless-argument scenario passes through.</summary>
    public const string HarmlessRuntimeArgumentLabel = "documentdb.storage-guard=harmless";

    /// <summary>
    /// The image a run is sealed on, and — beside it — a plain container that rewrites its own
    /// image after its endpoints are allocated. The container runtime launches the image each was
    /// prepared with, which is what the seal exists to keep this package honest about.
    /// </summary>
    public const string LaunchedImageScenario = "launched-image";

    /// <summary>The tag the launched-image scenario pins, and the images the probe moves between.</summary>
    public const string LaunchedImageTag = "pg17-0.116.0";

    public const string ProbeImage = "docker.io/library/alpine";

    public const string ProbePreparedTag = "3.20";

    public const string ProbeMutatedTag = "3.19";

    /// <summary>A DocumentDB image raised to a newer release after its endpoints are allocated.</summary>
    public const string LateImageUpgradeScenario = "late-image-upgrade";

    /// <summary>The same in the other direction, which is what makes the storage rules over-promise.</summary>
    public const string LateImageDowngradeScenario = "late-image-downgrade";

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

            case RawMountRuntimeArgumentScenario:
                builder.AddDocumentDB("documentdb")
                    .WithContainerRuntimeArgs("--mount", "type=volume,source=documentdb-raw-mount,target=/data");
                break;

            case RawReadOnlyVolumeRuntimeArgumentScenario:
                builder.AddDocumentDB("documentdb")
                    .WithContainerRuntimeArgs("--volume=documentdb-raw-shared:/data:ro");
                break;

            case RawTmpfsRuntimeArgumentScenario:
                builder.AddDocumentDB("documentdb")
                    .WithDataVolume(name: "documentdb-raw-tmpfs-data")
                    .WithContainerRuntimeArgs("--tmpfs", "/data");
                break;

            case RawReadOnlyRootRuntimeArgumentScenario:
                builder.AddDocumentDB("documentdb")
                    .WithContainerRuntimeArgs("--read-only");
                break;

            case RawDataPathRuntimeArgumentScenario:
                builder.AddDocumentDB("documentdb")
                    .WithDataVolume(name: "documentdb-raw-data-path-data")
                    .WithContainerRuntimeArgs("--env=DATA_PATH=/pgdata");
                break;

            case RawEntrypointRuntimeArgumentScenario:
                builder.AddDocumentDB("documentdb")
                    .WithContainerRuntimeArgs("--entrypoint", "/bin/sh");
                break;

            case RawPodmanSecretRuntimeArgumentScenario:
                builder.AddDocumentDB("documentdb")
                    .WithContainerRuntimeArgs("--secret", "documentdb-secret,type=mount,target=/data/pgdata");
                break;

            case RawPodmanEnvHostRuntimeArgumentScenario:
                builder.AddDocumentDB("documentdb")
                    .WithDataVolume(name: "documentdb-podman-env-host-data")
                    .WithContainerRuntimeArgs("--env-host");
                break;

            case RawPodmanRootfsRuntimeArgumentScenario:
                builder.AddDocumentDB("documentdb")
                    .WithContainerRuntimeArgs("--rootfs");
                break;

            case DeferredRuntimeArgumentScenario:
                var runtimeOption = builder.AddParameter("documentdb-runtime-option", "--mount");
                builder.AddDocumentDB("documentdb")
                    .WithContainerRuntimeArgs(context => context.Args.Add(runtimeOption.Resource));
                break;

            case HarmlessRuntimeArgumentScenario:
                builder.AddDocumentDB("documentdb")
                    .WithDataVolume(name: "documentdb-harmless-args-data")
                    .WithContainerRuntimeArgs("--label", HarmlessRuntimeArgumentLabel, "--label", "-v");
                break;

            case LaunchedImageScenario:
                builder.AddDocumentDB("documentdb").WithImageTag(LaunchedImageTag);

                // The control, and deliberately not a DocumentDB resource: it rewrites its own
                // image annotation the moment its endpoints are allocated, with none of this
                // package's code involved, so what the container runtime ends up launching is a
                // statement about Aspire rather than about the guard.
                var probe = builder.AddContainer("imageprobe", ProbeImage, ProbePreparedTag)
                    .WithArgs("sleep", "600");

                builder.Eventing.Subscribe<ResourceEndpointsAllocatedEvent>(probe.Resource, (_, _) =>
                {
                    probe.WithImage(ProbeImage, ProbeMutatedTag);
                    return Task.CompletedTask;
                });

                // A container with no endpoints is not guaranteed the event above, so the same
                // mutation is made again at the last phase before the container is created. Both
                // are after the image was snapshotted into the container spec.
                builder.Eventing.Subscribe<BeforeResourceStartedEvent>(probe.Resource, (_, _) =>
                {
                    probe.WithImage(ProbeImage, ProbeMutatedTag);
                    return Task.CompletedTask;
                });
                break;

            case LateImageUpgradeScenario:
                var upgraded = builder.AddDocumentDB("documentdb").WithImageTag("pg17-0.111.0");

                builder.Eventing.Subscribe<ResourceEndpointsAllocatedEvent>(upgraded.Resource, (_, _) =>
                {
                    upgraded.WithImageTag(LaunchedImageTag);
                    return Task.CompletedTask;
                });
                break;

            case LateImageDowngradeScenario:
                var downgraded = builder.AddDocumentDB("documentdb")
                    .WithImageTag(LaunchedImageTag)
                    .WithDataVolume(name: "documentdb-late-downgrade-data");

                builder.Eventing.Subscribe<ResourceEndpointsAllocatedEvent>(downgraded.Resource, (_, _) =>
                {
                    downgraded.WithImageTag("pg17-0.114.0");
                    return Task.CompletedTask;
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
