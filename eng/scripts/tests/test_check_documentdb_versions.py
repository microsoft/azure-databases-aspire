# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.
"""Unit tests for eng/scripts/check-documentdb-versions.py.

Run from the repository root with the standard library only (no pip dependencies):

    python -m unittest discover -s eng/scripts/tests

These tests cover the parts of the script that only execute when a *new* upstream version is
detected — in particular that the generated CHANGELOG line derives its container-tag list from
``REQUIRED_PG_SET`` rather than hardcoding pg15/pg16/pg17, which is how it silently drifted when
``pg18`` was introduced in DocumentDB 0.114.0.
"""
from __future__ import annotations

import contextlib
import importlib.util
import io
import shutil
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

REPO_ROOT = Path(__file__).resolve().parents[3]
SCRIPT_PATH = REPO_ROOT / "eng" / "scripts" / "check-documentdb-versions.py"
REAL_VERSIONS_FILE = REPO_ROOT / "src" / "Aspire.Hosting.DocumentDB" / "DocumentDBVersion.cs"
REAL_CHANGELOG_FILE = REPO_ROOT / "CHANGELOG.md"

# Models the expected production layout: the auto-generated block lives at the end of the
# [Unreleased] section, so regenerated notes never land inside a released section.
CHANGELOG_TEMPLATE = (
    "# Changelog\n"
    "\n"
    "## [Unreleased]\n"
    "\n"
    "<!-- auto-generated:documentdb-versions-start -->\n"
    "previous content\n"
    "<!-- auto-generated:documentdb-versions-end -->\n"
    "\n"
    "## [0.114.0] - 2026-07-20\n"
    "\n"
    "hand-written release notes\n"
)


def _load_script():
    """Import the hyphenated script file as a module."""
    spec = importlib.util.spec_from_file_location("check_documentdb_versions", SCRIPT_PATH)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    # Registering the module is required so dataclasses can resolve its __module__.
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


script = _load_script()


def _semver(text: str):
    major, minor, patch = (int(part) for part in text.split("."))
    return script.SemVer(major, minor, patch)


def _full_variants(*versions: str) -> dict:
    """GHCR map where every listed version has every required PG variant."""
    return {_semver(v): set(script.REQUIRED_PG_SET) for v in versions}


class FakeRepo:
    """Minimal on-disk copy of the files main() rewrites, isolated in a temp directory."""

    def __init__(self) -> None:
        self.root = Path(tempfile.mkdtemp())
        self.versions_file = self.root / "src" / "Aspire.Hosting.DocumentDB" / "DocumentDBVersion.cs"
        self.versions_file.parent.mkdir(parents=True)
        shutil.copyfile(REAL_VERSIONS_FILE, self.versions_file)
        self.changelog_file = self.root / "CHANGELOG.md"
        self.changelog_file.write_text(CHANGELOG_TEMPLATE, encoding="utf-8")

    def run_main(self, gh_versions, ghcr_map) -> tuple[int, str, str]:
        """Run main() against this fake repo; returns (exit_code, stdout, stderr)."""
        stdout, stderr = io.StringIO(), io.StringIO()
        with mock.patch.object(script, "REPO_ROOT", self.root), \
                mock.patch.object(script, "VERSIONS_FILE", self.versions_file), \
                mock.patch.object(script, "CHANGELOG_FILE", self.changelog_file), \
                mock.patch.object(script, "fetch_github_releases", return_value=gh_versions), \
                mock.patch.object(script, "fetch_ghcr_pg_tags", return_value=ghcr_map), \
                mock.patch.dict(script.os.environ, {"GITHUB_OUTPUT": ""}), \
                contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr):
            code = script.main()
        return code, stdout.getvalue(), stderr.getvalue()

    def versions_text(self) -> str:
        return self.versions_file.read_text(encoding="utf-8")

    def changelog_text(self) -> str:
        return self.changelog_file.read_text(encoding="utf-8")


class RequiredPgSetTests(unittest.TestCase):
    def test_contains_every_variant_published_upstream(self):
        # pg18 is published upstream from DocumentDB 0.114.0 onwards and mirrors
        # DocumentDBPostgresVersion in src/Aspire.Hosting.DocumentDB/DocumentDBVersion.cs.
        self.assertEqual({15, 16, 17, 18}, set(script.REQUIRED_PG_SET))

    def test_known_ghcr_variants_do_not_trigger_the_unknown_warning(self):
        self.assertEqual(set(), {15, 16, 17, 18} - script.REQUIRED_PG_SET)


