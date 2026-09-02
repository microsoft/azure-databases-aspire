// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Aspire.Hosting.DocumentDB.Tests;

[Trait("Category", "Unit")]
public class DocumentDBContainerImageTagsTests
{
    [Fact]
    public void MinimumPostgresEndpointVersionIs_0_112_0()
    {
        Assert.Equal(new Version(0, 112, 0), DocumentDBContainerImageTags.MinimumPostgresEndpointVersion);
    }

    [Theory]
    // Happy path - all three supported PG variants.
    [InlineData("pg15-0.112.0", 15, "0.112.0")]
    [InlineData("pg16-0.112.0", 16, "0.112.0")]
    [InlineData("pg17-0.112.0", 17, "0.112.0")]
    // Other valid release versions.
    [InlineData("pg17-0.109.0", 17, "0.109.0")]
    [InlineData("pg17-0.111.0", 17, "0.111.0")]
    [InlineData("pg17-1.0.0", 17, "1.0.0")]
    [InlineData("pg17-1.2.3", 17, "1.2.3")]
    [InlineData("pg17-10.20.30", 17, "10.20.30")]
    // Case-insensitive on "pg" prefix only.
    [InlineData("PG17-0.112.0", 17, "0.112.0")]
    [InlineData("Pg17-0.112.0", 17, "0.112.0")]
    [InlineData("pG17-0.112.0", 17, "0.112.0")]
    // Single-digit and multi-digit PG majors are both accepted (\d+).
    [InlineData("pg9-0.112.0", 9, "0.112.0")]
    [InlineData("pg175-0.112.0", 175, "0.112.0")]
    public void TryParseDocumentDBTagAcceptsStrictPgNNVersion(string tag, int expectedPg, string expectedVersion)
    {
        var ok = DocumentDBContainerImageTags.TryParseDocumentDBTag(tag, out var pg, out var docVersion);

        Assert.True(ok, $"expected '{tag}' to parse");
        Assert.Equal(expectedPg, pg);
        Assert.NotNull(docVersion);
        Assert.Equal(Version.Parse(expectedVersion), docVersion);
    }

    [Theory]
    // Null / empty / whitespace-only.
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // Whitespace at edges must NOT parse (strict).
    [InlineData(" pg17-0.112.0")]
    [InlineData("pg17-0.112.0 ")]
    [InlineData(" pg17-0.112.0 ")]
    // Trailing newline must NOT slip past the anchor (\z is used, not $).
    [InlineData("pg17-0.112.0\n")]
    [InlineData("pg17-0.112.0\r\n")]
    // Two-part version - rejected to avoid the System.Version.Build=-1 comparison bug.
    [InlineData("pg17-0.112")]
    [InlineData("pg17-1.0")]
    // Pre-release / build-metadata suffixes - rejected because System.Version cannot
    // represent them and we MUST NOT silently allow an rc/alpha to pass the floor.
    [InlineData("pg17-0.112.0-rc.1")]
    [InlineData("pg17-0.112.0-alpha")]
    [InlineData("pg17-0.112.0+build.42")]
    [InlineData("pg17-0.112.0-rc1+meta")]
    // Missing "pg" prefix.
    [InlineData("0.112.0")]
    [InlineData("17-0.112.0")]
    // "pg" without numeric major.
    [InlineData("pg-0.112.0")]
    [InlineData("pgX-0.112.0")]
    // Non-numeric version segments.
    [InlineData("pg17-foo")]
    [InlineData("pg17-latest")]
    [InlineData("pg17-0.x.0")]
    // Wrong separator between pg-prefix and version.
    [InlineData("pg17_0.112.0")]
    [InlineData("pg170.112.0")]
    [InlineData("pg17.0.112.0")]
    // Custom-image-style tags.
    [InlineData("latest")]
    [InlineData("nightly")]
    [InlineData("sha256-deadbeef")]
    // Four-part version (Revision) - rejected (strict 3-segment grammar).
    [InlineData("pg17-0.112.0.1")]
    // Unicode-digit categories must NOT match (regex uses [0-9], not \d).
    [InlineData("pg\u0661\u0667-0.112.0")] // pg with Arabic-Indic 17
    [InlineData("pg17-\u0660.112.0")]      // Arabic-Indic 0 in version
    public void TryParseDocumentDBTagRejectsUnknownPatterns(string? tag)
    {
        var ok = DocumentDBContainerImageTags.TryParseDocumentDBTag(tag, out var pg, out var docVersion);

        Assert.False(ok, $"expected '{tag ?? "<null>"}' to NOT parse");
        Assert.Equal(0, pg);
        Assert.Null(docVersion);
    }

