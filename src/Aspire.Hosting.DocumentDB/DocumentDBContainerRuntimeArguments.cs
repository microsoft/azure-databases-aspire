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

    /// <summary>An option that sets, imports, rewrites or clears environment this package owns.</summary>
    Environment,

    /// <summary>An option that replaces the image's entry point.</summary>
    Entrypoint,

    /// <summary>
    /// An option or operand that decides what the runtime runs, displacing the image the run was
    /// sealed on.
    /// </summary>
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
/// The option responsible, or <see langword="null"/> when no option is. This is always one of the
/// spellings held in this class's own tables, never a token the caller supplied — see the class
/// remarks.
/// </param>
/// <param name="Variable">
/// The package-owned environment variable an environment option names, or <see langword="null"/>.
/// This is always the name the owner itself supplied, never a token the caller supplied.
/// </param>
internal readonly record struct DocumentDBRuntimeArgumentFinding(
    DocumentDBRuntimeArgumentVerdict Verdict,
    string? Option,
    string? Variable)
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
/// <c>--label</c>, <c>--pull</c>, <c>--sysctl</c>, <c>--tz</c> and the rest usable.
/// </para>
/// <para>
/// The grammar is the union of the two runtimes Aspire drives, because which one is behind the
/// arguments is not this package's to know. Podman accepts everything Docker does and adds options
/// with no Docker equivalent that reach exactly the same places: <c>--secret</c> mounts a file or
/// sets a variable, <c>--image-volume</c> decides what happens to the image's own volumes,
/// <c>--init-path</c> bind-mounts a host binary, <c>--env-host</c> imports the whole host
/// environment, <c>--env-merge</c> rewrites a variable the image already carries,
/// <c>--unsetenv</c> and <c>--unsetenv-all</c> remove variables the guard wrote, and
/// <c>--rootfs</c> makes the operand a root filesystem path instead of an image. Their arities are
/// taken from the <c>podman-run</c> option reference, so an operand of a Podman-only option is
/// never mistaken for one of these or for the image.
/// </para>
/// <para>
/// Storage is not only what a <c>--mount</c> spells. An option that selects where a volume comes
/// from (<c>--volume-driver</c>), how the container's own root filesystem is built
/// (<c>--storage-opt</c>), which directories the runtime bind-mounts its managed files into
/// (<c>--chrootdirs</c>), whose <c>/dev/shm</c> the container gets (<c>--ipc</c>), which pod
/// supplies the container's infra namespaces and mounts (<c>--pod</c>, <c>--pod-id-file</c>), or
/// which mounts the runtime creates by itself (<c>--read-only-tmpfs</c>, <c>--systemd</c>,
/// <c>--use-api-socket</c>) reaches the same place a mount does, and reaches it without a
/// <c>ContainerMountAnnotation</c> for any rule to read. All of them are storage here.
/// </para>
/// <para>
/// Unknown options are read as flags rather than as value-taking options. That is the fail-closed
/// direction for what this exists to catch: a token after an unrecognised option is still examined,
/// so a <c>--mount</c> cannot hide behind one. The cost is a false report for an unrecognised
/// option whose <em>value</em> is spelled exactly like a mount or entry-point option, which no real
/// configuration does.
/// </para>
/// <para>
/// <strong>Nothing the caller wrote is ever reported.</strong> A finding carries an option spelling
/// and, for an environment option, a variable name — and both are returned from this class's own
/// tables or from the owner's own set, never from the token that was read. Operands are never
/// carried at all. That is what keeps a mount source, a variable's value, a positional token, or a
/// parameter or reference expression standing in for one, out of every diagnostic this produces:
/// any of them can be a credential, and a container-runtime argument is exactly where a caller
/// would put one.
/// </para>
/// </remarks>
internal static class DocumentDBContainerRuntimeArguments
{
    /// <summary>
    /// The end-of-options terminator. A literal of this class, so naming it in a diagnostic carries
    /// nothing the caller wrote.
    /// </summary>
    private const string OptionTerminator = "--";

