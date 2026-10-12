# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

from contextlib import redirect_stderr, redirect_stdout
import io
import json
import os
from pathlib import Path
from unittest.mock import patch

from winapp_cli.cli import main
from winapp_cli.errors import WinappError

from helpers import ProjectTest


class NativeInitTests(ProjectTest):
    def setUp(self):
        super().setUp()
        self.stdout = io.StringIO()
        self.stderr = io.StringIO()
        for patcher in (
            patch("winapp_cli.cli.Path.cwd", return_value=self.project),
            patch("winapp_cli.cli.find_native_cli", return_value=Path("winapp.exe")),
            patch("winapp_cli.cli.sys.platform", "win32"),
            patch("winapp_cli.cli.platform.python_implementation", return_value="CPython"),
            patch("winapp_cli.cli.platform.machine", return_value="AMD64"),
            patch("winapp_cli.cli.sys.stdin.isatty", return_value=False),
            patch.dict(os.environ, {"CI": ""}),
        ):
            patcher.start()
            self.addCleanup(patcher.stop)

    def context(self, *, project=None, mode="sdk", config=None, metadata=None):
        project = self.project if project is None else project
        return {
            "schema": 1, "project": str(project), "mode": mode,
            "config_directory": str(project if config is None else config),
            "metadata_directory": str(project / ".winapp" if metadata is None else metadata),
        }

    def native(self, executable, arguments, working_directory, *, init_result):
        init_result.write_text(json.dumps(self.context()), encoding="utf-8")
        return 0

    def invoke(self, *arguments):
        with redirect_stdout(self.stdout), redirect_stderr(self.stderr):
            return main(["init", *arguments])

    def unconfigured(self):
        (self.project / "pyproject.toml").write_text('[project]\nname = "Example-App"\n', encoding="utf-8")

    def test_bare_init_preserves_native_argv_and_existing_config_without_prompt(self):
        with (
            patch("winapp_cli.cli.forward_native", side_effect=self.native) as native,
            patch("winapp_cli.cli._configure_after_init") as setup,
            patch("builtins.input") as prompt,
        ):
            self.assertEqual(self.invoke(), 0)
        self.assertEqual(native.call_args.args[:3], (Path("winapp.exe"), ["init"], self.project))
        setup.assert_called_once_with(self.project, reset=False, config_only=False, verbose=False)
        prompt.assert_not_called()

    def test_noninteractive_new_project_requires_explicit_opt_in(self):
        for flags in ([], ["--use-defaults"], ["--no-prompt"]):
            with self.subTest(flags=flags):
                self.unconfigured()
                before = (self.project / "pyproject.toml").read_bytes()
                with (
                    patch("winapp_cli.cli.forward_native", side_effect=self.native),
                    patch("winapp_cli.cli._configure_after_init") as setup,
                ):
                    self.assertEqual(self.invoke(*flags), 0)
                setup.assert_not_called()
                self.assertEqual((self.project / "pyproject.toml").read_bytes(), before)

    def test_interactive_new_project_confirmation_defaults_to_yes_and_retries_invalid_answers(self):
        self.unconfigured()
        with (
            patch("winapp_cli.cli.sys.stdin.isatty", return_value=True),
            patch("winapp_cli.cli.forward_native", side_effect=self.native),
            patch("builtins.input", side_effect=["invalid", ""]) as prompt,
            patch("winapp_cli.cli._configure_after_init") as setup,
        ):
            self.assertEqual(self.invoke(), 0)
        self.assertEqual(prompt.call_count, 2)
        setup.assert_called_once()

    def test_interactive_no_makes_no_bindings_changes(self):
        self.unconfigured()
        with (
            patch("winapp_cli.cli.sys.stdin.isatty", return_value=True),
            patch("winapp_cli.cli.forward_native", side_effect=self.native),
            patch("builtins.input", return_value="no"),
            patch("winapp_cli.cli._configure_after_init") as setup,
        ):
            self.assertEqual(self.invoke(), 0)
        setup.assert_not_called()

    def test_explicit_opt_in_only_strips_its_own_flag(self):
        arguments = [
            str(self.project), "--setup-sdks", "preview", "--use-defaults",
            "--config-dir=" + str(self.project), "--no-gitignore", "--verbose",
        ]
        with (
            patch("winapp_cli.cli.forward_native", side_effect=self.native) as native,
            patch("winapp_cli.cli._configure_after_init") as setup,
        ):
            self.assertEqual(self.invoke(*arguments, "--add-python-bindings"), 0)
        self.assertEqual(native.call_args.args[1], ["init", *arguments])
        setup.assert_called_once_with(self.project, reset=False, config_only=False, verbose=True)

    def test_explicit_opt_in_requires_noninteractive_setup_and_rejects_no_sdk_modes(self):
        for arguments in (["--sparse"], ["--setup-sdks", "none"], ["--setup-sdks=none"], []):
            with self.subTest(arguments=arguments):
                with (
                    patch("winapp_cli.cli.sys.stdin.isatty", return_value=True),
                    patch("winapp_cli.cli.forward_native") as native,
                ):
                    self.assertEqual(self.invoke(*arguments, "--add-python-bindings"), 1)
                native.assert_not_called()

    def test_existing_defaults_reset_metadata_but_explicit_opt_in_preserves_it(self):
        for flags, reset in ((["--use-defaults"], True), (["--use-defaults", "--add-python-bindings"], False)):
            with self.subTest(flags=flags):
                with (
                    patch("winapp_cli.cli.forward_native", side_effect=self.native),
                    patch("winapp_cli.cli._configure_after_init") as setup,
                ):
                    self.assertEqual(self.invoke(*flags), 0)
                self.assertEqual(setup.call_args.kwargs["reset"], reset)

    def test_explicit_false_preserves_interactive_reset_confirmation(self):
        for flags in (
            ["--use-defaults", "false"], ["--no-prompt", "FALSE"],
            ["--use-defaults=false"], ["--no-prompt=false"],
        ):
            with self.subTest(flags=flags):
                with (
                    patch("winapp_cli.cli.sys.stdin.isatty", return_value=True),
                    patch("winapp_cli.cli.forward_native", side_effect=self.native) as native,
                    patch("builtins.input", return_value="n") as prompt,
                    patch("winapp_cli.cli._configure_after_init") as setup,
                ):
                    self.assertEqual(self.invoke(*flags), 0)
                prompt.assert_called_once()
                setup.assert_called_once_with(self.project, reset=False, config_only=False, verbose=False)
                self.assertEqual(native.call_args.args[1], ["init", *flags])

    def test_equals_true_uses_defaults_without_prompting(self):
        for flag in ("--use-defaults=true", "--no-prompt=TRUE"):
            with self.subTest(flag=flag):
                with (
                    patch("winapp_cli.cli.sys.stdin.isatty", return_value=True),
                    patch("winapp_cli.cli.forward_native", side_effect=self.native),
                    patch("builtins.input", return_value="n") as prompt,
                    patch("winapp_cli.cli._configure_after_init") as setup,
                ):
                    self.assertEqual(self.invoke(flag), 0)
                prompt.assert_not_called()
                self.assertTrue(setup.call_args.kwargs["reset"])

    def test_explicit_false_quiet_and_verbose_do_not_override_python_output(self):
        with (
            patch("winapp_cli.cli.forward_native", side_effect=self.native),
            patch("winapp_cli.cli._configure_after_init", side_effect=lambda *a, **kw: print("Python output")) as setup,
        ):
            self.assertEqual(self.invoke("--quiet", "false", "--verbose", "false"), 0)
        self.assertIn("Python output", self.stdout.getvalue())
        self.assertFalse(setup.call_args.kwargs["verbose"])

    def test_explicit_false_sparse_does_not_block_bindings(self):
        with (
            patch("winapp_cli.cli.forward_native", side_effect=self.native) as native,
            patch("winapp_cli.cli._configure_after_init") as setup,
        ):
            self.assertEqual(self.invoke("--sparse", "false", "--add-python-bindings"), 0)
        setup.assert_called_once()
        self.assertEqual(native.call_args.args[1], ["init", "--sparse", "false"])

    def test_ci_never_prompts_even_for_existing_config(self):
        with (
            patch("winapp_cli.cli.sys.stdin.isatty", return_value=True),
            patch.dict(os.environ, {"CI": "true"}),
            patch("winapp_cli.cli.forward_native", side_effect=self.native),
            patch("builtins.input") as prompt,
            patch("winapp_cli.cli._configure_after_init"),
        ):
            self.assertEqual(self.invoke(), 0)
        prompt.assert_not_called()

    def test_python_flags_other_than_opt_in_are_forwarded_to_native_validation(self):
        arguments = ["--install", "--python", "other.exe", "--package", "other_winrt", "--type", "Uri"]
        with (
            patch("winapp_cli.cli.forward_native", return_value=2) as native,
            patch("winapp_cli.cli._configure_after_init") as setup,
        ):
            self.assertEqual(self.invoke(*arguments), 2)
        self.assertEqual(native.call_args.args[1], ["init", *arguments])
        setup.assert_not_called()

    def test_native_failure_is_returned_before_config_or_dependency_changes(self):
        source = self.project / "pyproject.toml"
        source.write_text("not valid TOML", encoding="utf-8")
        with (
            patch("winapp_cli.cli.forward_native", return_value=37),
            patch("winapp_cli.cli._configure_after_init") as setup,
        ):
            self.assertEqual(self.invoke("--new-native-option"), 37)
        self.assertEqual(source.read_text(), "not valid TOML")
        setup.assert_not_called()

    def test_setup_uses_the_native_selected_project_not_the_callers_cwd(self):
        selected = self.root / "native-selected project"
        selected.mkdir()

        def native(executable, arguments, working_directory, *, init_result):
            self.assertEqual(working_directory, self.project)
            init_result.write_text(json.dumps(self.context(project=selected)), encoding="utf-8")
            return 0

        with (
            patch("winapp_cli.cli.forward_native", side_effect=native),
            patch("winapp_cli.cli._configure_after_init") as setup,
        ):
            self.assertEqual(self.invoke("--add-python-bindings"), 0)
        self.assertEqual(setup.call_args.args[0], selected)

    def test_help_and_schema_do_not_generate_and_help_exposes_the_wrapper_flag(self):
        for flag in ("--help", "-h", "-?", "/?", "--cli-schema", "--version"):
            with self.subTest(flag=flag):
                with (
                    patch("winapp_cli.cli.forward_native", return_value=0) as native,
                    patch("winapp_cli.cli._configure_after_init") as setup,
                ):
                    self.assertEqual(self.invoke(flag), 0)
                self.assertEqual(native.call_args.args[1], ["init", flag])
                setup.assert_not_called()
        self.assertIn("--add-python-bindings", self.stdout.getvalue())

    def test_native_help_aliases_skip_opt_in_validation_and_append_python_option(self):
        for flag in ("--help", "-h", "-?", "/?"):
            with self.subTest(flag=flag):
                self.stdout.seek(0)
                self.stdout.truncate()
                with (
                    patch("winapp_cli.cli.sys.stdin.isatty", return_value=True),
                    patch("winapp_cli.cli.forward_native", return_value=0) as native,
                    patch("winapp_cli.cli._configure_after_init") as setup,
                ):
                    self.assertEqual(self.invoke("--add-python-bindings", flag), 0)
                self.assertEqual(native.call_args.args[1], ["init", flag])
                self.assertIn("--add-python-bindings", self.stdout.getvalue())
                setup.assert_not_called()

    def test_native_non_sdk_modes_skip_and_explicit_requests_fail_loudly(self):
        for mode in ("none", "sparse", "unmanaged"):
            with self.subTest(mode=mode):
                def native(executable, arguments, working_directory, *, init_result):
                    init_result.write_text(json.dumps(self.context(mode=mode)), encoding="utf-8")
                    return 0
                with (
                    patch("winapp_cli.cli.forward_native", side_effect=native),
                    patch("winapp_cli.cli._configure_after_init") as setup,
                ):
                    self.assertEqual(self.invoke(), 0)
                    self.assertEqual(self.invoke("--add-python-bindings"), 1)
                setup.assert_not_called()

    def test_config_only_registers_without_running_generation(self):
        def native(executable, arguments, working_directory, *, init_result):
            context = self.context(mode="config-only")
            context["metadata_directory"] = None
            init_result.write_text(json.dumps(context), encoding="utf-8")
            return 0
        with (
            patch("winapp_cli.cli.forward_native", side_effect=native),
            patch("winapp_cli.cli._configure_after_init") as setup,
        ):
            self.assertEqual(self.invoke("--config-only", "--add-python-bindings"), 0)
        self.assertTrue(setup.call_args.kwargs["config_only"])

    def test_external_sdk_layout_is_not_guessed_or_written_into(self):
        def native(executable, arguments, working_directory, *, init_result):
            init_result.write_text(json.dumps(self.context(config=self.root / "external")), encoding="utf-8")
            return 0
        with (
            patch("winapp_cli.cli.forward_native", side_effect=native),
            patch("winapp_cli.cli._configure_after_init") as setup,
        ):
            self.assertEqual(self.invoke("--config-dir", str(self.root / "external")), 0)
            self.assertEqual(self.invoke("--add-python-bindings"), 1)
        setup.assert_not_called()

    def test_missing_and_invalid_native_context_are_errors(self):
        for data in (None, "not JSON", "[]", '{"schema":1,"mode":"sdk","project":null}'):
            with self.subTest(data=data):
                def native(executable, arguments, working_directory, *, init_result):
                    if data is not None:
                        init_result.write_text(data, encoding="utf-8")
                    return 0
                with (
                    patch("winapp_cli.cli.forward_native", side_effect=native),
                    patch("winapp_cli.cli._configure_after_init") as setup,
                ):
                    self.assertEqual(self.invoke(), 1)
                setup.assert_not_called()

    def test_native_runs_before_registration_generation_and_installation(self):
        order = []
        def native(*args, **kwargs):
            order.append("native")
            return self.native(*args, **kwargs)
        with (
            patch("winapp_cli.cli.forward_native", side_effect=native),
            patch("winapp_cli.cli.register_dependencies", side_effect=lambda *a, **kw: order.append("dependencies")),
            patch("winapp_cli.cli._bindings_after_sdk", side_effect=lambda *a, **kw: order.append("generate+sync")),
        ):
            self.assertEqual(self.invoke("--add-python-bindings"), 0)
        self.assertEqual(order, ["native", "dependencies", "generate+sync"])

    def test_new_init_records_the_short_default_name_without_resetting_custom_packages(self):
        self.unconfigured()
        with (
            patch("winapp_cli.cli.forward_native", side_effect=self.native),
            patch("winapp_cli.cli._bindings_after_sdk"),
        ):
            self.assertEqual(self.invoke("--add-python-bindings"), 0)
        self.assertEqual(self.config().package, "winapp_bindings")
        self.write_config(package="custom_winrt")
        with (
            patch("winapp_cli.cli.forward_native", side_effect=self.native),
            patch("winapp_cli.cli._bindings_after_sdk"),
        ):
            self.assertEqual(self.invoke("--add-python-bindings"), 0)
        self.assertEqual(self.config().package, "custom_winrt")

    def test_init_does_not_add_dynwinrt_or_codegen_project_dependencies(self):
        self.unconfigured()
        with (
            patch("winapp_cli.cli.forward_native", side_effect=self.native),
            patch("winapp_cli.cli._bindings_after_sdk"),
        ):
            self.assertEqual(self.invoke("--add-python-bindings"), 0)
        text = (self.project / "pyproject.toml").read_text()
        self.assertNotIn("dynwinrt", text)
        self.assertNotIn("dependency-groups", text)

    def test_setup_failure_returns_nonzero_after_native_success(self):
        with (
            patch("winapp_cli.cli.forward_native", side_effect=self.native),
            patch("winapp_cli.cli._configure_after_init", side_effect=WinappError("setup failed")),
        ):
            self.assertEqual(self.invoke(), 1)
        self.assertIn("setup failed", self.stderr.getvalue())

    def test_quiet_only_suppresses_python_output_not_native_streams(self):
        def native(*args, **kwargs):
            print("native output")
            return self.native(*args, **kwargs)
        with (
            patch("winapp_cli.cli.forward_native", side_effect=native),
            patch("winapp_cli.cli._configure_after_init", side_effect=lambda *a, **kw: print("Python output")),
        ):
            self.assertEqual(self.invoke("--quiet"), 0)
        self.assertIn("native output", self.stdout.getvalue())
        self.assertNotIn("Python output", self.stdout.getvalue())
