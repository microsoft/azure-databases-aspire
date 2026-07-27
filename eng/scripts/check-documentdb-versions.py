#!/usr/bin/env python3
# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.
"""Detect new upstream DocumentDB versions and rewrite source files.

What this script does:
  1. Lists GitHub Releases of the upstream `documentdb/documentdb` repository.
     - Filters out drafts and pre-releases.
     - Validates each release tag matches `^v\\d+\\.\\d+-\\d+$`. Non-matching tags are
       logged and skipped (does not crash). Maps `v0.110-0` to `0.110.0`.
  2. Lists tags of the GHCR container image `documentdb/documentdb-local`.
     - Uses the anonymous Bearer token flow (no auth required for public images).
     - Detects all `pgN-` prefixes and warns about any in neither REQUIRED_PG_SET nor
       DEFERRED_PG_SET (i.e. variants the package does not know about at all).
  3. Computes the intersection of (GH releases) and (GHCR tags), where each version is only
     considered "supported" if every PG variant in REQUIRED_PG_SET (currently {15, 16, 17, 18})
     has a `pgN-X.Y.Z` tag on GHCR. Every release at or above the oldest curated version that
     is not adopted is reported, so stalled adoption is never silent: `[warn] ... blocked: ...`
     for one missing a required variant or with no container tags at all, and `[warn] skipping
     backfill candidate(s) ...` for one that a newer adoption has leapfrogged. An empty
     intersection is reported too - it is what a GHCR/release feed that changed shape looks
     like, and both fetches return empty rather than raising in that case.
  4. Parses the auto-generated regions in `src/Aspire.Hosting.DocumentDB/DocumentDBVersion.cs`
     to learn the current curated list. Any line it cannot parse is a hard error: re-rendering
     a partially parsed enum would delete shipped members. Single-line attributes attached to a
     member (`[Obsolete("...")]`, which the enum's own XML docs prescribe for retiring a member)
     are parsed and re-emitted, so re-rendering never strips them.
  5. If the intersection contains versions not in the current list, rewrites the auto-generated
     regions in DocumentDBVersion.cs (only) and replace-in-place updates the auto-generated
     CHANGELOG block. Candidates OLDER than the newest shipped version ("backfill") are skipped
     with a warning rather than adopted, because numeric enum values must never shift; newer
     candidates in the same run are still adopted.
  6. Checks on EVERY run - adoption or not - that the CHANGELOG marker block still sits inside
     the `## [Unreleased]` section, and refuses to adopt anything while it does not, rather than
     filing the generated notes under an already-released version.

What this script does NOT do (deliberately):
  - It does not edit `src/Aspire.Hosting.DocumentDB/api/Aspire.Hosting.DocumentDB.cs`. That
    file is the public-API baseline, kept as an independent guard against unintentional public
    API changes; the maintainer reviewing the auto-PR appends new `DocumentDBVersion.V0_X_Y`
    members to it by hand. `VersionAutomationScriptTests.ApiBaselineListsEveryPublicEnumMember`
    (in tests/Aspire.Hosting.DocumentDB.Tests) fails until that hand-edit lands.
  - It does not bump the NuGet package version. That is a manual step (Git `v*` tag + MinVer).
  - It does not merge anything. The companion workflow opens a PR for human review.

Trust assumption: GHCR tags are mutable. "Version supported" here means "tag exists at the
time of this check", not "image bytes are immutable". Pinning by digest is a future
enhancement.

Exit status: 0 always when invoked normally (success / no-op / new-versions-detected). Non-zero
only on unrecoverable errors (network failures, malformed source files). Because a stalled
adoption still exits 0, every warning that means "a candidate exists but was not adopted" is
ALSO emitted as a GitHub Actions `::warning` annotation (see `emit_github_annotation`), so a
green scheduled run is still visibly annotated instead of hiding the stall in the log.
"""
from __future__ import annotations

import json
import os
import re
import sys
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
VERSIONS_FILE = REPO_ROOT / "src" / "Aspire.Hosting.DocumentDB" / "DocumentDBVersion.cs"
CHANGELOG_FILE = REPO_ROOT / "CHANGELOG.md"

GH_OWNER = "documentdb"
GH_REPO = "documentdb"
GHCR_IMAGE_PATH = "documentdb/documentdb/documentdb-local"

REQUIRED_PG_SET: frozenset[int] = frozenset({15, 16, 17, 18})

# PG variants that exist as DocumentDBPostgresVersion members (so users can select them) but are
# deliberately NOT part of the adoption gate above - for example a variant upstream has only
# started publishing for the newest releases. Every enum member must appear in one of these two
# sets: VersionAutomationScriptTests.EveryPgVariantIsRequiredOrExplicitlyDeferred fails otherwise,
# which is what catches "the enum gained a variant but the adoption gate never heard about it".
DEFERRED_PG_SET: frozenset[int] = frozenset()

