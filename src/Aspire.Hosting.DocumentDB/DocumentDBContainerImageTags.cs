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

    /// <summary>
    /// The earliest <c>documentdb-local</c> DocumentDB version whose <c>Dockerfile</c> declares
    /// <c>/data</c> as a container <c>VOLUME</c> and whose entrypoint claims the data directory
    /// with an exclusive <c>flock</c>. Both behaviours arrived together in <c>0.116-0</c>:
    /// <list type="bullet">
    /// <item><description>the declaration means a run that mounts nothing on <c>/data</c> gets an
    /// anonymous, never-reused volume the container runtime manages;</description></item>
    /// <item><description>the lock means a data directory backs at most one running container,
    /// and the loser refuses to start instead of corrupting the cluster.</description></item>
    /// </list>
    /// Images at or below <c>0.114.0</c> declare no volume (an unmounted <c>/data</c> lives in
    /// the writable container layer and is discarded with the container) and have no interlock,
    /// so <see cref="Aspire.Hosting.DocumentDBBuilderExtensions.WithDataVolume"/> and the
    /// data-storage guard scope their advice on this floor rather than stating it unconditionally.
    /// </summary>
    internal static readonly Version MinimumDeclaredDataVolumeVersion = new(0, 116, 0);

    /// <summary>
    /// The earliest DocumentDB version for which upstream publishes each PostgreSQL backend
    /// variant. Every combination of <see cref="DocumentDBVersion"/> and
    /// <see cref="DocumentDBPostgresVersion"/> produces a well-formed <c>pg{NN}-X.Y.Z</c> tag,
    /// but not every one of them exists: upstream only started building <c>pg18-</c> images at
    /// DocumentDB <c>0.114.0</c>, so <c>pg18-0.113.0</c> is selectable through the
    /// strongly-typed API and absent from GHCR. Without a floor the app fails at container-pull
    /// time with an opaque manifest-not-found error;
    /// <see cref="Aspire.Hosting.DocumentDBBuilderExtensions.AddDocumentDB(IDistributedApplicationBuilder, string, int?, IResourceBuilder{ParameterResource}?, IResourceBuilder{ParameterResource}?)"/>
    /// uses this map at startup to turn that into a loud, actionable
    /// <see cref="InvalidOperationException"/>. Variants absent from the map have no floor.
    /// </summary>
    internal static readonly IReadOnlyDictionary<int, Version> MinimumVersionByPgVariant =
        new Dictionary<int, Version>
        {
            [(int)DocumentDBPostgresVersion.Pg18] = new Version(0, 114, 0),
        };

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

    /// <summary>
    /// Whether a container image reference spelled as an Aspire
    /// <see cref="ContainerImageAnnotation"/> names the curated <c>documentdb-local</c>
    /// repository, and the tag or digest the reference carries inline if any.
    /// </summary>
    /// <param name="registry">The annotation's <see cref="ContainerImageAnnotation.Registry"/>.</param>
    /// <param name="image">The annotation's <see cref="ContainerImageAnnotation.Image"/>.</param>
    /// <param name="inlineTag">
    /// The tag the reference carries itself (<c>repo:tag</c>), or <see langword="null"/>. Aspire's
    /// <c>WithImage</c> splits such a tag out into <see cref="ContainerImageAnnotation.Tag"/> and
    /// refuses a tag given twice, so this only carries a value for an annotation built by hand.
    /// </param>
    /// <param name="inlineDigest">
    /// The digest the reference carries itself (<c>repo@sha256:...</c>) with its algorithm prefix
    /// removed, matching how <see cref="ContainerImageAnnotation.SHA256"/> is stored, or
    /// <see langword="null"/>.
    /// </param>
    /// <remarks>
    /// <para>
    /// The annotation models a reference as a registry prefix plus a repository, but Aspire only
    /// ever joins the two with a single separator — it never re-splits them, and it never
    /// validates either half. Where a caller puts the boundary is therefore not evidence about
    /// anything: <c>Registry</c> may hold a namespace, or nothing, or the whole reference may sit
    /// in <c>Image</c>. So the two fields are composed first and the composed reference is judged,
    /// which is also what guarantees that two spellings resolving to one reference classify alike.
    /// </para>
    /// <para>
    /// Exactly one prefix is then removed, and only one of two kinds may be removed:
    /// </para>
    /// <list type="bullet">
    /// <item><description>the curated registry <see cref="Registry"/> — the one namespace path
    /// this package publishes under, compared case-insensitively because registry hosts and GitHub
    /// owners are;</description></item>
    /// <item><description>a single registry host segment — a DNS name, an IPv4 or bracketed IPv6
    /// literal, or <c>localhost</c>, each with an optional port — and nothing after it.</description></item>
    /// </list>
    /// <para>
    /// What remains must equal <see cref="Image"/> exactly. A namespace, project or mirror path in
    /// front of the repository is therefore part of the repository and names a different one:
    /// <c>ghcr.io/evil/documentdb/documentdb-local</c>,
    /// <c>contoso.azurecr.io/mirrors/documentdb/documentdb-local</c> and
    /// <c>harbor.corp.local/library/documentdb/documentdb-local</c> are all custom, whether the
    /// caller wrote the prefix into <c>Registry</c> or inline. A true private mirror is a registry
    /// host and the exact repository beneath it — <c>contoso.azurecr.io/documentdb/documentdb-local</c>
    /// — which is what "only the registry differs" has always meant.
    /// </para>
    /// <para>
    /// A reference with an empty path segment — a leading or trailing separator, or a doubled one —
    /// is rejected before any of this, because no runtime can resolve one.
    /// </para>
    /// </remarks>
    internal static bool NamesCuratedRepository(
        string? registry,
        string? image,
        out string? inlineTag,
        out string? inlineDigest)
    {
        // Exactly what Aspire composes and the manifest carries. Registry is not trusted to be a
        // registry, and Image is not trusted to be a repository; only the whole is judged.
        var composed = string.IsNullOrEmpty(registry)
            ? image ?? string.Empty
            : registry + "/" + (image ?? string.Empty);

        var path = SplitTagAndDigest(composed, out inlineTag, out inlineDigest);

        if (path.Length == 0 ||
            path[0] == '/' ||
            path[^1] == '/' ||
            path.Contains("//", StringComparison.Ordinal))
        {
            return false;
        }

        // No prefix at all.
        if (IsCuratedRepositoryPath(path))
        {
            return true;
        }

        // The curated registry, which carries a namespace the host rule alone would leave
        // attached to the repository.
        if (TryRemoveCuratedRegistry(path, out var withoutCuratedRegistry) &&
            IsCuratedRepositoryPath(withoutCuratedRegistry))
        {
            return true;
        }

        // One registry host, and nothing between it and the repository.
        return TryRemoveRegistryHost(path, out var withoutHost) &&
            IsCuratedRepositoryPath(withoutHost);
    }

    private static bool IsCuratedRepositoryPath(string path) =>
        string.Equals(path, Image, StringComparison.Ordinal);

    /// <summary>
    /// Removes an inline <c>:tag</c> or <c>@digest</c> from a reference.
    /// </summary>
    /// <remarks>
    /// A digest is always last and brings its own <c>:</c>, and a <c>:</c> introduces a tag only
    /// in the final path segment — anywhere earlier it is a registry port, as in
    /// <c>localhost:5000/repo</c> or <c>[::1]:5000/repo</c>.
    /// </remarks>
    private static string SplitTagAndDigest(string reference, out string? tag, out string? digest)
    {
        tag = null;
        digest = null;

        var atDigest = reference.IndexOf('@', StringComparison.Ordinal);
        if (atDigest >= 0)
        {
            var value = reference[(atDigest + 1)..];
            const string Sha256Prefix = "sha256:";
            digest = value.StartsWith(Sha256Prefix, StringComparison.OrdinalIgnoreCase)
                ? value[Sha256Prefix.Length..]
                : value;
            reference = reference[..atDigest];
        }

        var atTag = reference.LastIndexOf(':');
        if (atTag > reference.LastIndexOf('/'))
        {
            tag = reference[(atTag + 1)..];
            reference = reference[..atTag];
        }

        return reference;
    }

    /// <summary>
    /// Removes <see cref="Registry"/> when the reference begins with it as a whole path, so that
    /// <c>ghcr.io/documentdbX/...</c> is not treated as living under <c>ghcr.io/documentdb</c>.
    /// </summary>
    private static bool TryRemoveCuratedRegistry(string path, out string remainder)
    {
        remainder = path;

        if (path.Length <= Registry.Length + 1 ||
            path[Registry.Length] != '/' ||
            !path.AsSpan(0, Registry.Length).Equals(Registry, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        remainder = path[(Registry.Length + 1)..];
        return true;
    }

    // A registry host and nothing else: localhost, a bracketed IPv6 literal, or a dotted DNS or
    // IPv4 name, each with an optional port -- plus the one shape that needs no dot, a bare name
    // with a port ("myregistry:5000"). This is the grammar Aspire's own (internal) container
    // reference parser applies, which is what decides whether the runtime reads a first segment
    // as a host or as the first component of the repository path.
    [GeneratedRegex(
        @"^((localhost|\[[a-fA-F0-9:]+\]|[\w-]+(\.[\w-]+)+)(:[0-9]+)?|[\w-]+:[0-9]+)\z",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex RegistryHostRegex();

    /// <summary>
    /// Removes a leading registry host, and only a host: any further path segment before the
    /// repository belongs to the repository.
    /// </summary>
    private static bool TryRemoveRegistryHost(string path, out string remainder)
    {
        remainder = path;

        var separator = path.IndexOf('/', StringComparison.Ordinal);
        if (separator <= 0 || !RegistryHostRegex().IsMatch(path.AsSpan(0, separator)))
        {
            return false;
        }

        remainder = path[(separator + 1)..];
        return true;
    }
}
