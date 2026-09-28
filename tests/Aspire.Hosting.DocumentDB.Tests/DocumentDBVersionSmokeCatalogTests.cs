// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Aspire.Hosting.DocumentDB.Tests;

/// <summary>
/// Keeps the generated version smoke honest: its cells must equal a reviewed list, and the CI legs
/// must run each of them exactly once.
/// </summary>
[Trait("Category", "Unit")]
public class DocumentDBVersionSmokeCatalogTests
{
    /// <summary>The number of <c>version-smoke</c> legs in <c>.github/workflows/build-and-test.yml</c>.</summary>
    internal const int CiLegCount = 9;

    // Written out by hand rather than derived: adopting a version or a PG variant must change this list.
    private static readonly string[] s_expectedTags =
    [
        "pg15-0.109.0", "pg16-0.109.0", "pg17-0.109.0",
        "pg15-0.110.0", "pg16-0.110.0", "pg17-0.110.0",
        "pg15-0.111.0", "pg16-0.111.0", "pg17-0.111.0",
        "pg15-0.112.0", "pg16-0.112.0", "pg17-0.112.0",
        "pg15-0.113.0", "pg16-0.113.0", "pg17-0.113.0",
        "pg15-0.114.0", "pg16-0.114.0", "pg17-0.114.0", "pg18-0.114.0",
        "pg15-0.116.0", "pg16-0.116.0", "pg17-0.116.0", "pg18-0.116.0",
        "pg15-0.117.0", "pg16-0.117.0", "pg17-0.117.0", "pg18-0.117.0",
    ];

    [Fact]
    public void GeneratedCellsMatchTheReviewedCatalog()
    {
        Assert.Equal(s_expectedTags, DocumentDBVersionSmokeTests.Cells().Select(cell => cell.Tag));
    }

    [Fact]
    public void CiLegsRunEveryCellExactlyOnce()
    {
        var cells = DocumentDBVersionSmokeTests.Cells();
        var legs = Enumerable.Range(1, CiLegCount)
            .Select(index => DocumentDBVersionSmokeTests.SelectLeg(cells, $"{index}/{CiLegCount}"))
            .ToArray();

        Assert.All(legs, Assert.NotEmpty);
        Assert.Equal(
            cells.OrderBy(cell => cell.Tag),
            legs.SelectMany(leg => leg).OrderBy(cell => cell.Tag));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AnUnsetLegRunsEveryCell(string? leg)
    {
        var cells = DocumentDBVersionSmokeTests.Cells();
        Assert.Equal(cells, DocumentDBVersionSmokeTests.SelectLeg(cells, leg));
    }

    [Theory]
    [InlineData("0/9")]
    [InlineData("10/9")]
    [InlineData("1/0")]
    [InlineData("3")]
    [InlineData("a/9")]
    public void AMalformedLegIsRejected(string leg)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => DocumentDBVersionSmokeTests.SelectLeg(DocumentDBVersionSmokeTests.Cells(), leg));
        Assert.Contains(DocumentDBVersionSmokeTests.LegEnvironmentVariable, exception.Message, StringComparison.Ordinal);
    }
}
