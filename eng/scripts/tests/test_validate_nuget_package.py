# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.
from __future__ import annotations

import contextlib
import importlib.util
import io
import shutil
import sys
import unittest
import uuid
import zipfile
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[3]
SCRIPT_PATH = REPO_ROOT / "eng" / "scripts" / "validate-nuget-package.py"


def _load_script():
    spec = importlib.util.spec_from_file_location("validate_nuget_package", SCRIPT_PATH)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


script = _load_script()


class NuGetPackageValidationTests(unittest.TestCase):
    def setUp(self) -> None:
        self.artifacts = (
            REPO_ROOT / "artifacts" / "test-validate-nuget-package" / uuid.uuid4().hex
        )
        self.artifacts.mkdir(parents=True)

    def tearDown(self) -> None:
        shutil.rmtree(self.artifacts)
        try:
            self.artifacts.parent.rmdir()
        except OSError:
            pass

    def create_package(
        self,
        *,
        package_id: str = "Aspire.Hosting.DocumentDB",
        version: str = "1.2.3",
        filename: str | None = None,
    ) -> Path:
        package = self.artifacts / (filename or f"{package_id}.{version}.nupkg")
        nuspec = f"""<?xml version="1.0"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>{package_id}</id>
    <version>{version}</version>
  </metadata>
</package>
"""
        with zipfile.ZipFile(package, "w") as archive:
            archive.writestr(f"{package_id}.nuspec", nuspec)
        return package

    def test_accepts_package_style_tag_matching_nuspec_version(self):
        package = self.create_package()

        actual = script.validate_package(self.artifacts, "v1.2.3")

        self.assertEqual((package, "Aspire.Hosting.DocumentDB", "1.2.3"), actual)

    def test_rejects_upstream_style_tag(self):
        self.create_package(version="0.0.0-alpha.1")

        with self.assertRaisesRegex(
            script.ValidationError, "require exactly 'vMAJOR.MINOR.PATCH'"
        ):
            script.validate_package(self.artifacts, "v0.116-0")

    def test_rejects_prerelease_and_noncanonical_tags(self):
        invalid_tags = ("v1.2.3-alpha.1", "v1.2", "1.2.3", "v01.2.3")

        for tag in invalid_tags:
            with self.subTest(tag=tag), self.assertRaises(script.ValidationError):
                script.parse_package_tag(tag)

    def test_rejects_package_version_that_differs_from_tag(self):
        self.create_package(version="1.2.4")

        with self.assertRaisesRegex(
            script.ValidationError,
            "Package version '1.2.4' does not exactly match release tag 'v1.2.3'",
        ):
            script.validate_package(self.artifacts, "v1.2.3")

    def test_rejects_ambiguous_package_set(self):
        self.create_package(package_id="First")
        self.create_package(package_id="Second")

        with self.assertRaisesRegex(script.ValidationError, "found 2"):
            script.validate_package(self.artifacts, "v1.2.3")

    def test_rejects_filename_that_disagrees_with_nuspec(self):
        self.create_package(filename="unexpected.nupkg")

        with self.assertRaisesRegex(script.ValidationError, "does not match its .nuspec identity"):
            script.validate_package(self.artifacts)

    def test_empty_tag_is_a_successful_non_publishing_validation(self):
        self.create_package(version="0.0.0-alpha.1")
        stdout = io.StringIO()

        with contextlib.redirect_stdout(stdout):
            exit_code = script.main(["--artifacts", str(self.artifacts), "--tag", ""])

        self.assertEqual(0, exit_code)
        self.assertIn("publishing remains disabled", stdout.getvalue())


class NuGetPublishWorkflowTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.workflow = (REPO_ROOT / ".github" / "workflows" / "nuget-publish.yml").read_text(
            encoding="utf-8"
        )
        cls.step_names = []
        cls.steps = {}
        for step in cls.workflow.split("      - name: ")[1:]:
            name, body = step.split("\n", 1)
            cls.step_names.append(name)
            cls.steps[name] = body

    def test_tag_trigger_only_matches_package_style_tags(self):
        self.assertIn("- 'v[0-9]+.[0-9]+.[0-9]+'", self.workflow)
        self.assertNotIn("- 'v*'", self.workflow)

    def test_validation_precedes_every_upload(self):
        validation = self.workflow.index("- name: Validate packed package")
        artifact_upload = self.workflow.index("- name: Upload artifact")
        nuget_push = self.workflow.index("- name: Push to NuGet")

        self.assertLess(validation, artifact_upload)
        self.assertLess(validation, nuget_push)

    def test_manual_dispatch_cannot_publish_or_create_a_release(self):
        publish_guard = "if: github.event_name == 'push' && github.ref_type == 'tag'"
        guard_line = f"        {publish_guard}"

        for step_name in ("Push to NuGet", "Create GitHub Release"):
            with self.subTest(step=step_name):
                self.assertIn(step_name, self.steps)
                self.assertEqual(1, self.steps[step_name].splitlines().count(guard_line))

        upload_index = self.step_names.index("Upload artifact")
        for step_name in self.step_names[upload_index + 1 :]:
            with self.subTest(release_step=step_name):
                self.assertEqual(1, self.steps[step_name].splitlines().count(guard_line))


if __name__ == "__main__":
    unittest.main()
