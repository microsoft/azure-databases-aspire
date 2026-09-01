// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Xunit;
using static Aspire.Hosting.DocumentDB.Tests.DocumentDBEndToEndSupport;

namespace Aspire.Hosting.DocumentDB.Tests;

/// <summary>
/// Container-level proof for the storage claims the package's guards and documentation rest on:
/// DocumentDB <c>0.116.0</c> declares <c>/data</c> as an image volume, refuses to share a data
/// directory with a second container, and cannot run against a read-only one.
/// </summary>
/// <remarks>
/// These drive <c>docker</c> directly rather than an AppHost, because the model-level guards now
/// reject two of the three configurations before a container is ever created. Testing them through
/// Aspire would only re-test the guard; the point here is that the guard describes what the image
/// really does.
/// </remarks>
[Trait("Category", "Integration")]
public class DocumentDBStorageBehaviorTests
{
    private const string CandidateVersion = "0.116.0";
    private const string BaselineVersion = "0.114.0";

    private static readonly string s_candidateImage =
        $"{DocumentDBContainerImageTags.Registry}/{DocumentDBContainerImageTags.Image}:pg17-{CandidateVersion}";

    private static readonly string s_baselineImage =
        $"{DocumentDBContainerImageTags.Registry}/{DocumentDBContainerImageTags.Image}:pg17-{BaselineVersion}";

    private static readonly string[] s_credentialEnvironment =
    [
        "-e", "USERNAME=storageprobe",
        "-e", "PASSWORD=Storage_Passw0rd!",
        "-e", "SKIP_INIT_DATA=true",
    ];

    /// <summary>
    /// The image declares <c>/data</c> as a volume, so a run that mounts nothing there still gets a
    /// container-runtime-managed anonymous volume. That is what <c>WithDataVolume()</c> suppresses
    /// by mounting on the same path, and what a non-default target path cannot suppress.
    /// </summary>
    [Fact]
    public async Task ImageDeclaresDataVolumeAndUnmountedRunsGetAnAnonymousVolume()
    {
        RequireDocker();
        await EnsureImageAsync(s_candidateImage);

        var (inspectExit, declared) = await RunDockerAsync(
            "image", "inspect", s_candidateImage, "--format", "{{json .Config.Volumes}}");
        Assert.Equal(0, inspectExit);
        Assert.Contains("/data", declared, StringComparison.Ordinal);

        var containerName = UniqueName("anon");
        try
        {
            var (runExit, _) = await RunDockerAsync(
                ["run", "-d", "--name", containerName, .. s_credentialEnvironment, s_candidateImage]);
            Assert.Equal(0, runExit);

            var (mountsExit, mountsJson) = await RunDockerAsync(
                "inspect", containerName, "--format", "{{json .Mounts}}");
            Assert.Equal(0, mountsExit);

            using var document = JsonDocument.Parse(mountsJson);
            var dataMount = document.RootElement
                .EnumerateArray()
                .Single(mount => mount.GetProperty("Destination").GetString() == "/data");

            Assert.Equal("volume", dataMount.GetProperty("Type").GetString());

            // An anonymous volume: the runtime names it with a random 64-character hex id rather
            // than anything derived from the container or the app.
            var name = dataMount.GetProperty("Name").GetString();
            Assert.NotNull(name);
            Assert.Equal(64, name!.Length);
            Assert.DoesNotContain(containerName, name, StringComparison.Ordinal);
        }
        finally
        {
            // Remove the anonymous volume with the container; a plain `docker rm` would strand it,
            // which is exactly the behaviour documented for unmounted runs.
            await RunDockerAsync("rm", "-f", "-v", containerName);
        }
    }

