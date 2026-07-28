// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.DocumentDB.AdaptationEndToEndApp;

/// <summary>
/// A scenario-driven AppHost covering the DocumentDB 0.114 adaptation claims that only had
/// event-level (in-process) coverage: the Pg18 availability floor and the corrected TLS story.
/// The scenario is selected through <see cref="ScenarioEnvironmentVariable"/> so a single AppHost
/// project serves every case rather than one project per permutation.
/// </summary>
public class Program
{
    public const string ScenarioEnvironmentVariable = "DOCUMENTDB_ADAPTATION_SCENARIO";

    /// <summary>Pg18 paired with the first version that publishes it. Must start and serve traffic.</summary>
    public const string Pg18SupportedScenario = "pg18-supported";

    /// <summary>Pg18 paired with a version predating pg18- images. Must fail at startup, not at pull time.</summary>
    public const string Pg18BelowFloorScenario = "pg18-below-floor";

    /// <summary>Default image, TLS off in the connection string. Valid from 0.114.0 (TLS_MODE=allowTLS).</summary>
    public const string PlaintextScenario = "plaintext";

    /// <summary>TLS_MODE=requireTLS with a plain connection string: the documented self-contradiction.</summary>
    public const string RequireTlsWithPlaintextClientScenario = "require-tls-plaintext-client";

    /// <summary>TLS_MODE=requireTLS with a TLS connection string: the supported opt-in.</summary>
    public const string RequireTlsScenario = "require-tls";

    public static async Task Main(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        var documentDB = builder.AddDocumentDB("documentdb");

        switch (GetScenario())
        {
            case Pg18SupportedScenario:
                documentDB
                    .WithPostgresVersion(DocumentDBPostgresVersion.Pg18)
                    .WithDocumentDBVersion(DocumentDBVersion.V0_114_0);
                break;

            case Pg18BelowFloorScenario:
                documentDB
                    .WithPostgresVersion(DocumentDBPostgresVersion.Pg18)
                    .WithDocumentDBVersion(DocumentDBVersion.V0_113_0);
                break;

            case PlaintextScenario:
                documentDB.UseTls(false);
                break;

            case RequireTlsWithPlaintextClientScenario:
                documentDB
                    .UseTls(false)
                    .WithEnvironment("TLS_MODE", "requireTLS");
                break;

            case RequireTlsScenario:
                documentDB
                    .UseTls(true)
                    .AllowInsecureTls()
                    .WithEnvironment("TLS_MODE", "requireTLS");
                break;

            case var unknown:
                throw new InvalidOperationException(
                    $"{ScenarioEnvironmentVariable} must name a known scenario, but was '{unknown}'.");
        }

        documentDB.AddDatabase("appdb");

        var app = builder.Build();

        await app.RunAsync();
    }

    private static string GetScenario()
    {
        var scenario = Environment.GetEnvironmentVariable(ScenarioEnvironmentVariable);

        return string.IsNullOrWhiteSpace(scenario)
            ? throw new InvalidOperationException($"{ScenarioEnvironmentVariable} must be set.")
            : scenario;
    }
}
