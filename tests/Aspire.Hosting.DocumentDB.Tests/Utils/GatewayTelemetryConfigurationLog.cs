// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Xunit;

namespace Aspire.Hosting.DocumentDB.Tests;

/// <summary>
/// Reads the telemetry section of the gateway's "Starting server with configuration" log line.
/// </summary>
/// <remarks>
/// The gateway prints that section as a Rust <c>Debug</c> value, and its shape depends on the image:
/// <c>0.116.0</c> prints a typed struct (<c>telemetry_options: Some(TelemetryOptions { service_name:
/// Some("x"), ..., metrics: None, tracing: Some(TracingOptions { enabled: Some(false), ...</c>),
/// while <c>0.117.0</c> prints the raw JSON it read (<c>telemetry_provider_options: Some(Object
/// {"ServiceName": String("x"), "Tracing": Object {"Enabled": Bool(false), ...</c>). Both carry the
/// same facts, so tests ask for the facts rather than matching either spelling.
/// </remarks>
internal sealed partial class GatewayTelemetryConfigurationLog
{
    private readonly string _section;

    private GatewayTelemetryConfigurationLog(string section) => _section = section;

    public static GatewayTelemetryConfigurationLog Parse(string logs)
    {
        var match = SectionRegex().Match(logs);
        Assert.True(match.Success, $"No gateway telemetry configuration was logged:{Environment.NewLine}{logs}");
        return new GatewayTelemetryConfigurationLog(match.Value);
    }

    /// <summary>Whether the configuration file still carries a <c>Metrics</c> object.</summary>
    public bool HasMetricsSection =>
        _section.Contains("metrics: Some(", StringComparison.Ordinal) ||
        _section.Contains("\"Metrics\":", StringComparison.Ordinal);

    public string? ServiceName => ReadString(TypedServiceNameRegex(), JsonServiceNameRegex());

    public string? ServiceVersion => ReadString(TypedServiceVersionRegex(), JsonServiceVersionRegex());

    /// <summary>The configuration file's <c>Tracing.Enabled</c>, or <see langword="null"/> when absent.</summary>
    public bool? TracingEnabled
    {
        get
        {
            var value = ReadString(TypedTracingEnabledRegex(), JsonTracingEnabledRegex());
            return value is null ? null : bool.Parse(value);
        }
    }

    public override string ToString() => _section;

    private string? ReadString(Regex typed, Regex json)
    {
        var match = typed.Match(_section);
        if (!match.Success)
        {
            match = json.Match(_section);
        }

        return match.Success ? match.Groups["v"].Value : null;
    }

    // Must end at the field that follows telemetry in both versions, so a truncated line fails to
    // parse instead of reading as a configuration with every setting removed.
    [GeneratedRegex(@"telemetry(?:_provider)?_options: (?:None|Some\([^\r\n]*?\))(?=, telemetry_settings:|, enable_pg_file_settings_refresh:)")]
    private static partial Regex SectionRegex();

    [GeneratedRegex(@"service_name: Some\(""(?<v>[^""]*)""\)")]
    private static partial Regex TypedServiceNameRegex();

    [GeneratedRegex(@"""ServiceName"": String\(""(?<v>[^""]*)""\)")]
    private static partial Regex JsonServiceNameRegex();

    [GeneratedRegex(@"service_version: Some\(""(?<v>[^""]*)""\)")]
    private static partial Regex TypedServiceVersionRegex();

    [GeneratedRegex(@"""ServiceVersion"": String\(""(?<v>[^""]*)""\)")]
    private static partial Regex JsonServiceVersionRegex();

    [GeneratedRegex(@"tracing: Some\(TracingOptions \{ enabled: Some\((?<v>true|false)\)")]
    private static partial Regex TypedTracingEnabledRegex();

    [GeneratedRegex(@"""Tracing"": Object \{""Enabled"": Bool\((?<v>true|false)\)")]
    private static partial Regex JsonTracingEnabledRegex();
}