# Upstream releases the maintainer has decided will never be adopted, as "X.Y.Z" strings.
# A release that upstream published while one of its required PG variants was still building can
# be leapfrogged: a newer version is adopted, and from then on the older one can only be taken by
# a manual PR, because numeric enum values must never shift. `report_unadopted_versions` keeps
# reporting it so the stall never goes silent - and would therefore report it every week forever,
# which is the version-level twin of the recurring "unknown PG variant" noise DEFERRED_PG_SET
# removes. Listing it here acknowledges the decision once and stops the warning. Releases older
# than the oldest curated member are out of scope already and never need an entry.
ACKNOWLEDGED_SKIPS: frozenset[str] = frozenset()

GH_TAG_RE = re.compile(r"^v(\d+)\.(\d+)-(\d+)$")
GHCR_TAG_RE = re.compile(r"^pg(\d+)-(\d+)\.(\d+)\.(\d+)$")
ENUM_MEMBER_LINE_RE = re.compile(r"^V(\d+)_(\d+)_(\d+)\s*=\s*(\d+)\s*,?$")
CONST_MEMBER_LINE_RE = re.compile(r'^public const string V(\d+)_(\d+)_(\d+)\s*=\s*"[^"]*"\s*;$')
# A single-line attribute applied to the member below it, e.g. `[Obsolete("Use V0_110_0.")]`.
ATTRIBUTE_LINE_RE = re.compile(r"^\[.+\]$")
UNRELEASED_HEADING = "## [Unreleased]"
AUTO_GEN_RE = re.compile(
    r"(// <auto-generated-versions-start>\n)(.*?)(\s*// <auto-generated-versions-end>)",
    re.DOTALL,
)
CHANGELOG_AUTO_GEN_RE = re.compile(
    r"(<!-- auto-generated:documentdb-versions-start -->\n)(.*?)(\n<!-- auto-generated:documentdb-versions-end -->)",
    re.DOTALL,
)


# ---------------------------------------------------------------------------
# Data model
# ---------------------------------------------------------------------------

@dataclass(frozen=True, order=True)
class SemVer:
    """Semantic version sortable by numeric segment (so 0.9.0 < 0.10.0)."""

    major: int
    minor: int
    patch: int

    def __str__(self) -> str:
        return f"{self.major}.{self.minor}.{self.patch}"

    @property
    def enum_member(self) -> str:
        return f"V{self.major}_{self.minor}_{self.patch}"


def acknowledged_skips() -> set[SemVer]:
    """Parse ACKNOWLEDGED_SKIPS, hard-failing on a malformed entry.

    A typo would otherwise silently fail to match any version and quietly restore the recurring
    warning the entry was added to silence.
    """
    parsed: set[SemVer] = set()
    for text in ACKNOWLEDGED_SKIPS:
        parts = text.split(".")
        if len(parts) != 3 or not all(part.isdigit() for part in parts):
            raise RuntimeError(
                f"ACKNOWLEDGED_SKIPS entry {text!r} is not a MAJOR.MINOR.PATCH version string."
            )
        parsed.add(SemVer(int(parts[0]), int(parts[1]), int(parts[2])))
    return parsed


# ---------------------------------------------------------------------------
# Reporting
# ---------------------------------------------------------------------------

def _escape_annotation(value: str, *, is_property: bool) -> str:
    """Escape a value for a GitHub Actions workflow command."""
    escaped = value.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")
    if is_property:
        escaped = escaped.replace(":", "%3A").replace(",", "%2C")
    return escaped


def emit_github_annotation(level: str, title: str, message: str) -> None:
    """Surface a message as a GitHub Actions annotation; no-op outside Actions.

    Every "cannot adopt yet" path in this script exits 0 by design, so a stuck weekly run looks
    exactly like a run with nothing to do: green, with the explanation buried in a log nobody
    opens. Annotations show up on the run summary itself, which is what makes a stall
    noticeable. Workflow commands are read from stdout, so this deliberately does NOT go to
    stderr alongside the corresponding `[warn]` line.
    """
    if not os.environ.get("GITHUB_ACTIONS"):
        return
    print(
        f"::{level} title={_escape_annotation(title, is_property=True)}"
        f"::{_escape_annotation(message, is_property=False)}"
    )


# ---------------------------------------------------------------------------
# Network
# ---------------------------------------------------------------------------

def _http_get_json_and_link(url: str, headers: dict[str, str] | None = None) -> tuple[object, str | None]:
    req = urllib.request.Request(url, headers=headers or {})
    with urllib.request.urlopen(req, timeout=30) as resp:
        return json.loads(resp.read().decode("utf-8")), resp.headers.get("Link")


def _http_get_json(url: str, headers: dict[str, str] | None = None) -> object:
    return _http_get_json_and_link(url, headers=headers)[0]


def _get_next_url_from_link_header(current_url: str, link_header: str | None) -> str | None:
    if not link_header:
        return None

    for link in link_header.split(","):
        link = link.strip()
        if not link.startswith("<"):
            continue

        url_end = link.find(">")
        if url_end == -1:
            continue

        target = link[1:url_end]
        for parameter in link[url_end + 1:].split(";"):
            name, separator, value = parameter.strip().partition("=")
            if separator and name.lower() == "rel" and value.strip("\"'").lower() == "next":
                return urllib.parse.urljoin(current_url, target)

    return None


