// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Aspire.Hosting.DocumentDB.Tests;

/// <summary>
/// The end-to-end telemetry tests only run the released image, so these pin the reader against the
/// print shape of every image it claims to understand. Samples are trimmed from real startup lines.
/// </summary>
[Trait("Category", "Unit")]
public class GatewayTelemetryConfigurationLogTests
{
    private const string Shipped0116 =
        "INFO Starting server with configuration: DocumentDBSetupConfiguration { instance_kind: \"\", " +
        "telemetry_options: Some(TelemetryOptions { service_name: Some(\"documentdb_gateway\"), service_version: None, " +
        "metrics: Some(MetricsOptions { enabled: Some(false), otlp_endpoint: Some(\"http://localhost:4317\") }), " +
        "tracing: Some(TracingOptions { enabled: Some(false), otlp_endpoint: Some(\"http://localhost:4317\") }) }), " +
        "enable_pg_file_settings_refresh: None }";

    private const string Sanitized0116 =
        "INFO Starting server with configuration: DocumentDBSetupConfiguration { " +
        "telemetry_options: Some(TelemetryOptions { service_name: None, service_version: None, metrics: None, " +
        "tracing: Some(TracingOptions { enabled: Some(false) }) }), enable_pg_file_settings_refresh: None }";

    private const string Shipped0117 =
        "INFO Starting server with configuration: DocumentDBSetupConfiguration { instance_kind: \"\", " +
        "telemetry_provider_options: Some(Object {\"ServiceName\": String(\"documentdb_gateway\"), " +
        "\"Metrics\": Object {\"Enabled\": Bool(false), \"OtlpEndpoint\": String(\"http://localhost:4317\")}, " +
        "\"Tracing\": Object {\"Enabled\": Bool(false), \"SamplerRatio\": Number(1.0)}}), " +
        "telemetry_settings: TelemetrySettings { request_metrics_enabled: false }, enable_pg_file_settings_refresh: None }";

    private const string Sanitized0117 =
        "INFO Starting server with configuration: DocumentDBSetupConfiguration { " +
        "telemetry_provider_options: Some(Object {\"ServiceVersion\": String(\"1.2.3\"), " +
        "\"Tracing\": Object {\"Enabled\": Bool(true)}}), " +
        "telemetry_settings: TelemetrySettings { request_metrics_enabled: true } }";

    [Theory]
    [InlineData(Shipped0116)]
    [InlineData(Shipped0117)]
    public void ReadsTheShippedConfiguration(string logs)
    {
        var telemetry = GatewayTelemetryConfigurationLog.Parse(logs);

        Assert.True(telemetry.HasMetricsSection);
        Assert.Equal("documentdb_gateway", telemetry.ServiceName);
        Assert.Null(telemetry.ServiceVersion);
        Assert.False(telemetry.TracingEnabled);
    }

    [Fact]
    public void ReadsA0116SanitizedConfiguration()
    {
        var telemetry = GatewayTelemetryConfigurationLog.Parse(Sanitized0116);

        Assert.False(telemetry.HasMetricsSection);
        Assert.Null(telemetry.ServiceName);
        Assert.Null(telemetry.ServiceVersion);
        Assert.False(telemetry.TracingEnabled);
    }

    [Fact]
    public void ReadsA0117SanitizedConfiguration()
    {
        var telemetry = GatewayTelemetryConfigurationLog.Parse(Sanitized0117);

        Assert.False(telemetry.HasMetricsSection);
        Assert.Null(telemetry.ServiceName);
        Assert.Equal("1.2.3", telemetry.ServiceVersion);
        Assert.True(telemetry.TracingEnabled);
    }

    [Fact]
    public void DoesNotReadFieldsOutsideTheTelemetrySection()
    {
        // A pool's metrics or another component's service name later on the line is not telemetry.
        var telemetry = GatewayTelemetryConfigurationLog.Parse(
            "telemetry_provider_options: None, telemetry_settings: TelemetrySettings { request_metrics_enabled: false }, " +
            "pool: Pool { metrics: Some(1), \"Metrics\": 1, service_name: Some(\"other\") }");

        Assert.False(telemetry.HasMetricsSection);
        Assert.Null(telemetry.ServiceName);
    }

    [Fact]
    public void FailsOnATruncatedLine()
    {
        Assert.ThrowsAny<Exception>(() => GatewayTelemetryConfigurationLog.Parse(
            "INFO Starting server with configuration: DocumentDBSetupConfiguration { telemetry_provider_options: Some(Object {"));
    }

    [Fact]
    public void FailsWhenNothingWasLogged()
    {
        Assert.ThrowsAny<Exception>(() => GatewayTelemetryConfigurationLog.Parse("Gateway is ready"));
    }
}
