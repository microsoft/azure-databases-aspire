// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Xunit;

namespace Aspire.Hosting.DocumentDB.Tests;

/// <summary>
/// Reads the telemetry section of the gateway's "Starting server with configuration" log line.
/// </summary>
/// <remarks>
/// From <c>0.117.0</c> the gateway prints the JSON it read as a Rust <c>Debug</c> value:
/// <c>telemetry_provider_options: Some(Object {"ServiceName": String("x"), "Tracing": Object
/// {"Enabled": Bool(false), ...}}), telemetry_settings: ...</c>.
/// </remarks>
internal sealed partial class GatewayTelemetryConfigurationLog
{
    private readonly string _section;

    private GatewayTelemetryConfigurationLog(string section) => _section = section;

    public static GatewayTelemetryConfigurationLog Parse(string logs)
    {
        var match = SectionRegex().Match(logs);
        Assert.True(match.Success, $"No complete gateway telemetry configuration was logged:{Environment.NewLine}{logs}");
        return new GatewayTelemetryConfigurationLog(match.Value);
    }

    public bool HasMetricsSection => _section.Contains("\"Metrics\":", StringComparison.Ordinal);

    public string? ServiceName => Read(ServiceNameRegex());

    public string? ServiceVersion => Read(ServiceVersionRegex());

    public bool? TracingEnabled => Read(TracingEnabledRegex()) is { } value ? bool.Parse(value) : null;

    public override string ToString() => _section;

    private string? Read(Regex regex) => regex.Match(_section) is { Success: true } match ? match.Groups["v"].Value : null;

    // Must end at the next field, so a truncated line fails to parse instead of reading as a
    // configuration with every setting removed.
    [GeneratedRegex(@"telemetry_provider_options: (?:None|Some\([^\r\n]*?\))(?=, telemetry_settings:)")]
    private static partial Regex SectionRegex();

    [GeneratedRegex(@"""ServiceName"": String\(""(?<v>[^""]*)""\)")]
    private static partial Regex ServiceNameRegex();

    [GeneratedRegex(@"""ServiceVersion"": String\(""(?<v>[^""]*)""\)")]
    private static partial Regex ServiceVersionRegex();

    [GeneratedRegex(@"""Tracing"": Object \{""Enabled"": Bool\((?<v>true|false)\)")]
    private static partial Regex TracingEnabledRegex();
}