def fetch_github_releases(owner: str, repo: str) -> list[SemVer]:
    """Return non-draft, non-prerelease releases whose tags map to a SemVer."""
    headers: dict[str, str] = {"Accept": "application/vnd.github+json"}
    token = os.environ.get("GITHUB_TOKEN")
    if token:
        headers["Authorization"] = f"Bearer {token}"

    versions: list[SemVer] = []
    page = 1
    per_page = 100
    while True:
        url = f"https://api.github.com/repos/{owner}/{repo}/releases?per_page={per_page}&page={page}"
        data = _http_get_json(url, headers=headers)
        if not isinstance(data, list):
            raise RuntimeError(f"Unexpected releases payload shape: {type(data).__name__}")
        if not data:
            break

        for release in data:
            if release.get("draft") or release.get("prerelease"):
                continue
            tag = release.get("tag_name") or ""
            match = GH_TAG_RE.match(tag)
            if not match:
                print(f"  [skip] release tag does not match expected vMAJOR.MINOR-PATCH: {tag!r}",
                      file=sys.stderr)
                continue
            versions.append(SemVer(int(match[1]), int(match[2]), int(match[3])))

        if len(data) < per_page:
            break
        page += 1

    return versions


def fetch_ghcr_pg_tags(image_path: str) -> dict[SemVer, set[int]]:
    """Return {version: {pg_variants}} for every pgN-X.Y.Z tag on the image."""
    token_url = (
        "https://ghcr.io/token?service=ghcr.io"
        f"&scope=repository:{image_path}:pull"
    )
    token_payload = _http_get_json(token_url)
    if not isinstance(token_payload, dict):
        raise RuntimeError("Unexpected GHCR token payload")
    token = token_payload.get("token") or token_payload.get("access_token")
    if not token:
        raise RuntimeError("GHCR token endpoint returned no token")

    tags: list[object] = []
    list_url: str | None = f"https://ghcr.io/v2/{image_path}/tags/list?n=500"
    headers = {"Authorization": f"Bearer {token}"}
    while list_url:
        payload, link_header = _http_get_json_and_link(list_url, headers=headers)
        if not isinstance(payload, dict):
            raise RuntimeError("Unexpected GHCR tags payload")
        page_tags = payload.get("tags") or []
        if not isinstance(page_tags, list):
            raise RuntimeError("GHCR tags field is not a list")

        tags.extend(page_tags)
        list_url = _get_next_url_from_link_header(list_url, link_header)

    by_version: dict[SemVer, set[int]] = {}
    seen_pg: set[int] = set()
    for tag in tags:
        if not isinstance(tag, str):
            continue
        match = GHCR_TAG_RE.match(tag)
        if not match:
            continue
        pg = int(match[1])
        seen_pg.add(pg)
        version = SemVer(int(match[2]), int(match[3]), int(match[4]))
        by_version.setdefault(version, set()).add(pg)

    # Deferred variants are known to the package (they have a DocumentDBPostgresVersion member),
    # they are just not part of the adoption gate - warning about them would be the exact recurring
    # noise DEFERRED_PG_SET exists to prevent. Only a variant in neither set is genuinely unknown.
    unknown = seen_pg - REQUIRED_PG_SET - DEFERRED_PG_SET
    if unknown:
        print(f"  [warn] GHCR has unknown PG variants {sorted(unknown)}; "
              "DocumentDBPostgresVersion is not auto-extended.", file=sys.stderr)

    return by_version


# ---------------------------------------------------------------------------
# Source parsing & rewriting
# ---------------------------------------------------------------------------

def _take_attribute_line(
    line: str, raw_line: str, pending: list[str], region: str, source: Path
) -> bool:
    """Collect `line` if it is a single-line attribute for the member below it.

    `DocumentDBVersion`'s XML docs prescribe `[Obsolete("...")]` for retiring a member instead of
    removing it, and that attribute has to live inside the auto-generated region. Attributes are
    therefore parsed (not a hard error) and re-emitted verbatim by `render_versions_file`.
    """
    if not line.startswith("["):
        return False
    if not ATTRIBUTE_LINE_RE.match(line):
        raise RuntimeError(
            f"Attribute in the {region} region of {source} spans more than one line: "
            f"{raw_line!r}. Keep it on a single line (for example "
            '`[Obsolete("Use V0_110_0 instead.")]`) so it can be re-emitted verbatim when the '
            "region is regenerated."
        )
    pending.append(line)
    return True


def _parse_enum_region(block: str, source: Path) -> tuple[dict[SemVer, int], dict[SemVer, list[str]]]:
    """Return ({SemVer: numeric_value}, {SemVer: attribute lines}) for the enum-member region."""
    values: dict[SemVer, int] = {}
    attributes: dict[SemVer, list[str]] = {}
    pending: list[str] = []
    for raw_line in block.splitlines():
        line = raw_line.strip()
        if not line or line.startswith("//"):
            continue
        if _take_attribute_line(line, raw_line, pending, "DocumentDBVersion enum", source):
            continue
        m = ENUM_MEMBER_LINE_RE.match(line)
        if not m:
            raise RuntimeError(
                f"Unrecognized line in the DocumentDBVersion enum region of {source}: "
                f"{raw_line!r}. Every non-comment line must look like 'V0_114_0 = 6,' or be a "
                "single-line attribute. Refusing to continue, because re-rendering a partially "
                "parsed enum would drop shipped members."
            )
        version = SemVer(int(m[1]), int(m[2]), int(m[3]))
        if version in values:
            raise RuntimeError(f"Duplicate enum member for {version} in {source}")
        values[version] = int(m[4])
        if pending:
            attributes[version] = pending
            pending = []

    if pending:
        raise RuntimeError(
            f"Trailing attribute(s) {pending} in the DocumentDBVersion enum region of {source} "
            "are not attached to any member; re-rendering would drop them."
        )
    if not values:
        raise RuntimeError(
            f"Parsed zero enum members from the auto-generated region of {source}. "
            "The region shape must have changed; refusing to continue."
        )
    return values, attributes


