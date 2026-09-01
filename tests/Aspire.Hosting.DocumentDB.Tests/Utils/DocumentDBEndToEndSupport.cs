// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
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
/// Shared plumbing for the container-backed end-to-end suites: Mongo connection retries, health
/// check polling, AppHost log capture, environment scoping and the small amount of Docker
/// introspection the feature matrix needs.
/// </summary>
internal static class DocumentDBEndToEndSupport
{
    private const string EndToEndTimeoutEnvironmentVariable = "DOCUMENTDB_E2E_TIMEOUT_SECONDS";
    private static readonly TimeSpan DefaultEndToEndTimeout = TimeSpan.FromMinutes(5);
    private static readonly Regex AnsiEscapeSequenceRegex = new(
        "\u001B\\[[0-?]*[ -/]*[@-~]",
        RegexOptions.CultureInvariant);

    public static void RequireDocker()
    {
        if (!RequiresDockerAttribute.IsSupported)
        {
            throw SkipException.ForSkip("Docker is required for DocumentDB end-to-end validation.");
        }
    }

    public static CancellationTokenSource CreateEndToEndTimeoutSource() => new(GetEndToEndTimeout());

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

    // ------------------------------------------------------------------
    // Environment scoping
    // ------------------------------------------------------------------

    /// <summary>
    /// Sets environment variables for the duration of a test and restores their previous values.
    /// The AppHost under test runs in this process, so this is how scenario inputs reach it.
    /// </summary>
    public sealed class EnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> _previous = [];

        public EnvironmentScope(params (string Name, string Value)[] variables)
        {
            foreach (var (name, value) in variables)
            {
                _previous[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Dispose()
        {
            foreach (var (name, value) in _previous)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    /// <summary>
    /// Takes a defensive copy of a live Aspire collection (resources, annotations), retrying if
    /// the orchestrator mutates it mid-enumeration. Aspire adds annotations from its own
    /// lifecycle threads while the application is being built and started, so enumerating those
    /// collections directly is an intermittent "Collection was modified" away from failing.
    /// </summary>
    public static T[] Snapshot<T>(IEnumerable<object> source)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return source.OfType<T>().ToArray();
            }
            catch (InvalidOperationException) when (attempt < 20)
            {
                Thread.Sleep(25);
            }
        }
    }

    // ------------------------------------------------------------------
    // Mongo client helpers
    // ------------------------------------------------------------------

    public static IMongoDatabase GetDatabase(string connectionString, string databaseName, TimeSpan timeout)
    {
        var settings = MongoClientSettings.FromConnectionString(connectionString);
        settings.ServerSelectionTimeout = timeout;
        settings.ConnectTimeout = timeout;
        settings.SocketTimeout = timeout;

        return new MongoClient(settings).GetDatabase(databaseName);
    }

    /// <summary>Single connection attempt with short timeouts, for negative assertions.</summary>
    public static async Task PingOnceAsync(string connectionString, string databaseName, CancellationToken cancellationToken)
    {
        var database = GetDatabase(connectionString, databaseName, TimeSpan.FromSeconds(10));
        await database.RunCommandAsync((Command<BsonDocument>)"{ ping: 1 }", cancellationToken: cancellationToken);
    }

    /// <summary>Connects with retries, for positive assertions where the container may still be starting.</summary>
    public static async Task<IMongoDatabase> ConnectAsync(string connectionString, string databaseName, CancellationToken cancellationToken)
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

    /// <summary>Insert, count, delete, count — the standard proof that a database is really usable.</summary>
    public static async Task AssertRoundTripAsync(
        string connectionString,
        string databaseName,
        string collectionName,
        string documentName,
        CancellationToken cancellationToken)
    {
        var database = await ConnectAsync(connectionString, databaseName, cancellationToken);
        var collection = database.GetCollection<BsonDocument>(collectionName);

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

    public static async Task WaitForHealthCheckAsync(
        HealthCheckService healthCheckService,
        string healthCheckKey,
        CancellationToken cancellationToken)
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

    // ------------------------------------------------------------------
    // AppHost log capture
    // ------------------------------------------------------------------

    /// <summary>Collects every message written through the AppHost's logger factory.</summary>
    public sealed class LogSink
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

    public sealed class LogSinkProvider(LogSink sink) : ILoggerProvider
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

    // ------------------------------------------------------------------
    // Docker introspection
    // ------------------------------------------------------------------

    /// <summary>
    /// Runs a docker command and returns (exit code, stdout). Both streams are drained
    /// concurrently with the wait because docker's JSON output routinely exceeds the pipe buffer,
    /// and the whole thing is bounded so a wedged daemon cannot stall the run.
    /// </summary>
    public static async Task<(int ExitCode, string StandardOutput)> RunDockerAsync(params string[] arguments)
    {
        var (exitCode, standardOutput, _) = await RunDockerCoreAsync(arguments);
        return (exitCode, standardOutput);
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunDockerCoreAsync(
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start 'docker {string.Join(' ', arguments)}'.");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);

        try
        {
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(timeout.Token));
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            throw new InvalidOperationException(
                $"'docker {string.Join(' ', arguments)}' did not complete within 30s; the Docker daemon appears unresponsive.");
        }

        return (process.ExitCode, await stdout, await stderr);
    }

    /// <summary>
    /// Resolves the Docker container id for a resource from its own snapshot.
    /// </summary>
    /// <remarks>
    /// Deliberately not "find the container publishing the endpoint port": DCP puts a proxy on
    /// the port Aspire allocates and publishes the container on a different, random host port, so
    /// <c>docker ps --filter publish=&lt;allocated&gt;</c> matches nothing. Nor a name-prefix
    /// match, which is ambiguous while test classes run in parallel. The snapshot is the only
    /// source that ties this resource to this container.
    /// </remarks>
    public static async Task<string> GetContainerIdAsync(
        DistributedApplication app,
        string resourceName,
        CancellationToken cancellationToken)
    {
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));

        try
        {
            await foreach (var resourceEvent in notifications.WatchAsync(timeout.Token))
            {
                if (!string.Equals(resourceEvent.Resource.Name, resourceName, StringComparison.Ordinal))
                {
                    continue;
                }

                var containerId = resourceEvent.Snapshot.Properties
                    .FirstOrDefault(p => string.Equals(p.Name, "container.id", StringComparison.OrdinalIgnoreCase))
                    ?.Value as string;

                if (!string.IsNullOrWhiteSpace(containerId))
                {
                    return containerId;
                }
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Fall through to the failure below.
        }

        throw new InvalidOperationException(
            $"Resource '{resourceName}' never reported a container id in its snapshot.");
    }

    /// <summary>Reads the effective environment of a running container as a dictionary.</summary>
    public static async Task<IReadOnlyDictionary<string, string>> GetContainerEnvironmentAsync(string containerId)
    {
        var (exitCode, output) = await RunDockerAsync(
            "inspect", containerId, "--format", "{{range .Config.Env}}{{println .}}{{end}}");

        Assert.Equal(0, exitCode);

        var environment = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0)
            {
                environment[line[..separator]] = line[(separator + 1)..];
            }
        }

