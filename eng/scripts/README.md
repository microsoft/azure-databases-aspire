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
   never auto-extended — see [Adding a PG variant](#adding-a-pg-variant).
3. The version list in `DocumentDBVersion.cs` is **append-only**. The script never removes a
   version that was previously shipped, even if it disappears from upstream.
4. Numeric enum values are derived deterministically from sort order (1, 2, 3, ...) and must
   remain stable: when adding a new latest version, it gets the next unused value. The script
   re-renders the entire enum so values for existing members never change.
5. The CHANGELOG block is **replace-in-place** (bounded by HTML comments) so reruns on the
   same auto-PR branch don't accumulate duplicate entries. It lives at the end of the
   `## [Unreleased]` section of `CHANGELOG.md`; the script warns if the markers have drifted
   into an already-released section (regenerated notes would be filed under the wrong release).
6. A candidate **older** than the newest already-shipped version ("backfill") is skipped with a
   warning instead of adopted, because numeric enum values must never shift. Newer candidates in
   the same run are still adopted, so one backfill candidate cannot wedge adoption forever.
7. A released version that is missing a required variant on GHCR is reported as
   `[warn] X.Y.Z blocked: missing required variants [...]`, so stalled adoption is never silent.
8. Parsing is fail-fast: an enum region line the script cannot parse, or a rewrite that would
   drop or renumber a shipped member, is a hard error rather than a silent partial rewrite.

### Adding a PG variant

Adding a variant (for example a future `pg19`) is intentionally manual. Touch all of these — the
listed guard test fails until you do:

| Place | Guard |
|---|---|
| `DocumentDBPostgresVersion` in `src/Aspire.Hosting.DocumentDB/DocumentDBVersion.cs` (append-only; numeric value is the PG major) | compile-time |
| `WithPostgresVersion` validation message in `src/Aspire.Hosting.DocumentDB/DocumentDBBuilderExtensions.cs` | none — check by hand |
| Public API baseline `src/Aspire.Hosting.DocumentDB/api/Aspire.Hosting.DocumentDB.cs` | `VersionAutomationScriptTests.ApiBaselineListsEveryPublicEnumMember` |
| `REQUIRED_PG_SET` here in the script (only once upstream publishes the tag for every version you intend to adopt) and the matching docstring line | `VersionAutomationScriptTests.RequiredPgSetIsASubsetOfDocumentDBPostgresVersion`, `ScriptDocstringListsTheSameRequiredPgSet` |
| The expected set in `eng/scripts/tests/test_check_documentdb_versions.py` (`RequiredPgSetTests`) | itself |
| `PG15/16/17/18` lists in `README.md` and `src/Aspire.Hosting.DocumentDB/README.md`; the `pgVersion` and `enum DocumentDBPostgresVersion` rows in `docs/configuration.md` | `ReadmeApiTableListsEverySelectablePgVariant`, `ConfigurationDocRowsListEverySelectablePgVariant` |
| The `pgNN-X.Y.Z` adoption-policy sentences in this file and `docs/configuration.md` | `AdoptionPolicyDocsMentionEveryRequiredPgVariant` |
| Tag-prefix theory case in `tests/Aspire.Hosting.DocumentDB.Tests/DocumentDBVersionSelectionTests.cs` | none — add the `InlineData` |

Note that `REQUIRED_PG_SET` is the *adoption gate*, not the list of selectable variants: it may
lag the enum (a variant users can select before it is required for adoption) and may shrink when
upstream drops a variant. The guard test therefore asserts subset, not equality.

Unrelated but adjacent: `tests/Aspire.Hosting.DocumentDB.PostgresEndToEndApp/Program.cs` pins an
explicit `pg17-X.Y.Z` tag because it gates the NuGet publish workflow; bump that pin when
adopting a new DocumentDB version.

### Trust assumption

GHCR tags are mutable. "Version supported" here means "tag exists at the time of the check",
not "image bytes are immutable". Pinning by digest is a future enhancement.

### Tests

`tests/test_check_documentdb_versions.py` covers `REQUIRED_PG_SET`, the CHANGELOG block rewrite,
backfill/blocked-version handling, and the fail-fast parse guards, by executing the real script
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
