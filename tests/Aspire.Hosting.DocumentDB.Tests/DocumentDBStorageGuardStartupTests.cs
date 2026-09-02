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
/// End-to-end proof that the data-storage guard is part of the pipeline Aspire really runs: an
/// AppHost that is actually started refuses a configuration the guard rejects, with the guard's
/// own message, and before any container is created.
/// </summary>
/// <remarks>
/// The unit tests drive the same <see cref="ExecutionConfigurationBuilder"/> the container creator
/// uses, which is what makes them meaningful. These exist so that "the container creator uses it,
/// after <see cref="BeforeStartEvent"/> has installed the guard" is asserted rather than assumed.
/// </remarks>
[Trait("Category", "Integration")]
public class DocumentDBStorageGuardStartupTests
{
    [Theory]
    [InlineData(Scenarios.ReadOnlyDataMountScenario, "mounts its data directory ('/data') read-only")]
    [InlineData(Scenarios.AboveRootMountTargetScenario, "reaches above the container root")]
    [InlineData(Scenarios.ReservedDataPathArgumentScenario, "passes the command-line argument '--data-path'")]
    [InlineData(Scenarios.SharedDataDirectoryScenario, "as their data directory")]
    public async Task AnUnusableDataStorageConfigurationFailsTheResourceOnARealStart(string scenario, string expected)
    {
        RequireDocker();

        using var cts = CreateEndToEndTimeoutSource();
        using var scope = SetScenario(scenario);

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Scenarios>(cts.Token);

        // The operator's channel for an orchestration failure is the AppHost's own log output, not
        // the (never-created) container's log stream.
        var hostLog = new LogSink();
        appHost.Services.AddLogging(logging => logging.AddProvider(new LogSinkProvider(hostLog)));

        await using var app = await appHost.BuildAsync(cts.Token);

        // With two resources on one directory, which of them loses the registration race is DCP's
        // choice: either failing is the assertion, so both are watched.
        string[] watched = scenario == Scenarios.SharedDataDirectoryScenario
            ? ["documentdb", "documentdb-peer"]
            : ["documentdb"];

        var diagnostics = await CaptureStartupFailureAsync(app, watched, cts.Token)
            + Environment.NewLine
            + hostLog.ToString();

        Assert.Contains(expected, diagnostics, StringComparison.Ordinal);
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
    /// <paramref name="resourceNames"/>: a start-up throw, a failed resource state, or both. The
    /// watch ends as soon as any of the named resources fails.
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
