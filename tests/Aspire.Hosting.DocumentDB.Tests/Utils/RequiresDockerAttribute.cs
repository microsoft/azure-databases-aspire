// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Aspire.TestUtilities;

/// <summary>
/// Attribute that can be applied to test classes or methods to indicate that they require Docker to be available.
/// When Docker is not supported, the test will be skipped.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public class RequiresDockerAttribute : Attribute
{
    // This property is `true` when docker is *expected* to be available.
    //
    // A hard-coded *expected* value is used here to ensure that docker
    // dependent tests *fail* if docker is not available/usable in an environment
    // where it is expected to be available. A run-time check would allow tests
    // to fail silently, which is not desirable.
    //
    // scenarios:
    // - Windows: assume installed only for *local* runs as docker isn't supported on CI yet
    //                - https://github.com/dotnet/aspire/issues/4291
    // - Linux - Local, or CI: always assume that docker is installed
    public static bool IsSupported =>
        OperatingSystem.IsLinux() || !IsRunningOnCI; // non-linux on CI does not support docker

    // Simple CI detection - check for common CI environment variables
    private static bool IsRunningOnCI =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")) ||
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BUILD_ID")) ||
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS")) ||
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AZURE_PIPELINES")) ||
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TEAMCITY_VERSION"));

    public string? Reason { get; init; }
    
    public RequiresDockerAttribute(string? reason = null)
    {
        Reason = reason;
    }
}
