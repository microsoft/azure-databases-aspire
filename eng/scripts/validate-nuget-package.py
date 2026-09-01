#!/usr/bin/env python3
"""Validate a packed NuGet package before publishing it."""
from __future__ import annotations

import argparse
import os
import re
import sys
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path, PurePosixPath

PACKAGE_TAG = re.compile(r"v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\Z")


class ValidationError(Exception):
    pass


def parse_package_tag(tag: str) -> str:
    match = PACKAGE_TAG.fullmatch(tag)
    if match is None:
        raise ValidationError(
            f"Invalid release tag {tag!r}. NuGet releases require exactly "
            "'vMAJOR.MINOR.PATCH' with numeric components and no prerelease or build suffix "
            "(for example, 'v1.2.3')."
        )
    return tag[1:]


def _element_text(parent: ET.Element, local_name: str) -> str | None:
    for element in parent.iter():
        if element.tag.rsplit("}", 1)[-1] == local_name and element.text:
            return element.text.strip()
    return None


def read_package_identity(package: Path) -> tuple[str, str]:
    try:
        with zipfile.ZipFile(package) as archive:
            corrupt_entry = archive.testzip()
            if corrupt_entry is not None:
                raise ValidationError(
                    f"Package {package} is corrupt; the first unreadable entry is {corrupt_entry!r}."
                )

            nuspec_entries = [
                name
                for name in archive.namelist()
                if PurePosixPath(name).parent == PurePosixPath(".")
                and name.lower().endswith(".nuspec")
            ]
            if len(nuspec_entries) != 1:
                raise ValidationError(
                    f"Package {package} must contain exactly one root-level .nuspec file; "
                    f"found {len(nuspec_entries)}."
                )

            try:
                root = ET.fromstring(archive.read(nuspec_entries[0]))
            except ET.ParseError as error:
                raise ValidationError(
                    f"Package {package} contains an invalid .nuspec XML file: {error}."
                ) from error
    except zipfile.BadZipFile as error:
        raise ValidationError(f"Package {package} is not a valid NuGet ZIP archive.") from error

    package_id = _element_text(root, "id")
    version = _element_text(root, "version")
    if not package_id or not version:
        raise ValidationError(f"Package {package} .nuspec must contain non-empty id and version values.")
    return package_id, version


def validate_package(artifacts: Path, tag: str | None = None) -> tuple[Path, str, str]:
    packages = sorted(
        package
        for package in artifacts.glob("*.nupkg")
        if not package.name.lower().endswith(".snupkg")
    )
    if len(packages) != 1:
        raise ValidationError(
            f"Expected exactly one .nupkg in {artifacts}; found {len(packages)}. "
            "Publishing multiple or ambiguous packages is not allowed."
        )

    package = packages[0]
    package_id, package_version = read_package_identity(package)
    expected_filename = f"{package_id}.{package_version}.nupkg"
    if package.name != expected_filename:
        raise ValidationError(
            f"Package filename {package.name!r} does not match its .nuspec identity "
            f"{expected_filename!r}."
        )

    if tag:
        tag_version = parse_package_tag(tag)
        if package_version != tag_version:
            raise ValidationError(
                f"Package version {package_version!r} does not exactly match release tag "
                f"{tag!r} (expected {tag_version!r}). Refusing to publish."
            )

    return package, package_id, package_version


def _fail(message: str) -> int:
    print(f"NuGet publish validation failed: {message}", file=sys.stderr)
    if os.environ.get("GITHUB_ACTIONS") == "true":
        annotation = message.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")
        print(f"::error title=NuGet publish validation failed::{annotation}", file=sys.stderr)
    return 1


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--artifacts",
        type=Path,
        required=True,
        help="Directory containing exactly one packed .nupkg",
    )
    parser.add_argument(
        "--tag",
        default="",
        help="Release tag to require, or empty for non-publishing validation",
    )
    args = parser.parse_args(argv)

    try:
        package, package_id, version = validate_package(args.artifacts, args.tag or None)
    except ValidationError as error:
        return _fail(str(error))

    if args.tag:
        print(f"Validated {package.name}: {package_id} version {version} exactly matches {args.tag}.")
    else:
        print(
            f"Validated {package.name}: {package_id} version {version}. "
            "No release tag was supplied; publishing remains disabled."
        )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
