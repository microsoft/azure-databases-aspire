// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.DocumentDB;

/// <summary>
/// What a raw container-runtime argument does to a resource this package guards.
/// </summary>
internal enum DocumentDBRuntimeArgumentVerdict
{
    /// <summary>The arguments change nothing this package is responsible for.</summary>
    Harmless,

    /// <summary>An option that mounts, un-mounts or re-points storage.</summary>
    Storage,

    /// <summary>An option that sets or imports an environment variable this package owns.</summary>
    Environment,

    /// <summary>An option that replaces the image's entry point.</summary>
    Entrypoint,

    /// <summary>An operand that would be read as the image, displacing the sealed one.</summary>
    Image,

    /// <summary>
    /// A value that is only known later, in a position where it could be any of the above.
    /// </summary>
    Undecidable,
}

/// <summary>
/// Whether an option consumes the token after it.
/// </summary>
internal enum DocumentDBRuntimeOptionArity
{
    None,
    Required,
}

/// <summary>
/// What an option reaches, and therefore whether this package can let it through.
/// </summary>
internal enum DocumentDBRuntimeOptionEffect
{
    None,
    Storage,
    Entrypoint,
    Environment,
    EnvironmentFile,
}

/// <summary>
/// One <c>docker run</c> option, in both the spellings the runtime accepts for it.
/// </summary>
/// <param name="LongName">The option's name without the <c>--</c> prefix.</param>
/// <param name="ShortName">The single-letter form, when the runtime has one.</param>
/// <param name="Arity">Whether the option consumes a value.</param>
/// <param name="Effect">What the option reaches.</param>
internal readonly record struct DocumentDBRuntimeOption(
    string LongName,
    char? ShortName,
    DocumentDBRuntimeOptionArity Arity,
    DocumentDBRuntimeOptionEffect Effect = DocumentDBRuntimeOptionEffect.None);

/// <summary>
/// The single reading of a resource's final container-runtime arguments.
/// </summary>
/// <param name="Verdict">What the arguments do.</param>
/// <param name="Option">
/// The option responsible, spelled as the caller wrote it — <c>-v</c>, <c>--mount</c> — and for an
/// environment option followed by the name of the variable it sets. Never the value of an option:
/// that is where a mount source, a variable's value or a password would be, and this string reaches
/// diagnostics. The one operand that does appear is the one the runtime would read as the image,
/// which is an image reference and is named for the same reason the image seal names both of its
/// own.
/// </param>
/// <param name="CanonicalOption">
/// The same option by its long name, so a diagnostic can report one spelling however the caller
/// wrote it.
/// </param>
/// <param name="Variable">
/// The environment variable an <see cref="DocumentDBRuntimeArgumentVerdict.Environment"/> finding
/// names, or <see langword="null"/> when the option supplies no readable name — an environment
/// file, or an option left without a value.
/// </param>
internal readonly record struct DocumentDBRuntimeArgumentFinding(
    DocumentDBRuntimeArgumentVerdict Verdict,
    string? Option,
    string? CanonicalOption = null,
    string? Variable = null)
{
    public static DocumentDBRuntimeArgumentFinding Harmless => default;
}