    /// <summary>
    /// A persisted data directory backs exactly one running container: the entrypoint takes an
    /// exclusive lock on it, and the second container refuses to start rather than corrupting it.
    /// </summary>
    [Fact]
    public async Task DataDirectoryIsClaimedByOneContainerAtATime()
    {
        RequireDocker();
        await EnsureImageAsync(s_candidateImage);

        var volumeName = UniqueName("lock-vol");
        var firstContainer = UniqueName("lock-a");
        var secondContainer = UniqueName("lock-b");

        try
        {
            var (createExit, _) = await RunDockerAsync("volume", "create", volumeName);
            Assert.Equal(0, createExit);

            var (firstExit, _) = await RunDockerAsync(
                ["run", "-d", "--name", firstContainer, "-v", $"{volumeName}:/data", .. s_credentialEnvironment, s_candidateImage]);
            Assert.Equal(0, firstExit);

            await WaitForLogAsync(firstContainer, "database system is ready to accept connections");

            // Same volume, second container: the lock is held, so this one must exit instead of
            // opening a second PostgreSQL instance on the same data directory.
            var (secondExit, _) = await RunDockerAsync(
                ["run", "--name", secondContainer, "-v", $"{volumeName}:/data", .. s_credentialEnvironment, s_candidateImage]);
            Assert.NotEqual(0, secondExit);

            var logs = await GetContainerLogsAsync(secondContainer);
            Assert.Contains(
                "another DocumentDB container is already using the data directory",
                logs,
                StringComparison.Ordinal);

            // The refusal must not take the running container down with it.
            var (stateExit, state) = await RunDockerAsync("inspect", firstContainer, "--format", "{{.State.Running}}");
            Assert.Equal(0, stateExit);
            Assert.Equal("true", state.Trim());
        }
        finally
        {
            await RunDockerAsync("rm", "-f", "-v", firstContainer);
            await RunDockerAsync("rm", "-f", "-v", secondContainer);
            await RemoveVolumeAsync(volumeName);
        }
    }

    /// <summary>
    /// A read-only data directory cannot work: <c>initdb</c> has to take ownership of it. The
    /// failure is also slow and misattributed, which is why the package rejects the configuration
    /// at build time instead of letting the container run.
    /// </summary>
    [Fact]
    public async Task ReadOnlyDataDirectoryFailsInitializationWithAMisleadingTimeout()
    {
        RequireDocker();
        await EnsureImageAsync(s_candidateImage);

        var volumeName = UniqueName("ro-vol");
        var containerName = UniqueName("ro");

        try
        {
            var (createExit, _) = await RunDockerAsync("volume", "create", volumeName);
            Assert.Equal(0, createExit);

            var (runExit, _) = await RunDockerAsync(
                ["run", "-d", "--name", containerName, "-v", $"{volumeName}:/data:ro", .. s_credentialEnvironment, s_candidateImage]);
            Assert.Equal(0, runExit);

            // The banner a user actually notices blames PostgreSQL start-up timing, and only
            // appears a full minute after the container already knew it could not proceed.
            var logs = await WaitForLogAsync(containerName, "PostgreSQL failed to start within 60 seconds");

            // The real causes are individual lines inside interleaved log streams.
            Assert.Contains("chown: changing ownership of '/data': Read-only file system", logs, StringComparison.Ordinal);
            Assert.Contains(
                "initdb: error: could not change permissions of directory \"/data\": Read-only file system",
                logs,
                StringComparison.Ordinal);

            var (_, exitCodeOutput) = await RunDockerAsync("inspect", containerName, "--format", "{{.State.ExitCode}}");
            Assert.Equal("1", exitCodeOutput.Trim());
        }
        finally
        {
            await RunDockerAsync("rm", "-f", "-v", containerName);
            await RemoveVolumeAsync(volumeName);
        }
    }

    /// <summary>
    /// The image volume is a v0.116-0 addition, not a property of DocumentDB Local in general:
    /// on the previous release an unmounted <c>/data</c> is an ordinary directory in the
    /// container's writable layer. The package's anonymous-volume warning is gated on this
    /// difference, so the difference itself is worth asserting.
    /// </summary>
    [Fact]
    public async Task ThePreviousImageDeclaresNoDataVolume()
    {
        RequireDocker();
        await EnsureImageAsync(s_baselineImage);

        var (inspectExit, declared) = await RunDockerAsync(
            "image", "inspect", s_baselineImage, "--format", "{{json .Config.Volumes}}");

        Assert.Equal(0, inspectExit);
        Assert.Equal("null", declared.Trim());
    }

