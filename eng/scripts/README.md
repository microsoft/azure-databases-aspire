# eng/scripts

Repository automation scripts.

## `check-documentdb-versions.py`

Detects new upstream DocumentDB releases and rewrites
`src/Aspire.Hosting.DocumentDB/DocumentDBVersion.cs` plus the auto-generated block in
`CHANGELOG.md`. Intentionally does **not** edit
`src/Aspire.Hosting.DocumentDB/api/Aspire.Hosting.DocumentDB.cs` — that file is the public API
analyzer baseline and must be updated by hand on the auto-PR before merging, so that the
analyzer remains an independent guard against unintentional public-API changes.

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
   `pg15-X.Y.Z`, `pg16-X.Y.Z`, and `pg17-X.Y.Z` published on GHCR.
2. The set of required PG variants is the script-level constant `REQUIRED_PG_SET`. Adding a new
   PG variant (for example `pg18`) is intentionally a manual code change in three places:
   `DocumentDBPostgresVersion`, this constant, and the documentation.
3. The version list in `DocumentDBVersion.cs` is **append-only**. The script never removes a
   version that was previously shipped, even if it disappears from upstream.
4. Numeric enum values are derived deterministically from sort order (1, 2, 3, ...) and must
   remain stable: when adding a new latest version, it gets the next unused value. The script
   re-renders the entire enum so values for existing members never change.
5. The CHANGELOG block is **replace-in-place** (bounded by HTML comments) so reruns on the
   same auto-PR branch don't accumulate duplicate entries.

### Trust assumption

GHCR tags are mutable. "Version supported" here means "tag exists at the time of the check",
not "image bytes are immutable". Pinning by digest is a future enhancement.
