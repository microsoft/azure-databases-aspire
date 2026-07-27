// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
using Xunit;

namespace Aspire.Hosting.DocumentDB.Tests;

/// <summary>
/// Cross-language drift guards: things only a .NET test can check, because they compare the
/// compiled enums (and the public-API baseline) against files that the C# compiler never sees —
/// the Python version-automation script and the Markdown documentation.
///
/// <para>
/// Behavioral verification of the script itself lives in <c>eng/scripts/tests/</c>, where the
/// real functions are executed. Nothing here should try to assert what the script *does*; these
/// tests only assert that the two worlds agree on the same set of versions and PG variants.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class VersionAutomationScriptTests
{
    private static readonly string s_repoRoot = FindRepoRoot();

    /// <summary>
    /// Walks up from the test assembly location until it finds the repository root. Avoids
    /// baking build-machine paths into the assembly, so a build here / test there topology
    /// still works.
    /// </summary>
    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "global.json")) ||
                Directory.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException(
            $"Could not locate the repository root: walked up from '{AppContext.BaseDirectory}' " +
            "looking for a directory containing 'global.json' or '.git'.");
    }

    private static string ReadRepoFile(params string[] relativeSegments)
    {
        var path = Path.Combine([s_repoRoot, .. relativeSegments]);
        Assert.True(File.Exists(path), $"Expected repository file not found: {path}");
        return File.ReadAllText(path);
    }

    private static string ReadScript() => ReadRepoFile("eng", "scripts", "check-documentdb-versions.py");

    private static int[] PgVariantsInEnum() =>
        Enum.GetValues<DocumentDBPostgresVersion>().Select(v => (int)v).Distinct().Order().ToArray();

    /// <summary>
    /// Parses a <c>frozenset({...})</c> constant out of the Python script. Anchored on the
    /// identifier and non-greedy up to the first <c>{...}</c> so a trailing comment (or a
    /// reformat that moves the literal onto its own line) cannot silently change the result.
    /// An empty <c>frozenset()</c> parses to an empty array.
    /// </summary>
    private static int[] ParsePgSet(string script, string constantName)
    {
        var match = Regex.Match(
            script,
            $@"^{Regex.Escape(constantName)}[^=]*=\s*frozenset\(\s*(?:\{{(?<items>[^}}]*)\}})?\s*\)",
            RegexOptions.Multiline | RegexOptions.Singleline);

        Assert.True(match.Success,
            $"Could not parse the {constantName} assignment in eng/scripts/check-documentdb-versions.py. " +
            "If the file was reformatted, update this regex to match the new layout.");

        return match.Groups["items"].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(int.Parse)
            .Order()
            .ToArray();
    }

    private static int[] ParseRequiredPgSet(string script) => ParsePgSet(script, "REQUIRED_PG_SET");

    [Fact]
    public void RequiredPgSetIsASubsetOfDocumentDBPostgresVersion()
    {
        // Subset, not equality, on purpose. DocumentDBPostgresVersion is append-only (a shipped
        // user-selectable option can never be removed), while REQUIRED_PG_SET is the adoption
        // gate and may legitimately shrink when upstream drops a variant, or lag a newly added
        // option. What must never happen is gating adoption on a variant users cannot select.
        // The reverse direction (an enum member the gate never heard about) is covered by
        // EveryPgVariantIsRequiredOrExplicitlyDeferred.
        var enumVariants = PgVariantsInEnum();
        var required = ParseRequiredPgSet(ReadScript());

        var notSelectable = required.Except(enumVariants).ToArray();
        Assert.True(
            notSelectable.Length == 0,
            $"REQUIRED_PG_SET gates adoption on PG variant(s) [{string.Join(", ", notSelectable)}] " +
            "that have no DocumentDBPostgresVersion member, so no user could select them.");
    }

    [Fact]
    public void EveryPgVariantIsRequiredOrExplicitlyDeferred()
    {
        // This is the guard for the drift that started all of this: Pg18 was added to the enum
        // while REQUIRED_PG_SET stayed {15, 16, 17}, and nothing noticed. Because the gate is
        // allowed to lag the enum, the rule is "accounted for", not "equal": a new variant must
        // either join REQUIRED_PG_SET or be listed in DEFERRED_PG_SET with a reason.
        var script = ReadScript();
        var accountedFor = ParseRequiredPgSet(script).Concat(ParsePgSet(script, "DEFERRED_PG_SET")).ToHashSet();

        var unaccounted = PgVariantsInEnum().Where(pg => !accountedFor.Contains(pg)).ToArray();

        Assert.True(
            unaccounted.Length == 0,
            $"DocumentDBPostgresVersion member(s) [{string.Join(", ", unaccounted.Select(pg => $"Pg{pg}"))}] " +
            "appear in neither REQUIRED_PG_SET nor DEFERRED_PG_SET in " +
            "eng/scripts/check-documentdb-versions.py. Add them to REQUIRED_PG_SET once upstream " +
            "publishes the tags for every version you intend to adopt, or to DEFERRED_PG_SET " +
            "(with a comment) if adoption must not depend on them yet.");
    }

    [Theory]
    // The script's own docstring: "REQUIRED_PG_SET (currently {15, 16, 17, 18})".
    [InlineData(@"REQUIRED_PG_SET \(currently \{(?<items>[^}]*)\}\)", "eng", "scripts", "check-documentdb-versions.py")]
    // eng/scripts/README.md: "the script-level constant `REQUIRED_PG_SET` (currently `{15, 16, 17, 18}`; …".
    [InlineData(@"`REQUIRED_PG_SET`\s+\(currently\s+`\{(?<items>[^}]*)\}`", "eng", "scripts", "README.md")]
    public void ProseRestatementsOfRequiredPgSetMatchTheConstant(string pattern, params string[] relativeSegments)
    {
        // Spelling the set out in prose is exactly the hand-maintained-list drift these guards
        // exist to catch, so every restatement of it needs a row here — the README copy was
        // unguarded, and the "Adding a PG variant" table listing guards for the neighbouring
        // rows made that easy to miss.
        var text = ReadRepoFile(relativeSegments);

        var match = Regex.Match(text, pattern, RegexOptions.Singleline);
        Assert.True(match.Success,
            $"Could not locate the REQUIRED_PG_SET restatement in {string.Join("/", relativeSegments)}. " +
            "If the file was reformatted, update this pattern to match the new layout.");

        var documented = match.Groups["items"].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(int.Parse)
            .Order()
            .ToArray();

        Assert.Equal(ParseRequiredPgSet(ReadScript()), documented);
    }

    [Theory]
    [InlineData("eng", "scripts", "README.md")]
    [InlineData("docs", "configuration.md")]
    public void AdoptionPolicyDocsListExactlyTheRequiredPgVariants(params string[] relativeSegments)
    {
        // Both files state the "a version is only adopted when every pgNN-X.Y.Z tag exists"
        // policy, which must stay consistent with REQUIRED_PG_SET. Compared for equality like
        // its sibling guards, not containment: the gate may legitimately SHRINK when upstream
        // drops a variant, and a one-directional check would leave both files advertising the
        // dropped variant as an adoption requirement forever.
        var text = ReadRepoFile(relativeSegments);

        // The policy sentence spells the variants out as one contiguous run of `pgNN-X.Y.Z`.
        // Other spots quote a single such tag for unrelated reasons (the end-to-end AppHost
        // pin), so compare the longest run rather than every match in the file.
        var runs = Regex.Matches(text, @"`pg\d+-X\.Y\.Z`(?:,\s+(?:and\s+)?`pg\d+-X\.Y\.Z`)*");
        Assert.True(runs.Count > 0,
            $"Could not find the pgNN-X.Y.Z adoption-policy list in {string.Join("/", relativeSegments)}.");

        var listed = Regex.Matches(runs.MaxBy(m => m.Length)!.Value, @"`pg(?<pg>\d+)-X\.Y\.Z`")
            .Select(m => int.Parse(m.Groups["pg"].Value))
            .Distinct()
            .Order()
            .ToArray();

        Assert.Equal(ParseRequiredPgSet(ReadScript()), listed);
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("src", "Aspire.Hosting.DocumentDB", "README.md")]
    public void ReadmeApiTableListsEverySelectablePgVariant(params string[] relativeSegments)
    {
        // e.g. "Choose PG15/16/17/18 backend variant (default: Pg17)" - hand-maintained prose
        // that silently went stale when Pg18 shipped. Compared for equality, not containment, so
        // over-listing (announcing a variant that does not exist) fails too.
        var text = ReadRepoFile(relativeSegments);

        var match = Regex.Match(text, @"PG(?<items>\d+(?:/\d+)*) backend variant");
        Assert.True(match.Success,
            $"Could not find the \"PGnn/nn backend variant\" list in {string.Join("/", relativeSegments)}.");

        var listed = match.Groups["items"].Value.Split('/').Select(int.Parse).Order().ToArray();
        Assert.Equal(PgVariantsInEnum(), listed);
    }

    [Theory]
    [InlineData("| `pgVersion` |")]
    [InlineData("| `enum DocumentDBPostgresVersion` |")]
    public void ConfigurationDocRowsListEverySelectablePgVariant(string rowPrefix)
    {
        var row = ReadRepoFile("docs", "configuration.md")
            .Split('\n')
            .FirstOrDefault(line => line.TrimStart().StartsWith(rowPrefix, StringComparison.Ordinal));

        Assert.True(row is not null, $"Could not find a row starting with \"{rowPrefix}\" in docs/configuration.md.");

        // Set equality: the row may mention a variant more than once (e.g. a caveat about Pg18),
        // but it must not omit a selectable variant or advertise one that does not exist.
        var listed = Regex.Matches(row, @"`Pg(?<pg>\d+)`")
            .Select(m => int.Parse(m.Groups["pg"].Value))
            .Distinct()
            .Order()
            .ToArray();

        Assert.Equal(PgVariantsInEnum(), listed);
    }

    [Fact]
    public void PostgresEndToEndAppPinsTheCurrentLatestVersion()
    {
        // The AppHost pins an explicit tag because it gates the NuGet publish workflow, and the
        // pin plus its explanatory comments are hand-maintained in two files. Nothing else would
        // notice if the pin were left behind at an older version.
        var program = ReadRepoFile("tests", "Aspire.Hosting.DocumentDB.PostgresEndToEndApp", "Program.cs");

        var match = Regex.Match(program, @"WithImageTag\(""(?<tag>pg\d+-(?<version>[\d.]+))""\)");
        Assert.True(match.Success, "Could not find the WithImageTag(...) pin in the Postgres end-to-end AppHost.");

        Assert.True(
            match.Groups["version"].Value == DocumentDBVersions.Latest,
            $"The Postgres end-to-end AppHost pins '{match.Groups["tag"].Value}' but " +
            $"DocumentDBVersions.Latest is {DocumentDBVersions.Latest}. Bump the pin (and the " +
            "comments referencing it) as part of adopting a new DocumentDB version.");

        var integrationTests = ReadRepoFile("tests", "Aspire.Hosting.DocumentDB.Tests", "DocumentDBIntegrationTests.cs");
        Assert.Contains(match.Groups["tag"].Value, integrationTests, StringComparison.Ordinal);
    }

    [Fact]
    public void DocsQuoteTheCurrentDefaultImageTag()
    {
        // Two documentation spots state the *current* default tag as a fact rather than as a
        // formula, so they silently go stale on every version adoption (both did before this
        // branch). Everything else that mentions an older tag - the WithPostgresEndpoint 0.112.0
        // floor examples, historical CHANGELOG entries - is intentionally version-specific and
        // deliberately not covered here.
        var defaultTag = DocumentDBContainerImageTags.Tag;

        var defaultsRow = ReadRepoFile("docs", "configuration.md")
            .Split('\n')
            .FirstOrDefault(line => line.TrimStart().StartsWith("| Image tag |", StringComparison.Ordinal));
        Assert.True(defaultsRow is not null, "Could not find the \"| Image tag |\" defaults row in docs/configuration.md.");
        Assert.True(defaultsRow!.Contains(defaultTag, StringComparison.Ordinal),
            $"The defaults table in docs/configuration.md does not quote the current default tag '{defaultTag}': {defaultsRow.Trim()}");

        var troubleshooting = ReadRepoFile("docs", "troubleshooting.md");
        Assert.True(troubleshooting.Contains($"documentdb-local:{defaultTag}", StringComparison.Ordinal),
            $"The 'docker pull' example in docs/troubleshooting.md does not use the current default tag '{defaultTag}'.");
    }

    [Fact]
    public void ApiBaselineListsEveryPublicEnumMember()
    {
        // The version-detection workflow appends members to DocumentDBVersion.cs but deliberately
        // never edits the public-API baseline; the maintainer does that by hand on the auto-PR.
        // This test is what makes that documented gate real - it fails until the baseline is
        // updated, so an auto-PR cannot merge with a stale public-API record.
        var baseline = ReadRepoFile("src", "Aspire.Hosting.DocumentDB", "api", "Aspire.Hosting.DocumentDB.cs");

        foreach (var version in Enum.GetValues<DocumentDBVersion>())
        {
            AssertBaselineDeclaresMember(baseline, version.ToString(), (int)version);
            Assert.Contains($"public const string {version} = ", baseline, StringComparison.Ordinal);
        }

        foreach (var pg in Enum.GetValues<DocumentDBPostgresVersion>())
        {
            AssertBaselineDeclaresMember(baseline, pg.ToString(), (int)pg);
        }
    }

    /// <summary>
    /// Asserts the baseline declares <paramref name="member"/> with <paramref name="value"/>.
    /// The trailing comma is optional: dropping it on the last member of an enum is valid C# and
    /// must not fail this guard.
    /// </summary>
    private static void AssertBaselineDeclaresMember(string baseline, string member, int value)
    {
        var declared = Regex.IsMatch(baseline, $@"\b{Regex.Escape(member)}\s*=\s*{value}\s*[,}}\r\n]");

        Assert.True(declared,
            $"The public API baseline (src/Aspire.Hosting.DocumentDB/api/Aspire.Hosting.DocumentDB.cs) " +
            $"does not declare '{member} = {value}'. Add it by hand - the baseline is the " +
            "human-reviewed record of public API changes and is never written by automation.");
    }
}
