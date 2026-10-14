# Spikes S1 + S2: the real XAML compiler + .NET hot reload

Branch `nmetulev-hot-reload-real-compiler-spike` (local, off public `nmetulev-watch-hot-reload` 5fb594d7).
WinUI 3.0.0-dev package from 10/13 09:43. App: a journey-style page (Frame → MainPage, an INotifyPropertyChanged
view model, ObservableCollection items, a static formatter). Unpackaged, Debug, x64.

## What each save does (spike path, env `WINAPP_HOT_RELOAD_REAL_COMPILER=1`)

1. `msbuild <proj> -restore:false -t:PrepareForBuild;ResolveReferences;PrepareResources` (the real
   MarkupCompilePass1 → XamlPreCompile → MarkupCompilePass2, into the app's own obj, no CoreCompile).
2. Every `*.g.cs` / `*.g.i.cs` the compiler wrote replaces its document in the Roslyn workspace, unmodified
   (one exception, see finding 2). The user's `.cs` files are updated as before.
3. Roslyn's hot reload service (`HotReloadService.GetUpdatesAsync`, the dotnet-watch engine) gives the delta or
   the rude edits. Nothing is rewritten: no x:Bind → Binding, no text XAML, no reflection metadata provider.
4. The app applies the delta (`MetadataUpdater.ApplyUpdate`), gets the compiler's `.xbf` through
   `SetComponentMarkup(uri, binary: true, bytes)`, and runs the page's compiled `InitializeComponent()` again,
   so WinUI calls the updated `Connect()` / `GetBindingConnector()`.

## S1 results

Each row is one save from a running app. "Default" = today's connection IDs; "Proto A" =
`EnableHotReloadStableConnectionIds=true`. Live result = the journey's UI checks in the same process (handler
fires, named field set, x:Bind shows and tracks the value). Restarts are isolated: after a restart the next row
starts from a fresh baseline.

| # | Edit | Default: delta / rude edit | Proto A: delta / rude edit | Live result (when applied) |
|---|------|----------------------------|----------------------------|----------------------------|
| s01 | Text property only | no change to apply (only `#pragma checksum` differs) | same | ✅ text updated (.xbf alone) |
| s02 | `Click` → existing method | delta, 1 type | delta, 1 type | ✅ handler fires |
| s03a | `Click` → method not written yet | `error CS1061: 'MainPage' does not contain a definition for 'New_Click'` | same | ✅ correct: a restart fails the same way |
| s03b | …then the method added (xaml + cs in one save) | delta, 1 type | delta, 1 type | ✅ handler fires |
| s04/s05 | add `x:Name`, then code uses the field | delta, 1 type each | same | ✅ field set (`later:named later`) |
| s06 | first x:Bind on the page (OneTime) | delta, 5 types (new Bindings classes) | same | ✅ `Hello` |
| s07 | x:Bind OneWay | delta, 3 types | same | ✅ shows 0, tracks to 1 |
| s08 | x:Bind TwoWay (TextBox) + OneWay reader | delta, 4 types | same | ✅ typing updates the reader |
| s09 | x:Bind function `local:Fmt.Twice(ViewModel.Count)` | delta, 4 types | same | ✅ `twice 2` → `twice 4` |
| s10 | x:Bind casts `((local:Item)ItemPicker.SelectedItem).Name`, `(x:String)Picker.SelectedItem` | delta, 4 types | same | ✅ `Apple`, `First` |
| s11 | change an x:Bind path | delta, 3 types | same | ✅ `Sub` |
| s12 | remove the element with the function x:Bind | **restart**: `ENC0009 ×2 Updating the type of field`, `ENC0020 Renaming field 'isobj15TextDisabled'`, `'isobj16ItemsSourceDisabled'`, `ENC0033: Deleting field 'obj18'`, `'isobj18TextDisabled'`, `Deleting delegate 'InvokeFunctionDelegate'`, `Deleting field 'PendingFunctionBindings'` | **restart**: `ENC0033: Deleting field 'obj15'`, `Deleting field 'isobj15TextDisabled'`, `Deleting delegate 'InvokeFunctionDelegate'`, `Deleting field 'PendingFunctionBindings'` | — |
| s13 | insert an element at the top (renumbers IDs) | **restart**: `ENC0020 Renaming field 'obj11'`, `ENC0009 ×3 Updating the type of field`, `ENC0020 ×3 Renaming field 'isobj11TextDisabled'…` | ✅ delta, 2 types | ✅ handlers, TwoWay, x:Name all still work |
| s14 | delete an element (with Click + x:Name) | **restart**: `ENC0020 ×5 Renaming field 'obj13'`, `'obj18'`, `'isobj15TextDisabled'`… | ✅ delta, 1 type | ✅ |
| s15 | ListView + `DataTemplate x:DataType` + x:Bind | delta, 3 types | same | ✅ 2 items, `Banana` |
| s16 | `x:Load="{x:Bind ViewModel.ShowLazy, Mode=OneWay}"` | **restart**: `ENC0020: Renaming class 'MainPage_obj20_Bindings'`, `Renaming field 'obj20'`, `'isobj20TextDisabled'` | ✅ delta, 3 types | ✅ absent → toggle → present |
| s17 | change `x:DataType` Item → ItemBase | **restart**: `MainPage.g.cs(56,13): ENC0009: Updating the type of field` (`dataRoot`) | same | — |
| s18a | new UserControl: Badge.xaml saved before its code | `XamlCompiler error WMC1110: Invalid binding path 'Label' : Property 'Label' not found on type 'Badge'` | same | ✅ correct: a restart fails the same way |
| s18b | …its code-behind | delta, 9 types (new type, XamlTypeInfo) | same | ✅ |
| s18c | use `<local:Badge>` on the page | **restart**: `ENC0020: Renaming class 'MainPage_obj21_Bindings'`… | ✅ delta, 1 type (needs finding 6) | ✅ `badge v1` |
| s19a/b | `Page.Resources` add + `{StaticResource}`, then edit | delta / no change | same | ✅ `res v1` → `res v2` |
| s20 | final text edit; re-check everything | no change | same | ✅ all handlers, TwoWay, bindings |

