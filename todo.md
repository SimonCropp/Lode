# Todo

Bugs, performance problems and missing features found in a review of the whole of `src/Lode` on 2026-11-18, at commit `f8a8fb2`. Line numbers are as of that commit. Those in the items marked **Reproduced** were read directly; some in the other items come from the reviewers' reports and were not each checked again.

How sure each item is:

 * **Reproduced**: a scratch program ran Lode's own code, or the Windows or .NET call it relies on, and showed it. The programs are not in the repository. One that names its assembly `Lode.Tests` sees Lode's internals, since the assembly is not signed.
 * **Read**: traced through the code, not run.
 * **Unverified**: the code is as described, but the item rests on how Windows, git or PowerShell behaves, which was not checked.

Contents:

 * [Bugs, reproduced](#bugs-reproduced)
 * [Bugs, read](#bugs-read)
 * [Bugs, unverified](#bugs-unverified)
 * [Smaller bugs](#smaller-bugs)
 * [Performance](#performance)
 * [Missing features](#missing-features)
 * [Checked and found sound](#checked-and-found-sound)


## Bugs, reproduced


### 3. One missing item makes a whole paste, delete or drop do nothing, without a word

**Where:** [ShellFileOperation.cs:88-90](src/Lode/ShellFileOperation.cs#L88) and [166-171](src/Lode/ShellFileOperation.cs#L166) (`CreateItem` throws), [FileOperations.cs:103-136](src/Lode/FileOperations.cs#L103) (`OnStaThread` puts the exception in the task), [FileOperations.cs:90-99](src/Lode/FileOperations.cs#L90) (`Rename`, on the UI thread, catches nothing). Callers that drop the task: [FolderView.cs:2199-2203](src/Lode/FolderView.cs#L2199) (`Paste`), [FolderView.cs:2209](src/Lode/FolderView.cs#L2209) (`Delete`), `FileOperations.cs:475` (`Drop`), `FolderTree.cs:113` and `1459-1463`.

**What happens:** `SHCreateItemFromParsingName` fails for a source that is not there, before anything is queued, so nothing is done for any of them. The exception ends up in a task nobody looks at.

 * Ctrl+X three files, one of which another program then deletes, Ctrl+V. Nothing moves, nothing is said, and `Clipboard.Clear()` has already run, so the cut is lost.
 * Delete in search results with one row out of date. What a search found is not watched.
 * A drop onto a tree node whose folder has gone.
 * Rename, of a search row or a tree node out of date: the same throw, but on the UI thread, out of `OnAfterLabelEdit` ([FolderView.cs:2419](src/Lode/FolderView.cs#L2419), `FolderTree.cs:1528`). `Program.cs` sets no `Application.ThreadException` handler, so it is .NET's "Unhandled exception" dialog.
 * Git purge to the Recycle Bin: every dry run comes first, which can take tens of seconds over many repositories. One listed file removed meanwhile, by a build or an IDE, and nothing is deleted anywhere, the entry is a bare "Failed", and the list of what was found is lost ([GitPurge.cs:218-262](src/Lode/GitPurge.cs#L218)). `Outright` has a smaller gap: it catches only `IOException` and `UnauthorizedAccessException`.

The undo path already leaves out what has gone, for this reason: see the comments on `Move` and `Recycle` in `UndoHistory.cs`.

**Reproduced:** `ShellFileOperation.Run` copying a file that existed and one that did not into a folder. It threw `FileNotFoundException` (0x80070002) and the folder was left empty.

**Fix:** in `Run`, leave out sources that are not there and say so through `Outcome`. Catch in `FileOperations.Rename`. Have `Paste`, `Delete` and `Drop` look at the task and show its message. Clear the clipboard only once a move has worked.


### 4. New window opens titled "Lode" with the address bar on This PC

**Where:** [MainForm.cs:1464-1474](src/Lode/MainForm.cs#L1464) (`NewWindow`), [MainForm.cs:929-960](src/Lode/MainForm.cs#L929) (`AddTab`), [MainForm.cs:764](src/Lode/MainForm.cs#L764).

**What happens:** the new window's tab control has no handle when `AddTab` runs. `OpenAt` raises `PathChanged` while `ActiveView` is still null, so only `Retitle` runs, which sets no title without an active view. `tabs.SelectedTab = page` then only stores the index, and neither that nor the handle being made afterwards raises `SelectedIndexChanged`. So `SyncToActiveView` is never called. `OpenInitialTabs` ([MainForm.cs:846-847](src/Lode/MainForm.cs#L846)) and `AdoptTab` ([MainForm.cs:1139-1146](src/Lode/MainForm.cs#L1139)) both call it themselves for this reason. `NewWindow` does not.

Until the first navigation the title is "Lode" and the address bar shows This PC. The tree and the Back and Forward buttons are set in the same place, so should be out of step too, though those were not looked at.

Affects Ctrl+N, **New window** in the ⋯ menu and the command palette, and the tree's **Open in new window**.

**Reproduced:** a `MainForm` made off screen, as the tests make them, and `NewWindow(path)` called on it. The new window had title `Lode`, `breadcrumb.Path` of `This PC`, and `ActiveView.CurrentPath` the folder. After `Navigate` both were right.

**Fix:** `window.SyncToActiveView()` after `AddTab` in `NewWindow`, or in `AddTab` when `activate` is set and the tab control has no handle. No test covers `NewWindow`.


### 5. Show more options can lose a shell extension's submenu

**Where:** [ShellContextMenu.cs:694-711](src/Lode/ShellContextMenu/ShellContextMenu.cs#L694) (`IsSeparator`, `TidySeparators`).

**What happens:** for an entry that opens a submenu, `GetMenuState` returns the flags in the low byte and how many entries the submenu has in the high byte. `MF_SEPARATOR` is 0x800, bit 3 of that count. So a submenu of 8 to 15 entries, or 24 to 31, or 40 to 47, reads as a separator. `TidySeparators` then deletes it when it is first, last, or directly above a separator or another such submenu. It also deletes a real separator directly above one.

Likely to be hit by TortoiseGit's and TortoiseSVN's submenus, which add a separator after themselves and whose length changes by folder, so it comes and goes, and by 7-Zip's cascaded menu on an archive.

**Reproduced:** menus built with a submenu of 1, 7, 8, 12, 15, 16 and 24 entries. `GetMenuState` gave 0x0110, 0x0710, 0x0810, 0x0C10, 0x0F10, 0x1010 and 0x1810: a "separator" at 8, 12, 15 and 24.

**Fix:** a separator only where `GetSubMenu(menu, position)` is zero, or `MF_POPUP` (0x10) is clear, or ask `GetMenuItemInfo` with `MIIM_FTYPE`. The tests only check what is absent, so nothing catches this.


### 6. A relative path typed in the address bar is taken from C:\Windows\System32

**Where:** [MainForm.cs:1606](src/Lode/MainForm.cs#L1606) (`view.Navigate(addressBox.Text)`), [Locations.cs:152-188](src/Lode/Locations.cs#L152) (`Normalize`, `Path.GetFullPath` at 173), [Program.cs:66](src/Lode/Program.cs#L66) (`Environment.CurrentDirectory = Environment.SystemDirectory`).

**What happens:** `Normalize` makes a path full from the process's working directory, which `Program.Main` sets to System32, rightly, so Lode never holds a folder open. The command line is made full in the launch that knows where it is from. The address bar is not.

**Reproduced:** `Locations.Normalize` with the working directory `Program.Main` sets:

| Typed | Becomes |
|-|-|
| `src` | `C:\WINDOWS\system32\src`, so "Lode can't find 'src'" |
| `..` | `C:\WINDOWS`, a folder, so the tab goes there |
| `drivers` | `C:\WINDOWS\system32\drivers`, a folder, so the tab goes there |
| `\Code` | `C:\Code`, whichever drive the tab is on |
| `cmd.exe` | `C:\WINDOWS\system32\cmd.exe`, a file, which `Go` opens |
| `cmd` | `C:\WINDOWS\system32\cmd`, not found |
| `shell:downloads` | `C:\WINDOWS\system32\shell:downloads`, not found |

**Fix:** where the tab shows a real folder, make what is typed full from it: `Path.GetFullPath(text, view.CurrentPath)` for input that is not rooted. `LocationsTests.Normalize` has no relative input. See also [Run a command from the address bar](#2-run-a-command-from-the-address-bar).


### 7. Going to the folder already shown drops Forward and adds to the history

**Where:** [FolderView.cs:447-496](src/Lode/FolderView.cs#L447) (`Go`), [FolderView.cs:621-636](src/Lode/FolderView.cs#L621) (`Open`). Reached from the breadcrumb ([MainForm.cs:156-160](src/Lode/MainForm.cs#L156)) and the address bar ([MainForm.cs:1606](src/Lode/MainForm.cs#L1606)).

**What happens:** `Go` does not ask whether the target is where the view already is. `Reveal` does. So clicking the last folder in the breadcrumb, or Ctrl+L then Enter, cuts off what is forward in the history, adds the folder a second time, so Back "goes" to the same folder, and reads it afresh, losing the selection, how far down it was scrolled, and the filter.

**Reproduced:** a view taken to `Projects`, then `Docs`, then Back: at `Projects`, `CanGoForward` true. `Navigate(Projects)`: `CanGoForward` false. `GoBack`: still at `Projects`.

**Fix:** in `Go`, with no `position` and `Locations.Same(normalized, CurrentPath)`, `Reload()` instead. `FolderViewTests.History` does not cover it.


## Bugs, read


### 8. Windows pile up in the tray

**Where:** [WindowContext.cs:130-151](src/Lode/WindowContext.cs#L130) (`OnLaunched`), [MainForm.cs:914-926](src/Lode/MainForm.cs#L914) (`OnFormClosing`), [WindowContext.cs:185-199](src/Lode/WindowContext.cs#L185) (`ShowWindows`).

**What happens:** close the only window, and it hides (W1). Another app opens a folder: no window is showing, so `OnLaunched` opens a new one (W2), as the readme says, and W1 stays hidden. Close W2: no other window is showing, so it is "the last visible" and hides too. Every open and close from then on adds another hidden window. Nothing but **Exit** closes one.

With Lode standing in for Explorer that is the ordinary day. Then a click on the tray icon, or Win+E, brings every one of them back at once, and each is rebuilt on every change to the settings or the pins meanwhile.

**Fix:** keep one window in the tray at most. Either open handed-over folders in the hidden window and show it, or close for real a window that is closed while another is already hidden. No test covers it.


### 9. Exit restores the window used longest ago, and forgets Maximized for a minimized window

**Where:** [WindowContext.cs:201-212](src/Lode/WindowContext.cs#L201) (`Exit`), [WindowContext.cs:107-123](src/Lode/WindowContext.cs#L107) (`Track` keeps `windows` most recent first), [MainForm.cs:897-912](src/Lode/MainForm.cs#L897).

**What happens:**

 * `Exit` closes the windows in list order, most recently used first, and each close writes its tabs and bounds to the settings. The last to write is the one used longest ago. The readme says "the last one closed is what the next start restores", which after **Exit**, or **Update and restart** ("starts Lode again with the tabs it had"), is the wrong one. With item 8 it can be a window hidden days before.
 * Maximize, minimize, then exit from the tray or shut Windows down. `WindowState` is `Minimized`, so `settings.Maximized` is saved false ([MainForm.cs:905](src/Lode/MainForm.cs#L905)) and the next start is not maximized.

**Fix:** close in reverse in `Exit`, as `ShowWindows` already goes through them. Take maximized from `GetWindowPlacement` (`WPF_RESTORETOMAXIMIZED`), or keep the last state that was not minimized, in `OnResize`.


### 10. Light and dark mix when Windows changes theme while Lode runs

**Where:** [ExplorerStyle.cs:29](src/Lode/ExplorerStyle.cs#L29) (`IsDark => Application.IsDarkModeEnabled`), `Pick` at `ExplorerStyle.cs:136-149`.

**What happens:** with the theme the same as Windows', the default, `Application.IsDarkModeEnabled` reads `AppsUseLightTheme` from the registry each time it is asked (Windows Forms 11.0.0, decompiled). `SystemColors` are set once, in `SetColorMode`. So when Windows goes from light to dark with Lode running, by a schedule say, every `Pick` turns to the dark colours while `SystemColors.MenuText` and `ControlText`, and the glyph bitmaps already made, stay as they were: black on dark grey in menus. Dark to light gives white on off-white. The readme says the theme is taken on when Lode next starts.

**Fix:** read `Application.IsDarkModeEnabled` once in `Use`, after `SetColorMode`, and have `IsDark` return that. See also [performance item 2](#2-isdark-reads-the-registry-every-time-it-is-asked).


### 11. A drive that could not be read once can never be expanded

**Where:** `FolderTree.cs:682-696` (`LoadChildren`), `700-728` (`ExpandOnceRead`), `822-827` (`ShowExpander`).

**What happens:** when the first read of a folder fails, `ReadSubfolders` returning null for a drive that is not ready or a mapped drive that can't be reached, `LoadChildren` clears the placeholder and watches the node. With no children it has no expander, so `OnBeforeExpand` never reads it again and `OnAfterCollapse` never lets go of it. A read of its parent skips it, as it is watched. `RefreshDrives` leaves it alone while the same drives are there, so F5 does not help.

 * Mapped drive Z:, off the VPN. Click its expander, or open a tab beneath it. The expander goes. Back on the VPN, Z: can't be expanded, and `SelectPath("Z:\x")` settles on Z:, until a pin, a change of options or a restart rebuilds the tree.
 * An empty card reader that later gets a card.

`SyncAsync` already refuses a null read (`FolderTree.cs:742-746`). The first load does not.

**Fix:** on a null read, keep the placeholder and do not watch, so the next expand reads again.


### 12. The tree misses folders made while their parent was collapsed

**Where:** `FolderTree.cs:926-945` (`WatchSelected` watches without reading), `1005-1011` (`OnBeforeExpand` only reads a node that is not watched), `1300-1307` (`Walk` breaks at a missing child before expanding).

**What happens:** a node's children are as they were when it was last read. A collapsed node is not watched, and a leaf's "no subfolders" is from its parent's last read. Selecting such a node watches it but does not read it, and as it is now watched, expanding it by hand does not read it either.

 * `D:\Code` was expanded once, then collapsed. `git clone` makes `D:\Code\New`. Open `D:\Code`, then `New`, in the list. The tree stays on `Code`, and expanding `Code` by hand does not show `New`.
 * `D:\Code\Proj` had no subfolders when `Code` was read. A tool makes `Proj\src`. Open `Proj`, then `src`. The tree stays on `Proj`, with no expander.

Both last until some other folder is made, deleted or renamed directly in that parent. F5 does not read the tree's folders again: `MainForm.Reload` only calls `RefreshDrives`.

**Fix:** where `WatchSelected` starts a watch on a node already read, also `Track(SyncAsync([node]))`. Or in `Walk`, where the child is missing and the folder was not read in this pass, read it before settling.


### 13. Pull, Push and Discard on a folder inside a repository find no repositories

**Where:** [CodeCommands.cs:14-25](src/Lode/CodeCommands.cs#L14) (`FindRepos`), [CodeMenu.cs:16-22](src/Lode/CodeMenu.cs#L16).

**What happens:** the Git menu is offered for every folder under the code folder. `FindRepos` looks at the folder and two levels beneath it, never above. So on `D:\Code\Lode\src` the three report "No git repositories". Purge walks up, with `InRepo` ([GitPurge.cs:92-108](src/Lode/GitPurge.cs#L92)).

**Fix:** walk up to the code folder the same way.


### 14. Discard leaves staged changes and untracked folders

**Where:** [CodeCommands.cs:126-143](src/Lode/CodeCommands.cs#L126), prompt at [CodeMenu.cs:92](src/Lode/CodeMenu.cs#L92).

**What happens:** the prompt says "Discard every uncommitted change, and delete untracked files". `git restore .` restores the working tree from the index, so what is staged stays. `git clean -f` without `-d` leaves untracked folders. After `git add x`, Discard reports done, and a later Push (`commit -am`) commits and pushes the change that was thought gone. A repository with no tracked files fails at `restore .` and is never cleaned.

It may be meant: the class says each command does what the matching script (discard.ps1) does. `DiscardRestoresAndCleans` covers only an unstaged edit and an untracked file at the top.

**Fix:** `git reset --hard` then `git clean -fd`, or word the prompt for what it does.


### 15. A git command can hang for good, and can't be cancelled

**Where:** [CodeCommands.cs:161-194](src/Lode/CodeCommands.cs#L161) (`Git`, `process.WaitForExit()` with no limit), [CodeCommands.cs:147-159](src/Lode/CodeCommands.cs#L147) (`ForEach`), `ActionsPanel.cs:347`.

**What happens:** `GIT_TERMINAL_PROMPT=0` stops only git's own prompt. Pulling over SSH with a key that has a passphrase and no agent, or a host whose key is not known, ssh asks on the hidden console, where nobody can answer. A connection that stalls does the same. The entry stays "Running…", the repositories after it are never reached, since they go one at a time, and **Remove** is off for an entry that is running.

That there is no limit and no cancel is read. That ssh waits, rather than fails, under `CreateNoWindow` is unverified.

**Fix:** where the user has set none, `GIT_SSH_COMMAND` of `ssh -o BatchMode=yes`. Pass a cancellation token into `Git` that calls `process.Kill(entireProcessTree: true)`, and offer **Cancel** on an entry that is running.


### 16. Exit, and Update and restart, pay no heed to actions under way

**Where:** `OptionsForm.cs:632` and `676-690`, `MainForm.cs:1493-1496`, [WindowContext.cs:201-212](src/Lode/WindowContext.cs#L201), `ToolUpdate.cs:113-116`.

**What happens:** nothing asks `ActionLog` whether anything is running. Update NuGets, or a purge that deletes outright, is `Task.Run` work, which dies when `Main` returns: part way through writing a props file, or half way through a delete, with nothing said. A purge to the Recycle Bin is on a foreground thread (`FileOperations.OnStaThread` never sets `IsBackground`), so the process stays, and the update script, having waited 30 seconds, stops it by force.

**Fix:** with any entry running, ask before exiting, or wait for it, and turn **Update and restart** off meanwhile.


### 17. Replace takes another file manager's Win+E, and Restore deletes it

**Where:** [ExplorerReplacement.cs:207-213](src/Lode/ExplorerReplacement.cs#L207) (`Replace`), [ExplorerReplacement.cs:272-276](src/Lode/ExplorerReplacement.cs#L272) (`Restore`).

**What happens:** `Replace` writes its command to `CLSID\{52205fd8-…}\shell\opennewwindow\command` and marks the key `CreatedBy=Lode` whatever was there. Where another file manager had it (Files uses this key), `Restore` then deletes the whole key, and Win+E falls to Explorer rather than back to the other tool. The folder verbs and the explore verb do keep and put back what was there. The comment on `ownerName` says the mark is "so Restore never removes another tool's". `RestoreLeavesAnotherToolsWinEAlone` covers only a Restore with no Replace before it.

**Fix:** `Keep` and `PutBack`, as the other keys have, on the Win+E command and its `DelegateExecute`, where the key is there and not Lode's.


### 18. Reopening a closed tab whose folder has gone leaves an empty tab named This PC

**Where:** [MainForm.cs:1053-1063](src/Lode/MainForm.cs#L1053) (`ReopenClosedTab`), [FolderView.cs:483-495](src/Lode/FolderView.cs#L483).

**What happens:** close a tab on `D:\Temp\x`, delete the folder or take its drive out, press Ctrl+Shift+T. "Lode can't find…" shows. The view never reaches `Open`, so `CurrentPath` is still what it starts as, This PC, and it is not stale: a tab titled This PC listing nothing, not even drives, until F5. The closed tab is used up. `OpenInitialTabs` and `Openable` check that paths are there. `ReopenClosedTab` does not.

**Fix:** skip what has gone, or go to the nearest folder still there, as a tab does when its folder is deleted.


### 19. Options is taller than a small or scaled screen, and can't scroll

**Where:** `OptionsForm.cs:298-304` (`FixedDialog`, `AutoSize`, `GrowAndShrink`, no `AutoScroll` anywhere in the file), `721-829` (a row for each of 28 actions), `1094-1107` (`PageHost` gives every page the height of the tallest).

**What happens:** `OptionsFormTests.ActionsPage.verified.png` is 804 by 856. If that is at 100%, a 1920 by 1080 screen at 150% has about 680 of those pixels to give, and one at 125% about 820, so the last rows and **Close** are off the bottom. Each action added is another 22. That it scales in proportion was not run.

`OptionsForm.cs:751`, `Size = new(iconSize + 4, iconSize + 4)`, may scale twice: `iconSize` is already in device pixels (line 743), and `AutoScaleMode.Dpi` scales it again. Unverified.

**Fix:** the actions table in a panel with `AutoScroll`, no taller than the screen's working area.


### 20. Undo on a drive with no Recycle Bin says "0 of 1" and stays

**Where:** [UndoHistory.cs:406-419](src/Lode/UndoHistory.cs#L406) (`Recycle`), `445-458` (`Counted`), `489-502` (`Pairs` keeps only a change with a result).

**What happens:** undoing a copy, or a new folder, file or shortcut, on a USB stick or a share. The shell asks to delete for good, the user agrees, the item goes, and its change has no result, as it is in no Recycle Bin. `recycled.Count` is 0, so it counts as waiting: "Lode sent 0 of 1 items to the Recycle Bin.", with "Undo copy" still next. A second Ctrl+Z says "1 of 1 items are no longer there…" and only then drops it.

**Fix:** have `IFileActions.Recycle` report what was deleted outright as well, and count those as undone and not to be redone.


### 21. Rename on the tree's Show more options does nothing

**Where:** `ShellContextMenu.cs:65` and `87` (every item menu and Quick access menu is shown with `canRenameFlag`), `FolderTree.cs:1576-1591` (`HandleVerb` handles only "open").

**What happens:** `CMF_CANRENAME` tells the shell that the host renames. `FolderView` does (`HandleItemVerb`). The tree does not. Shift+right-click a folder, a drive or a Pinned entry in the tree, then **Rename**: the verb goes to the shell, with no view to do it. The readme says drives, and Pinned and Recent entries, can't be renamed, yet it is offered. What the shell does with the verb then is unverified.

**Fix:** let the caller say whether it renames, and have the tree call `BeginRename(node)` for "rename" where its own menu would allow it.


### 22. Send to stays on the menu when it lists two or more drives

**Where:** [ShellContextMenu.cs:671-684](src/Lode/ShellContextMenu/ShellContextMenu.cs#L671) (`Lists`).

**What happens:** `Lists` allows one entry that is not in the SendTo folder. Windows adds removable and mapped drives to Send to, which are not in that folder. With two, a USB stick and a DVD drive or two mapped drives, the test fails and Send to stays, where the readme says it is left off. That Windows adds them is from memory.

**Fix:** leave out entries ending in a drive, "(X:)", before comparing, or go by most rather than all but one.


### 23. A Pinned entry stays selected, and its folder watched, after going to a share

**Where:** `FolderTree.cs:1217-1222` (`SelectAsync`), against `SelectShowing` at `1151-1156`, which handles the same case.

**What happens:** with **Expand the folder tree to the open folder** on, a folder that is in no drive and not pinned, any UNC path, has `Walk` return null. Setting `SelectedNode` to null raises no `AfterSelect`, so `ShowShortcutsSelected(null)` and `WatchSelected(null)` never run. A tab on pinned `D:\Code` that goes to `\\nas\share\x` leaves **Code** looking selected under Pinned, and `D:\Code` watched.

**Fix:** the `if (node == null)` block from `SelectShowing`, in `SelectAsync` too.


### 24. The palette's Terminal, Open in Claude and Coding agent ignore a selected folder

**Where:** `MainForm.cs:1734-1745` and `1777-1779`.

**What happens:** the window's own commands are added first, and `DistinctBy` on the name keeps them. So with a folder selected, **Terminal** from the palette opens in the tab's folder, where the right-click menu and the actions bar open it in the folder selected. The same for **Terminal (admin)**, **Open in Claude** and **Coding agent**. The readme says the palette lists "whatever the right-click menu offers for the selection", so this looks unmeant, but it may be.

**Fix:** where the selection has one of these, let it stand in for the window's.


### 25. A command prompt opened without Windows Terminal breaks on & in a path

**Where:** [FileOperations.cs:304-339](src/Lode/FileOperations.cs#L304).

**What happens:** only where `wt.exe` can't be started (Windows 10, Server). `ArgumentList` quotes an argument only for a space or a quote, so `D:\Code\R&D` gives `cmd.exe /k cd /d D:\Code\R&D`. cmd runs `cd /d D:\Code\R`, then `D`. An admin prompt ends up in System32, and a coding agent after `&&` never starts. The `wt.exe` path is fine: semicolons are escaped, and tested.

**Fix:** build `Arguments` by hand: `/k cd /d "<directory>"`.


## Bugs, unverified


### 26. What is run from Show more options starts in C:\Windows\System32

**Where:** [ShellContextMenu.cs:855-879](src/Lode/ShellContextMenu/ShellContextMenu.cs#L855) (`Invoke` sets no `Directory` or `DirectoryUnicode`), [Program.cs:66](src/Lode/Program.cs#L66).

**What happens:** **Show more options**, then **Open** on `build.cmd`, or **Run with PowerShell**, or any verb an app adds. With no directory given, the program started would get Lode's own, System32, so relative paths in a script go there. Lode's own Open sets `WorkingDirectory` to the file's folder (`FileOperations.cs:208`), and Explorer passes the folder.

**Unverified:** that the shell falls back to the caller's working directory when it is given none. A `.cmd` holding `cd & pause`, opened from the classic menu, would show it.

**Fix:** pass the items' folder into `Invoke` and set both fields, ANSI and Unicode, with the masks for them.


### 27. Safely remove may be blocked the second time a drive is plugged in

**Where:** `DriveRemoval.cs:41-60` (`Subscribe` reuses `registrations[root]`), `138-193`, [FolderWatcher.cs:93-124](src/Lode/FolderWatcher.cs#L93).

**What happens:** a notification registered on a device's handle is for that device. Once the device has been removed it is dead, but it stays in `registrations` while anything is subscribed, and the tree keeps a drive's nodes and their watchers after the drive goes, as nothing refreshes drives by itself ([missing feature 3](#3-drives-come-and-go-only-on-f5)). So: browse USB stick E:, **Safely remove**, plug it in again. The old watcher is never told it can watch again. A new one, from browsing E: again, subscribes to the dead registration, never hears that the new device is about to go, and holds its handle. The second **Safely remove** fails with "device is currently in use", which the readme says can't happen.

**Unverified:** that Windows sends nothing to the old registration for the device plugged in again. Microsoft's guidance is to unregister once a device is removed.

**Fix:** once removal is complete, take the registration out of the dictionary and unregister it, off the callback's thread, so the next `Subscribe` registers afresh.


### 28. A menu slow to build gets an extension refused for the session, cancelled or not

**Where:** [ShellExtensionGuard.cs:48-66](src/Lode/ShellExtensionGuard.cs#L48) (`CancelCall`).

**What happens:** the 3 seconds run from when the guard is made, not from when a call got stuck, and whatever class was activated last is added to `refused` before `CoCancelCall`, whose result is not looked at. Every activation is recorded, in-process ones and the helper classes an extension makes included. So a first, cold, build of a menu that takes over 3 seconds, or a slow in-process extension on a share, has nothing to cancel, yet the last class activated is refused until Lode restarts, and its entries are gone from every classic menu. The readme says extensions loaded into Lode can't be cut off. It does not say they are refused. `ShellExtensionGuardTests` has this in it: it calls `CancelCall()` with no call waiting and expects the refusal.

**Unverified:** that a build ever takes 3 seconds.

**Fix:** refuse only where `CoCancelCall` returns `S_OK`, and time from the last activation.


### 29. A typographic apostrophe in a path ends a PowerShell string early

**Where:** `PowerShell.cs:19-20` (`Quote` doubles only `'`). Used at `PowerShellScripts.cs:54`, `DefenderExclusion.cs:90-91`, `ToolUpdate.cs:109-120`.

**What happens:** PowerShell also takes U+2018, U+2019, U+201A and U+201B to end a single-quoted string. **Run** on `…\OneDrive - St Mary’s College\build.ps1` would end the string at the apostrophe, and the script fail to parse. The same for the code folder in the Defender script, which runs elevated. In the update script it would mean Lode has gone and is never started again. The `Script` test has only `O'Brien`.

**Unverified:** not run. From how PowerShell is documented to read those characters.

**Fix:** double all four as well.


### 30. Two launches at once can both become the running copy

**Where:** [Program.cs:57-78](src/Lode/Program.cs#L57), [WindowContext.cs:62-64](src/Lode/WindowContext.cs#L62), [SingleInstance.cs:41-105](src/Lode/SingleInstance.cs#L41).

**What happens:** the mutex is taken at `Program.cs:57`, but `Listen` runs only after the settings, the places, the whole first window and `first.Show()`. A second launch meanwhile, two folders opened together or a double-click done twice, waits 3 seconds for the pipe (`client.Connect(3000)`), and then carries on by itself: two copies, two tray icons, both writing `settings.json`. The copy that does not have the pipe then gets `UnauthorizedAccessException` from `new NamedPipeServerStream(…, 1, …)`. Only `IOException` and `OperationCanceledException` are caught, so its listening ends for good.

**Unverified:** that a cold start takes over 3 seconds to reach `Listen`.

**Fix:** listen straight after taking the mutex, and hold what is handed over until the context is there. Catch everything in the loop, with a short wait.


### 31. Purge and Push go by a .git being there, where git decides for itself

**Where:** [CodeCommands.cs:76-80](src/Lode/CodeCommands.cs#L76) (`IsRepo`), [CodeCommands.cs:161-194](src/Lode/CodeCommands.cs#L161) (`Git`), [GitPurge.cs:147-156](src/Lode/GitPurge.cs#L147).

**What happens:** with the home folder a repository, and `Code\Foo\.git` a folder git won't take (empty, or its `HEAD` zeroed in a crash), Lode counts Foo a repository, and git walks past it up to the home one, where all of Foo is untracked. The dry run then lists all of Foo, and Purge deletes it. Push there would commit to, and push, the home repository. The same if Lode is started with `GIT_DIR` or `GIT_WORK_TREE` set, from a git alias or hook: only `GIT_TERMINAL_PROMPT` is set.

**Unverified:** that git goes on upward past a `.git` folder it won't take. Unlikely to be met either way.

**Fix:** in `Git`, set `GIT_CEILING_DIRECTORIES` to the folder above the code folder, and take `GIT_DIR`, `GIT_WORK_TREE` and `GIT_INDEX_FILE` out of the environment.


### 32. Purge reads git's English

**Where:** [GitPurge.cs:164-194](src/Lode/GitPurge.cs#L164) (`const string prefix = "Would remove "`).

**What happens:** a git that words its dry run in another language would match nothing, and the entry would say "Nothing to purge", in green.

**Unverified:** that Git for Windows translates what it prints with `LANG` not set. It may ship no translations at all.

**Fix:** `LC_ALL=C` in `Git`, which costs nothing.


## Smaller bugs

All read, none run.

 * **A pending selection outlives a failed Reveal.** `Reveal` sets `pendingSelection`, then `Navigate` can end in "can't find" ([FolderView.cs:929-947](src/Lode/FolderView.cs#L929)). The next `Open` keeps it (`pendingSelection ??=`, [FolderView.cs:642](src/Lode/FolderView.cs#L642)), so the folder that leads back is not selected that once.
 * **This PC shows an error if a drive throws anything but `IOException`.** `DriveEntry` catches only that ([FolderView.cs:1100-1118](src/Lode/FolderView.cs#L1100)), and `Task.WaitAll` ([FolderView.cs:1138](src/Lode/FolderView.cs#L1138)) throws for a task that failed in time, so `UnauthorizedAccessException` from `TotalSize` makes the whole listing an error.
 * **A tab drag goes by index.** `ClosableTabControl` remembers the tab pressed as `pressedTab`, an index. A tab closed with Ctrl+W while dragging, and letting go moves the wrong one. Hold the `TabPage`.
 * **Redo is offered after a newer change.** A `Record` that lands while an undo is running clears redo, then the undo, done, adds its entry back (`UndoHistory.cs:213-256`).
 * **An undo that can never work blocks everything older.** A failure that will not clear, access denied say, is waiting for good (`UndoHistory.cs:292-295`, `452-455`).
 * **Restore from the Recycle Bin is counted done when the item is found**, whatever became of the undelete ([RecycleBin.cs:25-32](src/Lode/RecycleBin.cs#L25)). Delete `a.txt`, another program makes a new `a.txt`, Ctrl+Z, skip at the shell's prompt: the entry still goes to redo, and Ctrl+Y recycles the other `a.txt`. How the verb behaves on a conflict is unverified. The loop also goes through the whole Recycle Bin, by `dynamic`, without stopping once it has all it wants.
 * **A paste with one source already in the folder renames every clash.** `RenameOnCollision` is set for the whole paste ([FileOperations.cs:27-31](src/Lode/FileOperations.cs#L27)), so items of the same name from other folders, pasted from a search say, become "- Copy" without the prompt.
 * **A recently used `.zip` on a share can show under Recent as a folder.** It is a folder and in the file system to the shell, and skips the `Directory.Exists` check (`Places.cs:153-165`).
 * **A folder the tree loads on the way to one that is not there stays watched**, not expanded, until the tree is rebuilt (`FolderTree.cs:1296-1307`).
 * **An app's name loses its ampersand in Lode's menus.** `QuickMenu` gives names to `ToolStripMenuItem` as they are, so "Snip & Sketch" shows as "Snip  Sketch".
 * **Delete, New and Create shortcut chosen from the classic menu** go through the shell alone, so Lode's Ctrl+Z never hears of them.
 * **Rows and icons keep the first monitor's scale.** `PaddedIcons` sizes its images once, from the system image list ([ShellListView.cs:30-42](src/Lode/ShellListView.cs#L30)), and Lode is `PerMonitorV2` ([Program.cs:15](src/Lode/Program.cs#L15)), so a window moved to a monitor at another scale keeps the first one's row height.


## Performance


### 1. A search merges everything found so far, on the UI thread, four times a second

**Where:** [FolderView.cs:1291-1316](src/Lode/FolderView.cs#L1291) (`AddFound`), [FolderView.cs:2011-2025](src/Lode/FolderView.cs#L2011) (`ApplyFilter`), [FolderView.cs:1479-1581](src/Lode/FolderView.cs#L1479) (`Populate`), `searchBatch` at [FolderView.cs:84](src/Lode/FolderView.cs#L84).

**What happens:** every 250ms the search hands over a batch. `AddFound` merges it into all that was found before, and `Populate` then merges the folder's own matches into that again, compares the lot with what is displayed, gives the list its rows again and puts the selection back. Each is as long as everything found so far, and each comparison is a call to `StrCmpLogicalW`. Nothing limits how much is found.

**Measured:** the merge by itself, of sorted names by `StrCmpLogicalW`, with 2,000 more: 28ms into 100,000, 92ms into 500,000, 202ms into 1,000,000. Lode does two a batch, and the rest of `Populate` on top. So `*.cs` across a big code folder is jerky, and one letter typed at `C:\` near enough stops the window.

**Fix:** leave longer between batches as more is found, by the rule a folder's reads already go by (`WaitAfter`, [FolderView.cs:1070-1098](src/Lode/FolderView.cs#L1070)), or merge off the UI thread and hand over the merged list. Sorting by Folder also makes two strings for every comparison ([FolderView.cs:2049-2051](src/Lode/FolderView.cs#L2049)).


### 2. IsDark reads the registry every time it is asked

**Where:** [ExplorerStyle.cs:29](src/Lode/ExplorerStyle.cs#L29).

**Measured:** 11.2 microseconds a call with `SystemColorMode.System`, the default theme. Every colour, in every paint, asks. Fixed by what fixes [bug 10](#10-light-and-dark-mix-when-windows-changes-theme-while-lode-runs).


### 3. Icons of programs and shortcuts are read on the UI thread as rows come into view

**Where:** [FolderView.cs:1964-1996](src/Lode/FolderView.cs#L1964) (`CreateItem`), [ShellIcons.cs:182-214](src/Lode/ShellIcons.cs#L182) (`GetIcon`), [ShellIcons.cs:294-325](src/Lode/ShellIcons.cs#L294) (`GetActualIcon`, `GetFileIcon`).

**What happens:** for `.exe`, `.lnk`, `.ico`, `.cur`, `.ani`, `.url`, `.scr` and `.appref-ms`, the first row made for a file asks the shell for that file's own icon, `SHGetFileInfo` on its path, which reads the file. It is kept after that, by path and date. So the first scroll through a folder of programs, or of search results full of them, reads each on the UI thread, over the network for a folder on a share. A shortcut to somewhere on the network that can't be reached may hold the window for as long as the network takes to say so. That last is unverified.

**Fix:** give the row the icon its extension implies, ask for the file's own off the UI thread, and make the row again once it is known, as `DriveDescribed` does for drives ([FolderView.cs:1909-1919](src/Lode/FolderView.cs#L1909)).


### 4. One tick in Options, or one pin, reads every window's folder again and rebuilds every tree

**Where:** `OptionsForm.cs:787-791` and `800-807` (`SettingsChanged?.Invoke()`), `MainForm.cs:1484` and `1538-1566` (`RebuildActionsBar(); view.Reload(); RebuildTree();`), `FolderTree.cs:181-206` (`Rebuild`), `FolderTree.cs:527-541` (`LoadDrives`), `MainForm.cs:113` (`places.Changed`).

**What happens:** ticking **Button** for one action reads the folder showing in every window again, hidden ones too, starts any search over, and rebuilds every tree from `Nodes.Clear()`: every watcher let go of, every branch collapsed, and the way down to the open folder read again from the drive. The same on every pin, unpin and reorder of a pin, and at startup when Quick access differs from what was saved.

With **Expand the folder tree to the open folder** off, nothing is expanded again, so one pin collapses a tree the user laid out, which is what that option is there to prevent. `LoadDrives` does the same when the drives have changed, and `Reload` does not select the tab's folder in the tree afterwards.

**Fix:** tell actions, listing and tree apart, so a tick on the Actions page only calls `RebuildActionsBar`. On `places.Changed`, replace only Pinned and Recent. Have `Rebuild` keep what was expanded, and `LoadDrives` add and remove only the drives that came and went.


### 5. Every tab's icon is asked of the shell on every navigation

**Where:** [MainForm.cs:1204-1245](src/Lode/MainForm.cs#L1204) (`Retitle`, `TabIcon`), [ShellIcons.cs:383-398](src/Lode/ShellIcons.cs#L383) (`GetPlaceIcon`), [ShellIcons.cs:294-299](src/Lode/ShellIcons.cs#L294).

**What happens:** `Retitle` runs on every change of tab, every navigation and every background tab's `PathChanged`, and each time asks `SHGetFileInfo` for each tab's path, on the UI thread, with nothing kept. In `OpenInitialTabs` every `AddTab` raises `PathChanged`, so restoring N tabs makes N(N+1)/2 calls before the window shows: about 230 for 20 tabs, 1,300 for 50. What one call costs was not measured.

**Fix:** keep the icon for each path, forgotten on F5, and retitle once after the loop in `OpenInitialTabs`.


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
 * **Size column to fit measures every name.** `WidestCell` ([FolderView.cs:1833-1857](src/Lode/FolderView.cs#L1833)) sends the list a message for each different text, which for Name is every row of a big folder.


## Missing features


### 1. Git state

Nothing shows what branch a repository is on, whether it has changes, or how far ahead or behind it is. For a file manager for developers that is the first thing missing: the branch in the status bar or on the tab for a folder in a repository, and a column, or a mark on the icon, for the repositories listed in the code folder. Pull and Push are there with nothing to say whether either is wanted.


### 2. Run a command from the address bar

Explorer runs `cmd`, `powershell` or `wt` typed in the address bar, in the folder open, which developers use a good deal. Lode says it can't find `cmd` (see [bug 6](#6-a-relative-path-typed-in-the-address-bar-is-taken-from-cwindowssystem32)). The shell's own names, `shell:downloads` and the like, are not known either.


### 3. Drives come and go only on F5

**Where:** `MainForm.cs:1366-1373` is the only caller of `FolderTree.RefreshDrives`. `MainForm.WndProc` (`1956-1978`) handles only `WM_APPCOMMAND`. Nothing in `src/Lode` handles `WM_DEVICECHANGE` or registers with `SHChangeNotify`. This PC's list is not watched: `FolderView.Watch` returns at once for it.

A USB drive plugged in, or a drive mapped, This PC's own **Map network drive** included, is not in the tree or in This PC until F5, and one taken out stays, with what was beneath it. Explorer shows both at once. This is also what [bug 27](#27-safely-remove-may-be-blocked-the-second-time-a-drive-is-plugged-in) rests on.

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
 * Nothing limits how much it finds: see [performance item 1](#1-a-search-merges-everything-found-so-far-on-the-ui-thread-four-times-a-second).


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
 * A git action can't be cancelled: see [bug 15](#15-a-git-command-can-hang-for-good-and-cant-be-cancelled).


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