def _parse_const_region(block: str, source: Path) -> dict[SemVer, list[str]]:
    """Return {SemVer: attribute lines} for the string-constant region.

    Same fail-fast rule as the enum region: this region is regenerated wholesale, so a line the
    parser does not understand is a line that would be silently deleted.
    """
    attributes: dict[SemVer, list[str]] = {}
    pending: list[str] = []
    for raw_line in block.splitlines():
        line = raw_line.strip()
        if not line or line.startswith("//"):
            continue
        if _take_attribute_line(line, raw_line, pending, "DocumentDBVersions constant", source):
            continue
        m = CONST_MEMBER_LINE_RE.match(line)
        if not m:
            raise RuntimeError(
                f"Unrecognized line in the DocumentDBVersions constant region of {source}: "
                f"{raw_line!r}. Every non-comment line must look like "
                "'public const string V0_114_0 = \"0.114.0\";' or be a single-line attribute."
            )
        if pending:
            attributes[SemVer(int(m[1]), int(m[2]), int(m[3]))] = pending
            pending = []

    if pending:
        raise RuntimeError(
            f"Trailing attribute(s) {pending} in the DocumentDBVersions constant region of "
            f"{source} are not attached to any constant; re-rendering would drop them."
        )
    return attributes


def parse_versions_text(
    text: str, source: Path
) -> tuple[dict[SemVer, int], dict[SemVer, list[str]], dict[SemVer, list[str]]]:
    """Parse the auto-generated regions of a DocumentDBVersion.cs *body*.

    Returns (numeric values, enum-member attributes, string-constant attributes). Takes text
    rather than a path so `write_versions_file` can re-parse the exact bytes it is about to
    write, which is what turns `assert_append_only` into a real guard instead of a restatement
    of what the caller just built.

    Hard-fails on anything it does not fully understand. A partial parse is far more dangerous
    than a crash: the parsed data is what gets re-rendered, so silently skipping a member the
    regex no longer matches would DELETE that member from the shipped enum.
    """
    matches = AUTO_GEN_RE.findall(text)
    if not matches:
        raise RuntimeError(f"No auto-generated regions found in {source}")
    values, enum_attributes = _parse_enum_region(matches[0][1], source)
    const_attributes = _parse_const_region(matches[1][1], source) if len(matches) > 1 else {}
    return values, enum_attributes, const_attributes


@dataclass(frozen=True)
class VersionsSource:
    """DocumentDBVersion.cs as it currently stands on disk, read and parsed exactly once.

    Three consumers need overlapping pieces of this file: main() needs the numeric assignments to
    work out what is new, `render_versions_file` needs the attribute lines so a hand-applied
    `[Obsolete]` survives regeneration, and `write_versions_file` needs the raw text to locate the
    auto-generated regions plus the assignments to check its own output is append-only. Loading
    them together is one read and one parse instead of two, and removes the possibility of two
    callers disagreeing about what the file contains.
    """

    path: Path
    text: str
    values: dict[SemVer, int]
    enum_attributes: dict[SemVer, list[str]]
    const_attributes: dict[SemVer, list[str]]

    @classmethod
    def load(cls, path: Path) -> "VersionsSource":
        text = path.read_text(encoding="utf-8")
        values, enum_attributes, const_attributes = parse_versions_text(text, path)
        return cls(path, text, values, enum_attributes, const_attributes)


def parse_known_versions(versions_file: Path) -> dict[SemVer, int]:
    """Return {SemVer: numeric_value} for every member currently in DocumentDBVersion.cs.

    Reads from the FIRST auto-generated region in the file, which is the enum-member region.
    The numeric values must be preserved exactly; this dict is the source of truth used by
    `render_versions_file` to avoid renumbering existing members.
    """
    return VersionsSource.load(versions_file).values


def assert_append_only(
    existing: dict[SemVer, int], target: dict[SemVer, int]
) -> None:
    """Fail unless `target` is `existing` plus (possibly) new members.

    Last-line defence before writing. `write_versions_file` calls this with `target` re-parsed
    from the rendered text, so a rendering or region-replacement bug that dropped or renumbered
    a shipped member fails here instead of being written to disk and committed by the workflow.
    Passing the in-memory assignments straight from `assign_numeric_values` would make this a
    tautology - that function starts from `dict(existing)` and only ever adds keys.
    """
    if len(target) < len(existing):
        raise RuntimeError(
            f"Refusing to write {len(target)} enum member(s) over the {len(existing)} member(s) "
            "currently shipped: the version list is append-only."
        )
    for version, value in existing.items():
        if version not in target:
            raise RuntimeError(
                f"Refusing to write: shipped member {version.enum_member} would be removed."
            )
        if target[version] != value:
            raise RuntimeError(
                f"Refusing to write: shipped member {version.enum_member} would be renumbered "
                f"from {value} to {target[version]}."
            )