    /// <summary>
    /// Options that add, remove or re-point container storage.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>--read-only</c> is here because it makes every path that is not a mount unwritable, which
    /// is the same failure as mounting the data directory read-only. The Podman-only three are
    /// storage by the same test: <c>--secret</c> mounts a file into the container (and with
    /// <c>type=env</c> sets a variable instead, which is no better), <c>--image-volume</c> decides
    /// whether the image's own <c>VOLUME</c> declarations become volumes, a tmpfs or nothing, and
    /// <c>--init-path</c> bind-mounts a host binary into the container.
    /// </para>
    /// <para>
    /// The rest create, select or alter storage without naming a mount. <c>--storage-opt</c> sets
    /// the storage driver's options for this container, which is the size and backing of the root
    /// filesystem the data directory sits on when nothing is mounted over it.
    /// <c>--volume-driver</c> chooses which driver supplies every <c>-v</c> volume, so the same
    /// declaration can be backed by a different host, and <c>--chrootdirs</c> makes the runtime
    /// bind-mount its own managed files into further directories inside the container.
    /// <c>--ipc</c> decides whose <c>/dev/shm</c> the container gets — <c>host</c> mounts the
    /// host's and <c>container:</c><em>id</em> another container's. <c>--pod</c> and
    /// <c>--pod-id-file</c> join a pod, whose infra container brings namespaces and the pod's own
    /// volumes with it, and <c>--pod</c> also accepts <c>new:</c><em>name</em>, which creates one.
    /// <c>--read-only-tmpfs</c> is what mounts a read-write tmpfs over <c>/dev</c>, <c>/dev/shm</c>,
    /// <c>/run</c>, <c>/tmp</c> and <c>/var/tmp</c> under <c>--read-only</c>; <c>--systemd</c> puts
    /// the container in systemd mode, which mounts tmpfs on <c>/run</c>, <c>/run/lock</c>,
    /// <c>/tmp</c> and <c>/var/log/journal</c> and mounts the cgroup filesystem; and
    /// <c>--use-api-socket</c> bind-mounts the runtime's own API socket and a synthesized
    /// credential file into the container. Every one of them is storage the application model never
    /// sees, and any of them can put the data directory somewhere <c>DATA_PATH</c> does not
    /// describe.
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> s_storageOptions = new(StringComparer.Ordinal)
    {
        "--mount",
        "--volume",
        "-v",
        "--volumes-from",
        "--tmpfs",
        "--read-only",
        "--read-only-tmpfs",
        "--secret",
        "--image-volume",
        "--init-path",
        "--storage-opt",
        "--volume-driver",
        "--chrootdirs",
        "--ipc",
        "--pod",
        "--pod-id-file",
        "--systemd",
        "--use-api-socket",
    };

    /// <summary>
    /// Environment options whose effect is confined to the variable they name, so whether they
    /// matter is decided by that name.
    /// </summary>
    /// <remarks>
    /// <c>--env</c>/<c>-e</c> set one variable; Podman's <c>--env-merge</c> rewrites one the image
    /// already carries, and <c>--unsetenv</c> removes one. All three name their variable to the
    /// left of the first <c>=</c>, so one that names a variable this package does not own is
    /// ordinary configuration and is passed through.
    /// </remarks>
    private static readonly HashSet<string> s_scopedEnvironmentOptions = new(StringComparer.Ordinal)
    {
        "--env",
        "-e",
        "--env-merge",
        "--unsetenv",
    };

    /// <summary>
    /// Environment options whose reach cannot be read from the token, and which are therefore
    /// refused outright.
    /// </summary>
    /// <remarks>
    /// <c>--env-file</c> names a file whose contents decide which variables are set, and reading it
    /// here would be a second reading of a file the runtime reads at start. Podman's
    /// <c>--env-host</c> imports the entire host environment, which is where a stray
    /// <c>DATA_PATH</c>, <c>USERNAME</c> or <c>PASSWORD</c> would come from, and
    /// <c>--unsetenv-all</c> clears every variable the image declares — including the canonical
    /// <c>DATA_PATH</c> this package writes, which would drop the container back onto whatever
    /// directory its own default names.
    /// </remarks>
    private static readonly HashSet<string> s_unscopedEnvironmentOptions = new(StringComparer.Ordinal)
    {
        "--env-file",
        "--env-host",
        "--unsetenv-all",
    };

