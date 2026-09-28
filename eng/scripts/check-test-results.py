#!/usr/bin/env python3
# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.
"""Fail a CI leg whose TRX results ran nothing, skipped something, or are missing.

`dotnet test` exits 0 when a filter matches no test and when xUnit skips one, so a mistyped
shard filter or a new skip would otherwise pass the gate silently.
"""
from __future__ import annotations

import argparse
import os
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


class ResultsError(Exception):
    pass


def _local(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def read_counters(trx: Path) -> dict[str, int]:
    try:
        root = ET.parse(trx).getroot()
    except (ET.ParseError, OSError) as error:
        raise ResultsError(f"{trx} is not a readable TRX file: {error}.") from error

    for element in root.iter():
        if _local(element.tag) == "Counters":
            return {name: int(value) for name, value in element.attrib.items() if value.isdigit()}
    raise ResultsError(f"{trx} has no ResultSummary counters.")


def check(results: Path) -> str:
    trx_files = sorted(results.rglob("*.trx"))
    if not trx_files:
        raise ResultsError(f"No .trx file under {results}; the test step produced no results.")

    total = {"total": 0, "executed": 0, "passed": 0}
    for trx in trx_files:
        counters = read_counters(trx)
        for name in total:
            total[name] += counters.get(name, 0)

    if total["executed"] == 0:
        raise ResultsError("The run executed 0 tests; the filter selects nothing.")
    # The TRX logger always writes notExecuted="0"; a skip only shows as total > executed.
    skipped = total["total"] - total["executed"]
    if skipped > 0:
        raise ResultsError(
            f"{skipped} test(s) did not execute (skipped). Skips are not allowed in "
            "the gate; fix the condition or move the test out of this leg."
        )
    if total["passed"] != total["executed"]:
        raise ResultsError(f"Only {total['passed']} of {total['executed']} executed tests passed.")

    return f"{total['executed']} executed, {total['passed']} passed, 0 skipped."


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("results", type=Path, help="Directory searched recursively for .trx files")
    args = parser.parse_args(argv)

    try:
        summary = check(args.results)
    except ResultsError as error:
        print(f"Test results check failed: {error}", file=sys.stderr)
        if os.environ.get("GITHUB_ACTIONS") == "true":
            annotation = str(error).replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")
            print(f"::error title=Test results check failed::{annotation}", file=sys.stderr)
        return 1

    print(f"Test results: {summary}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
