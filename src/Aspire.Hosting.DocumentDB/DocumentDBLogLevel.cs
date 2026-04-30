// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting;

/// <summary>
/// Represents the supported DocumentDB Local container log levels.
/// </summary>
public enum DocumentDBLogLevel
{
    /// <summary>Suppresses all log output.</summary>
    Quiet,

    /// <summary>Logs error messages only.</summary>
    Error,

    /// <summary>Logs warnings and errors.</summary>
    Warn,

    /// <summary>Logs informational messages, warnings, and errors. This is the default DocumentDB Local log level.</summary>
    Info,

    /// <summary>Logs debug-level diagnostic messages and everything above.</summary>
    Debug,

    /// <summary>Logs the most verbose trace-level diagnostic messages and everything above.</summary>
    Trace
}

internal static class DocumentDBLogLevelExtensions
{
    public static string ToEnvironmentValue(this DocumentDBLogLevel logLevel) => logLevel switch
    {
        DocumentDBLogLevel.Quiet => "quiet",
        DocumentDBLogLevel.Error => "error",
        DocumentDBLogLevel.Warn => "warn",
        DocumentDBLogLevel.Info => "info",
        DocumentDBLogLevel.Debug => "debug",
        DocumentDBLogLevel.Trace => "trace",
        _ => throw new ArgumentOutOfRangeException(nameof(logLevel), logLevel, "Unsupported DocumentDB log level.")
    };
}
