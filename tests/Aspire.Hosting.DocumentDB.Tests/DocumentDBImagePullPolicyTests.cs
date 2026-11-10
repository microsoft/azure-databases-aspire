// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Hosting.DocumentDB.Tests;

/// <summary>
/// Upstream rebuilds published tags in place for security fixes, so the default image is pulled
/// on every start; a caller's own pull policy, build or repository is left alone.
/// </summary>
[Trait("Category", "Unit")]
public class DocumentDBImagePullPolicyTests
{
    [Fact]
    public void TheDefaultImageIsAlwaysPulled()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB");

        Assert.Equal(ImagePullPolicy.Always, Assert.Single(PullPolicies(documentDB.Resource)).ImagePullPolicy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheOfficialImageIsStillAlwaysPulledAfterTheBeforeStartPhase(bool viaMirror)
    {
        var appBuilder = CreateRunBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithDocumentDBVersion(DocumentDBVersion.V0_117_0)
            .WithPostgresVersion(DocumentDBPostgresVersion.Pg16);
        if (viaMirror)
        {
            documentDB.WithImageRegistry("mirror.example");
        }

        using var app = appBuilder.Build();
        await RunBeforeStartPhaseAsync(app);

        Assert.Equal(ImagePullPolicy.Always, Assert.Single(PullPolicies(documentDB.Resource)).ImagePullPolicy);
    }

    [Theory]
    [InlineData(ImagePullPolicy.Default)]
    [InlineData(ImagePullPolicy.Missing)]
    [InlineData(ImagePullPolicy.Never)]
    public async Task AnExplicitPullPolicyWins(ImagePullPolicy pullPolicy)
    {
        var appBuilder = CreateRunBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithImagePullPolicy(pullPolicy)
            .WithDocumentDBVersion(DocumentDBVersion.V1_0_0)
            .WithPostgresVersion(DocumentDBPostgresVersion.Pg18);

        using var app = appBuilder.Build();
        await RunBeforeStartPhaseAsync(app);

        Assert.Equal(pullPolicy, Assert.Single(PullPolicies(documentDB.Resource)).ImagePullPolicy);
    }

    [Fact]
    public async Task AnExplicitPullPolicySetFromABeforeStartSubscriberWins()
    {
        var appBuilder = CreateRunBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB");
        appBuilder.Eventing.Subscribe<BeforeStartEvent>((_, _) =>
        {
            documentDB.WithImagePullPolicy(ImagePullPolicy.Missing);
            return Task.CompletedTask;
        });

        using var app = appBuilder.Build();
        await RunBeforeStartPhaseAsync(app);

        Assert.Equal(ImagePullPolicy.Missing, Assert.Single(PullPolicies(documentDB.Resource)).ImagePullPolicy);
    }

    [Theory]
    [InlineData("custom-image")]
    [InlineData("dockerfile")]
    public async Task ACallersOwnImageKeepsTheRuntimeDefault(string origin)
    {
        // Such an image may exist only locally, where a forced pull would fail the start.
        var appBuilder = CreateRunBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB");
        if (origin == "dockerfile")
        {
            documentDB.WithDockerfile(AppContext.BaseDirectory);
        }
        else
        {
            documentDB.WithImage("my-documentdb", "dev").WithImageRegistry(null);
        }

        using var app = appBuilder.Build();
        await RunBeforeStartPhaseAsync(app);

        Assert.Empty(PullPolicies(documentDB.Resource));
    }

    [Fact]
    public async Task ACallersOwnImageKeepsAnExplicitAlways()
    {
        var appBuilder = CreateRunBuilder();
        var documentDB = appBuilder.AddDocumentDB("DocumentDB")
            .WithImage("my-documentdb", "dev")
            .WithImagePullPolicy(ImagePullPolicy.Always);

        using var app = appBuilder.Build();
        await RunBeforeStartPhaseAsync(app);

        Assert.Equal(ImagePullPolicy.Always, Assert.Single(PullPolicies(documentDB.Resource)).ImagePullPolicy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APublishTreatsTheImageAsARunDoes(bool callersOwnImage)
    {
        var appBuilder = DistributedApplication.CreateBuilder(
            ["--operation", "publish", "--publisher", "manifest", "--output-path", "./"]);
        var documentDB = appBuilder.AddDocumentDB("DocumentDB");
        if (callersOwnImage)
        {
            documentDB.WithImage("my-documentdb", "dev");
        }

        using var app = appBuilder.Build();
        await RunBeforeStartPhaseAsync(app);

        if (callersOwnImage)
        {
            Assert.Empty(PullPolicies(documentDB.Resource));
        }
        else
        {
            Assert.Equal(ImagePullPolicy.Always, Assert.Single(PullPolicies(documentDB.Resource)).ImagePullPolicy);
        }
    }

    private static ContainerImagePullPolicyAnnotation[] PullPolicies(IResource resource) =>
        [.. resource.Annotations.OfType<ContainerImagePullPolicyAnnotation>()];

    private static IDistributedApplicationBuilder CreateRunBuilder() =>
        DistributedApplication.CreateBuilder(
            ["DcpPublisher:CliPath=dcp", "DcpPublisher:DashboardPath=dashboard"]);

    // BeforeStartEvent to every subscriber first, then every lifecycle hook, as Aspire does.
    private static async Task RunBeforeStartPhaseAsync(DistributedApplication app)
    {
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        await app.Services.GetRequiredService<IDistributedApplicationEventing>()
            .PublishAsync(new BeforeStartEvent(app.Services, appModel), CancellationToken.None);

#pragma warning disable CS0618 // The package's lifecycle hook runs after every BeforeStartEvent subscriber.
        foreach (var hook in app.Services.GetServices<IDistributedApplicationLifecycleHook>())
        {
            await hook.BeforeStartAsync(appModel, CancellationToken.None);
        }
#pragma warning restore CS0618
    }
}