**Totals** (24 saves; 2 are correct compiler errors): default **18 live, 6 restarts**; Prototype A **22 live,
2 restarts** (s12 deleted members, s17 `dataRoot` type). Every live save passed all its UI checks; the only
failed checks are counter values reset by restarts.

## S2: generated C# changed per save

Lines changed in `MainPage.g.cs` + `MainPage.g.i.cs` (normalized: no BOM, no `#pragma checksum` line):

| Edit | Default | Rude edits | Proto A | Rude edits |
|------|---------|-----------|---------|-----------|
| Click on existing method | +1 | 0 | +1 | 0 |
| add x:Name | +6 −1 / +2 | 0 | +5 / +2 | 0 |
| first x:Bind | +174 / +23 | 0 | +174 / +23 | 0 |
| OneWay | +119 −1 / +2 | 0 | +119 −1 / +2 | 0 |
| TwoWay | +84 −4 / +4 | 0 | +84 −4 / +4 | 0 |
| function | +92 / +2 | 0 | +92 / +2 | 0 |
| casts | +205 / +6 | 0 | +205 / +6 | 0 |
| change path | +27 −20 | 0 | +27 −20 | 0 |
| remove element (function) | +21 −115 / −2 | 8 | +0 −94 / −2 | 4 (ENC0033 only) |
| insert at top | +72 −67 / +2 | 7 | +41 −36 / +2 | **0** |
| delete element | +59 −65 / −2 | 5 | +0 −6 / −2 | **0** |
| x:Load | +161 −19 / +2 | 3 | +143 −1 / +2 | **0** |
| x:DataType change | +4 −4 | 1 | +4 −4 | 1 |
| use new UserControl | +23 −18 / +2 | 3 | +5 / +2 | **0** |

Prototype A removes every rename/renumber rude edit (ENC0020, and the ENC0009s caused by a renumbered `objN`
field now holding a different element type). What is left are deletions (ENC0033) and a real type change.

## Findings (what had to be true for this to work)

1. **Don't run CoreCompile.** A normal `Build` overwrites `obj\App.pdb`. Edit and continue reads the baseline
   PDB through the path recorded in the running assembly, lazily, on the first update. It then matched the
   *new* generated text, treated the old document as current, and silently dropped the first update after every
   launch (`Document unchanged`, `NoChangesToApply`). The XAML targets alone fix it.
