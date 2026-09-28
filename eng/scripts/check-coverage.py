#!/usr/bin/env python3
# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.
"""Fail when line or branch coverage in a Cobertura report drops below its committed baseline."""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

CONDITION_COVERAGE = re.compile(r"\((\d+)/(\d+)\)")


class CoverageError(Exception):
    pass


def measure(report: Path, package: str | None = None) -> tuple[float, float, set[Path]]:
    """Return (line %, branch %, covered source files) for the report, or one package in it."""
    try:
        root = ET.parse(report).getroot()
    except FileNotFoundError as error:
        raise CoverageError(f"Coverage report {report} does not exist.") from error
    except ET.ParseError as error:
        raise CoverageError(f"Coverage report {report} is not valid XML: {error}.") from error

    sources = [Path(source.text.strip()) for source in root.iter("source") if source.text] or [Path.cwd()]
    packages = [p for p in root.iter("package") if package is None or p.get("name") == package]
    if not packages:
        raise CoverageError(f"Coverage report {report} has no package named {package!r}.")

    lines_valid = lines_covered = branches_valid = branches_covered = 0
    files: set[Path] = set()
    for class_element in (c for p in packages for c in p.iter("class")):
        filename = Path(class_element.get("filename", ""))
        files.update(
            [filename.resolve()] if filename.is_absolute() else [(s / filename).resolve() for s in sources]
        )
        # Class-level lines only: method elements repeat the same lines.
        for line in class_element.findall("lines/line"):
            lines_valid += 1
            lines_covered += int(line.get("hits", "0")) > 0
            match = CONDITION_COVERAGE.search(line.get("condition-coverage") or "")
            if match:
                branches_covered += int(match.group(1))
                branches_valid += int(match.group(2))

    if lines_valid == 0:
        raise CoverageError(f"Coverage report {report} has no measured lines.")
    if branches_valid == 0:
        raise CoverageError(f"Coverage report {report} has no branch data; was branch coverage collected?")
    return 100 * lines_covered / lines_valid, 100 * branches_covered / branches_valid, files


def check(
    report: Path, baseline: Path, package: str | None = None, require_files: list[Path] | None = None
) -> list[str]:
    """Return the summary lines; raise CoverageError on any regression."""
    try:
        expected = json.loads(baseline.read_text(encoding="utf-8"))
        floors = {"line": float(expected["line"]), "branch": float(expected["branch"])}
    except (OSError, ValueError, KeyError, TypeError) as error:
        raise CoverageError(f"Baseline {baseline} must be JSON with numeric 'line' and 'branch': {error}.") from error

    line, branch, files = measure(report, package)
    missing = [str(f) for f in require_files or [] if f.resolve() not in files]
    if missing:
        raise CoverageError(f"Coverage report {report} does not include: {', '.join(missing)}.")

    failures = []
    summary = []
    for kind, actual in (("line", line), ("branch", branch)):
        summary.append(f"{kind} coverage {actual:.2f}% (baseline {floors[kind]:.1f}%)")
        if actual < floors[kind]:
            failures.append(f"{kind} coverage {actual:.2f}% is below the baseline {floors[kind]:.1f}% in {baseline}")
        elif actual >= floors[kind] + 0.5:
            summary.append(f"  {kind} coverage is {actual - floors[kind]:.1f} points above baseline; raise it in {baseline}")
    if failures:
        raise CoverageError("; ".join(failures) + ".")
    return summary


def _fail(message: str) -> int:
    print(f"Coverage check failed: {message}", file=sys.stderr)
    if os.environ.get("GITHUB_ACTIONS") == "true":
        annotation = message.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")
        print(f"::error title=Coverage check failed::{annotation}", file=sys.stderr)
    return 1


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=Path, required=True, help="Cobertura XML report")
    parser.add_argument("--baseline", type=Path, required=True, help="JSON file with 'line' and 'branch' percentages")
    parser.add_argument("--package", help="Only measure this Cobertura package (for example an assembly name)")
    parser.add_argument(
        "--require-file", type=Path, action="append", default=[], help="Source file that must appear in the report"
    )
    args = parser.parse_args(argv)

    try:
        summary = check(args.report, args.baseline, args.package, args.require_file)
    except CoverageError as error:
        return _fail(str(error))
    print("\n".join(summary))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
