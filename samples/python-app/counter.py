# Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
# Licensed under the MIT License.

from dataclasses import dataclass
import json
import os
from pathlib import Path
import tempfile


def load_count(path: Path) -> int:
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        return 0
    if (
        not isinstance(data, dict) or set(data) != {"schema", "count"}
        or type(data["schema"]) is not int or data["schema"] != 1
        or type(data["count"]) is not int or data["count"] < 0
    ):
        raise ValueError(f"Invalid counter save; the file was not changed: {path}")
    return data["count"]


def save_count(path: Path, count: int) -> None:
    if type(count) is not int or count < 0:
        raise ValueError("Count must be a nonnegative integer.")
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", dir=path.parent, delete=False) as output:
            temporary = Path(output.name)
            json.dump({"schema": 1, "count": count}, output)
            output.flush()
            os.fsync(output.fileno())
        os.replace(temporary, path)
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)


@dataclass
class Counter:
    value: int = 0
    save_path: Path | None = None

    @classmethod
    def from_package_data(cls) -> "Counter":
        from winapp_bindings.windows.storage import ApplicationData

        data = ApplicationData.get_current()
        folder = data.local_folder if data is not None else None
        if folder is None or not folder.path:
            raise RuntimeError("Windows returned no package-local data folder.")
        path = Path(folder.path) / "python-counter.json"
        return cls(load_count(path), path)

    def increment(self) -> None:
        self._set_value(self.value + 1)

    def decrement(self) -> None:
        if self.value > 0:
            self._set_value(self.value - 1)

    def _set_value(self, value: int) -> None:
        if self.save_path is not None:
            save_count(self.save_path, value)
        self.value = value
