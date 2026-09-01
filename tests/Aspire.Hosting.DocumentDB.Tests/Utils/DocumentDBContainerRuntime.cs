// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;

namespace Aspire.Hosting.DocumentDB.Tests;

/// <summary>
/// What the container runtime under test is, as reported by the daemon itself.
/// </summary>
/// <param name="OperatingSystem">
/// The <c>OperatingSystem</c> field of <c>docker info</c>, or the host OS description when the
/// daemon could not be asked.
/// </param>
/// <param name="IsDockerDesktop">Whether that identifies Docker Desktop.</param>
/// <param name="DaemonAnswered">
/// Whether <paramref name="OperatingSystem"/> came from the daemon rather than from the fallback.
/// </param>
internal sealed record ContainerRuntimeDescription(string OperatingSystem, bool IsDockerDesktop, bool DaemonAnswered)
{
    public override string ToString() => DaemonAnswered
        ? $"docker info reported OperatingSystem='{OperatingSystem}' (Docker Desktop: {IsDockerDesktop})"
        : $"docker info could not be read, so the host OS '{OperatingSystem}' was used instead " +
          $"(Docker Desktop: {IsDockerDesktop})";
}

/// <summary>
/// Identifies the container runtime the end-to-end suite is talking to.
/// </summary>
/// <remarks>
/// Only Docker Desktop is allowed to fail a bind-mounted restart, so "which runtime is this"
/// has to be answered by the daemon rather than guessed from the host OS: a Linux CI runner and a
/// Linux developer box running Docker Desktop are the same <see cref="System.OperatingSystem"/>
/// and behave differently, and a macOS host could in principle run a runtime that hands the data
/// directory over correctly. The host OS is only a fallback, and only ever a stricter one.
/// </remarks>
internal static class DocumentDBContainerRuntime
{
    /// <summary>The value Docker Desktop puts in <c>docker info</c>'s <c>OperatingSystem</c> field.</summary>
    internal const string DockerDesktopOperatingSystem = "Docker Desktop";

    /// <summary>Classifies a <c>docker info</c> <c>OperatingSystem</c> value.</summary>
    /// <remarks>
    /// Docker Desktop reports exactly "Docker Desktop" on macOS, Windows and Linux. A native
    /// engine reports the host distribution ("Ubuntu 24.04.3 LTS", "Alpine Linux v3.20", ...),
    /// which is what a CI runner reports and what therefore stays strict.
    /// </remarks>
    public static bool IsDockerDesktop(string? dockerInfoOperatingSystem) =>
        !string.IsNullOrWhiteSpace(dockerInfoOperatingSystem) &&
        dockerInfoOperatingSystem.Contains(DockerDesktopOperatingSystem, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The rule used when the daemon cannot be asked: a Linux host is never granted the tolerance,
    /// so a CI runner stays strict even if <c>docker info</c> fails.
    /// </summary>
    public static bool FallbackIsDockerDesktop(bool hostIsLinux) => !hostIsLinux;

    public static async Task<ContainerRuntimeDescription> DescribeAsync()
    {
        string? operatingSystem = null;

        try
        {
            var (exitCode, output) = await DocumentDBEndToEndSupport.RunDockerAsync(
                "info", "--format", "{{.OperatingSystem}}");

            if (exitCode == 0)
            {
                operatingSystem = output.Trim();
            }
        }
        catch (InvalidOperationException)
        {
            // The daemon is unavailable or wedged; fall through to the stricter fallback.
        }

        return string.IsNullOrWhiteSpace(operatingSystem)
            ? new ContainerRuntimeDescription(
                RuntimeInformation.OSDescription,
                FallbackIsDockerDesktop(System.OperatingSystem.IsLinux()),
                DaemonAnswered: false)
            : new ContainerRuntimeDescription(operatingSystem, IsDockerDesktop(operatingSystem), DaemonAnswered: true);
    }
}
