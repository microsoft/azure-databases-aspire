// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using static Aspire.Hosting.DocumentDB.Tests.DocumentDBEndToEndSupport;
using Scenarios = Aspire.Hosting.DocumentDB.StorageGuardEndToEndApp.Program;

namespace Aspire.Hosting.DocumentDB.Tests;

/// <summary>
/// What the container runtime is really given, on a real start: the image it launches and the
/// arguments it is invoked with. The unit tests drive the same callbacks the container creator
/// drives, which is what makes them meaningful; these exist so that "the container runtime agrees
/// with the model" is asserted against Docker rather than assumed.
/// </summary>
[Trait("Category", "Integration")]
public class DocumentDBRuntimeAuthorityEndToEndTests
{
    /// <summary>
    /// The negative control, and the fact everything else rests on. Aspire snapshots a container's
    /// image into the orchestrator's container spec while it prepares resources; a plain container
    /// that rewrites its own image annotation afterwards therefore still launches the image it was
    /// prepared with, and the model and the container runtime end up disagreeing with nothing
    /// reported. No DocumentDB code is involved in the probe, so this is a statement about Aspire.
    /// </summary>
    [Fact]
    public async Task AContainerMutatedAfterPreparationStillLaunchesThePreparedImage()
    {
        RequireDocker();

        using var cts = CreateEndToEndTimeoutSource();
        using var scope = SetScenario(Scenarios.LaunchedImageScenario);

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Scenarios>(cts.Token);
        await using var app = await appHost.BuildAsync(cts.Token);
        await app.StartAsync(cts.Token);

        var probeImage = await GetLaunchedImageAsync(app, "imageprobe", cts.Token);

        // What the container runtime launched: the image the probe was prepared with.
        Assert.Contains(Scenarios.ProbePreparedTag, probeImage, StringComparison.Ordinal);
        Assert.DoesNotContain(Scenarios.ProbeMutatedTag, probeImage, StringComparison.Ordinal);

        // What the model says, which is the disagreement the seal exists to refuse.
        var probe = app.Services.GetRequiredService<DistributedApplicationModel>()
            .Resources.Single(resource => resource.Name == "imageprobe");

        Assert.True(probe.TryGetContainerImageName(out var modelImage));
        Assert.Contains(Scenarios.ProbeMutatedTag, modelImage, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same start, judged the other way round: a DocumentDB resource nobody changes launches
    /// exactly the image it was configured with, so the seal is not a check that only ever fires.
    /// </summary>
    [Fact]
    public async Task ASealedResourceLaunchesTheImageItWasConfiguredWith()
    {
        RequireDocker();

        using var cts = CreateEndToEndTimeoutSource();
        using var scope = SetScenario(Scenarios.LaunchedImageScenario);

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Scenarios>(cts.Token);
        await using var app = await appHost.BuildAsync(cts.Token);
        await app.StartAsync(cts.Token);

        var image = await GetLaunchedImageAsync(app, "documentdb", cts.Token);

        Assert.EndsWith(":" + Scenarios.LaunchedImageTag, image, StringComparison.Ordinal);
    }

    /// <summary>
    /// Both directions of the change the container runtime cannot follow. Raising the tag is what
    /// makes a version floor pass on an image that is not starting; lowering it is what makes this
    /// package promise a data-directory interlock the running release does not have.
    /// </summary>
    [Theory]
    [InlineData(Scenarios.LateImageUpgradeScenario)]
    [InlineData(Scenarios.LateImageDowngradeScenario)]
    public async Task AnImageChangedAfterPreparationFailsARealStart(string scenario)
    {
        RequireDocker();

        using var cts = CreateEndToEndTimeoutSource();
        using var scope = SetScenario(scenario);

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Scenarios>(cts.Token);

        var hostLog = new LogSink();
        appHost.Services.AddLogging(logging => logging.AddProvider(new LogSinkProvider(hostLog)));

        await using var app = await appHost.BuildAsync(cts.Token);

        var diagnostics = await CaptureStartupFailureAsync(app, ["documentdb"], cts.Token)
            + Environment.NewLine
            + hostLog.ToString();

        Assert.Contains("after the image it will run had already been settled", diagnostics, StringComparison.Ordinal);
    }

    /// <summary>
    /// Storage, a guarded environment variable and the entry point, each added straight to the
    /// container runtime where no annotation records them, on a real start.
    /// </summary>
    [Theory]
    [InlineData(Scenarios.RawMountRuntimeArgumentScenario, "'--mount', which changes what the container mounts")]
    [InlineData(Scenarios.RawReadOnlyVolumeRuntimeArgumentScenario, "'--volume', which changes what the container mounts")]
    [InlineData(Scenarios.RawTmpfsRuntimeArgumentScenario, "'--tmpfs', which changes what the container mounts")]
    [InlineData(Scenarios.RawReadOnlyRootRuntimeArgumentScenario, "'--read-only', which changes what the container mounts")]
    [InlineData(Scenarios.RawDataPathRuntimeArgumentScenario, "'--env DATA_PATH', which sets an environment variable this package has already decided")]
    [InlineData(Scenarios.RawEntrypointRuntimeArgumentScenario, "'--entrypoint', which replaces the image's entry point")]
    [InlineData(Scenarios.DeferredRuntimeArgumentScenario, "a value that is only known later, in a position where the runtime reads an option name")]
    public async Task ARawRuntimeArgumentFailsARealStart(string scenario, string expected)
    {
        RequireDocker();

        using var cts = CreateEndToEndTimeoutSource();
        using var scope = SetScenario(scenario);

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Scenarios>(cts.Token);

        var hostLog = new LogSink();
        appHost.Services.AddLogging(logging => logging.AddProvider(new LogSinkProvider(hostLog)));

        await using var app = await appHost.BuildAsync(cts.Token);

        var diagnostics = await CaptureStartupFailureAsync(app, ["documentdb"], cts.Token)
            + Environment.NewLine
            + hostLog.ToString();

        Assert.Contains(expected, diagnostics, StringComparison.Ordinal);

        // The container was never created, so nothing was started on storage that was never judged.
        var (_, containers) = await RunDockerAsync(
            "ps", "-a", "--filter", "volume=documentdb-raw-mount", "--format", "{{.ID}}");

        Assert.Equal(string.Empty, containers.Trim());
    }

    /// <summary>
    /// The positive control for the same reading: ordinary container-runtime arguments reach the
    /// container runtime untouched, which is what stops the guard from being a blanket refusal.
    /// The second label is <c>-v</c>, which a search for dangerous spellings would have refused and
    /// which the runtime reads as the label it is.
    /// </summary>
    [Fact]
    public async Task AHarmlessRuntimeArgumentReachesTheContainerRuntime()
    {
        RequireDocker();

        using var cts = CreateEndToEndTimeoutSource();
        using var scope = SetScenario(Scenarios.HarmlessRuntimeArgumentScenario);

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Scenarios>(cts.Token);
        await using var app = await appHost.BuildAsync(cts.Token);
        await app.StartAsync(cts.Token);

        var containerId = await GetContainerIdAsync(app, "documentdb", cts.Token);

        var (exitCode, labels) = await RunDockerAsync(
            "inspect", containerId, "--format", "{{range $key, $value := .Config.Labels}}{{$key}}={{$value}}{{println}}{{end}}");

        Assert.Equal(0, exitCode);

        var (name, value) = SplitLabel(Scenarios.HarmlessRuntimeArgumentLabel);

        Assert.Contains($"{name}={value}", labels, StringComparison.Ordinal);
        Assert.Contains("-v=", labels, StringComparison.Ordinal);
    }

    private static (string Name, string Value) SplitLabel(string label)
    {
        var separator = label.IndexOf('=', StringComparison.Ordinal);
        return (label[..separator], label[(separator + 1)..]);
    }

    /// <summary>
    /// The image the container runtime is really running, read off the created container rather
    /// than off the model. The resource snapshot's <c>container.image</c> property is not usable
    /// for this: Aspire computes it from the live model when the resource starts, so it agrees with
    /// the model even when the container does not.
    /// </summary>
    private static async Task<string> GetLaunchedImageAsync(
        DistributedApplication app,
        string resourceName,
        CancellationToken cancellationToken)
    {
        var containerId = await GetContainerIdAsync(app, resourceName, cancellationToken);

        var (exitCode, image) = await RunDockerAsync(
            "inspect", containerId, "--format", "{{.Config.Image}}");

        Assert.Equal(0, exitCode);

        return image.Trim();
    }

    private static IDisposable SetScenario(string scenario)
    {
        var previous = Environment.GetEnvironmentVariable(Scenarios.ScenarioEnvironmentVariable);
        Environment.SetEnvironmentVariable(Scenarios.ScenarioEnvironmentVariable, scenario);
        return new ScenarioScope(previous);
    }

    private sealed class ScenarioScope(string? previous) : IDisposable
    {
        public void Dispose() =>
            Environment.SetEnvironmentVariable(Scenarios.ScenarioEnvironmentVariable, previous);
    }

    /// <summary>
    /// Starts the application and collects whatever the AppHost reported about
    /// <paramref name="resourceNames"/>: a start-up throw, a failed resource state, or both.
    /// </summary>
    private static async Task<string> CaptureStartupFailureAsync(
        DistributedApplication app,
        string[] resourceNames,
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
        stateTimeout.CancelAfter(TimeSpan.FromSeconds(120));

        try
        {
            await foreach (var resourceEvent in notifications.WatchAsync(stateTimeout.Token))
            {
                if (!resourceNames.Contains(resourceEvent.Resource.Name, StringComparer.Ordinal))
                {
                    continue;
                }

                if (resourceEvent.Snapshot.State?.Text is { } stateText)
                {
                    diagnostics.AppendLine(stateText);

                    if (stateText.Contains("Fail", StringComparison.OrdinalIgnoreCase) ||
                        stateText.Contains("Error", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Whatever was collected is what the assertion gets to look at.
        }

        return diagnostics.ToString();
    }

    private sealed class LogSink
    {
        private readonly StringBuilder _builder = new();

        public void Append(string message)
        {
            lock (_builder)
            {
                _builder.AppendLine(message);
            }
        }

        public override string ToString()
        {
            lock (_builder)
            {
                return _builder.ToString();
            }
        }
    }

    private sealed class LogSinkProvider(LogSink sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new SinkLogger(sink);

        public void Dispose()
        {
        }

        private sealed class SinkLogger(LogSink sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                sink.Append(formatter(state, exception));

                if (exception is not null)
                {
                    sink.Append(exception.ToString());
                }
            }
        }
    }
}