    private static readonly HashSet<string> s_entrypointOptions = new(StringComparer.Ordinal)
    {
        "--entrypoint",
    };

    /// <summary>
    /// Options that decide what the runtime runs. Podman's <c>--rootfs</c> makes the operand a path
    /// to an exploded container rather than an image reference, so the run would not be the run
    /// that was sealed at all.
    /// </summary>
    private static readonly HashSet<string> s_imageOptions = new(StringComparer.Ordinal)
    {
        "--rootfs",
    };

    /// <summary>
    /// Every <c>docker run</c> and <c>podman run</c> option that consumes the token after it, so
    /// that the token is an operand rather than the next option — and, just as importantly, not the
    /// bare operand the runtime would read as the image.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The union of the two references. Options this package refuses outright are in the set too,
    /// because their arity still decides how the rest of the line is read. Boolean options are
    /// deliberately absent, and so is anything neither runtime documents: an unknown option is read
    /// as a flag, which keeps a following <c>--mount</c> visible.
    /// </para>
    /// <para>
    /// "Boolean" here means every option that does not consume the token after it. The flag parser
    /// both runtimes use gives a boolean flag a default value for the bare spelling, so
    /// <c>--read-only</c>, <c>--read-only-tmpfs</c> and <c>--use-api-socket</c> take their value
    /// only after an <c>=</c>. <c>--systemd</c> is kept out of this table for the same reason it is
    /// refused: it is <c>--systemd=true|false|always</c>, and reading it as consuming the next token
    /// would let a bare <c>--systemd</c> swallow a following <c>--mount=type=bind,...</c> as its
    /// operand and hide it. Refused in every form but an explicit <c>=false</c>, it can never
    /// consume anything; the cost is that a split <c>--systemd false</c> is refused as well, and
    /// <c>--systemd=false</c> is the spelling that passes.
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> s_valueTakingOptions = new(StringComparer.Ordinal)
    {
        // Shared by both runtimes.
        "--add-host", "--annotation", "--attach", "-a", "--blkio-weight", "--blkio-weight-device",
        "--cap-add", "--cap-drop", "--cgroup-parent", "--cgroupns", "--cidfile", "--cpu-period",
        "--cpu-quota", "--cpu-rt-period", "--cpu-rt-runtime", "--cpu-shares", "-c", "--cpus",
        "--cpuset-cpus", "--cpuset-mems", "--detach-keys", "--device", "--device-cgroup-rule",
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

        // Podman only. Present so that their operands are read as operands: without them a
        // '--tz local' would leave 'local' looking like the bare operand the runtime reads as the
        // image, and ordinary Podman configuration would be refused.
        "--arch", "--authfile", "--cert-dir", "--cgroup-conf", "--cgroups", "--chrootdirs",
        "--conmon-pidfile", "--creds", "--decryption-key", "--env-merge", "--gidmap",
        "--group-entry", "--health-log-destination", "--health-max-log-count",
        "--health-max-log-size", "--health-on-failure", "--health-startup-cmd",
        "--health-startup-interval", "--health-startup-retries", "--health-startup-success",
        "--health-startup-timeout", "--hosts-file", "--hostuser", "--image-volume", "--init-path",
        "--os", "--passwd-entry", "--personality", "--pidfile", "--pod", "--pod-id-file",
        "--preserve-fd", "--preserve-fds", "--rdt-class", "--requires", "--retry", "--retry-delay",
        "--sdnotify", "--seccomp-policy", "--secret", "--shm-size-systemd", "--signature-policy",
        "--subgidname", "--subuidname", "--timeout", "--tz", "--uidmap", "--umask",
        "--unsetenv", "--variant",
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
    /// Reads <paramref name="arguments"/> and reports the first token that could change storage, an
    /// environment variable <paramref name="resolveOwnedVariable"/> claims, the entry point, or
    /// what the runtime runs.
    /// </summary>
    /// <param name="arguments">
    /// The final argument list, as the runtime will receive it. Entries that are not
    /// <see cref="string"/> are values Aspire resolves afterwards; they are read by position only,
    /// never resolved and never rendered — resolving one here would duplicate Aspire's own
    /// evaluation of it, and rendering one would pull a parameter's value into this package.
    /// </param>
    /// <param name="resolveOwnedVariable">
    /// Given a variable name, the owner's own spelling of it when it is one whose value this
    /// package has already decided, and <see langword="null"/> otherwise. The owner's spelling is
    /// what a finding carries, so a name read from the arguments is used to look up and then
    /// discarded.
    /// </param>
    internal static DocumentDBRuntimeArgumentFinding Read(
        IEnumerable<object> arguments,
        Func<string, string?> resolveOwnedVariable)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(resolveOwnedVariable);

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

                if (pendingOption is { } option &&
                    !JudgeOperand(option, argument, resolveOwnedVariable, out var operandFinding))
                {
                    return operandFinding;
                }

                pendingOption = null;
                continue;
            }

