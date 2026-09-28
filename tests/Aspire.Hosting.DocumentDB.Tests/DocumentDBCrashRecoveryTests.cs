// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;
using static Aspire.Hosting.DocumentDB.Tests.DocumentDBEndToEndSupport;

namespace Aspire.Hosting.DocumentDB.Tests;

/// <summary>
/// Nightly pilot: writes the server acknowledged as journaled must survive the container being
/// SIGKILLed and a new container being started on the same named volume.
/// </summary>
[Trait("Category", "Nightly")]
public class DocumentDBCrashRecoveryTests
{
    private const int GatewayPort = 10260;
    private const string UserName = "crashprobe";
    private const string Password = "Crash_Passw0rd";
    private const int DocumentCount = 200;

    private static readonly string s_image = DocumentDBImageDigestLock.PinnedReference(DocumentDBContainerImageTags.Tag);

    [Fact]
    public async Task AcknowledgedWritesSurviveAKilledContainer()
    {
        RequireDocker();
        using var cts = CreateEndToEndTimeoutSource();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var volumeName = $"docdb-crash-vol-{suffix}";
        var killedContainer = $"docdb-crash-a-{suffix}";
        var restartedContainer = $"docdb-crash-b-{suffix}";

        try
        {
            var (pullExit, _) = await RunDockerAsync(TimeSpan.FromMinutes(15), "pull", s_image);
            Assert.Equal(0, pullExit);
            Assert.Equal(0, (await RunDockerAsync("volume", "create", volumeName)).ExitCode);

            var collection = await StartAndConnectAsync(killedContainer, volumeName, cts.Token);
            var acknowledged = new List<ObjectId>(DocumentCount);
            for (var i = 0; i < DocumentCount; i++)
            {
                var id = ObjectId.GenerateNewId();
                await collection.InsertOneAsync(new BsonDocument { ["_id"] = id, ["n"] = i }, cancellationToken: cts.Token);
                acknowledged.Add(id);
            }

            Assert.Equal(0, (await RunDockerAsync("kill", "--signal", "KILL", killedContainer)).ExitCode);
            var (stateExit, running) = await RunDockerAsync("inspect", killedContainer, "--format", "{{.State.Running}}");
            Assert.Equal(0, stateExit);
            Assert.Equal("false", running.Trim());

            var recovered = await StartAndConnectAsync(restartedContainer, volumeName, cts.Token);
            var found = await recovered.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(cts.Token);

            Assert.Equal(acknowledged.Order(), found.Select(document => document["_id"].AsObjectId).Order());
        }
        catch (Exception exception)
        {
            // The containers and volume are removed below, so keep what a failure needs to be diagnosed.
            throw new InvalidOperationException(
                $"Crash recovery failed on {s_image}.{await DescribeAsync(killedContainer)}{await DescribeAsync(restartedContainer)}",
                exception);
        }
        finally
        {
            await RunDockerAsync("rm", "-f", "-v", killedContainer);
            await RunDockerAsync("rm", "-f", "-v", restartedContainer);
            await RemoveVolumeAsync(volumeName);
        }
    }

    private static async Task<string> DescribeAsync(string containerName)
    {
        var (_, image) = await RunDockerAsync("inspect", containerName, "--format", "{{.Image}}");
        return $"{Environment.NewLine}--- {containerName} (image {image.Trim()}) ---{Environment.NewLine}{await GetContainerLogsAsync(containerName)}";
    }

    private static async Task<IMongoCollection<BsonDocument>> StartAndConnectAsync(
        string containerName,
        string volumeName,
        CancellationToken cancellationToken)
    {
        var (runExit, _) = await RunDockerAsync(
            "run", "-d", "--name", containerName,
            "-p", $"127.0.0.1::{GatewayPort}",
            "-v", $"{volumeName}:/data",
            "-e", $"USERNAME={UserName}",
            "-e", $"PASSWORD={Password}",
            "-e", "SKIP_INIT_DATA=true",
            s_image);
        Assert.Equal(0, runExit);

        // "127.0.0.1:49153" from either runtime; the host port is what follows the last colon.
        var (portExit, binding) = await RunDockerAsync("port", containerName, $"{GatewayPort}/tcp");
        Assert.Equal(0, portExit);
        var hostPort = binding.Trim().Split('\n')[0].Trim().Split(':')[^1];

        var connectionString =
            $"mongodb://{UserName}:{Password}@127.0.0.1:{hostPort}/?authSource=admin&authMechanism=SCRAM-SHA-256";
        var database = await ConnectAsync(WithTlsOptions(connectionString, insecure: true), "crash", cancellationToken);

        return database
            .GetCollection<BsonDocument>("acknowledged")
            .WithWriteConcern(new WriteConcern(WriteConcern.WMajority.W, journal: true));
    }
}
