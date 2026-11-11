# Meridian

A tabbed file manager for Windows, built with Windows Forms. An alternative to Windows Explorer that keeps the classic right-click menu.

## Install

Meridian ships as a [dotnet tool](https://learn.microsoft.com/en-us/dotnet/core/tools/global-tools), [MeridianFileManager](https://www.nuget.org/packages/MeridianFileManager). It needs the .NET Desktop Runtime 11 or later.

```
dotnet tool install -g MeridianFileManager
```

## Run

```
meridian
meridian D:\Code
meridian D:\Code\readme.md
```

Pass a folder to open it, or a file to open its folder with that file selected. With no arguments the tabs from last time are reopened. `meridian --help` lists the switches.

From source: `dotnet run --project src/Meridian`.

## Replacing File Explorer

```
meridian --replace-explorer
meridian --restore-explorer
```

The same two actions are buttons under **Options** in the ⋯ menu. Replacing makes Meridian what opens folders and drives (double-clicks, folder shortcuts, other apps opening a folder) and what Win+E starts. Restoring undoes it, and puts back whichever app was the default for folders before.

 * It writes only to `HKEY_CURRENT_USER\Software\Classes`, so it applies to the current Windows account and needs no administrator rights.
 * Folders are pointed at the installed tool (`%USERPROFILE%\.dotnet\tools\meridian.exe`) whenever it exists, even when Replace is clicked in a local build, so cleaning a build folder never breaks opening folders. Only without the tool is the running copy registered.
 * Explorer's own **Open** stays on every folder's right-click menu.
 * The taskbar's File Explorer button, and apps that start `explorer.exe` themselves or ask it to show a file selected, still open Explorer.
 * Run `meridian --restore-explorer` before uninstalling the tool. Otherwise folders keep pointing at a Meridian that is no longer there.

## Publish

Pushing a tag builds and publishes that version: tag `1.2.3` (or `v1.2.3`) publishes 1.2.3 to nuget.org through [trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing). The `publish` job in `.github/workflows/build.yml` needs a `nuget` environment on the repository and a trusted publishing policy on nuget.org for this repository and workflow.

## Build and test

Needs the .NET 11 SDK.

```
dotnet build src/Meridian.slnx
dotnet test --solution src/Meridian.slnx
```

Tests use [TUnit](https://github.com/thomhurst/TUnit) and [Verify](https://github.com/VerifyTests/Verify):

 * Text snapshots cover sorting, filtering, navigation history, the folder tree, settings, and copy, move and rename against a sample folder. They run in any configuration.
 * Image snapshots of the whole window ([Verify.WinForms](https://github.com/VerifyTests/Verify.WinForms)) run in Debug builds only, since pixels depend on the Windows version, theme and fonts of the machine that captured them. While they run they create a sample folder at `C:\Users\Public\Documents\MeridianTests`, a path that is the same on every machine so it can appear in the address bar, and delete it afterwards.

## Features

 * Tabs, each with its own back/forward history and a cross to close it.
 * Drag a tab off the tab row to open it in a window of its own, or onto another Meridian window to move it there, history and all.
 * Lives in the tray. Closing the last window hides it; click the tray icon (or launch Meridian again) to bring it back, and choose **Exit** from the tray menu or the ⋯ menu to quit. The tray icon is put on the taskbar rather than in the overflow, unless you have moved it there yourself.
 * One copy per Windows account. Launching Meridian again, including a folder opened from Explorer when Meridian replaces it, hands the folders to the running copy, which opens them in a new window. All windows share that one process, so tabs can be dragged between any of them.
 * Folder tree with a **Pinned** section (folders pinned to Quick access in Explorer), a **Recent** section (folders Explorer adds to Quick access because you use them often), then the drives. It follows the active tab. Pin or unpin folders in Explorer to change the list; Meridian reads it when it starts.
 * Details list with shell icons and type names, natural sort order, and click-to-sort columns.
 * The classic Windows context menu on files, folders and the folder background, including installed shell extensions (7-Zip, Open with, and so on), less Send to, Include in library, Restore previous versions and Edit in Notepad. Hold Shift for the extended menu.
 * **Options** in the ⋯ menu: show hidden items (on by default; also Ctrl+H), show the Recent section, and replace or restore File Explorer.
 * Copy, move, delete and rename go through the Windows shell: standard progress dialog, conflict prompts, Recycle Bin and undo.
 * Drag and drop to and from Explorer and other apps.
 * Filter box: type part of a name, or a wildcard such as `*.cs`.
 * The list refreshes itself when the folder changes on disk.
 * Window size, open tabs and the options are remembered in `%AppData%\Meridian\settings.json`. With several windows open, the last one closed is what the next start restores.

## Keyboard

| Keys                              | Action                        |
|-----------------------------------|-------------------------------|
| Alt+Left / Backspace, Alt+Right   | Back, forward                 |
| Alt+Up                            | Up one level                  |
| Ctrl+T, Ctrl+W                    | New tab, close tab            |
| Ctrl+Tab, Ctrl+Shift+Tab          | Next tab, previous tab        |
| Ctrl+L, Alt+D, F4                 | Address bar                   |
| Ctrl+F, F3                        | Filter box                    |
| F5                                | Refresh                       |
| F2                                | Rename                        |
| Delete, Shift+Delete              | Recycle, delete permanently   |
| Ctrl+C, Ctrl+X, Ctrl+V            | Copy, cut, paste              |
| Ctrl+Shift+N                      | New folder                    |
| Alt+Enter                         | Properties                    |
| Ctrl+H                            | Show or hide hidden items     |
| Ctrl+N                            | New window                    |

Click a tab's cross or middle-click the tab to close it. The mouse back and forward buttons work too.
