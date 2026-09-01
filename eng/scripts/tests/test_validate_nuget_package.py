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
EXPECTED_VALIDATION_RUN = (
    'python3 eng/scripts/validate-nuget-package.py --artifacts ./artifacts --tag "$RELEASE_TAG"'
)
EXPECTED_RELEASE_TAG_EXPRESSION = (
    "${{ github.event_name == 'push' && github.ref_name || '' }}"
)
VALIDATION_STEP = f"""      - name: Validate packed package
        env:
          RELEASE_TAG: {EXPECTED_RELEASE_TAG_EXPRESSION}
        run: {EXPECTED_VALIDATION_RUN}"""
KNOWN_PUBLISHING_STEPS = {"Push to NuGet", "Create GitHub Release"}
PUBLISHING_COMMANDS = (
    re.compile(r"\b(?:dotnet\s+)?nuget\s+push\b"),
    re.compile(r"\bgh\s+release\s+(?:create|edit|delete|upload)\b"),
)
PUBLISHING_ACTIONS = (
    "actions/create-release@",
    "alirezanet/publish-nuget@",
    "ncipollo/release-action@",
    "softprops/action-gh-release@",
    "svenstaro/upload-release-action@",
)


def parse_workflow_steps(workflow: str) -> list[tuple[str | None, str]]:
    steps_markers = list(re.finditer(r"(?m)^    steps:\s*$", workflow))
    if not steps_markers:
        raise AssertionError("Workflow must contain a steps section.")

    steps = []
    for steps_marker in steps_markers:
        section_start = steps_marker.end()
        section_end_match = re.search(r"(?m)^(?= {0,4}\S)", workflow[section_start:])
        section_end = (
            section_start + section_end_match.start()
            if section_end_match is not None
            else len(workflow)
        )
        section = workflow[section_start:section_end]
        entries = list(re.finditer(r"(?m)^      -(?:[ \t]+(?P<head>.*))?$", section))

        for index, entry in enumerate(entries):
            end = entries[index + 1].start() if index + 1 < len(entries) else len(section)
            body = section[entry.start():end]
            head = (entry.group("head") or "").strip()
            name = head.removeprefix("name:").strip() if head.startswith("name:") else None
            steps.append((name, body))
    return steps


def step_level_fields(body: str) -> list[tuple[str, str]]:
    fields = []
    for line in body.splitlines():
        match = re.match(
            r"^(?:      -[ \t]+|        )(?P<key>[A-Za-z][A-Za-z0-9-]*):(?P<value>.*)$",
            line,
        )
        if match is not None:
            fields.append((match.group("key"), match.group("value").strip()))
    return fields


def step_level_mapping(body: str, field_name: str) -> list[tuple[str, str]]:
    lines = body.splitlines()
    parent_indexes = [
        index
        for index, line in enumerate(lines)
        if re.match(rf"^        {re.escape(field_name)}:\s*$", line)
    ]
    if len(parent_indexes) != 1:
        raise AssertionError(
            f"Expected exactly one step-level {field_name!r} mapping."
        )

    entries = []
    for line in lines[parent_indexes[0] + 1 :]:
        if re.match(r"^        [A-Za-z][A-Za-z0-9-]*:", line):
            break
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        match = re.match(
            r"^          (?P<key>[A-Za-z_][A-Za-z0-9_]*):\s*(?P<value>.*)$",
            line,
        )
        if match is None:
            raise AssertionError(
                f"Could not safely parse step-level {field_name!r} mapping line: {line!r}"
            )
        entries.append((match.group("key"), match.group("value").strip()))
    return entries


def is_publishing_step(name: str | None, body: str) -> bool:
    uses_values = [
        value.lower().strip("'\"")
        for key, value in step_level_fields(body)
        if key == "uses"
    ]
    return (
        name in KNOWN_PUBLISHING_STEPS
        or any(pattern.search(body) for pattern in PUBLISHING_COMMANDS)
        or any(
            value.startswith(action)
            for value in uses_values
            for action in PUBLISHING_ACTIONS
        )
    )