def assign_numeric_values(
    existing: dict[SemVer, int], new_versions: list[SemVer]
) -> dict[SemVer, int]:
    """Return a {SemVer: numeric_value} map that PRESERVES every existing assignment.

    New versions get values strictly greater than `max(existing.values())`, assigned in sort
    order. We deliberately do NOT renumber existing members (binary stability), and we hard-fail
    if asked to assign a value for a version that, in sort order, comes before the current
    maximum -- that would either renumber existing members or assign a higher numeric value to
    a semantically older version, both of which violate the append-only contract. The caller
    (main()) catches this case earlier with a friendlier error; this assertion is the last-line
    defence.
    """
    out = dict(existing)
    if not new_versions:
        return out

    max_known = max(out.keys()) if out else None
    for v in sorted(new_versions):
        if v in out:
            continue
        if max_known is not None and v < max_known:
            raise ValueError(
                f"Refusing to assign a numeric value for {v}: it is older than the current "
                f"max known version {max_known}. Backfilling would either renumber existing "
                f"members or break the append-only invariant."
            )

    next_value = (max(out.values()) + 1) if out else 1
    for v in sorted(new_versions):
        if v in out:
            continue
        out[v] = next_value
        next_value += 1
    return out


def render_versions_file(
    assignments: dict[SemVer, int],
    enum_attributes: dict[SemVer, list[str]] | None = None,
    const_attributes: dict[SemVer, list[str]] | None = None,
) -> tuple[str, str, str, str]:
    """Build the four auto-generated regions in canonical form.

    `assignments` maps each SemVer to its numeric enum value. Output is sorted ascending by
    SemVer. Numeric values are preserved exactly as supplied (never renumbered).

    `enum_attributes` / `const_attributes` carry the attribute lines that were already attached
    to a member (typically `[Obsolete("...")]`), so regenerating a region never strips a
    deprecation the maintainer applied by hand.
    """
    if not assignments:
        raise ValueError("Refusing to render an empty version list.")

    enum_attributes = enum_attributes or {}
    const_attributes = const_attributes or {}
    versions = sorted(assignments.keys())

    enum_lines: list[str] = []
    enum_lines.append("    // APPEND-ONLY. Numeric values are explicit and frozen for binary stability.")
    enum_lines.append("    // Updated by .github/workflows/check-documentdb-version.yml.")
    enum_lines.append("")
    for v in versions:
        enum_lines.append(f"    /// <summary>DocumentDB <c>{v}</c>.</summary>")
        enum_lines.extend(f"    {attribute}" for attribute in enum_attributes.get(v, ()))
        enum_lines.append(f"    {v.enum_member} = {assignments[v]},")
        enum_lines.append("")
    enum_block = "\n".join(enum_lines).rstrip()

    const_lines: list[str] = []
    const_lines.append("    // Per-version string constants are stable forever once shipped (safe as `const`).")
    const_lines.append("    // Updated by .github/workflows/check-documentdb-version.yml.")
    const_lines.append("")
    for v in versions:
        const_lines.append(f"    /// <summary>DocumentDB version string <c>{v}</c>.</summary>")
        const_lines.extend(f"    {attribute}" for attribute in const_attributes.get(v, ()))
        const_lines.append(f'    public const string {v.enum_member} = "{v}";')
        const_lines.append("")
    const_block = "\n".join(const_lines).rstrip()

    list_block = "\n".join(f"        {v.enum_member}," for v in versions)

    switch_block = "\n".join(
        f"        DocumentDBVersion.{v.enum_member} => {v.enum_member},"
        for v in versions
    )

    return enum_block, const_block, list_block, switch_block


def write_versions_file(source: VersionsSource, assignments: dict[SemVer, int]) -> None:
    """Rewrite the four auto-generated regions, verifying the result before it hits disk.

    The rendered text is re-parsed and checked with `assert_append_only` against what the file
    currently contains. Checking the rendered bytes (rather than the in-memory assignments the
    caller just built) is what makes the guard able to catch a rendering or region-replacement
    bug; and doing it before `write_text` means a failure leaves the file untouched.
    """
    blocks = render_versions_file(assignments, source.enum_attributes, source.const_attributes)

    # Count the regions BEFORE pairing them with the rendered blocks. Indexing `blocks` inside the
    # loop instead would turn "the file grew a fifth region" into a bare IndexError traceback in
    # the cron log, rather than the actionable message below.
    regions = list(AUTO_GEN_RE.finditer(source.text))
    if len(regions) != len(blocks):
        raise RuntimeError(
            f"Expected {len(blocks)} auto-generated regions in {source.path}, found {len(regions)}"
        )

    # Replace each auto-generated region in order.
    new_parts: list[str] = []
    cursor = 0
    for block, match in zip(blocks, regions):
        start, end = match.span()
        new_parts.append(source.text[cursor:start])
        new_parts.append(match.group(1) + block + match.group(3))
        cursor = end

    new_parts.append(source.text[cursor:])
    new_text = "".join(new_parts)

    written, _, _ = parse_versions_text(new_text, source.path)
    assert_append_only(source.values, written)

    source.path.write_text(new_text, encoding="utf-8")


