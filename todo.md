# Todo

Bugs, performance problems and missing features found in a review of the whole of `src/Lode` on 2026-11-18, at commit `f8a8fb2`. Line numbers are as of that commit. Those in the items marked **Reproduced** were read directly; some in the other items come from the reviewers' reports and were not each checked again.

How sure each item is:

 * **Reproduced**: a scratch program ran Lode's own code, or the Windows or .NET call it relies on, and showed it. The programs are not in the repository. One that names its assembly `Lode.Tests` sees Lode's internals, since the assembly is not signed.
 * **Read**: traced through the code, not run.
 * **Unverified**: the code is as described, but the item rests on how Windows, git or PowerShell behaves, which was not checked.

Contents:

 * [Bugs, unverified](#bugs-unverified)
 * [Smaller bugs](#smaller-bugs)
 * [Performance](#performance)
 * [Missing features](#missing-features)
 * [Checked and found sound](#checked-and-found-sound)


## Bugs, unverified


### 31. Purge and Push go by a .git being there, where git decides for itself

**Where:** [CodeCommands.cs:76-80](src/Lode/CodeCommands.cs#L76) (`IsRepo`), [CodeCommands.cs:161-194](src/Lode/CodeCommands.cs#L161) (`Git`), [GitPurge.cs:147-156](src/Lode/GitPurge.cs#L147).

**What happens:** with the home folder a repository, and `Code\Foo\.git` a folder git won't take (empty, or its `HEAD` zeroed in a crash), Lode counts Foo a repository, and git walks past it up to the home one, where all of Foo is untracked. The dry run then lists all of Foo, and Purge deletes it. Push there would commit to, and push, the home repository. The same if Lode is started with `GIT_DIR` or `GIT_WORK_TREE` set, from a git alias or hook: only `GIT_TERMINAL_PROMPT` is set.

**Unverified:** that git goes on upward past a `.git` folder it won't take. Unlikely to be met either way.

**Fix:** in `Git`, set `GIT_CEILING_DIRECTORIES` to the folder above the code folder, and take `GIT_DIR`, `GIT_WORK_TREE` and `GIT_INDEX_FILE` out of the environment.


## Smaller bugs

All read, none run.

 * **New chosen from the classic menu** goes through the shell alone, so Lode's Ctrl+Z never hears of what it made. Delete and Create shortcut are caught by their verbs, `delete` and `link`, and done by Lode; New's entries are filled in only as its submenu opens, and whether they have verbs to catch is unverified.


## Performance


### 6. Closing the selected tab shows the first tab on the way

**Where:** [MainForm.cs:1014-1037](src/Lode/MainForm.cs#L1014) (`CloseTab`), [MainForm.cs:1151-1171](src/Lode/MainForm.cs#L1151) (`RemoveTab`), `MainForm.cs:1847-1853` (`CloseTabs`).

**What happens:** Windows Forms selects tab 0 when the selected page is removed, which raises `SelectedIndexChanged`. So with tabs A, B and C, closing C first syncs to A: A is showing, so it reads its folder if stale and watches it, and the tree walks to it. Then the code selects B, a second sync, and calls `SyncToActiveView` itself, a third. **Close other tabs** and **Close tabs to the right**, with the selected tab among those going, show and read each in turn. Traced through the Windows Forms source, not run.

**Fix:** select the tab that will be left before removing the page, and drop the last sync.


### 7. Git commands go through repositories one at a time

**Where:** [CodeCommands.cs:147-159](src/Lode/CodeCommands.cs#L147) (`ForEach`), [GitPurge.cs:218-229](src/Lode/GitPurge.cs#L218).

**What happens:** Pull on a code folder of 50 to 100 repositories is minutes of waiting on the network, one after another. Purge runs one `git clean -xdn` at a time before deleting anything. Each repository is its own working tree, so they can go together.

**Fix:** `Parallel.ForEach` with 4 to 8 at once, results kept by index, progress as "n of N done".


### 8. Starting a watch in the tree can hold the window on a drive that does not answer

**Where:** `FolderTree.cs:865-892` (`Watch`, called on the UI thread from `695`, `942` and `1009`), [FolderWatcher.cs:40-44](src/Lode/FolderWatcher.cs#L40) and [51-56](src/Lode/FolderWatcher.cs#L51), `DriveRemoval.cs:107`.

**What happens:** `new FileSystemWatcher(path)` asks whether the folder is there, and `EnableRaisingEvents` opens it. `FolderWatcher` then calls `DriveRemoval.Subscribe`, which opens the root with `CreateFileW` for anything but a UNC path, mapped drives included, holding its static gate. Click an offline mapped drive in the tree, or a folder on a disk that has to spin up: `OnAfterSelect` runs `WatchSelected` before `PathSelected`, so the window waits. The same at startup with the restored tab at `Z:\`. A registration that failed is not remembered, so every watcher on a mapped drive tries again. `FolderView` already keeps this off the UI thread for somewhere on the network.

**Fix:** make the watcher on the thread pool and give it to the node once made. No `DriveRemoval` for a root that `Locations.IsRemote`.


### 9. Open with is looked up before the right-click menu shows

**Where:** `QuickMenu.cs:174-184` (reading `command.Children` runs every `Deferred` list as the menu is built), [FolderView.cs:2538-2580](src/Lode/FolderView.cs#L2538), `OpenWith.cs:52-75`, `MainForm.cs:725-728`.

**What happens:** on a file, building the menu calls `OpenWith.Find`: `SHAssocEnumHandlers` and an icon for each app, kept nowhere, on every right-click, though the readme's point is a menu that opens at once. With **Open with** or **Coding agent** made a button, `command.Children.Any(…)` runs it on every change of selection. What it costs was not measured.

**Fix:** a placeholder child, filled on `DropDownOpening`, and `OpenWith.Find` kept by extension.


### 10. Show more options waits half a second for each submenu with one entry

**Where:** [ShellContextMenu.cs:587-605](src/Lode/ShellContextMenu/ShellContextMenu.cs#L587).

**What happens:** a submenu holding exactly one entry, which is not Send to or Include in library, is waited on for the full 500ms, `Application.DoEvents()` and `Thread.Sleep(5)` in a loop on the UI thread, each one, each time. `DoEvents` also lets a second right-click or a key in while the first menu is still being built. Whether Windows 11's own submenus that fill late (Open with, Cast to Device) hold one placeholder at that point is unverified. If so, this is most files.

**Fix:** one deadline for the whole menu, and wait only on a submenu whose one entry has no command of the shell's.


### 11. Smaller

 * **The PATH is searched on the UI thread every 30 seconds.** `CodingAgents.FindCached` (`CodingAgents.cs:34-46`) reads the PATH and looks in every folder on it, three extensions for each agent not installed, on the next change of selection. Milliseconds as a rule, but a folder on the PATH that is on a share which does not answer stops the window. Give the old answer and look again in the background.
 * **Push and Discard look for repositories on the UI thread** ([CodeMenu.cs:62](src/Lode/CodeMenu.cs#L62) and [83](src/Lode/CodeMenu.cs#L83)), two levels of folders, where Pull's own comment says that belongs off it.
 * **Sorting by Folder makes two strings for every comparison** ([FolderView.cs](src/Lode/FolderView.cs), `Compare`), each item's folder from its path, which a sort of what a big search found does many times over. Keep each item's folder on it.
 * **Size column to fit measures every name.** `WidestCell` ([FolderView.cs:1833-1857](src/Lode/FolderView.cs#L1833)) sends the list a message for each different text, which for Name is every row of a big folder.


## Missing features


### 1. Git state

Nothing shows what branch a repository is on, whether it has changes, or how far ahead or behind it is. For a file manager for developers that is the first thing missing: the branch in the status bar or on the tab for a folder in a repository, and a column, or a mark on the icon, for the repositories listed in the code folder. Pull and Push are there with nothing to say whether either is wanted.


### 2. Run a command from the address bar

Explorer runs `cmd`, `powershell` or `wt` typed in the address bar, in the folder open, which developers use a good deal. Lode takes `cmd` for a folder of that name in the folder open, and says it can't find it. The shell's own names, `shell:downloads` and the like, are not known either.


### 3. Drives come and go only on F5

**Where:** `MainForm.cs:1366-1373` is the only caller of `FolderTree.RefreshDrives`. `MainForm.WndProc` (`1956-1978`) handles only `WM_APPCOMMAND`. Nothing in `src/Lode` handles `WM_DEVICECHANGE` or registers with `SHChangeNotify`. This PC's list is not watched: `FolderView.Watch` returns at once for it.

A USB drive plugged in, or a drive mapped, This PC's own **Map network drive** included, is not in the tree or in This PC until F5, and one taken out stays, with what was beneath it. Explorer shows both at once.

**Fix:** handle `WM_DEVICECHANGE` (`DBT_DEVICEARRIVAL`, `DBT_DEVICEREMOVECOMPLETE`), a little after the last of a burst, and refresh only the drives that changed.


### 4. Tabs can't be put in another order

**Where:** `ClosableTabControl.cs:430-436` and `499`, `MainForm.cs:1078-1081` and `1141`.

Dragging a tab along its own row does nothing, which the test `DraggingATabAlongTheRowKeepsIt` holds it to. Dropped on another window's row between two tabs, `AdoptTab` puts it at the end. Explorer and browsers do both.

**Fix:** let go inside the row over another tab: `Remove` and `Insert` the page. Give `AdoptTab` where it was dropped.


### 5. Column widths and sort order are not remembered

**Where:** [FolderView.cs:30-31](src/Lode/FolderView.cs#L30) and [71-72](src/Lode/FolderView.cs#L71) (each view's own), `Settings.cs:128` (only `HiddenColumns` is saved).

A column widened, or a list sorted by date, is so in that tab until it is closed. A new tab, and every start, is back to Name ascending at the widths built in.


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


### 13. Smaller

 * Cut items are not dimmed, as Explorer dims them.
 * What is pasted is not selected once it is there ([FolderView.cs:2186-2204](src/Lode/FolderView.cs#L2186)).
 * No Ctrl+Shift+C for **Copy as path**, which Windows 11's Explorer has.
 * No Ctrl+1 to Ctrl+9 to go to a tab by where it is.
 * Dragging with the right button gives no menu of Copy here and Move here.


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
