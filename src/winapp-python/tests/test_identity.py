# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

from contextlib import redirect_stdout
import io
import json
from pathlib import Path
import subprocess
from unittest.mock import patch

from winapp_cli import identity
from winapp_cli.errors import WinappError
from winapp_cli.generate import generate
from winapp_cli.identity_environment import dependency_inputs
from winapp_cli.python_runtime import Distribution, Interpreter

from helpers import ProjectTest


class PythonIdentityTests(ProjectTest):
    def setUp(self):
        super().setUp()
        (self.project / "pyproject.toml").unlink()
        (self.project / "requirements.txt").write_text("", encoding="utf-8")
        (self.project / "Package.appxmanifest").write_text(
            '<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">'
            '<Identity Name="LocalPythonProbe" Publisher="CN=LocalPythonProbe" Version="1.0.0.0" />'
            '<Applications><Application Id="App" Executable="$targetnametoken$.exe" /></Applications>'
            '</Package>', encoding="utf-8",
        )
        (self.project / "app.py").write_text("print('hello')\n", encoding="utf-8")
        self.info = Interpreter("3.12.10", "x86_64")
        self.distribution = Distribution(
            self.info, "20250529",
            "https://github.com/astral-sh/python-build-standalone/releases/download/20250529/"
            "cpython-3.12.10%2B20250529-x86_64-pc-windows-msvc-install_only_stripped.tar.gz",
            "a" * 64,
        )
        for patcher in (
            patch.object(identity, "select_project_python", return_value=self.project / ".venv" / "Scripts" / "python.exe"),
            patch.object(identity, "probe_interpreter", return_value=self.info),
            patch.object(identity, "_require_developer_mode"),
        ):
            patcher.start()
            self.addCleanup(patcher.stop)

    def prepare_files(self, project, root, distribution, **kwargs):
        venv = root / "venv"
        runtime = venv / "runtime"
        runtime.mkdir(parents=True)
        (runtime / "python.exe").write_bytes(b"private PBS exe")
        (runtime / "python312.dll").write_bytes(b"private PBS dll")
        (venv / "Scripts").mkdir()
        (venv / "Scripts" / "python.exe").write_bytes(b"private launcher")
        (venv / "pyvenv.cfg").write_text(f"home = {runtime}\n", encoding="utf-8")
        return venv, runtime, venv / "Scripts" / "python.exe"

    def record(self):
        root = self.project / ".winapp" / identity.ROOT_NAME
        root.mkdir()
        owner = "a" * 32
        (root / identity.PRIVATE_MARKER).write_text(json.dumps({
            "schema": 2, "project": str(self.project), "owner": owner,
        }), encoding="utf-8")
        venv, runtime, entry = self.prepare_files(self.project, root, self.distribution)
        record = {
            "schema": 2, "project": str(self.project), "root": str(root), "owner": owner,
            "state": "ready", "entry": str(entry), "base": str(runtime / "python.exe"),
            "package": "LocalPythonProbe.debug", "publisher": "CN=LocalPythonProbe",
            "full_name": "LocalPythonProbe.debug_1.0.0.0_x64__abc", "registration_attempted": True,
            "interpreter": self.info.document(), "distribution": self.distribution.document(),
            "manifest_hash": identity._digest(self.project / "Package.appxmanifest"),
            "dependency_hash": dependency_inputs(self.project, None, self.info)[2],
            "runtime_hashes": {},
        }
        record["runtime_hashes"] = identity._runtime_hashes(record)
        identity._write_record(self.project, record)
        return record

    def package(self, record=None):
        return {
            "Name": record["package"] if record else "LocalPythonProbe.debug",
            "Publisher": record["publisher"] if record else "CN=LocalPythonProbe",
            "PackageFullName": record["full_name"] if record else "LocalPythonProbe.debug_1.0.0.0_x64__abc",
            "InstallLocation": str(self.project / ".winapp" / identity.ROOT_NAME),
            "IsDevelopmentMode": True,
        }

    def run_app(self, *args):
        return identity.run_with_identity(self.project, Path("app.py"), list(args),
                                         Path("winapp.exe"), None)

    def assert_rebuild(self, record, *, info=None):
        info = self.info if info is None else info
        distribution = Distribution(
            info, "20250529",
            "https://github.com/astral-sh/python-build-standalone/releases/download/20250529/"
            f"cpython-{info.version}%2B20250529-{info.architecture}-pc-windows-msvc-install_only_stripped.tar.gz",
            "a" * 64,
        )
        name, publisher = identity._manifest_identity(self.project / "Package.appxmanifest")
        package = {
            **self.package(), "Name": name, "Publisher": publisher,
            "PackageFullName": f"{name}_1.0.0.0_x64__abc",
        }
        registrations = [[self.package(record)]]
        if name != record["package"]:
            registrations.append([])
        registrations.extend([[self.package(record)], [], [], [], [package]])
        output = io.StringIO()
        with (
            patch.object(identity, "probe_interpreter", return_value=info),
            patch.object(identity, "_packages", side_effect=registrations),
            patch.object(identity, "resolve_distribution", return_value=distribution) as resolve,
            patch.object(identity, "prepare_runtime", side_effect=self.prepare_files) as prepare,
            patch.object(identity, "synchronize_dependencies") as install,
            patch.object(identity, "_powershell", return_value="") as remove,
            patch.object(identity, "run_tool") as native,
            patch.object(identity, "_verify_interpreter") as verify,
            patch.object(identity.subprocess, "run", return_value=subprocess.CompletedProcess([], 0)),
            redirect_stdout(output),
        ):
            self.assertEqual(self.run_app(), 0)
        if record["interpreter"] == info.document():
            resolve.assert_not_called()
        else:
            resolve.assert_called_once_with(info)
        prepare.assert_called_once()
        install.assert_called_once()
        native.assert_called_once()
        verify.assert_called_once()
        remove.assert_called_once()
        self.assertEqual(remove.call_args.kwargs["full_name"], record["full_name"])
        self.assertIn("-PreserveApplicationData", remove.call_args.args[0])
        self.assertIn("Rebuilding development identity", output.getvalue())
        current = identity._load_record(self.project)
        self.assertNotEqual(current["owner"], record["owner"])
        self.assertEqual(current["interpreter"], info.document())
        self.assertEqual(current["state"], "ready")
        self.assertEqual(current["package"], name)
        self.assertEqual(current["publisher"], publisher)
        self.assertEqual(current["runtime_hashes"], identity._runtime_hashes(current))
        return current

    def test_new_identity_prepares_matching_runtime_dependencies_and_native_registration(self):
        with (
            patch.object(identity, "_packages", side_effect=[[], [], [self.package()]]),
            patch.object(identity, "resolve_distribution", return_value=self.distribution),
            patch.object(identity, "prepare_runtime", side_effect=self.prepare_files),
            patch.object(identity, "synchronize_dependencies") as install,
            patch.object(identity, "run_tool") as native,
            patch.object(identity, "_verify_interpreter") as verify,
            patch.object(identity.subprocess, "run", return_value=subprocess.CompletedProcess([], 37)) as app,
        ):
            self.assertEqual(self.run_app("--value", "two words"), 37)
        record = identity._load_record(self.project)
        self.assertEqual(record["state"], "ready")
        self.assertEqual(record["interpreter"]["version"], "3.12.10")
        self.assertIn("runtime", native.call_args.args[0][2])
        install.assert_called_once()
        verify.assert_called_once()
        self.assertEqual(app.call_args.args[0], [record["entry"], str(self.project / "app.py"),
                                              "--value", "two words"])
        self.assertEqual(app.call_args.kwargs["env"]["VIRTUAL_ENV"],
                         str(Path(record["root"]) / "venv"))

    def test_reuse_does_not_resolve_catalog_download_or_resynchronize(self):
        record = self.record()
        (self.project / "app.py").write_text("print('source edit')\n", encoding="utf-8")
        with (
            patch.object(identity, "_packages", return_value=[self.package()]),
            patch.object(identity, "resolve_distribution") as resolve,
            patch.object(identity, "prepare_runtime") as prepare,
            patch.object(identity, "synchronize_dependencies") as install,
            patch.object(identity, "_verify_interpreter"),
            patch.object(identity.subprocess, "run", return_value=subprocess.CompletedProcess([], 0)),
        ):
            self.assertEqual(self.run_app(), 0)
        resolve.assert_not_called()
        prepare.assert_not_called()
        install.assert_not_called()
        self.assertEqual(identity._load_record(self.project), record)

    def test_changed_python_version_architecture_or_abi_rebuilds_owned_identity(self):
        record = self.record()
        for info in (
            Interpreter("3.12.11", "x86_64"),
            Interpreter("3.13.2", "x86_64"),
            Interpreter("3.13.2", "aarch64"),
            Interpreter("3.13.2", "x86_64", True),
            Interpreter("3.13.2", "x86_64", False, True),
        ):
            with self.subTest(interpreter=info):
                record = self.assert_rebuild(record, info=info)

    def test_changed_requirements_rebuilds_without_touching_ordinary_env_or_bindings(self):
        record = self.record()
        development = self.project / ".venv" / "keep.txt"
        development.parent.mkdir()
        development.write_text("keep", encoding="utf-8")
        bindings = self.project / ".winapp" / "python" / "keep.py"
        bindings.parent.mkdir()
        bindings.write_text("keep bindings", encoding="utf-8")
        (self.project / "requirements.txt").write_text("requests\n", encoding="utf-8")
        self.assert_rebuild(record)
        self.assertEqual(development.read_text(), "keep")
        self.assertEqual(bindings.read_text(), "keep bindings")

    def test_changed_included_requirements_rebuilds_owned_identity(self):
        included = self.project / "dependencies.txt"
        included.write_text("packaging\n", encoding="utf-8")
        (self.project / "requirements.txt").write_text("-r dependencies.txt\n", encoding="utf-8")
        record = self.record()
        included.write_text("requests\n", encoding="utf-8")
        self.assert_rebuild(record)

    def test_changed_project_metadata_rebuilds_owned_identity(self):
        record = self.record()
        (self.project / "pyproject.toml").write_text(
            '[project]\nname = "identity-test"\nversion = "1.0"\nrequires-python = ">=3.11"\n',
            encoding="utf-8",
        )
        self.assert_rebuild(record)

    def test_changed_sdk_pins_rebuilds_owned_identity(self):
        record = self.record()
        configuration = self.project / "winapp.yaml"
        configuration.write_text(
            "packages:\n  - name: Microsoft.WindowsAppSDK\n    version: 2.5.1\n", encoding="utf-8",
        )
        self.assert_rebuild(record)

    def test_identity_placeholders_are_rejected_before_registration_or_preparation(self):
        manifest = self.project / "Package.appxmanifest"
        original = manifest.read_text(encoding="utf-8")
        for before, after in (
            ('Name="LocalPythonProbe"', 'Name="Example.$targetnametoken$"'),
            ('Publisher="CN=LocalPythonProbe"', 'Publisher="CN=$targetentrypoint$"'),
        ):
            with self.subTest(attribute=after):
                manifest.write_text(original.replace(before, after), encoding="utf-8")
                with (
                    patch.object(identity, "_packages") as packages,
                    patch.object(identity, "resolve_distribution") as resolve,
                    patch.object(identity, "run_tool") as native,
                    self.assertRaisesRegex(WinappError, "literal"),
                ):
                    self.run_app()
                packages.assert_not_called()
                resolve.assert_not_called()
                native.assert_not_called()

    def test_changed_binding_dependencies_rebuilds_and_keeps_generated_sources(self):
        self.write_config()
        with patch("winapp_cli.generate.run_tool", side_effect=self.fake_codegen):
            bindings = generate(self.config(), Path("codegen.exe"), self.codegen_version)
        record = self.record()
        metadata = bindings / "pyproject.toml"

        def changed_codegen(arguments, project, **kwargs):
            self.fake_codegen(arguments, project, **kwargs)
            generated_metadata = Path(arguments[arguments.index("--output") + 1]) / "pyproject.toml"
            generated_metadata.write_text(
                generated_metadata.read_text(encoding="utf-8").replace(
                    'dependencies = [', 'dependencies = ["packaging", ',
                ), encoding="utf-8",
            )
            return ""

        with patch("winapp_cli.generate.run_tool", side_effect=changed_codegen):
            generate(self.config(), Path("codegen.exe"), self.codegen_version, force=True)
        updated = metadata.read_text(encoding="utf-8")
        self.assert_rebuild(record)
        self.assertEqual(metadata.read_text(encoding="utf-8"), updated)
        self.assertTrue((bindings / "Uri.py").is_file())

    def test_changed_manifest_version_name_or_publisher_rebuilds_owned_identity(self):
        manifest = self.project / "Package.appxmanifest"
        record = self.record()
        for before, after in (
            ('Version="1.0.0.0"', 'Version="2.0.0.0"'),
            ('Name="LocalPythonProbe"', 'Name="NewPythonProbe"'),
            ('Publisher="CN=LocalPythonProbe"', 'Publisher="CN=NewPythonProbe"'),
        ):
            manifest.write_text(manifest.read_text(encoding="utf-8").replace(before, after), encoding="utf-8")
            record = self.assert_rebuild(record)

    def test_unavailable_new_python_keeps_old_owned_identity(self):
        record = self.record()
        with (
            patch.object(identity, "probe_interpreter", return_value=Interpreter("3.13.2", "x86_64")),
            patch.object(identity, "_packages", return_value=[self.package()]),
            patch.object(identity, "resolve_distribution", side_effect=WinappError("No exact PBS distribution")),
            patch.object(identity, "_powershell") as remove,
            patch.object(identity, "prepare_runtime") as prepare,
            self.assertRaisesRegex(WinappError, "No exact PBS"),
        ):
            self.run_app()
        remove.assert_not_called()
        prepare.assert_not_called()
        self.assertEqual(identity._load_record(self.project), record)
        self.assertTrue(Path(record["entry"]).is_file())

    def test_incompatible_project_python_keeps_old_owned_identity(self):
        record = self.record()
        (self.project / "pyproject.toml").write_text('[project]\nrequires-python=">=3.13"\n', encoding="utf-8")
        with (
            patch.object(identity, "_powershell") as remove,
            self.assertRaisesRegex(WinappError, "selected environment uses 3.12.10"),
        ):
            self.run_app()
        remove.assert_not_called()
        self.assertEqual(identity._load_record(self.project), record)

    def test_changed_inputs_do_not_hide_private_runtime_tampering(self):
        record = self.record()
        Path(record["base"]).with_name("python312.dll").write_bytes(b"changed")
        (self.project / "requirements.txt").write_text("requests\n", encoding="utf-8")
        with (
            patch.object(identity, "_powershell") as remove,
            self.assertRaisesRegex(WinappError, "owned PBS runtime"),
        ):
            self.run_app()
        remove.assert_not_called()
        self.assertEqual(identity._load_record(self.project), record)

    def test_rebuild_refuses_foreign_registration_for_new_manifest_name(self):
        record = self.record()
        manifest = self.project / "Package.appxmanifest"
        manifest.write_text(
            manifest.read_text(encoding="utf-8").replace('Name="LocalPythonProbe"', 'Name="NewPythonProbe"'),
            encoding="utf-8",
        )
        foreign = {**self.package(), "Name": "NewPythonProbe.debug"}
        with (
            patch.object(identity, "_packages", side_effect=[[self.package()], [foreign]]),
            patch.object(identity, "_powershell") as remove,
            patch.object(identity, "resolve_distribution") as resolve,
            self.assertRaisesRegex(WinappError, "refusing to replace"),
        ):
            self.run_app()
        remove.assert_not_called()
        resolve.assert_not_called()
        self.assertEqual(identity._load_record(self.project), record)

    def test_rebuild_refuses_registration_replaced_during_cleanup(self):
        record = self.record()
        (self.project / "requirements.txt").write_text("requests\n", encoding="utf-8")
        foreign = {**self.package(), "PackageFullName": "LocalPythonProbe.debug_2.0.0.0_x64__other"}
        with (
            patch.object(identity, "_packages", side_effect=[[self.package()], [foreign]]),
            patch.object(identity, "_powershell") as remove,
            self.assertRaisesRegex(WinappError, "different registration"),
        ):
            self.run_app()
        remove.assert_not_called()
        self.assertEqual(identity._load_record(self.project), record)
        self.assertTrue(Path(record["entry"]).is_file())

    def test_rebuild_unregister_failure_keeps_owned_files_and_record(self):
        record = self.record()
        (self.project / "requirements.txt").write_text("requests\n", encoding="utf-8")
        with (
            patch.object(identity, "_packages", return_value=[self.package()]),
            patch.object(identity, "_powershell", return_value=""),
            self.assertRaisesRegex(WinappError, "no files were removed"),
        ):
            self.run_app()
        self.assertEqual(identity._load_record(self.project), record)
        self.assertTrue(Path(record["entry"]).is_file())

    def test_failed_rebuild_does_not_launch_old_environment(self):
        record = self.record()
        (self.project / "requirements.txt").write_text("requests\n", encoding="utf-8")
        with (
            patch.object(identity, "_packages", side_effect=[[self.package()], [self.package()], [], [], []]),
            patch.object(identity, "_powershell", return_value=""),
            patch.object(identity, "prepare_runtime", side_effect=self.prepare_files),
            patch.object(identity, "synchronize_dependencies", side_effect=WinappError("dependency conflict")),
            patch.object(identity, "run_tool") as native,
            patch.object(identity.subprocess, "run") as app,
            self.assertRaisesRegex(WinappError, "dependency conflict"),
        ):
            self.run_app()
        native.assert_not_called()
        app.assert_not_called()
        self.assertFalse(identity._record_path(self.project).exists())
        self.assertFalse(Path(record["root"]).exists())

    def test_private_dll_or_venv_cfg_tampering_is_detected(self):
        record = self.record()
        dll = Path(record["base"]).with_name("python312.dll")
        dll.write_bytes(b"changed")
        with self.assertRaisesRegex(WinappError, "owned PBS runtime"):
            self.run_app()

    def test_unknown_existing_registration_is_never_replaced(self):
        with patch.object(identity, "_packages", return_value=[self.package()]):
            with patch.object(identity, "run_tool") as native:
                with self.assertRaisesRegex(WinappError, "refusing to replace"):
                    self.run_app()
        native.assert_not_called()

    def test_missing_developer_mode_fails_before_runtime_download(self):
        with (
            patch.object(identity, "_packages", return_value=[]),
            patch.object(identity, "_require_developer_mode", side_effect=WinappError("Developer Mode required")),
            patch.object(identity, "resolve_distribution") as resolve,
        ):
            with self.assertRaisesRegex(WinappError, "Developer Mode required"):
                self.run_app()
        resolve.assert_not_called()

    def test_missing_registration_is_not_repaired_implicitly(self):
        self.record()
        with patch.object(identity, "_packages", return_value=[]):
            with self.assertRaisesRegex(WinappError, "package is missing"):
                self.run_app()

    def test_dependency_failure_removes_preparation_without_registering(self):
        with (
            patch.object(identity, "_packages", return_value=[]),
            patch.object(identity, "resolve_distribution", return_value=self.distribution),
            patch.object(identity, "prepare_runtime", side_effect=self.prepare_files),
            patch.object(identity, "synchronize_dependencies", side_effect=WinappError("dependency conflict")),
            patch.object(identity, "run_tool") as native,
        ):
            with self.assertRaisesRegex(WinappError, "dependency conflict"):
                self.run_app()
        native.assert_not_called()
        self.assertFalse(identity._record_path(self.project).exists())
        self.assertFalse((self.project / ".winapp" / identity.ROOT_NAME).exists())

    def test_native_registration_failure_cleans_only_verified_new_package(self):
        with (
            patch.object(identity, "_packages", side_effect=[[], [], [self.package()], []]),
            patch.object(identity, "resolve_distribution", return_value=self.distribution),
            patch.object(identity, "prepare_runtime", side_effect=self.prepare_files),
            patch.object(identity, "synchronize_dependencies"),
            patch.object(identity, "run_tool", side_effect=WinappError("native verification failed")),
            patch.object(identity, "_powershell", return_value="") as remove,
        ):
            with self.assertRaisesRegex(WinappError, "native verification failed"):
                self.run_app()
        self.assertEqual(remove.call_args.kwargs["full_name"], self.package()["PackageFullName"])
        self.assertIn("-PreserveApplicationData", remove.call_args.args[0])
        self.assertFalse(identity._record_path(self.project).exists())

    def test_wrong_identity_after_registration_is_rolled_back_not_marked_ready(self):
        with (
            patch.object(identity, "_packages", side_effect=[[], [], [self.package()],
                                                           [self.package()], []]),
            patch.object(identity, "resolve_distribution", return_value=self.distribution),
            patch.object(identity, "prepare_runtime", side_effect=self.prepare_files),
            patch.object(identity, "synchronize_dependencies"),
            patch.object(identity, "run_tool"),
            patch.object(identity, "_verify_interpreter", side_effect=WinappError("wrong identity")),
            patch.object(identity, "_powershell", return_value=""),
        ):
            with self.assertRaisesRegex(WinappError, "wrong identity"):
                self.run_app()
        self.assertFalse(identity._record_path(self.project).exists())
        self.assertFalse((self.project / ".winapp" / identity.ROOT_NAME).exists())

    def test_unowned_registration_after_failed_preparation_is_not_removed(self):
        package = self.package()
        package["InstallLocation"] = str(self.root / "another-project")
        with (
            patch.object(identity, "_packages", side_effect=[[], [], [package]]),
            patch.object(identity, "resolve_distribution", return_value=self.distribution),
            patch.object(identity, "prepare_runtime", side_effect=self.prepare_files),
            patch.object(identity, "synchronize_dependencies"),
            patch.object(identity, "run_tool", side_effect=WinappError("native failed")),
            patch.object(identity, "_powershell") as remove,
        ):
            with self.assertRaisesRegex(WinappError, "Cleanup could not finish"):
                self.run_app()
        remove.assert_not_called()
        self.assertTrue(identity._record_path(self.project).exists())

    def test_clear_preserves_app_data_and_never_touches_development_env(self):
        record = self.record()
        development = self.project / ".venv" / "keep.txt"
        development.parent.mkdir()
        development.write_text("keep", encoding="utf-8")
        with (
            patch.object(identity, "_packages", side_effect=[[self.package()], []]),
            patch.object(identity, "_powershell", return_value="") as remove,
        ):
            self.assertEqual(identity.clear_identity(self.project), 0)
        self.assertIn("-PreserveApplicationData", remove.call_args.args[0])
        self.assertEqual(development.read_text(), "keep")
        self.assertFalse(Path(record["root"]).exists())

    def test_clear_can_unregister_after_private_environment_was_deleted(self):
        record = self.record()
        import shutil
        shutil.rmtree(record["root"])
        with (
            patch.object(identity, "_packages", side_effect=[[self.package()], []]),
            patch.object(identity, "_powershell", return_value=""),
        ):
            self.assertEqual(identity.clear_identity(self.project), 0)
        self.assertFalse(identity._record_path(self.project).exists())

    def test_tampered_record_cannot_delete_another_directory(self):
        record = self.record()
        record["root"] = str(self.project)
        identity._write_record(self.project, record)
        with patch.object(identity, "_packages") as packages:
            with self.assertRaisesRegex(WinappError, "outside"):
                identity.clear_identity(self.project)
        packages.assert_not_called()

    def test_active_operation_blocks_clear_and_second_run(self):
        self.record()
        (self.project / "requirements.txt").write_text("requests\n", encoding="utf-8")
        with identity._operation_lock(self.project):
            with self.assertRaisesRegex(WinappError, "in use"):
                identity.clear_identity(self.project)
            with patch.object(identity, "_powershell") as remove:
                with self.assertRaisesRegex(WinappError, "in use"):
                    self.run_app()
            remove.assert_not_called()

    def test_probe_checks_real_image_base_prefix_and_version(self):
        record = self.record()
        output = {
            "code": 0, "name": record["full_name"], "prefix": str(Path(record["root"]) / "venv"),
            "base_prefix": str(Path(record["root"]) / "venv" / "runtime"),
            "image": record["base"], "version": "3.12.10", "platform": "win-amd64",
            "free_threaded": False, "debug": False,
        }
        with patch.object(identity.subprocess, "run",
                          return_value=subprocess.CompletedProcess([], 0, json.dumps(output), "")) as probe:
            identity._verify_interpreter(self.project, record)
        self.assertEqual(probe.call_args.args[0][:3], [record["entry"], "-I", "-c"])
        output["base_prefix"] = str(self.root / "unrelated-python")
        with patch.object(identity.subprocess, "run",
                          return_value=subprocess.CompletedProcess([], 0, json.dumps(output), "")):
            with self.assertRaisesRegex(WinappError, "wrong identity or environment"):
                identity._verify_interpreter(self.project, record)

    def test_malformed_identity_probe_does_not_escape_as_a_type_error(self):
        record = self.record()
        for output in ("[]", '{"prefix": null, "code": 0}'):
            with self.subTest(output=output):
                with patch.object(identity.subprocess, "run",
                                  return_value=subprocess.CompletedProcess([], 0, output, "")):
                    with self.assertRaisesRegex(WinappError, "invalid identity state"):
                        identity._verify_interpreter(self.project, record)
