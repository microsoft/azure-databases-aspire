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
    public void EveryDocumentDBVersionEnumMemberMapsToAVersionString()
    {
        // Drives auto-generation consistency: if the update script ever forgets to update one
        // of the auto-generated regions in DocumentDBVersion.cs, this test fails.
        var allMembers = Enum.GetValues<DocumentDBVersion>();
        Assert.NotEmpty(allMembers);
        Assert.Equal(allMembers.Length, DocumentDBVersions.All.Count);

        foreach (var member in allMembers)
        {
            // Reflectively reach the internal ToVersionString to keep this test self-contained.
            var method = typeof(DocumentDBVersions).GetMethod(
                "ToVersionString",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var versionString = (string)method.Invoke(null, new object[] { member })!;
            Assert.Contains(versionString, DocumentDBVersions.All);
        }
    }
}