    [Theory]
    // Above the floor.
    [InlineData("pg17-0.112.0", false)]
    [InlineData("pg17-0.113.0", false)]
    [InlineData("pg17-0.200.0", false)]
    [InlineData("pg17-1.0.0", false)]
    [InlineData("pg15-0.112.0", false)]
    // Below the floor.
    [InlineData("pg17-0.111.0", true)]
    [InlineData("pg17-0.109.0", true)]
    [InlineData("pg17-0.0.1", true)]
    [InlineData("pg15-0.111.0", true)]
    public void ParsedVersionComparesCorrectlyAgainstMinimumPostgresEndpointVersion(string tag, bool expectedBelowFloor)
    {
        Assert.True(DocumentDBContainerImageTags.TryParseDocumentDBTag(tag, out _, out var docVersion));
        var isBelow = docVersion < DocumentDBContainerImageTags.MinimumPostgresEndpointVersion;
        Assert.Equal(expectedBelowFloor, isBelow);
    }

    /// <summary>
    /// Where the caller puts the boundary between registry and repository is not evidence about
    /// anything — Aspire joins the two fields and validates neither — so the composed reference is
    /// judged, and exactly one prefix may stand in front of the repository: the curated registry,
    /// or a single registry host.
    /// </summary>
    [Theory]
    // The canonical spelling, and every rearrangement of it that composes the same reference.
    [InlineData("ghcr.io/documentdb", "documentdb/documentdb-local", true)]
    [InlineData(null, "ghcr.io/documentdb/documentdb/documentdb-local", true)]
    [InlineData("", "ghcr.io/documentdb/documentdb/documentdb-local", true)]
    [InlineData("ghcr.io", "documentdb/documentdb/documentdb-local", true)]
    [InlineData("ghcr.io/documentdb/documentdb", "documentdb-local", true)]
    // Registry hosts and the curated namespace are case-insensitive.
    [InlineData(null, "GHCR.IO/DocumentDB/documentdb/documentdb-local", true)]
    // A private mirror is a registry host and the exact repository beneath it, in either spelling.
    [InlineData("contoso.azurecr.io", "documentdb/documentdb-local", true)]
    [InlineData(null, "contoso.azurecr.io/documentdb/documentdb-local", true)]
    [InlineData("harbor.corp.local", "documentdb/documentdb-local", true)]
    [InlineData("localhost:5000", "documentdb/documentdb-local", true)]
    [InlineData(null, "localhost:5000/documentdb/documentdb-local", true)]
    [InlineData(null, "localhost/documentdb/documentdb-local", true)]
    [InlineData(null, "127.0.0.1:5000/documentdb/documentdb-local", true)]
    [InlineData(null, "[::1]:5000/documentdb/documentdb-local", true)]
    [InlineData(null, "[fe80::1]/documentdb/documentdb-local", true)]
    // A bare name is a host only when it carries a port; otherwise it is a path segment.
    [InlineData("myregistry:5000", "documentdb/documentdb-local", true)]
    [InlineData("myregistry", "documentdb/documentdb-local", false)]
    [InlineData(null, "myregistry/documentdb/documentdb-local", false)]
    // No prefix at all.
    [InlineData(null, "documentdb/documentdb-local", true)]
    // ghcr.io + the exact repository. This is one segment short of the path this package
    // publishes, but it is the reference WithImageRegistry("ghcr.io") composes and it follows
    // from the host rule, so it classifies as a mirror. Harmless: the pull then fails.
    [InlineData(null, "ghcr.io/documentdb/documentdb-local", true)]
    [InlineData("ghcr.io/documentdb", "documentdb-local", true)]
    // A namespace, project or mirror path in front of the repository is part of the repository,
    // whether the caller wrote it into Registry or inline. Both spellings of each, because the
    // composed reference is the same and has to classify the same.
    [InlineData("ghcr.io/evil", "documentdb/documentdb-local", false)]
    [InlineData(null, "ghcr.io/evil/documentdb/documentdb-local", false)]
    [InlineData("contoso.azurecr.io/mirrors", "documentdb/documentdb-local", false)]
    [InlineData(null, "contoso.azurecr.io/mirrors/documentdb/documentdb-local", false)]
    [InlineData("harbor.corp.local/library", "documentdb/documentdb-local", false)]
    [InlineData(null, "harbor.corp.local/library/documentdb/documentdb-local", false)]
    [InlineData("localhost:5000/mirrors", "documentdb/documentdb-local", false)]
    [InlineData(null, "localhost:5000/mirrors/documentdb/documentdb-local", false)]
    [InlineData("ghcr.io/documentdb", "evil/documentdb/documentdb-local", false)]
    [InlineData(null, "evil/documentdb/documentdb-local", false)]
    [InlineData(null, "ghcr.io/documentdbX/documentdb/documentdb-local", false)]
    [InlineData(null, "x/documentdb/documentdb-local", false)]
    // Writing the registry into Image without clearing it composes a doubled, unresolvable
    // reference, which is not the official image.
    [InlineData("ghcr.io/documentdb", "ghcr.io/documentdb/documentdb/documentdb-local", false)]
    // Repositories that merely resemble the curated one.
    [InlineData(null, "contoso/mydocumentdb/documentdb-local", false)]
    [InlineData("ghcr.io/documentdb", "documentdb/documentdb-local-fork", false)]
    [InlineData(null, "documentdb-local", false)]
    [InlineData("ghcr.io/documentdb", "documentdb/DocumentDB-Local", false)]
    // References the runtime cannot resolve are not the curated one either.
    [InlineData("ghcr.io/documentdb/", "/documentdb/documentdb-local/", false)]
    [InlineData("ghcr.io/documentdb/", "documentdb/documentdb-local", false)]
    [InlineData("ghcr.io/documentdb", "documentdb/documentdb-local/", false)]
    [InlineData("ghcr.io/documentdb", "", false)]
    [InlineData(null, "", false)]
    public void NamesCuratedRepositoryJudgesTheComposedReference(string? registry, string image, bool expected)
    {
        Assert.Equal(
            expected,
            DocumentDBContainerImageTags.NamesCuratedRepository(registry, image, out _, out _));
    }

