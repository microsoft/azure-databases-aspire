#!/usr/bin/env python3
# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.
"""Extract a release's notes from CHANGELOG.md, failing when its section is missing or empty."""
from __future__ import annotations

import argparse
import os
import re
import sys
from pathlib import Path

NEXT_HEADING = re.compile(r"^## \[")


class ReleaseNotesError(Exception):
    pass


def extract_release_notes(changelog: str, tag: str) -> str:
    version = tag.removeprefix("v")
    heading = re.compile(rf"^## \[{re.escape(version)}\](?: - .*)?$")
    lines = changelog.splitlines()

    start = next((index + 1 for index, line in enumerate(lines) if heading.match(line)), None)
    if start is None:
        raise ReleaseNotesError(
            f"CHANGELOG.md has no '## [{version}]' heading. Move the release notes under it "
            "before tagging."
        )

    notes = []
    for line in lines[start:]:
        if NEXT_HEADING.match(line):
            break
        notes.append(line)

    notes_text = "\n".join(notes).strip()
    if not notes_text:
        raise ReleaseNotesError(f"CHANGELOG.md section '## [{version}]' is empty.")
    return notes_text


def _fail(message: str) -> int:
    print(f"Release notes extraction failed: {message}", file=sys.stderr)
    if os.environ.get("GITHUB_ACTIONS") == "true":
        annotation = message.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")
        print(f"::error title=Release notes extraction failed::{annotation}", file=sys.stderr)
    return 1


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--changelog", type=Path, required=True, help="Path to CHANGELOG.md")
    parser.add_argument("--tag", required=True, help="Release tag, for example v1.2.3")
    parser.add_argument("--output", type=Path, required=True, help="File to write the notes to")
    args = parser.parse_args(argv)

    try:
        notes = extract_release_notes(args.changelog.read_text(encoding="utf-8"), args.tag)
    except ReleaseNotesError as error:
        return _fail(str(error))

    args.output.write_text(notes + "\n", encoding="utf-8")
    print(f"Wrote release notes for {args.tag} to {args.output}.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