2. **Roslyn bug (affects plain C# too, i.e. today's rewrite path).** Roslyn 5.9.0 (SDK 10.0.401) throws
   `NullReferenceException at DefinitionMap.GetPreviousMethodHandle … GetDeletedSynthesizedMethods` when an
   update adds an initialized static field to a type that an *earlier update* added. Repro in plain C#:
   save 1 adds `class Helper { static bool s_a = false; … }`, save 2 adds `static bool s_b = false;` → crash,
   restart. The compiler's `private static bool isobjNTextDisabled = false;` hits it on the second x:Bind save.
   The spike strips the redundant `= false` (one regex, labeled as a simulated compiler change). Asks: a Roslyn
   issue, and the XAML compiler not emitting the redundant initializer.
3. **Roslyn skips `*.g.i.cs` entirely.** `EditAndContinue/Utilities/Extensions.cs` treats `.g.i.cs` as a WPF
   design-time-only document. WinUI puts x:Name fields, `InitializeComponent` and the `Bindings` field there.
   Additions happen to work, but changes are invisible. Show me: `<TextBlock x:Name="Later">` →
   `<TextBox x:Name="Later">` → **applied, no rude edit**; the app now stores a TextBox in a field whose runtime
   type is TextBlock and calls TextBlock members on it. Expected: ENC0009 → restart. Ask: the XAML compiler
   emits a name Roslyn analyzes (any suffix other than `.g.i.cs`).
4. **Re-init in place needs `Bindings.Update()`.** The compiled bindings initialize on the root's `Loading`,
   which doesn't fire again for a page that is already loaded. The spike calls the new `Bindings.Update()`.
5. **Old Bindings objects keep running.** Each re-init left the previous compiled Bindings subscribed to the
   view model (1 → 5 handlers over 5 saves). After a delta they run the *new* method bodies against fields they
   never set → first-chance NullReferenceExceptions, and TwoWay/function checks failed. Calling the old
   `Bindings.StopTracking()` *before* `ApplyUpdate` (while old code matches old fields) keeps it at 1 handler and
   all checks pass. Could move into WinUI: the generated `InitializeComponent` could stop the old Bindings.
6. **XamlTypeInfo caches its type tables.** `InitTypeTables()` runs once; an added type isn't found
   (`XAML parsing failed` for `<local:Badge>`), and inserting a type **shifts every index** that the updated
   `CreateXamlType(index)` uses, so a stale table can resolve the wrong type. The spike resets the app's
   `__appProvider`. Ask: the compiler generates a `MetadataUpdateHandler` that clears these tables.
7. Binary `.xbf` via `SetComponentMarkup(binary: true)` worked for every save (the agent removes the URI's
   markup first; not verified as necessary). The rewrite path's load-callback wiring must be off for these
   pages: its `BindingsStandIn` replaced the compiled `Bindings` with a no-op.
8. Correctness matches a restart, as Morten asked: a `Click` to a missing method and a UserControl binding to a
   missing property are compiler errors live, exactly as after a restart.
9. Speed (not a goal here): 13–16 s per XAML save for the msbuild invocation. The rest is milliseconds.

## Recommendation

**Yes, this approach can replace the rewrite path**, with these conditions. All are small, and most are on the
compiler side, which is the right owner:

- WinUI compiler: Prototype A on by default for hot-reload builds; keep retired members as tombstones
  (removes the ENC0033 deletions); no `= false` static initializers; don't name the output `.g.i.cs`; generate a
  XamlTypeInfo reset hook; ideally stop the old Bindings in `InitializeComponent`.
- Roslyn: file the static-initializer NRE (plain C# repro above).
- winapp: never write obj's PDB (compiler-as-a-service fits); after a delta, re-init then `Bindings.Update()`;
  `StopTracking()` before the delta; no rewrite-path wiring for these pages.
- Remaining restarts with all of the above: an `x:DataType` change (`dataRoot` changes type), unless the
  compiler types that field more loosely. That is an acceptable restart.

With Prototype A and the agent-side fixes, the spike already runs 22 of 24 edits live (2 are genuine compiler
errors) with real x:Bind, without rewriting x:Bind, without a second copy of compiler behavior, and from the
compiler's own `.xbf`.

## Reproduce

```powershell
# scripts in C:\hrmvp-scratch: gen-spike.ps1 (journeys), stage-spike.ps1, killspike.ps1, s2-diff.ps1
$env:WINAPP_HOT_RELOAD_REAL_COMPILER='1'; $env:WINAPP_HOT_RELOAD_SPIKE_LOG='C:\hrspike\log-stable'
.\test\benchmarks\watch-hot-reload\Run-WatchJourney.ps1 -Winapp .\artifacts\cli\win-x64\winapp.exe `
  -JourneyName real-compiler-spike-stable -Packaging Unpackaged -WinUIPackageSource C:\mux-hr\PackageStore -OutputRoot C:\hrspike\runs
.\s2-diff.ps1 -Log C:\hrspike\log-stable
```

Runs: `C:\hrspike\runs\20261014-145419` (default), `20261014-151323` (Proto A), `20261014-152333` (x:Name type
change). Per-save before/after generated code and EnC results: `C:\hrspike\log-default`, `log-stable`,
`log-nametype`. Roslyn EnC traces: `C:\hrspike\enclog-*`.

## Parity gate (nmetulev-hr-parity-gate @ e56f4c59, rebased WinUI package c7cbf40ee)

The gate compares the hot-reloaded page with a clean build of the same source. I merged it into the spike branch
and ran it with `-Parity On`. In the gate's swap journey, every case after d1 restarts, because each case deletes the
previous one (ENC0033/ENC0020, with or without Prototype A). A restart is a clean build, so those rows match
trivially. To test each case live, I also ran each one as the first save after launch (`parity-dN-isolated`).

| Case | Rewrite path (gate author) | Real compiler, in-place re-init | Real compiler, page re-created |
|------|---------------------------|---------------------------------|--------------------------------|
| d1 template x:Bind under `DataContext="{x:Bind Inner}"` | red: 'inner' vs 'item' | ✅ green, live, same process | ✅ green |
| d2 template x:Bind to a public field | red: '' vs 'field' | ✅ green, live (isolated) | ✅ green |
| d3 `x:Load="{x:Bind Vm.Flag, Mode=OneWay}"`, Flag false | red: present vs missing | ✅ green, live (isolated) | ✅ green |
| d4 OneTime `{x:Bind Late}`, Late set after Loaded with no notification | red: 'loaded' vs 'ctor' | ❌ red, verbatim below | ✅ green, live, same process |
| parity-xbind p1 | green | ✅ green | ✅ green |

d4, in place (`C:\hrspike\runs\20261014-154731`):

```
parity: 3 difference(s) between hot reload and a clean build (6 elements, 3 readings; ...)
  [initial] BindPage / StackPanel#Root/TextBlock#CaseLate / Text: hot reload 'loaded' vs clean build 'ctor'
  [after set] BindPage / StackPanel#Root/TextBlock#CaseLate / Text: hot reload 'loaded' vs clean build 'ctor'
  [after reset] BindPage / StackPanel#Root/TextBlock#CaseLate / Text: hot reload 'loaded' vs clean build 'ctor'
```

Why: the page is already loaded, so its Loading event never fires again. The re-init has to initialize the compiled
bindings itself (the spike calls the new `Bindings.Update()`), and that reads `Late` after `Loaded` changed it. No
in-place variant can match, because the value the clean build saw is gone. Skipping `Update()` doesn't help either:
the binding would then never initialize.

To match: re-create the page (a new instance, navigated in place of the old one, so its constructor, `Loading` and
`Loaded` run in the clean-build order). The spike does this with `WINAPP_HOT_RELOAD_SPIKE_RECREATE=1` (the agent's
existing Renavigate). d1–d4 and p1 were all green, live, in the same process
(`C:\hrspike\runs\20261014-155250`, `-155400`, `-155452`, `-155540`, `-155628`). The cost is page state: fields,
page-owned view models, typed text, scroll position and selection reset to what a fresh navigation gives. A window
can't be re-created this way.

Suggested product rule: re-create pages by default, because that is what a restart would show. Offer in-place re-init
(keeps state) as the option for people who accept differences like d4.

Prototype A on the swap journey: still 3 restarts. A replaced element gets a new append-only ID, and Roslyn reports
the dropped `obj8` next to the new `obj13` as `ENC0020: Renaming field 'obj8'`, or as
`ENC0033: Deleting class 'BindPage_obj1_BindingsTracking'`. Keeping retired members as tombstones (already in the
compiler asks) would cover this.