    /// <summary>
    /// A data directory that holds anything other than a PostgreSQL cluster is refused, not
    /// cleaned: the container leaves the contents alone, never starts PostgreSQL, and exits behind
    /// the same misleading 60-second banner. One stray dot-file — a <c>.gitkeep</c> committed to
    /// keep the directory in source control, or a <c>.DS_Store</c> the host wrote — is enough.
    /// </summary>
    [Fact]
    public async Task ANonEmptyDataDirectoryWithoutAClusterIsRefusedAndLeftIntact()
    {
        RequireDocker();
        await EnsureImageAsync(s_candidateImage);

        var hostDirectory = Path.Combine(AppContext.BaseDirectory, UniqueName("stray"));
        Directory.CreateDirectory(hostDirectory);
        var strayFile = Path.Combine(hostDirectory, ".gitkeep");
        await File.WriteAllTextAsync(strayFile, "keep");

        var containerName = UniqueName("stray");
        try
        {
            var (runExit, _) = await RunDockerAsync(
                ["run", "-d", "--name", containerName, "-v", $"{hostDirectory}:/data", .. s_credentialEnvironment, s_candidateImage]);
            Assert.Equal(0, runExit);

            var logs = await WaitForLogAsync(containerName, "PostgreSQL failed to start within 60 seconds");

            Assert.Contains(
                "Directory /data exists but doesn't appear to contain a valid PostgreSQL data directory",
                logs,
                StringComparison.Ordinal);

            var (_, exitCodeOutput) = await RunDockerAsync("inspect", containerName, "--format", "{{.State.ExitCode}}");
            Assert.Equal("1", exitCodeOutput.Trim());

            // The refusal is not destructive: the stray file is still there, and nothing was
            // initialized over it.
            Assert.True(File.Exists(strayFile), "The container removed the stray file it refused to initialize over.");
            Assert.Equal("keep", await File.ReadAllTextAsync(strayFile));
            Assert.False(File.Exists(Path.Combine(hostDirectory, "PG_VERSION")));
        }
        finally
        {
            await RunDockerAsync("rm", "-f", "-v", containerName);
            TryDeleteDirectory(hostDirectory);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // The container took ownership of the contents; leaving the scratch directory behind
            // in the test output folder is preferable to failing a passing test on cleanup.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string UniqueName(string prefix) =>
        $"docdb-storage-{prefix}-{Guid.NewGuid().ToString("N")[..8]}";

    /// <summary>
    /// Makes the candidate image available locally, pulling it if necessary.
    /// </summary>
    /// <remarks>
    /// The shared <see cref="RunDockerAsync"/> helper bounds every command at 30 seconds, which is
    /// right for introspection and far too short for a multi-hundred-megabyte pull on a cold CI
    /// agent. The pull therefore runs through a local runner with its own generous deadline. The
    /// work is memoized in a <see cref="Lazy{T}"/> so it happens exactly once per test class no
    /// matter which test runs first or how many run in parallel — no test may depend on another
    /// having warmed the cache.
    /// </remarks>
    private static readonly ConcurrentDictionary<string, Lazy<Task>> s_imagesReady = new(StringComparer.Ordinal);

    private static Task EnsureImageAsync(string image) =>
        s_imagesReady.GetOrAdd(image, key => new Lazy<Task>(() => PullImageAsync(key))).Value;

    private static async Task PullImageAsync(string image)
    {
        var (inspectExit, _, _) = await RunDockerWithTimeoutAsync(ImageInspectTimeout, "image", "inspect", image);
        if (inspectExit == 0)
        {
            return;
        }

        var (pullExit, output, error) = await RunDockerWithTimeoutAsync(ImagePullTimeout, "pull", image);
        Assert.True(pullExit == 0, $"Could not pull '{image}': {output}{Environment.NewLine}{error}");
    }

    private static readonly TimeSpan ImageInspectTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ImagePullTimeout = TimeSpan.FromMinutes(15);

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunDockerWithTimeoutAsync(
        TimeSpan timeout,
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

        using var deadline = new CancellationTokenSource(timeout);

        // Both streams are drained concurrently with the wait: a pull writes progress to stderr
        // continuously, and a full pipe buffer would deadlock the process.
        var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var stderr = process.StandardError.ReadToEndAsync(deadline.Token);

        try
        {
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(deadline.Token));
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
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
                $"'docker {string.Join(' ', arguments)}' did not complete within {timeout.TotalSeconds:0}s.");
        }

        return (process.ExitCode, await stdout, await stderr);
    }

    private static async Task<string> WaitForLogAsync(string containerName, string expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        var logs = string.Empty;

        while (DateTime.UtcNow < deadline)
        {
            logs = await GetContainerLogsAsync(containerName);
            if (logs.Contains(expected, StringComparison.Ordinal))
            {
                return logs;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        Assert.Fail($"Container '{containerName}' never logged '{expected}'. Logs:{Environment.NewLine}{logs}");
        return logs;
    }
}
