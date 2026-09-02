// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Hosting.DocumentDB.Tests;

/// <summary>
/// Coverage for the image seal: the one authoritative record of what DCP will launch, taken at
/// the last phase that precedes container preparation.
/// </summary>
/// <remarks>
/// The floors in <see cref="AddDocumentDBTest"/> judge whatever the model says, which is correct
/// as long as the model still decides what runs. It stops being true at
/// <c>ContainerCreator.PrepareObjects()</c>: that composes the image reference once, writes it
/// into the DCP container spec, and nothing re-reads the annotation afterwards. Every event a
/// resource raises from that point on — <c>ResourceEndpointsAllocatedEvent</c>,
/// <c>BeforeResourceStartedEvent</c> — and the container-runtime-arguments callback all run after
/// the decision has been made. These tests pin that the seal is taken before that point, that it
/// is what the floors are judged on, and that a later change is refused rather than believed.
/// </remarks>
[Trait("Category", "Unit")]
public class DocumentDBImageSealTests
{
    private const string SealMessageFragment = "changed the container image it will run after";

    // ------------------------------------------------------------------
    // What the seal records, and when
    // ------------------------------------------------------------------

    [Fact]
    public async Task ARunSealsEveryDocumentDBResource()
    {
        var appBuilder = CreateRunBuilder();
        appBuilder.AddDocumentDB("first");
        appBuilder.AddDocumentDB("second");

        using var app = appBuilder.Build();
        await RunBeforeStartPhaseAsync(app);

        foreach (var resource in ServerResources(app))
        {
            Assert.True(HasSeal(resource), $"'{resource.Name}' was not sealed.");
        }
    }

    [Fact]
    public async Task APublishTakesNoSealSoTheManifestKeepsJudgingTheLiveModel()
    {
        // DCP never runs in a publish and the manifest is written from the model at serialization
        // time, so a seal would make the checkpoint judge an image that is not the one published.
        var appBuilder = DistributedApplication.CreateBuilder(
            ["--operation", "publish", "--publisher", "manifest", "--output-path", "./"]);
        appBuilder.AddDocumentDB("DocumentDB");

        using var app = appBuilder.Build();
        await RunBeforeStartPhaseAsync(app);

        Assert.False(HasSeal(SingleServerResource(app)));
    }

    [Fact]
    public async Task TheSealIsTakenAfterEveryBeforeStartSubscriber()
    {
        // A subscriber registered after AddDocumentDB is exactly the case this package cannot
        // outrun by subscribing, and it is still ordinary pre-start configuration: the lifecycle
        // hook runs after all of them, so the seal is the image that subscriber chose.
        var appBuilder = CreateRunBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB").WithPostgresEndpoint();

        appBuilder.Eventing.Subscribe<BeforeStartEvent>((_, _) =>
        {
            documentDB.WithImageTag("pg17-0.114.0");
            return Task.CompletedTask;
        });

        using var app = appBuilder.Build();
        await RunBeforeStartPhaseAsync(app);

        var resource = SingleServerResource(app);
        Assert.Empty(await RunContainerRuntimeArgsAsync(resource));
        await PublishBeforeResourceStartedAsync(app, resource);
    }

    [Fact]
    public async Task AnIllegalImageChosenBeforeTheSealStillFailsWithTheFloorMessage()
    {
        // The seal changes nothing about which images are legal. A pre-start choice is still the
        // final image, so it is still judged by the floor - with the floor's own message.
        var appBuilder = CreateRunBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB").WithPostgresEndpoint();

        appBuilder.Eventing.Subscribe<BeforeStartEvent>((_, _) =>
        {
            documentDB.WithImageTag("pg17-0.111.0");
            return Task.CompletedTask;
        });

        using var app = appBuilder.Build();
        await RunBeforeStartPhaseAsync(app);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunContainerRuntimeArgsAsync(SingleServerResource(app)));

