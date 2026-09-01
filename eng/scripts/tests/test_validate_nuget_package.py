# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.
from __future__ import annotations

import contextlib
import importlib.util
import io
import re
import shutil
import sys
import unittest
import uuid
import zipfile
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[3]
SCRIPT_PATH = REPO_ROOT / "eng" / "scripts" / "validate-nuget-package.py"
PUBLISH_GUARD = "if: github.event_name == 'push' && github.ref_type == 'tag'"
KNOWN_PUBLISHING_STEPS = {"Push to NuGet", "Create GitHub Release"}
PUBLISHING_COMMANDS = (
    re.compile(r"\bdotnet\s+nuget\s+push\b"),
    re.compile(r"\bgh\s+release\s+(?:create|edit|delete|upload)\b"),
)


def parse_workflow_steps(workflow: str) -> list[tuple[str, str]]:
    steps = []
    for step in workflow.split("      - name: ")[1:]:
        name, body = step.split("\n", 1)
        steps.append((name, body))
    return steps


def assert_publish_steps_guarded(workflow: str) -> list[tuple[str, str]]:
    steps = parse_workflow_steps(workflow)
    validation_indexes = [
        index for index, (name, _) in enumerate(steps) if name == "Validate packed package"
    ]
    if len(validation_indexes) != 1:
        raise AssertionError(
            "Expected exactly one 'Validate packed package' step before publishing steps."
        )

    guard_line = f"        {PUBLISH_GUARD}"
    for name, body in steps[validation_indexes[0] + 1 :]:
        is_publishing = name in KNOWN_PUBLISHING_STEPS or any(
            pattern.search(body) for pattern in PUBLISHING_COMMANDS
        )
        if is_publishing and body.splitlines().count(guard_line) != 1:
            raise AssertionError(
                f"Publishing step {name!r} must contain exactly this guard: {PUBLISH_GUARD}"
            )
    return steps


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
        cls.steps = parse_workflow_steps(cls.workflow)

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
        steps = assert_publish_steps_guarded(self.workflow)

        for step_name in ("Push to NuGet", "Create GitHub Release"):
            with self.subTest(step=step_name):
                matching_steps = [body for name, body in steps if name == step_name]
                self.assertTrue(matching_steps)
                for body in matching_steps:
                    self.assertEqual(
                        1, body.splitlines().count(f"        {PUBLISH_GUARD}")
                    )

    def test_unguarded_nuget_push_before_artifact_upload_fails(self):
        workflow = """jobs:
  publish:
    steps:
      - name: Validate packed package
        run: validate
      - name: Early package publication
        run: dotnet nuget push ./artifacts/*.nupkg
      - name: Upload artifact
        uses: actions/upload-artifact@sha
"""

        with self.assertRaisesRegex(AssertionError, "Early package publication"):
            assert_publish_steps_guarded(workflow)

    def test_duplicate_name_unguarded_release_step_fails(self):
        workflow = f"""jobs:
  publish:
    steps:
      - name: Validate packed package
        run: validate
      - name: Create GitHub Release
        {PUBLISH_GUARD}
        run: gh release create v1.2.3
      - name: Create GitHub Release
        run: gh release create v1.2.4
"""

        steps = parse_workflow_steps(workflow)
        self.assertEqual(
            ["Validate packed package", "Create GitHub Release", "Create GitHub Release"],
            [name for name, _ in steps],
        )
        with self.assertRaisesRegex(AssertionError, "Create GitHub Release"):
            assert_publish_steps_guarded(workflow)

    def test_harmless_post_validation_steps_do_not_require_publish_guard(self):
        workflow = """jobs:
  publish:
    steps:
      - name: Validate packed package
        run: validate
      - name: Upload artifact
        uses: actions/upload-artifact@sha
      - name: Write job summary
        run: echo "Validation complete" >> "$GITHUB_STEP_SUMMARY"
"""

        assert_publish_steps_guarded(workflow)


if __name__ == "__main__":
    unittest.main()
