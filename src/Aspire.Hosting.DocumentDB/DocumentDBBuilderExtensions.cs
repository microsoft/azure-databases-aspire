// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECONTAINERSHELLEXECUTION001 // Guard Aspire's experimental shell argument rewrite.

using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.DocumentDB;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding DocumentDB resources to an <see cref="IDistributedApplicationBuilder"/>.
/// </summary>
public static class DocumentDBBuilderExtensions
{
    private sealed class OpenTelemetryGatewayConfigurationAnnotation : IResourceAnnotation
    {
        public bool ServiceNameConfigured { get; set; }
        public bool ServiceVersionConfigured { get; set; }
        public bool EntrypointOwned { get; set; }
    }

    private enum GatewayConfigurationRequirement
    {
        NotConfigured,
        NotApplicable,
        Required,
    }

    /// <summary>
    /// The callbacks this package is allowed to own on a resource, and the ordered steps each one
    /// runs. See <see cref="EnsureTerminalGuard"/> for the contract.
    /// </summary>
    private sealed class TerminalGuardAnnotation : IResourceAnnotation
    {
        private readonly List<(int Rank, int Sequence, Action<TerminalCommandLineState> Step)> _commandLineSteps = [];
        private readonly List<Action<TerminalCommandLineState>> _commandLineValidations = [];
        private int _sequence;

        /// <summary>
        /// The command-line callback whose position in the annotation collection is what makes this
        /// guard terminal. The instance never changes, so Aspire's per-callback result cache
        /// survives every move.
        /// </summary>
        public CommandLineArgsCallbackAnnotation CommandLineCallback { get; set; } = null!;

        /// <summary>
        /// The terminal container-runtime-arguments callback. Aspire never caches these, so it is
        /// guaranteed to run on every container creation: it verifies the command seal, then
        /// resolves and validates the completed runtime-argument list.
        /// </summary>
        public ContainerRuntimeArgsCallbackAnnotation RuntimeCheckpoint { get; set; } = null!;

        /// <summary>
        /// The manifest publishing callback. It is the publish counterpart of
        /// <see cref="RuntimeCheckpoint"/>: it verifies the cached command while the resource is
        /// actually being serialized, then hands writing on to the callback it displaced.
        /// </summary>
        public ManifestPublishingCallbackAnnotation ManifestCheckpoint { get; set; } = null!;

        /// <summary>
        /// What the resource looked like when the command-line callback last produced a result, or
        /// <see langword="null"/> if it has not run yet.
        /// </summary>
        public TerminalConfigurationSeal? Seal { get; set; }

        public void AddCommandLineStep(int rank, Action<TerminalCommandLineState> step) =>
            _commandLineSteps.Add((rank, _sequence++, step));

        public void AddCommandLineValidation(Action<TerminalCommandLineState> validation) =>
            _commandLineValidations.Add(validation);

        public TerminalCommandLineState RunCommandLine(CommandLineArgsCallbackContext context)
        {
            var state = new TerminalCommandLineState(context);

            foreach (var (_, _, step) in _commandLineSteps.OrderBy(step => step.Rank).ThenBy(step => step.Sequence))
            {
                step(state);
            }

            foreach (var validation in _commandLineValidations)
            {
                validation(state);
            }

            return state;
        }

    }

    /// <summary>
    /// Everything the container's command depends on, as it stood when the command-line callback
    /// produced the result Aspire cached.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Aspire evaluates each callback annotation at most once per run and reuses the recorded
    /// result afterwards, so a callback that validated a configuration cannot notice the
    /// configuration changing behind it. Worse, the gatherer takes the <em>last</em> annotation's
    /// recorded result as the final argument list, so an annotation appended after a first
    /// evaluation does not merely reorder the command line - it replaces it, because the earlier
    /// annotations no longer write into the shared list at all.
    /// </para>
    /// <para>
    /// The seal is what makes that detectable. It is compared at the checkpoints that Aspire never
    /// caches, so the answer the wrapper gave is either still the answer or the resource is failed.
    /// </para>
    /// </remarks>
    private sealed record TerminalConfigurationSeal(
        System.Collections.Immutable.ImmutableArray<CommandLineArgsCallbackAnnotation> CommandLineCallbacks,
        System.Collections.Immutable.ImmutableArray<EnvironmentCallbackAnnotation> EnvironmentCallbacks,
        System.Collections.Immutable.ImmutableArray<ContainerRuntimeArgsCallbackAnnotation> RuntimeCallbacks,
        string? Entrypoint,
        bool ShellExecutionEnabled,
        DocumentDBEffectiveImage Image,
        TerminalCommandSeal Command);

    /// <summary>
    /// The fixed, non-secret-bearing part of the command result the terminal callback returned to
    /// Aspire's immutable cache.
    /// </summary>
    private sealed record TerminalCommandSeal(
        GatewayConfigurationRequirement GatewayRequirement,
        string? WrapperScript,
        string? ShellOption,
        bool ScriptIsSecondArgument,
        string? Delimiter,
        bool HasDuplicateWrapperScript);

    /// <summary>
    /// What one evaluation of the terminal command-line guard produced. Scoped to the evaluation
    /// rather than stored on an annotation, so a validation always judges the arguments its own
    /// steps built.
    /// </summary>
    private sealed class TerminalCommandLineState(CommandLineArgsCallbackContext context)
    {
        public CommandLineArgsCallbackContext Context { get; } = context;

        public IList<object> Args => Context.Args;

        /// <summary>
        /// The exact OpenTelemetry wrapper script instance this evaluation inserted, or
        /// <see langword="null"/> when the wrapper was not applied.
        /// </summary>
        public string? WrapperScript { get; set; }
    }

    /// <summary>
    /// Rank of the OpenTelemetry gateway wrapper among the terminal guard's steps. It is the
    /// highest-ranked step in this package on purpose: the wrapper turns the argument list into a
    /// <c>/bin/bash -c</c> command line, so every step that reads arguments as the container
    /// entrypoint's own <c>--option value</c> grammar has to have run first.
    /// </summary>
    private const int TerminalCommandLineOpenTelemetryWrapperRank = 100;
    private const string ManifestPublishingPipelineStepName = "publish-manifest";
    private const string TerminalManifestCheckpointPipelineStepName =
        "documentdb-terminal-manifest-checkpoint";

    // default internal port is 10260.
    private const int DefaultContainerPort = 10260;
    // default PostgreSQL coordinator port inside the documentdb-local container.
    private const int DefaultPostgresContainerPort = 9712;
    private const string DefaultHealthCheckDatabaseName = "admin";
    private static readonly Version FirstGatewayTelemetryConfigurationVersion = new(0, 116, 0);

    private const string UserEnvVarName = "USERNAME";
    private const string PasswordEnvVarName = "PASSWORD";
    private const string LogLevelEnvVarName = "LOG_LEVEL";
    private const string InitDataPathEnvVarName = "INIT_DATA_PATH";
    private const string SkipInitDataEnvVarName = "SKIP_INIT_DATA";
    private const string CertPathEnvVarName = "CERT_PATH";
    private const string KeyFileEnvVarName = "KEY_FILE";
    private const string EnableTelemetryEnvVarName = "ENABLE_TELEMETRY";
    private const string OtelMetricsEnabledEnvVarName = "OTEL_METRICS_ENABLED";
    private const string OtelExporterOtlpMetricsEndpointEnvVarName = "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT";
    private const string OtelExporterOtlpMetricsTimeoutEnvVarName = "OTEL_EXPORTER_OTLP_METRICS_TIMEOUT";
    private const string OtelMetricExportIntervalEnvVarName = "OTEL_METRIC_EXPORT_INTERVAL";
    private const string OtelServiceNameEnvVarName = "OTEL_SERVICE_NAME";
    private const string OtelServiceVersionEnvVarName = "OTEL_SERVICE_VERSION";
    private const string OwnerEnvVarName = "OWNER";
    private const string DataPathEnvVarName = "DATA_PATH";
    private const string DisableExtendedRumEnvVarName = "DISABLE_EXTENDED_RUM";
    private const string CreateUserEnvVarName = "CREATE_USER";
    private const string AllowExternalConnectionsEnvVarName = "ALLOW_EXTERNAL_CONNECTIONS";

    private const string DefaultMountedDataPath = "/data";
    private const string InitDataMountPath = "/init_doc_db.d";
    private const string DefaultGatewayHome = "/home/documentdb/gateway";
    private const string PackagedGatewayConfigurationDirectory = "/etc/documentdb/gateway";
    private const string PackagedLayoutProbeScript = "/usr/share/documentdb/scripts/start_oss_server.sh";
    private const string PackagedLayoutProbeUtils = "/usr/share/documentdb/scripts/utils.sh";
    private const string GatewayEntrypointScriptPath = "/home/documentdb/gateway/scripts/emulator_entrypoint.sh";
    private const string GatewayConfigurationShell = "/bin/bash";
    private const string GatewayConfigurationShellCommandOption = "-c";
    private const string GatewayConfigurationShellArgumentZero = "--";
    private const string GatewayValueTakingOptionsShellPattern =
        "--allow-external-connections|--cert-path|--create-user|--documentdb-port|--enable-telemetry|" +
        "--init-data|--init-data-path|--key-file|--log-level|--owner|--password|--pg-port|--start-pg|" +
        "--tlsMode|--toast-compression|--username";

    private enum ContainerRuntimeOptionValueArity
    {
        None,
        Required,
    }

    private enum ContainerRuntimeOptionEffect
    {
        None,
        Storage,
        Entrypoint,
        ImageOrRootfs,
        Environment,
        EnvironmentImport,
    }

    private readonly record struct ContainerRuntimeOption(
        string LongName,
        char? ShortName,
        ContainerRuntimeOptionValueArity ValueArity,
        ContainerRuntimeOptionEffect Effect = ContainerRuntimeOptionEffect.None);

    /// <summary>
    /// The Docker and Podman <c>run</c> option union: every required-value option, plus value-less
    /// options needed for compact short grammar or for a safety decision. Keeping arity and effect
    /// in one typed table prevents a value such as <c>--mount</c>, supplied to an unrelated option,
    /// from being mistaken for another option.
    /// </summary>
    private static readonly ContainerRuntimeOption[] s_containerRuntimeOptions =
    [
        new("add-host", null, ContainerRuntimeOptionValueArity.Required),
        new("annotation", null, ContainerRuntimeOptionValueArity.Required),
        new("arch", null, ContainerRuntimeOptionValueArity.Required),
        new("attach", 'a', ContainerRuntimeOptionValueArity.Required),
        new("authfile", null, ContainerRuntimeOptionValueArity.Required),
        new("blkio-weight", null, ContainerRuntimeOptionValueArity.Required),
        new("blkio-weight-device", null, ContainerRuntimeOptionValueArity.Required),
        new("cap-add", null, ContainerRuntimeOptionValueArity.Required),
        new("cap-drop", null, ContainerRuntimeOptionValueArity.Required),
        new("cert-dir", null, ContainerRuntimeOptionValueArity.Required),
        new("cgroup-conf", null, ContainerRuntimeOptionValueArity.Required),
        new("cgroup-parent", null, ContainerRuntimeOptionValueArity.Required),
        new("cgroupns", null, ContainerRuntimeOptionValueArity.Required),
        new("cgroups", null, ContainerRuntimeOptionValueArity.Required),
        new("chrootdirs", null, ContainerRuntimeOptionValueArity.Required, ContainerRuntimeOptionEffect.Storage),
        new("cidfile", null, ContainerRuntimeOptionValueArity.Required),
        new("conmon-pidfile", null, ContainerRuntimeOptionValueArity.Required),
        new("cpu-period", null, ContainerRuntimeOptionValueArity.Required),
        new("cpu-quota", null, ContainerRuntimeOptionValueArity.Required),
        new("cpu-rt-period", null, ContainerRuntimeOptionValueArity.Required),
        new("cpu-rt-runtime", null, ContainerRuntimeOptionValueArity.Required),
        new("cpu-shares", 'c', ContainerRuntimeOptionValueArity.Required),
        new("cpus", null, ContainerRuntimeOptionValueArity.Required),
        new("cpuset-cpus", null, ContainerRuntimeOptionValueArity.Required),
        new("cpuset-mems", null, ContainerRuntimeOptionValueArity.Required),
        new("creds", null, ContainerRuntimeOptionValueArity.Required),
        new("decryption-key", null, ContainerRuntimeOptionValueArity.Required),
        new("detach", 'd', ContainerRuntimeOptionValueArity.None),
        new("detach-keys", null, ContainerRuntimeOptionValueArity.Required),
        new("device", null, ContainerRuntimeOptionValueArity.Required),
        new("device-cgroup-rule", null, ContainerRuntimeOptionValueArity.Required),
        new("device-read-bps", null, ContainerRuntimeOptionValueArity.Required),
        new("device-read-iops", null, ContainerRuntimeOptionValueArity.Required),
        new("device-write-bps", null, ContainerRuntimeOptionValueArity.Required),
        new("device-write-iops", null, ContainerRuntimeOptionValueArity.Required),
        new("disable-content-trust", null, ContainerRuntimeOptionValueArity.None),
        new("dns", null, ContainerRuntimeOptionValueArity.Required),
        new("dns-option", null, ContainerRuntimeOptionValueArity.Required),
        new("dns-search", null, ContainerRuntimeOptionValueArity.Required),
        new("domainname", null, ContainerRuntimeOptionValueArity.Required),
        new("entrypoint", null, ContainerRuntimeOptionValueArity.Required, ContainerRuntimeOptionEffect.Entrypoint),
        new("env", 'e', ContainerRuntimeOptionValueArity.Required, ContainerRuntimeOptionEffect.Environment),
        new("env-file", null, ContainerRuntimeOptionValueArity.Required, ContainerRuntimeOptionEffect.EnvironmentImport),
        new("env-host", null, ContainerRuntimeOptionValueArity.None, ContainerRuntimeOptionEffect.EnvironmentImport),
        new("env-merge", null, ContainerRuntimeOptionValueArity.Required, ContainerRuntimeOptionEffect.Environment),
        new("expose", null, ContainerRuntimeOptionValueArity.Required),
        new("gidmap", null, ContainerRuntimeOptionValueArity.Required),
        new("gpus", null, ContainerRuntimeOptionValueArity.Required),
        new("group-add", null, ContainerRuntimeOptionValueArity.Required),
        new("group-entry", null, ContainerRuntimeOptionValueArity.Required),
        new("health-cmd", null, ContainerRuntimeOptionValueArity.Required),
        new("health-interval", null, ContainerRuntimeOptionValueArity.Required),
        new("health-log-destination", null, ContainerRuntimeOptionValueArity.Required),
        new("health-max-log-count", null, ContainerRuntimeOptionValueArity.Required),
        new("health-max-log-size", null, ContainerRuntimeOptionValueArity.Required),
        new("health-on-failure", null, ContainerRuntimeOptionValueArity.Required),
        new("health-retries", null, ContainerRuntimeOptionValueArity.Required),
        new("health-start-interval", null, ContainerRuntimeOptionValueArity.Required),
        new("health-start-period", null, ContainerRuntimeOptionValueArity.Required),
        new("health-startup-cmd", null, ContainerRuntimeOptionValueArity.Required),
        new("health-startup-interval", null, ContainerRuntimeOptionValueArity.Required),
        new("health-startup-retries", null, ContainerRuntimeOptionValueArity.Required),
        new("health-startup-success", null, ContainerRuntimeOptionValueArity.Required),
        new("health-startup-timeout", null, ContainerRuntimeOptionValueArity.Required),
        new("health-timeout", null, ContainerRuntimeOptionValueArity.Required),
        new("help", null, ContainerRuntimeOptionValueArity.None),
        new("hostname", 'h', ContainerRuntimeOptionValueArity.Required),
        new("hosts-file", null, ContainerRuntimeOptionValueArity.Required),
        new("hostuser", null, ContainerRuntimeOptionValueArity.Required),
        new("http-proxy", null, ContainerRuntimeOptionValueArity.None),
        new("image-volume", null, ContainerRuntimeOptionValueArity.Required, ContainerRuntimeOptionEffect.Storage),
        new("init", null, ContainerRuntimeOptionValueArity.None),
        new("init-path", null, ContainerRuntimeOptionValueArity.Required, ContainerRuntimeOptionEffect.Storage),
        new("interactive", 'i', ContainerRuntimeOptionValueArity.None),
        new("ip", null, ContainerRuntimeOptionValueArity.Required),
        new("ip6", null, ContainerRuntimeOptionValueArity.Required),
        new("ipc", null, ContainerRuntimeOptionValueArity.Required, ContainerRuntimeOptionEffect.Storage),
        new("isolation", null, ContainerRuntimeOptionValueArity.Required),
        new("label", 'l', ContainerRuntimeOptionValueArity.Required),
        new("label-file", null, ContainerRuntimeOptionValueArity.Required),
        new("link", null, ContainerRuntimeOptionValueArity.Required),
        new("link-local-ip", null, ContainerRuntimeOptionValueArity.Required),
        new("log-driver", null, ContainerRuntimeOptionValueArity.Required),
        new("log-opt", null, ContainerRuntimeOptionValueArity.Required),
        new("mac-address", null, ContainerRuntimeOptionValueArity.Required),
        new("memory", 'm', ContainerRuntimeOptionValueArity.Required),
        new("memory-reservation", null, ContainerRuntimeOptionValueArity.Required),
        new("memory-swap", null, ContainerRuntimeOptionValueArity.Required),
        new("memory-swappiness", null, ContainerRuntimeOptionValueArity.Required),
        new("mount", null, ContainerRuntimeOptionValueArity.Required, ContainerRuntimeOptionEffect.Storage),
        new("name", null, ContainerRuntimeOptionValueArity.Required),
        new("net", null, ContainerRuntimeOptionValueArity.Required),
        new("network", null, ContainerRuntimeOptionValueArity.Required),
        new("network-alias", null, ContainerRuntimeOptionValueArity.Required),
        new("no-healthcheck", null, ContainerRuntimeOptionValueArity.None),
        new("no-hostname", null, ContainerRuntimeOptionValueArity.None),
        new("no-hosts", null, ContainerRuntimeOptionValueArity.None),
        new("oom-kill-disable", null, ContainerRuntimeOptionValueArity.None),
        new("oom-score-adj", null, ContainerRuntimeOptionValueArity.Required),
        new("os", null, ContainerRuntimeOptionValueArity.Required),
        new("passwd", null, ContainerRuntimeOptionValueArity.None),
        new("passwd-entry", null, ContainerRuntimeOptionValueArity.Required),
        new("personality", null, ContainerRuntimeOptionValueArity.Required),
        new("pid", null, ContainerRuntimeOptionValueArity.Required),
        new("pidfile", null, ContainerRuntimeOptionValueArity.Required),
        new("pids-limit", null, ContainerRuntimeOptionValueArity.Required),
        new("platform", null, ContainerRuntimeOptionValueArity.Required),
        new("pod", null, ContainerRuntimeOptionValueArity.Required, ContainerRuntimeOptionEffect.Storage),
        new("pod-id-file", null, ContainerRuntimeOptionValueArity.Required, ContainerRuntimeOptionEffect.Storage),
        new("preserve-fd", null, ContainerRuntimeOptionValueArity.Required),
        new("preserve-fds", null, ContainerRuntimeOptionValueArity.Required),
        new("privileged", null, ContainerRuntimeOptionValueArity.None),
        new("publish", 'p', ContainerRuntimeOptionValueArity.Required),
        new("publish-all", 'P', ContainerRuntimeOptionValueArity.None),
        new("pull", null, ContainerRuntimeOptionValueArity.Required),
        new("quiet", 'q', ContainerRuntimeOptionValueArity.None),
        new("rdt-class", null, ContainerRuntimeOptionValueArity.Required),
        new("read-only", null, ContainerRuntimeOptionValueArity.None, ContainerRuntimeOptionEffect.Storage),
        new("read-only-tmpfs", null, ContainerRuntimeOptionValueArity.None, ContainerRuntimeOptionEffect.Storage),
        new("replace", null, ContainerRuntimeOptionValueArity.None),
        new("requires", null, ContainerRuntimeOptionValueArity.Required),
        new("restart", null, ContainerRuntimeOptionValueArity.Required),
        new("retry", null, ContainerRuntimeOptionValueArity.Required),
        new("retry-delay", null, ContainerRuntimeOptionValueArity.Required),
        new("rm", null, ContainerRuntimeOptionValueArity.None),
        new("rmi", null, ContainerRuntimeOptionValueArity.None),
        new("rootfs", null, ContainerRuntimeOptionValueArity.None, ContainerRuntimeOptionEffect.ImageOrRootfs),
        new("runtime", null, ContainerRuntimeOptionValueArity.Required),
        new("sdnotify", null, ContainerRuntimeOptionValueArity.Required),
        new("seccomp-policy", null, ContainerRuntimeOptionValueArity.Required),
        new("secret", null, ContainerRuntimeOptionValueArity.Required, ContainerRuntimeOptionEffect.Storage),
        new("security-opt", null, ContainerRuntimeOptionValueArity.Required),
        new("shm-size", null, ContainerRuntimeOptionValueArity.Required),
        new("shm-size-systemd", null, ContainerRuntimeOptionValueArity.Required),
        new("sig-proxy", null, ContainerRuntimeOptionValueArity.None),
        new("stop-signal", null, ContainerRuntimeOptionValueArity.Required),
        new("stop-timeout", null, ContainerRuntimeOptionValueArity.Required),
        new("storage-opt", null, ContainerRuntimeOptionValueArity.Required, ContainerRuntimeOptionEffect.Storage),
        new("subgidname", null, ContainerRuntimeOptionValueArity.Required),
        new("subuidname", null, ContainerRuntimeOptionValueArity.Required),
        new("sysctl", null, ContainerRuntimeOptionValueArity.Required),
        new("systemd", null, ContainerRuntimeOptionValueArity.None, ContainerRuntimeOptionEffect.Storage),
        new("timeout", null, ContainerRuntimeOptionValueArity.Required),
        new("tls-verify", null, ContainerRuntimeOptionValueArity.None),
        new("tmpfs", null, ContainerRuntimeOptionValueArity.Required, ContainerRuntimeOptionEffect.Storage),
        new("tty", 't', ContainerRuntimeOptionValueArity.None),
        new("tz", null, ContainerRuntimeOptionValueArity.Required),
        new("uidmap", null, ContainerRuntimeOptionValueArity.Required),
        new("ulimit", null, ContainerRuntimeOptionValueArity.Required),
        new("umask", null, ContainerRuntimeOptionValueArity.Required),
        new("unsetenv", null, ContainerRuntimeOptionValueArity.Required, ContainerRuntimeOptionEffect.Environment),
        new("unsetenv-all", null, ContainerRuntimeOptionValueArity.None, ContainerRuntimeOptionEffect.EnvironmentImport),
        new("use-api-socket", null, ContainerRuntimeOptionValueArity.None, ContainerRuntimeOptionEffect.Storage),
        new("user", 'u', ContainerRuntimeOptionValueArity.Required),
        new("userns", null, ContainerRuntimeOptionValueArity.Required),
        new("uts", null, ContainerRuntimeOptionValueArity.Required),
        new("variant", null, ContainerRuntimeOptionValueArity.Required),
        new("volume", 'v', ContainerRuntimeOptionValueArity.Required, ContainerRuntimeOptionEffect.Storage),
        new("volume-driver", null, ContainerRuntimeOptionValueArity.Required, ContainerRuntimeOptionEffect.Storage),
        new("volumes-from", null, ContainerRuntimeOptionValueArity.Required, ContainerRuntimeOptionEffect.Storage),
        new("workdir", 'w', ContainerRuntimeOptionValueArity.Required),
    ];

