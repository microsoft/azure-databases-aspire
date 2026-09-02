// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Aspire.Hosting.DocumentDB.Tests;

/// <summary>
/// Covers the two decisions the bind-mount restart test makes without Docker: whether the current
/// run refused the mounted data directory, and whether this runtime is allowed to.
/// </summary>
/// <remarks>
/// Both were wrong in ways only a unit test can pin down. The classifier originally matched the
/// PostgreSQL FATAL anywhere in the log, which a replay of the persisted <c>pglog.log</c> tail
/// satisfies even when the run recovered; and the refusal was originally tolerated on every
/// runtime, which would have let a real persistence regression pass on Linux CI.
/// </remarks>
[Trait("Category", "Unit")]
public class DocumentDBBindMountRestartTests
{
    private const string DataPath = "/data";

    // Docker's own StartedAt format, nanosecond precision, taken from a real inspect.
    private const string ContainerStartedAtText = "2026-09-01T20:27:02.00239905Z";

    private static DateTimeOffset ContainerStartedAt =>
        DocumentDBEndToEndSupport.TryParseContainerStartedAt(ContainerStartedAtText, out var value)
            ? value
            : throw new InvalidOperationException("The sample container start time did not parse.");

    /// <summary>The entrypoint stdout and streamed PostgreSQL lines of a run that refused the directory.</summary>
    private const string RefusedRunLog = """
        Release Version: 0.116-0
        Using data path: /data
        Setting ownership of /data to documentdb:documentdb
        Starting OSS server...
        Calling: /usr/lib/postgresql/17/bin/pg_ctl start -D /data -o "-p 9712" -l /data/pglog.log
        pg_ctl: could not start server
        Examine the log output.
        Starting log streaming from /var/log/documentdb/postgres/pglog.log with prefix [POSTGRES]...
        [POSTGRES] 2026-09-01 20:27:02.297 UTC [53] FATAL:  data directory "/data" has wrong ownership
        [POSTGRES] 2026-09-01 20:27:02.297 UTC [53] HINT:  The server must be started by the user that owns the data directory.
        """;

    /// <summary>
    /// A healthy run whose log still replays the previous container's failure out of the persisted
    /// <c>pglog.log</c>: the FATAL predates this container and no <c>pg_ctl</c> failure is present.
    /// </summary>
    private const string RecoveredRunLogWithReplayedHistory = """
        Release Version: 0.116-0
        Using data path: /data
        Setting ownership of /data to documentdb:documentdb
        Starting OSS server...
        Calling: /usr/lib/postgresql/17/bin/pg_ctl start -D /data -o "-p 9712" -l /data/pglog.log
        waiting for server to start.... done
        server started
        Starting log streaming from /var/log/documentdb/postgres/pglog.log with prefix [POSTGRES]...
        [POSTGRES] 2026-09-01 19:12:19.586 UTC [78] FATAL:  data directory "/data" has wrong ownership
        [POSTGRES] 2026-09-01 19:12:19.586 UTC [78] HINT:  The server must be started by the user that owns the data directory.
        [POSTGRES] 2026-09-01 20:27:05.101 UTC [91] LOG:  database system is ready to accept connections
        === DocumentDB is ready ===
        """;

    // ------------------------------------------------------------------
    // Current-run classification
    // ------------------------------------------------------------------

    [Fact]
    public void CurrentRunOwnershipRefusalIsRecognised()
    {
        Assert.True(DocumentDBBindMountRestart.IndicatesStaleOwnershipRefusal(
            RefusedRunLog, DataPath, ContainerStartedAt));
    }

    [Fact]
    public void ReplayedHistoricalFatalFromAPreviousRunIsIgnored()
    {
        // The whole point: this log contains the FATAL, and the run succeeded.
        Assert.Contains(
            DocumentDBBindMountRestart.StaleOwnershipFatal(DataPath),
            RecoveredRunLogWithReplayedHistory,
            StringComparison.Ordinal);

        Assert.False(DocumentDBBindMountRestart.IndicatesStaleOwnershipRefusal(
            RecoveredRunLogWithReplayedHistory, DataPath, ContainerStartedAt));
    }