            if (argument is not string token)
            {
                // An option name is the one position where a value that is not yet known cannot be
                // ruled out: it could resolve to '--mount', '-v', '--secret' or '--entrypoint'. It
                // is also the position a bare operand occupies, so it could equally be the image.
                return new(DocumentDBRuntimeArgumentVerdict.Undecidable, null, null);
            }

            if (token == OptionTerminator)
            {
                // Option parsing ends here. Everything after is positional, and the first
                // positional is the image; on its own the terminator supplies none, so the image
                // Aspire appends is still the image.
                return index + 1 < tokens.Count
                    ? new(DocumentDBRuntimeArgumentVerdict.Image, OptionTerminator, null)
                    : DocumentDBRuntimeArgumentFinding.Harmless;
            }

            if (token.Length > 0 && token[0] != '-')
            {
                // A bare operand is read as the image, and the image Aspire appends becomes the
                // command. The run would not be the run that was sealed. The token itself is not
                // reported: it is a value, and this is exactly where a caller would have written a
                // reference that carries credentials.
                return new(DocumentDBRuntimeArgumentVerdict.Image, null, null);
            }

            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                var separator = token.IndexOf('=', StringComparison.Ordinal);
                var name = separator < 0 ? token : token[..separator];
                var inlineValue = separator < 0 ? null : token[(separator + 1)..];

                if (!JudgeOption(name, inlineValue, resolveOwnedVariable, out var finding))
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

            if (!ReadShortOptionCluster(token, resolveOwnedVariable, out var clusterFinding, out var clusterPending))
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
        Func<string, string?> resolveOwnedVariable,
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

            if (!JudgeOption(name, inlineValue, resolveOwnedVariable, out finding))
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
    /// The spellings <c>pflag</c> — the flag parser both runtimes use — reads as <see langword="false"/>
    /// for a boolean flag.
    /// </summary>
    private static readonly HashSet<string> s_falseFlagValues = new(StringComparer.Ordinal)
    {
        "0", "f", "F", "false", "FALSE", "False",
    };