/// <summary>
/// Reads a resource's final container-runtime arguments with the container runtime's own option
/// grammar, and reports the first one that could change something this package has already
/// decided.
/// </summary>
/// <remarks>
/// <para>
/// Aspire passes these straight through to <c>docker run</c> / <c>podman run</c> ahead of the
/// image, so an argument here is not configuration this package can observe through the model: a
/// <c>--mount</c> written this way adds storage without a <c>ContainerMountAnnotation</c>, a
/// <c>--env</c> sets a variable without an <c>EnvironmentCallbackAnnotation</c>, and an
/// <c>--entrypoint</c> replaces the entry point without touching
/// <see cref="Aspire.Hosting.ApplicationModel.ContainerResource.Entrypoint"/>. Every rule this
/// package applies to storage, to the command, and to the telemetry wrapper is written against the
/// model, so an argument that reaches past the model reaches past the rules.
/// </para>
/// <para>
/// Which is why this is a parser and not a search. The tokens have a grammar — an option that
/// takes a value consumes the next token, or carries it after an <c>=</c>; short options cluster
/// until one of them takes a value; <c>--</c> ends option parsing — and a search would both miss
/// and over-report: <c>--label -v</c> passes a harmless label, and <c>--memory 512m</c> is not a
/// mount however the token after it reads. Only options that can actually reach storage, this
/// package's environment or the entry point are refused; everything else the runtime accepts is
/// passed through, which is what keeps <c>--cap-add</c>, <c>--network</c>, <c>--memory</c>,
/// <c>--label</c>, <c>--pull</c> and the rest usable.
/// </para>
/// <para>
/// There is exactly one of these for the package. The storage rules and the OpenTelemetry
/// compatibility wrapper both have to judge the same finished line, and two readings of one
/// grammar would be two chances to disagree with the runtime; they call this with their own
/// idea of which environment variables matter and turn the same finding into their own
/// diagnostic.
/// </para>
/// <para>
/// Unknown options are read as flags rather than as value-taking options. That is the fail-closed
/// direction for what this exists to catch: a token after an unrecognised option is still examined,
/// so a <c>--mount</c> cannot hide behind one. The cost is a false report for an unrecognised
/// option whose <em>value</em> is spelled exactly like a mount or entry-point option, which no real
/// configuration does.
/// </para>
/// </remarks>
internal static class DocumentDBContainerRuntimeArguments
{
    /// <summary>
    /// The <c>docker run</c> grammar this package needs: every option that consumes a following
    /// value, plus the short boolean options needed to read a compact token such as <c>-it</c>,
    /// plus the value-less options that still reach storage. Keeping it in one typed table is what
    /// prevents a value such as <c>--mount</c>, supplied to an unrelated option, from being
    /// mistaken for an option of its own.
    /// </summary>
    /// <remarks>
    /// <c>--read-only</c> and <c>--use-api-socket</c> take no value and are still storage:
    /// the first makes every path that is not a mount unwritable, which is the same failure as
    /// mounting the data directory read-only, and the second binds the caller's API socket into
    /// the container.
    /// </remarks>
    private static readonly DocumentDBRuntimeOption[] s_options =
    [
        new("add-host", null, DocumentDBRuntimeOptionArity.Required),
        new("annotation", null, DocumentDBRuntimeOptionArity.Required),
        new("attach", 'a', DocumentDBRuntimeOptionArity.Required),
        new("blkio-weight", null, DocumentDBRuntimeOptionArity.Required),
        new("blkio-weight-device", null, DocumentDBRuntimeOptionArity.Required),
        new("cap-add", null, DocumentDBRuntimeOptionArity.Required),
        new("cap-drop", null, DocumentDBRuntimeOptionArity.Required),
        new("cgroup-parent", null, DocumentDBRuntimeOptionArity.Required),
        new("cgroupns", null, DocumentDBRuntimeOptionArity.Required),
        new("cidfile", null, DocumentDBRuntimeOptionArity.Required),
        new("cpu-period", null, DocumentDBRuntimeOptionArity.Required),
        new("cpu-quota", null, DocumentDBRuntimeOptionArity.Required),
        new("cpu-rt-period", null, DocumentDBRuntimeOptionArity.Required),
        new("cpu-rt-runtime", null, DocumentDBRuntimeOptionArity.Required),
        new("cpu-shares", 'c', DocumentDBRuntimeOptionArity.Required),
        new("cpus", null, DocumentDBRuntimeOptionArity.Required),
        new("cpuset-cpus", null, DocumentDBRuntimeOptionArity.Required),
        new("cpuset-mems", null, DocumentDBRuntimeOptionArity.Required),
        new("detach", 'd', DocumentDBRuntimeOptionArity.None),
        new("detach-keys", null, DocumentDBRuntimeOptionArity.Required),
        new("device", null, DocumentDBRuntimeOptionArity.Required),
        new("device-cgroup-rule", null, DocumentDBRuntimeOptionArity.Required),
        new("device-read-bps", null, DocumentDBRuntimeOptionArity.Required),
        new("device-read-iops", null, DocumentDBRuntimeOptionArity.Required),
        new("device-write-bps", null, DocumentDBRuntimeOptionArity.Required),
        new("device-write-iops", null, DocumentDBRuntimeOptionArity.Required),
        new("dns", null, DocumentDBRuntimeOptionArity.Required),
        new("dns-option", null, DocumentDBRuntimeOptionArity.Required),
        new("dns-search", null, DocumentDBRuntimeOptionArity.Required),
        new("domainname", null, DocumentDBRuntimeOptionArity.Required),
        new("entrypoint", null, DocumentDBRuntimeOptionArity.Required, DocumentDBRuntimeOptionEffect.Entrypoint),
        new("env", 'e', DocumentDBRuntimeOptionArity.Required, DocumentDBRuntimeOptionEffect.Environment),
        new("env-file", null, DocumentDBRuntimeOptionArity.Required, DocumentDBRuntimeOptionEffect.EnvironmentFile),
        new("expose", null, DocumentDBRuntimeOptionArity.Required),
        new("gpus", null, DocumentDBRuntimeOptionArity.Required),
        new("group-add", null, DocumentDBRuntimeOptionArity.Required),
        new("health-cmd", null, DocumentDBRuntimeOptionArity.Required),
        new("health-interval", null, DocumentDBRuntimeOptionArity.Required),
        new("health-retries", null, DocumentDBRuntimeOptionArity.Required),
        new("health-start-interval", null, DocumentDBRuntimeOptionArity.Required),
        new("health-start-period", null, DocumentDBRuntimeOptionArity.Required),
        new("health-timeout", null, DocumentDBRuntimeOptionArity.Required),
        new("hostname", 'h', DocumentDBRuntimeOptionArity.Required),
        new("interactive", 'i', DocumentDBRuntimeOptionArity.None),
        new("io-maxbandwidth", null, DocumentDBRuntimeOptionArity.Required),
        new("io-maxiops", null, DocumentDBRuntimeOptionArity.Required),
        new("ip", null, DocumentDBRuntimeOptionArity.Required),
        new("ip6", null, DocumentDBRuntimeOptionArity.Required),
        new("ipc", null, DocumentDBRuntimeOptionArity.Required),
        new("isolation", null, DocumentDBRuntimeOptionArity.Required),
        new("kernel-memory", null, DocumentDBRuntimeOptionArity.Required),
        new("label", 'l', DocumentDBRuntimeOptionArity.Required),
        new("label-file", null, DocumentDBRuntimeOptionArity.Required),
        new("link", null, DocumentDBRuntimeOptionArity.Required),
        new("link-local-ip", null, DocumentDBRuntimeOptionArity.Required),
        new("log-driver", null, DocumentDBRuntimeOptionArity.Required),
        new("log-opt", null, DocumentDBRuntimeOptionArity.Required),
        new("mac-address", null, DocumentDBRuntimeOptionArity.Required),
        new("memory", 'm', DocumentDBRuntimeOptionArity.Required),
        new("memory-reservation", null, DocumentDBRuntimeOptionArity.Required),
        new("memory-swap", null, DocumentDBRuntimeOptionArity.Required),
        new("memory-swappiness", null, DocumentDBRuntimeOptionArity.Required),
        new("mount", null, DocumentDBRuntimeOptionArity.Required, DocumentDBRuntimeOptionEffect.Storage),
        new("name", null, DocumentDBRuntimeOptionArity.Required),
        new("net", null, DocumentDBRuntimeOptionArity.Required),
        new("net-alias", null, DocumentDBRuntimeOptionArity.Required),
        new("network", null, DocumentDBRuntimeOptionArity.Required),
        new("network-alias", null, DocumentDBRuntimeOptionArity.Required),
        new("oom-score-adj", null, DocumentDBRuntimeOptionArity.Required),
        new("pid", null, DocumentDBRuntimeOptionArity.Required),
        new("pids-limit", null, DocumentDBRuntimeOptionArity.Required),
        new("platform", null, DocumentDBRuntimeOptionArity.Required),
        new("publish", 'p', DocumentDBRuntimeOptionArity.Required),
        new("publish-all", 'P', DocumentDBRuntimeOptionArity.None),
        new("pull", null, DocumentDBRuntimeOptionArity.Required),
        new("quiet", 'q', DocumentDBRuntimeOptionArity.None),
        new("read-only", null, DocumentDBRuntimeOptionArity.None, DocumentDBRuntimeOptionEffect.Storage),
        new("restart", null, DocumentDBRuntimeOptionArity.Required),
        new("runtime", null, DocumentDBRuntimeOptionArity.Required),
        new("security-opt", null, DocumentDBRuntimeOptionArity.Required),
        new("shm-size", null, DocumentDBRuntimeOptionArity.Required),
        new("stop-signal", null, DocumentDBRuntimeOptionArity.Required),
        new("stop-timeout", null, DocumentDBRuntimeOptionArity.Required),
        new("storage-opt", null, DocumentDBRuntimeOptionArity.Required),
        new("sysctl", null, DocumentDBRuntimeOptionArity.Required),
        new("tmpfs", null, DocumentDBRuntimeOptionArity.Required, DocumentDBRuntimeOptionEffect.Storage),
        new("tty", 't', DocumentDBRuntimeOptionArity.None),
        new("ulimit", null, DocumentDBRuntimeOptionArity.Required),
        new("use-api-socket", null, DocumentDBRuntimeOptionArity.None, DocumentDBRuntimeOptionEffect.Storage),
        new("user", 'u', DocumentDBRuntimeOptionArity.Required),
        new("userns", null, DocumentDBRuntimeOptionArity.Required),
        new("uts", null, DocumentDBRuntimeOptionArity.Required),
        new("volume", 'v', DocumentDBRuntimeOptionArity.Required, DocumentDBRuntimeOptionEffect.Storage),
        new("volume-driver", null, DocumentDBRuntimeOptionArity.Required, DocumentDBRuntimeOptionEffect.Storage),
        new("volumes-from", null, DocumentDBRuntimeOptionArity.Required, DocumentDBRuntimeOptionEffect.Storage),
        new("workdir", 'w', DocumentDBRuntimeOptionArity.Required),
    ];

