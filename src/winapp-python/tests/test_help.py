# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

from contextlib import redirect_stderr, redirect_stdout
import io
from pathlib import Path
from unittest.mock import patch

from winapp_cli.cli import main
from winapp_cli.errors import WinappError

from helpers import ProjectTest


class HelpTests(ProjectTest):
    def setUp(self):
        super().setUp()
        self.stdout = io.StringIO()
        self.stderr = io.StringIO()
        self.native = Path("winapp.exe")
        (self.project / "pyproject.toml").write_text("invalid project metadata", encoding="utf-8")
        for patcher in (
            patch("winapp_cli.cli.Path.cwd", return_value=self.project),
            patch("winapp_cli.cli.find_native_cli", return_value=self.native),
        ):
            patcher.start()
            self.addCleanup(patcher.stop)

    def forward_help(self, executable, arguments, directory):
        print(f"native help: {' '.join(arguments)}")
        return 0

    def invoke_help(self, arguments, expected_code=0):
        self.stdout.seek(0)
        self.stdout.truncate()
        with redirect_stdout(self.stdout), redirect_stderr(self.stderr):
            with self.assertRaises(SystemExit) as result:
                main(arguments)
        self.assertEqual(result.exception.code, expected_code)
        return self.stdout.getvalue()

    def test_root_help_forwards_native_then_lists_python_workflows(self):
        for flag in ("--help", "-h", "help"):
            with self.subTest(flag=flag):
                with (
                    patch("winapp_cli.cli.forward_native", side_effect=self.forward_help) as forward,
                    patch("winapp_cli.cli._prepare_codegen") as codegen,
                    patch("winapp_cli.cli.clear_identity") as clear,
                ):
                    text = self.invoke_help([flag])
                forward.assert_called_once_with(self.native, ["--help"], self.project)
                self.assertLess(text.index("native help:"), text.index("Python extensions:"))
                self.assertIn("winapp-py", text)
                for name in ("generate", "identity", "restore", "update", "run"):
                    self.assertIn(name, text)
                codegen.assert_not_called()
                clear.assert_not_called()
                self.assertFalse((self.project / ".venv").exists())

    def test_no_arguments_show_combined_help_with_existing_usage_error_code(self):
        with (
            patch("winapp_cli.cli.forward_native", side_effect=self.forward_help),
            redirect_stdout(self.stdout), redirect_stderr(self.stderr),
        ):
            self.assertEqual(main([]), 2)
        self.assertIn("native help:", self.stdout.getvalue())
        self.assertIn("Python extensions:", self.stdout.getvalue())

    def test_shared_command_help_appends_the_effective_python_syntax(self):
        for command, option in (("restore", "--python"), ("update", "--channel"), ("run", "--identity")):
            with self.subTest(command=command):
                with patch("winapp_cli.cli.forward_native", side_effect=self.forward_help) as forward:
                    text = self.invoke_help([command, "--help"])
                forward.assert_called_once_with(self.native, [command, "--help"], self.project)
                self.assertLess(text.index("native help:"), text.index(f"Python {command} workflow:"))
                self.assertIn(f"winapp-py {command}", text)
                self.assertIn(option, text)

    def test_native_selection_prefix_is_respected_by_help(self):
        for arguments in (
            ["--winapp", "chosen.exe", "--help"],
            ["--winapp=chosen.exe", "restore", "--help"],
            ["update", "--winapp", "chosen.exe", "--help"],
        ):
            with self.subTest(arguments=arguments):
                with (
                    patch("winapp_cli.cli.find_native_cli", return_value=self.native) as locate,
                    patch("winapp_cli.cli.forward_native", side_effect=self.forward_help),
                ):
                    self.invoke_help(arguments)
                self.assertEqual(locate.call_args.args[0], "chosen.exe")
                locate.assert_called_once_with("chosen.exe")

    def test_run_help_distinguishes_existing_venv_identity_and_native_inputs(self):
        with patch("winapp_cli.cli.forward_native", side_effect=self.forward_help):
            text = self.invoke_help(["run", "--help"])
        self.assertIn("project .venv", text)
        self.assertIn("private development environment", text)
        self.assertIn("Other inputs use the native syntax", text)
        self.assertNotIn("[--help] --identity", text)

    def test_native_help_failure_is_not_hidden_by_python_help(self):
        for command in ([], ["restore"], ["run"]):
            with self.subTest(command=command):
                with patch("winapp_cli.cli.forward_native", return_value=37):
                    text = self.invoke_help([*command, "--help"], expected_code=37)
                self.assertNotIn("Python extensions:", text)
                self.assertNotIn("Python restore workflow:", text)
                self.assertNotIn("Python run workflow:", text)

    def test_missing_native_cli_reports_an_error_instead_of_combined_success(self):
        with (
            patch("winapp_cli.cli.find_native_cli", side_effect=WinappError("Native CLI is missing")),
            redirect_stdout(self.stdout), redirect_stderr(self.stderr),
        ):
            self.assertEqual(main(["--help"]), 1)
        self.assertIn("Native CLI is missing", self.stderr.getvalue())
        self.assertNotIn("Python extensions:", self.stdout.getvalue())

    def test_python_only_help_does_not_need_native_cli_or_project_configuration(self):
        for arguments in (["generate", "--help"], ["identity", "--help"], ["identity", "clear", "--help"]):
            with self.subTest(arguments=arguments):
                with patch("winapp_cli.cli.find_native_cli") as locate:
                    text = self.invoke_help(arguments)
                locate.assert_not_called()
                self.assertIn(f"winapp-py {' '.join(arguments[:-1])}", text)
                self.assertNotIn("native help:", text)

    def test_generic_native_help_is_forwarded_without_python_supplement(self):
        with patch("winapp_cli.cli.forward_native", side_effect=self.forward_help) as forward:
            with redirect_stdout(self.stdout), redirect_stderr(self.stderr):
                self.assertEqual(main(["manifest", "--help"]), 0)
        forward.assert_called_once_with(self.native, ["manifest", "--help"], self.project)
        self.assertNotIn("Python extensions:", self.stdout.getvalue())

    def test_help_after_identity_script_remains_an_application_argument(self):
        for suffix in (["--help"], ["--", "--help"]):
            with self.subTest(suffix=suffix):
                with (
                    patch("winapp_cli.cli._require_supported_python"),
                    patch("winapp_cli.cli.run_with_identity", return_value=37) as launch,
                    patch("winapp_cli.cli.forward_native") as forward,
                    redirect_stdout(self.stdout), redirect_stderr(self.stderr),
                ):
                    self.assertEqual(main(["run", "--identity", "app.py", *suffix]), 37)
                self.assertEqual(launch.call_args.args[2], ["--help"])
                forward.assert_not_called()

    def test_version_displays_package_and_native_versions_without_project_setup(self):
        with (
            patch("winapp_cli.cli.version", return_value="0.7.2.dev37"),
            patch("winapp_cli.cli.forward_native", side_effect=lambda *a: print("0.7.2-prerelease.37") or 0) as forward,
            patch("winapp_cli.cli._prepare_codegen") as codegen,
        ):
            text = self.invoke_help(["--version"])
        self.assertEqual(text.strip().splitlines(), [
            "Python Package: winapp-cli v0.7.2.dev37", "Native CLI:", "0.7.2-prerelease.37",
        ])
        forward.assert_called_once_with(self.native, ["--version"], self.project)
        codegen.assert_not_called()
        self.assertFalse((self.project / ".venv").exists())

    def test_version_respects_native_override_and_propagates_failure(self):
        with (
            patch("winapp_cli.cli.find_native_cli", return_value=self.native) as locate,
            patch("winapp_cli.cli.forward_native", return_value=37),
        ):
            self.invoke_help(["--winapp", "chosen.exe", "--version"], expected_code=37)
        locate.assert_called_once_with("chosen.exe")

    def test_missing_native_version_is_reported_as_an_error(self):
        with (
            patch("winapp_cli.cli.find_native_cli", side_effect=WinappError("Native CLI is missing")),
            redirect_stdout(self.stdout), redirect_stderr(self.stderr),
        ):
            self.assertEqual(main(["--version"]), 1)
        self.assertIn("Python Package: winapp-cli", self.stdout.getvalue())
        self.assertIn("Native CLI is missing", self.stderr.getvalue())

    def test_completion_remains_native_forwarding(self):
        with patch("winapp_cli.cli.forward_native", return_value=0) as forward:
            arguments = ["complete", "--commandline", "winapp-py ge", "--position", "12"]
            self.assertEqual(main(arguments), 0)
        self.assertEqual(forward.call_args.args[1], arguments)
