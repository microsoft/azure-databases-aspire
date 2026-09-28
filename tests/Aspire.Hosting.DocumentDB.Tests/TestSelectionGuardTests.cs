// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Xunit;

namespace Aspire.Hosting.DocumentDB.Tests;

/// <summary>
/// CI selects tests only by their Category and Shard traits, so a test with a missing, unknown or
/// duplicated one would run in no job, or in two.
/// </summary>
[Trait("Category", "Unit")]
public class TestSelectionGuardTests
{
    private static readonly string[] s_categories = ["Unit", "Integration", "VersionSmoke", "Backend", "Nightly"];
    private static readonly string[] s_shards = ["1", "2", "3", "4", "5"];

    // Nightly is the one category no PR job runs, so joining it is a reviewed edit to this owned file.
    private static readonly string[] s_nightlyClasses = [typeof(DocumentDBCrashRecoveryTests).FullName!];

    /// <summary>Every test method in this assembly, with its class- and method-level traits.</summary>
    internal static IReadOnlyList<(string Name, ILookup<string, string> Traits)> Tests { get; } =
    [
        .. from type in typeof(TestSelectionGuardTests).Assembly.GetTypes()
           // As xUnit runs them: never on an abstract (non-static) class, inherited ones under the derived class's traits.
           where !type.IsAbstract || type.IsSealed
           from method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
           where method.IsDefined(typeof(FactAttribute), inherit: true)
           select ($"{type.FullName}.{method.Name}", type.GetCustomAttributesData()
               .Concat(method.GetCustomAttributesData())
               .Where(attribute => attribute.AttributeType == typeof(TraitAttribute))
               .ToLookup(attribute => (string)attribute.ConstructorArguments[0].Value!, attribute => (string)attribute.ConstructorArguments[1].Value!)),
    ];

    [Fact]
    public void EveryTestHasExactlyOneKnownCategory()
    {
        AssertNone(
            Tests.Where(test => test.Traits["Category"].Count() != 1 || !s_categories.Contains(test.Traits["Category"].Single())),
            $"Every test needs exactly one Category trait out of {string.Join(", ", s_categories)}");
    }

    [Fact]
    public void OnlyIntegrationTestsHaveAShardAndEachHasExactlyOneKnownShard()
    {
        AssertNone(
            Tests.Where(test => IsIntegration(test)
                ? test.Traits["Shard"].Count() != 1 || !s_shards.Contains(test.Traits["Shard"].Single())
                : test.Traits["Shard"].Any()),
            $"Every Integration test needs exactly one Shard trait out of {string.Join(", ", s_shards)}, and no other test may have one");
    }

    [Fact]
    public void OnlyAllowlistedClassesRunNightlyOnly()
    {
        AssertNone(
            Tests.Where(test => test.Traits["Category"].Contains("Nightly") && !s_nightlyClasses.Any(type => test.Name.StartsWith(type + ".", StringComparison.Ordinal))),
            "These tests would leave every PR job; add their class to s_nightlyClasses only if that is intended");
    }

    [Fact]
    public void EveryShardHasAnIntegrationTest()
    {
        var empty = s_shards.Except(Tests.Where(IsIntegration).SelectMany(test => test.Traits["Shard"])).ToArray();

        Assert.True(empty.Length == 0, $"No Integration test is in shard {string.Join(", ", empty)}, so its CI leg would run nothing.");
    }

    private static bool IsIntegration((string Name, ILookup<string, string> Traits) test) =>
        test.Traits["Category"].Contains("Integration");

    private static void AssertNone(IEnumerable<(string Name, ILookup<string, string> Traits)> offenders, string rule)
    {
        var lines = offenders
            .Select(test => $"  {test.Name} [{string.Join(", ", test.Traits.SelectMany(trait => trait.Select(value => $"{trait.Key}={value}")))}]")
            .ToArray();

        Assert.True(lines.Length == 0, $"{rule}:{Environment.NewLine}{string.Join(Environment.NewLine, lines)}");
    }
}