        return environment;
    }

    public static async Task<string> GetContainerLogsAsync(string containerId)
    {
        var (_, output, error) = await RunDockerCoreAsync("logs", containerId);
        return CombineStandardOutputAndError(output, error);
    }

    public static async Task<string> GetContainerLogsSinceAsync(string containerId, DateTimeOffset since)
    {
        var (exitCode, output, error) = await RunDockerCoreAsync(
            "logs",
            "--since",
            since.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            containerId);

        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"'docker logs --since' failed for container '{containerId}' with exit code {exitCode}: {error}");
        }

        return CombineStandardOutputAndError(output, error);
    }

    internal static string NormalizeContainerLogs(string logs) =>
        AnsiEscapeSequenceRegex.Replace(logs, string.Empty);

    internal static string CombineStandardOutputAndError(string standardOutput, string standardError)
    {
        if (string.IsNullOrEmpty(standardOutput))
        {
            return standardError;
        }

        if (string.IsNullOrEmpty(standardError) || standardOutput.EndsWith('\n'))
        {
            return standardOutput + standardError;
        }

        return standardOutput + Environment.NewLine + standardError;
    }

    /// <summary>
    /// Runs a shell command as root against a host directory bind-mounted at <c>/probe</c>.
    /// </summary>
    /// <remarks>
    /// The DocumentDB entrypoint runs <c>initdb</c> against <c>DATA_PATH</c>, which leaves the data
    /// directory mode 0700 owned by the container's uid. Linux bind mounts share the host inode, so
    /// once the container has started, a test process running under a different uid — which is the
    /// case on CI runners — can no longer even enumerate the host path. Docker Desktop on Windows and
    /// macOS hides that behind a filesystem translation layer, which is why host-side enumeration
    /// only fails on Linux. Reading the mount back through a container behaves the same everywhere.
    /// The curated DocumentDB image doubles as the probe so no additional image has to be pulled.
    /// </remarks>
    public static async Task<(int ExitCode, string StandardOutput)> RunInBindMountAsync(
        string hostPath,
        string shellCommand,
        bool isReadOnly = true) =>
        await RunDockerAsync(
            "run", "--rm", "--user", "0:0", "--entrypoint", "/bin/sh",
            "-v", isReadOnly ? $"{hostPath}:/probe:ro" : $"{hostPath}:/probe",
            $"{DocumentDBContainerImageTags.Registry}/{DocumentDBContainerImageTags.Image}:{DocumentDBContainerImageTags.Tag}",
            "-c", shellCommand);

    /// <summary>Lists the entries of a bind-mounted host directory from inside a container.</summary>
    public static async Task<string[]> ListBindMountEntriesAsync(string hostPath)
    {
        var (exitCode, output) = await RunInBindMountAsync(hostPath, "ls -A /probe");

        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"Could not list the bind-mounted host path '{hostPath}' from a container (exit code {exitCode}).");
        }

        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// Widens the modes under a bind-mounted host directory so the test process can delete it.
    /// </summary>
    /// <remarks>Best effort: a leaked temp directory is acceptable, a masked test failure is not.</remarks>
    public static async Task TryRelaxBindMountPermissionsAsync(string hostPath)
    {
        try
        {
            await RunInBindMountAsync(hostPath, "chmod -R 0777 /probe", isReadOnly: false);
        }
        catch (InvalidOperationException)
        {
            // Docker is unavailable or wedged; the host-side delete will fall back to leaking.
        }
    }

    public static async Task RemoveVolumeAsync(string volumeName) =>
        await RunDockerAsync("volume", "rm", "-f", volumeName);

    /// <summary>Appends TLS options, choosing the separator instead of assuming a query exists.</summary>
    public static string WithTlsOptions(string connectionString, bool insecure) =>
        connectionString
        + (connectionString.Contains('?', StringComparison.Ordinal) ? "&" : "?")
        + (insecure ? "tls=true&tlsInsecure=true" : "tls=true");
}