    private static readonly IReadOnlyDictionary<string, ContainerRuntimeOption> s_longContainerRuntimeOptions =
        s_containerRuntimeOptions.ToDictionary(option => option.LongName, StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<char, ContainerRuntimeOption> s_shortContainerRuntimeOptions =
        s_containerRuntimeOptions
            .Where(option => option.ShortName is not null)
            .ToDictionary(option => option.ShortName!.Value);

    private static readonly HashSet<string> s_openTelemetryRuntimeProtectedEnvironmentVariables =
        new(StringComparer.Ordinal)
        {
            "CONFIG_DIR",
            "GATEWAY_HOME",
            DataPathEnvVarName,
            EnableTelemetryEnvVarName,
            OtelMetricsEnabledEnvVarName,
            OtelExporterOtlpMetricsEndpointEnvVarName,
            OtelExporterOtlpMetricsTimeoutEnvVarName,
            OtelMetricExportIntervalEnvVarName,
            OtelServiceNameEnvVarName,
            OtelServiceVersionEnvVarName,
            "OTEL_EXPORTER_OTLP_ENDPOINT",
            "OTEL_EXPORTER_OTLP_TIMEOUT",
            "OTEL_RESOURCE_ATTRIBUTES",
        };

    private sealed class ContainerRuntimeArgumentParser(DocumentDBServerResource resource)
    {
        private ContainerRuntimeOption? _pendingOption;
        private bool _endOfOptions;

        public void Parse(string argument)
        {
            if (_pendingOption is { } pending)
            {
                _pendingOption = null;
                ValidateValue(pending, argument);
                return;
            }

            if (_endOfOptions)
            {
                throw UnsafeContainerRuntimeOperand(resource);
            }

            if (string.Equals(argument, "--", StringComparison.Ordinal))
            {
                _endOfOptions = true;
                return;
            }

            if (argument.StartsWith("--", StringComparison.Ordinal) && argument.Length > 2)
            {
                ParseLongOption(argument);
                return;
            }

            if (argument.Length > 1 && argument[0] == '-' && argument[1] != '-')
            {
                ParseShortOptions(argument);
                return;
            }

            throw UnsafeContainerRuntimeOperand(resource);
        }

        public void Complete()
        {
            if (_pendingOption is { } pending)
            {
                throw MissingContainerRuntimeOptionValue(resource, pending);
            }
        }

        private void ParseLongOption(string argument)
        {
            var equals = argument.IndexOf('=', 2);
            var name = equals < 0 ? argument[2..] : argument[2..equals];

            if (!s_longContainerRuntimeOptions.TryGetValue(name, out var option))
            {
                throw UnsupportedContainerRuntimeOption(resource);
            }

            var hasAttachedValue = equals >= 0;
            var attachedValue = hasAttachedValue ? argument[(equals + 1)..] : null;
            Apply(option, hasAttachedValue, attachedValue);
        }

        private void ParseShortOptions(string argument)
        {
            for (var index = 1; index < argument.Length; index++)
            {
                if (!s_shortContainerRuntimeOptions.TryGetValue(argument[index], out var option))
                {
                    throw UnsupportedContainerRuntimeOption(resource);
                }

                if (option.Effect != ContainerRuntimeOptionEffect.None &&
                    option.Effect != ContainerRuntimeOptionEffect.Environment)
                {
                    Reject(option);
                }

                if (option.ValueArity == ContainerRuntimeOptionValueArity.None)
                {
                    continue;
                }

                var attachedValue = argument[(index + 1)..];
                var hasAttachedValue = attachedValue.Length > 0;
                if (attachedValue.StartsWith('='))
                {
                    attachedValue = attachedValue[1..];
                    hasAttachedValue = true;
                }

                Apply(option, hasAttachedValue, attachedValue);
                return;
            }
        }

        private void Apply(ContainerRuntimeOption option, bool hasAttachedValue, string? attachedValue)
        {
            if (option.Effect != ContainerRuntimeOptionEffect.None &&
                option.Effect != ContainerRuntimeOptionEffect.Environment)
            {
                Reject(option);
            }

            if (option.ValueArity == ContainerRuntimeOptionValueArity.None)
            {
                return;
            }

            if (hasAttachedValue)
            {
                ValidateValue(option, attachedValue!);
            }
            else
            {
                _pendingOption = option;
            }
        }

        private void ValidateValue(ContainerRuntimeOption option, string value)
        {
            if (option.Effect != ContainerRuntimeOptionEffect.Environment)
            {
                return;
            }

            var equals = value.IndexOf('=');
            var name = equals < 0 ? value : value[..equals];

            if (s_openTelemetryRuntimeProtectedEnvironmentVariables.Contains(name))
            {
                throw UnsafeContainerRuntimeEnvironment(resource, "it overrides protected container configuration");
            }
        }

        private void Reject(ContainerRuntimeOption option)
        {
            var displayName = "--" + option.LongName;

            throw option.Effect switch
            {
                ContainerRuntimeOptionEffect.Storage => UnsafeContainerRuntimeStorage(resource, displayName),
                ContainerRuntimeOptionEffect.Entrypoint => UnsafeContainerRuntimeEntrypoint(resource),
                ContainerRuntimeOptionEffect.ImageOrRootfs => UnsafeContainerRuntimeImageOrRootfs(resource, displayName),
                ContainerRuntimeOptionEffect.EnvironmentImport =>
                    UnsafeContainerRuntimeEnvironment(resource, "the option imports or clears environment values outside the model"),
                _ => throw new InvalidOperationException(),
            };
        }
    }

    private static InvalidOperationException UnsafeContainerRuntimeStorage(
        DocumentDBServerResource resource,
        string option) =>
        new(
            $"DocumentDB resource '{resource.Name}' adds the storage-changing container runtime " +
            $"option '{option}' while the WithOpenTelemetryMetrics() compatibility wrapper is " +
            $"required. That option can add or replace mounts outside the resource model, so the " +
            $"wrapper cannot prove that its temporary configuration stays off DATA_PATH storage. " +
            $"Use the modeled mount and environment APIs where applicable, or remove the raw " +
            $"runtime option.");

    private static InvalidOperationException UnsafeContainerRuntimeEntrypoint(
        DocumentDBServerResource resource) =>
        new(
            $"DocumentDB resource '{resource.Name}' adds the container runtime option " +
            $"'--entrypoint' while the WithOpenTelemetryMetrics() compatibility wrapper is " +
            $"required. That raw option can replace the verified '/bin/bash' entrypoint after the " +
            $"resource model has been sealed. Configure no custom entrypoint, or drop " +
            $"WithOpenTelemetryMetrics() and configure telemetry from your own entrypoint.");

    private static InvalidOperationException UnsafeContainerRuntimeImageOrRootfs(
        DocumentDBServerResource resource,
        string option) =>
        new(
            $"DocumentDB resource '{resource.Name}' adds the container runtime option '{option}' " +
            $"while the WithOpenTelemetryMetrics() compatibility wrapper is required. That option " +
            $"can replace the image or root filesystem whose entrypoint and telemetry layout the " +
            $"wrapper validated. Select the image through the resource model instead.");

    private static InvalidOperationException UnsafeContainerRuntimeOperand(
        DocumentDBServerResource resource) =>
        new(
            $"DocumentDB resource '{resource.Name}' adds a positional container runtime operand " +
            $"while the WithOpenTelemetryMetrics() compatibility wrapper is required. A positional " +
            $"operand in runtime options can replace the model-selected image or root filesystem. " +
            $"The value is intentionally omitted because runtime operands may contain secrets. " +
            $"Select the image through the resource model and pass container arguments with " +
            $"WithArgs(...).");

    private static InvalidOperationException UnsupportedContainerRuntimeOption(
        DocumentDBServerResource resource) =>
        new(
            $"DocumentDB resource '{resource.Name}' adds a container runtime option outside the " +
            $"Docker and Podman run grammar understood by the WithOpenTelemetryMetrics() safety " +
            $"check. The option and its value are intentionally omitted because a deferred runtime " +
            $"argument may contain secrets. Use a supported modeled API or remove the option.");

    private static InvalidOperationException MissingContainerRuntimeOptionValue(
        DocumentDBServerResource resource,
        ContainerRuntimeOption option) =>
        new(
            $"DocumentDB resource '{resource.Name}' does not provide the required value for " +
            $"container runtime option '--{option.LongName}'. The value is intentionally omitted " +
            $"because runtime option values may contain secrets.");

    private static InvalidOperationException UnsafeContainerRuntimeEnvironment(
        DocumentDBServerResource resource,
        string reason) =>
        new(
            $"DocumentDB resource '{resource.Name}' adds a raw container runtime environment " +
            $"override while the WithOpenTelemetryMetrics() compatibility wrapper is required, " +
            $"but {reason}. The override could invalidate the telemetry command or its DATA_PATH " +
            $"isolation after the resource model has been sealed. Use WithEnvironment(...) so " +
            $"the value is part of the validated model.");

    private static InvalidOperationException UnresolvedContainerRuntimeArgument(
        DocumentDBServerResource resource) =>
        new(
            $"DocumentDB resource '{resource.Name}' has a deferred container runtime argument that " +
            $"could not be resolved for the WithOpenTelemetryMetrics() safety check. The argument, " +
            $"its partial value and the resolution error are intentionally omitted because they " +
            $"may contain credentials or other secrets.");

    /// <summary>
    /// Resolves the completed runtime-argument list once, validates the exact strings Docker or
    /// Podman will receive, and replaces deferred values with those strings so Aspire does not
    /// resolve them a second time.
    /// </summary>
    private static async Task ValidateOpenTelemetryContainerRuntimeArgumentsAsync(
        DocumentDBServerResource resource,
        ContainerRuntimeArgsCallbackContext context,
        DistributedApplicationExecutionContext executionContext)
    {
        if (ResolveOpenTelemetryGatewayConfigurationRequirement(resource) !=
            GatewayConfigurationRequirement.Required)
        {
            return;
        }

        var parser = new ContainerRuntimeArgumentParser(resource);
        var resolvedArguments = new List<object>(context.Args.Count);
        var valueProviderContext = new ValueProviderContext
        {
            Caller = resource,
            ExecutionContext = executionContext,
        };

        foreach (var argument in context.Args.ToArray())
        {
            string? resolved;

            try
            {
                resolved = argument switch
                {
                    string value => value,
                    IValueProvider provider => await provider
                        .GetValueAsync(valueProviderContext, context.CancellationToken)
                        .ConfigureAwait(false),
                    null => null,
                    _ => argument.ToString(),
                };
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                throw UnresolvedContainerRuntimeArgument(resource);
            }

            if (resolved is null)
            {
                continue;
            }

            parser.Parse(resolved);
            resolvedArguments.Add(resolved);
        }

        parser.Complete();

        context.Args.Clear();
        foreach (var argument in resolvedArguments)
        {
            context.Args.Add(argument);
        }
    }

    /// <summary>
    /// Builds the wrapper script that makes the OpenTelemetry environment variables this package
    /// writes authoritative over the gateway's <c>SetupConfiguration.json</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The configuration directory is resolved exactly the way the image's own entrypoint resolves
    /// it: an explicit <c>CONFIG_DIR</c> first, then the packaged layout at
    /// <c>/etc/documentdb/gateway</c> when the scripts that layout is keyed on are present, then
    /// <c>$GATEWAY_HOME/pg_documentdb_gw</c> with the upstream <c>GATEWAY_HOME</c> default. Any
    /// other resolution would sanitize a file the gateway does not read.
    /// </para>
    /// <para>
    /// The scratch directory the sanitized copy is written to has to be outside <c>DATA_PATH</c>
    /// <em>and</em> outside the storage that backs it. Two container paths that do not contain one
    /// another can still be one directory — a bind mount of the same host directory at
    /// <c>/tmp</c> and at <c>/data</c>, or one named volume mounted twice — and a scratch
    /// directory created through the second window appears inside the data directory, which
    /// DocumentDB <c>0.116.0</c> refuses to initialise. The container path test stays in the
    /// script, because <c>DATA_PATH</c> can still be moved at runtime with <c>--data-path</c>; the
    /// backing test is decided here, where the mount table is known, and emitted as the exact set
    /// of data directories each candidate root cannot be used with. Named volumes can be compared
    /// exactly. Bind sources are resolved by the Docker daemon and may traverse unavailable
    /// symbolic links, so a bind-backed candidate is conservatively skipped for every bind-backed
    /// data path without consulting the local filesystem. Raw runtime mounts are rejected before
    /// container creation because they are absent from this table. See
    /// <see cref="BuildOpenTelemetryScratchRootAliases"/>.
    /// </para>
    /// <para>
    /// Single-line and brace-free on purpose. Publishers post-process container arguments: azd
    /// evaluates <c>{...}</c> in every argument as a manifest binding expression, so a shell
    /// <c>${VAR:-default}</c> is either passed through by luck or rejected outright, and a newline
    /// turns the rendered YAML scalar into a block scalar.
    /// </para>
    /// </remarks>
    private static string BuildOpenTelemetryGatewayConfigurationScript(
        IResource resource,
        OpenTelemetryGatewayConfigurationAnnotation configuration)
    {
        var aliases = BuildOpenTelemetryScratchRootAliases(resource);

        return
        "set -e; " +
        "c=\"$CONFIG_DIR\"; " +
        "if [ -z \"$c\" ]; then " +
            $"if [ -f \"{PackagedLayoutProbeScript}\" ] && [ -f \"{PackagedLayoutProbeUtils}\" ]; then " +
                $"c=\"{PackagedGatewayConfigurationDirectory}\"; " +
            "else " +
                "g=\"$GATEWAY_HOME\"; " +
                $"if [ -z \"$g\" ]; then g=\"{DefaultGatewayHome}\"; fi; " +
                "c=\"$g/pg_documentdb_gw\"; " +
            "fi; " +
        "fi; " +
        "s=\"$c/SetupConfiguration.json\"; " +
        "if [ ! -r \"$s\" ]; then echo \"aspire-documentdb -- gateway configuration $s is missing or unreadable\" >&2; exit 1; fi; " +
        "if ! command -v jq >/dev/null 2>&1; then echo \"aspire-documentdb -- jq is required to make the OpenTelemetry environment variables authoritative\" >&2; exit 1; fi; " +
        "if ! command -v realpath >/dev/null 2>&1; then echo \"aspire-documentdb -- realpath is required to keep the telemetry configuration outside DATA_PATH\" >&2; exit 1; fi; " +
        "d=\"$DATA_PATH\"; q=\"\"; " +
        "for a in \"$@\"; do " +
            "if [ \"$q\" = \"d\" ]; then d=\"$a\"; q=\"\"; continue; fi; " +
            "if [ \"$q\" = \"v\" ]; then q=\"\"; continue; fi; " +
            "case \"$a\" in " +
                "-d|--data-path) q=\"d\";; " +
                $"{GatewayValueTakingOptionsShellPattern}) q=\"v\";; " +
            "esac; " +
        "done; " +
        "if [ \"$q\" = \"d\" ]; then echo \"aspire-documentdb -- --data-path requires a value before telemetry configuration can be prepared\" >&2; exit 1; fi; " +
        $"if [ -z \"$d\" ]; then d=\"{DefaultMountedDataPath}\"; fi; " +
        "if ! d=\"$(realpath -m -- \"$d\" 2>/dev/null)\"; then echo \"aspire-documentdb -- DATA_PATH could not be canonicalized for the telemetry configuration\" >&2; exit 1; fi; " +
        "if [ \"$d\" = \"/\" ]; then echo \"aspire-documentdb -- no temporary directory can be safely separated from a root DATA_PATH\" >&2; exit 1; fi; " +
        "r=\"\"; " +
        (aliases.Length == 0 ? "" : "w=\"\"; ") +
        $"for y in {string.Join(' ', s_openTelemetryScratchRoots)}; do " +
            (aliases.Length == 0 ? "" : $"case \"$y|$d\" in {string.Join('|', aliases)}) w=1; continue;; esac; ") +
            "if ! x=\"$(realpath -m -- \"$y\" 2>/dev/null)\"; then continue; fi; " +
            "if [ ! -d \"$x\" ] || [ ! -w \"$x\" ]; then continue; fi; " +
            "case \"$x\" in \"$d\"|\"$d\"/*) continue;; esac; " +
            "case \"$d\" in \"$x\"|\"$x\"/*) continue;; esac; " +
            "r=\"$x\"; break; " +
        "done; " +
        (aliases.Length == 0
            ? "if [ -z \"$r\" ]; then echo \"aspire-documentdb -- no writable temporary directory is safely separated from DATA_PATH\" >&2; exit 1; fi; "
            : "if [ -z \"$r\" ]; then " +
                "if [ -n \"$w\" ]; then " +
                    "echo \"aspire-documentdb -- every temporary directory aliases DATA_PATH storage or cannot be proven independent of it, so the telemetry configuration cannot be kept out of it\" >&2; " +
                "else " +
                    "echo \"aspire-documentdb -- no writable temporary directory is safely separated from DATA_PATH\" >&2; " +
                "fi; " +
                "exit 1; " +
            "fi; ") +
        "if ! o=\"$(mktemp -d \"$r/aspire-documentdb-otel.XXXXXX\")\"; then echo \"aspire-documentdb -- could not create the temporary gateway configuration\" >&2; exit 1; fi; " +
        $"jq '{BuildOpenTelemetryGatewayConfigurationFilter(configuration)}' \"$s\" >\"$o/SetupConfiguration.json\"; " +
        "export CONFIG_DIR=\"$o\"; " +
        $"exec {GatewayEntrypointScriptPath} \"$@\"";
    }

    /// <summary>
    /// Canonicalizes an absolute Linux container path the way the container runtime resolves one
    /// before mounting: repeated separators collapse, <c>.</c> segments drop out, <c>..</c>
    /// segments remove the preceding one, and a <c>..</c> at the root is clamped to the root.
    /// </summary>
    /// <remarks>
    /// Every storage comparison runs on canonical paths, because Docker compares the resolved path
    /// and not the string the caller wrote: <c>/data</c>, <c>//data/</c>, <c>/foo/../data</c> and
    /// <c>/../data</c> are one and the same mount destination. Comparing them as written would let
    /// an alias of the data directory be picked as a scratch root. Returns <see langword="null"/>
    /// for a path the runtime would refuse outright — one that is not absolute, or that collapses
    /// to the container root.
    /// </remarks>
    private static string? CanonicalizeContainerPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '/')
        {
            return null;
        }

