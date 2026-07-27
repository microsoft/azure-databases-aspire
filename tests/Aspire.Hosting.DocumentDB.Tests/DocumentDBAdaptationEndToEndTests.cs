// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Aspire.TestUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;
using Xunit.Sdk;

namespace Aspire.Hosting.DocumentDB.Tests;

/// <summary>
/// End-to-end coverage for the DocumentDB 0.114 adaptation claims that previously had only
/// in-process coverage.
/// </summary>
/// <remarks>
/// <para>
/// The Pg18 availability floor is unit-tested by publishing <c>BeforeResourceStartedEvent</c>
/// by hand, which proves the handler's logic but not that a real run reaches it. The TLS
/// corrections (<c>UseTls(false)</c> works against the default image from 0.114.0;
/// <c>TLS_MODE=requireTLS</c> rejects plain connections) are claims about container behaviour
/// and had no executable coverage at all — they were verified once, by hand, and written into
/// docs and XML comments where they can silently rot on the next image bump.
/// </para>
/// <para>
/// Every test here drives the real orchestrator against real containers.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public class DocumentDBAdaptationEndToEndTests
{
    private const string EndToEndTimeoutEnvironmentVariable = "DOCUMENTDB_E2E_TIMEOUT_SECONDS";
    private static readonly TimeSpan DefaultEndToEndTimeout = TimeSpan.FromMinutes(5);

    private const string ScenarioEnvironmentVariable =
        Aspire.Hosting.DocumentDB.AdaptationEndToEndApp.Program.ScenarioEnvironmentVariable;

    // ------------------------------------------------------------------
    // Pg18 availability floor
    // ------------------------------------------------------------------

    [Fact]
    public async Task Pg18PairedWithV0_114_0StartsAndServesTraffic()
    {
        // The floor is only correct if the first version it admits actually exists upstream and
        // works. A floor set one version too high would be invisible to every negative test.
        RequireDocker();

        using var cts = CreateEndToEndTimeoutSource();
        using var scenario = SetScenario(Aspire.Hosting.DocumentDB.AdaptationEndToEndApp.Program.Pg18SupportedScenario);

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Aspire.Hosting.DocumentDB.AdaptationEndToEndApp.Program>(cts.Token);
        await using var app = await appHost.BuildAsync(cts.Token);

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var serverResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());
        var image = Assert.Single(serverResource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal("pg18-0.114.0", image.Tag);

        await app.StartAsync(cts.Token);

        var healthCheckService = app.Services.GetRequiredService<HealthCheckService>();
        await WaitForHealthCheckAsync(healthCheckService, "documentdb_check", cts.Token);
        await WaitForHealthCheckAsync(healthCheckService, "appdb_check", cts.Token);

        var connectionString = await app.GetConnectionStringAsync("appdb", cts.Token);
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        await AssertRoundTripAsync(connectionString!, "pg18-widget", cts.Token);
    }

    [Fact]
    public async Task Pg18BelowTheFloorFailsAtStartupInsteadOfPullTime()
    {
        // The promise in the CHANGELOG is specifically that this stops being an opaque
        // manifest-not-found at pull time. So: the guard's message must surface through a real
        // run, and the non-existent image must never be requested.
        RequireDocker();

        const string absentTag = "pg18-0.113.0";
        Assert.False(
            await DockerImageExistsLocallyAsync($"ghcr.io/documentdb/documentdb/documentdb-local:{absentTag}"),
            $"Precondition: '{absentTag}' must not be present locally for this test to prove anything.");

        using var cts = CreateEndToEndTimeoutSource();
        using var scenario = SetScenario(Aspire.Hosting.DocumentDB.AdaptationEndToEndApp.Program.Pg18BelowFloorScenario);

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Aspire.Hosting.DocumentDB.AdaptationEndToEndApp.Program>(cts.Token);

        // The operator's actual channel for an orchestration failure is the AppHost's own log
        // output, not the (never-created) container's log stream.
        var hostLog = new LogSink();
        appHost.Services.AddLogging(logging => logging.AddProvider(new LogSinkProvider(hostLog)));

        await using var app = await appHost.BuildAsync(cts.Token);

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var serverResource = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());
        var image = Assert.Single(serverResource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal(absentTag, image.Tag);

        // The failure may surface as a StartAsync exception or as a failed resource state,
        // depending on how the orchestrator handles a BeforeResourceStartedEvent throw. Both are
        // acceptable; what matters is that the guard's actionable text reaches the operator.
        var diagnostics = await CaptureStartupFailureAsync(app, serverResource.Name, cts.Token)
            + Environment.NewLine
            + hostLog.ToString();

        Assert.Contains("pg18", diagnostics, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0.114.0", diagnostics, StringComparison.Ordinal);
        Assert.Contains("WithPostgresVersion", diagnostics, StringComparison.Ordinal);

        // The opaque failure this replaced.
        Assert.DoesNotContain("manifest unknown", diagnostics, StringComparison.OrdinalIgnoreCase);

        Assert.False(
            await DockerImageExistsLocallyAsync($"ghcr.io/documentdb/documentdb/documentdb-local:{absentTag}"),
            "The guard must fail before the runtime attempts to pull the non-existent image.");
    }

    // ------------------------------------------------------------------
    // TLS enforcement (corrected in 0.114.0)
    // ------------------------------------------------------------------

    [Fact]
    public async Task PlainConnectionsSucceedAgainstTheDefaultImage()
    {
        // CHANGELOG "Fixed": from 0.114.0 the container's default TLS_MODE=allowTLS accepts plain
        // connections, so UseTls(false) works against the default image. Images <= 0.113.0
        // rejected plain connections regardless, which is what the old XML docs described.
        RequireDocker();

        using var cts = CreateEndToEndTimeoutSource();
        using var scenario = SetScenario(Aspire.Hosting.DocumentDB.AdaptationEndToEndApp.Program.PlaintextScenario);

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Aspire.Hosting.DocumentDB.AdaptationEndToEndApp.Program>(cts.Token);
        await using var app = await appHost.BuildAsync(cts.Token);

        await app.StartAsync(cts.Token);

        var connectionString = await app.GetConnectionStringAsync("appdb", cts.Token);
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        Assert.DoesNotContain("tls=true", connectionString!, StringComparison.Ordinal);

        // Health checks use the same plain connection string, so their going healthy is itself
        // part of the claim.
        var healthCheckService = app.Services.GetRequiredService<HealthCheckService>();
        await WaitForHealthCheckAsync(healthCheckService, "documentdb_check", cts.Token);
        await WaitForHealthCheckAsync(healthCheckService, "appdb_check", cts.Token);

        await AssertRoundTripAsync(connectionString!, "plaintext-widget", cts.Token);
    }

    [Fact]
    public async Task RequireTlsRejectsPlainConnectionsButAcceptsTlsOnTheSamePort()
    {
        // The documented escape hatch and the documented self-contradiction, in one run: the
        // container is demonstrably alive (a TLS client on the same endpoint works), so the plain
        // client's failure is TLS enforcement rather than a container that never came up.
        RequireDocker();

        using var cts = CreateEndToEndTimeoutSource();
        using var scenario = SetScenario(Aspire.Hosting.DocumentDB.AdaptationEndToEndApp.Program.RequireTlsWithPlaintextClientScenario);

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Aspire.Hosting.DocumentDB.AdaptationEndToEndApp.Program>(cts.Token);
        await using var app = await appHost.BuildAsync(cts.Token);

        await app.StartAsync(cts.Token);

        var plainConnectionString = await app.GetConnectionStringAsync("appdb", cts.Token);
        Assert.False(string.IsNullOrWhiteSpace(plainConnectionString));
        Assert.DoesNotContain("tls=true", plainConnectionString!, StringComparison.Ordinal);

        // Control: the same endpoint, spoken to over TLS, must work. This also waits out
        // container startup so the negative assertion below is not just a race.
        var tlsConnectionString = WithTlsOptions(plainConnectionString!);
        var database = await ConnectAsync(tlsConnectionString, "appdb", cts.Token);
        await database.RunCommandAsync((Command<BsonDocument>)"{ ping: 1 }", cancellationToken: cts.Token);

        // The claim under test: plain is refused.
        //
        // Deliberately NOT the end-to-end token. Cancelling it would make PingOnceAsync throw
        // OperationCanceledException, which any "some exception was thrown" assertion would
        // happily accept - so a timed-out run would report success for the one assertion this
        // test exists to make. PingOnceAsync carries its own 10s driver timeouts.
        var rejection = await Record.ExceptionAsync(
            () => PingOnceAsync(plainConnectionString!, "appdb", CancellationToken.None));

        Assert.NotNull(rejection);
        Assert.True(
            rejection is TimeoutException or MongoException,
            $"Expected the plain connection to be refused by TLS enforcement, but got: {rejection}");
    }

    [Fact]
    public async Task RequireTlsWithTlsEnabledWorksEndToEnd()
    {
        // The sanctioned combination from the docs: opt into rejecting plain connections while
        // keeping the connection string (and therefore the health checks) on TLS.
        RequireDocker();

        using var cts = CreateEndToEndTimeoutSource();
        using var scenario = SetScenario(Aspire.Hosting.DocumentDB.AdaptationEndToEndApp.Program.RequireTlsScenario);

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Aspire.Hosting.DocumentDB.AdaptationEndToEndApp.Program>(cts.Token);
        await using var app = await appHost.BuildAsync(cts.Token);

        await app.StartAsync(cts.Token);

        var healthCheckService = app.Services.GetRequiredService<HealthCheckService>();
        await WaitForHealthCheckAsync(healthCheckService, "documentdb_check", cts.Token);
        await WaitForHealthCheckAsync(healthCheckService, "appdb_check", cts.Token);

        var connectionString = await app.GetConnectionStringAsync("appdb", cts.Token);
        Assert.Contains("tls=true", connectionString!, StringComparison.Ordinal);

        await AssertRoundTripAsync(connectionString!, "require-tls-widget", cts.Token);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static void RequireDocker()
    {
        if (!RequiresDockerAttribute.IsSupported)
        {
            throw SkipException.ForSkip("Docker is required for DocumentDB end-to-end validation.");
        }
    }

    /// <summary>
    /// Sets the AppHost scenario for the duration of the test and restores the previous value.
    /// </summary>
    private static ScenarioScope SetScenario(string scenario) => new(scenario);

    private sealed class ScenarioScope : IDisposable
    {
        private readonly string? _previous;

        public ScenarioScope(string scenario)
        {
            _previous = Environment.GetEnvironmentVariable(ScenarioEnvironmentVariable);
            Environment.SetEnvironmentVariable(ScenarioEnvironmentVariable, scenario);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(ScenarioEnvironmentVariable, _previous);
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

                var snapshot = resourceEvent.Snapshot;
                if (snapshot.State?.Text is { } stateText)
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
            // Log stream stays open for the app's lifetime; the timeout is the exit condition.
        }

        return diagnostics.ToString();
    }

    /// <summary>Collects every log message written through the AppHost's logger factory.</summary>
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
        public ILogger CreateLogger(string categoryName) => new SinkLogger(sink, categoryName);

        public void Dispose()
        {
        }

        private sealed class SinkLogger(LogSink sink, string categoryName) : ILogger
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
                sink.Append($"[{logLevel}] {categoryName}: {formatter(state, exception)} {exception}");
            }
        }
    }

    private static async Task<bool> DockerImageExistsLocallyAsync(string image)
    {
        // Both streams are redirected to keep the test output clean, so both must be drained
        // concurrently with the wait: 'docker image inspect' emits the full manifest JSON on
        // success (~4.3 KB, larger than the pipe buffer), so waiting first would deadlock as soon
        // as the image being probed actually exists locally. The timeout covers a wedged daemon,
        // which would otherwise stall the whole run.
        var startInfo = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("image");
        startInfo.ArgumentList.Add("inspect");
        startInfo.ArgumentList.Add(image);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start 'docker image inspect'.");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var drainStdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var drainStderr = process.StandardError.ReadToEndAsync(timeout.Token);

        try
        {
            await Task.WhenAll(drainStdout, drainStderr, process.WaitForExitAsync(timeout.Token));
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited between the timeout firing and the kill.
            }

            throw new InvalidOperationException(
                $"'docker image inspect {image}' did not complete within 30s; the Docker daemon appears unresponsive.");
        }

        return process.ExitCode == 0;
    }

    private static async Task AssertRoundTripAsync(string connectionString, string documentName, CancellationToken cancellationToken)
    {
        var database = await ConnectAsync(connectionString, "appdb", cancellationToken);
        var collection = database.GetCollection<BsonDocument>("widgets");

        var id = ObjectId.GenerateNewId();
        var filter = Builders<BsonDocument>.Filter.Eq("_id", id);

        await collection.InsertOneAsync(
            new BsonDocument { ["_id"] = id, ["name"] = documentName },
            cancellationToken: cancellationToken);
        Assert.Equal(1, await collection.CountDocumentsAsync(filter, cancellationToken: cancellationToken));

        var deleteResult = await collection.DeleteOneAsync(filter, cancellationToken: cancellationToken);
        Assert.Equal(1, deleteResult.DeletedCount);
        Assert.Equal(0, await collection.CountDocumentsAsync(filter, cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Appends the TLS options to a connection string produced with <c>UseTls(false)</c>, picking
    /// the query separator rather than assuming one is already present. The resource only emits a
    /// query string when it has a password parameter; hard-coding <c>&amp;</c> would silently
    /// produce a malformed URI if that ever stopped being true, and a malformed URI would make the
    /// TLS control in this test fail for a reason unrelated to TLS.
    /// </summary>
    private static string WithTlsOptions(string connectionString) =>
        connectionString + (connectionString.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "tls=true&tlsInsecure=true";

    private static IMongoDatabase GetDatabase(string connectionString, string databaseName, TimeSpan timeout)
    {
        var settings = MongoClientSettings.FromConnectionString(connectionString);
        settings.ServerSelectionTimeout = timeout;
        settings.ConnectTimeout = timeout;
        settings.SocketTimeout = timeout;

        return new MongoClient(settings).GetDatabase(databaseName);
    }

    private static async Task PingOnceAsync(string connectionString, string databaseName, CancellationToken cancellationToken)
    {
        var database = GetDatabase(connectionString, databaseName, TimeSpan.FromSeconds(10));
        await database.RunCommandAsync((Command<BsonDocument>)"{ ping: 1 }", cancellationToken: cancellationToken);
    }

    private static async Task<IMongoDatabase> ConnectAsync(string connectionString, string databaseName, CancellationToken cancellationToken)
    {
        var database = GetDatabase(connectionString, databaseName, TimeSpan.FromSeconds(5));

        Exception? lastException = null;

        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                await database.RunCommandAsync((Command<BsonDocument>)"{ ping: 1 }", cancellationToken: cancellationToken);
                return database;
            }
            catch (TimeoutException ex)
            {
                lastException = ex;
            }
            catch (MongoException ex)
            {
                lastException = ex;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new InvalidOperationException("DocumentDB did not become reachable in time.", lastException);
    }

    private static async Task WaitForHealthCheckAsync(HealthCheckService healthCheckService, string healthCheckKey, CancellationToken cancellationToken)
    {
        HealthReport? lastReport = null;

        for (var attempt = 0; attempt < 60; attempt++)
        {
            lastReport = await healthCheckService.CheckHealthAsync(
                registration => registration.Name == healthCheckKey,
                cancellationToken);

            if (lastReport.Entries.TryGetValue(healthCheckKey, out var entry) && entry.Status == HealthStatus.Healthy)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        var lastMessage = "The health check registration was not found.";
        if (lastReport is not null && lastReport.Entries.TryGetValue(healthCheckKey, out var lastEntry))
        {
            lastMessage = $"{lastEntry.Status}: {lastEntry.Description}";
        }

        throw new InvalidOperationException($"Health check '{healthCheckKey}' did not become healthy in time. Last result: {lastMessage}");
    }

    private static CancellationTokenSource CreateEndToEndTimeoutSource() => new(GetEndToEndTimeout());

    private static TimeSpan GetEndToEndTimeout()
    {
        var configuredTimeout = Environment.GetEnvironmentVariable(EndToEndTimeoutEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredTimeout))
        {
            return DefaultEndToEndTimeout;
        }

        if (!int.TryParse(configuredTimeout, out var timeoutSeconds) || timeoutSeconds <= 0)
        {
            throw new InvalidOperationException(
                $"{EndToEndTimeoutEnvironmentVariable} must be a positive integer number of seconds, but was '{configuredTimeout}'.");
        }

        return TimeSpan.FromSeconds(timeoutSeconds);
    }
}
