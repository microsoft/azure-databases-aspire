// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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
    private static readonly string s_candidateImage =
        $"{DocumentDBContainerImageTags.Registry}/{DocumentDBContainerImageTags.Image}:pg17-{CandidateVersion}";

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
        await EnsureImageAsync();

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
        await EnsureImageAsync();

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
        await EnsureImageAsync();

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

    private static string UniqueName(string prefix) =>
        $"docdb-storage-{prefix}-{Guid.NewGuid().ToString("N")[..8]}";

    private static async Task EnsureImageAsync()
    {
        var (exitCode, _) = await RunDockerAsync("image", "inspect", s_candidateImage);
        if (exitCode == 0)
        {
            return;
        }

        var (pullExit, output) = await RunDockerAsync("pull", s_candidateImage);
        Assert.True(pullExit == 0, $"Could not pull '{s_candidateImage}': {output}");
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
