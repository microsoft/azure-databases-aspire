// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.DocumentDB;

internal static partial class DocumentDBContainerImageTags
{
    /// <remarks>ghcr.io/documentdb</remarks>
    public const string Registry = "ghcr.io/documentdb";

    /// <remarks>documentdb/documentdb-local</remarks>
    public const string Image = "documentdb/documentdb-local";

    /// <summary>
    /// Default container tag for the <c>documentdb-local</c> image: <c>pg17-{Latest}</c>.
    /// Computed at runtime so it follows <see cref="DocumentDBVersions.Latest"/> rather than
    /// being baked in at compile time as a <see langword="const"/>.
    /// </summary>
    public static string Tag => $"pg{(int)DocumentDBPostgresVersion.Pg17}-{DocumentDBVersions.Latest}";

    /// <summary>
    /// The earliest <c>documentdb-local</c> DocumentDB version whose entrypoint passes
    /// the gateway-supplied <c>USERNAME</c>/<c>PASSWORD</c> through to PostgreSQL admin-user
    /// creation. On older images the entrypoint hard-codes <c>docdb_admin</c>/<c>Admin100</c>,
    /// so the Aspire-generated <c>postgresql://</c> connection string silently fails
    /// authentication. <see cref="Aspire.Hosting.DocumentDBBuilderExtensions.WithPostgresEndpoint"/>
    /// uses this floor at startup to convert that silent failure into a loud
    /// <see cref="InvalidOperationException"/>.
    /// </summary>
    internal static readonly Version MinimumPostgresEndpointVersion = new(0, 112, 0);

    // Anchored (^ ... \z, NOT $ which also matches before a final \n) and ASCII-only
    // ([0-9] not \d, so unicode-digit categories cannot slip through). Case-insensitive
    // on the "pg" prefix only. Requires exactly three numeric version segments; any
    // trailing pre-release ("-rc.1") or build-metadata ("+abc") suffix causes the match
    // to fail, so such tags are treated as "unknown" by callers (warn + skip) rather
    // than silently passing the version floor.
    [GeneratedRegex(@"^[pP][gG](?<pg>[0-9]+)-(?<v>[0-9]+\.[0-9]+\.[0-9]+)\z", RegexOptions.CultureInvariant)]
    private static partial Regex DocumentDBTagRegex();

    /// <summary>
    /// Attempts to parse a <c>documentdb-local</c> container image tag of the
    /// form <c>pg{NN}-X.Y.Z</c> (e.g., <c>pg17-0.112.0</c>) into its PostgreSQL
    /// major version and the DocumentDB semantic version.
    /// </summary>
    /// <param name="tag">The container image tag to parse. May be <see langword="null"/>.</param>
    /// <param name="pg">The PostgreSQL major version (e.g., <c>17</c>) on success.</param>
    /// <param name="docVersion">The DocumentDB semantic version (e.g., <c>0.112.0</c>) on success.</param>
    /// <returns>
    /// <see langword="true"/> if the tag matches the strict <c>pg{NN}-X.Y.Z</c> grammar.
    /// <see langword="false"/> for <see langword="null"/>, empty/whitespace, two-part
    /// versions (e.g., <c>pg17-0.112</c>), pre-release/build-metadata suffixes
    /// (e.g., <c>pg17-0.112.0-rc.1</c>), missing prefix (e.g., <c>0.112.0</c>),
    /// or any other custom/unrecognised tag (e.g., <c>latest</c>, <c>nightly</c>).
    /// Callers should treat <see langword="false"/> as "unknown — do not enforce
    /// version-floor policy" rather than as a failed assertion.
    /// </returns>
    internal static bool TryParseDocumentDBTag(
        string? tag,
        out int pg,
        [NotNullWhen(true)] out Version? docVersion)
    {
        pg = 0;
        docVersion = null;

        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        var match = DocumentDBTagRegex().Match(tag);
        if (!match.Success)
        {
            return false;
        }

        // The regex constrains both groups to ASCII digits, but int.TryParse still
        // protects against integer overflow on absurdly long pg-major strings (e.g.,
        // "pg99999999999999999999-0.112.0").
        if (!int.TryParse(match.Groups["pg"].ValueSpan, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pgParsed))
        {
            return false;
        }

        if (!Version.TryParse(match.Groups["v"].ValueSpan, out var parsedVersion))
        {
            return false;
        }

        pg = pgParsed;
        docVersion = parsedVersion;
        return true;
    }
}