    private static readonly Dictionary<string, DocumentDBRuntimeOption> s_byLongName =
        s_options.ToDictionary(option => option.LongName, StringComparer.Ordinal);

    private static readonly Dictionary<char, DocumentDBRuntimeOption> s_byShortName =
        s_options.Where(option => option.ShortName is not null)
            .ToDictionary(option => option.ShortName!.Value);

    /// <summary>
    /// Reads <paramref name="arguments"/> and reports the first token that could change storage,
    /// an environment variable <paramref name="ownsEnvironmentVariable"/> claims, the entry point,
    /// or the image.
    /// </summary>
    /// <param name="arguments">
    /// The final argument list, as the runtime will receive it. Entries that are not
    /// <see cref="string"/> are values Aspire resolves afterwards; they are read by position only,
    /// never resolved — resolving one here would duplicate Aspire's own evaluation of it and could
    /// pull a secret into this package. A caller that has already resolved the list, because it
    /// has to hand the exact strings on, simply passes strings.
    /// </param>
    /// <param name="ownsEnvironmentVariable">
    /// Whether a variable of that name is one whose value this package has already decided, and
    /// which therefore may not be set from outside the model.
    /// </param>
    internal static DocumentDBRuntimeArgumentFinding Read(
        IEnumerable<object> arguments,
        Func<string, bool> ownsEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(ownsEnvironmentVariable);

        var tokens = arguments as IReadOnlyList<object> ?? [.. arguments];

        // Mirrors the runtime's own cursor: an option that takes a value consumes exactly the next
        // token, whatever that token looks like.
        DocumentDBRuntimeOption? pendingOption = null;
        string? pendingSpelling = null;

        for (var index = 0; index < tokens.Count; index++)
        {
            var argument = tokens[index];

            if (pendingOption is { } pending)
            {
                pendingOption = null;

                if (!JudgeOperand(pending, pendingSpelling!, argument, ownsEnvironmentVariable, out var operandFinding))
                {
                    return operandFinding;
                }

                pendingSpelling = null;
                continue;
            }

            if (argument is not string token)
            {
                // An option name is the one position where a value that is not yet known cannot be
                // ruled out: it could resolve to '--mount', '-v' or '--entrypoint'.
                return new(DocumentDBRuntimeArgumentVerdict.Undecidable, null);
            }

            if (token == "--")
            {
                // Option parsing ends here. Everything after is positional, and the first
                // positional is the image; on its own the terminator supplies none, so the image
                // Aspire appends is still the image.
                return index + 1 < tokens.Count
                    ? new(DocumentDBRuntimeArgumentVerdict.Image, token, token)
                    : DocumentDBRuntimeArgumentFinding.Harmless;
            }

            if (token.Length > 0 && token[0] != '-')
            {
                // A bare operand is read as the image, and the image Aspire appends becomes the
                // command. The run would not be the run that was sealed.
                return new(DocumentDBRuntimeArgumentVerdict.Image, token, token);
            }

            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                var separator = token.IndexOf('=', StringComparison.Ordinal);
                var spelling = separator < 0 ? token : token[..separator];
                var inlineValue = separator < 0 ? null : token[(separator + 1)..];

                if (!s_byLongName.TryGetValue(spelling[2..], out var option))
                {
                    continue;
                }

                if (!JudgeOption(option, spelling, inlineValue, ownsEnvironmentVariable, out var finding))
                {
                    return finding;
                }

                if (separator < 0 && option.Arity == DocumentDBRuntimeOptionArity.Required)
                {
                    pendingOption = option;
                    pendingSpelling = spelling;
                }

                continue;
            }

            if (!ReadShortOptionCluster(token, ownsEnvironmentVariable, out var clusterFinding, out var clusterPending, out var clusterSpelling))
            {
                return clusterFinding;
            }

            if (clusterPending is { } shortPending)
            {
                pendingOption = shortPending;
                pendingSpelling = clusterSpelling;
            }
        }

