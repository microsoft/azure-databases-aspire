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
/// The single reading of a resource's final container-runtime arguments.
/// </summary>
/// <param name="Verdict">What the arguments do.</param>
/// <param name="Option">
/// The option responsible, by name only. Never an operand: an operand is where a mount source, a
/// variable's value or a password would be, and this string reaches diagnostics.
/// </param>
internal readonly record struct DocumentDBRuntimeArgumentFinding(
    DocumentDBRuntimeArgumentVerdict Verdict,
    string? Option)
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
/// package applies to storage is written against the model, so an argument that reaches past the
/// model reaches past the rules.
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
    /// Options that add, remove or re-point container storage. <c>--read-only</c> is here because
    /// it makes every path that is not a mount unwritable, which is the same failure as mounting
    /// the data directory read-only.
    /// </summary>
    private static readonly HashSet<string> s_storageOptions = new(StringComparer.Ordinal)
    {
        "--mount",
        "--volume",
        "-v",
        "--volumes-from",
        "--tmpfs",
        "--read-only",
    };

    /// <summary>
    /// Options that set or import environment variables. Whether one of them matters depends on
    /// the variable it names, which is the caller's to decide — see
    /// <see cref="Read(IEnumerable{object}, Func{string, bool})"/>.
    /// </summary>
    private static readonly HashSet<string> s_environmentOptions = new(StringComparer.Ordinal)
    {
        "--env",
        "-e",
        "--env-file",
    };

    private static readonly HashSet<string> s_entrypointOptions = new(StringComparer.Ordinal)
    {
        "--entrypoint",
    };

    /// <summary>
    /// The <c>docker run</c> options that consume the token after them, so that the token is an
    /// operand rather than the next option.
    /// </summary>
    /// <remarks>
    /// Taken from the <c>docker run</c> reference. Options this package refuses outright are in the
    /// set too, because their arity still decides how the rest of the line is read when a refusal
    /// is turned into a report rather than a throw.
    /// </remarks>
    private static readonly HashSet<string> s_valueTakingOptions = new(StringComparer.Ordinal)
    {
        "--add-host", "--annotation", "--attach", "-a", "--blkio-weight", "--blkio-weight-device",
        "--cap-add", "--cap-drop", "--cgroup-parent", "--cgroupns", "--cidfile", "--cpu-period",
        "--cpu-quota", "--cpu-rt-period", "--cpu-rt-runtime", "--cpu-shares", "-c", "--cpus",
        "--cpuset-cpus", "--cpuset-mems", "--device", "--device-cgroup-rule",
        "--device-read-bps", "--device-read-iops", "--device-write-bps", "--device-write-iops",
        "--dns", "--dns-option", "--dns-search", "--domainname", "--entrypoint", "--env", "-e",
        "--env-file", "--expose", "--gpus", "--group-add", "--health-cmd", "--health-interval",
        "--health-retries", "--health-start-interval", "--health-start-period",
        "--health-timeout", "--hostname", "-h", "--io-maxbandwidth", "--io-maxiops", "--ip",
        "--ip6", "--ipc", "--isolation", "--kernel-memory", "--label", "-l", "--label-file",
        "--link", "--link-local-ip", "--log-driver", "--log-opt", "--mac-address", "--memory",
        "-m", "--memory-reservation", "--memory-swap", "--memory-swappiness", "--mount",
        "--name", "--network", "--net", "--network-alias", "--net-alias", "--oom-score-adj",
        "--pid", "--pids-limit", "--platform", "--publish", "-p", "--pull", "--restart",
        "--runtime", "--security-opt", "--shm-size", "--stop-signal", "--stop-timeout",
        "--storage-opt", "--sysctl", "--tmpfs", "--ulimit", "--user", "-u", "--userns", "--uts",
        "--volume", "-v", "--volume-driver", "--volumes-from", "--workdir", "-w",
    };

    /// <summary>
    /// The single-letter forms of <see cref="s_valueTakingOptions"/>, for reading a cluster such
    /// as <c>-itv</c>, where the runtime hands the rest of the cluster (or the next token) to the
    /// first letter that takes a value.
    /// </summary>
    private static readonly HashSet<char> s_valueTakingShortOptions =
        [.. s_valueTakingOptions
            .Where(option => option.Length == 2 && option[0] == '-' && option[1] != '-')
            .Select(option => option[1])];

    /// <summary>
    /// Reads <paramref name="arguments"/> and reports the first token that could change storage,
    /// an environment variable <paramref name="ownsEnvironmentVariable"/> claims, or the entry
    /// point.
    /// </summary>
    /// <param name="arguments">
    /// The final argument list, as the runtime will receive it. Entries that are not
    /// <see cref="string"/> are values Aspire resolves afterwards; they are read by position only,
    /// never resolved — resolving one here would duplicate Aspire's own evaluation of it and could
    /// pull a secret into this package.
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
        var pendingOption = (string?)null;
        var expectOperand = false;

        for (var index = 0; index < tokens.Count; index++)
        {
            var argument = tokens[index];

            if (expectOperand)
            {
                expectOperand = false;

                if (pendingOption is { } option && !JudgeOperand(option, argument, ownsEnvironmentVariable, out var operandFinding))
                {
                    return operandFinding;
                }

                pendingOption = null;
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
                    ? new(DocumentDBRuntimeArgumentVerdict.Image, token)
                    : DocumentDBRuntimeArgumentFinding.Harmless;
            }

            if (token.Length > 0 && token[0] != '-')
            {
                // A bare operand is read as the image, and the image Aspire appends becomes the
                // command. The run would not be the run that was sealed.
                return new(DocumentDBRuntimeArgumentVerdict.Image, token);
            }

            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                var separator = token.IndexOf('=', StringComparison.Ordinal);
                var name = separator < 0 ? token : token[..separator];
                var inlineValue = separator < 0 ? null : token[(separator + 1)..];

                if (!JudgeOption(name, inlineValue, ownsEnvironmentVariable, out var finding))
                {
                    return finding;
                }

                if (separator < 0 && s_valueTakingOptions.Contains(name))
                {
                    pendingOption = name;
                    expectOperand = true;
                }

                continue;
            }

            if (!ReadShortOptionCluster(token, ownsEnvironmentVariable, out var clusterFinding, out var clusterPending))
            {
                return clusterFinding;
            }

            if (clusterPending is not null)
            {
                pendingOption = clusterPending;
                expectOperand = true;
            }
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
        out string? pendingOption)
    {
        finding = DocumentDBRuntimeArgumentFinding.Harmless;
        pendingOption = null;

        for (var index = 1; index < token.Length; index++)
        {
            var name = string.Concat("-", token[index]);
            var takesValue = s_valueTakingShortOptions.Contains(token[index]);

            // '-eFOO=bar' and '-e FOO=bar' are the same call; so are '-itv /a:/b' and '-i -t -v /a:/b'.
            var rest = index + 1 < token.Length ? token[(index + 1)..] : null;
            var inlineValue = takesValue
                ? (rest is not null && rest[0] == '=' ? rest[1..] : rest)
                : null;

            if (!JudgeOption(name, inlineValue, ownsEnvironmentVariable, out finding))
            {
                return false;
            }

            if (takesValue)
            {
                if (inlineValue is null)
                {
                    pendingOption = name;
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
        string name,
        string? inlineValue,
        Func<string, bool> ownsEnvironmentVariable,
        out DocumentDBRuntimeArgumentFinding finding)
    {
        if (s_storageOptions.Contains(name))
        {
            finding = new(DocumentDBRuntimeArgumentVerdict.Storage, name);
            return false;
        }

        if (s_entrypointOptions.Contains(name))
        {
            finding = new(DocumentDBRuntimeArgumentVerdict.Entrypoint, name);
            return false;
        }

        if (s_environmentOptions.Contains(name))
        {
            // '--env-file' names a file whose contents decide which variables are set, and reading
            // it here would be a second reading of a file the runtime reads at start.
            if (string.Equals(name, "--env-file", StringComparison.Ordinal))
            {
                finding = new(DocumentDBRuntimeArgumentVerdict.Environment, name);
                return false;
            }

            if (inlineValue is not null && !JudgeEnvironmentAssignment(name, inlineValue, ownsEnvironmentVariable, out finding))
            {
                return false;
            }
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
        string option,
        object operand,
        Func<string, bool> ownsEnvironmentVariable,
        out DocumentDBRuntimeArgumentFinding finding)
    {
        if (!s_environmentOptions.Contains(option))
        {
            finding = DocumentDBRuntimeArgumentFinding.Harmless;
            return true;
        }

        if (operand is not string assignment)
        {
            // The name of the variable is inside a value that is not known yet, so whether it is
            // one of this package's cannot be decided without resolving it.
            finding = new(DocumentDBRuntimeArgumentVerdict.Undecidable, option);
            return false;
        }

        return JudgeEnvironmentAssignment(option, assignment, ownsEnvironmentVariable, out finding);
    }

    /// <summary>
    /// Whether an <c>--env</c> assignment names a variable this package owns. A bare name with no
    /// <c>=</c> is the runtime's "import this one from the host environment", which sets the
    /// variable just as surely.
    /// </summary>
    private static bool JudgeEnvironmentAssignment(
        string option,
        string assignment,
        Func<string, bool> ownsEnvironmentVariable,
        out DocumentDBRuntimeArgumentFinding finding)
    {
        var separator = assignment.IndexOf('=', StringComparison.Ordinal);
        var variable = separator < 0 ? assignment : assignment[..separator];

        if (ownsEnvironmentVariable(variable))
        {
            // The variable's name, not its value: the value is what a password would be in.
            finding = new(DocumentDBRuntimeArgumentVerdict.Environment, string.Concat(option, " ", variable));
            return false;
        }

        finding = DocumentDBRuntimeArgumentFinding.Harmless;
        return true;
    }
}
