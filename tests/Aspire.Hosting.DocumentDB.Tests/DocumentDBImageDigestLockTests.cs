// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Aspire.Hosting.DocumentDB.Tests;

[Trait("Category", "Unit")]
public class DocumentDBImageDigestLockTests
{
    private static readonly Regex s_digest = new("^sha256:[0-9a-f]{64}$");

    [Fact]
    public void LockHasExactlyTheCellsTheCatalogSelects()
    {
        var expected = DocumentDBImageDigestLock.CatalogTags().Order(StringComparer.Ordinal).ToArray();
        var locked = DocumentDBImageDigestLock.Entries.Keys.Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(expected, locked);
    }

    [Fact]
    public void EveryEntryHasWellFormedDigests()
    {
        foreach (var (tag, entry) in DocumentDBImageDigestLock.Entries)
        {
            Assert.Matches(s_digest, entry.GetProperty("index").GetString());
            Assert.Matches(s_digest, entry.GetProperty("linux/amd64").GetString());

            var arm64 = entry.GetProperty("linux/arm64");
            Assert.True(
                arm64.ValueKind == JsonValueKind.Null || s_digest.IsMatch(arm64.GetString()!),
                $"{tag} has a malformed linux/arm64 digest.");
        }
    }

    [Fact]
    public void PinnedReferenceNamesTheCuratedRepositoryByIndexDigest()
    {
        var tag = DocumentDBContainerImageTags.Tag;
        var index = DocumentDBImageDigestLock.Entries[tag].GetProperty("index").GetString();

        Assert.Equal(
            $"ghcr.io/documentdb/documentdb/documentdb-local@{index}",
            DocumentDBImageDigestLock.PinnedReference(tag));
        Assert.Throws<InvalidOperationException>(() => DocumentDBImageDigestLock.PinnedReference("pg17-0.0.1"));
    }
}
