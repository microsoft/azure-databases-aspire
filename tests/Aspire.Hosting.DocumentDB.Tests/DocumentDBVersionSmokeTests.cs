// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;
using static Aspire.Hosting.DocumentDB.Tests.DocumentDBEndToEndSupport;
using AppHost = Aspire.Hosting.DocumentDB.FeatureMatrixEndToEndApp.Program;

namespace Aspire.Hosting.DocumentDB.Tests;

/// <summary>One (DocumentDB version, PostgreSQL backend) combination the curated catalog offers.</summary>
internal readonly record struct VersionSmokeCell(DocumentDBVersion Version, DocumentDBPostgresVersion Postgres)
{
    public Version DocumentDBVersion => System.Version.Parse(DocumentDBVersions.ToVersionString(Version));

    public string Tag => $"pg{(int)Postgres}-{DocumentDBVersions.ToVersionString(Version)}";

    public bool SupportsPostgresEndpoint => DocumentDBVersion >= DocumentDBContainerImageTags.MinimumPostgresEndpointVersion;
}

/// <summary>
/// Starts every released cell of the catalog through the typed version API and proves it serves
/// authenticated traffic, so a tag upstream never published or later broke fails here rather than
/// in a consumer's AppHost.
/// </summary>
/// <remarks>
/// CI splits the cells across parallel legs with <see cref="LegEnvironmentVariable"/>
/// (<c>"3/9"</c> runs every ninth cell starting at the third); unset, every cell runs.
/// </remarks>
[Trait("Category", "VersionSmoke")]
[Collection(DocumentDBFeatureMatrixAppHostCollection.Name)]
public class DocumentDBVersionSmokeTests
{
    internal const string LegEnvironmentVariable = "DOCUMENTDB_SMOKE_LEG";

    internal static IReadOnlyList<VersionSmokeCell> Cells() =>
    [
        .. from version in Enum.GetValues<DocumentDBVersion>()
           from postgres in Enum.GetValues<DocumentDBPostgresVersion>()
           let cell = new VersionSmokeCell(version, postgres)
           where !DocumentDBContainerImageTags.MinimumVersionByPgVariant.TryGetValue((int)postgres, out var floor) ||
                 cell.DocumentDBVersion >= floor
           select cell,
    ];

    internal static IReadOnlyList<VersionSmokeCell> SelectLeg(IReadOnlyList<VersionSmokeCell> cells, string? leg)
    {
        if (string.IsNullOrWhiteSpace(leg))
        {
            return cells;
        }

        var parts = leg.Split('/');
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], out var index) ||
            !int.TryParse(parts[1], out var count) ||
            count < 1 || index < 1 || index > count)
        {
            throw new InvalidOperationException(
                $"{LegEnvironmentVariable} must be 'index/count' with 1 <= index <= count, but was '{leg}'.");
        }

        return [.. cells.Where((_, position) => position % count == index - 1)];
    }

    public static TheoryData<DocumentDBVersion, DocumentDBPostgresVersion> LegCells()
    {
        var data = new TheoryData<DocumentDBVersion, DocumentDBPostgresVersion>();
        foreach (var cell in SelectLeg(Cells(), Environment.GetEnvironmentVariable(LegEnvironmentVariable)))
        {
            data.Add(cell.Version, cell.Postgres);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(LegCells))]
    public async Task CatalogCellStartsAndServesAuthenticatedTraffic(DocumentDBVersion version, DocumentDBPostgresVersion postgres)
    {
        RequireDocker();

        var cell = new VersionSmokeCell(version, postgres);
        using var cts = CreateEndToEndTimeoutSource();
        using var scenario = new EnvironmentScope(
            (AppHost.ScenarioEnvironmentVariable, AppHost.CatalogCellScenario),
            (AppHost.DocumentDBVersionEnvironmentVariable, version.ToString()),
            (AppHost.PostgresVersionEnvironmentVariable, postgres.ToString()),
            (AppHost.PostgresEndpointEnvironmentVariable, cell.SupportsPostgresEndpoint.ToString()));

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<AppHost>(cts.Token);
        await using var app = await appHost.BuildAsync(cts.Token);

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var server = Assert.Single(Snapshot<DocumentDBServerResource>(appModel.Resources));
        var image = Assert.Single(Snapshot<ContainerImageAnnotation>(server.Annotations));
        Assert.Equal(cell.Tag, image.Tag);

        await app.StartAsync(cts.Token);

        var healthCheckService = app.Services.GetRequiredService<HealthCheckService>();
        await WaitForHealthCheckAsync(healthCheckService, "documentdb_check", cts.Token);

        var connectionString = await app.GetConnectionStringAsync("appdb", cts.Token);
        await AssertRoundTripAsync(connectionString!, "appdb", "smoke", cell.Tag, cts.Token);

        if (cell.SupportsPostgresEndpoint)
        {
            // Authenticates with the generated credentials and proves the tag's pgNN is the backend.
            var postgresUri = await server.PostgresConnectionStringExpression.GetValueAsync(cts.Token);
            var serverVersion = await DocumentDBFeatureMatrixEndToEndTests.QueryPostgresAsync(
                postgresUri!,
                "SHOW server_version_num",
                cts.Token);
            Assert.Equal((int)postgres, int.Parse(Convert.ToString(serverVersion)!) / 10000);
        }

        await app.StopAsync(cts.Token);
    }
}
