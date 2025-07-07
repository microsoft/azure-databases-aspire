// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.Json;
using Xunit;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Publishing;

namespace Aspire.Hosting.Utils;

public sealed class ManifestUtils
{
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