def unreleased_section_bounds(text: str) -> tuple[int, int] | None:
    """Return the ``[start, end)`` offsets of the ``## [Unreleased]`` section, or None if absent.

    `end` is the offset of the next top-level release heading, or the end of the text. Both the
    placement guard and the bootstrap insertion point derive from this, so "where the Unreleased
    section stops" has exactly one definition and changing the heading convention is a one-line
    edit rather than two edits that can disagree.
    """
    start = text.find(UNRELEASED_HEADING)
    if start == -1:
        return None
    end = text.find("\n## [", start + 1)
    return start, len(text) if end == -1 else end


def warn_if_markers_outside_unreleased(
    changelog_file: Path, text: str, marker_start: int
) -> bool:
    """Warn when the auto-generated block does not live in the ``## [Unreleased]`` section.

    The block is rewritten in place on every run, so if it drifts into an already-released
    section the generated notes end up filed under the wrong release. Returns True when the
    placement is wrong (warning emitted), False otherwise.

    A missing ``## [Unreleased]`` heading counts as wrong, not as "nothing to check": that is
    exactly what a release cut produces when the heading is renamed to ``## [X.Y.Z] - <date>``
    and not re-added, and it means the markers are now inside an already-released section.
    """
    bounds = unreleased_section_bounds(text)
    if bounds is None:
        message = (
            f"{changelog_file.name} has no '{UNRELEASED_HEADING}' section, so the auto-generated "
            "versions block cannot be inside one; the generated notes are being written into an "
            "already-released section. Re-add the heading (a release cut renames it) and move "
            "the markers, with their body, back into it."
        )
        print(f"  [warn] {message}", file=sys.stderr)
        emit_github_annotation("warning", "CHANGELOG has no [Unreleased] section", message)
        return True

    section_start, section_end = bounds
    if section_start < marker_start < section_end:
        return False

    message = (
        f"the auto-generated versions block in {changelog_file.name} is outside the "
        f"'{UNRELEASED_HEADING}' section; generated notes will be filed under a released "
        "section. Move the markers (and their body) into [Unreleased]."
    )
    print(f"  [warn] {message}", file=sys.stderr)
    emit_github_annotation("warning", "CHANGELOG auto-generated block is misplaced", message)
    return True


def check_changelog_placement(changelog_file: Path) -> bool:
    """Run the placement guard over `changelog_file`; True when the markers are misplaced.

    main() calls this on EVERY run, not only when a version is adopted. A release cut that renames
    ``## [Unreleased]`` and forgets to re-add it is followed by weeks of runs with nothing to
    adopt, and a guard reachable only from the write path would sit unexecuted through all of
    them - green, unannotated, and quietly writing to the wrong section on the run that finally
    does find something.

    A file with no markers at all is not a placement problem: `update_changelog` bootstraps the
    block inside [Unreleased] the first time it runs.
    """
    text = changelog_file.read_text(encoding="utf-8")
    match = CHANGELOG_AUTO_GEN_RE.search(text)
    if match is None:
        return False
    return warn_if_markers_outside_unreleased(changelog_file, text, match.start())


def update_changelog(changelog_file: Path, new_versions: list[SemVer]) -> None:
    """Replace-in-place the auto-generated DocumentDB versions block in CHANGELOG.md.

    The block contains ONLY auto-detected upstream version notes. The surrounding
    ``## [Unreleased]`` header and any manually-authored changelog entries live
    OUTSIDE the marker block and are never touched by this script.

    If the marker block does not exist yet (bootstrap), insert an empty block at
    the end of the existing ``## [Unreleased]`` section, or — if no such section
    exists — right after the top-level title.
    """
    today = datetime.now(timezone.utc).strftime("%Y-%m-%d")
    body_lines = [
        "### Added (auto-detected upstream DocumentDB versions)",
        "",
    ]
    for v in new_versions:
        tag_list = ", ".join(f"`pg{pg}-{v}`" for pg in sorted(REQUIRED_PG_SET))
        body_lines.append(
            f"- DocumentDB `{v}` upstream release detected on {today} "
            f"(container tags {tag_list})."
        )
    body_lines.append("")
    body_lines.append(
        "_Maintainer: append the matching `DocumentDBVersion.V0_X_Y` enum members and "
        "`public const string V0_X_Y = \"X.Y.Z\";` lines to "
        "`src/Aspire.Hosting.DocumentDB/api/Aspire.Hosting.DocumentDB.cs` before merging._"
    )
    body = "\n".join(body_lines)

    text = changelog_file.read_text(encoding="utf-8")
    marker_match = CHANGELOG_AUTO_GEN_RE.search(text)
    if marker_match:
        # Detecting a misplaced block and then rewriting it anyway would annotate the run and
        # still commit the generated notes into an already-released section. main() checks the
        # placement before it writes anything, so reaching this raise means the file changed
        # underneath the run; either way, refusing is the only useful response.
        if warn_if_markers_outside_unreleased(changelog_file, text, marker_match.start()):
            raise RuntimeError(
                f"Refusing to rewrite the auto-generated block in {changelog_file.name}: it is "
                f"not inside the '{UNRELEASED_HEADING}' section, so the generated notes would be "
                "filed under an already-released version. Move the markers (and their body) back "
                "into [Unreleased] and re-run."
            )
        text = CHANGELOG_AUTO_GEN_RE.sub(
            lambda m: m.group(1) + body + m.group(3),
            text,
            count=1,
        )
    else:
        block = (
            "<!-- auto-generated:documentdb-versions-start -->\n"
            f"{body}\n"
            "<!-- auto-generated:documentdb-versions-end -->\n"
        )
        # Prefer to bootstrap inside the existing [Unreleased] section so future runs
        # never touch the manually-authored entries. Insert at the end of that section
        # (immediately before the next "## [" heading). Fall back to "right after the
        # top-level title" when no [Unreleased] section exists.
        bounds = unreleased_section_bounds(text)
        if bounds is None:
            first_break = text.find("\n\n")
            if first_break == -1:
                text = block + text
            else:
                text = text[: first_break + 2] + block + text[first_break + 2 :]
        elif bounds[1] == len(text):
            text = text.rstrip() + "\n\n" + block
        else:
            insertion_point = bounds[1] + 1  # keep the leading newline
            text = text[:insertion_point] + block + "\n" + text[insertion_point:]

    changelog_file.write_text(text, encoding="utf-8")


