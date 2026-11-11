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
 * Folder tree showing Explorer's Quick access folders, then the drives. It follows the active tab. To change the folders at the top, pin or unpin them in Explorer; Meridian reads the list when it starts.
 * Details list with shell icons and type names, natural sort order, and click-to-sort columns.
 * The classic Windows context menu on files, folders and the folder background, including installed shell extensions (7-Zip, Send to, Open with, and so on). Hold Shift for the extended menu.
 * Copy, move, delete and rename go through the Windows shell: standard progress dialog, conflict prompts, Recycle Bin and undo.
 * Drag and drop to and from Explorer and other apps.
 * Filter box: type part of a name, or a wildcard such as `*.cs`.
 * The list refreshes itself when the folder changes on disk.
 * Window size, open tabs and the hidden-items setting are remembered in `%AppData%\Meridian\settings.json`.

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
