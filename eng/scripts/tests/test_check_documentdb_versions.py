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

import importlib.util
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

REPO_ROOT = Path(__file__).resolve().parents[3]
SCRIPT_PATH = REPO_ROOT / "eng" / "scripts" / "check-documentdb-versions.py"


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


class RequiredPgSetTests(unittest.TestCase):
    def test_contains_every_variant_published_upstream(self):
        # pg18 is published upstream from DocumentDB 0.114.0 onwards and mirrors
        # DocumentDBPostgresVersion in src/Aspire.Hosting.DocumentDB/DocumentDBVersion.cs.
        self.assertEqual({15, 16, 17, 18}, set(script.REQUIRED_PG_SET))

    def test_known_ghcr_variants_do_not_trigger_the_unknown_warning(self):
        self.assertEqual(set(), {15, 16, 17, 18} - script.REQUIRED_PG_SET)


class UpdateChangelogTests(unittest.TestCase):
    TEMPLATE = (
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


if __name__ == "__main__":
    unittest.main()