# ---------------------------------------------------------------------------
# Orchestration
# ---------------------------------------------------------------------------

def report_unadopted_versions(
    gh_versions: list[SemVer],
    ghcr_map: dict[SemVer, set[int]],
    known: set[SemVer],
) -> tuple[list[SemVer], list[SemVer]]:
    """Warn about every upstream release that exists but is not in the curated list.

    Returns (blocked, backfill). Three stall modes, all of which are otherwise indistinguishable
    from "nothing new upstream" - a silent, green run while adoption is stuck:

    * no container tags at all (the GitHub release usually lands before the image build finishes),
    * some tags but a required PG variant missing,
    * *leapfrogged*: a newer version was adopted while this one was incomplete, so it is now
      older than the newest curated member and can only be taken by a manual PR, because numeric
      enum values must never shift. Filtering on "newer than the newest shipped version" is what
      used to make this third case vanish the moment it became permanent - exactly when reporting
      it started to matter.

    Scope is releases at or above the OLDEST curated member. Anything below that was never a
    candidate (the curated list starts where it starts) and reporting it would bury the signal.
    A release the maintainer has decided never to adopt goes in ACKNOWLEDGED_SKIPS, so the
    warning does not recur every week forever.

    The run still exits 0 (an unadopted version is upstream's state, not an error here), so the
    findings are also emitted as GitHub Actions annotations - a green scheduled run whose log
    nobody opens is precisely the failure mode this reporting exists to prevent.
    """
    oldest_known = min(known) if known else None
    newest_known = max(known) if known else None
    acknowledged = acknowledged_skips()

    blocked: list[SemVer] = []
    blocked_reasons: list[str] = []
    backfill: list[SemVer] = []

    for version in sorted(gh_versions):
        if version in known or version in acknowledged:
            continue
        if oldest_known is not None and version < oldest_known:
            continue

        if newest_known is not None and version < newest_known:
            # Leapfrogged. Its tags may since have completed, but adoption is manual either way,
            # so the tag state is not worth reporting - the decision to make is the same.
            backfill.append(version)
            continue

        variants = ghcr_map.get(version) or set()
        missing = REQUIRED_PG_SET - variants
        if not missing:
            continue
        blocked.append(version)
        if not variants:
            # The usual cause: the GitHub release landed before the image build finished
            # (or the image build failed outright).
            reason = (
                f"{version} blocked: no pg tags published on GHCR yet. Adoption is deferred "
                "until the container images appear."
            )
        else:
            reason = (
                f"{version} blocked: missing required variants {sorted(missing)} on GHCR "
                f"(published: {sorted(variants)}). Adoption is deferred until they appear."
            )
        print(f"  [warn] {reason}", file=sys.stderr)
        blocked_reasons.append(reason)

    if blocked:
        emit_github_annotation(
            "warning",
            "DocumentDB adoption blocked",
            f"{len(blocked)} upstream release(s) cannot be adopted yet. "
            + " ".join(blocked_reasons),
        )

    if backfill:
        message = (
            f"skipping backfill candidate(s) {[str(v) for v in backfill]} - older than "
            f"shipped max {newest_known}; open a manual PR if intentional, or add them to "
            "ACKNOWLEDGED_SKIPS to stop reporting them."
        )
        print(f"  [warn] {message}", file=sys.stderr)
        emit_github_annotation("warning", "DocumentDB backfill candidate skipped", message)

    return blocked, backfill