        var segments = new List<string>();

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || string.Equals(segment, ".", StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(segment, "..", StringComparison.Ordinal))
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }

                // The runtime clamps at the root rather than failing, so canonicalization
                // continues on the clamped path: that is the destination it really creates.
                continue;
            }

            segments.Add(segment);
        }

        return segments.Count == 0 ? null : "/" + string.Join('/', segments);
    }

    /// <summary>
    /// The container path a mount really lands on, or <see langword="null"/> when the runtime would
    /// refuse the target outright.
    /// </summary>
    private static string? ResolveMountTarget(ContainerMountAnnotation mount) =>
        CanonicalizeContainerPath(mount.Target);

    /// <summary>
    /// Whether <paramref name="canonicalPath"/> is inside — or is — the directory a mount on
    /// <paramref name="canonicalTarget"/> supplies. Both paths are canonical, and the comparison is
    /// made on segment boundaries so <c>/datastore</c> is not treated as living under <c>/data</c>.
    /// </summary>
    private static bool BacksContainerPath(string canonicalTarget, string canonicalPath) =>
        string.Equals(canonicalTarget, canonicalPath, StringComparison.Ordinal) ||
        (canonicalPath.Length > canonicalTarget.Length &&
         canonicalPath[canonicalTarget.Length] == '/' &&
         canonicalPath.StartsWith(canonicalTarget, StringComparison.Ordinal));

    /// <summary>
    /// The scratch roots the wrapper will try, in order. Each has to exist, be writable, be
    /// outside <c>DATA_PATH</c> as a container path, and be backed by storage that is not the
    /// storage <c>DATA_PATH</c> is on.
    /// </summary>
    private static readonly string[] s_openTelemetryScratchRoots = ["/tmp", "/var/tmp", "/dev/shm"];

    /// <summary>
    /// Where a volume-backed container path lives: a named volume plus the part of the path below
    /// its mount point. Bind mounts retain only their type because their physical source cannot be
    /// proven from a host path string.
    /// </summary>
    /// <remarks>
    /// A bind source is resolved by the Docker daemon, which may be remote and may traverse
    /// symbolic links that do not exist on the model-building or publishing machine. Therefore two
    /// bind-backed paths are conservatively treated as potentially aliased regardless of how their
    /// source strings differ. Named-volume identity is daemon-independent and can still be compared
    /// exactly.
    /// </remarks>
    private readonly record struct MountBackingRegion(ContainerMountType Type, string Source, string Subpath);

    /// <summary>
    /// For every scratch root that some mount backs, the canonical <c>DATA_PATH</c> values that
    /// would put the wrapper's scratch directory on the same storage as the data directory,
    /// rendered as quoted <c>case</c> patterns over <c>"$y|$d"</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A candidate root that no mount covers lives in the container's own filesystem and cannot
    /// alias anything the container path test does not already catch, so it contributes nothing
    /// and the emitted script is exactly the one a resource with no mounts gets.
    /// </para>
    /// <para>
    /// The data directory is only known at runtime — <c>DATA_PATH</c> can be a deferred value and
    /// <c>--data-path</c> can move it again — but the mount table is known here, so the answer is
    /// expressed as the set of data directories each candidate is incompatible with rather than as
    /// a decision. Named volumes can be compared exactly. Bind paths cannot: when both the
    /// candidate and a possible data path are bind-backed, that candidate is skipped because the
    /// Docker daemon may resolve lexically different sources through a symbolic link to the same
    /// directory. This is deliberately independent of the local filesystem so run and publish
    /// produce the same command for a remote daemon.
    /// </para>
    /// <para>
    /// Anonymous volumes are left out: nothing else can be mounted from one, so a candidate and a
    /// data directory on the same anonymous volume are the same container path subtree, which the
    /// script's own test already refuses.
    /// </para>
    /// </remarks>
    private static System.Collections.Immutable.ImmutableArray<string> BuildOpenTelemetryScratchRootAliases(IResource resource)
    {
        var mounts = resource.Annotations.OfType<ContainerMountAnnotation>()
            .Select(mount => (Mount: mount, Target: ResolveMountTarget(mount)))
            .Where(entry => entry.Target is not null && entry.Mount.Source is not null)
            .Select(entry => (entry.Mount, Target: entry.Target!))
            .ToList();

        if (mounts.Count == 0)
        {
            return [];
        }

        var patterns = new List<string>();

        foreach (var candidate in s_openTelemetryScratchRoots)
        {
            // The most specific mount wins, exactly as the kernel resolves it. Duplicate targets
            // are a configuration the storage rules refuse in their own right; here they are all
            // taken into account, because either of them could be the one that supplies the root.
            var depth = -1;
            var forbidden = new SortedSet<string>(StringComparer.Ordinal);

            foreach (var (mount, target) in mounts)
            {
                if (!BacksContainerPath(target, candidate) || target.Length < depth)
                {
                    continue;
                }

                if (target.Length > depth)
                {
                    depth = target.Length;
                    forbidden.Clear();
                }

                var candidateRegion = DescribeMountBackingRegion(mount, target, candidate);

                foreach (var (other, otherTarget) in mounts)
                {
                    if (candidateRegion.Type == ContainerMountType.BindMount &&
                        other.Type == ContainerMountType.BindMount)
                    {
                        // There is no daemon-independent proof that two bind source spellings are
                        // physically disjoint. The remote daemon may resolve either through an
                        // ancestor or a symbolic link that is absent on this machine.
                        AddForbiddenDataPathAndAncestors(
                            forbidden,
                            candidate,
                            otherTarget,
                            withDescendants: true);
                        continue;
                    }

                    var otherRegion = DescribeMountBackingRegion(other, otherTarget, otherTarget);

                    if (TryGetRegionSubpath(candidateRegion, otherRegion, out _))
                    {
                        // Everything the other mount supplies is inside the candidate's region,
                        // and a DATA_PATH above the mount point contains that alias too.
                        AddForbiddenDataPathAndAncestors(
                            forbidden,
                            candidate,
                            otherTarget,
                            withDescendants: true);
                        continue;
                    }

                    if (!TryGetRegionSubpath(otherRegion, candidateRegion, out var delta))
                    {
                        continue;
                    }

                    // The candidate's region is one directory below the other mount's. That exact
                    // container path is the alias, and so is anything under it - and so is every
                    // directory between the mount point and it, which would contain it.
                    AddForbiddenDataPathAndAncestors(
                        forbidden,
                        candidate,
                        otherTarget,
                        withDescendants: false);

                    var path = otherTarget;
                    foreach (var segment in delta.Split('/', StringSplitOptions.RemoveEmptyEntries))
                    {
                        AddForbiddenDataPath(forbidden, candidate, path, withDescendants: false);
                        path += "/" + segment;
                    }

                    AddForbiddenDataPath(forbidden, candidate, path, withDescendants: true);
                }
            }

            patterns.AddRange(forbidden);
        }

        return [.. patterns];
    }

    /// <summary>
    /// Records a mounted alias and every container directory above it, because DATA_PATH contains
    /// the alias when it names any such ancestor.
    /// </summary>
    private static void AddForbiddenDataPathAndAncestors(
        SortedSet<string> forbidden,
        string candidate,
        string dataPath,
        bool withDescendants)
    {
        AddForbiddenDataPath(forbidden, candidate, dataPath, withDescendants);

        var ancestor = dataPath;
        while (true)
        {
            var separator = ancestor.LastIndexOf('/');
            if (separator <= 0)
            {
                break;
            }

            ancestor = ancestor[..separator];
            AddForbiddenDataPath(forbidden, candidate, ancestor, withDescendants: false);
        }
    }

    /// <summary>
    /// Records a data directory the candidate root cannot be used with, unless the script's own
    /// container-path test already refuses that pair.
    /// </summary>
    /// <remarks>
    /// A data directory at or below the candidate root — and, with
    /// <paramref name="withDescendants"/>, everything below it — is already excluded at runtime by
    /// <c>case "$d" in "$x"|"$x"/*</c>, whatever storage backs either of them. Emitting it again
    /// would only make the wrapper report an aliasing problem where the plain container-path rule
    /// is the reason.
    /// </remarks>
    private static void AddForbiddenDataPath(
        SortedSet<string> forbidden,
        string candidate,
        string dataPath,
        bool withDescendants)
    {
        if (BacksContainerPath(candidate, dataPath))
        {
            return;
        }

        forbidden.Add(QuoteScratchAliasPattern(candidate, dataPath));

        if (withDescendants)
        {
            forbidden.Add(QuoteScratchAliasPattern(candidate, dataPath) + "/*");
        }
    }

    /// <summary>
    /// The region <paramref name="path"/> occupies, given that <paramref name="mount"/> lands on
    /// <paramref name="mountTarget"/> and supplies it.
    /// </summary>
    private static MountBackingRegion DescribeMountBackingRegion(
        ContainerMountAnnotation mount,
        string mountTarget,
        string path)
    {
        var subpath = path.Length == mountTarget.Length ? string.Empty : path[(mountTarget.Length + 1)..];

        return mount.Type == ContainerMountType.BindMount
            ? new(ContainerMountType.BindMount, string.Empty, string.Empty)
            : new(ContainerMountType.Volume, mount.Source!, subpath);
    }

    /// <summary>
    /// Whether <paramref name="inner"/> is inside — or is — <paramref name="outer"/>, and if so
    /// how far below it, as a <c>/</c>-separated relative path.
    /// </summary>
    private static bool TryGetRegionSubpath(MountBackingRegion outer, MountBackingRegion inner, out string subpath)
    {
        subpath = string.Empty;

        if (outer.Type != ContainerMountType.Volume ||
            inner.Type != ContainerMountType.Volume)
        {
            return false;
        }

        if (!string.Equals(outer.Source, inner.Source, StringComparison.Ordinal))
        {
            return false;
        }

        if (outer.Subpath.Length == 0)
        {
            subpath = inner.Subpath;
            return true;
        }

        if (string.Equals(outer.Subpath, inner.Subpath, StringComparison.Ordinal))
        {
            return true;
        }

        if (inner.Subpath.Length <= outer.Subpath.Length ||
            inner.Subpath[outer.Subpath.Length] != '/' ||
            !inner.Subpath.StartsWith(outer.Subpath, StringComparison.Ordinal))
        {
            return false;
        }

        subpath = inner.Subpath[(outer.Subpath.Length + 1)..];
        return true;
    }

    /// <summary>
    /// One <c>case</c> pattern matching the literal candidate root and data directory pair, as a
    /// quoted shell word so that a path containing a glob character matches only itself.
    /// </summary>
    private static string QuoteScratchAliasPattern(string candidate, string dataPath) =>
        "\"" + EscapeShellDoubleQuoted(candidate) + "|" + EscapeShellDoubleQuoted(dataPath) + "\"";

    private static string EscapeShellDoubleQuoted(string value)
    {
        var escaped = new System.Text.StringBuilder(value.Length);

        foreach (var character in value)
        {
            if (character is '\\' or '"' or '$' or '`')
            {
                escaped.Append('\\');
            }

            escaped.Append(character);
        }

        return escaped.ToString();
    }

    /// <summary>
    /// Builds the <c>jq</c> filter that removes exactly the <c>SetupConfiguration.json</c> keys
    /// this package's environment variables have to win over, and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>TelemetryOptions.Metrics</c> object is removed whole, not key by key, because this
    /// API owns the metrics signal end to end. Any surviving key re-pins that setting ahead of
    /// the documented environment precedence - the shipped
    /// <c>OtlpEndpoint: http://localhost:4317</c> would beat
    /// <c>OTEL_EXPORTER_OTLP_METRICS_ENDPOINT</c> and <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> and
    /// export metrics into the container itself - and enumerating the keys individually would
    /// silently leave any field a later gateway release adds authoritative over the environment.
    /// Removing the object costs nothing on the stock image: the values it ships are the
    /// gateway's own compiled-in defaults.
    /// </para>
    /// <para>
    /// The identity keys are different: they are shared with tracing, and the shipped
    /// <c>ServiceName</c> is not the gateway's compiled-in default, so removing it would silently
    /// rename every signal. They are removed only when the caller explicitly supplied the
    /// corresponding parameter. <c>TelemetryOptions.Tracing</c> is never touched.
    /// </para>
    /// </remarks>
    private static string BuildOpenTelemetryGatewayConfigurationFilter(
        OpenTelemetryGatewayConfigurationAnnotation configuration)
    {
        var paths = new List<string> { ".TelemetryOptions.Metrics" };

        if (configuration.ServiceNameConfigured)
        {
            paths.Add(".TelemetryOptions.ServiceName");
        }

        if (configuration.ServiceVersionConfigured)
        {
            paths.Add(".TelemetryOptions.ServiceVersion");
        }

        return $"del({string.Join(", ", paths)})";
    }

    /// <summary>
    /// Adds a DocumentDB resource to the application model. A container is used for local development.
    /// </summary>
    /// <remarks>
    /// This resource includes a built-in health check. When this resource is referenced as a dependency
    /// using the <see cref="ResourceBuilderExtensions.WaitFor{T}(IResourceBuilder{T}, IResourceBuilder{IResource})"/>
    /// extension method then the dependent resource will wait until the DocumentDB server responds to ping.
    /// This version of the package defaults to the <inheritdoc cref="DocumentDBContainerImageTags.Tag"/> tag of the <inheritdoc cref="DocumentDBContainerImageTags.Image"/> container image.
    /// </remarks>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
    /// <param name="port">The host port for DocumentDB.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <example>
    /// <code>
    /// var server = builder.AddDocumentDB("documentdb", port: 10260);
    /// </code>
    /// </example>
    public static IResourceBuilder<DocumentDBServerResource> AddDocumentDB(this IDistributedApplicationBuilder builder, [ResourceName] string name, int? port)
        => AddDocumentDB(builder, name, port, null, null);

    /// <summary>
    /// <inheritdoc cref="AddDocumentDB(IDistributedApplicationBuilder, string, int?)"/>
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
    /// <param name="port">The host port for DocumentDB.</param>
    /// <param name="userName">A parameter that contains the DocumentDB server user name, or <see langword="null"/> to use a default value.</param>
    /// <param name="password">A parameter that contains the DocumentDB server password, or <see langword="null"/> to use a generated password.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <example>
    /// <code>
    /// // Minimal usage with generated credentials:
    /// var server = builder.AddDocumentDB("documentdb");
    /// var database = server.AddDatabase("mydb");
    ///
    /// // With custom credentials:
    /// var user = builder.AddParameter("db-user");
    /// var pass = builder.AddParameter("db-pass", secret: true);
    /// var securedServer = builder.AddDocumentDB("documentdb", userName: user, password: pass);
    /// </code>
    /// </example>
    public static IResourceBuilder<DocumentDBServerResource> AddDocumentDB(this IDistributedApplicationBuilder builder,
        string name,
        int? port = null,
        IResourceBuilder<ParameterResource>? userName = null,
        IResourceBuilder<ParameterResource>? password = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var passwordParameter = password?.Resource ?? ParameterResourceBuilderExtensions.CreateDefaultPasswordParameter(builder, $"{name}-password", special: false);

        var DocumentDBContainer = new DocumentDBServerResource(name, userName?.Resource, passwordParameter);

        string? connectionString = null;

        builder.Eventing.Subscribe<ConnectionStringAvailableEvent>(DocumentDBContainer, async (@event, ct) =>
        {
            connectionString = await DocumentDBContainer.ConnectionStringExpression.GetValueAsync(ct).ConfigureAwait(false)
                ?? throw new DistributedApplicationException($"ConnectionStringAvailableEvent was published for the '{DocumentDBContainer.Name}' resource but the connection string was null.");
        });

        var healthCheckKey = $"{name}_check";
        // Use a database-scoped check so the MongoDB health check package executes a ping command.
        IMongoDatabase? database = null;
        builder.Services.AddHealthChecks()
            .AddMongoDb(
                _ => database ??=
                    new MongoClient(connectionString ?? throw new InvalidOperationException("Connection string is unavailable"))
                        .GetDatabase(DefaultHealthCheckDatabaseName),
                name: healthCheckKey);

        return builder
            .AddResource(DocumentDBContainer)
            .WithEndpoint(port: port, targetPort: DefaultContainerPort, name: DocumentDBServerResource.PrimaryEndpointName)
            .WithImage(DocumentDBContainerImageTags.Image, DocumentDBContainerImageTags.Tag)
            .WithImageRegistry(DocumentDBContainerImageTags.Registry)
            .WithEnvironment(context =>
            {
                context.EnvironmentVariables[UserEnvVarName] = DocumentDBContainer.UserNameReference;
                context.EnvironmentVariables[PasswordEnvVarName] = DocumentDBContainer.PasswordParameter!;
            })
            .WithHealthCheck(healthCheckKey)
            .SubscribeMinimumPgVariantImageGuard();
    }

    /// <summary>
    /// Subscribes a <see cref="BeforeResourceStartedEvent"/> handler that throws
    /// <see cref="InvalidOperationException"/> when the resource's effective image tag names a
    /// PostgreSQL backend variant upstream does not publish for that DocumentDB version — see
    /// <see cref="DocumentDBContainerImageTags.MinimumVersionByPgVariant"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Subscribed from <c>AddDocumentDB</c> rather than from <see cref="WithPostgresVersion"/>,
    /// because neither half of the tag is a problem on its own and the documented precedence is
    /// "last call wins": selecting <c>Pg18</c> before <c>V0_114_0</c> is perfectly legitimate,
    /// so only the effective tag at start time can be judged.
    /// </para>
    /// <para>
    /// Same carve-outs as <see cref="SubscribeMinimumPostgresImageGuard"/>: custom images, tags
    /// outside the strict <c>pg{NN}-X.Y.Z</c> grammar and caller-owned Dockerfile builds are
    /// exempt, and the guard is run-mode only, so manifest generation is unaffected. Unlike that
    /// guard this one is always subscribed, so the exempt paths stay silent rather than warning on
    /// every app that pins a custom image.
    /// </para>
    /// </remarks>
    private static IResourceBuilder<DocumentDBServerResource> SubscribeMinimumPgVariantImageGuard(
        this IResourceBuilder<DocumentDBServerResource> builder)
    {
        builder.ApplicationBuilder.Eventing.Subscribe<BeforeResourceStartedEvent>(
            builder.Resource,
            (evt, ct) =>
            {
                // Only a curated image is pulled by tag from the upstream registry. A fork
                // publishing its own images decides its own variant matrix, and a resource built
                // from the caller's own Dockerfile never resolves that tag at all.
                var image = ResolveEffectiveImage(evt.Resource);
                if (image is not { Origin: DocumentDBImageOrigin.Curated, KnownVersion: { } docVersion })
                {
                    return Task.CompletedTask;
                }

                if (!DocumentDBContainerImageTags.MinimumVersionByPgVariant.TryGetValue(image.PostgresVariant, out var minimum) ||
                    docVersion >= minimum)
                {
                    return Task.CompletedTask;
                }

                throw new InvalidOperationException(
                    $"DocumentDB resource '{evt.Resource.Name}' resolves to image tag " +
                    $"'{image.Tag}', but upstream only publishes pg{image.PostgresVariant} images " +
                    $"from DocumentDB v{minimum} onwards. That tag does not exist on " +
                    $"{DocumentDBContainerImageTags.Registry}/{DocumentDBContainerImageTags.Image}, " +
                    $"so starting the resource would fail with an opaque manifest-not-found error. " +
                    $"Recovery: pair " +
                    $"'.WithPostgresVersion(DocumentDBPostgresVersion.Pg{image.PostgresVariant})' " +
                    $"with DocumentDB v{minimum} or newer, or choose a PostgreSQL variant that " +
                    $"exists for v{docVersion}.");
            });

        return builder;
    }

    /// <summary>
    /// How much this package can know about the container image a DocumentDB resource will
    /// actually run.
    /// </summary>
    private enum DocumentDBImageOrigin
    {
        /// <summary>The resource carries no <see cref="ContainerImageAnnotation"/> at all.</summary>
        None,

        /// <summary>
        /// The image is the output of a container build the caller owns, so nothing this package
        /// documents about a published DocumentDB release has been established for it.
        /// </summary>
        DockerfileBuild,

        /// <summary>A repository other than the curated <c>documentdb-local</c> one.</summary>
        CustomRepository,

        /// <summary>
        /// The curated repository, with a tag outside the strict <c>pg{NN}-X.Y.Z</c> grammar.
        /// </summary>
        UnrecognizedTag,

        /// <summary>
        /// The curated repository, pinned by digest. The digest is what the runtime resolves, so
        /// the version is unknown whatever tag the reference also carries.
        /// </summary>
        DigestPinned,

        /// <summary>
        /// The curated repository with a tag this build recognises — the only origin that carries
        /// a DocumentDB version.
        /// </summary>
        Curated,
    }

    /// <summary>
    /// What this package knows about the container image a DocumentDB resource will run.
    /// </summary>
    /// <remarks>
    /// <see cref="KnownVersion"/> and <see cref="PostgresVariant"/> are populated only when
    /// <see cref="Origin"/> is <see cref="DocumentDBImageOrigin.Curated"/>, which in particular
    /// means never when a <see cref="Digest"/> is present. Every version-dependent decision in
    /// this package is therefore gated on <c>KnownVersion is { } version</c>: one place decides
    /// what is known, and no caller can conclude a version from an image whose version is not
    /// known.
    /// </remarks>
    /// <param name="Origin">How much is known about the image.</param>
    /// <param name="Image">
    /// The repository as the annotation spells it, for diagnostics. It is not the identity: see
    /// <see cref="DocumentDBContainerImageTags.NamesCuratedRepository"/>.
    /// </param>
    /// <param name="Tag">The effective tag, whether the annotation or the reference carried it.</param>
    /// <param name="Digest">
    /// The effective digest, whether the annotation or the reference carried it, without its
    /// algorithm prefix.
    /// </param>
    /// <param name="PostgresVariant">The PostgreSQL major version of a curated tag.</param>
    /// <param name="KnownVersion">The DocumentDB version of a curated tag.</param>
    private readonly record struct DocumentDBEffectiveImage(
        DocumentDBImageOrigin Origin,
        string? Image,
        string? Tag,
        string? Digest,
        int PostgresVariant,
        Version? KnownVersion);

    /// <summary>
    /// Resolves what this package knows about the container image <paramref name="resource"/>
    /// will actually run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A container build the caller owns is decided first and unconditionally, because for such a
    /// resource the image annotation does not describe what runs. Aspire keeps the
    /// <see cref="ContainerImageAnnotation"/> that <c>AddDocumentDB</c> installs when
    /// <c>WithDockerfile(...)</c> is chained onto the resource, and a caller may also point that
    /// annotation at the official repository and tag afterwards, so a Dockerfile-built resource
    /// can be annotated indistinguishably from an official release while running an image built
    /// from an arbitrary <c>Dockerfile</c> — one that merely inherits the official image as its
    /// base, or does not use it at all. What runs is the build output: the manifest emits
    /// <c>build</c> instead of <c>image</c>, and in run mode the orchestrator builds the context
    /// before starting the container. None of the release properties this package acts on — the
    /// <c>/data</c> volume declaration, the data-directory <c>flock</c>, the PostgreSQL credential
    /// pass-through, the gateway configuration layout and entrypoint — has been proven for such an
    /// image, so it is classified as unknown rather than granted them on the strength of a label.
    /// </para>
    /// <para>
    /// <see cref="DockerfileBuildAnnotation"/> is the single authoritative signal, and one check
    /// covers every entry point: <c>WithDockerfile</c>, <c>WithDockerfileFactory</c> and
    /// <c>WithDockerfileBuilder</c> all add it — the last adds a
    /// <c>DockerfileBuilderCallbackAnnotation</c> beside it, not instead of it.
    /// <c>DockerfileBaseImageAnnotation</c> on its own is not a build: it selects base images for
    /// a <em>generated</em> Dockerfile, and with no build to generate one it changes neither the
    /// image a container resource pulls nor the manifest it publishes.
    /// </para>
    /// <para>
    /// Everything else is decided on the reference Aspire composes rather than on
    /// <see cref="ContainerImageAnnotation.Image"/> alone, because the boundary between registry
    /// and repository is the caller's to move: see
    /// <see cref="DocumentDBContainerImageTags.NamesCuratedRepository"/>. A tag or digest the
    /// annotation supplies wins over one written into the reference, and two tags in one reference
    /// are contradictory — Aspire emits <c>repo:a:b</c>, which resolves to nothing — so neither is
    /// trusted.
    /// </para>
    /// <para>
    /// A digest beats every tag. A reference may carry both — <c>repo:pg17-0.116.0@sha256:...</c>,
    /// or an inline tag beside an annotation <c>SHA256</c>, or the reverse — and the runtime
    /// resolves the digest and ignores the tag, so the tag names whichever release the caller
    /// last typed rather than the image that starts. Reading a version out of it would let an
    /// image predating the <c>/data</c> volume, the data-directory <c>flock</c> or a credential
    /// floor inherit the promises of a release it is not. So any digest at all classifies the
    /// resource as <see cref="DocumentDBImageOrigin.DigestPinned"/>, which carries no version;
    /// the repository is still known, which is what lets the telemetry API reject the pin with a
    /// message instead of silently skipping it.
    /// </para>
    /// </remarks>
    private static DocumentDBEffectiveImage ResolveEffectiveImage(IResource resource)
    {
        var image = resource.Annotations.OfType<ContainerImageAnnotation>().LastOrDefault();

        if (resource.Annotations.OfType<DockerfileBuildAnnotation>().Any())
        {
            return new(DocumentDBImageOrigin.DockerfileBuild, image?.Image, image?.Tag, image?.SHA256, 0, null);
        }

        if (image is null)
        {
            return new(DocumentDBImageOrigin.None, null, null, null, 0, null);
        }

        var curated = DocumentDBContainerImageTags.NamesCuratedRepository(
            image.Registry, image.Image, out var inlineTag, out var inlineDigest);

        var tag = string.IsNullOrEmpty(image.Tag) ? inlineTag : image.Tag;
        var digest = string.IsNullOrEmpty(image.SHA256) ? inlineDigest : image.SHA256;

        // Two tags in one reference contradict each other. A digest given twice needs no such
        // rule: any digest at all already forces the version unknown.
        var ambiguousTag = !string.IsNullOrEmpty(image.Tag) && inlineTag is not null;

        if (!curated)
        {
            return new(DocumentDBImageOrigin.CustomRepository, image.Image, tag, digest, 0, null);
        }

        if (!string.IsNullOrEmpty(digest))
        {
            return new(DocumentDBImageOrigin.DigestPinned, image.Image, tag, digest, 0, null);
        }

        if (ambiguousTag ||
            !DocumentDBContainerImageTags.TryParseDocumentDBTag(tag, out var pg, out var version))
        {
            return new(DocumentDBImageOrigin.UnrecognizedTag, image.Image, tag, digest, 0, null);
        }

        return new(DocumentDBImageOrigin.Curated, image.Image, tag, digest, pg, version);
    }

    /// <summary>
    /// Adds a DocumentDB database to the application model.
    /// </summary>
    /// <remarks>
    /// The database resource inherits the parent server's connection string and appends the database name.
    /// Services should reference the database resource (not the server) via <c>.WithReference(db)</c>.
    /// This resource includes a built-in health check. When this resource is referenced as a dependency
    /// using the <see cref="ResourceBuilderExtensions.WaitFor{T}(IResourceBuilder{T}, IResourceBuilder{IResource})"/>
    /// extension method then the dependent resource will wait until the DocumentDB database responds to ping.
    /// </remarks>
    /// <param name="builder">The DocumentDB server resource builder.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
    /// <param name="databaseName">The name of the database. If not provided, this defaults to the same value as <paramref name="name"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <example>
    /// <code>
    /// var server = builder.AddDocumentDB("documentdb");
    /// var ordersDb = server.AddDatabase("orders");
    /// var usersDb = server.AddDatabase("users");
    /// </code>
    /// </example>
    public static IResourceBuilder<DocumentDBDatabaseResource> AddDatabase(this IResourceBuilder<DocumentDBServerResource> builder, [ResourceName] string name, string? databaseName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        // Use the resource name as the database name if it's not provided
        databaseName ??= name;

        var DocumentDBDatabase = new DocumentDBDatabaseResource(name, databaseName, builder.Resource);
        builder.Resource.AddDatabase(DocumentDBDatabase);

        string? connectionString = null;

        builder.ApplicationBuilder.Eventing.Subscribe<ConnectionStringAvailableEvent>(DocumentDBDatabase, async (@event, ct) =>
        {
            connectionString = await DocumentDBDatabase.ConnectionStringExpression.GetValueAsync(ct).ConfigureAwait(false)
                ?? throw new DistributedApplicationException($"ConnectionStringAvailableEvent was published for the '{DocumentDBDatabase.Name}' resource but the connection string was null.");
        });

        var healthCheckKey = $"{name}_check";
        // cache the database instance so it is reused on subsequent calls to the health check
        IMongoDatabase? database = null;
        builder.ApplicationBuilder.Services.AddHealthChecks()
            .AddMongoDb(
                _ => database ??=
                    new MongoClient(connectionString ?? throw new InvalidOperationException("Connection string is unavailable"))
                        .GetDatabase(databaseName),
                name: healthCheckKey);

        return builder.ApplicationBuilder
            .AddResource(DocumentDBDatabase)
            .WithHealthCheck(healthCheckKey);
    }

    /// <summary>
    /// Configures the host port that the DocumentDB resource is exposed on instead of using randomly assigned port.
    /// </summary>
    /// <param name="builder">The resource builder for DocumentDB.</param>
    /// <param name="port">The port to bind on the host. If <see langword="null"/> is used random port will be assigned.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <example>
    /// <code>
    /// var server = builder.AddDocumentDB("documentdb")
    ///                     .WithHostPort(10260);
    /// </code>
    /// </example>
    public static IResourceBuilder<DocumentDBServerResource> WithHostPort(this IResourceBuilder<DocumentDBServerResource> builder, int? port)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithEndpoint(DocumentDBServerResource.PrimaryEndpointName, endpoint =>
        {
            endpoint.Port = port;
        });
    }

    /// <summary>
    /// Exposes the PostgreSQL backend coordinator port of the DocumentDB Local container
    /// (default container port <c>9712</c>) as a second endpoint on the resource.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>documentdb-local</c> container bundles a MongoDB-compatible gateway and a
    /// PostgreSQL coordinator listening on separate ports. By default this integration only
    /// publishes the gateway port (<c>10260</c>) and surfaces a <c>mongodb://</c> connection
    /// string. Calling <see cref="WithPostgresEndpoint"/> additionally publishes the
    /// PostgreSQL port so consumers can use psql/Npgsql/etc. directly, and enables
    /// <see cref="DocumentDBServerResource.PostgresConnectionStringExpression"/>.
    /// </para>
    /// <para>
    /// The endpoint uses the same <c>userName</c>/<c>password</c> parameters as the gateway
    /// because the container provisions a single admin user shared by both surfaces.
    /// The default database in the resulting URI is <c>postgres</c>, matching the upstream
    /// entrypoint, which connects with <c>-d postgres</c>.
    /// </para>
    /// </remarks>
    /// <param name="builder">The resource builder for DocumentDB.</param>
    /// <param name="port">
    /// The host port to bind to. If <see langword="null"/> a random port is assigned.
    /// </param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <example>
    /// <code>
    /// var documentDB = builder.AddDocumentDB("documentdb")
    ///                         .WithPostgresEndpoint();
    ///
    /// builder.AddProject&lt;Projects.Worker&gt;("worker")
    ///        .WithEnvironment("ConnectionStrings__pg", documentDB.Resource.PostgresConnectionStringExpression);
    /// </code>
    /// </example>
    public static IResourceBuilder<DocumentDBServerResource> WithPostgresEndpoint(
        this IResourceBuilder<DocumentDBServerResource> builder,
        int? port = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.Resource.Annotations.OfType<EndpointAnnotation>()
                .Any(e => e.Name == DocumentDBServerResource.PostgresEndpointName))
        {
            throw new InvalidOperationException(
                $"A PostgreSQL endpoint has already been added to resource '{builder.Resource.Name}'. " +
                $"Call '{nameof(WithPostgresEndpoint)}()' at most once per DocumentDB resource.");
        }

        return builder
            .WithEndpoint(
                port: port,
                targetPort: DefaultPostgresContainerPort,
                scheme: "postgresql",
                name: DocumentDBServerResource.PostgresEndpointName)
            .WithEnvironment(context =>
            {
                // Explicitly opt the upstream entrypoint into accepting external PostgreSQL
                // connections (sets PGOPTIONS=-e -> listen_addresses='*' + permissive pg_hba.conf).
                // Setting this is required so publishing the host port produces a reachable
                // server even on upstream container builds where the entrypoint's default
                // ALLOW_EXTERNAL_CONNECTIONS handling is corrected.
                context.EnvironmentVariables[AllowExternalConnectionsEnvVarName] = "true";
            })
            .SubscribeMinimumPostgresImageGuard();
    }

    /// <summary>
    /// Subscribes a <see cref="BeforeResourceStartedEvent"/> handler that throws
    /// <see cref="InvalidOperationException"/> if the resource's effective container image
    /// tag is older than <see cref="DocumentDBContainerImageTags.MinimumPostgresEndpointVersion"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The handler is registered AFTER endpoint and environment configuration have run,
    /// but executes at run-time via the orchestrator, which honours the documented
    /// "last call wins" precedence: a <c>WithImageTag(...)</c> chained after
    /// <see cref="WithPostgresEndpoint"/> still affects the tag the guard sees.
    /// </para>
    /// <para>
    /// The guard is run-mode only. <see cref="BeforeResourceStartedEvent"/> is not published
    /// during manifest generation, so <c>azd publish</c> / <c>--publisher manifest</c> flows
    /// are unaffected — that is intentional, because no container is started in those modes.
    /// </para>
    /// <para>
    /// Custom images (anything whose <see cref="ContainerImageAnnotation.Image"/> is not
    /// the curated <see cref="DocumentDBContainerImageTags.Image"/>) are exempt with a
    /// single warning. Tags that do not match the strict <c>pg{NN}-X.Y.Z</c> pattern
    /// (e.g., <c>nightly</c>, <c>pg17-0.112.0-rc.1</c>) are also exempt with a single
    /// warning, so callers pinning custom builds or pre-releases are not surprised by an
    /// unactionable hard failure. A resource built from the caller's own Dockerfile is exempt
    /// on the same terms even when its image annotation names the curated image and a
    /// recognised tag, because the tag describes the build's starting point at best and the
    /// floor is a property of the published release. So is a digest-pinned reference, whose tag
    /// the runtime discards in favour of the digest.
    /// </para>
    /// </remarks>
    private static IResourceBuilder<DocumentDBServerResource> SubscribeMinimumPostgresImageGuard(
        this IResourceBuilder<DocumentDBServerResource> builder)
    {
        // Captured per-resource one-shot flag so unknown-tag / custom-image warnings
        // don't spam on every restart attempt. Hard-failure exceptions are deterministic
        // and intentionally re-thrown on each start attempt. Interlocked guard makes
        // the at-most-once property memory-safe even if a future Aspire orchestrator
        // dispatches BeforeResourceStartedEvent concurrently for the same resource.
        var warningLogged = 0;

        builder.ApplicationBuilder.Eventing.Subscribe<BeforeResourceStartedEvent>(
            builder.Resource,
            (evt, ct) =>
            {
                var image = ResolveEffectiveImage(evt.Resource);
                if (image.Origin == DocumentDBImageOrigin.None)
                {
                    // Defensive: AddDocumentDB sets ContainerImageAnnotation eagerly via WithImage.
                    return Task.CompletedTask;
                }

                var logger = TryGetResourceLogger(evt);

                // Caller-built carve-out, judged before the repository because a Dockerfile build
                // may carry the curated repository and tag verbatim: what starts is the output of
                // that build, so the tag says nothing about whether the credential fix is in it.
                if (image.Origin == DocumentDBImageOrigin.DockerfileBuild)
                {
                    if (Interlocked.CompareExchange(ref warningLogged, 1, 0) == 0)
                    {
                        logger?.LogWarning(
                            "DocumentDB resource '{ResourceName}' builds its container image from a Dockerfile, " +
                            "so what it runs is not a published DocumentDB release however its image annotation " +
                            "'{Image}:{Tag}' reads. The v{MinVersion} minimum required by WithPostgresEndpoint() " +
                            "for credential parity is NOT enforced on Dockerfile builds.",
                            evt.Resource.Name,
                            image.Image,
                            image.Tag,
                            DocumentDBContainerImageTags.MinimumPostgresEndpointVersion);
                    }
                    return Task.CompletedTask;
                }

                // Custom-image carve-out: only enforce the floor on the curated
                // documentdb-local image. A fork using a different image name
                // (regardless of registry) is assumed to know what it is doing.
                if (image.Origin == DocumentDBImageOrigin.CustomRepository)
                {
                    if (Interlocked.CompareExchange(ref warningLogged, 1, 0) == 0)
                    {
                        logger?.LogWarning(
                            "DocumentDB resource '{ResourceName}' uses custom image '{Image}:{Tag}'. " +
                            "The v{MinVersion} minimum required by WithPostgresEndpoint() for credential parity " +
                            "is NOT enforced on custom images.",
                            evt.Resource.Name,
                            image.Image,
                            image.Tag,
                            DocumentDBContainerImageTags.MinimumPostgresEndpointVersion);
                    }
                    return Task.CompletedTask;
                }

                // A digest supersedes the tag at the runtime, so the tag says nothing about the
                // image that starts and the floor has nothing to check it against.
                if (image.Origin == DocumentDBImageOrigin.DigestPinned)
                {
                    if (Interlocked.CompareExchange(ref warningLogged, 1, 0) == 0)
                    {
                        logger?.LogWarning(
                            "DocumentDB resource '{ResourceName}' pins its image by digest '{Digest}', so the " +
                            "DocumentDB version it runs is not the one tag '{Tag}' names. The v{MinVersion} " +
                            "minimum required by WithPostgresEndpoint() for credential parity is NOT enforced " +
                            "on digest-pinned images.",
                            evt.Resource.Name,
                            image.Digest,
                            image.Tag ?? "<none>",
                            DocumentDBContainerImageTags.MinimumPostgresEndpointVersion);
                    }
                    return Task.CompletedTask;
                }

                if (image.KnownVersion is not { } docVersion)
                {
                    if (Interlocked.CompareExchange(ref warningLogged, 1, 0) == 0)
                    {
                        logger?.LogWarning(
                            "DocumentDB resource '{ResourceName}' uses image tag '{Tag}', which does not match " +
                            "the curated 'pg{{NN}}-X.Y.Z' pattern. The v{MinVersion} minimum required by " +
                            "WithPostgresEndpoint() for credential parity is NOT enforced on unrecognised tags.",
                            evt.Resource.Name,
                            image.Tag,
                            DocumentDBContainerImageTags.MinimumPostgresEndpointVersion);
                    }
                    return Task.CompletedTask;
                }

                if (docVersion < DocumentDBContainerImageTags.MinimumPostgresEndpointVersion)
                {
                    throw new InvalidOperationException(
                        $"DocumentDB resource '{evt.Resource.Name}' is configured with image tag " +
                        $"'{image.Tag}', but WithPostgresEndpoint() requires DocumentDB " +
                        $"v{DocumentDBContainerImageTags.MinimumPostgresEndpointVersion} or later. " +
                        $"Earlier images hard-code the PostgreSQL admin credentials to " +
                        $"'docdb_admin'/'Admin100', so the Aspire-generated postgresql:// connection " +
                        $"string would silently fail to authenticate. Recovery: chain " +
                        $"'.WithImageTag(\"pg{{NN}}-{DocumentDBContainerImageTags.MinimumPostgresEndpointVersion}\")' " +
                        $"(or newer) after AddDocumentDB(...). See " +
                        $"https://github.com/microsoft/azure-databases-aspire/issues/71.");
                }

                return Task.CompletedTask;
            });

        return builder;
    }

    private static ILogger? TryGetResourceLogger(BeforeResourceStartedEvent evt)
    {
        // Prefer per-resource logger so the message shows in the Aspire dashboard's
        // resource log pane. Fall back to a general host logger if the service is
        // not registered (shouldn't happen in 13.3.5, but defensive).
        var resourceLoggerService = evt.Services.GetService<ResourceLoggerService>();
        if (resourceLoggerService is not null)
        {
            return resourceLoggerService.GetLogger(evt.Resource);
        }

        var loggerFactory = evt.Services.GetService<ILoggerFactory>();
        return loggerFactory?.CreateLogger("Aspire.Hosting.DocumentDB.WithPostgresEndpoint");
    }

    /// <summary>
    /// Adds a named volume for the data folder to a DocumentDB container resource.
    /// </summary>
    /// <remarks>
    /// The bare DocumentDB container defaults <c>DATA_PATH</c> to <c>/data</c>. Starting with
    /// DocumentDB v0.116-0, the image declares that path as a Docker volume, so omitting this
    /// helper can create an anonymous volume whose lifetime is controlled by the container
    /// runtime. Use this helper when persistence should be explicit and predictable.
    /// This helper mounts the volume at <paramref name="targetPath"/> and sets
    /// <c>DATA_PATH</c> to the same value so DocumentDB writes to the mounted directory.
    /// A persisted data directory may be attached to only one running v0.116-0 container.
    /// </remarks>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the volume. Defaults to an auto-generated name based on the application and resource names.</param>
    /// <param name="isReadOnly">A flag that indicates if this is a read-only volume.</param>
    /// <param name="targetPath">The target path inside the container. Defaults to /data to match the container default when this helper is used.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <example>
    /// <code>
    /// var server = builder.AddDocumentDB("documentdb")
    ///                     .WithDataVolume();
    /// </code>
    /// </example>
    public static IResourceBuilder<DocumentDBServerResource> WithDataVolume(
        this IResourceBuilder<DocumentDBServerResource> builder,
        string? name = null,
        bool isReadOnly = false,
        string? targetPath = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        targetPath ??= DefaultMountedDataPath;

        return builder
            .WithVolume(name ?? VolumeNameGenerator.Generate(builder, "data"), targetPath, isReadOnly)
            .WithEnvironment(context =>
            {
                context.EnvironmentVariables[DataPathEnvVarName] = targetPath;
            });
    }

    /// <summary>
    /// Adds a bind mount for the data folder to a DocumentDB container resource.
    /// </summary>
    /// <remarks>
    /// Prefer <see cref="WithDataVolume"/> for most cases. Bind mounts are useful when you need
    /// direct access to the data files on the host filesystem.
    /// The bare DocumentDB container defaults <c>DATA_PATH</c> to <c>/data</c>.
    /// This helper mounts the directory at <c>/data</c> (the container default) and sets
    /// <c>DATA_PATH</c> to the same value so DocumentDB writes to the mounted directory.
    /// </remarks>
    /// <param name="builder">The resource builder.</param>
    /// <param name="source">The source directory on the host to mount into the container.</param>
    /// <param name="isReadOnly">A flag that indicates if this is a read-only mount.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <example>
    /// <code>
    /// var server = builder.AddDocumentDB("documentdb")
    ///                     .WithDataBindMount("./data/documentdb");
    /// </code>
    /// </example>
    public static IResourceBuilder<DocumentDBServerResource> WithDataBindMount(this IResourceBuilder<DocumentDBServerResource> builder, string source, bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(source);

        const string targetPath = DefaultMountedDataPath;

        return builder
            .WithBindMount(source, targetPath, isReadOnly)
            .WithEnvironment(context =>
            {
                context.EnvironmentVariables[DataPathEnvVarName] = targetPath;
            });
    }

    /// <summary>
    /// Configures the DocumentDB Local container log level.
    /// </summary>
    /// <param name="builder">The resource builder for DocumentDB.</param>
    /// <param name="logLevel">The log level to configure.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<DocumentDBServerResource> WithLogLevel(this IResourceBuilder<DocumentDBServerResource> builder, DocumentDBLogLevel logLevel)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithEnvironment(context =>
        {
            context.EnvironmentVariables[LogLevelEnvVarName] = logLevel.ToEnvironmentValue();
        });
    }

    /// <summary>
    /// Mounts custom initialization scripts into the DocumentDB Local container.
    /// </summary>
    /// <remarks>
    /// The provided directory is bind-mounted at <c>/init_doc_db.d</c>, and the built-in sample data
    /// initialization is implicitly disabled so the mounted scripts are the only initialization source.
    /// DocumentDB v0.116-0 records initialization attempts in the data directory. Scripts run once
    /// for a new volume and are not reapplied when their contents change. A failed partial attempt is
    /// also not automatically retried; use a fresh or reset volume after correcting the scripts.
    /// </remarks>
    /// <param name="builder">The resource builder for DocumentDB.</param>
    /// <param name="source">The source directory on the host to mount into the container.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<DocumentDBServerResource> WithInitData(this IResourceBuilder<DocumentDBServerResource> builder, string source)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(source);

        return builder
            .WithBindMount(source, InitDataMountPath, isReadOnly: true)
            .WithEnvironment(context =>
            {
                context.EnvironmentVariables[InitDataPathEnvVarName] = InitDataMountPath;
                context.EnvironmentVariables[SkipInitDataEnvVarName] = "true";
            });
    }

    /// <summary>
    /// Disables the built-in sample data initialization performed by the DocumentDB Local container.
    /// </summary>
    /// <param name="builder">The resource builder for DocumentDB.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<DocumentDBServerResource> WithoutSampleData(this IResourceBuilder<DocumentDBServerResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithEnvironment(context =>
        {
            context.EnvironmentVariables[SkipInitDataEnvVarName] = "true";
        });
    }

    /// <summary>
    /// Disables the <c>extended_rum</c> index access method in the DocumentDB Local container
    /// by setting <c>DISABLE_EXTENDED_RUM=true</c>.
    /// </summary>
    /// <remarks>
    /// Available in DocumentDB <c>v0.111-0</c> and later. On older container images the
    /// environment variable is ignored.
    /// </remarks>
    /// <param name="builder">The resource builder for DocumentDB.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<DocumentDBServerResource> WithoutExtendedRum(this IResourceBuilder<DocumentDBServerResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithEnvironment(context =>
        {
            context.EnvironmentVariables[DisableExtendedRumEnvVarName] = "true";
        });
    }

    /// <summary>
    /// Disables the DocumentDB Local container's automatic user creation by setting the
    /// upstream <c>CREATE_USER=false</c> environment variable.
    /// </summary>
    /// <remarks>
    /// Use only after a previous run has already created the user in persisted storage
    /// (<see cref="WithDataVolume"/> / <see cref="WithDataBindMount"/>). Setting
    /// <c>CREATE_USER=false</c> on a fresh container will cause init-data steps to fail
    /// authentication and the container entrypoint to exit non-zero. To avoid spurious
    /// init-data runs on subsequent starts, also call <see cref="WithoutSampleData"/>.
    /// <para>
    /// <strong>Important:</strong> The container's init-data scripts (both built-in sample data
    /// and custom scripts mounted via <see cref="WithInitData"/>) authenticate using the
    /// configured credentials. If the user does not exist because creation was skipped,
    /// these scripts will fail and the container will exit. Always pair this method with
    /// <see cref="WithoutSampleData"/> and ensure the user already exists in the persisted data.
    /// </para>
    /// </remarks>
    /// <param name="builder">The resource builder for DocumentDB.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<DocumentDBServerResource> WithoutUserCreation(this IResourceBuilder<DocumentDBServerResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithEnvironment(context =>
        {
            context.EnvironmentVariables[CreateUserEnvVarName] = "false";
        });
    }

    /// <summary>
    /// Mounts a custom TLS certificate and key into the DocumentDB Local container.
    /// </summary>
    /// <remarks>
    /// The certificate and key files are mounted at distinct container paths so that
    /// they do not collide even if their host file names are identical.
    /// </remarks>
    /// <param name="builder">The resource builder for DocumentDB.</param>
    /// <param name="certPath">The certificate file to mount into the container.</param>
    /// <param name="keyPath">The private key file to mount into the container.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<DocumentDBServerResource> WithTlsCertificate(this IResourceBuilder<DocumentDBServerResource> builder, string certPath, string keyPath)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(certPath);
        ArgumentException.ThrowIfNullOrEmpty(keyPath);

        var certTargetPath = GetMountedFilePath(certPath, nameof(certPath), "documentdb-cert-");
        var keyTargetPath = GetMountedFilePath(keyPath, nameof(keyPath), "documentdb-key-");

        return builder
            .WithBindMount(certPath, certTargetPath, isReadOnly: true)
            .WithBindMount(keyPath, keyTargetPath, isReadOnly: true)
            .WithEnvironment(context =>
            {
                context.EnvironmentVariables[CertPathEnvVarName] = certTargetPath;
                context.EnvironmentVariables[KeyFileEnvVarName] = keyTargetPath;
            });
    }

    /// <summary>
    /// Enables or disables DocumentDB Local telemetry by setting the <c>ENABLE_TELEMETRY</c>
    /// environment variable.
    /// </summary>
    /// <param name="builder">The resource builder for DocumentDB.</param>
    /// <param name="enabled">Whether telemetry should be enabled.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// The <c>ENABLE_TELEMETRY</c> environment variable is not consumed by the DocumentDB gateway
    /// in container image v0.112-0 or later. On those images this method has no observable effect
    /// on the running container. Use <see cref="WithOpenTelemetryMetrics(IResourceBuilder{DocumentDBServerResource}, string?, bool, TimeSpan?, TimeSpan?, string?, string?)"/>
    /// to configure OTLP metrics export.
    /// </remarks>
    [Obsolete(
        "ENABLE_TELEMETRY is not consumed by the DocumentDB gateway in container image v0.112-0 " +
        "or later, so this method has no observable effect on those images. Use " +
        "WithOpenTelemetryMetrics(...) for OTLP metrics. This member is kept for binary " +
        "compatibility and may be removed in a future release.",
        error: false,
        DiagnosticId = "ASPIREDOCDB0001",
        UrlFormat = "https://github.com/microsoft/azure-databases-aspire/blob/main/docs/configuration.md#withtelemetry-obsolete")]
    public static IResourceBuilder<DocumentDBServerResource> WithTelemetry(this IResourceBuilder<DocumentDBServerResource> builder, bool enabled = true)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithEnvironment(context =>
        {
            context.EnvironmentVariables[EnableTelemetryEnvVarName] = enabled ? "true" : "false";
        });
    }

    /// <summary>
    /// Enables OpenTelemetry metrics export from the DocumentDB gateway via OTLP/gRPC.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Requires container image v0.112-0 or later. This API configures metrics only. The
    /// gateway also supports tracing in v0.116-0 and later, but this package does not yet
    /// expose a typed tracing API.
    /// </para>
    /// <para>
    /// The container default for <c>OTEL_METRICS_ENABLED</c> is <c>false</c>; calling this method
    /// flips it to <c>true</c> unless <paramref name="enabled"/> is explicitly set to <c>false</c>.
    /// </para>
    /// <para>
    /// Starting with DocumentDB v0.116-0, the gateway resolves telemetry settings as
    /// <em>JSON &gt; environment variable &gt; default</em>, reading them from
    /// <c>SetupConfiguration.json</c>, and the shipped file pins metrics off. Whenever this method
    /// is called against an official <c>documentdb-local</c> image of that version or later, it
    /// therefore wraps the container entrypoint so the container starts from a copy of that
    /// configuration with the shadowing keys removed. The copy is derived from the same directory
    /// the image's own entrypoint would read: an explicit <c>CONFIG_DIR</c>, else the packaged
    /// <c>/etc/documentdb/gateway</c> layout when present, else
    /// <c>$GATEWAY_HOME/pg_documentdb_gw</c>.
    /// </para>
    /// <para>
    /// The wrapper is applied for <c>enabled: false</c> as well, because a caller-supplied
    /// configuration file can turn metrics on from JSON and an explicit
    /// <c>enabled: false</c> has to win. The <c>TelemetryOptions.Metrics</c> object is removed
    /// whole, since this API owns that signal and any surviving key - including one a later
    /// gateway release adds - would re-pin a setting ahead of the environment precedence
    /// documented below, such as the <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> fallback. The shared
    /// identity keys are removed only
    /// when the corresponding parameter was explicitly supplied on some call, and
    /// <c>TelemetryOptions.Tracing</c> is never touched, so the stock image keeps its shipped
    /// service identity and its disabled tracing.
    /// </para>
    /// <para>
    /// Because the gateway builds one OpenTelemetry <c>Resource</c> for all signals, supplying
    /// <paramref name="serviceName"/> or <paramref name="serviceVersion"/> removes the shared
    /// JSON identity and therefore changes the identity of exported traces too, not only metrics.
    /// Omit them to keep the identity the configuration file specifies.
    /// </para>
    /// <para>
    /// The wrapper is expressed purely as the container <c>entrypoint</c> and <c>args</c>, both of
    /// which round-trip through the Aspire manifest, so publish mode, <c>azd</c> and direct run
    /// mode all execute the same thing. Custom images, tags outside the <c>pg{NN}-X.Y.Z</c>
    /// grammar and resources built from your own Dockerfile are left completely untouched — the
    /// last of those even when the resource's image annotation names the official image and a
    /// recognised tag, because what runs is the build output; private mirrors of the official
    /// image are not, because only the registry differs. Pinning the official image by digest
    /// throws, because the digest makes the version opaque and both applying and skipping the
    /// wrapper on a guess are silently wrong. Supplying your own container entrypoint or enabling
    /// <see cref="ContainerResource.ShellExecution"/> on the same resource also throws, because
    /// either can replace the verified command. Raw Docker/Podman image operands, rootfs,
    /// storage, entrypoint and protected environment overrides are rejected for the same reason;
    /// express them through the resource model instead. Runtime diagnostics never repeat an
    /// operand or value. The wrapper needs <c>bash</c> and <c>jq</c>, which the official image
    /// provides; it fails the container start with a diagnostic rather than starting without the
    /// override if either is missing.
    /// </para>
    /// <para>
    /// Merge semantics across multiple calls on the same builder:
    /// <list type="bullet">
    ///   <item>
    ///     <paramref name="enabled"/> is non-nullable and is therefore written on every call.
    ///     The last call's value wins (defaulting to <c>true</c> when omitted), even if a
    ///     previous call set it to <c>false</c>.
    ///   </item>
    ///   <item>
    ///     All other parameters are nullable; later calls override only the environment variables
    ///     they explicitly set, and values from earlier calls are preserved for parameters left
    ///     at <see langword="null"/>.
    ///   </item>
    /// </list>
    /// </para>
    /// <para>
    /// When <paramref name="endpoint"/> is omitted, the gateway falls back to the standard OTLP/gRPC
    /// default (<c>http://localhost:4317</c>). In an Aspire container scenario, that fallback is
    /// rarely reachable, so an explicit endpoint pointing to your collector is recommended.
    /// </para>
    /// <para>
    /// <paramref name="exportInterval"/> and <paramref name="timeout"/> are written as integer
    /// milliseconds via <see cref="CultureInfo.InvariantCulture"/>. Values smaller than one
    /// millisecond (sub-ms ticks) truncate to <c>0</c>; callers should pass whole-millisecond or
    /// larger granularities.
    /// </para>
    /// </remarks>
    /// <param name="builder">The resource builder for DocumentDB.</param>
    /// <param name="endpoint">
    /// OTLP/gRPC endpoint of the collector that should receive metrics. When provided, sets
    /// <c>OTEL_EXPORTER_OTLP_METRICS_ENDPOINT</c> (which takes precedence over the generic
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> per the OpenTelemetry specification). Defaults to
    /// <see langword="null"/> (leave the environment variable unset; gateway falls back to its
    /// own default).
    /// </param>
    /// <param name="enabled">
    /// Whether metrics export is enabled. Sets <c>OTEL_METRICS_ENABLED</c>. Defaults to
    /// <see langword="true"/>: opting into this method clearly indicates the caller wants metrics on.
    /// </param>
    /// <param name="exportInterval">
    /// How often the gateway flushes accumulated metrics to the collector. When provided, sets
    /// <c>OTEL_METRIC_EXPORT_INTERVAL</c> (milliseconds, integer). Must be non-negative.
    /// </param>
    /// <param name="timeout">
    /// Per-export request timeout. When provided, sets <c>OTEL_EXPORTER_OTLP_METRICS_TIMEOUT</c>
    /// (milliseconds, integer). Must be non-negative.
    /// </param>
    /// <param name="serviceName">
    /// Logical service name attached to the telemetry. When provided, sets
    /// <c>OTEL_SERVICE_NAME</c> and, on affected images, removes the shared
    /// <c>TelemetryOptions.ServiceName</c> so this value wins for every signal.
    /// </param>
    /// <param name="serviceVersion">
    /// Logical service version attached to the telemetry. When provided, sets
    /// <c>OTEL_SERVICE_VERSION</c> and, on affected images, removes the shared
    /// <c>TelemetryOptions.ServiceVersion</c> so this value wins for every signal.
    /// </param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="endpoint"/>, <paramref name="serviceName"/>, or <paramref name="serviceVersion"/>
    /// is provided but is empty or whitespace.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="exportInterval"/> or <paramref name="timeout"/> is negative.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The affected official image cannot be classified safely, or a custom entrypoint,
    /// <see cref="ContainerResource.ShellExecution"/>, or raw Docker/Podman image, rootfs, storage,
    /// entrypoint, or protected environment override would replace part of the compatibility
    /// command.
    /// </exception>
    /// <example>
    /// <code>
    /// var server = builder.AddDocumentDB("documentdb")
    ///                     .WithOpenTelemetryMetrics(
    ///                         endpoint: "http://otel-collector:4317",
    ///                         exportInterval: TimeSpan.FromSeconds(30));
    /// </code>
    /// </example>
    public static IResourceBuilder<DocumentDBServerResource> WithOpenTelemetryMetrics(
        this IResourceBuilder<DocumentDBServerResource> builder,
        string? endpoint = null,
        bool enabled = true,
        TimeSpan? exportInterval = null,
        TimeSpan? timeout = null,
        string? serviceName = null,
        string? serviceVersion = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (endpoint is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        }

        if (serviceName is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        }

        if (serviceVersion is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(serviceVersion);
        }

        if (exportInterval is { } ei)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(ei, TimeSpan.Zero, nameof(exportInterval));
        }

        if (timeout is { } to)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(to, TimeSpan.Zero, nameof(timeout));
        }

        var guard = EnsureOpenTelemetryGatewayConfiguration(
            builder,
            serviceName is not null,
            serviceVersion is not null);

        builder.WithEnvironment(context =>
        {
            context.EnvironmentVariables[OtelMetricsEnabledEnvVarName] = enabled ? "true" : "false";

            if (endpoint is not null)
            {
                context.EnvironmentVariables[OtelExporterOtlpMetricsEndpointEnvVarName] = endpoint;
            }

            if (exportInterval is { } interval)
            {
                context.EnvironmentVariables[OtelMetricExportIntervalEnvVarName] =
                    ((long)interval.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
            }

            if (timeout is { } timeoutValue)
            {
                context.EnvironmentVariables[OtelExporterOtlpMetricsTimeoutEnvVarName] =
                    ((long)timeoutValue.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
            }

            if (serviceName is not null)
            {
                context.EnvironmentVariables[OtelServiceNameEnvVarName] = serviceName;
            }

            if (serviceVersion is not null)
            {
                context.EnvironmentVariables[OtelServiceVersionEnvVarName] = serviceVersion;
            }
        });

        return builder;
    }

    /// <summary>
    /// Wires the gateway configuration override that keeps the OpenTelemetry environment
    /// variables authoritative on images whose <c>SetupConfiguration.json</c> pins telemetry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The override is a container entrypoint wrapper rather than an injected container file
    /// because only <c>entrypoint</c> and <c>args</c> round-trip through the Aspire manifest, so
    /// the same mechanism is emitted verbatim for run mode, the manifest/azd path, and every
    /// other publisher. The wrapper derives a copy of the configuration the container would have
    /// used, deletes only the keys this package's environment variables have to win over, points
    /// <c>CONFIG_DIR</c> at the copy, and execs the image's own entrypoint.
    /// </para>
    /// <para>
    /// It is wired whenever the API is called at all, not only when metrics end up enabled: a
    /// caller-supplied configuration file can enable metrics from JSON, and
    /// <c>enabled: false</c> has to beat that.
    /// </para>
    /// <para>
    /// Both halves of the wrapper are resolved lazily against the resource's final image, because
    /// the image tag is routinely selected after this method runs (for example
    /// <c>WithOpenTelemetryMetrics().WithDocumentDBVersion(...)</c>). The entrypoint is applied
    /// from <see cref="BeforeStartEvent"/>, which the manifest publisher raises before it
    /// serializes the resource — and which is the last phase that can still change the entrypoint
    /// a manifest carries, because the publisher writes <c>entrypoint</c> before it evaluates
    /// <c>args</c>. The arguments come from the terminal command-line guard
    /// (<see cref="EnsureTerminalGuard"/>), which is the last command-line callback the
    /// resource has and fails the resource if it is not.
    /// </para>
    /// </remarks>
    private static TerminalGuardAnnotation EnsureOpenTelemetryGatewayConfiguration(
        IResourceBuilder<DocumentDBServerResource> builder,
        bool serviceNameConfigured,
        bool serviceVersionConfigured)
    {
        var configuration = builder.Resource.Annotations
            .OfType<OpenTelemetryGatewayConfigurationAnnotation>()
            .SingleOrDefault();

        var firstCall = configuration is null;
        configuration ??= new OpenTelemetryGatewayConfigurationAnnotation();

        // The set of explicitly supplied identity parameters accumulates across calls, matching
        // how the environment variables those parameters write merge.
        configuration.ServiceNameConfigured |= serviceNameConfigured;
        configuration.ServiceVersionConfigured |= serviceVersionConfigured;

        if (!firstCall)
        {
            return EnsureTerminalGuard(builder);
        }

        builder.Resource.Annotations.Add(configuration);

        builder.ApplicationBuilder.Eventing.Subscribe<BeforeStartEvent>((_, _) =>
        {
            if (ResolveOpenTelemetryGatewayConfigurationRequirement(builder.Resource) !=
                GatewayConfigurationRequirement.Required)
            {
                return Task.CompletedTask;
            }

            if (configuration.EntrypointOwned)
            {
                // A later event must find the entrypoint this wrapper installed. Anything else
                // means the resource was re-pointed after the wrapper took ownership.
                if (!string.Equals(builder.Resource.Entrypoint, GatewayConfigurationShell, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"DocumentDB resource '{builder.Resource.Name}' replaced the container " +
                        $"entrypoint installed by WithOpenTelemetryMetrics() with " +
                        $"'{builder.Resource.Entrypoint ?? "<none>"}'. The OpenTelemetry " +
                        $"environment variables would be silently ignored on DocumentDB " +
                        $"v{FirstGatewayTelemetryConfigurationVersion} and later.");
                }

                return Task.CompletedTask;
            }

            if (builder.Resource.Entrypoint is { } callerEntrypoint)
            {
                throw new InvalidOperationException(
                    $"DocumentDB resource '{builder.Resource.Name}' sets the container " +
                    $"entrypoint to '{callerEntrypoint}', but WithOpenTelemetryMetrics() has " +
                    $"to own the entrypoint on DocumentDB " +
                    $"v{FirstGatewayTelemetryConfigurationVersion} and later. Those images " +
                    $"ship a SetupConfiguration.json whose telemetry values take precedence " +
                    $"over OTEL_* environment variables, so the metrics settings would be " +
                    $"silently ignored. Recovery: drop the custom entrypoint, or drop " +
                    $"WithOpenTelemetryMetrics() and configure telemetry from your own " +
                    $"entrypoint.");
            }

            builder.Resource.Entrypoint = GatewayConfigurationShell;
            configuration.EntrypointOwned = true;
            return Task.CompletedTask;
        });

        var guard = EnsureTerminalGuard(builder);

        guard.AddCommandLineStep(TerminalCommandLineOpenTelemetryWrapperRank, state =>
        {
            if (ResolveOpenTelemetryGatewayConfigurationRequirement(builder.Resource) !=
                GatewayConfigurationRequirement.Required)
            {
                return;
            }

            // The arguments are only meaningful to the entrypoint this wrapper installs. This step
            // runs from the resource's last command-line callback, after every event subscriber
            // and lifecycle hook, so it is the last point at which one that replaced the
            // entrypoint later in the same startup can still be caught - after which these
            // arguments would be spliced into someone else's command line.
            if (!configuration.EntrypointOwned ||
                !string.Equals(builder.Resource.Entrypoint, GatewayConfigurationShell, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"DocumentDB resource '{builder.Resource.Name}' resolved container arguments " +
                    $"with the container entrypoint set to " +
                    $"'{builder.Resource.Entrypoint ?? "<image default>"}' instead of the " +
                    $"'{GatewayConfigurationShell}' wrapper WithOpenTelemetryMetrics() installs. " +
                    $"On DocumentDB v{FirstGatewayTelemetryConfigurationVersion} and later that " +
                    $"wrapper is what makes the OTEL_* environment variables authoritative over " +
                    $"SetupConfiguration.json, and its arguments mean nothing to any other " +
                    $"entrypoint. Recovery: stop overriding the entrypoint of this resource - " +
                    $"including from a BeforeStartEvent subscriber or lifecycle hook - or drop " +
                    $"WithOpenTelemetryMetrics() and configure telemetry from your own " +
                    $"entrypoint.");
            }

            var script = BuildOpenTelemetryGatewayConfigurationScript(builder.Resource, configuration);

            state.Args.Insert(0, GatewayConfigurationShellArgumentZero);
            state.Args.Insert(0, script);
            state.Args.Insert(0, GatewayConfigurationShellCommandOption);
            state.WrapperScript = script;
        });

        guard.AddCommandLineValidation(state => ValidateOpenTelemetryGatewayCommand(builder.Resource, state));

        return guard;
    }

    /// <summary>
    /// Returns the resource's terminal guard, installing it the first time it is asked for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This package owns exactly one <see cref="CommandLineArgsCallbackAnnotation"/>, one
    /// <see cref="ContainerRuntimeArgsCallbackAnnotation"/>, and one
    /// <see cref="ManifestPublishingCallbackAnnotation"/> per resource. Everything it needs to do
    /// to the container command line is a step of that one command-line callback, ordered by rank,
    /// followed by the validations that judge the finished list. One callback per pipeline is what
    /// makes the contract expressible at all: Aspire evaluates callbacks in annotation order over
    /// one shared value, so "last" is a single position, and two package callbacks that each
    /// demanded it would be an impossible requirement rather than a check.
    /// </para>
    /// <para>
    /// Being last is established at every phase and then verified. The callbacks are appended when
    /// the API that needs them is called, and moved back to the end of their pipelines at
    /// <see cref="BeforeStartEvent"/> — which covers every builder-time <c>WithArgs</c> and
    /// <c>WithEnvironment</c>, whatever the call order, in run and publish mode alike — then again
    /// at <see cref="ResourceEndpointsAllocatedEvent"/> and
    /// <see cref="BeforeResourceStartedEvent"/>, the last per-resource phases a run publishes.
    /// Anything appended after that is caught by the checks themselves.
    /// </para>
    /// <para>
    /// Position alone is not enough, because Aspire caches each callback's result for the run. A
    /// caller who builds the resource's configuration through the public
    /// <see cref="ExecutionConfigurationBuilder"/> — from an
    /// <see cref="Lifecycle.IDistributedApplicationLifecycleHook"/>, say — and only then changes
    /// the model gets a validated answer recorded before the change and reused after it. That is
    /// what the seal is for: the command-line callback records what the resource looked like when
    /// it produced its result, including whether Aspire would shell-rewrite that result, and the
    /// two checkpoints Aspire never caches compare it.
    /// </para>
    /// <list type="bullet">
    /// <item><description>Run: the container-runtime-arguments callback. Aspire re-invokes those on
    /// every container creation without caching, and it does so after the last opportunity a
    /// caller has to change anything — a caller's own runtime-arguments callback — and before the
    /// container's command, arguments and environment are read. It also resolves the completed
    /// Docker/Podman runtime argument list once and rejects raw image operands, command,
    /// environment and mount overrides that bypass the resource model.</description></item>
    /// <item><description>Publish: the manifest callback itself. A publishing-pipeline prerequisite
    /// re-establishes it after every <see cref="Publishing.BeforePublishEvent"/> subscriber has
    /// completed, so a normal model event cannot replace or shadow the checkpoint; the callback
    /// then verifies while Aspire serializes the resource.</description></item>
    /// </list>
    /// <para>
    /// The annotation instances are moved, never re-created, so Aspire's per-callback result cache
    /// is untouched: the steps are evaluated exactly once per run and re-evaluated on restart,
    /// which is what keeps a deferred or secret-bearing value from being resolved twice.
    /// </para>
    /// </remarks>
    private static TerminalGuardAnnotation EnsureTerminalGuard(
        IResourceBuilder<DocumentDBServerResource> builder)
    {
        var resource = builder.Resource;

        if (resource.Annotations.OfType<TerminalGuardAnnotation>().LastOrDefault() is { } installed)
        {
            return installed;
        }

        var guard = new TerminalGuardAnnotation();

        guard.CommandLineCallback = new CommandLineArgsCallbackAnnotation(context =>
        {
            EnsureTerminalCallbackRunsLast(resource, guard.CommandLineCallback, "command-line");
            var state = guard.RunCommandLine(context);
            guard.Seal = CaptureTerminalConfigurationSeal(resource, state);
            return Task.CompletedTask;
        });

        guard.RuntimeCheckpoint = new ContainerRuntimeArgsCallbackAnnotation(async context =>
        {
            EnsureTerminalCallbackRunsLast(resource, guard.RuntimeCheckpoint, "container-runtime-arguments");
            VerifyTerminalConfigurationSeal(resource, guard);
            await ValidateOpenTelemetryContainerRuntimeArgumentsAsync(
                resource,
                context,
                builder.ApplicationBuilder.ExecutionContext).ConfigureAwait(false);
        });

        guard.ManifestCheckpoint = new ManifestPublishingCallbackAnnotation(async context =>
        {
            VerifyTerminalConfigurationSeal(resource, guard);

            // Whatever would have written this resource had the checkpoint not been installed
            // still writes it. A caller's custom manifest callback is honoured; with none, this is
            // the same container writer Aspire would have selected.
            var displaced = resource.Annotations.OfType<ManifestPublishingCallbackAnnotation>()
                .LastOrDefault(annotation => !ReferenceEquals(annotation, guard.ManifestCheckpoint));

            if (displaced is null)
            {
                await context.WriteContainerAsync(resource).ConfigureAwait(false);
            }
            else if (displaced.Callback is { } callback)
            {
                await callback(context).ConfigureAwait(false);
            }
        });

        resource.Annotations.Add(guard);
        resource.Annotations.Add(guard.CommandLineCallback);
        resource.Annotations.Add(guard.RuntimeCheckpoint);
        EstablishManifestCheckpoint(resource, guard);
        RegisterTerminalManifestCheckpoint(builder.ApplicationBuilder, resource, guard);

        void RetakeLastPosition() => RetakeTerminalGuardPositions(resource, guard);

        builder.ApplicationBuilder.Eventing.Subscribe<BeforeStartEvent>((_, _) =>
        {
            RetakeLastPosition();
            return Task.CompletedTask;
        });

        builder.ApplicationBuilder.Eventing.Subscribe<ResourceEndpointsAllocatedEvent>(resource, (_, _) =>
        {
            RetakeLastPosition();
            return Task.CompletedTask;
        });

        builder.ApplicationBuilder.Eventing.Subscribe<BeforeResourceStartedEvent>(resource, (_, _) =>
        {
            RetakeLastPosition();
            return Task.CompletedTask;
        });

        builder.ApplicationBuilder.Eventing.Subscribe<Publishing.BeforePublishEvent>((_, _) =>
        {
            // Retake after every lifecycle hook. A pipeline prerequisite repeats the manifest
            // retake after later subscribers to this same event have completed.
            RetakeLastPosition();
            VerifyTerminalConfigurationSeal(resource, guard);
            return Task.CompletedTask;
        });

        return guard;
    }

    /// <summary>
    /// Moves an annotation the resource already carries to the end of the collection, so it is the
    /// last of its kind when Aspire gathers it. The instance is preserved, so its cached result —
    /// and with it the guarantee of a single evaluation — is preserved too.
    /// </summary>
    private static void MoveToEnd(DocumentDBServerResource resource, IResourceAnnotation annotation)
    {
        if (!resource.Annotations.Remove(annotation))
        {
            return;
        }

        resource.Annotations.Add(annotation);
    }

    /// <summary>
    /// Puts both of the package's callbacks back at the end of their pipelines.
    /// </summary>
    /// <remarks>
    /// Called at every lifecycle phase, and again by any API that adds a callback of its own after
    /// the guard was installed, so the guard is last from the moment the model is written rather
    /// than only from the moment the application starts.
    /// </remarks>
    private static void RetakeTerminalGuardPositions(
        DocumentDBServerResource resource,
        TerminalGuardAnnotation guard)
    {
        MoveToEnd(resource, guard.CommandLineCallback);
        MoveToEnd(resource, guard.RuntimeCheckpoint);
        EstablishManifestCheckpoint(resource, guard);
    }

    /// <summary>
    /// Registers one application-wide publishing step that restores every DocumentDB manifest
    /// checkpoint after all model events and before Aspire's manifest writer runs.
    /// </summary>
    /// <remarks>
    /// <c>WithManifestPublishingCallback(...)</c> replaces the last callback through a supported
    /// public API. A subscriber registered after this package's
    /// <see cref="Publishing.BeforePublishEvent"/> subscriber can therefore remove the checkpoint
    /// after the event-level retake. Pipeline resolution happens only after every subscriber has
    /// completed, so this prerequisite closes that window without competing with the checker: it
    /// only restores callback ownership, and the single callback still performs the verification
    /// at serialization. <see cref="ResourceBuilderExtensions.ExcludeFromManifest{T}"/> remains the
    /// deliberate boundary; a resource that emits nothing has no published command to verify.
    /// An application that rewrites Aspire's publishing pipeline itself owns serialization order
    /// and is outside this resource-annotation contract; ordinary model events and manifest
    /// callback replacement are covered.
    /// </remarks>
    private static void RegisterTerminalManifestCheckpoint(
        IDistributedApplicationBuilder appBuilder,
        DocumentDBServerResource resource,
        TerminalGuardAnnotation guard)
    {
        if (!appBuilder.ExecutionContext.IsPublishMode)
        {
            return;
        }

        var registration = appBuilder.Services
            .Where(service => service.ServiceType == typeof(TerminalManifestCheckpointPipelineRegistration))
            .Select(service => service.ImplementationInstance)
            .OfType<TerminalManifestCheckpointPipelineRegistration>()
            .SingleOrDefault();

        if (registration is not null)
        {
            registration.Guards[resource] = guard;
            return;
        }

        registration = new TerminalManifestCheckpointPipelineRegistration();
        registration.Guards.Add(resource, guard);
        appBuilder.Services.AddSingleton(registration);

#pragma warning disable ASPIREPIPELINES001
        appBuilder.Pipeline.AddStep(
            TerminalManifestCheckpointPipelineStepName,
            _ =>
            {
                if (!registration.ManifestPublishing)
                {
                    return Task.CompletedTask;
                }

                foreach (var (registeredResource, registeredGuard) in registration.Guards)
                {
                    EstablishManifestCheckpoint(registeredResource, registeredGuard);
                }

                return Task.CompletedTask;
            });

        appBuilder.Pipeline.AddPipelineConfiguration(context =>
        {
            var manifestStep = context.Steps.SingleOrDefault(step =>
                string.Equals(step.Name, ManifestPublishingPipelineStepName, StringComparison.Ordinal));
            registration.ManifestPublishing = manifestStep is not null;

            if (manifestStep is not null &&
                !manifestStep.DependsOnSteps.Contains(
                    TerminalManifestCheckpointPipelineStepName,
                    StringComparer.Ordinal))
            {
                manifestStep.DependsOn(TerminalManifestCheckpointPipelineStepName);
            }

            return Task.CompletedTask;
        });
#pragma warning restore ASPIREPIPELINES001
    }

    private sealed class TerminalManifestCheckpointPipelineRegistration
    {
        public Dictionary<DocumentDBServerResource, TerminalGuardAnnotation> Guards { get; } =
            new(ReferenceEqualityComparer.Instance);

        public bool ManifestPublishing { get; set; }
    }

    /// <summary>
    /// Puts the guard's manifest checkpoint in the position Aspire reads — last — unless the
    /// resource is excluded from the manifest.
    /// </summary>
    /// <remarks>
    /// Aspire invokes only the last <see cref="ManifestPublishingCallbackAnnotation"/>. The
    /// checkpoint therefore delegates to the callback it displaced. A callback-less annotation is
    /// <c>ExcludeFromManifest()</c>; it intentionally wins, so the resource stays absent and the
    /// checkpoint is removed rather than turning an exclusion back into output.
    /// </remarks>
    private static void EstablishManifestCheckpoint(
        DocumentDBServerResource resource,
        TerminalGuardAnnotation guard)
    {
        var displaced = resource.Annotations.OfType<ManifestPublishingCallbackAnnotation>()
            .LastOrDefault(annotation => !ReferenceEquals(annotation, guard.ManifestCheckpoint));

        resource.Annotations.Remove(guard.ManifestCheckpoint);

        if (displaced is not null && displaced.Callback is null)
        {
            return;
        }

        resource.Annotations.Add(guard.ManifestCheckpoint);
    }

    /// <summary>
    /// Records what the container's command depends on, at the moment the command-line callback
    /// produced the result Aspire will reuse for the rest of the run.
    /// </summary>
    private static TerminalConfigurationSeal CaptureTerminalConfigurationSeal(
        DocumentDBServerResource resource,
        TerminalCommandLineState state) =>
        new(
            [.. resource.Annotations.OfType<CommandLineArgsCallbackAnnotation>()],
            [.. resource.Annotations.OfType<EnvironmentCallbackAnnotation>()],
            [.. resource.Annotations.OfType<ContainerRuntimeArgsCallbackAnnotation>()],
            resource.Entrypoint,
            resource.ShellExecution is true,
            ResolveEffectiveImage(resource),
            CaptureTerminalCommandSeal(resource, state));

    private static TerminalCommandSeal CaptureTerminalCommandSeal(
        DocumentDBServerResource resource,
        TerminalCommandLineState state)
    {
        var requirement = ResolveOpenTelemetryGatewayConfigurationRequirement(resource);

        if (state.WrapperScript is not { } script)
        {
            return new(requirement, null, null, false, null, false);
        }

        var args = state.Args;

        return new(
            requirement,
            script,
            args.Count > 0 ? args[0] as string : null,
            args.Count > 1 && ReferenceEquals(args[1], script),
            args.Count > 2 ? args[2] as string : null,
            args.Skip(3).OfType<string>().Any(argument =>
                string.Equals(argument, script, StringComparison.Ordinal)));
    }

    /// <summary>
    /// Fails the resource when anything the container's command depends on changed after the
    /// command-line callback produced the answer Aspire cached.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called from the two uncached checkpoints the integration owns: the
    /// container-runtime-arguments callback in a run and the manifest publishing callback while a
    /// publish serializes the resource. Together they cover every supported way to change the model
    /// after the command line has been decided: appending or inserting a callback in either
    /// pipeline, re-pointing the entrypoint, enabling Aspire's shell rewrite, and swapping the
    /// image, tag, digest or Dockerfile.
    /// </para>
    /// <para>
    /// Nothing is repaired. Re-running the wrapper is not an option — Aspire would keep the cached
    /// result anyway — and starting a container on an answer that has since been contradicted is
    /// the failure this exists to prevent.
    /// </para>
    /// <para>
    /// No value is reported, only what kind of thing changed: the callback that changed the model
    /// may well be the one carrying a secret.
    /// </para>
    /// </remarks>
    private static void VerifyTerminalConfigurationSeal(
        DocumentDBServerResource resource,
        TerminalGuardAnnotation guard)
    {
        EnsureTerminalCallbackRunsLast(resource, guard.CommandLineCallback, "command-line");
        EnsureTerminalCallbackRunsLast(resource, guard.RuntimeCheckpoint, "container-runtime-arguments");

        if (guard.Seal is not { } seal)
        {
            ValidateOpenTelemetryShellExecution(resource);
            return;
        }

        if (!SameCallbacks(
            [.. resource.Annotations.OfType<CommandLineArgsCallbackAnnotation>()],
            seal.CommandLineCallbacks))
        {
            throw StaleConfiguration(resource, "a command-line callback was added or removed");
        }

        if (!SameCallbacks(
            [.. resource.Annotations.OfType<EnvironmentCallbackAnnotation>()],
            seal.EnvironmentCallbacks))
        {
            throw StaleConfiguration(resource, "an environment callback was added or removed");
        }

        if (!SameCallbacks(
            [.. resource.Annotations.OfType<ContainerRuntimeArgsCallbackAnnotation>()],
            seal.RuntimeCallbacks))
        {
            throw StaleConfiguration(resource, "a container-runtime-argument callback was added or removed");
        }

        if (!string.Equals(resource.Entrypoint, seal.Entrypoint, StringComparison.Ordinal))
        {
            throw StaleConfiguration(resource, "its container entrypoint changed");
        }

        if (seal.Command.GatewayRequirement == GatewayConfigurationRequirement.Required &&
            (resource.ShellExecution is true) != seal.ShellExecutionEnabled)
        {
            ValidateOpenTelemetryShellExecution(resource);
            throw StaleConfiguration(
                resource,
                "whether Aspire shell-rewrites its container arguments changed");
        }

        if (!ResolveEffectiveImage(resource).Equals(seal.Image))
        {
            throw StaleConfiguration(resource, "the image it will run changed");
        }

        ValidateOpenTelemetryShellExecution(resource);
        VerifyTerminalCommandSeal(resource, seal.Command);
    }

    /// <summary>
    /// Re-checks the load-bearing fixed prefix in the immutable command result against the model
    /// that is about to be serialized.
    /// </summary>
    /// <remarks>
    /// Aspire freezes a callback result as an immutable list. Once callback membership is
    /// unchanged, the caller arguments cannot rewrite the recorded prefix behind this check, so
    /// only the fixed tokens and script need to be retained; caller values are neither stored nor
    /// resolved.
    /// </remarks>
    private static void VerifyTerminalCommandSeal(
        DocumentDBServerResource resource,
        TerminalCommandSeal command)
    {
        var requirement = ResolveOpenTelemetryGatewayConfigurationRequirement(resource);

        if (requirement != command.GatewayRequirement)
        {
            throw StaleConfiguration(
                resource,
                "whether its cached command needs the OpenTelemetry compatibility wrapper changed");
        }

        if (requirement != GatewayConfigurationRequirement.Required)
        {
            if (command.WrapperScript is not null)
            {
                throw StaleConfiguration(
                    resource,
                    "its cached command carries an OpenTelemetry compatibility wrapper that is no longer applicable");
            }

            return;
        }

        var configuration = resource.Annotations
            .OfType<OpenTelemetryGatewayConfigurationAnnotation>()
            .Single();
        var expectedScript = BuildOpenTelemetryGatewayConfigurationScript(resource, configuration);

        if (command.WrapperScript is null ||
            !string.Equals(command.ShellOption, GatewayConfigurationShellCommandOption, StringComparison.Ordinal) ||
            !command.ScriptIsSecondArgument ||
            !string.Equals(command.Delimiter, GatewayConfigurationShellArgumentZero, StringComparison.Ordinal) ||
            command.HasDuplicateWrapperScript ||
            !string.Equals(command.WrapperScript, expectedScript, StringComparison.Ordinal))
        {
            throw StaleConfiguration(
                resource,
                "its cached OpenTelemetry wrapper prefix, script or delimiter no longer matches the terminal command");
        }
    }

    /// <summary>
    /// Compares two recordings of one pipeline by membership rather than by order.
    /// </summary>
    /// <remarks>
    /// What the seal has to detect is a callback appearing or disappearing, because the app host
    /// evaluates each one at most once and then reuses the recorded result: a callback added after
    /// the recording runs unrecorded, and for arguments its result replaces the whole list. Order
    /// is deliberately not compared. This package moves its own callbacks to the end of their
    /// pipelines at every phase, so an order comparison would report the guard's own repositioning
    /// as a change; ordering is guaranteed instead by
    /// <see cref="EnsureTerminalCallbackRunsLast{TAnnotation}"/>, which puts this package last, and
    /// with it last the recorded result that Aspire keeps.
    /// </remarks>
    private static bool SameCallbacks<TAnnotation>(
        System.Collections.Immutable.ImmutableArray<TAnnotation> current,
        System.Collections.Immutable.ImmutableArray<TAnnotation> sealed_)
        where TAnnotation : class, IResourceAnnotation
    {
        if (current.Length != sealed_.Length)
        {
            return false;
        }

        // Identity, not equality: two annotations can be indistinguishable by value and still be
        // two separate recordings.
        var recorded = new HashSet<object>(sealed_, ReferenceEqualityComparer.Instance);

        return current.All(annotation => recorded.Contains(annotation));
    }

    private static InvalidOperationException StaleConfiguration(
        DocumentDBServerResource resource,
        string change) =>
        new(
            $"DocumentDB resource '{resource.Name}' was changed after its container command line " +
            $"had already been built: {change}. Aspire records each callback's result the first " +
            $"time it runs and reuses it for the rest of the run, so a configuration built before " +
            $"the change is the one the container would receive, and the checks that ran with it " +
            $"cannot be repeated. This usually comes from building the resource's configuration " +
            $"early — ExecutionConfigurationBuilder or GetArgumentValuesAsync from an " +
            $"IDistributedApplicationLifecycleHook or an event subscriber — and then changing the " +
            $"resource. The resource is failed instead of being started or published on a command " +
            $"line that was decided and then contradicted. Recovery: finish configuring the " +
            $"resource before anything reads its configuration, or make the change part of the " +
            $"application model (WithArgs(...), WithEnvironment(...), WithImageTag(...)) while it " +
            $"is being built.");

    /// <summary>
    /// Fails the resource when something appended a callback after the one this package owns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every rule this package applies is about the configuration the container receives, and a
    /// callback that runs afterwards can prepend to it, clear it, or reorder it. The wrapper is
    /// the sharpest case: <c>/bin/bash</c> reads its command from the first arguments, so a single
    /// value inserted in front turns the whole wrapper into an operand and the container starts
    /// nothing.
    /// </para>
    /// <para>
    /// That is reported rather than tolerated, and no value appears in the message: the point is
    /// the shape of the pipeline, and the callback that ran last may well be carrying a secret.
    /// </para>
    /// </remarks>
    private static void EnsureTerminalCallbackRunsLast<TAnnotation>(
        DocumentDBServerResource resource,
        TAnnotation guardCallback,
        string pipeline)
        where TAnnotation : class, IResourceAnnotation
    {
        var last = resource.Annotations.OfType<TAnnotation>().LastOrDefault();
        if (ReferenceEquals(last, guardCallback))
        {
            return;
        }

        throw new InvalidOperationException(
            $"DocumentDB resource '{resource.Name}' has a later {pipeline} callback registered " +
            $"after the one this package owns, so the configuration it built is not the one the " +
            $"container would receive. That callback is appended when the application starts and " +
            $"moved back to the end of the pipeline at the latest per-resource phase the run " +
            $"offers; a callback added after that usually comes from a subscriber registered " +
            $"after AddDocumentDB, or from an IDistributedApplicationLifecycleHook. The resource " +
            $"is failed instead of being started on a configuration that was checked and then " +
            $"changed. Recovery: make that configuration part of the application model " +
            $"(WithArgs(...), WithEnvironment(...)) while it is being built, or register the " +
            $"subscriber before AddDocumentDB.");
    }

    /// <summary>
    /// Refuses Aspire's post-validation shell rewrite when the compatibility wrapper owns the
    /// command. DCP applies this switch after it gathers the verified arguments.
    /// </summary>
    private static void ValidateOpenTelemetryShellExecution(DocumentDBServerResource resource)
    {
        if (resource.ShellExecution is not true ||
            ResolveOpenTelemetryGatewayConfigurationRequirement(resource) !=
                GatewayConfigurationRequirement.Required)
        {
            return;
        }

        throw new InvalidOperationException(
            $"DocumentDB resource '{resource.Name}' enables ContainerResource.ShellExecution " +
            $"while the WithOpenTelemetryMetrics() compatibility wrapper is required. Aspire " +
            $"rewrites the already verified wrapper arguments into one joined '-c' command after " +
            $"the terminal check, so '/bin/bash' receives a nested shell command and DocumentDB " +
            $"does not start. Set ShellExecution to false or null, or drop " +
            $"WithOpenTelemetryMetrics() and configure telemetry from your own shell command.");
    }

    /// <summary>
    /// Verifies that the finished command line is exactly the one the gateway configuration
    /// wrapper needs, whenever the wrapper is required at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The wrapper is a <c>/bin/bash -c &lt;script&gt; -- &lt;image arguments&gt;</c> command line,
    /// and every part of that shape is load-bearing: bash reads the script from the value after
    /// <c>-c</c> and assigns the values after <c>--</c> to <c>$@</c>, which is what the script
    /// forwards to the image's own entrypoint. Anything in front of <c>-c</c> is a bash option or
    /// operand instead, a different script is a different container, and a second copy of the
    /// prefix would be handed to the image as an argument.
    /// </para>
    /// <para>
    /// The image is classified again here rather than trusted from the step that applied the
    /// wrapper, so an image, tag, digest or Dockerfile selected in between is judged on what the
    /// container will actually run.
    /// </para>
    /// <para>
    /// Nothing but the fixed tokens this package wrote is compared or reported. Caller arguments
    /// are counted, never read: one of them may be a parameter or an expression whose value is a
    /// secret, and resolving it here would both duplicate Aspire's own evaluation and risk putting
    /// it in an exception message.
    /// </para>
    /// </remarks>
    private static void ValidateOpenTelemetryGatewayCommand(
        DocumentDBServerResource resource,
        TerminalCommandLineState state)
    {
        ValidateOpenTelemetryShellExecution(resource);

        var required = ResolveOpenTelemetryGatewayConfigurationRequirement(resource) ==
            GatewayConfigurationRequirement.Required;

        if (!required)
        {
            if (state.WrapperScript is null)
            {
                return;
            }

            throw new InvalidOperationException(
                $"DocumentDB resource '{resource.Name}' stopped needing the " +
                $"WithOpenTelemetryMetrics() compatibility wrapper while its container command " +
                $"line was being built, after that wrapper had already been written into it. " +
                $"Select the image before configuring metrics.");
        }

        if (state.WrapperScript is not { } script)
        {
            throw new InvalidOperationException(
                $"DocumentDB resource '{resource.Name}' needs the WithOpenTelemetryMetrics() " +
                $"compatibility wrapper on its container command line, but the finished command " +
                $"line does not carry it. On DocumentDB " +
                $"v{FirstGatewayTelemetryConfigurationVersion} and later the OTEL_* environment " +
                $"variables would be silently ignored. Recovery: select the image before " +
                $"configuring metrics.");
        }

        if (!string.Equals(resource.Entrypoint, GatewayConfigurationShell, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"DocumentDB resource '{resource.Name}' finished building its container command " +
                $"line with the entrypoint set to '{resource.Entrypoint ?? "<image default>"}' " +
                $"instead of the '{GatewayConfigurationShell}' wrapper " +
                $"WithOpenTelemetryMetrics() installs. Those arguments mean nothing to any other " +
                $"entrypoint. Recovery: stop overriding the entrypoint of this resource - " +
                $"including from a BeforeStartEvent subscriber or lifecycle hook - or drop " +
                $"WithOpenTelemetryMetrics() and configure telemetry from your own entrypoint.");
        }

        var args = state.Args;

        var intact = args.Count >= 3
            && args[0] is string option
            && string.Equals(option, GatewayConfigurationShellCommandOption, StringComparison.Ordinal)
            && ReferenceEquals(args[1], script)
            && args[2] is string delimiter
            && string.Equals(delimiter, GatewayConfigurationShellArgumentZero, StringComparison.Ordinal);

        if (!intact)
        {
            throw new InvalidOperationException(
                $"DocumentDB resource '{resource.Name}' finished building a container command " +
                $"line that does not start with the " +
                $"'{GatewayConfigurationShellCommandOption} <script> " +
                $"{GatewayConfigurationShellArgumentZero}' prefix " +
                $"WithOpenTelemetryMetrics() installs; {args.Count} argument(s) were built. " +
                $"'{GatewayConfigurationShell}' reads its command from those first arguments, so " +
                $"anything placed in front of them, a cleared or reordered list, or a replaced " +
                $"script or '{GatewayConfigurationShellArgumentZero}' delimiter leaves a " +
                $"container that does not start DocumentDB. Recovery: add container arguments " +
                $"with WithArgs(...), which appends them after the wrapper, rather than by " +
                $"rewriting the argument list in place.");
        }

        for (var index = 3; index < args.Count; index++)
        {
            if (args[index] is string duplicate &&
                string.Equals(duplicate, script, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"DocumentDB resource '{resource.Name}' finished building a container " +
                    $"command line carrying the WithOpenTelemetryMetrics() wrapper script more " +
                    $"than once. Only the first copy is the command " +
                    $"'{GatewayConfigurationShell}' runs; the rest are passed to DocumentDB as " +
                    $"arguments. Recovery: configure metrics on this resource through " +
                    $"WithOpenTelemetryMetrics() alone, and do not copy its arguments.");
            }
        }
    }

    /// <summary>
    /// Classifies whether <paramref name="resource"/> needs the gateway configuration wrapper.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The affected range is open-ended from
    /// <see cref="FirstGatewayTelemetryConfigurationVersion"/>: the JSON-over-environment
    /// precedence arrived in that release, it is the newest published DocumentDB version, and
    /// nothing upstream retracts it. Tags outside the strict <c>pg{NN}-X.Y.Z</c> grammar and
    /// images other than the official one are exempt, so forks and custom images keep the stock
    /// behaviour. So is any resource built from the caller's own Dockerfile, whose image
    /// annotation describes the build at best and may name the official image exactly. Private
    /// mirrors of the official image are not exempt, because only the registry differs.
    /// </para>
    /// <para>
    /// A digest pin on the official image makes the version opaque - the runtime resolves the
    /// image from the digest and ignores the tag, so a tag left over from an earlier call says
    /// nothing about what will actually run. Guessing in either direction is silently wrong, so
    /// this throws instead.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The official image is pinned by digest while the metrics API is configured.
    /// </exception>
    private static GatewayConfigurationRequirement ResolveOpenTelemetryGatewayConfigurationRequirement(
        DocumentDBServerResource resource)
    {
        var configuration = resource.Annotations
            .OfType<OpenTelemetryGatewayConfigurationAnnotation>()
            .SingleOrDefault();

        if (configuration is null)
        {
            return GatewayConfigurationRequirement.NotConfigured;
        }

        var image = ResolveEffectiveImage(resource);

        // A caller-owned Dockerfile build lands here with the custom images: it is not the
        // official image however its annotations read, and every part of the wrapper - the
        // entrypoint script path, the packaged configuration layout, bash and jq - is a property
        // of the official image that this package has not established for a build it did not
        // produce. Stated as the complement so that every origin naming the curated repository -
        // including a digest pin, which has to reach the rejection below rather than be skipped -
        // falls through.
        if (image.Origin is DocumentDBImageOrigin.None
            or DocumentDBImageOrigin.DockerfileBuild
            or DocumentDBImageOrigin.CustomRepository)
        {
            return NotRequired(configuration, resource, image);
        }

        if (!string.IsNullOrEmpty(image.Digest))
        {
            throw new InvalidOperationException(
                $"DocumentDB resource '{resource.Name}' pins " +
                $"{DocumentDBContainerImageTags.Image} by digest '{image.Digest}', so its " +
                $"DocumentDB version cannot be determined and the tag " +
                $"'{image.Tag ?? "<none>"}' is not what the runtime resolves. " +
                $"WithOpenTelemetryMetrics() needs the version because DocumentDB " +
                $"v{FirstGatewayTelemetryConfigurationVersion} and later give " +
                $"SetupConfiguration.json precedence over the OTEL_* environment variables, and " +
                $"applying or skipping the compatibility wrapper on a guess is silently wrong " +
                $"either way. Recovery: select the image by tag instead of by digest, or drop " +
                $"WithOpenTelemetryMetrics() and configure telemetry inside the image the digest " +
                $"names.");
        }

        if (image.KnownVersion is not { } version)
        {
            return NotRequired(configuration, resource, image);
        }

        return version >= FirstGatewayTelemetryConfigurationVersion
            ? GatewayConfigurationRequirement.Required
            : NotRequired(configuration, resource, image);

        // The wrapper cannot be uninstalled once the entrypoint carries it: an image swapped in
        // after installation would leave /bin/bash with no arguments, which starts nothing.
        static GatewayConfigurationRequirement NotRequired(
            OpenTelemetryGatewayConfigurationAnnotation configuration,
            DocumentDBServerResource resource,
            DocumentDBEffectiveImage image)
        {
            if (configuration.EntrypointOwned)
            {
                var description = image.Origin == DocumentDBImageOrigin.DockerfileBuild
                    ? $"a Dockerfile build (annotated '{image.Tag ?? "<none>"}')"
                    : $"an image ('{image.Tag ?? "<none>"}')";

                throw new InvalidOperationException(
                    $"DocumentDB resource '{resource.Name}' changed to {description} that does " +
                    $"not need the WithOpenTelemetryMetrics() compatibility wrapper after that " +
                    $"wrapper had already taken over the container entrypoint. Select the image " +
                    $"before configuring metrics.");
            }

            return GatewayConfigurationRequirement.NotApplicable;
        }
    }


    /// <summary>
    /// Configures the PostgreSQL owner role used by the DocumentDB Local container.
    /// </summary>
    /// <remarks>
    /// The bundled PostgreSQL instance creates the default <c>documentdb</c> role. A custom value
    /// must name a role that already exists, such as the owner of an externally managed
    /// PostgreSQL instance. DocumentDB <c>0.116.0</c> aborts explicitly while creating the
    /// DocumentDB admin user when the configured role does not exist. Earlier images also fail
    /// startup, but only later while waiting for the gateway to start.
    /// </remarks>
    /// <param name="builder">The resource builder for DocumentDB.</param>
    /// <param name="owner">The existing PostgreSQL role used for DocumentDB database operations.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<DocumentDBServerResource> WithOwner(this IResourceBuilder<DocumentDBServerResource> builder, string owner)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(owner);

        return builder.WithEnvironment(context =>
        {
            context.EnvironmentVariables[OwnerEnvVarName] = owner;
        });
    }

    /// <summary>
    /// Enables TLS for the DocumentDB connection string. TLS is enabled by default
    /// because the DocumentDB Local container serves TLS on its gateway port using a
    /// self-signed certificate.
    /// Call <c>UseTls(false)</c> to disable TLS if connecting to a non-TLS endpoint.
    /// </summary>
    /// <remarks>
    /// From DocumentDB <c>0.114.0</c> the container's default <c>TLS_MODE=allowTLS</c> accepts
    /// both plain and TLS connections, so <c>UseTls(false)</c> works against the default image.
    /// Container images up to and including <c>0.113.0</c> rejected plain connections regardless
    /// of that setting. Set <c>.WithEnvironment("TLS_MODE", "requireTLS")</c> to make the
    /// container reject plain connections; combining that with <c>UseTls(false)</c> is
    /// self-contradictory and connections will fail.
    /// </remarks>
    /// <param name="builder">The resource builder for DocumentDB.</param>
    /// <param name="useTls">Whether to enable TLS. Defaults to <see langword="true"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <example>
    /// <code>
    /// // Disable TLS for a non-TLS endpoint:
    /// var server = builder.AddDocumentDB("documentdb")
    ///                     .UseTls(false);
    /// </code>
    /// </example>
    public static IResourceBuilder<DocumentDBServerResource> UseTls(this IResourceBuilder<DocumentDBServerResource> builder, bool useTls = true)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.SetUseTls(useTls);
        return builder;
    }

    /// <summary>
    /// Allows insecure TLS connections by adding <c>tlsInsecure=true</c> to the connection string.
    /// This is enabled by default so the .NET MongoDB driver can connect to the self-signed
    /// certificate used by the DocumentDB Local container.
    /// Call <c>AllowInsecureTls(false)</c> to require valid certificates.
    /// </summary>
    /// <remarks>
    /// The extension uses <c>tlsInsecure=true</c> rather than <c>tlsAllowInvalidCertificates=true</c>
    /// because the .NET MongoDB driver does not fully honor <c>tlsAllowInvalidCertificates</c> for
    /// self-signed certificates and raises <c>UntrustedRoot</c> errors.
    /// </remarks>
    /// <param name="builder">The resource builder for DocumentDB.</param>
    /// <param name="allowInsecureTls">Whether to allow insecure TLS. Defaults to <see langword="true"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <example>
    /// <code>
    /// // Require valid certificates (for example, production with real certs):
    /// var server = builder.AddDocumentDB("documentdb")
    ///                     .AllowInsecureTls(false);
    /// </code>
    /// </example>
    public static IResourceBuilder<DocumentDBServerResource> AllowInsecureTls(this IResourceBuilder<DocumentDBServerResource> builder, bool allowInsecureTls = true)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.SetAllowInsecureTls(allowInsecureTls);
        return builder;
    }

    /// <summary>
    /// Pins the DocumentDB version to a specific release known to this build of the package.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The selected version is combined with the currently selected
    /// <see cref="DocumentDBPostgresVersion"/> (default <see cref="DocumentDBPostgresVersion.Pg17"/>)
    /// to produce the container image tag <c>pgN-X.Y.Z</c>.
    /// </para>
    /// <para>
    /// <b>Precedence:</b> for the image tag, the most recent of <see cref="WithDocumentDBVersion"/>,
    /// <see cref="WithPostgresVersion"/>,
    /// <see cref="ContainerResourceBuilderExtensions.WithImage{T}(IResourceBuilder{T}, string, string?)"/>,
    /// and <see cref="ContainerResourceBuilderExtensions.WithImageTag{T}(IResourceBuilder{T}, string)"/>
    /// wins. They all converge on the same single <see cref="ContainerImageAnnotation"/>.
    /// </para>
    /// <para>
    /// This method updates only the image tag. A custom image name or registry configured with
    /// <see cref="ContainerResourceBuilderExtensions.WithImage{T}(IResourceBuilder{T}, string, string?)"/>
    /// or <see cref="ContainerResourceBuilderExtensions.WithImageRegistry{T}(IResourceBuilder{T}, string)"/>
    /// is preserved.
    /// </para>
    /// <para>
    /// To pin to a version not in <see cref="DocumentDBVersion"/> (for example, a brand-new
    /// upstream release this package has not yet been updated to know about), use
    /// <see cref="ContainerResourceBuilderExtensions.WithImageTag{T}(IResourceBuilder{T}, string)"/>
    /// directly with a tag like <c>"pg17-0.999.0"</c>.
    /// </para>
    /// </remarks>
    /// <param name="builder">The resource builder for DocumentDB.</param>
    /// <param name="version">The DocumentDB version to use.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <example>
    /// <code>
    /// var server = builder.AddDocumentDB("documentdb")
    ///                     .WithDocumentDBVersion(DocumentDBVersion.V0_110_0);
    /// </code>
    /// </example>
    public static IResourceBuilder<DocumentDBServerResource> WithDocumentDBVersion(
        this IResourceBuilder<DocumentDBServerResource> builder,
        DocumentDBVersion version)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.SetVersion(version);
        return builder.WithImageTag(builder.Resource.ComputeImageTag());
    }

    /// <summary>
    /// Selects the PostgreSQL backend variant of the <c>documentdb-local</c> container image.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The selected variant is combined with the currently selected
    /// <see cref="DocumentDBVersion"/> (or <see cref="DocumentDBVersions.Latest"/> by default)
    /// to produce the container image tag <c>pgN-X.Y.Z</c>.
    /// </para>
    /// <para>
    /// <b>Precedence:</b> see <see cref="WithDocumentDBVersion"/> — last call wins.
    /// </para>
    /// </remarks>
    /// <param name="builder">The resource builder for DocumentDB.</param>
    /// <param name="pgVersion">The PostgreSQL backend variant to use.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="pgVersion"/> is not a defined member of
    /// <see cref="DocumentDBPostgresVersion"/>. Use a free-form
    /// <see cref="ContainerResourceBuilderExtensions.WithImageTag{T}(IResourceBuilder{T}, string)"/>
    /// call to target an unsupported PG variant.
    /// </exception>
    /// <example>
    /// <code>
    /// var server = builder.AddDocumentDB("documentdb")
    ///                     .WithPostgresVersion(DocumentDBPostgresVersion.Pg16)
    ///                     .WithDocumentDBVersion(DocumentDBVersion.V0_110_0);
    /// // -&gt; image tag "pg16-0.110.0"
    /// </code>
    /// </example>
    public static IResourceBuilder<DocumentDBServerResource> WithPostgresVersion(
        this IResourceBuilder<DocumentDBServerResource> builder,
        DocumentDBPostgresVersion pgVersion)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (!Enum.IsDefined(pgVersion))
        {
            throw new ArgumentOutOfRangeException(
                nameof(pgVersion),
                pgVersion,
                $"Unsupported PostgreSQL backend variant '{pgVersion}'. " +
                $"Use one of {nameof(DocumentDBPostgresVersion.Pg15)}, " +
                $"{nameof(DocumentDBPostgresVersion.Pg16)}, " +
                $"{nameof(DocumentDBPostgresVersion.Pg17)}, or " +
                $"{nameof(DocumentDBPostgresVersion.Pg18)}, or fall back to a free-form " +
                $"WithImageTag(...) for unsupported variants.");
        }

        builder.Resource.SetPgVersion(pgVersion);
        return builder.WithImageTag(builder.Resource.ComputeImageTag());
    }

    private static string GetMountedFilePath(string source, string paramName, string prefix)
    {
        var fileName = Path.GetFileName(source);

        if (string.IsNullOrEmpty(fileName))
        {
            throw new ArgumentException("The path must include a file name.", paramName);
        }

        return $"/{prefix}{fileName}";
    }
}
