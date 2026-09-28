#!/usr/bin/env python3
# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.
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
REPO_ROOT = Path(__file__).resolve().parents[2]
MSBUILD_PROPERTY = re.compile(r"\$\(([A-Za-z_][A-Za-z0-9_]*)\)")


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


def _local(element: ET.Element) -> str:
    return element.tag.rsplit("}", 1)[-1]


def _msbuild_property(project: Path, name: str, depth: int = 0) -> str | None:
    """Reads a property from the project or the nearest Directory.Build.props; enough for plain values."""
    candidates = [project]
    for directory in project.parents:
        props = directory / "Directory.Build.props"
        if props.is_file():
            candidates.append(props)
            break
    for candidate in candidates:
        for element in ET.parse(candidate).getroot().iter():
            if _local(element) == name and element.text and element.text.strip():
                value = element.text.strip()
                if depth < 5:
                    value = MSBUILD_PROPERTY.sub(
                        lambda m: _msbuild_property(project, m.group(1), depth + 1) or m.group(0), value
                    )
                return value
    return None


def read_project_expectations(project: Path) -> dict:
    """What the packed package must contain, derived from the project file itself."""
    if not project.is_file():
        raise ValidationError(f"Project file {project} does not exist.")

    root = ET.parse(project).getroot()
    public, private = set(), set()
    root_files = set()
    for element in root.iter():
        if _local(element) == "PackageReference" and element.get("Include"):
            private_assets = element.get("PrivateAssets") or _element_text(element, "PrivateAssets") or ""
            (private if private_assets.lower() == "all" else public).add(element.get("Include"))
        elif (
            _local(element) == "None"
            and (element.get("Pack") or "").lower() == "true"
            and element.get("PackagePath") in ("\\", "/", "")
            and "*" not in (element.get("Include") or "*")
        ):
            root_files.add(re.split(r"[\\/)]", element.get("Include"))[-1])

    return {
        "tfm": _msbuild_property(project, "TargetFramework"),
        "license": _msbuild_property(project, "PackageLicenseExpression"),
        "icon": _msbuild_property(project, "PackageIcon"),
        "readme": _msbuild_property(project, "PackageReadmeFile"),
        "root_files": root_files,
        "public_dependencies": public,
        "private_dependencies": private,
    }


def validate_package_contents(package: Path, package_id: str, project: Path) -> None:
    expected = read_project_expectations(project)
    tfm = expected["tfm"]
    if not tfm or "$(" in tfm:
        raise ValidationError(f"Could not resolve TargetFramework from {project}.")

    with zipfile.ZipFile(package) as archive:
        names = set(archive.namelist())
        nuspec_name = next(n for n in names if "/" not in n and n.lower().endswith(".nuspec"))
        nuspec = ET.fromstring(archive.read(nuspec_name))
    problems = []

    lib_folders = {PurePosixPath(name).parts[1] for name in names if name.startswith("lib/")}
    if lib_folders != {tfm}:
        problems.append(f"lib/ must contain exactly {tfm!r}; found {sorted(lib_folders)}.")
    for extension in ("dll", "xml"):
        if f"lib/{tfm}/{package_id}.{extension}" not in names:
            problems.append(f"missing lib/{tfm}/{package_id}.{extension}.")

    for name in sorted(expected["root_files"]):
        if name not in names:
            problems.append(f"missing packed file {name!r}.")

    for element_name in ("icon", "readme"):
        value = _element_text(nuspec, element_name)
        if value != expected[element_name]:
            problems.append(f"nuspec {element_name} is {value!r}, expected {expected[element_name]!r}.")
        elif value not in names:
            problems.append(f"nuspec {element_name} {value!r} is not in the package.")

    license_element = next((e for e in nuspec.iter() if _local(e) == "license"), None)
    if (
        license_element is None
        or license_element.get("type") != "expression"
        or (license_element.text or "").strip() != expected["license"]
    ):
        problems.append(f"nuspec license must be the expression {expected['license']!r}.")

    groups = [e for e in nuspec.iter() if _local(e) == "group"]
    if [g.get("targetFramework") for g in groups] != [tfm]:
        problems.append(f"nuspec must have exactly one dependency group for {tfm!r}.")
    dependencies = {d.get("id") for g in groups for d in g if _local(d) == "dependency"}
    for missing in sorted(expected["public_dependencies"] - dependencies):
        problems.append(f"dependency {missing!r} is missing.")
    for leaked in sorted(expected["private_dependencies"] & dependencies):
        problems.append(f"private dependency {leaked!r} must not ship.")

    if problems:
        raise ValidationError(f"Package {package.name} contents are wrong: " + " ".join(problems))


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
    parser.add_argument(
        "--project",
        type=Path,
        help="Project the package was packed from (default: src/<package id>/<package id>.csproj)",
    )
    args = parser.parse_args(argv)

    try:
        package, package_id, version = validate_package(args.artifacts, args.tag or None)
        project = args.project or REPO_ROOT / "src" / package_id / f"{package_id}.csproj"
        validate_package_contents(package, package_id, project)
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
