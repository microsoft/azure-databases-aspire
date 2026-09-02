// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.DocumentDB.ImageSealEndToEndApp;

/// <summary>
/// A scenario-driven AppHost for the image seal: the claim that the image this package judges is
/// the image the container runtime is actually given.
/// </summary>
/// <remarks>
/// Every scenario here is about ordering rather than configuration, so each one has to run through
/// a real orchestration: Aspire composes the image reference once, in
/// <c>ContainerCreator.PrepareObjects()</c>, and writes it into the DCP container spec before any
/// per-resource event is raised. Only a started application can show what that produced.
/// </remarks>
public class Program
{
    public const string ScenarioEnvironmentVariable = "DOCUMENTDB_IMAGE_SEAL_SCENARIO";

    /// <summary>The DocumentDB version the positive scenarios settle on.</summary>
    public const string SealedTag = "pg17-0.116.0";

    /// <summary>The tag the pre-start scenario starts from and must not run.</summary>
    public const string SupersededTag = "pg17-0.114.0";

    /// <summary>The tag the downgrade scenario seals and the late subscriber tries to leave.</summary>
    public const string DowngradeTag = "pg17-0.114.0";

    /// <summary>The tag the upgrade scenario seals: below the WithPostgresEndpoint() floor.</summary>
    public const string BelowFloorTag = "pg17-0.111.0";

    public const string ResourceName = "documentdb";

    /// <summary>The plain container the negative control runs, and the tag it must actually launch.</summary>
    public const string ProbeResourceName = "imageprobe";
    public const string ProbeImage = "docker.io/library/alpine";
    public const string ProbeSealedTag = "3.20";
    public const string ProbeMutatedTag = "3.19";

    /// <summary>Nothing touches the image. The container must run the tag the model was built with.</summary>
    public const string UnmutatedScenario = "unmutated";

    /// <summary>
    /// A <c>BeforeStartEvent</c> subscriber registered after <c>AddDocumentDB</c> replaces the tag.
    /// That is ordinary pre-start configuration: it must be accepted, and it must be the tag the
    /// container runtime is given.
    /// </summary>
    public const string PreStartMutationScenario = "pre-start-mutation";

    /// <summary>
    /// A <c>ResourceEndpointsAllocatedEvent</c> subscriber upgrades away from an image below the
    /// WithPostgresEndpoint() credential floor. The floors would clear the upgraded tag while the
    /// prepared container still runs the one that cannot authenticate.
    /// </summary>
    public const string LateUpgradeScenario = "late-upgrade";

    /// <summary>
    /// The reverse: a late subscriber downgrades, so the model believes a newer release than the
    /// container runtime was given.
    /// </summary>
    public const string LateDowngradeScenario = "late-downgrade";

    /// <summary>
    /// The negative control. A plain Aspire container - no DocumentDB resource, so no code of this
    /// package is involved - mutates its own image from a <c>ResourceEndpointsAllocatedEvent</c>
    /// subscriber. The container must launch the pre-mutation image, which is the ordering fact
    /// the seal exists for.
    /// </summary>
    public const string NegativeControlScenario = "negative-control";

    public static async Task Main(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);
        var scenario = GetScenario();

        if (scenario == NegativeControlScenario)
        {
            var probe = builder.AddContainer(ProbeResourceName, ProbeImage, ProbeSealedTag)
                .WithArgs("sleep", "3600");

            builder.Eventing.Subscribe<ResourceEndpointsAllocatedEvent>(probe.Resource, (_, _) =>
            {
                probe.WithImageTag(ProbeMutatedTag);
                return Task.CompletedTask;
            });

            await builder.Build().RunAsync();
            return;
        }

        var documentDB = builder.AddDocumentDB(ResourceName);

        switch (scenario)
        {
            case UnmutatedScenario:
                documentDB.WithImageTag(SealedTag);
                break;

            case PreStartMutationScenario:
                documentDB.WithImageTag(SupersededTag);
                builder.Eventing.Subscribe<BeforeStartEvent>((_, _) =>
                {
                    documentDB.WithImageTag(SealedTag);
                    return Task.CompletedTask;
                });
                break;

            case LateUpgradeScenario:
                documentDB.WithImageTag(BelowFloorTag).WithPostgresEndpoint();
                SubscribeLateImageChange(builder, documentDB, SealedTag);
                break;

            case LateDowngradeScenario:
                documentDB.WithImageTag(SealedTag);
                SubscribeLateImageChange(builder, documentDB, DowngradeTag);
                break;

            case var unknown:
                throw new InvalidOperationException(
                    $"{ScenarioEnvironmentVariable} must name a known scenario, but was '{unknown}'.");
        }

        documentDB.AddDatabase("appdb");

        await builder.Build().RunAsync();
    }

    /// <summary>
    /// Replaces the image from the earliest per-resource event Aspire raises after the image has
    /// already been written into the DCP container spec.
    /// </summary>
    private static void SubscribeLateImageChange(
        IDistributedApplicationBuilder builder,
        IResourceBuilder<DocumentDBServerResource> documentDB,
        string tag) =>
        builder.Eventing.Subscribe<ResourceEndpointsAllocatedEvent>(documentDB.Resource, (_, _) =>
        {
            documentDB.WithImageTag(tag);
            return Task.CompletedTask;
        });

    private static string GetScenario()
    {
        var scenario = Environment.GetEnvironmentVariable(ScenarioEnvironmentVariable);

        return string.IsNullOrWhiteSpace(scenario)
            ? throw new InvalidOperationException($"{ScenarioEnvironmentVariable} must be set.")
            : scenario;
    }
}
