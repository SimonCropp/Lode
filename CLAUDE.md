# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build and Test Commands

```bash
dotnet build src/Meridian.slnx --configuration Release
dotnet test src/Meridian.slnx --configuration Release
dotnet test src/Meridian.slnx --configuration Release --filter "FullyQualifiedName~NavigationManagerTests.GoBack"
dotnet run --project src/Meridian/Meridian.csproj
```

The solution file is `src/Meridian.slnx` (XML-based slnx format). Always build/test from the repo root using that path.

## Code Style (enforced as build errors)

- **No accessibility modifiers** (`dotnet_style_require_accessibility_modifiers = never:error`). Classes default to `internal`, members to `private`. Only add `public` when required (e.g., WPF types referenced in XAML need `public` for XamlWriter serialization).
- **`var` everywhere** — all three var preferences are errors.
- **Expression-bodied members** for methods, constructors, properties, accessors — all errors.
- **File-scoped namespaces** (`resharper_namespace_body = file_scoped`).
- **Braces required** for all control flow (if/else, foreach, while, for, lock, fixed).
- **No `this.` qualifier** — error.
- **LangVersion preview**, **Nullable enabled**, **TreatWarningsAsErrors**.
- XML/XAML/csproj/props indent is 2 spaces; C# is 4 spaces. Line endings are LF.
- `CA1822` (mark members as static) is an error — any method that doesn't access instance data must be `static`.

## Architecture

WPF app using MVVM pattern on .NET 11 Preview (`net11.0-windows`).

**MVVM layers:**
- `Views/` — XAML + code-behind. `MainWindow` hosts a `TabControl` bound to `MainViewModel.Tabs`. Each tab's content template renders a `FileBrowserTab` UserControl. `AddressBar` is a separate UserControl.
- `ViewModels/` — `MainViewModel` owns the tab collection and delegates navigation commands to the active `TabViewModel`. Each `TabViewModel` owns its own `NavigationManager`, `FileSystemWatcher`, and `ObservableCollection<FileItemViewModel>` for the file list. `FolderNode` is the TreeView item model with lazy child loading.
- `Services/` — static utility classes: `FileSystemService` (System.IO wrapper), `ShellRegistration` (HKCU registry for shell replacement), `ClipboardService` (file cut/copy/paste via DataFormats.FileDrop), `NavigationManager` (back/forward/up stack), `IconCache` (SHGetFileInfo → frozen BitmapSource, keyed by extension).
- `Interop/` — `NativeMethods` has DllImport for `SHGetFileInfo`/`DestroyIcon`. `ShellApi` converts HICON to WPF `BitmapSource`.
- `Models/` — `FileItem` record.

**Key design decisions:**
- `IconCache` is a shared singleton (one per `MainViewModel`). Icons are frozen `BitmapSource` for thread safety, cached by file extension.
- `FileSystemWatcher` events are throttled via a 300ms `DispatcherTimer` to avoid UI churn.
- `FolderNode` adds a dummy "Loading..." child; real children are loaded on first expand. The dummy node uses an empty path string — the constructor guards against infinite recursion by only adding the placeholder when `path.Length > 0`.
- `InternalsVisibleTo` exposes internals to the `Tests` project. Assembly signing is disabled.

## Testing

NUnit + Verify.Xaml + Verify.DiffPlex. Tests are in `src/Tests/`.

- Snapshot tests for WPF controls need `[Apartment(ApartmentState.STA)]` on the fixture.
- `ModuleInitializer` calls `VerifyDiffPlex.Initialize()` and `VerifyXaml.Initialize()`.
- Verified snapshot files (`.verified.xml`, `.verified.png`) are checked into `src/Tests/Views/`.
- WPF XamlWriter serialization is non-deterministic for complex controls (may add `xmlns:swb` namespace). Use `ScrubLinesContaining` or assertion-based tests for complex controls.
- `FileSystemServiceTests` uses `[SetUp]`/`[TearDown]` with unique temp directories per test — NUnit reuses fixture instances.

## Central Package Management

All package versions are in `src/Directory.Packages.props`. Project files use `<PackageReference>` without version attributes. `CentralPackageTransitivePinningEnabled` is on.
