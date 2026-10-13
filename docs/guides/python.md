<!-- mslearn: true -->
<!-- description: Use winapp with Python to generate Windows App SDK and WinUI bindings, open an image picker, and run the app with package identity. -->

# Using winapp CLI with Python

This guide creates a code-only WinUI 3 app that opens a Windows App SDK file picker and displays the selected image path. No C# host or XAML is needed.

## Prerequisites

- **Windows 11**.
- **Windows CPython 3.11-3.14**, x64 or ARM64.
- **winapp-py**: Install the `winapp-cli` package from PyPI:

```powershell
python -m pip install --upgrade winapp-cli
winapp-py --help
```

The package includes the native CLI; no separate winapp or Node.js installation is needed.

## 1. Prepare the Python Project

Create a project directory, or use an existing Python project:

```powershell
New-Item -ItemType Directory -Path python-file-picker
Set-Location .\python-file-picker
```

Create `pyproject.toml`:

```toml
[project]
name = "python-file-picker"
version = "0.0.0"
requires-python = ">=3.11,<3.15"
dependencies = []

[tool.winapp.python-bindings]
package = "winapp_bindings"
scope = "default"
types = [
    "Microsoft.UI.Xaml.Application",
    "Microsoft.UI.Xaml.Window",
    "Microsoft.UI.Windowing.OverlappedPresenter",
    "Microsoft.UI.Xaml.Controls.Button",
    "Microsoft.UI.Xaml.Controls.Grid",
    "Microsoft.UI.Xaml.Controls.ScrollViewer",
    "Microsoft.UI.Xaml.Controls.TextBlock",
    "Microsoft.UI.Xaml.Automation.AutomationProperties",
    "Microsoft.UI.Xaml.Media.SolidColorBrush",
    "Windows.UI.ColorHelper",
]
additional-winmds = []
additional-refs = []
```

The file picker is included in the default Windows App SDK bindings. The `types` list adds only the WinUI controls needed for this app. In an existing project, update its bindings table rather than adding a duplicate table.

## 2. Initialize the Project

Run:

```powershell
winapp-py init --use-defaults --add-python-bindings
```

This prepares SDKs, Windows App Runtime, the manifest and icons, generates bindings, and installs declared dependencies into `.venv`. Continue when it reports **Python bindings ready**.

## 3. Add the File Picker

Create `app.py`:

```python
from threading import Thread
import sys

from dynwinrt import RoApartment, projected_lifetime_scope, to_winrt_object
from winapp_bindings.microsoft.ui.windowing import OverlappedPresenter
from winapp_bindings.microsoft.ui.xaml import (
    Application, ApplicationTheme, GridLength, GridUnitType,
    HorizontalAlignment, Style, TextWrapping, Thickness, Window,
)
from winapp_bindings.microsoft.ui.xaml.automation import AutomationProperties
from winapp_bindings.microsoft.ui.xaml.controls import (
    Button, Grid, RowDefinition, ScrollBarVisibility, ScrollViewer, TextBlock,
)
from winapp_bindings.microsoft.ui.xaml.media import SolidColorBrush
from winapp_bindings.microsoft.windows.storage.pickers import FileOpenPicker
from winapp_bindings.windows.graphics import SizeInt32
from winapp_bindings.windows.ui import ColorHelper


subscriptions = []
application = None
window = None


def launched():
    global window
    window = Window()
    window.title = "Python WinUI file picker"
    panel = Grid()
    panel.row_spacing = 16
    panel.margin = Thickness(24, 24, 24, 24)
    button_row = RowDefinition()
    button_row.height = GridLength(1, GridUnitType.Auto)
    result_row = RowDefinition()
    result_row.height = GridLength(1, GridUnitType.Star)
    panel.row_definitions.extend([button_row, result_row])
    button = Button()
    button.horizontal_alignment = HorizontalAlignment.Left
    accent_style = application.resources.lookup(to_winrt_object("AccentButtonStyle"))
    if accent_style is None:
        raise RuntimeError("WinUI AccentButtonStyle was not loaded.")
    button.style = Style(accent_style)
    button.content = to_winrt_object("Pick an image")
    status = TextBlock()
    status.text = "Choose a PNG or JPEG image."
    status.text_wrapping = TextWrapping.Wrap
    status.is_text_selection_enabled = True
    scroll = ScrollViewer()
    scroll.horizontal_scroll_bar_visibility = ScrollBarVisibility.Disabled
    scroll.vertical_scroll_bar_visibility = ScrollBarVisibility.Auto
    scroll.content = status
    Grid.set_row(scroll, 1)
    AutomationProperties.set_automation_id(button, "Pick")
    AutomationProperties.set_automation_id(status, "Result")
    AutomationProperties.set_automation_id(scroll, "ResultScroll")
    panel.children.extend([button, scroll])
    shade = 32 if application.requested_theme == ApplicationTheme.Dark else 243
    root = Grid()
    root.background = SolidColorBrush(ColorHelper.from_argb(255, shade, shade, shade))
    root.children.append(panel)
    window.content = root
    app_window = window.app_window
    presenter = OverlappedPresenter.create()
    presenter.is_resizable = False
    presenter.is_maximizable = False
    app_window.set_presenter(presenter)
    app_window.resize(SizeInt32(620, 420))
    dispatcher = window.dispatcher_queue

    def choose(_sender, _args):
        button.is_enabled = False
        window_id = window.app_window.id

        def wait_for_result():
            with RoApartment(1), projected_lifetime_scope():
                try:
                    picker = FileOpenPicker.create_instance(window_id)
                    picker.file_type_filter.extend([".png", ".jpg", ".jpeg"])
                    result = picker.pick_single_file_async().wait()
                    text = result.path if result else "Cancelled"
                except (OSError, RuntimeError) as error:
                    text = f"Picker failed: {error}"
                    print(text, file=sys.stderr, flush=True)

                def update():
                    status.text = text
                    button.is_enabled = True
                    print(text, flush=True)

                if not dispatcher.try_enqueue(update):
                    print("Picker window closed before the result could be displayed.",
                          file=sys.stderr, flush=True)

        Thread(target=wait_for_result).start()

    subscriptions.append(button.subscribe_click(choose))
    subscriptions.append(window.subscribe_closed(lambda _s, _a: Application.get_current().exit()))
    window.activate()


def initialize(_params):
    global application
    application = Application.create(launched)


with RoApartment(0), projected_lifetime_scope():
    Application.start(initialize)
    for unsubscribe in reversed(subscriptions):
        unsubscribe()
    window = None
    application = None
```

