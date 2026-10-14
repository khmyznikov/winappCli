import asyncio
import os
from pathlib import Path
import sysconfig

from dynwinrt import RoApartment, init_winappsdk
from winapp_bindings.microsoft.windows.app_notifications import AppNotificationManager
from winapp_bindings.microsoft.windows.app_notifications.builder import (
    AppNotificationBuilder,
    AppNotificationProgressBar,
)


TAG = "python-projection-demo"


async def show_notification() -> None:
    with RoApartment():
        if not AppNotificationManager.is_supported():
            raise RuntimeError("Windows app notifications are not supported")

        progress = (
            AppNotificationProgressBar()
            .set_title("Processing images")
            .set_status("Running locally")
            .set_value(0.65)
            .set_value_string_override("65%")
        )
        notification = (
            AppNotificationBuilder()
            .add_text("Python task in progress")
            .add_text("This is a native Windows progress notification.")
            .add_progress_bar(progress)
            .set_tag(TAG)
            .build_notification()
        )

        manager = AppNotificationManager.get_default()
        manager.register()
        try:
            manager.show(notification)
            if notification.id == 0:
                raise RuntimeError("Windows did not assign a notification ID")
            print(f"Notification {notification.id}: native progress bar at 65%")
            await asyncio.sleep(5)
        finally:
            try:
                await manager.remove_by_tag_async(TAG)
            finally:
                manager.unregister()


def main() -> None:
    architecture = {
        "win-arm64": "arm64",
        "win-amd64": "x64",
    }.get(sysconfig.get_platform())
    if architecture is None:
        raise RuntimeError("Use Windows x64 or ARM64 CPython")
    bootstrap = (
        Path(__file__).resolve().parent
        / ".winapp"
        / "bin"
        / architecture
        / "Microsoft.WindowsAppRuntime.Bootstrap.dll"
    )
    if not bootstrap.is_file():
        raise FileNotFoundError(f"Run winapp-py restore; missing {bootstrap}")
    os.environ["WINAPPSDK_BOOTSTRAP_DLL_PATH"] = str(bootstrap)
    runtime = init_winappsdk(2, 5)
    try:
        asyncio.run(show_notification())
    finally:
        del runtime


if __name__ == "__main__":
    main()
