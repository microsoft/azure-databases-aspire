// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Aspire.Hosting.DocumentDB.Tests;

/// <summary>
/// Pins what the required <c>ci-gate</c> check stands for: it runs on every change to main, waits
/// for every job, and those jobs select every test the assembly declares.
/// </summary>
[Trait("Category", "Unit")]
public class WorkflowContractTests
{
    private static readonly YamlMappingNode s_buildAndTest = LoadWorkflow("build-and-test.yml");
    private static readonly YamlMappingNode s_nightly = LoadWorkflow("nightly.yml");

    [Fact]
    public void BuildAndTestRunsOnEveryPullRequestAndPushToMain()
    {
        var on = Map(s_buildAndTest, "on");

        Assert.Equal(["main"], Strings(Map(on, "pull_request")["branches"]));
        Assert.Equal(["main"], Strings(Map(on, "push")["branches"]));

        // A path- or type-filtered run never reports ci-gate, so the change is either blocked or ungated.
        Assert.DoesNotContain(Keys(on), key => key is "paths" or "paths-ignore" or "types");
    }

    [Fact]
    public void CiGateAlwaysRunsAndNeedsEveryJobButThePilot()
    {
        var jobs = Map(s_buildAndTest, "jobs");
        var gate = Map(jobs, "ci-gate");

        Assert.Equal("always()", Scalar(gate["if"]));
        Assert.Equal(
            jobs.Children.Keys.Select(Scalar).Except(["ci-gate", "unit-test-pilot"]).Order(),
            Strings(gate["needs"]).Order());

        // Named, so removing a job together with its needs entry still fails here.
        Assert.Subset(
            Strings(gate["needs"]).ToHashSet(),
            new HashSet<string> { "lint", "unit-test", "script-test", "package", "consumer-smoke", "integration-test", "version-smoke", "backend" });

        // The verdict itself: a skipped or loosened check passes whatever its needs did.
        var step = (YamlMappingNode)Assert.Single(Steps(gate));
        Assert.Equal(["name", "env", "run"], step.Children.Keys.Select(Scalar));
        Assert.Equal("${{ toJSON(needs) }}", Scalar(Map(step, "env")["NEEDS"]));
        Assert.Equal(
            [
                """echo "$NEEDS" | jq -r 'to_entries[] | "\(.key): \(.value.result)"'""",
                """echo "$NEEDS" | jq -e 'length > 0 and all(.[]; .result == "success")' > /dev/null""",
            ],
            Run(step).TrimEnd().Split('\n'));

        // A job or step that continues on error reaches ci-gate as a success.
        Assert.DoesNotContain("continue-on-error", Keys(s_buildAndTest));
    }

    [Fact]
    public void IntegrationMatrixRunsExactlyTheShardsTestsAreIn()
    {
        var matrix = Map(Map(Map(Map(s_buildAndTest, "jobs"), "integration-test"), "strategy"), "matrix");

        Assert.Equal(
            TestSelectionGuardTests.Tests.SelectMany(test => test.Traits["Shard"]).Distinct().Order(),
            ((YamlSequenceNode)matrix["include"]).Select(leg => Scalar(((YamlMappingNode)leg)["shard"])).Order());
    }

    [Fact]
    public void SmokeAndBackendMatricesRunEveryCellAndBackend()
    {
        var jobs = Map(s_buildAndTest, "jobs");
        var legs = DocumentDBVersionSmokeCatalogTests.CiLegCount;

        // CiLegsRunEveryCellExactlyOnce only holds if CI runs legs 1..CiLegCount, each told that count.
        Assert.Equal(Enumerable.Range(1, legs).Select(leg => $"{leg}").Order(), MatrixValues(jobs, "version-smoke", "leg").Order());
        Assert.Equal("${{ matrix.leg }}/" + legs, TestStepEnv(jobs, "version-smoke", DocumentDBVersionSmokeTests.LegEnvironmentVariable));

        var backends = DocumentDBBackendCompactTests.Backends.Select(backend => $"{(int)backend}");
        Assert.Equal(backends.Order(), MatrixValues(jobs, "backend", "pg").Order());
        Assert.Equal("${{ matrix.pg }}", TestStepEnv(jobs, "backend", DocumentDBBackendCompactTests.PostgresEnvironmentVariable));

        static string[] MatrixValues(YamlMappingNode jobs, string job, string key) =>
            Strings(Map(Map(Map(jobs, job), "strategy"), "matrix")[key]);

        static string TestStepEnv(YamlMappingNode jobs, string job, string variable) =>
            Scalar(Map((YamlMappingNode)Steps(Map(jobs, job)).Single(step => IsDotnetTest(Run(step))), "env")[variable]);
    }

