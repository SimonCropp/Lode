## Missing features


### 7. Search

 * The search box, by name, with hidden items shown, the default, goes through `.git`, as well as `node_modules`, `bin` and `obj`. Leaving out what git ignores, as Find in Files does, would make it quicker and what it finds more useful.



### 10. No Network in the tree

A tab on a share has nowhere in the tree unless that very folder is pinned. See [bug 23](#23-a-pinned-entry-stays-selected-and-its-folder-watched-after-going-to-a-share).


### 12. Accessibility

 * **High contrast:** the entry selected in a menu is `Blend(background, ControlText, 0.06f)` with its text in `MenuText` (`ExplorerRenderer.cs:196-241`, `ExplorerStyle.cs:126-134`). On a black high contrast menu that is #0F0F0F on black, so what is selected can't be seen. `ShadeButton` (`349-367`) and `GlyphButton`'s hover are the same. With high contrast on, fill with `SystemColors.Highlight` and draw in `HighlightText`.
 * **Names:** a `GlyphButton`'s text is a private-use character from the icon font, and it has no `AccessibleName` (`GlyphButton.cs`, `MainForm.cs:204-216`). Back, Forward, Up, Refresh, New tab and Menu have no name for a screen reader. Only the address, search and palette boxes do. Give each its tooltip's text in `CreateButton`.

