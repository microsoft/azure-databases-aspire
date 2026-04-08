// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.DocumentDB;
using Xunit;

namespace Aspire.TestUtilities;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequiresPublishedDocumentDBImageAttribute : FactAttribute
{
    public RequiresPublishedDocumentDBImageAttribute(DocumentDBPostgreSqlVersion version)
    {
        if (!RequiresDockerAttribute.IsSupported)
        {
            Skip = "Docker is required for DocumentDB end-to-end validation.";
            return;
        }

        var imageReference = $"{DocumentDBContainerImageTags.Registry}/{DocumentDBContainerImageTags.Image}:{DocumentDBContainerImageTags.GetTag(version)}";

        if (IsManifestUnknown(imageReference))
        {
            Skip = $"DocumentDB Local image '{imageReference}' is not published.";
        }
    }

    private static bool IsManifestUnknown(string imageReference)
    {
        try
        {
            var startInfo = new ProcessStartInfo("docker")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            startInfo.ArgumentList.Add("manifest");
            startInfo.ArgumentList.Add("inspect");
            startInfo.ArgumentList.Add(imageReference);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            var standardOutput = process.StandardOutput.ReadToEnd();
            var standardError = process.StandardError.ReadToEnd();

            process.WaitForExit();

            return process.ExitCode != 0 &&
                (standardOutput.Contains("manifest unknown", StringComparison.OrdinalIgnoreCase) ||
                 standardError.Contains("manifest unknown", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }
}
