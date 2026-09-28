// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.DocumentDB.Tests;

/// <summary>
/// Reads <c>eng/documentdb-image-digests.json</c>, the reviewed tag-to-digest record the
/// version automation appends to, so tests that drive <c>docker</c> directly run the exact bytes
/// that were adopted rather than whatever a mutable GHCR tag points at today.
/// </summary>
internal static class DocumentDBImageDigestLock
{
    private static readonly Lazy<IReadOnlyDictionary<string, JsonElement>> s_entries = new(Load);

    public static IReadOnlyDictionary<string, JsonElement> Entries => s_entries.Value;

    /// <summary>The <c>repository@sha256:...</c> reference of a locked tag's manifest index.</summary>
    public static string PinnedReference(string tag) =>
        Entries.TryGetValue(tag, out var entry)
            ? $"{DocumentDBContainerImageTags.Registry}/{DocumentDBContainerImageTags.Image}@{entry.GetProperty("index").GetString()}"
            : throw new InvalidOperationException($"'{tag}' is not in eng/documentdb-image-digests.json.");

    /// <summary>Every tag the catalog can select that upstream publishes.</summary>
    public static IEnumerable<string> CatalogTags() =>
        from version in DocumentDBVersions.All
        from pg in Enum.GetValues<DocumentDBPostgresVersion>().Select(pg => (int)pg)
        where !DocumentDBContainerImageTags.MinimumVersionByPgVariant.TryGetValue(pg, out var floor) ||
              Version.Parse(version) >= floor
        select $"pg{pg}-{version}";

    private static IReadOnlyDictionary<string, JsonElement> Load()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "eng", "documentdb-image-digests.json");
            if (File.Exists(path))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                return document.RootElement.EnumerateObject()
                    .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
            }
        }

        throw new InvalidOperationException(
            $"eng/documentdb-image-digests.json not found above '{AppContext.BaseDirectory}'.");
    }
}