    [Fact]
    public void AFatalOlderThanTheContainerIsIgnoredEvenWhenThisRunAlsoFailedToStart()
    {
        // pg_ctl failed for some other reason and the tail replayed an old ownership FATAL.
        // Classifying that as an ownership refusal would let an unrelated regression through.
        var log = """
            Calling: /usr/lib/postgresql/17/bin/pg_ctl start -D /data -o "-p 9712" -l /data/pglog.log
            pg_ctl: could not start server
            [POSTGRES] 2026-09-01 19:12:19.586 UTC [78] FATAL:  data directory "/data" has wrong ownership
            """;

        Assert.False(DocumentDBBindMountRestart.IndicatesStaleOwnershipRefusal(log, DataPath, ContainerStartedAt));
    }

    [Fact]
    public void AFatalWithoutThisRunsPostmasterFailureIsIgnored()
    {
        var log = $"""
            Starting OSS server...
            [POSTGRES] 2026-09-01 20:27:02.297 UTC [53] FATAL:  data directory "{DataPath}" has wrong ownership
            """;

        Assert.False(DocumentDBBindMountRestart.IndicatesStaleOwnershipRefusal(log, DataPath, ContainerStartedAt));
    }

    [Fact]
    public void APostmasterFailureReplayedOutOfThePersistedPostgresLogDoesNotCount()
    {
        // [POSTGRES]/[POSTGRES-SYSTEM] are streamed from the data directory and therefore survive
        // the container, so they can never establish that THIS run failed.
        var log = $"""
            Starting OSS server...
            [POSTGRES] 2026-09-01 20:27:02.100 UTC [53] LOG:  {DocumentDBBindMountRestart.PostmasterStartFailureMarker}
            [POSTGRES] 2026-09-01 20:27:02.297 UTC [53] FATAL:  data directory "{DataPath}" has wrong ownership
            """;

        Assert.False(DocumentDBBindMountRestart.IndicatesStaleOwnershipRefusal(log, DataPath, ContainerStartedAt));
    }

    [Fact]
    public void AFatalNamingADifferentDataDirectoryIsIgnored()
    {
        var log = """
            pg_ctl: could not start server
            [POSTGRES] 2026-09-01 20:27:02.297 UTC [53] FATAL:  data directory "/other" has wrong ownership
            """;

        Assert.False(DocumentDBBindMountRestart.IndicatesStaleOwnershipRefusal(log, DataPath, ContainerStartedAt));
    }

    [Fact]
    public void AFatalWithoutAParseableTimestampIsNotAccepted()
    {
        // If the container's log_line_prefix ever changes, the caller must keep waiting and fail
        // with the whole log rather than guess that this run is the one that failed.
        var log = """
            pg_ctl: could not start server
            [POSTGRES] FATAL:  data directory "/data" has wrong ownership
            """;

        Assert.False(DocumentDBBindMountRestart.IndicatesStaleOwnershipRefusal(log, DataPath, ContainerStartedAt));
    }

