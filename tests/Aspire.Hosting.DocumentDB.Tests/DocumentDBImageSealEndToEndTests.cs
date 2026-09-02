// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Xunit;
using static Aspire.Hosting.DocumentDB.Tests.DocumentDBEndToEndSupport;
using AppHost = Aspire.Hosting.DocumentDB.ImageSealEndToEndApp.Program;

namespace Aspire.Hosting.DocumentDB.Tests;

/// <summary>
/// Container-backed coverage for the image seal, asserted against the image the container runtime
/// was actually given rather than against the application model.
/// </summary>
/// <remarks>
/// The resource snapshot cannot settle this question. <c>ApplicationOrchestrator</c> computes the
/// snapshot's <c>container.image</c> from the live model with <c>TryGetContainerImageName</c>, so
/// it agrees with the model in exactly the case that is broken. Every positive assertion here
/// therefore goes through <c>docker inspect</c>, and the negative control proves that route can
/// observe a real disagreement.
/// </remarks>
[Trait("Category", "Integration")]
public class DocumentDBImageSealEndToEndTests
{
    // ------------------------------------------------------------------
    // The negative control: what Aspire does without a seal
    // ------------------------------------------------------------------

    [Fact]
    public async Task APlainContainerMutatedAfterEndpointAllocationStillLaunchesThePreMutationImage()
    {
        // No DocumentDB resource is involved, so nothing in this package is bypassed or weakened.
        // A plain Aspire container replaces its own image from a ResourceEndpointsAllocatedEvent
        // subscriber - the earliest per-resource event there is - and the runtime still gets the
        // image from before the change. That is the ordering fact the seal rests on, and it also
        // shows this harness can see a model/runtime disagreement rather than only reporting
        // agreement.
        RequireDocker();

        using var cts = CreateEndToEndTimeoutSource();
        using var scenario = new EnvironmentScope((AppHost.ScenarioEnvironmentVariable, AppHost.NegativeControlScenario));

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<AppHost>(cts.Token);
        await using var app = await appHost.BuildAsync(cts.Token);

        await app.StartAsync(cts.Token);

        var containerId = await GetContainerIdAsync(app, AppHost.ProbeResourceName, cts.Token);
        var launchedImage = await GetContainerImageAsync(containerId);

        Assert.Contains(AppHost.ProbeSealedTag, launchedImage, StringComparison.Ordinal);
        Assert.DoesNotContain(AppHost.ProbeMutatedTag, launchedImage, StringComparison.Ordinal);

        // And the model does say the other thing, so the disagreement is real rather than the
        // subscriber having failed to run.
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var probe = Assert.Single(Snapshot<IResource>(appModel.Resources)
            .Where(resource => resource.Name == AppHost.ProbeResourceName));

        Assert.True(probe.TryGetContainerImageName(out var modelImage));
        Assert.Contains(AppHost.ProbeMutatedTag, modelImage!, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // The seal is the image the runtime is given
    // ------------------------------------------------------------------

    [Fact]
    public async Task AnUnmutatedResourceLaunchesTheImageItWasBuiltWith()
    {
        RequireDocker();

        using var cts = CreateEndToEndTimeoutSource();
        using var scenario = new EnvironmentScope((AppHost.ScenarioEnvironmentVariable, AppHost.UnmutatedScenario));

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<AppHost>(cts.Token);
        await using var app = await appHost.BuildAsync(cts.Token);

        await app.StartAsync(cts.Token);

        var containerId = await GetContainerIdAsync(app, AppHost.ResourceName, cts.Token);
        Assert.Contains(AppHost.SealedTag, await GetContainerImageAsync(containerId), StringComparison.Ordinal);

        // The seal must not have made a legal resource any harder to start.
        await WaitForHealthCheckAsync(
            app.Services.GetRequiredService<HealthCheckService>(),
            $"{AppHost.ResourceName}_check",
            cts.Token);

        var connectionString = await app.GetConnectionStringAsync("appdb", cts.Token);
        await AssertRoundTripAsync(connectionString!, "appdb", "sealed", "sealed-document", cts.Token);
    }

    [Fact]
    public async Task AnImageChosenByABeforeStartSubscriberIsTheImageTheRuntimeLaunches()
    {
        // A BeforeStartEvent subscriber registered after AddDocumentDB is ordinary pre-start
        // configuration and is the last thing that can legitimately choose the image. The seal is
        // taken after it, so its choice is what runs - and the tag it superseded must not.
        RequireDocker();

        using var cts = CreateEndToEndTimeoutSource();
        using var scenario = new EnvironmentScope((AppHost.ScenarioEnvironmentVariable, AppHost.PreStartMutationScenario));

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<AppHost>(cts.Token);
        await using var app = await appHost.BuildAsync(cts.Token);

        await app.StartAsync(cts.Token);

        var containerId = await GetContainerIdAsync(app, AppHost.ResourceName, cts.Token);
        var launchedImage = await GetContainerImageAsync(containerId);

        Assert.Contains(AppHost.SealedTag, launchedImage, StringComparison.Ordinal);
        Assert.DoesNotContain(AppHost.SupersededTag, launchedImage, StringComparison.Ordinal);

        await WaitForHealthCheckAsync(
            app.Services.GetRequiredService<HealthCheckService>(),
            $"{AppHost.ResourceName}_check",
            cts.Token);
    }

    // ------------------------------------------------------------------
    // Both directions of a change made after the seal
    // ------------------------------------------------------------------

    [Fact]
    public async Task UpgradingTheImageAfterEndpointAllocationIsRefusedByARealRun()
    {
        // The false accept this fixes: the floors read the upgraded annotation and clear the
        // resource, while the prepared container still runs the tag that hard-codes the PostgreSQL
        // credentials - the exact configuration the credential floor exists to refuse.
        var diagnostics = await RunExpectingFailureAsync(AppHost.LateUpgradeScenario);

        AssertSealFailure(diagnostics, AppHost.BelowFloorTag, AppHost.SealedTag);
    }

    [Fact]
    public async Task DowngradingTheImageAfterEndpointAllocationIsRefusedByARealRun()
    {
        // The reverse direction: without the seal the model reports a newer release than the
        // container runtime was ever given.
        var diagnostics = await RunExpectingFailureAsync(AppHost.LateDowngradeScenario);

        AssertSealFailure(diagnostics, AppHost.SealedTag, AppHost.DowngradeTag);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static async Task<string> RunExpectingFailureAsync(string scenarioName)
    {
        RequireDocker();

        using var cts = CreateEndToEndTimeoutSource();
        using var scenario = new EnvironmentScope((AppHost.ScenarioEnvironmentVariable, scenarioName));

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<AppHost>(cts.Token);

        // An orchestration failure reaches the operator through the AppHost's own log output, not
        // through the (never-started) container's log stream.
        var hostLog = new LogSink();
        appHost.Services.AddLogging(logging => logging.AddProvider(new LogSinkProvider(hostLog)));

        await using var app = await appHost.BuildAsync(cts.Token);

        return await CaptureStartupFailureAsync(app, AppHost.ResourceName, cts.Token)
            + Environment.NewLine
            + hostLog.ToString();
    }

    private static void AssertSealFailure(string diagnostics, string sealedTag, string currentTag)
    {
        Assert.Contains("changed the container image it will run after", diagnostics, StringComparison.Ordinal);
        Assert.Contains("DCP container spec", diagnostics, StringComparison.Ordinal);
        Assert.Contains($"Sealed reference: '{Reference(sealedTag)}'", diagnostics, StringComparison.Ordinal);
        Assert.Contains($"Current reference: '{Reference(currentTag)}'", diagnostics, StringComparison.Ordinal);
        Assert.Contains("ResourceEndpointsAllocatedEvent", diagnostics, StringComparison.Ordinal);

        static string Reference(string tag) =>
            $"{DocumentDBContainerImageTags.Registry}/{DocumentDBContainerImageTags.Image}:{tag}";
    }

    /// <summary>
    /// Starts the application expecting the resource not to come up, and returns everything the
    /// operator would see: the StartAsync exception (if any), the resource's terminal state text
    /// and its logs.
    /// </summary>
    private static async Task<string> CaptureStartupFailureAsync(
        DistributedApplication app,
        string resourceName,
        CancellationToken cancellationToken)
    {
        var diagnostics = new StringBuilder();

        try
        {
            await app.StartAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            diagnostics.AppendLine(ex.ToString());
        }

        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();

        using var stateTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stateTimeout.CancelAfter(TimeSpan.FromSeconds(60));

        try
        {
            await foreach (var resourceEvent in notifications.WatchAsync(stateTimeout.Token))
            {
                if (!string.Equals(resourceEvent.Resource.Name, resourceName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (resourceEvent.Snapshot.State?.Text is not { } stateText)
                {
                    continue;
                }

                diagnostics.AppendLine(stateText);

                if (stateText.Contains("Fail", StringComparison.OrdinalIgnoreCase) ||
                    stateText.Contains("Error", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (stateTimeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            diagnostics.AppendLine("[resource never reported a failed state within 60s]");
        }

        var loggerService = app.Services.GetRequiredService<ResourceLoggerService>();

        using var logTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        logTimeout.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            await foreach (var logBatch in loggerService.WatchAsync(resourceName).WithCancellation(logTimeout.Token))
            {
                foreach (var line in logBatch)
                {
                    diagnostics.AppendLine(line.Content);
                }
            }
        }
        catch (OperationCanceledException) when (logTimeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The log stream stays open for the app's lifetime; the timeout is the exit condition.
        }

        return diagnostics.ToString();
    }
}