    [Fact]
    public void UnitAndIntegrationJobsFilterOnTheirCategory()
    {
        Assert.Equal(["Category=Unit"], Filters(s_buildAndTest, "unit-test"));
        Assert.Equal(["Category=Integration&Shard=${{ matrix.shard }}"], Filters(s_buildAndTest, "integration-test"));
    }

    [Fact]
    public void EveryTestCategoryIsSelectedByAGatingJobOrNightly()
    {
        var gating = Strings(Map(Map(s_buildAndTest, "jobs"), "ci-gate")["needs"]).SelectMany(job => Filters(s_buildAndTest, job));
        var nightly = Map(s_nightly, "jobs").Children.Keys.Select(Scalar).SelectMany(job => Filters(s_nightly, job));

        var unselected = TestSelectionGuardTests.Tests
            .SelectMany(test => test.Traits["Category"])
            .Distinct()
            .Where(category => !SelectedCategories(category == "Nightly" ? nightly : gating).Contains(category))
            .ToArray();

        Assert.True(
            unselected.Length == 0,
            $"No dotnet test filter in a ci-gate job (nightly.yml for Nightly) selects Category {string.Join(", ", unselected)}.");
    }

    [Theory]
    [InlineData("build-and-test.yml")]
    [InlineData("nightly.yml")]
    public void EveryDotnetTestStepIsFollowedByAResultsCheck(string workflow)
    {
        // dotnet test passes when its filter matches nothing; check-test-results.py does not, so it must run whenever the tests do.
        var unverified =
            (from job in Map(LoadWorkflow(workflow), "jobs").Children
             let steps = Steps((YamlMappingNode)job.Value).ToArray()
             from index in Enumerable.Range(0, steps.Length)
             where IsDotnetTest(Run(steps[index])) && !steps.Skip(index + 1).Any(step =>
                 Run(step).Contains("check-test-results.py", StringComparison.Ordinal) && Field(step, "if") == Field(steps[index], "if"))
             select $"{Scalar(job.Key)} step {index + 1}").ToArray();

        Assert.True(unverified.Length == 0, $"dotnet test without a later check-test-results.py step under the same if: {string.Join(", ", unverified)}");
    }

    private static YamlMappingNode LoadWorkflow(string name)
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(VersionAutomationScriptTests.ReadRepoFile(".github", "workflows", name)));
        return (YamlMappingNode)yaml.Documents[0].RootNode;
    }

    /// <summary>The <c>--filter</c> of every dotnet test step in a job, with <c>$VAR</c> env references expanded.</summary>
    private static IEnumerable<string> Filters(YamlMappingNode workflow, string jobName)
    {
        var job = Map(Map(workflow, "jobs"), jobName);

        foreach (var step in Steps(job).Where(step => IsDotnetTest(Run(step))))
        {
            var env = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var scope in new[] { workflow, job, (YamlMappingNode)step })
            {
                if (scope.Children.TryGetValue("env", out var variables))
                {
                    foreach (var (name, value) in ((YamlMappingNode)variables).Children)
                    {
                        env[Scalar(name)] = Scalar(value);
                    }
                }
            }

            var run = Regex.Replace(Run(step), @"\$\{?(?<name>\w+)\}?", match => env.GetValueOrDefault(match.Groups["name"].Value, match.Value));
            foreach (Match filter in Regex.Matches(run, @"--filter\s+""(?<filter>[^""]*)"""))
            {
                yield return filter.Groups["filter"].Value;
            }
        }
    }

    private static IEnumerable<string> SelectedCategories(IEnumerable<string> filters) =>
        filters.SelectMany(filter => Regex.Matches(filter, @"\bCategory=(?<category>\w+)")).Select(match => match.Groups["category"].Value);

    private static bool IsDotnetTest(string run) => Regex.IsMatch(run, @"\bdotnet\s+test\b");

    private static IEnumerable<YamlNode> Steps(YamlMappingNode job) => (YamlSequenceNode)job["steps"];

    private static string Run(YamlNode step) => Field(step, "run");

    private static string Field(YamlNode step, string key) =>
        ((YamlMappingNode)step).Children.TryGetValue(key, out var value) ? Scalar(value) : string.Empty;

    private static IEnumerable<string> Keys(YamlNode node) =>
        node.AllNodes.OfType<YamlMappingNode>().SelectMany(map => map.Children.Keys).Select(Scalar);

    private static YamlMappingNode Map(YamlMappingNode node, string key) => (YamlMappingNode)node[key];

    private static string Scalar(YamlNode node) => ((YamlScalarNode)node).Value ?? string.Empty;

    private static string[] Strings(YamlNode node) => node is YamlSequenceNode items ? [.. items.Select(Scalar)] : [Scalar(node)];
}
