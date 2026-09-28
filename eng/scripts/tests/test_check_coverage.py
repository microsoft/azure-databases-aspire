# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.
from __future__ import annotations

import contextlib
import importlib.util
import io
import json
import os
import shutil
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

REPO_ROOT = Path(__file__).resolve().parents[3]
SCRIPT_PATH = REPO_ROOT / "eng" / "scripts" / "check-coverage.py"

spec = importlib.util.spec_from_file_location("check_coverage", SCRIPT_PATH)
check_coverage = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = check_coverage
spec.loader.exec_module(check_coverage)

# Two packages; method elements repeat class lines and must not be double counted.
REPORT = """<?xml version="1.0"?>
<coverage line-rate="0.1" branch-rate="0.1">
  <sources><source>{source}</source></sources>
  <packages>
    <package name="Product">
      <classes>
        <class name="A" filename="a.cs">
          <methods><method name="M"><lines>
            <line number="1" hits="1" branch="False" />
          </lines></method></methods>
          <lines>
            <line number="1" hits="1" branch="False" />
            <line number="2" hits="1" branch="True" condition-coverage="50% (1/2)" />
            <line number="3" hits="0" branch="False" />
            <line number="4" hits="1" branch="True" condition-coverage="100% (2/2)" />
          </lines>
        </class>
      </classes>
    </package>
    <package name="Tests">
      <classes>
        <class name="T" filename="t.cs">
          <lines><line number="1" hits="0" branch="True" condition-coverage="0% (0/2)" /></lines>
        </class>
      </classes>
    </package>
  </packages>
</coverage>
"""


class CheckCoverageTests(unittest.TestCase):
    def setUp(self) -> None:
        self.directory = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.directory)
        self.report = self.directory / "coverage.xml"
        self.report.write_text(REPORT.format(source=self.directory), encoding="utf-8")

    def baseline(self, line: float, branch: float) -> Path:
        path = self.directory / "baseline.json"
        path.write_text(json.dumps({"line": line, "branch": branch}), encoding="utf-8")
        return path

    def test_measures_class_lines_of_one_package(self) -> None:
        line, branch, files = check_coverage.measure(self.report, "Product")

        self.assertEqual(75.0, line)
        self.assertEqual(75.0, branch)
        self.assertEqual({(self.directory / "a.cs").resolve()}, files)

    def test_measures_whole_report_without_package(self) -> None:
        line, branch, _ = check_coverage.measure(self.report)

        self.assertEqual(60.0, line)
        self.assertEqual(50.0, branch)

    def test_passes_at_baseline_and_hints_when_well_above(self) -> None:
        summary = check_coverage.check(self.report, self.baseline(74.0, 75.0), "Product")

        self.assertIn("line coverage 75.00% (baseline 74.0%)", summary)
        self.assertTrue(any("raise it" in line for line in summary))

    def test_line_and_branch_are_enforced_separately(self) -> None:
        for line, branch, kind in ((75.1, 0.0, "line"), (0.0, 75.1, "branch")):
            with self.subTest(kind=kind), self.assertRaisesRegex(
                check_coverage.CoverageError, f"^{kind} coverage 75.00% is below"
            ):
                check_coverage.check(self.report, self.baseline(line, branch), "Product")

    def test_missing_report_fails(self) -> None:
        with self.assertRaisesRegex(check_coverage.CoverageError, "does not exist"):
            check_coverage.check(self.directory / "absent.xml", self.baseline(0, 0))

    def test_report_without_branch_data_fails(self) -> None:
        self.report.write_text(
            REPORT.format(source=self.directory).replace('condition-coverage="50% (1/2)"', "")
            .replace('condition-coverage="100% (2/2)"', ""),
            encoding="utf-8",
        )

        with self.assertRaisesRegex(check_coverage.CoverageError, "no branch data"):
            check_coverage.check(self.report, self.baseline(0, 0), "Product")

    def test_unknown_package_fails(self) -> None:
        with self.assertRaisesRegex(check_coverage.CoverageError, "no package named 'Other'"):
            check_coverage.check(self.report, self.baseline(0, 0), "Other")

    def test_required_file_absent_from_report_fails(self) -> None:
        check_coverage.check(self.report, self.baseline(0, 0), "Product", [self.directory / "a.cs"])

        with self.assertRaisesRegex(check_coverage.CoverageError, "does not include: .*b.py"):
            check_coverage.check(self.report, self.baseline(0, 0), "Product", [self.directory / "b.py"])

    def test_malformed_baseline_fails(self) -> None:
        path = self.directory / "baseline.json"
        path.write_text('{"line": 1}', encoding="utf-8")

        with self.assertRaisesRegex(check_coverage.CoverageError, "numeric 'line' and 'branch'"):
            check_coverage.check(self.report, path)

    def test_main_reports_failure_as_github_annotation(self) -> None:
        stderr = io.StringIO()
        with mock.patch.dict(os.environ, {"GITHUB_ACTIONS": "true"}), contextlib.redirect_stderr(stderr):
            exit_code = check_coverage.main(
                ["--report", str(self.report), "--baseline", str(self.baseline(90, 0)), "--package", "Product"]
            )

        self.assertEqual(1, exit_code)
        self.assertIn("::error title=Coverage check failed::line coverage 75.00%25 is below", stderr.getvalue())

    def test_main_prints_summary_on_success(self) -> None:
        stdout = io.StringIO()
        with contextlib.redirect_stdout(stdout):
            exit_code = check_coverage.main(
                ["--report", str(self.report), "--baseline", str(self.baseline(75, 75)), "--package", "Product"]
            )

        self.assertEqual(0, exit_code)
        self.assertIn("branch coverage 75.00% (baseline 75.0%)", stdout.getvalue())


if __name__ == "__main__":
    unittest.main()
