// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.RegularExpressions;

namespace Aspire.Hosting.DocumentDB.Tests;

internal enum BindMountRestartOutcome
{
    /// <summary>The restarted container served the persisted data directory.</summary>
    Reachable,

    /// <summary>
    /// PostgreSQL refused the data directory in this run because its owner was not the
    /// postmaster's.
    /// </summary>
    RefusedForStaleOwnership,
}

/// <summary>
/// Reads a <c>documentdb-local</c> container log and decides whether <em>this</em> run refused the
/// mounted data directory over its ownership, and whether that refusal is allowed at all.
/// </summary>
/// <remarks>
/// Both questions are pure functions over strings so they can be tested without Docker, which is
/// the only way to cover the cases that matter: a historical failure replayed into a healthy run,
/// and a refusal on a runtime that is expected to succeed.
/// </remarks>
internal static partial class DocumentDBBindMountRestart
{
    /// <summary>
    /// Written by the entrypoint's own <c>pg_ctl</c> invocation in the current run.
    /// </summary>
    /// <remarks>
    /// Unlike the PostgreSQL log lines, this cannot be replayed from a previous run: it is
    /// produced by this container's <c>start_oss_server.sh</c> and reaches the container's stdout
    /// (and the container-local <c>oss_server.log</c>), neither of which lives in the mounted data
    /// directory.
    /// </remarks>
    internal const string PostmasterStartFailureMarker = "pg_ctl: could not start server";

    /// <summary>
    /// Prefixes the entrypoint puts on lines it streams out of <c>DATA_PATH/pglog.log</c>, which
    /// survives the container and is therefore replayed into the next run's <c>docker logs</c>.
    /// </summary>
    private static readonly string[] PersistedPostgresLogPrefixes = ["[POSTGRES]", "[POSTGRES-SYSTEM]"];

    /// <summary>PostgreSQL's own words when it rejects a data directory's owner.</summary>
    internal static string StaleOwnershipFatal(string dataPath) =>
        $"data directory \"{dataPath}\" has wrong ownership";

    // "[POSTGRES] 2026-09-01 19:12:19.586 UTC [78] FATAL: ..." — the stream prefix is optional
    // because the same line also appears unprefixed in pglog.log itself.
    [GeneratedRegex(
        @"^(?:\[[^\]]*\]\s*)?(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}(?:\.\d+)?) UTC\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex PostgresLogTimestampRegex();

    /// <summary>
    /// Whether the log shows the <em>current</em> run failing to start PostgreSQL because the data
    /// directory's owner was not the postmaster's.
    /// </summary>
    /// <remarks>
    /// Two independent current-run anchors, because the naive "the log contains the FATAL" test is
    /// wrong: the entrypoint tails the persisted <c>pglog.log</c>, so a failure recorded by an
    /// earlier container is replayed into a later, healthy one and would classify a successful
    /// recovery as a refusal.
    /// <list type="number">
    /// <item>The current run's own <see cref="PostmasterStartFailureMarker"/> must be present on a
    /// line that did not come out of the persisted PostgreSQL log. A run whose postmaster started
    /// never emits it, so a recovery can never be read as a refusal.</item>
    /// <item>At least one matching FATAL must carry a timestamp at or after
    /// <paramref name="containerStartedAt"/>, so the replayed tail of an older run is ignored on
    /// its own evidence rather than by position in the stream.</item>
    /// </list>
    /// A FATAL whose timestamp cannot be parsed is not accepted; the caller then keeps waiting and
    /// ultimately fails with the whole log attached, which is the right outcome if the container's
    /// log format ever changes underneath this.
    /// </remarks>
    public static bool IndicatesStaleOwnershipRefusal(
        string containerLog,
        string dataPath,
        DateTimeOffset containerStartedAt)
    {
        ArgumentNullException.ThrowIfNull(containerLog);
        ArgumentException.ThrowIfNullOrEmpty(dataPath);

        var lines = containerLog.Split('\n');

        if (!Array.Exists(lines, IsCurrentRunPostmasterStartFailure))
        {
            return false;
        }

        var fatal = StaleOwnershipFatal(dataPath);

        foreach (var line in lines)
        {
            if (line.Contains(fatal, StringComparison.Ordinal) &&
                TryReadPostgresTimestamp(line, out var loggedAt) &&
                loggedAt >= containerStartedAt)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCurrentRunPostmasterStartFailure(string line) =>
        line.Contains(PostmasterStartFailureMarker, StringComparison.Ordinal) &&
        !IsStreamedFromPersistedPostgresLog(line);

    private static bool IsStreamedFromPersistedPostgresLog(string line)
    {
        var trimmed = line.TrimStart();

        return Array.Exists(
            PersistedPostgresLogPrefixes,
            prefix => trimmed.StartsWith(prefix, StringComparison.Ordinal));
    }

    internal static bool TryReadPostgresTimestamp(string line, out DateTimeOffset timestamp)
    {
        timestamp = default;

        var match = PostgresLogTimestampRegex().Match(line.TrimStart());
        if (!match.Success)
        {
            return false;
        }

        if (!DateTime.TryParse(
                match.Groups["ts"].Value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return false;
        }

        timestamp = new DateTimeOffset(parsed, TimeSpan.Zero);
        return true;
    }

    /// <summary>
    /// Throws unless the observed restart outcome is one this runtime is allowed to produce.
    /// </summary>
    /// <remarks>
    /// Reachable is always allowed. A refusal is allowed only on Docker Desktop, whose host file
    /// sharing applies <c>chown(2)</c> asynchronously — measured on macOS/VirtioFS, and expected on
    /// its Windows and Linux hosts, which share the same shared-filesystem design. Everywhere else
    /// a refusal is a persistence regression in this package or its image, not a platform
    /// limitation, and is reported as such with the container's own log.
    /// </remarks>
    public static void AssertOutcomeIsAllowed(
        BindMountRestartOutcome outcome,
        ContainerRuntimeDescription runtime,
        string containerLog)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        if (outcome == BindMountRestartOutcome.Reachable || runtime.IsDockerDesktop)
        {
            return;
        }

        throw new InvalidOperationException(
            "The restarted DocumentDB container refused the bind-mounted data directory over its " +
            "ownership on a runtime that is expected to hand it over. Only Docker Desktop may fail " +
            "this: its host file sharing applies chown(2) asynchronously, so the postmaster reads " +
            "the previous owner. On a native engine the mount is an ordinary one and this is a " +
            "persistence regression, not a platform limitation. " +
            $"Runtime: {runtime}." +
            $"{Environment.NewLine}Container log:{Environment.NewLine}{containerLog}");
    }
}
