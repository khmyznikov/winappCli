# Python WinUI counter

Follow this walkthrough to run the same code-only WinUI 3 counter in ordinary Python, try persistent storage with development package identity, and install it as an MSIX.

Before starting, follow the [Windows, Python and winapp-py installation prerequisites](../../docs/guides/python.md#prerequisites).

## 1. Initialize the sample

If you do not have this repository, clone it from a separate working directory first:

```powershell
git clone https://github.com/microsoft/winappCli.git
Set-Location .\winappCli
```

Open a normal PowerShell session at the repository root and enter the sample directory:

```powershell
Set-Location .\samples\python-app
```

Initialize the Windows project files and Python environment:

```powershell
winapp-py init --use-defaults --add-python-bindings
```

The sample initially contains source and `pyproject.toml`, not a preconfigured Windows project. `init` creates `winapp.yaml`, `Package.appxmanifest` and `Assets`, restores SDK files under `.winapp`, generates the selected WinRT bindings, and installs declared dependencies into an ordinary project `.venv`. It also installs the matching Windows App Runtime needed by WinUI.

Continue when the command reports **Python bindings ready**. Unless a step says otherwise, run all remaining commands from this sample directory in normal PowerShell. Run each command separately and wait for it to finish successfully; you do not need to activate `.venv`.

## 2. Run normally: count in memory

Start the app with the project's ordinary Python environment:

```powershell
winapp-py run .\app.py
```

The command prints **Using project .venv** and selects this directory's existing `.venv\Scripts\python.exe`; it does not assign package identity or install dependencies.

The window shows **No identity - memory only** and starts at **0**. Use **+** on the left to add one and **-** on the right to subtract one. At zero, the minus button is disabled. The number occupies a fixed area in the middle, so gaining digits does not move the buttons.

Click **+** twice and **-** once: the count becomes **1**. Close the window and run the same command again: the count returns to **0**, because this mode does not save it.

## 3. Run with development identity: save the count

Close the ordinary Python window. Enable **Windows Developer Mode** in Windows Settings if needed, then run:

```powershell
winapp-py run --identity .\app.py
```

The window now shows **Package identity - saved counter**. Package identity lets Windows recognize which registered app is running; the sample uses it to access app-local storage through `Windows.Storage.ApplicationData`.

Click **+**, close the window, and run the same command again. The saved count is restored from `LocalState\python-counter.json`; the terminal prints the full save-file path. Unlike the ordinary run, each successful change is saved.

The first run prepares a private Python environment under `.winapp\python-identity`. Your ordinary `.venv` is unchanged. This is a source-development runner, not a distributable app; the next section builds an installable copy.

### Clear the development identity

Close the window, then run:

```powershell
winapp-py identity clear
```

This removes the development registration and private environment, while preserving the ordinary `.venv`, generated bindings and saved application data. You can still build the MSIX from the ordinary environment.

## 4. Package as MSIX

PyInstaller builds the Python application directory; winapp then packages that directory as an MSIX. Building does not require `run --identity`.

### Restore build dependencies

First, prepare the SDK files, bindings and declared project dependencies:

```powershell
winapp-py restore
```

PyInstaller is already declared in this sample's `dev` group in `pyproject.toml`. `restore` installs it into `.venv`; winapp does not add PyInstaller to other projects.

### Build the application directory

After `restore` succeeds, run this separate command with the ordinary `.venv`, not the private development-identity environment:

```powershell
.\.venv\Scripts\python.exe -I -B -m PyInstaller `
  --clean --noconfirm --onedir --windowed --name PythonCounter `
  --paths .\.winapp\python `
  --collect-all dynwinrt `
  --collect-submodules winapp_bindings `
  .\app.py
```

The backticks continue one PowerShell command across multiple lines. The output directory, `dist\PythonCounter`, contains `PythonCounter.exe`, its Python runtime, DynWinRT and generated bindings. Its architecture matches the Python interpreter used to build it.

`--onedir` keeps the executable and supporting files together for MSIX packaging; `--windowed` makes it a GUI app without a terminal. Keep `--paths` so PyInstaller can find the generated package, and both `--collect-*` options so it includes the dynamically loaded bindings and DynWinRT files. Package the whole output directory, not just the EXE.

### Package and sign

For local testing, generate a development certificate matching the manifest's publisher:

```powershell
winapp-py cert generate --manifest .\Package.appxmanifest --output .\devcert.pfx
```

Keep `devcert.pfx` private. This certificate is for local testing; use your release-signing certificate when distributing the app. Before packaging for redistribution, put the sample's and bundled dependencies' license notices, including CPython, DynWinRT and PyInstaller, in `dist\PythonCounter\licenses`.

Then create and sign the MSIX from the complete application directory:

```powershell
winapp-py package .\dist\PythonCounter `
  --manifest .\Package.appxmanifest `
  --executable PythonCounter.exe `
  --cert .\devcert.pfx `
  --output .\dist\PythonCounter.msix
```

The result is `dist\PythonCounter.msix`. It includes Python and application dependencies, so users do not need to install Python separately. It does not copy the project's `.venv` or private development-identity environment.

**Windows App Runtime is not bundled.** The target machine must have the version and architecture required by the package's framework dependency. Initialization already installed the matching runtime on the build machine.

### Install and launch

Open **Administrator PowerShell**, return to this sample directory, and trust the development certificate:

```powershell
winapp-py cert install .\devcert.pfx
```

Then install the MSIX in your normal PowerShell session:

```powershell
Add-AppxPackage .\dist\PythonCounter.msix
```

Launch the installed app from **Start**, not the EXE in `dist\PythonCounter`. Windows provides package identity automatically, so the counter saves across app restarts without `winapp-py run --identity`. This installed app has its own saved count, separate from the development runner's count.

When finished testing, close the app and uninstall it through **Windows Settings > Apps > Installed apps**. `winapp-py identity clear` only clears the development registration, not this installed MSIX. If you trusted a certificate solely for this test, remove that certificate from **Certificates (Local Computer) > Trusted People > Certificates** when it is no longer needed.

## Continue developing

| File | What it controls |
| --- | --- |
| `app.py` | Application entry point and choice of memory or saved counter |
| `runtime.py` | Bootstrap selection and WinRT apartment lifetime |
| `window.py` | Code-only WinUI layout, window lifetime, button events and displayed messages |
| `counter.py` | Counter state and package-local JSON storage |
| `pyproject.toml` | Python dependencies and the WinRT types selected for binding generation |
| `winapp.yaml` | SDK version pins created during initialization |
| `Package.appxmanifest` and `Assets` | Windows package identity, launch information and icons |

Keep application edits in the source files, not in generated `.winapp` files. For changing API selections, see [configure bindings](../../docs/guides/python.md#configure-bindings); for rebuilding generated files or changing SDK versions, see [restore, generate and update](../../docs/guides/python.md#5-restore-when-needed).

## If a step fails

| Problem | Action |
| --- | --- |
| SDK setup, source startup or saving fails | Follow the [Python guide's troubleshooting steps](../../docs/guides/python.md#troubleshooting). The source app prints errors and its save-file path in the terminal. |
| The counter cannot save | Check the save path printed in the terminal and its permissions, then try again. The previous count is retained on failure. |
| The saved counter file is invalid | Repair the file reported by the app, or back it up and remove it to reset the count. The app does not overwrite invalid data. |
| PyInstaller reports missing `winapp_bindings` imports | Run `winapp-py restore`, then rebuild with all the options shown above. Do not package a build with missing bindings. |
| MSIX installation fails with `0x800B0109` | Trust the signing certificate with `winapp-py cert install .\devcert.pfx` in Administrator PowerShell, then retry installation. |
| MSIX installation reports a missing Windows App Runtime dependency | Install the runtime version and architecture required by the package on the target machine, then retry. |

## Test

From this sample directory, run the standard-library counter and bootstrap helper tests:

```powershell
python -m unittest -v test_app
```

For the full source workflow tests, run this command from the repository root with Pester 5 and a built Python platform wheel:

```powershell
.\scripts\test-samples.ps1 -Samples python-app -WinappPath .\artifacts\python
```

The suite initializes isolated copies, exercises real WinUI button events in ordinary and development-identity modes, checks persistence and cleanup, and restores a clean copy. It does not change this sample directory or an existing user registration.