def main() -> int:
    print(f"Checking upstream DocumentDB releases at {datetime.now(timezone.utc).isoformat()}")
    print(f"  REPO_ROOT       = {REPO_ROOT}")
    print(f"  VERSIONS_FILE   = {VERSIONS_FILE}")
    print(f"  REQUIRED_PG_SET = {sorted(REQUIRED_PG_SET)}")

    try:
        gh_versions = fetch_github_releases(GH_OWNER, GH_REPO)
    except (urllib.error.URLError, RuntimeError) as e:
        print(f"ERROR fetching GitHub releases: {e}", file=sys.stderr)
        return 2

    try:
        ghcr_map = fetch_ghcr_pg_tags(GHCR_IMAGE_PATH)
    except (urllib.error.URLError, RuntimeError) as e:
        print(f"ERROR fetching GHCR tags: {e}", file=sys.stderr)
        return 2

    print(f"  GH releases parsed   : {len(gh_versions)}")
    print(f"  GHCR versions parsed : {len(ghcr_map)}")

    versions_source = VersionsSource.load(VERSIONS_FILE)
    known_assignments = versions_source.values
    known = sorted(known_assignments.keys())
    max_known = max(known) if known else None
    print(f"  Known to package    : {[str(v) for v in known]}")

    # Unconditional, because the failure it catches (a release cut that renamed the
    # "## [Unreleased]" heading and did not re-add it) is followed by weeks of runs with nothing
    # to adopt. Checking only on the write path would let every one of those pass silently.
    changelog_misplaced = check_changelog_placement(CHANGELOG_FILE)

    # Intersection: must be in BOTH sources, and ALL required pg variants must be present.
    intersected = sorted(
        v for v in gh_versions
        if v in ghcr_map and REQUIRED_PG_SET.issubset(ghcr_map[v])
    )
    print(f"  Intersection found  : {[str(v) for v in intersected]}")

    # Surface versions that exist upstream but were not adopted - held back by a missing required
    # variant, or leapfrogged - so "stuck" adoption is never silent.
    report_unadopted_versions(gh_versions, ghcr_map, set(known))

    if not intersected:
        # Not a routine no-op. REQUIRED_PG_SET is narrow enough that only a handful of releases
        # clear it, so "not one of them does" is also what a GHCR tag list or release feed that
        # changed shape looks like - and that fetch returns an empty result rather than raising.
        # Saying so is the difference between a detected regression and a permanently green cron.
        message = (
            f"no upstream release has the full required variant set {sorted(REQUIRED_PG_SET)} "
            f"({len(gh_versions)} release(s) and {len(ghcr_map)} tagged version(s) parsed). "
            "Nothing can be adopted; if that is unexpected, check whether the GHCR tag list or "
            "the release feed changed shape."
        )
        print(f"  [warn] {message}", file=sys.stderr)
        emit_github_annotation("warning", "DocumentDB version detection found nothing", message)
        return 0

    # Append-only: never remove a version we already shipped, even if it disappears upstream.
    new_versions = sorted(set(intersected) - set(known))

    # Never "backfill" a version older than the current max known: numeric enum values for
    # existing members must never shift, so adopting an older version is a manual decision.
    # Skipping just those candidates (rather than failing the whole run) keeps adoption of
    # NEWER versions working, which matters because a required variant can lag a release.
    # report_unadopted_versions has already warned and annotated for the ones at or above the
    # oldest curated version; anything below that was never a candidate and is dropped silently
    # on purpose, because the curated list starts where it starts.
    if max_known is not None:
        new_versions = [v for v in new_versions if v > max_known]

    if not new_versions:
        print("No new versions detected. Nothing to do.")
        return 0

    print(f"  NEW versions        : {[str(v) for v in new_versions]}")

    if changelog_misplaced:
        # Checked here, before anything is written, so a misplaced block cannot leave
        # DocumentDBVersion.cs adopted and the CHANGELOG filed under the wrong release.
        message = (
            f"refusing to adopt {[str(v) for v in new_versions]}: the auto-generated block in "
            f"{CHANGELOG_FILE.name} is not inside '{UNRELEASED_HEADING}', so the release notes "
            "would be filed under an already-released version. Move the markers (and their "
            "body) back into [Unreleased] and re-run."
        )
        print(f"ERROR: {message}", file=sys.stderr)
        emit_github_annotation(
            "error", "DocumentDB adoption blocked by CHANGELOG placement", message
        )
        return 2

    target_assignments = assign_numeric_values(known_assignments, new_versions)

    # The append-only guard lives inside write_versions_file, which applies it to the RENDERED
    # text rather than to `target_assignments`: checking the dict here could never fail, because
    # assign_numeric_values starts from a copy of `known_assignments` and only adds keys.
    write_versions_file(versions_source, target_assignments)
    print(f"Updated {VERSIONS_FILE.relative_to(REPO_ROOT)}")

    update_changelog(CHANGELOG_FILE, new_versions)
    print(f"Updated {CHANGELOG_FILE.relative_to(REPO_ROOT)}")

    # Emit a key=value to GITHUB_OUTPUT so the workflow can compose its PR title/body.
    out = os.environ.get("GITHUB_OUTPUT")
    if out:
        with open(out, "a", encoding="utf-8") as f:
            f.write(f"new_versions={','.join(str(v) for v in new_versions)}\n")
            f.write(f"all_versions={','.join(str(v) for v in sorted(target_assignments.keys()))}\n")

    return 0


if __name__ == "__main__":
    sys.exit(main())
