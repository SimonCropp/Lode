# Meridian

A tabbed file manager for Windows, built with Windows Forms. An alternative to Windows Explorer that keeps the classic right-click menu.

## Run

```
dotnet run --project src/Meridian
```

Pass a folder to open it, or a file to open its folder with that file selected:

```
Meridian.exe D:\Code
Meridian.exe D:\Code\readme.md
```

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