The picker uses the WinUI window's `AppWindow.Id`. Waiting runs on a worker thread so the UI stays responsive; the dispatcher updates the result on the UI thread.

## 4. Run with Identity

Enable **Windows Developer Mode** in Windows Settings if needed, then run:

```powershell
winapp-py run --identity .\app.py
```

Click **Pick an image**. Select a PNG or JPEG: its path appears in the window and terminal. Long paths wrap and can be scrolled or selected for copying; the window stays a fixed size. Cancel the dialog: the window displays **Cancelled**. The button becomes available again for another selection.

### Clean up development identity

Close the window, then run:

```powershell
winapp-py identity clear
```

This removes the development registration and private Python environment, while preserving `.venv`, bindings, project files and application data.

## 5. Restore When Needed

After cloning the project or removing generated files, run:

```powershell
winapp-py restore
```

To change API selections, edit `types` and run `winapp-py generate`. To update SDK and DynWinRT versions, run `winapp-py update`.

## 6. Package as MSIX

Build an application directory with Python and a packaging tool such as PyInstaller, then pass it to `winapp-py package`. Follow the [WinUI counter sample's packaging steps](../../samples/python-app/README.md#4-package-as-msix); use this app's entry file and chosen executable name.

## Tips

### Configure bindings

Keep `scope = "default"` to retain Windows App SDK bindings and add WinUI or Windows SDK types through `types`. Use `scope = "types"` only for an explicitly selected API surface.

### Run without identity

`winapp-py run .\app.py` uses the project root's existing `.venv` and prints its interpreter path. uv's default `.venv` works the same way, without `uv sync`. A missing interpreter produces manual-run guidance; non-Python inputs continue to use native `run`.

### Runtime initialization

The run command above uses package identity to make Windows App Runtime available. Ordinary WinUI execution needs Bootstrap initialization first; see the [counter sample](../../samples/python-app/README.md) for that workflow. The picker itself does not inherently require identity.

### Dependency declarations

Run `winapp-py update` to use a new DynWinRT release without upgrading winapp. Initial bindings setup records the resolved `codegen-version` in `[tool.winapp.python-bindings]` in your project's `pyproject.toml`; commit that file with the project. `restore` and `generate` reuse or install that exact generator version and automatically install the matching `dynwinrt` runtime through the generated package. Neither package needs a direct application dependency declaration. Running the app uses its generated bindings without checking for updates.

Declare your application's own dependencies in project metadata or requirements files; the private identity environment does not copy manually installed packages from `.venv`.

### Editor workflow

Select `.venv\Scripts\python.exe` as the editor interpreter. Do not edit generated files under `.winapp`.

### Help

Use `winapp-py <command> --help` for options. For shared commands, use the Python syntax appended after native help.

Run `winapp-py --version` to see both the installed Python package version and the native CLI version. Python prerelease numbers use PEP 440 formatting, so the same build can display `0.7.2.dev37` for Python and `0.7.2-prerelease.37` for native.

## Troubleshooting

| Problem | Action |
| --- | --- |
| Imports or SDK metadata are missing | Run `winapp-py restore`. |
| `FileOpenPicker` is missing after customizing bindings | Keep `scope = "default"` or explicitly select the picker, then regenerate. |
| WinUI cannot activate | Run with `--identity` as above; check that the configured Windows App Runtime is installed. |
| The picker fails | Read the error printed in the terminal, fix the reported cause and retry. |
| Ordinary `run` cannot find `.venv` | Use your chosen interpreter or `uv run <script>`; the wrapper does not create an environment or fall back to global Python. |
| DynWinRT dependency versions conflict | Remove direct `dynwinrt` and `dynwinrt-codegen` declarations from project dependencies, then run `winapp-py generate`. |
| The project has no recorded codegen version | Run `winapp-py init . --use-defaults --add-python-bindings` to record a version without resetting existing API selections, or run `winapp-py update` to select a new version. |

## Next Steps

- [WinUI counter sample](../../samples/python-app/README.md): ordinary execution, saved state and MSIX packaging.
- [Windows App SDK pickers](https://learn.microsoft.com/windows/apps/develop/files/using-file-folder-pickers): picker options and supported behavior.
- [CLI command reference](../usage.md): manifests, signing and packaging.