    /// <summary>
    /// The same reference composed from different splits of the two annotation fields always
    /// classifies the same, because only the composition is read.
    /// </summary>
    [Theory]
    [InlineData("ghcr.io/documentdb/documentdb/documentdb-local", true)]
    [InlineData("contoso.azurecr.io/documentdb/documentdb-local", true)]
    [InlineData("ghcr.io/evil/documentdb/documentdb-local", false)]
    [InlineData("contoso.azurecr.io/mirrors/documentdb/documentdb-local", false)]
    [InlineData("harbor.corp.local/library/documentdb/documentdb-local", false)]
    public void NamesCuratedRepositoryIsIndifferentToWhereTheSplitFalls(string reference, bool expected)
    {
        var segments = reference.Split('/');

        for (var boundary = 0; boundary < segments.Length; boundary++)
        {
            var registry = boundary == 0 ? null : string.Join('/', segments[..boundary]);
            var image = string.Join('/', segments[boundary..]);

            Assert.Equal(
                expected,
                DocumentDBContainerImageTags.NamesCuratedRepository(registry, image, out _, out _));
        }
    }

    [Theory]
    // Aspire's WithImage splits these out itself; a hand-built annotation does not.
    [InlineData("ghcr.io/documentdb", "documentdb/documentdb-local:pg17-0.116.0", "pg17-0.116.0", null)]
    [InlineData(null, "ghcr.io/documentdb/documentdb/documentdb-local:pg17-0.116.0", "pg17-0.116.0", null)]
    // A registry port is not a tag: the ':' has to be in the last path segment.
    [InlineData(null, "localhost:5000/documentdb/documentdb-local", null, null)]
    [InlineData(null, "localhost:5000/documentdb/documentdb-local:pg17-0.116.0", "pg17-0.116.0", null)]
    [InlineData(null, "[::1]:5000/documentdb/documentdb-local:pg17-0.116.0", "pg17-0.116.0", null)]
    // A digest brings its own ':' and is stored the way ContainerImageAnnotation.SHA256 is.
    [InlineData("ghcr.io/documentdb", "documentdb/documentdb-local@sha256:abc123", null, "abc123")]
    [InlineData(null, "ghcr.io/documentdb/documentdb/documentdb-local@sha256:abc123", null, "abc123")]
    [InlineData("ghcr.io/documentdb", "documentdb/documentdb-local", null, null)]
    public void NamesCuratedRepositoryReportsAnInlineTagOrDigest(
        string? registry,
        string image,
        string? expectedTag,
        string? expectedDigest)
    {
        Assert.True(DocumentDBContainerImageTags.NamesCuratedRepository(
            registry, image, out var tag, out var digest));

        Assert.Equal(expectedTag, tag);
        Assert.Equal(expectedDigest, digest);
    }
}