class UpdateChangelogTests(unittest.TestCase):
    TEMPLATE = CHANGELOG_TEMPLATE

    def _write_changelog(self) -> Path:
        path = Path(tempfile.mkdtemp()) / "CHANGELOG.md"
        path.write_text(self.TEMPLATE, encoding="utf-8")
        return path

    def test_tag_list_covers_every_required_pg_variant(self):
        path = self._write_changelog()

        script.update_changelog(path, [script.SemVer(0, 115, 0)])
        text = path.read_text(encoding="utf-8")

        for pg in sorted(script.REQUIRED_PG_SET):
            self.assertIn(f"`pg{pg}-0.115.0`", text)

    def test_tag_list_is_derived_from_required_pg_set(self):
        path = self._write_changelog()

        with mock.patch.object(script, "REQUIRED_PG_SET", frozenset({16, 19})):
            script.update_changelog(path, [script.SemVer(0, 115, 0)])

        text = path.read_text(encoding="utf-8")
        self.assertIn("(container tags `pg16-0.115.0`, `pg19-0.115.0`).", text)
        self.assertNotIn("pg15-0.115.0", text)
        self.assertNotIn("pg17-0.115.0", text)

    def test_replaces_block_in_place_without_touching_handwritten_sections(self):
        path = self._write_changelog()

        script.update_changelog(path, [script.SemVer(0, 115, 0)])
        script.update_changelog(path, [script.SemVer(0, 116, 0)])
        text = path.read_text(encoding="utf-8")

        self.assertEqual(1, text.count("<!-- auto-generated:documentdb-versions-start -->"))
        self.assertNotIn("previous content", text)
        self.assertNotIn("0.115.0", text)
        self.assertIn("`pg18-0.116.0`", text)
        self.assertIn("## [0.114.0] - 2026-07-20", text)
        self.assertIn("hand-written release notes", text)


class BackfillHandlingTests(unittest.TestCase):
    """A1: a backfill candidate must not wedge adoption of newer versions."""

    def test_backfill_candidate_is_skipped_but_newer_version_is_adopted(self):
        # Week 2 of the finding's scenario: a newer version was adopted while a required
        # variant for an older release was still building. Now the older release is complete
        # (a backfill candidate) and must not block adoption of a genuinely newer one.
        repo = FakeRepo()
        gh = [_semver("0.113.5"), _semver("0.117.0")]

        code, stdout, stderr = repo.run_main(gh, _full_variants("0.113.5", "0.117.0"))

        self.assertEqual(0, code)
        self.assertIn("skipping backfill candidate(s) ['0.113.5']", stderr)
        text = repo.versions_text()
        self.assertIn("V0_117_0 = 7,", text)
        self.assertNotIn("V0_113_5", text)
        # Existing members keep their numeric values.
        self.assertIn("V0_114_0 = 6,", text)
        self.assertIn("`0.117.0` upstream release detected", repo.changelog_text())

    def test_run_with_only_backfill_candidates_is_a_warned_no_op(self):
        repo = FakeRepo()
        before = repo.versions_text()

        code, stdout, stderr = repo.run_main([_semver("0.115.0")], _full_variants("0.115.0"))
        self.assertEqual(0, code)  # sanity: a newer version alone is adopted
        self.assertIn("V0_115_0 = 7,", repo.versions_text())

        repo = FakeRepo()
        code, stdout, stderr = repo.run_main([_semver("0.113.5")], _full_variants("0.113.5"))

        self.assertEqual(0, code)
        self.assertIn("skipping backfill candidate(s) ['0.113.5']", stderr)
        self.assertIn("No new versions detected", stdout)
        self.assertEqual(before, repo.versions_text())


class BlockedVersionReportingTests(unittest.TestCase):
    """A2: a version held back by a missing required variant must be reported, not silent."""

    def test_missing_required_variant_is_reported_on_stderr(self):
        repo = FakeRepo()
        before = repo.versions_text()

        code, stdout, stderr = repo.run_main(
            [_semver("0.115.0")], {_semver("0.115.0"): {15, 16, 17}}
        )

        self.assertEqual(0, code)
        self.assertIn("0.115.0 blocked", stderr)
        self.assertIn("[18]", stderr)
        self.assertEqual(before, repo.versions_text())

    def test_complete_versions_are_not_reported_as_blocked(self):
        repo = FakeRepo()

        _, _, stderr = repo.run_main([_semver("0.115.0")], _full_variants("0.115.0"))

        self.assertNotIn("blocked", stderr)

    def test_already_shipped_versions_are_not_reported_as_blocked(self):
        # 0.109.0 predates pg18 entirely; it is shipped, so it is not an adoption candidate.
        repo = FakeRepo()

        _, _, stderr = repo.run_main(
            [_semver("0.109.0")], {_semver("0.109.0"): {15, 16, 17}}
        )

        self.assertNotIn("blocked", stderr)


