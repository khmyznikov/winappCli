# WinApp CLI Python blog examples

Runnable companions to the [Python projection blog](../../ifdef-blog-python-projections-draft.md). This workflow uses the Python CLI from [microsoft/winappCli#1062](https://github.com/microsoft/winappCli/pull/1062), not an assumed published `winapp-cli` package. DynWinRT and codegen are the published **0.1.0rc23** packages.

## Prerequisites

- Windows 11 x64 or ARM64 with CPython 3.11-3.14 for this walkthrough.
- The Python CLI from microsoft/winappCli#1062, already installed and available as `winapp-py` in the terminal. Preview installation is described separately below.
- Windows App Runtime matching the restored SDK; the validated SDK is 2.5.1.

The application does not need a per-API native extension build. CLI installation is separate from the project environment it manages.

The three examples below are independent projects. Start each example from the repository root in a normal PowerShell session; its setup defines its own source paths and creates a new folder under your home directory. The CLI is installed once, but each folder has its own `.venv`, SDK pins, bindings configuration, and manifest.

## 1. Notification without identity

Use a non-elevated terminal. From the repository root, create and initialize a separate writable project before copying any scripts. Leave generated content outside this sample checkout:

```powershell
$repo = (Get-Location).Path
$sample = Join-Path $repo "samples\python-projections-blog"
$project = Join-Path $HOME "python-notification-demo"
if (Test-Path -LiteralPath $project) { throw "Choose a new project directory" }
New-Item -ItemType Directory -Path $project | Out-Null
Set-Location $project

winapp-py init . --use-defaults --add-python-bindings
if ($LASTEXITCODE -ne 0) { throw "Python project initialization failed" }
```

Initialization creates the project `.venv`, manifest, SDK pins and bindings configuration. The default generated package is `winapp_bindings`, installed editable from `.winapp\python\winapp_bindings`. Notification APIs are included in the default scope, so no image types or handwritten `[project]` metadata are needed. Keep the generated `types = []` for now; the sample `pyproject.toml` records these same default bindings settings.

Copy `notification.py`, run initialization again, then run it without identity:

```powershell
Copy-Item -LiteralPath (Join-Path $sample "notification.py") -Destination .
winapp-py init . --use-defaults --add-python-bindings
if ($LASTEXITCODE -ne 0) { throw "Python project initialization failed" }
winapp-py run .\notification.py
```

`notification.py` runs unpackaged, without `--identity`. It loads the restored architecture's Bootstrap DLL and calls `init_winappsdk(2, 5)`, matching SDK 2.5.1. It calls `register()` to let Windows App SDK register the Python executable as a notification source with its default name and icon, submits a 65% progress notification, checks its native ID, and removes its tag after five seconds. `unregister()` releases the process's active notification registration; source information remains for later runs. The SDK context is released after apartment cleanup. This is display-only: notification click handling and startup from a notification are not implemented.

## 2. WinUI counter without identity

`counter.py` creates a native WinUI window with a button and a click count. WinUI itself does not require package identity: this example runs unpackaged, without `--identity`. It does not need an NPU or the `systemAIModels` capability. It uses explicit SDK bootstrap, an STA for the UI, and event/object cleanup. The `init_winappsdk(2, 5)` arguments match the tested SDK 2.5.1.

From the repository root, create and initialize its own project:

```powershell
$repo = (Get-Location).Path
$sample = Join-Path $repo "samples\python-projections-blog"
$project = Join-Path $HOME "python-winui-counter"
if (Test-Path -LiteralPath $project) { throw "Choose a new project directory" }
New-Item -ItemType Directory -Path $project | Out-Null
Set-Location $project
winapp-py init . --use-defaults --add-python-bindings
if ($LASTEXITCODE -ne 0) { throw "Python project initialization failed" }
```

In this project's generated `pyproject.toml`, replace `types = []` in `[tool.winapp.python-bindings]` with these six WinUI types. Do not create a duplicate bindings table:

```toml
types = [
    "Microsoft.UI.Xaml.Application",
    "Microsoft.UI.Xaml.Window",
    "Microsoft.UI.Xaml.Controls.Button",
    "Microsoft.UI.Xaml.Controls.StackPanel",
    "Microsoft.UI.Xaml.Controls.TextBlock",
    "Microsoft.UI.Xaml.Automation.AutomationProperties",
]
```

Copy `counter.py`, run initialization again, then run it without identity:

```powershell
Copy-Item -LiteralPath (Join-Path $sample "counter.py") -Destination .
winapp-py init . --use-defaults --add-python-bindings
if ($LASTEXITCODE -ne 0) { throw "Python project initialization failed" }
winapp-py run .\counter.py
```

Click **Click me** and close the window when finished. WinUI hosting is experimental; this counter is not a complete GUI framework integration guide.

The ordinary counter command was verified on ARM64 at 200% display scaling: the real button was invoked twice, the label changed to `Clicks: 1` and `Clicks: 2`, and titlebar close ended the process with exit code 0. The [article screenshot](../../assets/python-winui-counter.png) comes from that unpackaged run, not a development-identity launch.

## 3. NPU OCR with identity

This example needs Developer Mode, a supported Copilot+ PC/NPU, and a ready Windows AI Text Recognition model. From the repository root, create and initialize a separate OCR project:

```powershell
$repo = (Get-Location).Path
$sample = Join-Path $repo "samples\python-projections-blog"
$project = Join-Path $HOME "python-npu-ocr"
if (Test-Path -LiteralPath $project) { throw "Choose a new project directory" }
New-Item -ItemType Directory -Path $project | Out-Null
Set-Location $project
winapp-py init . --use-defaults --add-python-bindings
if ($LASTEXITCODE -ne 0) { throw "Python project initialization failed" }
```

In the OCR project's generated `pyproject.toml`, replace `types = []` in `[tool.winapp.python-bindings]` with these three image and stream types:

```toml
types = [
    "Windows.Graphics.Imaging.BitmapDecoder",
    "Windows.Storage.Streams.InMemoryRandomAccessStream",
    "Windows.Storage.Streams.DataWriter",
]
```

`TextRecognizer` and `ImageBuffer` are already included in the default scope. Keep the OCR project's `package = "winapp_bindings"` and `scope = "default"`; do not create a duplicate bindings table.

```powershell
Copy-Item -LiteralPath (Join-Path $sample "ocr.py") -Destination .
winapp-py init . --use-defaults --add-python-bindings
if ($LASTEXITCODE -ne 0) { throw "Python project initialization failed" }
```

After initialization, edit the OCR project's `Package.appxmanifest` following the [article's OCR capability steps](../../ifdef-blog-python-projections-draft.md#3-recognize-image-text-locally-with-the-npu): declare `systemAIModels`, retain the existing namespace tokens and capabilities, and set a recent desktop `MaxVersionTested`. The declaration does not prepare a missing model.

Copy the article's input image as `sample.png`, then run with identity:

```powershell
Copy-Item -LiteralPath (Join-Path $repo "assets\python-npu-ocr-input-english.png") `
    -Destination .\sample.png
winapp-py run --identity .\ocr.py -- .\sample.png
```

`ocr.py` uses the NPU-backed Windows AI `TextRecognizer`, not the legacy `Windows.Media.Ocr` engine. It checks readiness, decodes the image into a BGRA8 bitmap, and prints the recognized lines as plain text. `recognize(path)` returns those lines joined into a string for use in your own application. The article shows the [input image and a recognized text excerpt](../../ifdef-blog-python-projections-draft.md#3-recognize-image-text-locally-with-the-npu); use a Unicode-capable terminal for non-ASCII output.

The tested model was already `Ready`. If it is not ready on another device, the script raises explicitly; it does not download components or quietly switch engines. Call `ensure_ready_async()` only after consent to preparation, and check its status and error result.

From `python-npu-ocr`, follow the [article's four packaging steps](../../ifdef-blog-python-projections-draft.md#package-and-share-the-ocr-app) to bundle, sign, install, and run the OCR command-line app.

## Extra: AppInstance initialization check

`app_instance.py` is an initialization check, not a replacement for `os.getpid()`. It can run in any initialized project above and verifies Windows App SDK usage in both execution modes. Without identity, it sets the architecture-specific Bootstrap DLL path and retains SDK initialization until after apartment cleanup. Its `2, 5` version must match the restored SDK. Copy it into the project you choose and run the following from that folder, using the `$sample` path defined in that project's setup.

```powershell
Copy-Item -LiteralPath (Join-Path $sample "app_instance.py") -Destination .
winapp-py run .\app_instance.py
winapp-py run --identity .\app_instance.py
winapp-py identity clear
```

To restore a project's pinned SDK inputs or force regeneration later, run these commands from that project's folder:

```powershell
winapp-py restore
winapp-py generate --force
```

## Validation boundaries

The revised init-first workflow and default-package imports have been checked statically; the complete sequence has not been replayed. The inline packaging steps have not been executed. The recorded native tests used a fresh project on a machine that already had Windows App Runtime, a NuGet SDK cache, and a ready NPU model, so this is not a clean-machine installation test. Before the AI capability edit, OCR failed with `CapabilityMissing`; afterward it returned the bilingual document's five lines and 36 word items.

The unpackaged notification program has been checked for syntax and registration/cleanup ordering with mocked APIs, but has not been run natively. Banner visibility also depends on Windows settings.

The OCR and AppInstance scripts were run on native ARM64/CPython 3.13.15 with the preview CLI and published DynWinRT 0.1.0rc23. NPU OCR output was checked against the test words and a generated bilingual document. The document yielded five lines and 36 words; the mixed-language `AI` was recognized as `Al`, illustrating that confidence is not guaranteed correctness. Native scores and polygon coordinates were checked. AppInstance returned the actual process PID in both execution modes.

For mypy, point it at the parent of the editable generated package:

```powershell
$env:MYPYPATH = (Resolve-Path .\.winapp\python).Path
python -m pip --python .\.venv\Scripts\python.exe install "mypy==1.20.2"
.\.venv\Scripts\python.exe -m mypy --strict --follow-imports=silent `
    .\notification.py .\ocr.py .\app_instance.py
```

This optional check installs mypy in the selected environment. It does not suppress missing generated imports or use casts. No Python toast click activation, arbitrary custom component deployment, or x64 execution is claimed by this validation. The optional counter's WinUI scope is separate from the main data API examples.

The private identity environment installs declared application dependencies and the generated bindings; packages manually added to `.venv` are not automatically copied. Ordinary `run` requires an existing `.venv` and does not initialize Windows App SDK on a script's behalf.

## Preview tool installation and version reproduction

This section is for obtaining the unreleased preview tool or reproducing the exact versions used during development. It is not part of the OCR runtime flow, and can be skipped when `winapp-py` is already installed.

The tested wheel is `winapp_cli-0.7.2.dev63-py3-none-win_arm64.whl`, built from microsoft/winappCli#1062 at `da26b1e8c220458d08cca1800cee86848e4e42c5`. The live PR head changed to `20585326` during review; this validation concerns the installed build, not that newer source. Install the matching architecture's wheel into the Python environment chosen for the CLI, for example the normal Python installation:

```powershell
$cliWheel = "C:\path\to\winapp_cli-0.7.2.dev63-py3-none-win_arm64.whl"
python -m pip install $cliWheel
winapp-py --version
```

The wheel installs the `winapp-cli` distribution and its `winapp-py` console entry. Its Python `Scripts` directory must be on `PATH`. This is a local PR build, not a claim that `pip install winapp-cli` retrieves the Python feature from a public release.

The validation used published DynWinRT/runtime and codegen `0.1.0rc23`. To reproduce those versions, copy the supplied [`constraints.txt`](constraints.txt) into the project and enable it before CLI init, restore, or forced generation:

```powershell
$env:PIP_CONSTRAINT = (Resolve-Path .\constraints.txt).Path
winapp-py generate --force
```

Constraints only limit package versions; they do not install the packages themselves. `PIP_CONSTRAINT` applies to this terminal and its pip subprocesses, including the CLI's codegen preparation, not to the entire machine. Normal use does not require creating a constraints file. The generated package independently pins the runtime matching its generator.
