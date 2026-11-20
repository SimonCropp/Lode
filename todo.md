# Todo

Bugs, performance problems and missing features found in a review of the whole of `src/Lode` on 2026-11-18, at commit `f8a8fb2`. Line numbers are as of that commit. Those in the items marked **Reproduced** were read directly; some in the other items come from the reviewers' reports and were not each checked again.

How sure each item is:

 * **Reproduced**: a scratch program ran Lode's own code, or the Windows or .NET call it relies on, and showed it. The programs are not in the repository. One that names its assembly `Lode.Tests` sees Lode's internals, since the assembly is not signed.
 * **Read**: traced through the code, not run.
 * **Unverified**: the code is as described, but the item rests on how Windows, git or PowerShell behaves, which was not checked.

Contents:

 * [Bugs, unverified](#bugs-unverified)
 * [Missing features](#missing-features)
 * [Checked and found sound](#checked-and-found-sound)


## Bugs, unverified


### 31. Purge and Push go by a .git being there, where git decides for itself

**Where:** [CodeCommands.cs:76-80](src/Lode/CodeCommands.cs#L76) (`IsRepo`), [CodeCommands.cs:161-194](src/Lode/CodeCommands.cs#L161) (`Git`), [GitPurge.cs:147-156](src/Lode/GitPurge.cs#L147).

**What happens:** with the home folder a repository, and `Code\Foo\.git` a folder git won't take (empty, or its `HEAD` zeroed in a crash), Lode counts Foo a repository, and git walks past it up to the home one, where all of Foo is untracked. The dry run then lists all of Foo, and Purge deletes it. Push there would commit to, and push, the home repository. The same if Lode is started with `GIT_DIR` or `GIT_WORK_TREE` set, from a git alias or hook: only `GIT_TERMINAL_PROMPT` is set.

**Unverified:** that git goes on upward past a `.git` folder it won't take. Unlikely to be met either way.

**Fix:** in `Git`, set `GIT_CEILING_DIRECTORIES` to the folder above the code folder, and take `GIT_DIR`, `GIT_WORK_TREE` and `GIT_INDEX_FILE` out of the environment.


## Missing features


### 2. Run a command from the address bar

Explorer runs `cmd`, `powershell` or `wt` typed in the address bar, in the folder open, which developers use a good deal. Lode takes `cmd` for a folder of that name in the folder open, and says it can't find it. The shell's own names, `shell:downloads` and the like, are not known either.


### 3. Drives come and go only on F5

**Where:** `MainForm.cs:1366-1373` is the only caller of `FolderTree.RefreshDrives`. `MainForm.WndProc` (`1956-1978`) handles only `WM_APPCOMMAND`. Nothing in `src/Lode` handles `WM_DEVICECHANGE` or registers with `SHChangeNotify`. This PC's list is not watched: `FolderView.Watch` returns at once for it.

A USB drive plugged in, or a drive mapped, This PC's own **Map network drive** included, is not in the tree or in This PC until F5, and one taken out stays, with what was beneath it. Explorer shows both at once.

**Fix:** handle `WM_DEVICECHANGE` (`DBT_DEVICEARRIVAL`, `DBT_DEVICEREMOVECOMPLETE`), a little after the last of a burst, and refresh only the drives that changed.


### 6. Only a details list

No icons, tiles or thumbnails, and no preview pane. In the list a folder always has the plain folder icon ([ShellIcons.cs:184-187](src/Lode/ShellIcons.cs#L184)), where the tree and the tabs show a folder's own (Downloads, Documents, one with a `desktop.ini`).


### 7. Search

 * By name only: nothing looks inside files.
 * With hidden items shown, the default, it goes through `.git`, as well as `node_modules`, `bin` and `obj`. An option to leave out what git ignores would make it quicker and what it finds more useful.
 * Nothing limits how much it finds. What it has found is shown less often as there comes to be more of it, so the window is not kept busy, but each showing still merges in all of it on the UI thread.


### 8. Purge

 * It asks before it knows what will go. The dry run is worked out anyway, so the prompt could come after it, with how many and how big, or the list.
 * No way to purge only what git ignores (`git clean -Xd`). New source files not yet added go with `bin` and `obj`, for good if **Delete them permanently** is ticked.


### 9. Drag and drop on the tree

**Where:** `FolderTree.cs:394-405` (`OnItemDrag` acts only on a pinned entry), `1594-1603` and `1686-1702` (`DragOver` only sets the effect).

Dragging files over the tree shows no folder as the one that would take them, opens nothing held over, and does not scroll at the edges, so where a drop will land is a guess, and a folder inside a collapsed one can't be reached. A folder under This PC can't be dragged out of the tree, to the list or another app, which Explorer's pane allows.

**Fix:** `TVIS_DROPHILITED` on the node under the pointer in `DragOver`, a timer to expand it, and a `FileDrop` drag from `OnItemDrag` for folders.


### 10. No Network in the tree

A tab on a share has nowhere in the tree unless that very folder is pinned. See [bug 23](#23-a-pinned-entry-stays-selected-and-its-folder-watched-after-going-to-a-share).


### 11. The classic menu is light in the dark theme

**Where:** [ShellContextMenu.cs:503](src/Lode/ShellContextMenu/ShellContextMenu.cs#L503) (`TrackPopupMenuEx`).

Windows' own popup menus go dark only for a process that asks, through uxtheme's `SetPreferredAppMode` (ordinal 135) and `FlushMenuThemes` (136). Lode makes no such call, and Windows Forms 11.0.0 has no import of it. So in dark, **Show more options** would be a white menu over a dark window, where Explorer's is dark. Unverified: not run.

**Fix:** `SetPreferredAppMode`, allow dark, or force it for the Dark theme, once in `ExplorerStyle.Use`.


### 12. Accessibility

 * **High contrast:** the entry selected in a menu is `Blend(background, ControlText, 0.06f)` with its text in `MenuText` (`ExplorerRenderer.cs:196-241`, `ExplorerStyle.cs:126-134`). On a black high contrast menu that is #0F0F0F on black, so what is selected can't be seen. `ShadeButton` (`349-367`) and `GlyphButton`'s hover are the same. With high contrast on, fill with `SystemColors.Highlight` and draw in `HighlightText`.
 * **Names:** a `GlyphButton`'s text is a private-use character from the icon font, and it has no `AccessibleName` (`GlyphButton.cs`, `MainForm.cs:204-216`). Back, Forward, Up, Refresh, New tab and Menu have no name for a screen reader. Only the address, search and palette boxes do. Give each its tooltip's text in `CreateButton`.


## Checked and found sound

So that nobody goes over them again for nothing.

 * **Shell interop:** `IContextMenu`, `IContextMenu2` and `IContextMenu3` (vtable order, IIDs, `PreserveSig`), `CMINVOKECOMMANDINFOEX` (field order, 104 bytes on x64, masks), `IShellFolder`, `IEnumIDList`, `IExecuteCommand`, `IObjectWithSelection`, `IPropertyStore` and `PROPVARIANT`, `IActivationFilter` and the `CoCancelCall` family, `SHFILEINFOW`, `HDITEMW`, `LVITEMW`, `TVITEMW`, `SCROLLINFO`, and the message and flag constants, against the Windows SDK headers.
 * **Menu command ids:** offset from the first on `GetCommandString` and on invoke, Lode's own below 0x100.
 * **Freed on every path, errors included:** PIDLs, HMENUs, HICONs and COM objects in `ShellContextMenu`. The shell items, the operation and `Unadvise` in `ShellFileOperation.Run`. `PathOf`'s string. `ShellLink.Save`. The NetApi buffer in `Shares`. `DriveRemoval`'s registration and `GCHandle` on the last unsubscribe.
 * **Paint:** every pen, brush and path is in a `using`. Glyph bitmaps are kept by size and colour.
 * **Threading:** `UndoHistory.Changed` and `ShellFileOperation.Moved` are raised off the UI thread and both subscribers go back to it. `ActionLog` changes only on the UI thread. `JumpList` and `Places` work on STA threads of their own, with a guarded post back. The update check comes back to the UI thread before `settings.Save()`. The extension guard's timer and `Dispose` are under one lock, and it leaves no thread behind.
 * **Off the UI thread already:** free space, Quick access, and reading a folder's subfolders.
 * **Git:** `Git` reads both streams without deadlock, disposes the process and goes by the exit code. Purge leaves nested repositories, worktrees and submodules to git's own dry run, follows no junction when deleting outright, and keeps what it parses inside the folder purged.
 * **Updates:** prereleases are compared as `NuGetVersion`. The update script starts Lode again whether or not the update worked.
 * **Explorer replacement:** the default verb and the explore verb go there and back. `"C:\"` with its closing quote escaped is handled by `Locations.Normalize`.
 * **Tree:** `SelectAsync`, `ExpandOnceRead` and `SyncAsync` hold against a rebuild or being overtaken.
 * **File operations:** a rename that only changes case, long paths, and the order things are reported across volumes, are handled and tested.
 * **Settings:** a corrupt file falls back to the defaults, and is tested.
 * **`SvgPath`:** M, L, H, V, C and Z, their relative forms and repeats, are right, and it is only ever given the one outline.
 * No TODO or FIXME anywhere in `src/Lode`.
