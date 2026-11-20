## Missing features


### 7. Search

 * By name only: nothing looks inside files.
 * With hidden items shown, the default, it goes through `.git`, as well as `node_modules`, `bin` and `obj`. An option to leave out what git ignores would make it quicker and what it finds more useful.
 * Nothing limits how much it finds. What it has found is shown less often as there comes to be more of it, so the window is not kept busy, but each showing still merges in all of it on the UI thread.



### 9. Drag and drop on the tree

**Where:** `FolderTree.cs:394-405` (`OnItemDrag` acts only on a pinned entry), `1594-1603` and `1686-1702` (`DragOver` only sets the effect).

Dragging files over the tree shows no folder as the one that would take them, opens nothing held over, and does not scroll at the edges, so where a drop will land is a guess, and a folder inside a collapsed one can't be reached. A folder under This PC can't be dragged out of the tree, to the list or another app, which Explorer's pane allows.

**Fix:** `TVIS_DROPHILITED` on the node under the pointer in `DragOver`, a timer to expand it, and a `FileDrop` drag from `OnItemDrag` for folders.


### 10. No Network in the tree

A tab on a share has nowhere in the tree unless that very folder is pinned. See [bug 23](#23-a-pinned-entry-stays-selected-and-its-folder-watched-after-going-to-a-share).


### 12. Accessibility

 * **High contrast:** the entry selected in a menu is `Blend(background, ControlText, 0.06f)` with its text in `MenuText` (`ExplorerRenderer.cs:196-241`, `ExplorerStyle.cs:126-134`). On a black high contrast menu that is #0F0F0F on black, so what is selected can't be seen. `ShadeButton` (`349-367`) and `GlyphButton`'s hover are the same. With high contrast on, fill with `SystemColors.Highlight` and draw in `HighlightText`.
 * **Names:** a `GlyphButton`'s text is a private-use character from the icon font, and it has no `AccessibleName` (`GlyphButton.cs`, `MainForm.cs:204-216`). Back, Forward, Up, Refresh, New tab and Menu have no name for a screen reader. Only the address, search and palette boxes do. Give each its tooltip's text in `CreateButton`.

