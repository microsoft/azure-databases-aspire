// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Aspire.Hosting.DocumentDB.Tests;

/// <summary>
/// Pins the reader the telemetry end-to-end tests rely on, so an absence check cannot pass on a
/// log it failed to read. Samples are trimmed from real <c>0.117.0</c> startup lines.
/// </summary>
[Trait("Category", "Unit")]
public class GatewayTelemetryConfigurationLogTests
{
    [Fact]
    public void ReadsTheShippedConfiguration()
    {
        var telemetry = GatewayTelemetryConfigurationLog.Parse(
            "INFO Starting server with configuration: DocumentDBSetupConfiguration { instance_kind: \"\", " +
            "telemetry_provider_options: Some(Object {\"ServiceName\": String(\"documentdb_gateway\"), " +
            "\"Metrics\": Object {\"Enabled\": Bool(false)}, \"Tracing\": Object {\"Enabled\": Bool(false)}}), " +
            "telemetry_settings: TelemetrySettings { request_metrics_enabled: false } }");

        Assert.True(telemetry.HasMetricsSection);
        Assert.Equal("documentdb_gateway", telemetry.ServiceName);
        Assert.Null(telemetry.ServiceVersion);
        Assert.False(telemetry.TracingEnabled);
    }

    [Fact]
    public void ReadsASanitizedConfiguration()
    {
        var telemetry = GatewayTelemetryConfigurationLog.Parse(
            "telemetry_provider_options: Some(Object {\"ServiceVersion\": String(\"1.2.3\"), " +
            "\"Tracing\": Object {\"Enabled\": Bool(true)}}), telemetry_settings: TelemetrySettings { }");

        Assert.False(telemetry.HasMetricsSection);
        Assert.Null(telemetry.ServiceName);
        Assert.Equal("1.2.3", telemetry.ServiceVersion);
        Assert.True(telemetry.TracingEnabled);
    }

    [Theory]
    [InlineData("Gateway is ready")]
    [InlineData("Starting server with configuration: DocumentDBSetupConfiguration { telemetry_provider_options: Some(Object {")]
    public void FailsWithoutACompleteSection(string logs)
    {
        Assert.ThrowsAny<Exception>(() => GatewayTelemetryConfigurationLog.Parse(logs));
    }
}