        Assert.Contains("WithPostgresEndpoint() requires DocumentDB", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SealMessageFragment, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnchangedResourceClearsBothCheckpoints()
    {
        var appBuilder = CreateRunBuilder();
        appBuilder.AddDocumentDB("DocumentDB").WithPostgresEndpoint();

        using var app = appBuilder.Build();
        await RunBeforeStartPhaseAsync(app);

        var resource = SingleServerResource(app);
        Assert.Empty(await RunContainerRuntimeArgsAsync(resource));
        await PublishBeforeResourceStartedAsync(app, resource);
    }

    [Fact]
    public async Task AnExplicitStartResourceIsStillSealedAndStillClearsTheRunCheckpoint()
    {
        // WithExplicitStart() only delays the container's creation. The seal is still taken with
        // the rest of the model, and the runtime checkpoint still runs when the container is
        // eventually created.
        var appBuilder = CreateRunBuilder();
        appBuilder.AddDocumentDB("DocumentDB").WithPostgresEndpoint().WithExplicitStart();

        using var app = appBuilder.Build();
        await RunBeforeStartPhaseAsync(app);

        var resource = SingleServerResource(app);
        Assert.True(HasSeal(resource));
        Assert.Empty(await RunContainerRuntimeArgsAsync(resource));
    }

    [Fact]
    public async Task ReapplyingTheSameImageAfterTheSealIsAccepted()
    {
        // The seal refuses a change, not a call: re-stating the same tag composes the same
        // reference, which is the same container.
        var appBuilder = CreateRunBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB");

        using var app = appBuilder.Build();
        await RunBeforeStartPhaseAsync(app);

        documentDB.WithImageTag(DocumentDBContainerImageTags.Tag);

        Assert.Empty(await RunContainerRuntimeArgsAsync(SingleServerResource(app)));
    }

    // ------------------------------------------------------------------
    // Both directions of a change made after the seal
    // ------------------------------------------------------------------

    [Fact]
    public async Task UpgradingTheImageAfterTheSealFailsTheRunCheckpoint()
    {
        // Today's false accept: the floor reads the mutated annotation and clears the resource,
        // while the prepared container still launches the tag that cannot authenticate.
        var app = await BuildLateMutatedApplicationAsync("pg17-0.111.0", "pg17-0.116.0", postgresEndpoint: true);

        using (app)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => RunContainerRuntimeArgsAsync(SingleServerResource(app)));

            AssertNamesBothReferences(ex.Message, "pg17-0.111.0", "pg17-0.116.0");
        }
    }

    [Fact]
    public async Task DowngradingTheImageAfterTheSealFailsTheRunCheckpoint()
    {
        // The reverse direction: the model believes a newer release than DCP runs.
        var app = await BuildLateMutatedApplicationAsync("pg17-0.116.0", "pg17-0.114.0", postgresEndpoint: false);

        using (app)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => RunContainerRuntimeArgsAsync(SingleServerResource(app)));

            AssertNamesBothReferences(ex.Message, "pg17-0.116.0", "pg17-0.114.0");
        }
    }

    [Fact]
    public async Task UpgradingTheImageAfterTheSealAlsoFailsTheResourceStartEvent()
    {
        var app = await BuildLateMutatedApplicationAsync("pg17-0.111.0", "pg17-0.116.0", postgresEndpoint: true);

        using (app)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => PublishBeforeResourceStartedAsync(app, SingleServerResource(app)));

            AssertNamesBothReferences(ex.Message, "pg17-0.111.0", "pg17-0.116.0");
        }
    }

    [Fact]
    public async Task DowngradingTheImageAfterTheSealAlsoFailsTheResourceStartEvent()
    {
        var app = await BuildLateMutatedApplicationAsync("pg17-0.116.0", "pg17-0.114.0", postgresEndpoint: false);

        using (app)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => PublishBeforeResourceStartedAsync(app, SingleServerResource(app)));

            AssertNamesBothReferences(ex.Message, "pg17-0.116.0", "pg17-0.114.0");
        }
    }

    [Fact]
    public async Task ChangingTheRegistryAfterTheSealFailsTheRunCheckpoint()
    {
        // The seal is the composed reference, not the tag, so a mirror swapped in late is caught
        // even though every version-bearing part of the tag is unchanged.
        var appBuilder = CreateRunBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB");

        using var app = appBuilder.Build();
        await RunBeforeStartPhaseAsync(app);

        await PublishResourceEndpointsAllocatedAsync(app, SingleServerResource(app), () =>
            documentDB.WithImageRegistry("mirror.example.com"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunContainerRuntimeArgsAsync(SingleServerResource(app)));

        Assert.Contains(DocumentDBContainerImageTags.Registry, ex.Message, StringComparison.Ordinal);
        Assert.Contains("mirror.example.com", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PinningADigestAfterTheSealFailsTheRunCheckpoint()
    {
        // A digest pin is exempt from the version floors because it names no release - which is
        // exactly why it must not be a way to swap the image out from under a sealed run.
        var appBuilder = CreateRunBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB");

        using var app = appBuilder.Build();
        await RunBeforeStartPhaseAsync(app);

        await PublishResourceEndpointsAllocatedAsync(app, SingleServerResource(app), () =>
            documentDB.WithImageSHA256(OlderReleaseDigest));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunContainerRuntimeArgsAsync(SingleServerResource(app)));

        Assert.Contains($"@sha256:{OlderReleaseDigest}", ex.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Build origin
    // ------------------------------------------------------------------

    [Fact]
    public async Task ADockerfileBuildOriginAddedAfterTheSealFailsTheRunCheckpoint()
    {
        // WithDockerfile is what makes TryGetContainerImageName return a locally built image
        // instead of the curated one, so adding it late replaces the image DCP already snapshotted.
        var appBuilder = CreateRunBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB");

        using var app = appBuilder.Build();
        await RunBeforeStartPhaseAsync(app);

        await PublishResourceEndpointsAllocatedAsync(app, SingleServerResource(app), () =>
            documentDB.WithDockerfile(AppContext.BaseDirectory));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunContainerRuntimeArgsAsync(SingleServerResource(app)));

        Assert.Contains(SealMessageFragment, ex.Message, StringComparison.Ordinal);
        Assert.Contains(DocumentDBContainerImageTags.Tag, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADockerfileBuildOriginRemovedAfterTheSealFailsTheRunCheckpoint()
    {
        var appBuilder = CreateRunBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB").WithDockerfile(AppContext.BaseDirectory);

        using var app = appBuilder.Build();
        await RunBeforeStartPhaseAsync(app);

        var resource = SingleServerResource(app);
        var build = Assert.Single(resource.Annotations.OfType<DockerfileBuildAnnotation>());

        await PublishResourceEndpointsAllocatedAsync(app, resource, () => resource.Annotations.Remove(build));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunContainerRuntimeArgsAsync(resource));

        Assert.Contains(SealMessageFragment, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADockerfileBuildOriginReplacedAfterTheSealFailsEvenWhenTheReferenceIsUnchanged()
    {
        // The build origin also decides what is built and what a publish emits, so a replacement
        // that happens to compose the same reference is still a different container.
        var appBuilder = CreateRunBuilder();
        appBuilder.AddDocumentDB("DocumentDB").WithDockerfile(AppContext.BaseDirectory);

        using var app = appBuilder.Build();
        await RunBeforeStartPhaseAsync(app);

        var resource = SingleServerResource(app);
        var build = Assert.Single(resource.Annotations.OfType<DockerfileBuildAnnotation>());

        var identical = new DockerfileBuildAnnotation(build.ContextPath, build.DockerfilePath, build.Stage)
        {
            ImageName = build.ImageName,
            ImageTag = build.ImageTag,
        };

        await PublishResourceEndpointsAllocatedAsync(app, resource, () =>
        {
            resource.Annotations.Remove(build);
            resource.Annotations.Add(identical);
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunContainerRuntimeArgsAsync(resource));

        Assert.Contains(SealMessageFragment, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADockerfileBuildOriginChosenBeforeTheSealIsAccepted()
    {
        var appBuilder = CreateRunBuilder();
        appBuilder.AddDocumentDB("DocumentDB").WithDockerfile(AppContext.BaseDirectory);

        using var app = appBuilder.Build();
        await RunBeforeStartPhaseAsync(app);

        var resource = SingleServerResource(app);
        Assert.Empty(await RunContainerRuntimeArgsAsync(resource));
        await PublishBeforeResourceStartedAsync(app, resource);
    }

    // ------------------------------------------------------------------
    // Carve-outs the seal must not narrow
    // ------------------------------------------------------------------

    [Fact]
    public async Task ASealedCustomImageIsStillExemptFromTheFloors()
    {
        var appBuilder = CreateRunBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithPostgresEndpoint()
            .WithImage("forks/my-build", "pg18-0.110.0");

        using var app = appBuilder.Build();
        await RunBeforeStartPhaseAsync(app);

        var resource = SingleServerResource(app);
        Assert.Empty(await RunContainerRuntimeArgsAsync(resource));
        await PublishBeforeResourceStartedAsync(app, resource, useEmptyServices: true);
    }

    [Theory]
    [InlineData("nightly")]
    [InlineData("pg18-0.113.0-rc.1")]
    public async Task ASealedUnrecognisedTagIsStillExemptFromTheFloors(string tag)
    {
        var appBuilder = CreateRunBuilder();
        appBuilder.AddDocumentDB("DocumentDB").WithPostgresEndpoint().WithImageTag(tag);

        using var app = appBuilder.Build();
        await RunBeforeStartPhaseAsync(app);

        var resource = SingleServerResource(app);
        Assert.Empty(await RunContainerRuntimeArgsAsync(resource));
        await PublishBeforeResourceStartedAsync(app, resource, useEmptyServices: true);
    }

    [Fact]
    public async Task ASealedDigestPinIsStillExemptFromTheFloors()
    {
        var appBuilder = CreateRunBuilder();
        appBuilder.AddDocumentDB("DocumentDB")
            .WithPostgresEndpoint()
            .WithImageTag("pg17-0.111.0")
            .WithImageSHA256(OlderReleaseDigest);

        using var app = appBuilder.Build();
        await RunBeforeStartPhaseAsync(app);

        var resource = SingleServerResource(app);
        Assert.Empty(await RunContainerRuntimeArgsAsync(resource));
        await PublishBeforeResourceStartedAsync(app, resource, useEmptyServices: true);
    }

    // ------------------------------------------------------------------
    // Registration
    // ------------------------------------------------------------------

    [Fact]
    public void RepeatedAddDocumentDBCallsRegisterASingleSealHook()
    {
        var appBuilder = CreateRunBuilder();
        appBuilder.AddDocumentDB("first");
        appBuilder.AddDocumentDB("second");
        appBuilder.AddDocumentDB("third");

        using var app = appBuilder.Build();

#pragma warning disable CS0618 // The seal has to run later than every BeforeStartEvent subscriber.
        var hooks = app.Services.GetServices<IDistributedApplicationLifecycleHook>()
            .Where(hook => hook.GetType().Name == "DocumentDBImageSealLifecycleHook")
            .ToArray();
#pragma warning restore CS0618

        Assert.Single(hooks);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Builds an application whose image is chosen before the seal and replaced afterwards, from a
    /// <see cref="ResourceEndpointsAllocatedEvent"/> subscriber - the earliest per-resource event
    /// Aspire raises after the image has already been written into the DCP container spec.
    /// </summary>
    private static async Task<DistributedApplication> BuildLateMutatedApplicationAsync(
        string sealedTag,
        string mutatedTag,
        bool postgresEndpoint)
    {
        var appBuilder = CreateRunBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB").WithImageTag(sealedTag);

        if (postgresEndpoint)
        {
            documentDB.WithPostgresEndpoint();
        }

        var app = appBuilder.Build();

        try
        {
            await RunBeforeStartPhaseAsync(app);
            await PublishResourceEndpointsAllocatedAsync(app, SingleServerResource(app), () =>
                documentDB.WithImageTag(mutatedTag));
        }
        catch
        {
            app.Dispose();
            throw;
        }

        return app;
    }

    /// <summary>
    /// A run-mode builder whose DCP options pass validation.
    /// </summary>
    /// <remarks>
    /// Publishing <see cref="BeforeStartEvent"/> is what makes these tests reproduce the real
    /// ordering, and Aspire's own <c>BeforeStartEvent</c> handler generates DCP resource names,
    /// which reads validated <c>DcpOptions</c>. Nothing here launches DCP — the two required paths
    /// only have to be present.
    /// </remarks>
    private static IDistributedApplicationBuilder CreateRunBuilder() =>
        DistributedApplication.CreateBuilder(
            ["DcpPublisher:CliPath=dcp", "DcpPublisher:DashboardPath=dashboard"]);

    /// <summary>
    /// Runs the before-start phase the way Aspire's <c>ExecuteBeforeStartHooksAsync</c> does:
    /// <see cref="BeforeStartEvent"/> to every subscriber first, then every lifecycle hook.
    /// </summary>
    private static async Task RunBeforeStartPhaseAsync(DistributedApplication app)
    {
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        await app.Services.GetRequiredService<IDistributedApplicationEventing>()
            .PublishAsync(new BeforeStartEvent(app.Services, appModel), CancellationToken.None);

#pragma warning disable CS0618 // The seal has to run later than every BeforeStartEvent subscriber.
        foreach (var hook in app.Services.GetServices<IDistributedApplicationLifecycleHook>())
        {
            await hook.BeforeStartAsync(appModel, CancellationToken.None);
        }
#pragma warning restore CS0618
    }

    /// <summary>
    /// Publishes <see cref="ResourceEndpointsAllocatedEvent"/> with a subscriber that mutates the
    /// resource, which is the position a caller has after DCP has snapshotted the image.
    /// </summary>
    private static Task PublishResourceEndpointsAllocatedAsync(
        DistributedApplication app,
        IResource resource,
        Action mutate)
    {
        var eventing = app.Services.GetRequiredService<IDistributedApplicationEventing>();

        eventing.Subscribe<ResourceEndpointsAllocatedEvent>(resource, (_, _) =>
        {
            mutate();
            return Task.CompletedTask;
        });

        return eventing.PublishAsync(
            new ResourceEndpointsAllocatedEvent(resource, app.Services),
            EventDispatchBehavior.BlockingSequential,
            CancellationToken.None);
    }

    private static Task PublishBeforeResourceStartedAsync(
        DistributedApplication app,
        IResource resource,
        bool useEmptyServices = false)
    {
        var eventing = app.Services.GetRequiredService<IDistributedApplicationEventing>();
        var services = useEmptyServices
            ? new ServiceCollection()
                .AddSingleton(app.Services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>())
                .BuildServiceProvider()
            : app.Services;

        return eventing.PublishAsync(
            new BeforeResourceStartedEvent(resource, services),
            EventDispatchBehavior.BlockingSequential,
            CancellationToken.None);
    }

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

    private static void AssertNamesBothReferences(string message, string sealedTag, string currentTag)
    {
        Assert.Contains(SealMessageFragment, message, StringComparison.Ordinal);
        Assert.Contains("DCP container spec", message, StringComparison.Ordinal);
        Assert.Contains("'DocumentDB'", message, StringComparison.Ordinal);
        Assert.Contains(
            $"Sealed reference: '{DocumentDBContainerImageTags.Registry}/{DocumentDBContainerImageTags.Image}:{sealedTag}'",
            message,
            StringComparison.Ordinal);
        Assert.Contains(
            $"Current reference: '{DocumentDBContainerImageTags.Registry}/{DocumentDBContainerImageTags.Image}:{currentTag}'",
            message,
            StringComparison.Ordinal);
        Assert.Contains("WithImageTag", message, StringComparison.Ordinal);
        Assert.Contains("ResourceEndpointsAllocatedEvent", message, StringComparison.Ordinal);
    }

    private static bool HasSeal(IResource resource) =>
        resource.Annotations.Any(annotation => annotation.GetType().Name == "DocumentDBImageSealAnnotation");

    private static DocumentDBServerResource SingleServerResource(DistributedApplication app) =>
        Assert.Single(ServerResources(app));

    private static DocumentDBServerResource[] ServerResources(DistributedApplication app) =>
        [.. app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DocumentDBServerResource>()];

    // A real digest of a published documentdb-local image, embedded as a literal so nothing here
    // depends on the registry or on a tag upstream can move.
    private const string OlderReleaseDigest =
        "8c8a716e27f398b03c397424c4ddd901bddbc22b9f910b17096b0b246c7c9011";
}
