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
SCRIPT_PATH = REPO_ROOT / "eng" / "scripts" / "check-test-results.py"

spec = importlib.util.spec_from_file_location("check_test_results", SCRIPT_PATH)
check_test_results = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = check_test_results
spec.loader.exec_module(check_test_results)


# Counter shape the VSTest TRX logger really writes: a skip lowers executed, notExecuted stays 0.
def trx(total: int, executed: int, passed: int) -> str:
    return f"""<?xml version="1.0" encoding="utf-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <ResultSummary outcome="Completed">
    <Counters total="{total}" executed="{executed}" passed="{passed}" failed="{executed - passed}"
              error="0" timeout="0" aborted="0" inconclusive="0" passedButRunAborted="0"
              notRunnable="0" notExecuted="0" disconnected="0" warning="0"
              completed="0" inProgress="0" pending="0" />
  </ResultSummary>
</TestRun>"""


class CheckTestResultsTests(unittest.TestCase):
    def setUp(self) -> None:
        self._directory = tempfile.TemporaryDirectory()
        self.results = Path(self._directory.name)

    def tearDown(self) -> None:
        self._directory.cleanup()

    def write(self, name: str, content: str) -> None:
        path = self.results / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8")

    def run_main(self) -> tuple[int, str]:
        stderr = io.StringIO()
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(stderr):
            code = check_test_results.main([str(self.results)])
        return code, stderr.getvalue()

    def test_passing_run_is_accepted_and_nested_files_are_summed(self) -> None:
        self.write("a.trx", trx(3, 3, 3))
        self.write("nested/b.trx", trx(2, 2, 2))
        self.assertEqual(check_test_results.check(self.results), "5 executed, 5 passed, 0 skipped.")

    def test_missing_results_fail(self) -> None:
        code, stderr = self.run_main()
        self.assertEqual(code, 1)
        self.assertIn("No .trx file", stderr)

    def test_zero_executed_fails(self) -> None:
        self.write("a.trx", trx(0, 0, 0))
        code, stderr = self.run_main()
        self.assertEqual(code, 1)
        self.assertIn("executed 0 tests", stderr)

    def test_skipped_test_fails(self) -> None:
        self.write("a.trx", trx(3, 2, 2))
        code, stderr = self.run_main()
        self.assertEqual(code, 1)
        self.assertIn("1 test(s) did not execute", stderr)

    def test_failed_test_fails(self) -> None:
        self.write("a.trx", trx(3, 3, 2))
        code, stderr = self.run_main()
        self.assertEqual(code, 1)
        self.assertIn("Only 2 of 3", stderr)

    def test_unreadable_trx_fails(self) -> None:
        self.write("a.trx", "<not xml")
        code, stderr = self.run_main()
        self.assertEqual(code, 1)
        self.assertIn("not a readable TRX file", stderr)

    def test_trx_without_counters_fails(self) -> None:
        self.write("a.trx", "<TestRun />")
        code, stderr = self.run_main()
        self.assertEqual(code, 1)
        self.assertIn("no ResultSummary counters", stderr)


if __name__ == "__main__":
    unittest.main()
