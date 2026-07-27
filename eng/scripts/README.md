# eng/scripts

Repository automation scripts.

## `check-documentdb-versions.py`

Detects new upstream DocumentDB releases and rewrites
`src/Aspire.Hosting.DocumentDB/DocumentDBVersion.cs` plus the auto-generated block in
`CHANGELOG.md`. Intentionally does **not** edit
`src/Aspire.Hosting.DocumentDB/api/Aspire.Hosting.DocumentDB.cs` — that file is the public API
baseline and must be updated by hand on the auto-PR before merging, so it stays an independent,
human-reviewed record of public-API changes. The unit test
`VersionAutomationScriptTests.ApiBaselineListsEveryPublicEnumMember` enforces this: CI fails on
the auto-PR until every new enum member (and its string constant) is present in the baseline.

### Usage

```bash
# From the repo root.
python3 eng/scripts/check-documentdb-versions.py
```

Optional: set `GITHUB_TOKEN` to use authenticated GitHub API requests (avoids the 60/hr
unauthenticated rate limit). Used automatically inside GitHub Actions.

### Inputs

- GitHub Releases of `documentdb/documentdb` (filters drafts/prereleases).
- GHCR tag list of `documentdb/documentdb/documentdb-local` (anonymous Bearer token flow).

### Output rules

1. A version is considered **supported** only if it appears in BOTH the GitHub releases AND has
   `pg15-X.Y.Z`, `pg16-X.Y.Z`, `pg17-X.Y.Z`, and `pg18-X.Y.Z` published on GHCR.