class ParseKnownVersionsTests(unittest.TestCase):
    """A3: a degraded parse must crash instead of silently shrinking the shipped enum."""

    def _write_versions_file(self, text: str) -> Path:
        path = Path(tempfile.mkdtemp()) / "DocumentDBVersion.cs"
        path.write_text(text, encoding="utf-8")
        return path

    def test_parses_the_real_versions_file(self):
        known = script.parse_known_versions(REAL_VERSIONS_FILE)

        self.assertIn(_semver("0.114.0"), known)
        self.assertEqual(sorted(known.values()), list(range(1, len(known) + 1)))

    def test_member_without_numeric_value_is_a_hard_error(self):
        text = REAL_VERSIONS_FILE.read_text(encoding="utf-8").replace(
            "V0_113_0 = 5,", "V0_113_0,"
        )
        path = self._write_versions_file(text)

        with self.assertRaises(RuntimeError) as ctx:
            script.parse_known_versions(path)

        self.assertIn("V0_113_0", str(ctx.exception))

    def test_empty_enum_region_is_a_hard_error(self):
        text = (
            "// <auto-generated-versions-start>\n"
            "    // only comments here\n"
            "    // <auto-generated-versions-end>\n"
        )
        path = self._write_versions_file(text)

        with self.assertRaises(RuntimeError):
            script.parse_known_versions(path)


class AssertAppendOnlyTests(unittest.TestCase):
    """A3(b): writing must only ever append."""

    def setUp(self):
        self.existing = {_semver("0.113.0"): 1, _semver("0.114.0"): 2}

    def test_appending_is_allowed(self):
        target = dict(self.existing)
        target[_semver("0.115.0")] = 3

        script.assert_append_only(self.existing, target)  # must not raise

    def test_dropping_a_shipped_member_is_rejected(self):
        target = {_semver("0.114.0"): 2}

        with self.assertRaises(RuntimeError):
            script.assert_append_only(self.existing, target)

    def test_renumbering_a_shipped_member_is_rejected(self):
        target = dict(self.existing)
        target[_semver("0.113.0")] = 9
        target[_semver("0.115.0")] = 3

        with self.assertRaises(RuntimeError) as ctx:
            script.assert_append_only(self.existing, target)

        self.assertIn("renumbered", str(ctx.exception))


class ChangelogMarkerPlacementTests(unittest.TestCase):
    """A4: the rewritten block must live in [Unreleased], never in a released section."""

    def test_repo_changelog_markers_live_in_the_unreleased_section(self):
        text = REAL_CHANGELOG_FILE.read_text(encoding="utf-8")
        match = script.CHANGELOG_AUTO_GEN_RE.search(text)
        self.assertIsNotNone(match, "CHANGELOG.md lost its auto-generated markers.")

        stderr = io.StringIO()
        with contextlib.redirect_stderr(stderr):
            misplaced = script.warn_if_markers_outside_unreleased(
                REAL_CHANGELOG_FILE, text, match.start()
            )

        self.assertFalse(misplaced, stderr.getvalue())

    def test_markers_inside_a_released_section_are_reported(self):
        text = (
            "# Changelog\n"
            "\n"
            "## [Unreleased]\n"
            "\n"
            "## [0.114.0] - 2026-07-20\n"
            "\n"
            "<!-- auto-generated:documentdb-versions-start -->\n"
            "body\n"
            "<!-- auto-generated:documentdb-versions-end -->\n"
        )
        path = Path(tempfile.mkdtemp()) / "CHANGELOG.md"
        path.write_text(text, encoding="utf-8")
        match = script.CHANGELOG_AUTO_GEN_RE.search(text)

        stderr = io.StringIO()
        with contextlib.redirect_stderr(stderr):
            misplaced = script.warn_if_markers_outside_unreleased(path, text, match.start())

        self.assertTrue(misplaced)
        self.assertIn("[Unreleased]", stderr.getvalue())


if __name__ == "__main__":
    unittest.main()
