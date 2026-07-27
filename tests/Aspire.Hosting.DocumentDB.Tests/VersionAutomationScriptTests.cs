// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
using Xunit;

namespace Aspire.Hosting.DocumentDB.Tests;

/// <summary>
/// Drift guards between the C# <see cref="DocumentDBPostgresVersion"/> enum and the
/// version-detection automation that feeds it.
///
/// <para>
/// <c>eng/scripts/README.md</c> documents that adding a PG variant is a deliberate manual change
/// in three places: the enum, the script constant <c>REQUIRED_PG_SET</c>, and the documentation.
/// Nothing enforced that, and the set silently drifted when <c>Pg18</c> was added for DocumentDB
/// 0.114.0. These tests fail loudly on the next such drift.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class VersionAutomationScriptTests
{
    private static readonly string s_repoRoot = Assembly.GetExecutingAssembly()
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(a => a.Key == "RepoRoot")
        .Value!;

    private static string ReadRepoFile(params string[] relativeSegments)
    {
        var path = Path.Combine(new[] { s_repoRoot }.Concat(relativeSegments).ToArray());
        Assert.True(File.Exists(path), $"Expected repository file not found: {path}");
        return File.ReadAllText(path);
    }

    private static string ReadScript() => ReadRepoFile("eng", "scripts", "check-documentdb-versions.py");

    private static int[] ParseRequiredPgSet(string script)
    {
        var match = Regex.Match(script, @"^REQUIRED_PG_SET.*=\s*frozenset\(\{(?<values>[^}]*)\}\)", RegexOptions.Multiline);
        Assert.True(match.Success, "Could not locate the REQUIRED_PG_SET assignment in check-documentdb-versions.py.");

        return match.Groups["values"].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(int.Parse)
            .Order()
            .ToArray();
    }

    [Fact]
    public void RequiredPgSetMatchesDocumentDBPostgresVersionEnum()
    {
        var expected = Enum.GetValues<DocumentDBPostgresVersion>().Select(v => (int)v).Order().ToArray();

        Assert.Equal(expected, ParseRequiredPgSet(ReadScript()));
    }

    [Fact]
    public void ScriptDocstringListsTheSameRequiredPgSet()
    {
        var script = ReadScript();

        var docstring = Regex.Match(script, @"REQUIRED_PG_SET \(currently \{(?<values>[^}]*)\}\)");
        Assert.True(docstring.Success, "Could not locate the REQUIRED_PG_SET mention in the script docstring.");

        var documented = docstring.Groups["values"].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(int.Parse)
            .Order()
            .ToArray();

        Assert.Equal(ParseRequiredPgSet(script), documented);
    }

    [Fact]
    public void ChangelogTagListIsDerivedFromRequiredPgSet()
    {
        // The generated CHANGELOG line used to hardcode pg15/pg16/pg17, which is how it drifted
        // out of sync with REQUIRED_PG_SET. Keep it derived from the constant.
        var script = ReadScript();

        Assert.Contains("sorted(REQUIRED_PG_SET)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("`pg15-{v}`", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("eng", "scripts", "README.md")]
    [InlineData("docs", "configuration.md")]
    public void AdoptionPolicyDocsMentionEveryRequiredPgVariant(params string[] relativeSegments)
    {
        // Both files state the "a version is only adopted when every pgNN-X.Y.Z tag exists"
        // policy, which must stay consistent with REQUIRED_PG_SET.
        var text = ReadRepoFile(relativeSegments);

        foreach (var pg in ParseRequiredPgSet(ReadScript()))
        {
            Assert.Contains($"pg{pg}-X.Y.Z", text, StringComparison.Ordinal);
        }
    }
}
