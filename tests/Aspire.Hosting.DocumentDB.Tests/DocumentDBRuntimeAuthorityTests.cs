// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Hosting.DocumentDB.Tests;

/// <summary>
/// The three things a run has to agree with the container runtime about before any storage rule
/// means anything: which image is being started, what the runtime is being told on the command
/// line, and what the published connection string says about how the connection is secured.
/// </summary>
[Trait("Category", "Unit")]
public class DocumentDBRuntimeAuthorityTests
{
    private const string InterlockedTag = "pg17-0.116.0";
    private const string PreInterlockTag = "pg17-0.114.0";
    private const string PreCredentialParityTag = "pg17-0.111.0";

    // ------------------------------------------------------------------
    // The image the orchestrator is committed to
    // ------------------------------------------------------------------

    /// <summary>
    /// Aspire snapshots the image into the orchestrator's container spec while it prepares
    /// resources, which is before endpoints are allocated. Raising the tag afterwards used to make
    /// every version-dependent rule judge a release the container is not running.
    /// </summary>
    [Fact]
    public async Task RaisingTheImageTagAfterTheSealIsRefusedAtTheRunCheckpoint()
    {
        using var app = BuildStartedApplication(builder =>
            builder.AddDocumentDB("documentdb").WithImageTag(PreCredentialParityTag));

        await StartRunPhasesAsync(app);

        Server(app).Annotations.Add(new ContainerImageAnnotation
        {
            Registry = DocumentDBContainerImageTags.Registry,
            Image = DocumentDBContainerImageTags.Image,
            Tag = InterlockedTag,
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunContainerRuntimeArgsAsync(Server(app)));

        Assert.Contains("after the image it will run had already been settled", exception.Message, StringComparison.Ordinal);
        Assert.Contains(PreCredentialParityTag, exception.Message, StringComparison.Ordinal);
        Assert.Contains(InterlockedTag, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other direction: the model claims a release that interlocks the data directory while
    /// the container runs one that does not, so two clusters could open one directory with nothing
    /// reported.
    /// </summary>
    [Fact]
    public async Task LoweringTheImageTagAfterTheSealIsRefusedAtTheRunCheckpoint()
    {
        using var app = BuildStartedApplication(builder =>
            builder.AddDocumentDB("documentdb").WithImageTag(InterlockedTag));

        await StartRunPhasesAsync(app);

        Server(app).Annotations.Add(new ContainerImageAnnotation
        {
            Registry = DocumentDBContainerImageTags.Registry,
            Image = DocumentDBContainerImageTags.Image,
            Tag = PreInterlockTag,
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunContainerRuntimeArgsAsync(Server(app)));

        Assert.Contains("the image reference it will run changed", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The per-resource start event is where a caller sees the failure attributed to the resource,
    /// so the same change is refused there too rather than only at container creation.
    /// </summary>
    [Fact]
    public async Task AnImageChangedAfterTheSealIsAlsoRefusedAtTheResourceStartEvent()
    {
        using var app = BuildStartedApplication(builder =>
            builder.AddDocumentDB("documentdb").WithImageTag(InterlockedTag));

        await RunLifecycleHooksAsync(app);

        Server(app).Annotations.Add(new ContainerImageAnnotation
        {
            Registry = DocumentDBContainerImageTags.Registry,
            Image = DocumentDBContainerImageTags.Image,
            Tag = PreInterlockTag,
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PublishBeforeResourceStartedAsync(app, "documentdb"));

        Assert.Contains("after the image it will run had already been settled", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A registry change leaves the tag alone and still starts a different image.
    /// </summary>
    [Fact]
    public async Task ChangingTheRegistryAfterTheSealIsRefused()
    {
        using var app = BuildStartedApplication(builder =>
            builder.AddDocumentDB("documentdb").WithImageTag(InterlockedTag));

        await StartRunPhasesAsync(app);

        Server(app).Annotations.Add(new ContainerImageAnnotation
        {
            Registry = "mirror.example.invalid",
            Image = DocumentDBContainerImageTags.Image,
            Tag = InterlockedTag,
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunContainerRuntimeArgsAsync(Server(app)));

        Assert.Contains("the image reference it will run changed", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A build origin added after the seal changes what the reference resolves to and turns a
    /// published release into an image this package knows nothing about.
    /// </summary>
    [Fact]
    public async Task ADockerfileBuildAddedAfterTheSealIsRefused()
    {
        using var app = BuildStartedApplication(builder =>
            builder.AddDocumentDB("documentdb").WithImageTag(InterlockedTag));

        await StartRunPhasesAsync(app);

        Server(app).Annotations.Add(new DockerfileBuildAnnotation(
            AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "Dockerfile"), stage: null));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunContainerRuntimeArgsAsync(Server(app)));

        Assert.Contains("the container build it is produced by was added, removed or replaced", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Replacing a build definition with an identical-looking one is still a different build: the
    /// Dockerfile factory behind it cannot be compared by value at all.
    /// </summary>
    [Fact]
    public async Task ADockerfileBuildReplacedAfterTheSealIsRefusedEvenWhenTheReferenceIsUnchanged()
    {
        var contextPath = AppContext.BaseDirectory;
        var dockerfilePath = Path.Combine(AppContext.BaseDirectory, "Dockerfile");

        using var app = BuildStartedApplication(builder =>
        {
            var documentDB = builder.AddDocumentDB("documentdb");
            documentDB.Resource.Annotations.Add(new DockerfileBuildAnnotation(contextPath, dockerfilePath, stage: null));
        });

        await StartRunPhasesAsync(app);

        var resource = Server(app);
        var original = resource.Annotations.OfType<DockerfileBuildAnnotation>().Single();
        resource.Annotations.Remove(original);
        resource.Annotations.Add(new DockerfileBuildAnnotation(contextPath, dockerfilePath, stage: null));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunContainerRuntimeArgsAsync(resource));

        Assert.Contains("the container build it is produced by was added, removed or replaced", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The seal is taken after every <c>BeforeStartEvent</c> subscriber, so choosing the image from
    /// one — the ordinary place for late configuration — is accepted and is what the floors judge.
    /// </summary>
    [Fact]
    public async Task AnImageChosenByABeforeStartSubscriberIsSealedRatherThanRefused()
    {
        using var app = BuildStartedApplication(builder =>
        {
            var documentDB = builder.AddDocumentDB("documentdb").WithImageTag(PreCredentialParityTag);

            builder.Eventing.Subscribe<BeforeStartEvent>((_, _) =>
            {
                documentDB.WithImageTag(InterlockedTag);
                return Task.CompletedTask;
            });
        });

        await StartRunPhasesAsync(app);

        await RunContainerRuntimeArgsAsync(Server(app));
    }

    /// <summary>
    /// A resource nobody changes clears both checkpoints, which is what keeps the seal from being
    /// a check that only ever fires.
    /// </summary>
    [Fact]
    public async Task AnUnchangedResourceClearsTheRunCheckpoint()
    {
        using var app = BuildStartedApplication(builder =>
            builder.AddDocumentDB("documentdb").WithImageTag(InterlockedTag).WithDataVolume(name: "documentdb-unchanged"));

        await StartRunPhasesAsync(app);

        await RunContainerRuntimeArgsAsync(Server(app));
    }

    /// <summary>
    /// Re-applying the image the run was sealed on is not a change: the reference is what the
    /// orchestrator holds, not the annotation instance.
    /// </summary>
    [Fact]
    public async Task ReapplyingTheSameImageAfterTheSealIsAccepted()
    {
        using var app = BuildStartedApplication(builder =>
            builder.AddDocumentDB("documentdb").WithImageTag(InterlockedTag));

        await StartRunPhasesAsync(app);

        Server(app).Annotations.Add(new ContainerImageAnnotation
        {
            Registry = DocumentDBContainerImageTags.Registry,
            Image = DocumentDBContainerImageTags.Image,
            Tag = InterlockedTag,
        });

        await RunContainerRuntimeArgsAsync(Server(app));
    }

    /// <summary>
    /// A version floor still fires on an image that was chosen before the seal — the seal decides
    /// <em>which</em> image is judged, not whether it is.
    /// </summary>
    [Fact]
    public async Task AVariantFloorStillFiresOnTheSealedImage()
    {
        using var app = BuildStartedApplication(builder =>
            builder.AddDocumentDB("documentdb").WithImageTag("pg18-0.111.0"));

        await RunLifecycleHooksAsync(app);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PublishBeforeResourceStartedAsync(app, "documentdb"));

        Assert.Contains("upstream only publishes pg18 images", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A publish takes no seal: no container is ever prepared, the manifest is written from the
    /// model at serialization time, and sealing would report ordinary publish-time configuration as
    /// a change.
    /// </summary>
    [Fact]
    public async Task APublishTakesNoSealSoTheManifestKeepsJudgingTheModel()
    {
        var manifest = await ManifestUtils.PublishManifestAsync(builder =>
        {
            var documentDB = builder.AddDocumentDB("documentdb").WithImageTag(PreInterlockTag);

            builder.Eventing.Subscribe<Publishing.BeforePublishEvent>((_, _) =>
            {
                documentDB.WithImageTag(InterlockedTag);
                return Task.CompletedTask;
            });
        });

        var image = manifest["resources"]?["documentdb"]?["image"]?.GetValue<string>();

        Assert.NotNull(image);
        Assert.EndsWith(":" + InterlockedTag, image, StringComparison.Ordinal);
    }

    /// <summary>
    /// The seal is taken by one hook for the whole model, so a second resource does not register a
    /// second one.
    /// </summary>
    [Fact]
    public void RepeatedAddDocumentDBCallsRegisterOneSealHook()
    {
        using var app = BuildStartedApplication(builder =>
        {
            builder.AddDocumentDB("first");
            builder.AddDocumentDB("second");
        });

#pragma warning disable CS0618 // Type or member is obsolete
        var hooks = app.Services.GetServices<IDistributedApplicationLifecycleHook>()
            .Where(hook => hook.GetType().Name.Contains("DocumentDBImageSeal", StringComparison.Ordinal));
#pragma warning restore CS0618

        Assert.Single(hooks);
    }

    // ------------------------------------------------------------------
    // What the container runtime is told on the command line
    // ------------------------------------------------------------------

    /// <summary>
    /// Every spelling of a storage option the container runtime accepts, in both the split and the
    /// <c>option=value</c> form, and in the attached short form.
    /// </summary>
    public static TheoryData<string[], string> StorageRuntimeArguments => new()
    {
        { new[] { "--mount", "type=bind,source=/host,target=/data" }, "--mount" },
        { new[] { "--mount=type=bind,source=/host,target=/data" }, "--mount" },
        { new[] { "--mount", "type=bind,source=/host,target=/data,readonly" }, "--mount" },
        { new[] { "--mount", "type=bind,source=/host,target=/data,bind-propagation=shared" }, "--mount" },
        { new[] { "--mount", "type=tmpfs,destination=/data" }, "--mount" },
        { new[] { "-v", "/host:/data" }, "-v" },
        { new[] { "-v", "/host:/data:ro" }, "-v" },
        { new[] { "-v/host:/data" }, "-v" },
        { new[] { "--volume", "documentdb-shared:/data" }, "--volume" },
        { new[] { "--volume=documentdb-shared:/data" }, "--volume" },
        { new[] { "--volumes-from", "other-container" }, "--volumes-from" },
        { new[] { "--tmpfs", "/data" }, "--tmpfs" },
        { new[] { "--tmpfs=/data" }, "--tmpfs" },
        { new[] { "--read-only" }, "--read-only" },
    };

    [Theory]
    [MemberData(nameof(StorageRuntimeArguments))]
    public async Task AStorageRuntimeArgumentIsRefused(string[] arguments, string option)
    {
        var exception = await RunWithRuntimeArgumentsExpectingFailureAsync(arguments);

        Assert.Contains($"'{option}', which changes what the container mounts", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The data directory has one checked path through the guard; <c>--env</c> would be a second.
    /// </summary>
    public static TheoryData<string[], string> GuardOwnedEnvironmentRuntimeArguments => new()
    {
        { new[] { "--env", "DATA_PATH=/pgdata" }, "--env DATA_PATH" },
        { new[] { "--env=DATA_PATH=/pgdata" }, "--env DATA_PATH" },
        { new[] { "-e", "DATA_PATH=/pgdata" }, "-e DATA_PATH" },
        { new[] { "-eDATA_PATH=/pgdata" }, "-e DATA_PATH" },
        { new[] { "-e", "DATA_PATH" }, "-e DATA_PATH" },
        { new[] { "--env", "PASSWORD=hunter2" }, "--env PASSWORD" },
        { new[] { "--env", "USERNAME=root" }, "--env USERNAME" },
    };

    [Theory]
    [MemberData(nameof(GuardOwnedEnvironmentRuntimeArguments))]
    public async Task AGuardOwnedEnvironmentRuntimeArgumentIsRefused(string[] arguments, string option)
    {
        var exception = await RunWithRuntimeArgumentsExpectingFailureAsync(arguments);

        Assert.Contains($"'{option}', which sets an environment variable this package has already decided", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An environment file names variables this package cannot read without reading the file a
    /// second time, so it is refused rather than guessed at.
    /// </summary>
    [Fact]
    public async Task AnEnvironmentFileRuntimeArgumentIsRefused()
    {
        var exception = await RunWithRuntimeArgumentsExpectingFailureAsync(["--env-file", "/host/documentdb.env"]);

        Assert.Contains("'--env-file', which sets an environment variable this package has already decided", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("/host/documentdb.env", exception.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string[]> EntrypointRuntimeArguments => new()
    {
        new[] { "--entrypoint", "/bin/sh" },
        new[] { "--entrypoint=/bin/sh" },
    };

    [Theory]
    [MemberData(nameof(EntrypointRuntimeArguments))]
    public async Task AnEntrypointRuntimeArgumentIsRefused(string[] arguments)
    {
        var exception = await RunWithRuntimeArgumentsExpectingFailureAsync(arguments);

        Assert.Contains("'--entrypoint', which replaces the image's entry point", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The runtime reads the first bare operand as the image, which would displace the one the run
    /// was sealed on and turn the sealed image into the container's command.
    /// </summary>
    public static TheoryData<string[]> ImageDisplacingRuntimeArguments => new()
    {
        new[] { "alpine:3.20" },
        new[] { "--", "alpine:3.20" },
    };

    [Theory]
    [MemberData(nameof(ImageDisplacingRuntimeArguments))]
    public async Task AnOperandThatWouldBecomeTheImageIsRefused(string[] arguments)
    {
        var exception = await RunWithRuntimeArgumentsExpectingFailureAsync(arguments);

        Assert.Contains("which the runtime reads as the image", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A value that is only known later cannot be ruled out where the runtime reads an option name,
    /// and resolving it here would duplicate the evaluation Aspire is about to make of it.
    /// </summary>
    [Fact]
    public async Task ADeferredTokenInAnOptionNamePositionIsRefused()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var app = BuildStartedApplication(builder =>
            {
                var option = builder.AddParameter("documentdb-option", "--mount");

                builder.AddDocumentDB("documentdb")
                    .WithImageTag(InterlockedTag)
                    .WithContainerRuntimeArgs(context => context.Args.Add(option.Resource));
            });

            await StartRunPhasesAsync(app);
            await RunContainerRuntimeArgsAsync(Server(app));
        });

        Assert.Contains("a value that is only known later, in a position where the runtime reads an option name", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same rule one position along: the variable an <c>--env</c> sets is inside the deferred
    /// value, so whether it is one of this package's cannot be decided without resolving it.
    /// </summary>
    [Fact]
    public async Task ADeferredEnvironmentOperandIsRefused()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var app = BuildStartedApplication(builder =>
            {
                var assignment = builder.AddParameter("documentdb-assignment", "DATA_PATH=/pgdata");

                builder.AddDocumentDB("documentdb")
                    .WithImageTag(InterlockedTag)
                    .WithContainerRuntimeArgs(context =>
                    {
                        context.Args.Add("-e");
                        context.Args.Add(assignment.Resource);
                    });
            });

            await StartRunPhasesAsync(app);
            await RunContainerRuntimeArgsAsync(Server(app));
        });

        Assert.Contains("a value that is only known later", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The value of a storage option is where a host path lives and the value of an environment
    /// option is where a password lives, so neither ever reaches the diagnostic.
    /// </summary>
    [Fact]
    public async Task NoRuntimeArgumentValueReachesTheDiagnostic()
    {
        var mount = await RunWithRuntimeArgumentsExpectingFailureAsync(
            ["--mount", "type=bind,source=/host/secrets,target=/data"]);

        Assert.DoesNotContain("/host/secrets", mount.Message, StringComparison.Ordinal);

        var password = await RunWithRuntimeArgumentsExpectingFailureAsync(["--env", "PASSWORD=hunter2"]);

        Assert.DoesNotContain("hunter2", password.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Everything the container runtime accepts that this package has no business refusing. The
    /// <c>--label -v</c> case is the point of parsing rather than searching: <c>-v</c> there is the
    /// label, not a mount.
    /// </summary>
    public static TheoryData<string[]> HarmlessRuntimeArguments => new()
    {
        new[] { "--cap-add=SYS_PTRACE" },
        new[] { "--cap-add", "SYS_PTRACE" },
        new[] { "--network", "host" },
        new[] { "--memory", "512m" },
        new[] { "--memory=512m" },
        new[] { "--label", "-v" },
        new[] { "--label", "--mount" },
        new[] { "--label", "--entrypoint" },
        new[] { "--label=owner=documentdb" },
        new[] { "--env", "TZ=UTC" },
        new[] { "--env=TZ=UTC" },
        new[] { "-e", "TZ=UTC" },
        new[] { "-eTZ=UTC" },
        new[] { "-it" },
        new[] { "--pull=always" },
        new[] { "--platform", "linux/amd64" },
        new[] { "--dns", "1.1.1.1", "--dns-search", "example.invalid" },
        new[] { "--ulimit", "nofile=65536:65536" },
        new[] { "--" },
        new[] { "--sysctl", "net.core.somaxconn=1024", "--cap-add", "NET_ADMIN" },
    };

    [Theory]
    [MemberData(nameof(HarmlessRuntimeArguments))]
    public async Task AHarmlessRuntimeArgumentIsPassedThrough(string[] arguments)
    {
        using var app = BuildStartedApplication(builder =>
            builder.AddDocumentDB("documentdb")
                .WithImageTag(InterlockedTag)
                .WithContainerRuntimeArgs(arguments));

        await StartRunPhasesAsync(app);

        var passed = await RunContainerRuntimeArgsAsync(Server(app));

        Assert.Equal(arguments, passed);
    }

    /// <summary>
    /// A deferred value is fine where the runtime reads an operand: an option known to take one
    /// consumes exactly the next token, whatever it turns out to be.
    /// </summary>
    [Fact]
    public async Task ADeferredOperandOfAnUnrelatedOptionIsPassedThrough()
    {
        using var app = BuildStartedApplication(builder =>
        {
            var network = builder.AddParameter("documentdb-network", "host");

            builder.AddDocumentDB("documentdb")
                .WithImageTag(InterlockedTag)
                .WithContainerRuntimeArgs(context =>
                {
                    context.Args.Add("--network");
                    context.Args.Add(network.Resource);
                });
        });

        await StartRunPhasesAsync(app);

        await RunContainerRuntimeArgsAsync(Server(app));
    }

    /// <summary>
    /// The arguments are read once, at the end, so a value another callback contributed is judged
    /// with the rest — which is the only place the whole line exists.
    /// </summary>
    [Fact]
    public async Task AStorageArgumentContributedByALaterCallbackIsStillRefused()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var app = BuildStartedApplication(builder =>
            {
                var documentDB = builder.AddDocumentDB("documentdb")
                    .WithImageTag(InterlockedTag)
                    .WithContainerRuntimeArgs("--cap-add", "SYS_PTRACE");

                builder.Eventing.Subscribe<BeforeStartEvent>((_, _) =>
                {
                    documentDB.WithContainerRuntimeArgs("--tmpfs", "/data");
                    return Task.CompletedTask;
                });
            });

            await StartRunPhasesAsync(app);
            await RunContainerRuntimeArgsAsync(Server(app));
        });

        Assert.Contains("'--tmpfs', which changes what the container mounts", exception.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // What the manifest says about how the connection is secured
    // ------------------------------------------------------------------

    /// <summary>
    /// The reported repro. Aspire writes the connection string before it evaluates a single
    /// environment callback, so a callback that turns insecure TLS off publishes
    /// <c>tlsInsecure=true</c> while the final model says certificates must be valid.
    /// </summary>
    [Fact]
    public async Task DisablingInsecureTlsFromAnEnvironmentCallbackFailsThePublish()
    {
        var log = await ManifestUtils.PublishManifestExpectingFailureAsync(builder =>
        {
            var documentDB = builder.AddDocumentDB("documentdb").WithImageTag(InterlockedTag);
            documentDB.WithEnvironment(_ => documentDB.AllowInsecureTls(false));
        });

        Assert.Contains(
            "whether its published connection string accepts an invalid TLS certificate changed",
            log,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisablingTlsFromAnEnvironmentCallbackFailsThePublish()
    {
        var log = await ManifestUtils.PublishManifestExpectingFailureAsync(builder =>
        {
            var documentDB = builder.AddDocumentDB("documentdb").WithImageTag(InterlockedTag);
            documentDB.WithEnvironment(_ => documentDB.UseTls(false));
        });

        Assert.Contains(
            "whether its published connection string uses TLS changed",
            log,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnablingTlsFromAnEnvironmentCallbackFailsThePublish()
    {
        var log = await ManifestUtils.PublishManifestExpectingFailureAsync(builder =>
        {
            var documentDB = builder.AddDocumentDB("documentdb").WithImageTag(InterlockedTag).UseTls(false);
            documentDB.WithEnvironment(_ => documentDB.UseTls(true));
        });

        Assert.Contains(
            "whether its published connection string uses TLS changed",
            log,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A change that reaches the published string without touching either flag is caught with them,
    /// because the expression that was written is recorded beside them.
    /// </summary>
    [Fact]
    public async Task AConnectionStringChangedFromAnEnvironmentCallbackFailsThePublish()
    {
        var log = await ManifestUtils.PublishManifestExpectingFailureAsync(builder =>
        {
            var documentDB = builder.AddDocumentDB("documentdb").WithImageTag(InterlockedTag);

            documentDB.WithEnvironment(_ =>
                documentDB.Resource.Annotations.Add(new EndpointAnnotation(
                    System.Net.Sockets.ProtocolType.Tcp,
                    name: "tcp",
                    targetPort: 20260)));
        });

        Assert.Contains("DocumentDB resource 'documentdb' was changed while its manifest entry was being written", log, StringComparison.Ordinal);
    }

    /// <summary>
    /// The failure names the kind of change and nothing else: the connection string it is about
    /// carries the resource's credentials.
    /// </summary>
    [Fact]
    public async Task NoConnectionStringOrCredentialReachesTheFailureMessage()
    {
        var log = await ManifestUtils.PublishManifestExpectingFailureAsync(builder =>
        {
            var password = builder.AddParameter("documentdb-password", "hunter2", secret: true);
            var documentDB = builder.AddDocumentDB("documentdb", password: password).WithImageTag(InterlockedTag);
            documentDB.WithEnvironment(_ => documentDB.AllowInsecureTls(false));
        });

        Assert.DoesNotContain("hunter2", log, StringComparison.Ordinal);
        Assert.DoesNotContain("mongodb://", log, StringComparison.Ordinal);
        Assert.DoesNotContain("tlsInsecure", log, StringComparison.Ordinal);
    }

    /// <summary>
    /// A database publishes nothing but the connection string its parent builds, so the same
    /// mutation changes what every child ships. Written through a caller's own manifest callback,
    /// which is also what proves the checkpoint hands the writing on rather than replacing it.
    /// </summary>
    [Fact]
    public async Task ADatabaseConnectionStringChangedWhileItIsWrittenFailsThePublish()
    {
        var log = await ManifestUtils.PublishManifestExpectingFailureAsync(builder =>
        {
            var documentDB = builder.AddDocumentDB("documentdb").WithImageTag(InterlockedTag);
            var database = documentDB.AddDatabase("orders");

            database.WithManifestPublishingCallback(context =>
            {
                documentDB.AllowInsecureTls(false);
                context.Writer.WriteString("type", "value.v0");
                context.WriteConnectionString(database.Resource);
                return Task.CompletedTask;
            });
        });

        Assert.Contains("DocumentDB database resource 'orders' was changed while its manifest entry was being written", log, StringComparison.Ordinal);
        Assert.Contains("whether it accepts an invalid TLS certificate changed", log, StringComparison.Ordinal);
    }

    /// <summary>
    /// A caller's own writer still writes the entry, and the checkpoint that judges it adds nothing
    /// to what is published.
    /// </summary>
    [Fact]
    public async Task ACustomDatabaseWriterStillProducesTheEntry()
    {
        var manifest = await ManifestUtils.PublishManifestAsync(builder =>
        {
            var documentDB = builder.AddDocumentDB("documentdb").WithImageTag(InterlockedTag);

            documentDB.AddDatabase("orders").WithManifestPublishingCallback(context =>
            {
                context.Writer.WriteString("type", "value.v0");
                context.Writer.WriteString("connectionString", "written-by-the-caller");
                return Task.CompletedTask;
            });
        });

        Assert.Equal(
            "written-by-the-caller",
            manifest["resources"]?["orders"]?["connectionString"]?.GetValue<string>());
    }

    /// <summary>
    /// A resource that publishes nothing has no published connection string to judge, so the
    /// exclusion is left in place.
    /// </summary>
    [Fact]
    public async Task ExcludeFromManifestStillExcludesADatabase()
    {
        var manifest = await ManifestUtils.PublishManifestAsync(builder =>
        {
            var documentDB = builder.AddDocumentDB("documentdb").WithImageTag(InterlockedTag);
            documentDB.AddDatabase("orders").ExcludeFromManifest();
        });

        Assert.Null(manifest["resources"]?["orders"]);
    }

    /// <summary>
    /// Setting a flag to the value it already has changes nothing that is published, so it is not
    /// reported.
    /// </summary>
    [Fact]
    public async Task ANoOpTlsCallFromAnEnvironmentCallbackIsNotReported()
    {
        var manifest = await ManifestUtils.PublishManifestAsync(builder =>
        {
            var documentDB = builder.AddDocumentDB("documentdb").WithImageTag(InterlockedTag);

            documentDB.WithEnvironment(_ =>
            {
                documentDB.UseTls(true);
                documentDB.AllowInsecureTls(true);
            });
        });

        var connectionString = manifest["resources"]?["documentdb"]?["connectionString"]?.GetValue<string>();

        Assert.NotNull(connectionString);
        Assert.Contains("tls=true", connectionString, StringComparison.Ordinal);
        Assert.Contains("tlsInsecure=true", connectionString, StringComparison.Ordinal);
    }

    /// <summary>
    /// The ordinary case: a callback that only sets an environment variable is exactly what
    /// environment callbacks are for.
    /// </summary>
    [Fact]
    public async Task AnEnvironmentOnlyCallbackIsNotReported()
    {
        var manifest = await ManifestUtils.PublishManifestAsync(builder =>
            builder.AddDocumentDB("documentdb")
                .WithImageTag(InterlockedTag)
                .WithEnvironment("TZ", "UTC"));

        Assert.Equal("UTC", manifest["resources"]?["documentdb"]?["env"]?["TZ"]?.GetValue<string>());
    }

    /// <summary>
    /// The declared TLS state is what both entries publish when nothing mutates, which is the
    /// baseline the failures above are departures from.
    /// </summary>
    [Theory]
    [InlineData(true, true, "tls=true", "tlsInsecure=true")]
    [InlineData(true, false, "tls=true", null)]
    [InlineData(false, true, null, null)]
    public async Task TheDeclaredTlsStateIsWhatTheServerAndItsDatabasePublish(
        bool useTls,
        bool allowInsecureTls,
        string? expectedTls,
        string? expectedInsecure)
    {
        var manifest = await ManifestUtils.PublishManifestAsync(builder =>
        {
            var documentDB = builder.AddDocumentDB("documentdb")
                .WithImageTag(InterlockedTag)
                .UseTls(useTls)
                .AllowInsecureTls(allowInsecureTls);

            documentDB.AddDatabase("orders");
        });

        foreach (var name in new[] { "documentdb", "orders" })
        {
            var connectionString = manifest["resources"]?[name]?["connectionString"]?.GetValue<string>();
            Assert.NotNull(connectionString);

            AssertContainment(connectionString, "tls=true", expectedTls is not null);
            AssertContainment(connectionString, "tlsInsecure=true", expectedInsecure is not null);
        }

        static void AssertContainment(string connectionString, string fragment, bool expected)
        {
            if (expected)
            {
                Assert.Contains(fragment, connectionString, StringComparison.Ordinal);
            }
            else
            {
                Assert.DoesNotContain(fragment, connectionString, StringComparison.Ordinal);
            }
        }
    }

    // ------------------------------------------------------------------
    // Harness
    // ------------------------------------------------------------------

    /// <summary>
    /// A run-mode application. Aspire's own <c>BeforeStartEvent</c> subscriber names the containers
    /// it is about to create, which needs the orchestrator's paths to be configured; nothing here
    /// starts one, so any real path satisfies it.
    /// </summary>
    private static DistributedApplication BuildStartedApplication(Action<IDistributedApplicationBuilder> configure)
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            $"DcpPublisher:CliPath={AppContext.BaseDirectory}",
            $"DcpPublisher:DashboardPath={AppContext.BaseDirectory}");

        configure(builder.Builder);
        return builder.Build();
    }

    private static DocumentDBServerResource Server(DistributedApplication app) =>
        app.Services.GetRequiredService<DistributedApplicationModel>()
            .Resources.OfType<DocumentDBServerResource>().Single();

    /// <summary>
    /// The phases a run goes through before the orchestrator prepares a container, in the order
    /// Aspire runs them: every <c>BeforeStartEvent</c> subscriber, then every lifecycle hook — which
    /// is where the image is sealed — then the per-resource start event.
    /// </summary>
    private static async Task StartRunPhasesAsync(DistributedApplication app)
    {
        await PublishBeforeStartAsync(app);
        await RunLifecycleHooksAsync(app);
        await PublishBeforeResourceStartedAsync(app, Server(app).Name);
    }

    private static Task PublishBeforeStartAsync(DistributedApplication app)
    {
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        return app.Services.GetRequiredService<IDistributedApplicationEventing>().PublishAsync(
            new BeforeStartEvent(app.Services, model),
            EventDispatchBehavior.BlockingSequential,
            CancellationToken.None);
    }

    private static Task PublishBeforeResourceStartedAsync(DistributedApplication app, string resourceName)
    {
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource = model.Resources.OfType<DocumentDBServerResource>().Single(r => r.Name == resourceName);

        return app.Services.GetRequiredService<IDistributedApplicationEventing>().PublishAsync(
            new BeforeResourceStartedEvent(resource, app.Services),
            EventDispatchBehavior.BlockingSequential,
            CancellationToken.None);
    }

#pragma warning disable CS0618 // Type or member is obsolete
    private static async Task RunLifecycleHooksAsync(DistributedApplication app)
    {
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        foreach (var hook in app.Services.GetServices<IDistributedApplicationLifecycleHook>())
        {
            await hook.BeforeStartAsync(model, CancellationToken.None);
        }
    }
#pragma warning restore CS0618

    /// <summary>
    /// Invokes the resource's container-runtime-argument callbacks the way Aspire's container
    /// creator does: in annotation order, over one shared list, with no result cache.
    /// </summary>
    private static async Task<string[]> RunContainerRuntimeArgsAsync(DocumentDBServerResource resource)
    {
        var args = new List<object>();
        var context = new ContainerRuntimeArgsCallbackContext(args, CancellationToken.None);

        foreach (var annotation in resource.Annotations.OfType<ContainerRuntimeArgsCallbackAnnotation>())
        {
            await annotation.Callback(context);
        }

        return [.. args.Select(argument => argument as string ?? argument.ToString()!)];
    }

    private static async Task<InvalidOperationException> RunWithRuntimeArgumentsExpectingFailureAsync(string[] arguments)
    {
        using var app = BuildStartedApplication(builder =>
            builder.AddDocumentDB("documentdb")
                .WithImageTag(InterlockedTag)
                .WithContainerRuntimeArgs(arguments));

        await StartRunPhasesAsync(app);

        return await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunContainerRuntimeArgsAsync(Server(app)));
    }
}
