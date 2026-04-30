// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Hosting.DocumentDB.Tests;

[Trait("Category", "Unit")]
public class DocumentDBVersionSelectionTests
{
    [Fact]
    public void DefaultImageTagMatchesLatestKnownVersion()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.AddDocumentDB("documentdb");

        using var app = appBuilder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var server = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());
        var image = Assert.Single(server.Annotations.OfType<ContainerImageAnnotation>());

        Assert.Equal($"pg17-{DocumentDBVersions.Latest}", image.Tag);
        Assert.Equal($"pg17-{DocumentDBVersions.Latest}", DocumentDBContainerImageTags.Tag);
    }

    [Theory]
    [InlineData(DocumentDBVersion.V0_109_0, "pg17-0.109.0")]
    [InlineData(DocumentDBVersion.V0_110_0, "pg17-0.110.0")]
    public void WithDocumentDBVersionAloneSetsExpectedTag(DocumentDBVersion version, string expectedTag)
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.AddDocumentDB("documentdb").WithDocumentDBVersion(version);

        using var app = appBuilder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var server = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());
        var image = Assert.Single(server.Annotations.OfType<ContainerImageAnnotation>());

        Assert.Equal(expectedTag, image.Tag);
    }

    [Theory]
    [InlineData(DocumentDBPostgresVersion.Pg15, "pg15-")]
    [InlineData(DocumentDBPostgresVersion.Pg16, "pg16-")]
    [InlineData(DocumentDBPostgresVersion.Pg17, "pg17-")]
    public void WithPostgresVersionAloneSetsExpectedPrefixWithLatest(DocumentDBPostgresVersion pg, string expectedPrefix)
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.AddDocumentDB("documentdb").WithPostgresVersion(pg);

        using var app = appBuilder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var server = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());
        var image = Assert.Single(server.Annotations.OfType<ContainerImageAnnotation>());

        Assert.Equal($"{expectedPrefix}{DocumentDBVersions.Latest}", image.Tag);
    }

    [Fact]
    public void WithDocumentDBVersionThenWithPostgresVersionCombinesCorrectly()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.AddDocumentDB("documentdb")
            .WithDocumentDBVersion(DocumentDBVersion.V0_110_0)
            .WithPostgresVersion(DocumentDBPostgresVersion.Pg15);

        using var app = appBuilder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var server = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());
        var image = Assert.Single(server.Annotations.OfType<ContainerImageAnnotation>());

        Assert.Equal("pg15-0.110.0", image.Tag);
    }

    [Fact]
    public void WithPostgresVersionThenWithDocumentDBVersionCombinesCorrectly()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.AddDocumentDB("documentdb")
            .WithPostgresVersion(DocumentDBPostgresVersion.Pg16)
            .WithDocumentDBVersion(DocumentDBVersion.V0_109_0);

        using var app = appBuilder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var server = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());
        var image = Assert.Single(server.Annotations.OfType<ContainerImageAnnotation>());

        Assert.Equal("pg16-0.109.0", image.Tag);
    }

    [Fact]
    public void FreeFormWithImageTagAfterTypedMethodWins()
    {
        // Last-call-wins precedence: a free-form WithImageTag(...) AFTER WithDocumentDBVersion(...)
        // overrides the typed selection. This is the documented escape hatch for tags the package
        // does not yet know about.
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.AddDocumentDB("documentdb")
            .WithDocumentDBVersion(DocumentDBVersion.V0_110_0)
            .WithImageTag("pg17-0.999.0");

        using var app = appBuilder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var server = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());
        var image = Assert.Single(server.Annotations.OfType<ContainerImageAnnotation>());

        Assert.Equal("pg17-0.999.0", image.Tag);
    }

    [Fact]
    public void TypedMethodAfterFreeFormWithImageTagWins()
    {
        // Last-call-wins precedence: a typed WithDocumentDBVersion(...) AFTER WithImageTag(...)
        // re-applies the curated value.
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.AddDocumentDB("documentdb")
            .WithImageTag("pg17-0.999.0")
            .WithDocumentDBVersion(DocumentDBVersion.V0_110_0);

        using var app = appBuilder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var server = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());
        var image = Assert.Single(server.Annotations.OfType<ContainerImageAnnotation>());

        Assert.Equal("pg17-0.110.0", image.Tag);
    }

    [Fact]
    public void TypedVersionMethodsPreserveCustomImageAndRegistry()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.AddDocumentDB("documentdb")
            .WithImage("custom/documentdb-local", "pg17-0.999.0")
            .WithImageRegistry("registry.example.com")
            .WithDocumentDBVersion(DocumentDBVersion.V0_110_0)
            .WithPostgresVersion(DocumentDBPostgresVersion.Pg15);

        using var app = appBuilder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var server = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());
        var image = Assert.Single(server.Annotations.OfType<ContainerImageAnnotation>());

        Assert.Equal("custom/documentdb-local", image.Image);
        Assert.Equal("registry.example.com", image.Registry);
        Assert.Equal("pg15-0.110.0", image.Tag);
    }

    [Fact]
    public void RepeatedCallsKeepASingleContainerImageAnnotation()
    {
        // Aspire's WithImage / WithImageTag mutate the existing ContainerImageAnnotation in
        // place (verified against Aspire 13.2.3 source). This regression test pins that contract
        // so that future Aspire upgrades can't silently break the single-annotation invariant
        // our typed methods rely on.
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.AddDocumentDB("documentdb")
            .WithDocumentDBVersion(DocumentDBVersion.V0_109_0)
            .WithDocumentDBVersion(DocumentDBVersion.V0_110_0)
            .WithPostgresVersion(DocumentDBPostgresVersion.Pg15)
            .WithPostgresVersion(DocumentDBPostgresVersion.Pg17)
            .WithImageTag("pg16-0.109.0");

        using var app = appBuilder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var server = Assert.Single(appModel.Resources.OfType<DocumentDBServerResource>());

        var image = Assert.Single(server.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal("pg16-0.109.0", image.Tag);
    }

    [Fact]
    public void DocumentDBVersionsListIsNonEmptyAndLatestIsLast()
    {
        Assert.NotEmpty(DocumentDBVersions.All);
        Assert.Equal(DocumentDBVersions.All[DocumentDBVersions.All.Count - 1], DocumentDBVersions.Latest);
        Assert.Contains(DocumentDBVersions.Latest, DocumentDBVersions.All);
    }

    [Fact]
    public void DocumentDBVersionsListIsSortedAscendingBySemver()
    {
        // Numeric per-segment, not lexical: 0.9.0 must sort before 0.10.0 if both ever appear.
        var sorted = DocumentDBVersions.All
            .Select(s => s.Split('.').Select(int.Parse).ToArray())
            .ToArray();
        for (var i = 1; i < sorted.Length; i++)
        {
            Assert.True(CompareSemver(sorted[i - 1], sorted[i]) < 0,
                $"DocumentDBVersions.All must be sorted ascending by semver. " +
                $"Saw {string.Join('.', sorted[i - 1])} before {string.Join('.', sorted[i])}.");
        }

        static int CompareSemver(int[] a, int[] b)
        {
            for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
            {
                if (a[i] != b[i])
                {
                    return a[i].CompareTo(b[i]);
                }
            }
            return a.Length.CompareTo(b.Length);
        }
    }

    [Fact]
    public void DocumentDBVersionsAllCannotBeMutatedByDowncast()
    {
        // Regression: previously the backing field was a `string[]`, so callers could
        // cast the IReadOnlyList<string> back to string[] and mutate it process-wide,
        // which would silently change `Latest` (and therefore the default container tag)
        // for subsequent AddDocumentDB(...) calls.
        var snapshotBefore = DocumentDBVersions.All.ToArray();
        var latestBefore = DocumentDBVersions.Latest;

        Assert.False(
            DocumentDBVersions.All is string[],
            "DocumentDBVersions.All must not be a string[] — it must be a read-only wrapper " +
            "to prevent callers from downcasting and mutating it process-wide.");

        var asReadOnlyList = DocumentDBVersions.All;
        Assert.IsAssignableFrom<System.Collections.ObjectModel.ReadOnlyCollection<string>>(asReadOnlyList);

        // Sanity: even an attempt to round-trip through ICollection<string>.Add must fail.
        if (asReadOnlyList is ICollection<string> mutableView)
        {
            Assert.Throws<NotSupportedException>(() => mutableView.Add("0.999.0"));
        }

        Assert.Equal(snapshotBefore, DocumentDBVersions.All);
        Assert.Equal(latestBefore, DocumentDBVersions.Latest);
    }

    [Fact]
    public void WithPostgresVersionRejectsUndefinedEnumValues()
    {
        // Regression: previously WithPostgresVersion forwarded any int-cast value to
        // `(int)PgVersion`, producing tags like "pg999-..." that bypass the curated PG
        // variant contract.
        var appBuilder = DistributedApplication.CreateBuilder();
        var resourceBuilder = appBuilder.AddDocumentDB("documentdb");

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => resourceBuilder.WithPostgresVersion((DocumentDBPostgresVersion)999));

        Assert.Equal("pgVersion", ex.ParamName);
    }

    [Fact]
    public void WithPostgresVersionRejectsZero()
    {
        // The default(DocumentDBPostgresVersion) value is 0, which is not a defined member
        // (Pg15, Pg16, Pg17 use explicit numeric values 15/16/17).
        var appBuilder = DistributedApplication.CreateBuilder();
        var resourceBuilder = appBuilder.AddDocumentDB("documentdb");

        Assert.Throws<ArgumentOutOfRangeException>(
            () => resourceBuilder.WithPostgresVersion(default));
    }
}
