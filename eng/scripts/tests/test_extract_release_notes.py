# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.
from __future__ import annotations

import contextlib
import importlib.util
import io
import sys
import tempfile
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[3]
SCRIPT_PATH = REPO_ROOT / "eng" / "scripts" / "extract-release-notes.py"

CHANGELOG = """# Changelog

## [Unreleased]

- Pending.

## [1.2.0] - 2026-01-02

### Added
- New thing.

## [1.1.0]

## [1.0.0] - 2025-12-01

- First release.
"""


def _load_script():
    spec = importlib.util.spec_from_file_location("extract_release_notes", SCRIPT_PATH)
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


script = _load_script()


class ExtractReleaseNotesTests(unittest.TestCase):
    def test_extracts_section_with_date_suffix_up_to_next_heading(self):
        self.assertEqual("### Added\n- New thing.", script.extract_release_notes(CHANGELOG, "v1.2.0"))

    def test_extracts_last_section_in_file(self):
        self.assertEqual("- First release.", script.extract_release_notes(CHANGELOG, "v1.0.0"))

    def test_missing_heading_fails(self):
        with self.assertRaisesRegex(script.ReleaseNotesError, r"no '## \[9\.9\.9\]' heading"):
            script.extract_release_notes(CHANGELOG, "v9.9.9")

    def test_empty_section_fails(self):
        with self.assertRaisesRegex(script.ReleaseNotesError, "is empty"):
            script.extract_release_notes(CHANGELOG, "v1.1.0")

    def test_version_is_matched_literally(self):
        with self.assertRaises(script.ReleaseNotesError):
            script.extract_release_notes(CHANGELOG, "v1x2.0")

    def test_repository_changelog_has_notes_for_a_shipped_release(self):
        notes = script.extract_release_notes(
            (REPO_ROOT / "CHANGELOG.md").read_text(encoding="utf-8"), "v0.116.0"
        )
        self.assertTrue(notes.startswith("### Added"))
        self.assertNotIn("## [0.114.1]", notes)

    def test_main_writes_notes_and_fails_without_writing_on_missing_heading(self):
        with tempfile.TemporaryDirectory() as directory:
            changelog = Path(directory) / "CHANGELOG.md"
            changelog.write_text(CHANGELOG, encoding="utf-8")
            output = Path(directory) / "notes.md"

            args = ["--changelog", str(changelog), "--output", str(output)]
            with contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(0, script.main([*args, "--tag", "v1.0.0"]))
            self.assertEqual("- First release.\n", output.read_text(encoding="utf-8"))

            output.unlink()
            with contextlib.redirect_stderr(io.StringIO()) as stderr:
                self.assertEqual(1, script.main([*args, "--tag", "v9.9.9"]))
            self.assertIn("Release notes extraction failed", stderr.getvalue())
            self.assertFalse(output.exists())


if __name__ == "__main__":
    unittest.main()