    /// <summary>
    /// The spellings Podman reads as "not systemd mode". <c>--systemd</c> is not a boolean flag but
    /// a three-valued one — <c>true</c>, <c>false</c> or <c>always</c>, matched without regard to
    /// case — so <c>pflag</c>'s boolean spellings are not what reads it, and <c>--systemd=0</c> is
    /// a value Podman rejects rather than a request to turn it off.
    /// </summary>
    private static readonly HashSet<string> s_systemdFalseValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "false",
    };

    /// <summary>
    /// The options this package refuses that never consume the token after them, mapped to the
    /// values the runtime reads as the caller declining them.
    /// </summary>
    /// <remarks>
    /// These take a value only after an <c>=</c>, so the bare spelling is the option being set and
    /// the token after it is the next option — which is why <c>--systemd --mount=type=bind,...</c>
    /// cannot hide the mount as an operand. An explicit off value is the caller saying "do not do
    /// the thing", which is what this package wants; refusing it would be a refusal of the safe
    /// spelling. Only spellings the runtime's own parser reads that way are treated as off —
    /// anything else, including a value neither runtime would accept, is read as the option being
    /// set.
    /// </remarks>
    private static readonly Dictionary<string, HashSet<string>> s_valuelessOptions = new(StringComparer.Ordinal)
    {
        ["--read-only"] = s_falseFlagValues,
        ["--read-only-tmpfs"] = s_falseFlagValues,
        ["--use-api-socket"] = s_falseFlagValues,
        ["--env-host"] = s_falseFlagValues,
        ["--unsetenv-all"] = s_falseFlagValues,
        ["--rootfs"] = s_falseFlagValues,
        ["--systemd"] = s_systemdFalseValues,
    };

    /// <summary>
    /// Whether an option — with its value, when the token carried one — is one this package can
    /// let through. Every refusal names the spelling held in this class's own table rather than the
    /// one that was read, so what reaches a diagnostic is a constant of this file.
    /// </summary>
    private static bool JudgeOption(
        string name,
        string? inlineValue,
        Func<string, string?> resolveOwnedVariable,
        out DocumentDBRuntimeArgumentFinding finding)
    {
        if (inlineValue is not null &&
            s_valuelessOptions.TryGetValue(name, out var declined) &&
            declined.Contains(inlineValue))
        {
            finding = DocumentDBRuntimeArgumentFinding.Harmless;
            return true;
        }

        if (s_storageOptions.TryGetValue(name, out var storage))
        {
            finding = new(DocumentDBRuntimeArgumentVerdict.Storage, storage, null);
            return false;
        }

        if (s_entrypointOptions.TryGetValue(name, out var entrypoint))
        {
            finding = new(DocumentDBRuntimeArgumentVerdict.Entrypoint, entrypoint, null);
            return false;
        }

        if (s_imageOptions.TryGetValue(name, out var image))
        {
            finding = new(DocumentDBRuntimeArgumentVerdict.Image, image, null);
            return false;
        }

        if (s_unscopedEnvironmentOptions.TryGetValue(name, out var unscoped))
        {
            finding = new(DocumentDBRuntimeArgumentVerdict.Environment, unscoped, null);
            return false;
        }

        if (s_scopedEnvironmentOptions.TryGetValue(name, out var scoped) &&
            inlineValue is not null &&
            !JudgeEnvironmentAssignment(scoped, inlineValue, resolveOwnedVariable, out finding))
        {
            return false;
        }

        finding = DocumentDBRuntimeArgumentFinding.Harmless;
        return true;
    }

    /// <summary>
    /// Whether the operand of a value-taking option is one this package can let through. Only the
    /// operand of a scoped environment option is read at all: every other operand is a port, a
    /// label, a memory limit or a password, none of which this package has any business inspecting
    /// — and none of which it reports either way.
    /// </summary>
    private static bool JudgeOperand(
        string option,
        object operand,
        Func<string, string?> resolveOwnedVariable,
        out DocumentDBRuntimeArgumentFinding finding)
    {
        if (!s_scopedEnvironmentOptions.TryGetValue(option, out var scoped))
        {
            finding = DocumentDBRuntimeArgumentFinding.Harmless;
            return true;
        }

        if (operand is not string assignment)
        {
            // The name of the variable is inside a value that is not known yet, so whether it is
            // one of this package's cannot be decided without resolving it.
            finding = new(DocumentDBRuntimeArgumentVerdict.Undecidable, scoped, null);
            return false;
        }

        return JudgeEnvironmentAssignment(scoped, assignment, resolveOwnedVariable, out finding);
    }

    /// <summary>
    /// Whether an environment assignment names a variable this package owns. A bare name with no
    /// <c>=</c> is the runtime's "import this one from the host environment" for <c>--env</c>, and
    /// the whole of the argument for <c>--unsetenv</c>; either way it sets or clears the variable
    /// just as surely.
    /// </summary>
    private static bool JudgeEnvironmentAssignment(
        string option,
        string assignment,
        Func<string, string?> resolveOwnedVariable,
        out DocumentDBRuntimeArgumentFinding finding)
    {
        var separator = assignment.IndexOf('=', StringComparison.Ordinal);
        var variable = separator < 0 ? assignment : assignment[..separator];

        // The owner's own spelling, not the one that was read: this is the only part of the
        // argument that reaches a diagnostic, and it has to be a name this package chose.
        if (resolveOwnedVariable(variable) is { } owned)
        {
            finding = new(DocumentDBRuntimeArgumentVerdict.Environment, option, owned);
            return false;
        }

        finding = DocumentDBRuntimeArgumentFinding.Harmless;
        return true;
    }
}
