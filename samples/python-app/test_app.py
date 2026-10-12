# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

import json
from pathlib import Path
import tempfile
import tomllib
import unittest
from unittest.mock import patch

from counter import Counter, load_count, save_count
from runtime import bootstrap_dll_path, windows_appsdk_version


class CounterTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.save = self.root / "python-counter.json"

    def test_missing_save_starts_at_zero_without_writing(self):
        self.assertEqual(load_count(self.save), 0)
        self.assertFalse(self.save.exists())

    def test_completed_writes_survive_reopening(self):
        save_count(self.save, 1)
        save_count(self.save, load_count(self.save) + 1)
        self.assertEqual(load_count(self.save), 2)

    def test_invalid_save_is_not_overwritten(self):
        for text in (
            "{bad", "[]", "{}", '{"schema":2,"count":1}', '{"schema":true,"count":0}',
            '{"schema":1,"count":true}', '{"schema":1,"count":-1}', '{"schema":1,"count":1.5}',
        ):
            with self.subTest(text=text):
                self.save.write_text(text, encoding="utf-8")
                with self.assertRaises(ValueError):
                    load_count(self.save)
                self.assertEqual(self.save.read_text(encoding="utf-8"), text)

    def test_failed_write_preserves_the_previous_count(self):
        save_count(self.save, 1)
        before = self.save.read_bytes()
        with patch("counter.os.replace", side_effect=PermissionError("read-only save")):
            with self.assertRaises(PermissionError):
                save_count(self.save, 2)
        self.assertEqual(self.save.read_bytes(), before)
        self.assertEqual(list(self.root.iterdir()), [self.save])

    def test_invalid_count_fails_before_writing(self):
        for count in (-1, True, 1.5):
            with self.subTest(count=count), self.assertRaises(ValueError):
                save_count(self.save, count)
        self.assertFalse(self.save.exists())

    def test_memory_counter_does_not_write_a_file(self):
        counter = Counter()
        counter.increment()
        self.assertEqual(counter.value, 1)
        self.assertIsNone(counter.save_path)
        self.assertFalse(self.save.exists())

    def test_persistent_counter_updates_value_only_after_saving(self):
        counter = Counter(save_path=self.save)
        counter.increment()
        self.assertEqual(counter.value, 1)
        self.assertEqual(load_count(self.save), 1)
        with patch("counter.os.replace", side_effect=PermissionError("read-only save")):
            with self.assertRaises(PermissionError):
                counter.increment()
        self.assertEqual(counter.value, 1)
        self.assertEqual(load_count(self.save), 1)
        counter.increment()
        self.assertEqual(counter.value, 2)
        self.assertEqual(load_count(self.save), 2)

    def test_memory_counter_decrements_and_stops_at_zero(self):
        counter = Counter(value=2)
        counter.decrement()
        self.assertEqual(counter.value, 1)
        counter.decrement()
        counter.decrement()
        self.assertEqual(counter.value, 0)
        self.assertFalse(self.save.exists())

    def test_decrement_at_zero_does_not_write(self):
        counter = Counter(save_path=self.save)
        with patch("counter.save_count") as save:
            counter.decrement()
        save.assert_not_called()
        self.assertEqual(counter.value, 0)
        self.assertFalse(self.save.exists())

    def test_persistent_counter_decrements_only_after_saving(self):
        counter = Counter(value=2, save_path=self.save)
        save_count(self.save, counter.value)
        before = self.save.read_bytes()
        with patch("counter.os.replace", side_effect=PermissionError("read-only save")):
            with self.assertRaises(PermissionError):
                counter.decrement()
        self.assertEqual(counter.value, 2)
        self.assertEqual(self.save.read_bytes(), before)
        counter.decrement()
        self.assertEqual(counter.value, 1)
        self.assertEqual(load_count(self.save), 1)
        counter.decrement()
        self.assertEqual(counter.value, 0)
        self.assertEqual(load_count(self.save), 0)

    def test_large_counter_values_are_preserved_when_stepping(self):
        counter = Counter(value=9999999999, save_path=self.save)
        counter.increment()
        self.assertEqual(counter.value, 10000000000)
        self.assertEqual(load_count(self.save), 10000000000)
        counter.decrement()
        self.assertEqual(counter.value, 9999999999)
        self.assertEqual(load_count(self.save), 9999999999)

    def test_bootstrap_dll_uses_python_architecture_without_reading_inventory(self):
        for platform, architecture in (("win-amd64", "x64"), ("win-arm64", "arm64")):
            with self.subTest(platform=platform):
                dll = self.root / ".winapp" / "bin" / architecture / "Microsoft.WindowsAppRuntime.Bootstrap.dll"
                dll.parent.mkdir(parents=True)
                dll.touch()
                self.assertEqual(bootstrap_dll_path(self.root, platform), dll)
                dll.unlink()
                with self.assertRaises(FileNotFoundError):
                    bootstrap_dll_path(self.root, platform)
        with self.assertRaises(RuntimeError):
            bootstrap_dll_path(self.root, "win32")

    def test_windows_appsdk_version_uses_product_package_without_requiring_dll(self):
        (self.root / ".winapp").mkdir()
        inventory = {
            "schema": 3, "packages": [
                {"name": "Microsoft.WindowsAppSDK", "version": "2.5.1"},
                {"name": "Microsoft.WindowsAppSDK.Foundation", "version": "2.3.12"},
            ],
        }
        for version, expected in (("2.5.1", (2, 5)), ("1.8.260416003", (1, 8))):
            with self.subTest(version=version):
                inventory["packages"][0]["version"] = version
                (self.root / ".winapp" / "winmds.lock.json").write_text(json.dumps(inventory), encoding="utf-8")
                self.assertEqual(windows_appsdk_version(self.root), expected)

    def test_windows_appsdk_version_rejects_invalid_inventory(self):
        (self.root / ".winapp").mkdir()
        package = {"name": "Microsoft.WindowsAppSDK", "version": "2.5.1"}
        for inventory in (
            [],
            {"schema": 2, "packages": [package]},
            {"schema": 3},
            {"schema": 3, "packages": []},
            {"schema": 3, "packages": [package, package]},
            {"schema": 3, "packages": [{"name": "Microsoft.WindowsAppSDK", "version": "2.5.1-preview1"}]},
        ):
            with self.subTest(inventory=inventory):
                (self.root / ".winapp" / "winmds.lock.json").write_text(json.dumps(inventory), encoding="utf-8")
                with self.assertRaises(ValueError):
                    windows_appsdk_version(self.root)

    def test_sample_uses_code_only_winui_without_xaml_reader(self):
        sample = Path(__file__).resolve().parent
        self.assertEqual(list(sample.glob("*.xaml")), [])
        configuration = tomllib.loads((sample / "pyproject.toml").read_text(encoding="utf-8"))
        selected = configuration["tool"]["winapp"]["python-bindings"]["types"]
        self.assertIn("Microsoft.UI.Xaml.Controls.StackPanel", selected)
        self.assertNotIn("Microsoft.UI.Xaml.Markup.XamlReader", selected)
        self.assertNotIn("XamlReader", (sample / "window.py").read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
