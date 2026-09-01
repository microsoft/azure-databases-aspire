// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.Json;
using Xunit;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Publishing;

namespace Aspire.Hosting.Utils;

public sealed class ManifestUtils
{
    /// <summary>
    /// Runs the real Aspire publishing pipeline in manifest mode and returns the manifest the
    /// publisher wrote to disk.
    /// </summary>
    /// <remarks>
    /// <see cref="GetManifestOrNull"/> serializes a single resource directly and therefore skips
    /// everything the pipeline does first — most importantly <c>BeforeStartEvent</c>, which
    /// integrations use to finish shaping a resource before it is published. Assertions about
    /// what actually ships to <c>azd</c> need the full pipeline.
    /// </remarks>
    public static async Task<JsonNode> PublishManifestAsync(
        Action<IDistributedApplicationBuilder> configure,
        [CallerMemberName] string? testName = null)
    {
        var outputPath = Path.Combine(
            AppContext.BaseDirectory,
            "published-manifests",
            $"{testName ?? "manifest"}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputPath);

        var appBuilder = DistributedApplication.CreateBuilder(
            ["--operation", "publish", "--publisher", "manifest", "--output-path", outputPath]);
        configure(appBuilder);

        using (var app = appBuilder.Build())
        {
            await app.RunAsync();
        }

        var manifestPath = Path.Combine(outputPath, "aspire-manifest.json");
        Assert.True(File.Exists(manifestPath), $"The manifest publisher did not write '{manifestPath}'.");

        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath));
        Assert.NotNull(manifest);

        Directory.Delete(outputPath, recursive: true);

        return manifest;
    }

    public static async Task<JsonNode> GetManifest(IResource resource, string? manifestDirectory = null)
    {
        var node = await GetManifestOrNull(resource, manifestDirectory);
        Assert.NotNull(node);
        return node;
    }

    public static async Task<JsonNode?> GetManifestOrNull(IResource resource, string? manifestDirectory = null)
    {
        manifestDirectory ??= Environment.CurrentDirectory;

        using var ms = new MemoryStream();
        var writer = new Utf8JsonWriter(ms);
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish);
        writer.WriteStartObject();
        var context = new ManifestPublishingContext(executionContext, Path.Combine(manifestDirectory, "manifest.json"), writer);
        
        // Use reflection to access the internal WriteResourceAsync method
        var contextType = typeof(ManifestPublishingContext);
        var writeResourceMethod = contextType.GetMethod("WriteResourceAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(writeResourceMethod);
        
        await (Task)writeResourceMethod.Invoke(context, [resource])!;
        
        writer.WriteEndObject();
        writer.Flush();
        ms.Position = 0;
        var obj = JsonNode.Parse(ms);
        Assert.NotNull(obj);
        var resourceNode = obj[resource.Name];
        return resourceNode;
    }

    public static async Task<JsonNode[]> GetManifests(IResource[] resources)
    {
        using var ms = new MemoryStream();
        var writer = new Utf8JsonWriter(ms);
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish);
        var context = new ManifestPublishingContext(executionContext, Path.Combine(Environment.CurrentDirectory, "manifest.json"), writer);

        // Use reflection to access the internal WriteResourceAsync method
        var contextType = typeof(ManifestPublishingContext);
        var writeResourceMethod = contextType.GetMethod("WriteResourceAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(writeResourceMethod);

        var results = new List<JsonNode>();

        foreach (var r in resources)
        {
            writer.WriteStartObject();
            await (Task)writeResourceMethod.Invoke(context, [r])!;
            writer.WriteEndObject();
            writer.Flush();
            ms.Position = 0;
            var obj = JsonNode.Parse(ms);
            Assert.NotNull(obj);
            var resourceNode = obj[r.Name];
            Assert.NotNull(resourceNode);
            results.Add(resourceNode);

            ms.Position = 0;
            writer.Reset(ms);
        }

        return [.. results];
    }
}