    [Theory]
    [InlineData("[POSTGRES] 2026-09-01 20:27:02.297 UTC [53] FATAL:  boom", "2026-09-01T20:27:02.297Z")]
    [InlineData("2026-09-01 20:27:02 UTC [53] LOG:  plain", "2026-09-01T20:27:02.000Z")]
    public void PostgresLogTimestampsAreReadWithAndWithoutAStreamPrefix(string line, string expected)
    {
        Assert.True(DocumentDBBindMountRestart.TryReadPostgresTimestamp(line, out var timestamp));
        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), timestamp);
    }

    [Fact]
    public void ContainerStartTimesKeepTheirSubSecondPrecision()
    {
        Assert.True(DocumentDBEndToEndSupport.TryParseContainerStartedAt(ContainerStartedAtText, out var startedAt));
        Assert.Equal(TimeSpan.Zero, startedAt.Offset);

        // The refusing run's FATAL is only ~295ms later, so a parse that dropped the fraction
        // would put the FATAL before the container and silently stop recognising real refusals.
        Assert.True(DocumentDBBindMountRestart.TryReadPostgresTimestamp(
            "[POSTGRES] 2026-09-01 20:27:02.297 UTC [53] FATAL:  x", out var fatalAt));
        Assert.True(fatalAt > startedAt);
        Assert.True(fatalAt - startedAt < TimeSpan.FromSeconds(1));
    }

    // ------------------------------------------------------------------
    // The zero start time of a created-but-not-started container
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("0001-01-01T00:00:00Z")]
    [InlineData("0001-01-01T00:00:00.000000000Z")]
    [InlineData("0001-01-01T00:00:00+00:00")]
    public void TheZeroStartTimeOfAnUnstartedContainerIsRejectedAsAnAnchor(string value)
    {
        Assert.True(DocumentDBEndToEndSupport.TryParseContainerStartedAt(value, out var startedAt));
        Assert.True(DocumentDBEndToEndSupport.IsUnstartedContainerStartTime(startedAt));
    }

    [Fact]
    public void ARealStartTimeIsNotMistakenForTheZeroValue()
    {
        Assert.True(DocumentDBEndToEndSupport.TryParseContainerStartedAt(ContainerStartedAtText, out var startedAt));
        Assert.False(DocumentDBEndToEndSupport.IsUnstartedContainerStartTime(startedAt));
        Assert.False(DocumentDBEndToEndSupport.IsUnstartedContainerStartTime(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void TheZeroStartTimeWouldOtherwiseAcceptEveryReplayedFatal()
    {
        // Why the zero value has to be rejected rather than used: DateTimeOffset.MinValue is at or
        // before every timestamp in the log, so the "at or after the container started" gate
        // disappears and the previous run's failure classifies the current one.
        Assert.True(DocumentDBEndToEndSupport.IsUnstartedContainerStartTime(DateTimeOffset.MinValue));

        var log = """
            pg_ctl: could not start server
            [POSTGRES] 2026-09-01 19:12:19.586 UTC [78] FATAL:  data directory "/data" has wrong ownership
            """;

        Assert.True(DocumentDBBindMountRestart.IndicatesStaleOwnershipRefusal(
            log, DataPath, DateTimeOffset.MinValue));
        Assert.False(DocumentDBBindMountRestart.IndicatesStaleOwnershipRefusal(
            log, DataPath, ContainerStartedAt));
    }

    // ------------------------------------------------------------------
    // Which runtimes may refuse
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("Docker Desktop", true)]
    [InlineData("docker desktop", true)]
    [InlineData("Ubuntu 24.04.3 LTS", false)]
    [InlineData("Alpine Linux v3.20", false)]
    [InlineData("Debian GNU/Linux 12 (bookworm)", false)]
    [InlineData("Red Hat Enterprise Linux 9.4 (Plow)", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void DockerDesktopIsIdentifiedFromTheDaemonsOperatingSystemField(string? operatingSystem, bool expected)
    {
        Assert.Equal(expected, DocumentDBContainerRuntime.IsDockerDesktop(operatingSystem));
    }

    [Theory]
    [InlineData(true, false)]    // a Linux host is never granted the tolerance
    [InlineData(false, true)]
    public void TheFallbackNeverGrantsToleranceToALinuxHost(bool hostIsLinux, bool expected)
    {
        Assert.Equal(expected, DocumentDBContainerRuntime.FallbackIsDockerDesktop(hostIsLinux));
    }

    [Fact]
    public void AMissingDockerExecutableCountsAsAnUnansweredDaemon()
    {
        // Process.Start throws Win32Exception when the executable is not on PATH; a wedged daemon
        // surfaces as InvalidOperationException. Both must take the strict fallback rather than
        // escaping as an unexplained test failure.
        Assert.True(DocumentDBContainerRuntime.IsDaemonUnavailable(
            new System.ComponentModel.Win32Exception(2, "No such file or directory")));
        Assert.True(DocumentDBContainerRuntime.IsDaemonUnavailable(
            new InvalidOperationException("the Docker daemon appears unresponsive")));
    }

    [Fact]
    public void OtherFailuresAreNotSwallowedAsAnUnansweredDaemon()
    {
        Assert.False(DocumentDBContainerRuntime.IsDaemonUnavailable(new OperationCanceledException()));
        Assert.False(DocumentDBContainerRuntime.IsDaemonUnavailable(new OutOfMemoryException()));
        Assert.False(DocumentDBContainerRuntime.IsDaemonUnavailable(new FormatException()));
    }

    [Fact]
    public void ReachableIsAllowedOnEveryRuntime()
    {
        DocumentDBBindMountRestart.AssertOutcomeIsAllowed(
            BindMountRestartOutcome.Reachable, NativeEngine(), RefusedRunLog);

        DocumentDBBindMountRestart.AssertOutcomeIsAllowed(
            BindMountRestartOutcome.Reachable, DockerDesktop(), RefusedRunLog);
    }

    [Fact]
    public void RefusalIsToleratedOnDockerDesktop()
    {
        DocumentDBBindMountRestart.AssertOutcomeIsAllowed(
            BindMountRestartOutcome.RefusedForStaleOwnership, DockerDesktop(), RefusedRunLog);
    }

    [Fact]
    public void RefusalOnANativeEngineFailsWithTheFullDiagnosis()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            DocumentDBBindMountRestart.AssertOutcomeIsAllowed(
                BindMountRestartOutcome.RefusedForStaleOwnership, NativeEngine(), RefusedRunLog));

        Assert.Contains("Ubuntu 24.04.3 LTS", exception.Message, StringComparison.Ordinal);
        Assert.Contains("not tolerated", exception.Message, StringComparison.Ordinal);
        Assert.Contains("have not been characterised", exception.Message, StringComparison.Ordinal);
        Assert.Contains(RefusedRunLog, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUncharacterisedVmBackedRuntimeIsHeldStrictlyRatherThanCalledARegression()
    {
        // Rancher Desktop, Colima, Podman machine and friends may or may not share Docker
        // Desktop's deferred chown. Until one is measured it is held to the strict rule, and the
        // message says that rather than declaring the refusal a proven package defect.
        var uncharacterised = new ContainerRuntimeDescription(
            "Rancher Desktop", IsDockerDesktop: false, DaemonAnswered: true);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            DocumentDBBindMountRestart.AssertOutcomeIsAllowed(
                BindMountRestartOutcome.RefusedForStaleOwnership, uncharacterised, RefusedRunLog));

        Assert.Contains("Rancher Desktop", exception.Message, StringComparison.Ordinal);
        Assert.Contains("characterise it and add it to the tolerated set", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("this is a persistence regression, not a platform limitation", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusalIsNotToleratedWhenTheDaemonCouldNotBeAskedOnALinuxHost()
    {
        // The fallback path: docker info failed, the host is Linux, so the strict rule applies.
        var fallback = new ContainerRuntimeDescription(
            "Linux 6.8.0-generic",
            DocumentDBContainerRuntime.FallbackIsDockerDesktop(hostIsLinux: true),
            DaemonAnswered: false);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            DocumentDBBindMountRestart.AssertOutcomeIsAllowed(
                BindMountRestartOutcome.RefusedForStaleOwnership, fallback, RefusedRunLog));

        Assert.Contains("docker info could not be read", exception.Message, StringComparison.Ordinal);
    }

    private static ContainerRuntimeDescription DockerDesktop() =>
        new("Docker Desktop", IsDockerDesktop: true, DaemonAnswered: true);

    private static ContainerRuntimeDescription NativeEngine() =>
        new("Ubuntu 24.04.3 LTS", IsDockerDesktop: false, DaemonAnswered: true);
}