def assert_publish_steps_guarded(workflow: str) -> list[tuple[str | None, str]]:
    steps = parse_workflow_steps(workflow)
    validation_indexes = [
        index for index, (name, _) in enumerate(steps) if name == "Validate packed package"
    ]
    if len(validation_indexes) != 1:
        raise AssertionError(
            "Expected exactly one 'Validate packed package' step before publishing steps."
        )

    guard_line = f"        {PUBLISH_GUARD}"
    validation_index = validation_indexes[0]
    validation_body = steps[validation_index][1]
    validation_runs = [
        value for key, value in step_level_fields(validation_body) if key == "run"
    ]
    if validation_runs != [EXPECTED_VALIDATION_RUN]:
        raise AssertionError(
            "'Validate packed package' must use exactly this run command: "
            f"{EXPECTED_VALIDATION_RUN}"
        )

    validation_env = step_level_mapping(validation_body, "env")
    expected_env = [("RELEASE_TAG", EXPECTED_RELEASE_TAG_EXPRESSION)]
    if validation_env != expected_env:
        raise AssertionError(
            "'Validate packed package' must have exactly this env entry: "
            f"RELEASE_TAG: {EXPECTED_RELEASE_TAG_EXPRESSION}"
        )

    validation_fields = {key for key, _ in step_level_fields(steps[validation_index][1])}
    forbidden_validation_fields = validation_fields & {"if", "continue-on-error"}
    if forbidden_validation_fields:
        fields = ", ".join(sorted(forbidden_validation_fields))
        raise AssertionError(
            "'Validate packed package' must be unconditional and fatal; "
            f"remove step-level key(s): {fields}."
        )

    for index, (name, body) in enumerate(steps):
        if not is_publishing_step(name, body):
            continue

        display_name = name or f"unnamed step #{index + 1}"
        if index < validation_index:
            raise AssertionError(
                f"Publishing step {display_name!r} appears before 'Validate packed package'."
            )
        if index > validation_index and body.splitlines().count(guard_line) != 1:
            raise AssertionError(
                f"Publishing step {display_name!r} must contain exactly this guard: {PUBLISH_GUARD}"
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

    def test_validation_precedes_artifact_upload_and_all_publishing_steps(self):
        validation_index = next(
            index
            for index, (name, _) in enumerate(self.steps)
            if name == "Validate packed package"
        )
        artifact_indexes = [
            index for index, (name, _) in enumerate(self.steps) if name == "Upload artifact"
        ]
        publishing_indexes = [
            index
            for index, (name, body) in enumerate(self.steps)
            if is_publishing_step(name, body)
        ]

        self.assertTrue(artifact_indexes)
        self.assertTrue(publishing_indexes)
        self.assertTrue(all(validation_index < index for index in artifact_indexes))
        self.assertTrue(all(validation_index < index for index in publishing_indexes))

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

    def test_conditional_validation_step_fails(self):
        mutated = self.workflow.replace(
            "      - name: Validate packed package\n",
            "      - name: Validate packed package\n        if: false\n",
            1,
        )

        with self.assertRaisesRegex(AssertionError, "unconditional and fatal.*if"):
            assert_publish_steps_guarded(mutated)

    def test_continue_on_error_validation_step_fails(self):
        mutated = self.workflow.replace(
            "      - name: Validate packed package\n",
            "      - name: Validate packed package\n        continue-on-error: true\n",
            1,
        )

        with self.assertRaisesRegex(
            AssertionError, "unconditional and fatal.*continue-on-error"
        ):
            assert_publish_steps_guarded(mutated)

    def test_empty_tag_argument_fails(self):
        mutated = self.workflow.replace(
            '--tag "$RELEASE_TAG"',
            '--tag ""',
            1,
        )

        with self.assertRaisesRegex(AssertionError, "must use exactly this run command"):
            assert_publish_steps_guarded(mutated)

    def test_empty_release_tag_environment_fails(self):
        mutated = self.workflow.replace(
            f"RELEASE_TAG: {EXPECTED_RELEASE_TAG_EXPRESSION}",
            "RELEASE_TAG: ''",
            1,
        )

        with self.assertRaisesRegex(AssertionError, "must have exactly this env entry"):
            assert_publish_steps_guarded(mutated)

    def test_validation_command_cannot_ignore_failure(self):
        mutated = self.workflow.replace(
            f"run: {EXPECTED_VALIDATION_RUN}",
            f"run: {EXPECTED_VALIDATION_RUN} || true",
            1,
        )

        with self.assertRaisesRegex(AssertionError, "must use exactly this run command"):
            assert_publish_steps_guarded(mutated)

    def test_validation_command_cannot_be_replaced(self):
        mutated = self.workflow.replace(
            f"run: {EXPECTED_VALIDATION_RUN}",
            "run: echo skip",
            1,
        )

        with self.assertRaisesRegex(AssertionError, "must use exactly this run command"):
            assert_publish_steps_guarded(mutated)

    def test_validation_run_content_cannot_impersonate_step_level_keys(self):
        workflow = """jobs:
  publish:
    steps:
      - name: Validate packed package
        run: |
          if: false
          continue-on-error: true
"""

        validation_body = parse_workflow_steps(workflow)[0][1]
        self.assertEqual(
            ["name", "run"],
            [key for key, _ in step_level_fields(validation_body)],
        )

    def test_unguarded_nuget_push_before_artifact_upload_fails(self):
        workflow = f"""jobs:
  publish:
    steps:
{VALIDATION_STEP}
      - name: Early package publication
        run: dotnet nuget push ./artifacts/*.nupkg
      - name: Upload artifact
        uses: actions/upload-artifact@sha
"""

        with self.assertRaisesRegex(AssertionError, "Early package publication"):
            assert_publish_steps_guarded(workflow)

    def test_unguarded_nuget_push_before_validation_fails_with_ordering_error(self):
        workflow = f"""jobs:
  publish:
    steps:
      - run: dotnet nuget push ./artifacts/*.nupkg
{VALIDATION_STEP}
"""

        with self.assertRaisesRegex(
            AssertionError, "unnamed step #1.*appears before 'Validate packed package'"
        ):
            assert_publish_steps_guarded(workflow)

    def test_unnamed_unguarded_push_after_guarded_step_fails(self):
        workflow = f"""jobs:
  publish:
    steps:
{VALIDATION_STEP}
      - name: Push to NuGet
        {PUBLISH_GUARD}
        run: dotnet nuget push ./artifacts/package.nupkg
      - run: dotnet nuget push ./artifacts/other.nupkg
"""

        steps = parse_workflow_steps(workflow)
        self.assertEqual(
            ["Validate packed package", "Push to NuGet", None],
            [name for name, _ in steps],
        )
        with self.assertRaisesRegex(AssertionError, "unnamed step #3"):
            assert_publish_steps_guarded(workflow)

    def test_unnamed_release_action_with_extra_spacing_requires_guard(self):
        workflow = f"""jobs:
  publish:
    steps:
{VALIDATION_STEP}
      - uses:  softprops/action-gh-release@sha
"""

        with self.assertRaisesRegex(AssertionError, "unnamed step #2"):
            assert_publish_steps_guarded(workflow)

    def test_duplicate_name_unguarded_release_step_fails(self):
        workflow = f"""jobs:
  publish:
    steps:
{VALIDATION_STEP}
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

    def test_harmless_named_and_unnamed_steps_do_not_require_publish_guard(self):
        workflow = f"""jobs:
  publish:
    steps:
      - run: echo "Preparing validation"
{VALIDATION_STEP}
      - uses: actions/upload-artifact@sha
      - name: Write job summary
        run: echo "Validation complete" >> "$GITHUB_STEP_SUMMARY"
      - run: echo "Done"
"""

        assert_publish_steps_guarded(workflow)


if __name__ == "__main__":
    unittest.main()