        if (pendingOption is { Effect: DocumentDBRuntimeOptionEffect.Environment } trailing)
        {
            // The runtime would refuse the line outright; this package refuses it first, because
            // an option left without a value names no variable it can clear.
            return new(
                DocumentDBRuntimeArgumentVerdict.Environment,
                pendingSpelling,
                "--" + trailing.LongName);
        }

        return DocumentDBRuntimeArgumentFinding.Harmless;
    }

    /// <summary>
    /// Reads a single-dash token, which the runtime treats as a cluster of short options: each
    /// letter in turn, until one takes a value, at which point the rest of the cluster is that
    /// value and — when the cluster ends there — the next token is.
    /// </summary>
    private static bool ReadShortOptionCluster(
        string token,
        Func<string, bool> ownsEnvironmentVariable,
        out DocumentDBRuntimeArgumentFinding finding,
        out DocumentDBRuntimeOption? pendingOption,
        out string? pendingSpelling)
    {
        finding = DocumentDBRuntimeArgumentFinding.Harmless;
        pendingOption = null;
        pendingSpelling = null;

        for (var index = 1; index < token.Length; index++)
        {
            if (!s_byShortName.TryGetValue(token[index], out var option))
            {
                // An unrecognised letter cannot be given an arity, so the rest of the cluster is
                // left alone rather than guessed at.
                return true;
            }

            var spelling = string.Concat("-", token[index]);
            var takesValue = option.Arity == DocumentDBRuntimeOptionArity.Required;

            // '-eFOO=bar' and '-e FOO=bar' are the same call; so are '-itv /a:/b' and '-i -t -v /a:/b'.
            var rest = index + 1 < token.Length ? token[(index + 1)..] : null;
            var inlineValue = takesValue
                ? (rest is not null && rest[0] == '=' ? rest[1..] : rest)
                : null;

            if (!JudgeOption(option, spelling, inlineValue, ownsEnvironmentVariable, out finding))
            {
                return false;
            }

            if (takesValue)
            {
                if (inlineValue is null)
                {
                    pendingOption = option;
                    pendingSpelling = spelling;
                }

                return true;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether an option — with its value, when the token carried one — is one this package can
    /// let through.
    /// </summary>
    private static bool JudgeOption(
        DocumentDBRuntimeOption option,
        string spelling,
        string? inlineValue,
        Func<string, bool> ownsEnvironmentVariable,
        out DocumentDBRuntimeArgumentFinding finding)
    {
        var canonical = "--" + option.LongName;

        switch (option.Effect)
        {
            case DocumentDBRuntimeOptionEffect.Storage:
                finding = new(DocumentDBRuntimeArgumentVerdict.Storage, spelling, canonical);
                return false;

            case DocumentDBRuntimeOptionEffect.Entrypoint:
                finding = new(DocumentDBRuntimeArgumentVerdict.Entrypoint, spelling, canonical);
                return false;

            case DocumentDBRuntimeOptionEffect.EnvironmentFile:
                // An environment file names variables this package cannot read without reading a
                // file the runtime reads at start, on a machine that may not be this one.
                finding = new(DocumentDBRuntimeArgumentVerdict.Environment, spelling, canonical);
                return false;

            case DocumentDBRuntimeOptionEffect.Environment when inlineValue is not null:
                return JudgeEnvironmentAssignment(spelling, canonical, inlineValue, ownsEnvironmentVariable, out finding);
        }

        finding = DocumentDBRuntimeArgumentFinding.Harmless;
        return true;
    }

    /// <summary>
    /// Whether the operand of a value-taking option is one this package can let through. Only the
    /// operand of an environment option is read at all: every other operand is a port, a label, a
    /// memory limit or a password, none of which this package has any business inspecting.
    /// </summary>
    private static bool JudgeOperand(
        DocumentDBRuntimeOption option,
        string spelling,
        object operand,
        Func<string, bool> ownsEnvironmentVariable,
        out DocumentDBRuntimeArgumentFinding finding)
    {
        if (option.Effect != DocumentDBRuntimeOptionEffect.Environment)
        {
            finding = DocumentDBRuntimeArgumentFinding.Harmless;
            return true;
        }

        var canonical = "--" + option.LongName;

        if (operand is not string assignment)
        {
            // The name of the variable is inside a value that is not known yet, so whether it is
            // one of this package's cannot be decided without resolving it.
            finding = new(DocumentDBRuntimeArgumentVerdict.Undecidable, spelling, canonical);
            return false;
        }

        return JudgeEnvironmentAssignment(spelling, canonical, assignment, ownsEnvironmentVariable, out finding);
    }

    /// <summary>
    /// Whether an <c>--env</c> assignment names a variable this package owns. A bare name with no
    /// <c>=</c> is the runtime's "import this one from the host environment", which sets the
    /// variable just as surely.
    /// </summary>
    private static bool JudgeEnvironmentAssignment(
        string spelling,
        string canonical,
        string assignment,
        Func<string, bool> ownsEnvironmentVariable,
        out DocumentDBRuntimeArgumentFinding finding)
    {
        var separator = assignment.IndexOf('=', StringComparison.Ordinal);
        var variable = separator < 0 ? assignment : assignment[..separator];

        if (ownsEnvironmentVariable(variable))
        {
            // The variable's name, not its value: the value is what a password would be in.
            finding = new(
                DocumentDBRuntimeArgumentVerdict.Environment,
                string.Concat(spelling, " ", variable),
                canonical,
                variable);
            return false;
        }

        finding = DocumentDBRuntimeArgumentFinding.Harmless;
        return true;
    }
}