2. The set of required PG variants is the script-level constant `REQUIRED_PG_SET` (currently
   `{15, 16, 17, 18}`; `pg18` is published upstream from DocumentDB 0.114.0 onwards). It is
   never auto-extended — see [Adding a PG variant](#adding-a-pg-variant). A `pgN-` tag on GHCR
   whose variant is in neither `REQUIRED_PG_SET` nor `DEFERRED_PG_SET` is reported as an unknown
   variant; a deferred one is not, because the package already knows about it.
3. The version list in `DocumentDBVersion.cs` is **append-only**. The script never removes a
   version that was previously shipped, even if it disappears from upstream.
4. Numeric enum values are derived deterministically from sort order (1, 2, 3, ...) and must
   remain stable: when adding a new latest version, it gets the next unused value. The script
   re-renders the entire enum so values for existing members never change.
5. The CHANGELOG block is **replace-in-place** (bounded by HTML comments) so reruns on the
   same auto-PR branch don't accumulate duplicate entries. It lives at the end of the
   `## [Unreleased]` section of `CHANGELOG.md`; the script warns if the markers have drifted
   into an already-released section, or if there is no `## [Unreleased]` heading at all (a
   release cut renames it), because regenerated notes would then be filed under the wrong
   release.
6. A candidate **older** than the newest already-shipped version ("backfill") is skipped with a
   warning instead of adopted, because numeric enum values must never shift. Newer candidates in
   the same run are still adopted, so one backfill candidate cannot wedge adoption forever.
7. A released version newer than the newest shipped one that cannot be adopted is reported as
   `[warn] X.Y.Z blocked: ...` — both when it is missing a required variant and when it has no
   container tags at all (the common case: the GitHub release lands before the image build
   finishes). Stalled adoption is therefore never silent.
8. Parsing is fail-fast: a line the script cannot parse in the enum or string-constant region is
   a hard error rather than a silent partial rewrite. The append-only check runs against the
   **rendered text**, re-parsed just before it would be written, so a rendering or
   region-replacement bug that dropped or renumbered a shipped member fails the run and leaves
   the file untouched.
9. Single-line attributes attached to a generated member — `[Obsolete("…")]`, which
   `DocumentDBVersion`'s XML docs prescribe for retiring a member instead of removing it — are
   parsed and re-emitted verbatim in the enum and string-constant regions, so regenerating never
   strips them. See [Retiring a version](#retiring-a-version).
10. Every warning that means "a candidate exists upstream but was not adopted" (blocked version,
    skipped backfill, misplaced CHANGELOG markers) is also emitted as a GitHub Actions
    `::warning` annotation. The script exits 0 in all of those cases by design, so without the
    annotation a stalled weekly run looks exactly like a run with nothing to do.

### Adding a PG variant

Adding a variant (for example a future `pg19`) is intentionally manual. Touch all of these; where
the Guard column names a test, that test fails until you do — the rows marked "none" are on you:

| Place | Guard |
|---|---|
| `DocumentDBPostgresVersion` in `src/Aspire.Hosting.DocumentDB/DocumentDBVersion.cs` (append-only; numeric value is the PG major) | compile-time |
| `WithPostgresVersion` validation message in `src/Aspire.Hosting.DocumentDB/DocumentDBBuilderExtensions.cs` | none — check by hand |
| Public API baseline `src/Aspire.Hosting.DocumentDB/api/Aspire.Hosting.DocumentDB.cs` | `VersionAutomationScriptTests.ApiBaselineListsEveryPublicEnumMember` |
| `REQUIRED_PG_SET` here in the script — or `DEFERRED_PG_SET` if upstream has not published the tag for every version you intend to adopt yet — and the matching docstring line | `VersionAutomationScriptTests.EveryPgVariantIsRequiredOrExplicitlyDeferred`, `RequiredPgSetIsASubsetOfDocumentDBPostgresVersion`, `ScriptDocstringListsTheSameRequiredPgSet` |
| The expected set in `eng/scripts/tests/test_check_documentdb_versions.py` (`RequiredPgSetTests`) | itself |
| `PG15/16/17/18` lists in `README.md` and `src/Aspire.Hosting.DocumentDB/README.md`; the `pgVersion` and `enum DocumentDBPostgresVersion` rows in `docs/configuration.md` | `ReadmeApiTableListsEverySelectablePgVariant`, `ConfigurationDocRowsListEverySelectablePgVariant` |
| The `pgNN-X.Y.Z` adoption-policy sentences in this file and `docs/configuration.md` | `AdoptionPolicyDocsMentionEveryRequiredPgVariant` |
| Tag-prefix theory case in `tests/Aspire.Hosting.DocumentDB.Tests/DocumentDBVersionSelectionTests.cs` | none — add the `InlineData` |

Note that `REQUIRED_PG_SET` is the *adoption gate*, not the list of selectable variants: it may
lag the enum (a variant users can select before it is required for adoption) and may shrink when
upstream drops a variant. The guard tests therefore assert two weaker things instead of equality:
every gated variant must be selectable (subset), and every selectable variant must be either
gated or explicitly listed in `DEFERRED_PG_SET`, so a new enum member can never be forgotten here.

Unrelated but adjacent: `tests/Aspire.Hosting.DocumentDB.PostgresEndToEndApp/Program.cs` pins an
explicit `pg17-X.Y.Z` tag because it gates the NuGet publish workflow; bump that pin when
adopting a new DocumentDB version (`VersionAutomationScriptTests.PostgresEndToEndAppPinsTheCurrentLatestVersion`
fails until you do).

### Adopting a new DocumentDB version

The script opens the auto-PR with `DocumentDBVersion.cs` and the CHANGELOG block already
rewritten. The rest is deliberately manual, and CI stays red until it is done:

| Place | Guard |
|---|---|
| Public API baseline `src/Aspire.Hosting.DocumentDB/api/Aspire.Hosting.DocumentDB.cs` (new enum member + string constant) | `VersionAutomationScriptTests.ApiBaselineListsEveryPublicEnumMember` |
| `WithImageTag("pgNN-X.Y.Z")` pin in `tests/Aspire.Hosting.DocumentDB.PostgresEndToEndApp/Program.cs`, plus the comments quoting it in `DocumentDBIntegrationTests.cs` | `VersionAutomationScriptTests.PostgresEndToEndAppPinsTheCurrentLatestVersion` |
| `\| Image tag \|` row in `docs/configuration.md` and the `docker pull` example in `docs/troubleshooting.md` | `VersionAutomationScriptTests.DocsQuoteTheCurrentDefaultImageTag` |
| Release notes: move the generated block's content into a dated `## [X.Y.Z]` section when you cut the package release, and reset the block body to its "nothing detected" placeholder line | `test_unreleased_block_does_not_restate_an_already_released_version` (Python suite) — a version left in both places fails it |
| Release cut: keep a `## [Unreleased]` heading above the markers after renaming the old one | none directly; the next detection run warns and annotates |
| Optional: an `InlineData` case in `DocumentDBVersionSelectionTests.WithDocumentDBVersionAloneSetsExpectedTag` | none — the drift guard in that file already covers correctness |

Deliberately NOT updated per version: the `0.112.0` floor in `DocumentDBContainerImageTags.MinimumPostgresEndpointVersion`
and the doc examples explaining it.

### Retiring a version

`DocumentDBVersion` is append-only, so a version that should no longer be used is marked
`[Obsolete("…")]` rather than removed. Put the attribute on its own single line directly above
the member (and, if you also want to deprecate the string constant, above that `public const
string` line). Both regions are auto-generated, and the script parses and re-emits attribute lines
verbatim, so the deprecation survives every later rewrite.

Two caveats:

- Keep the attribute on **one** line. A wrapped attribute is a hard error, because the script
  cannot reattach it to its member when regenerating the region.
- The `All` array and the `ToVersionString` switch still reference the obsoleted member, which
  raises `CS0618`. Put `#pragma warning disable CS0618` / `restore` around the whole
  `All` property and the whole `ToVersionString` method — **outside** the auto-generated markers.
  Anything placed inside a marker block is discarded on the next rewrite.

### Trust assumption

GHCR tags are mutable. "Version supported" here means "tag exists at the time of the check",
not "image bytes are immutable". Pinning by digest is a future enhancement.

### Tests

`tests/test_check_documentdb_versions.py` covers `REQUIRED_PG_SET`/`DEFERRED_PG_SET`, the CHANGELOG
block rewrite and placement guard, backfill/blocked-version handling and their annotations, the
`[Obsolete]` round-trip, and the fail-fast parse/write guards, by executing the real script
functions (standard library `unittest` only, no pip dependencies):

```bash
# From the repo root.
python3 -m unittest discover -s eng/scripts/tests
```

It runs in CI as the `script-test` job of `.github/workflows/build-and-test.yml`. The weekly
`check-documentdb-version` workflow deliberately does **not** gate detection on these tests: a
failure there would silently stop version detection instead of surfacing on a PR.

The companion C# drift guard
`VersionAutomationScriptTests` (in `tests/Aspire.Hosting.DocumentDB.Tests`) covers what only .NET
can see: that `REQUIRED_PG_SET` stays a subset of `DocumentDBPostgresVersion`, that the public API
baseline lists every shipped enum member, and that the hand-maintained variant lists in the
documentation match the enum. Behavioral assertions about the script itself belong in the Python
suite, which executes the real functions.
