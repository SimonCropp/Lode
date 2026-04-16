# Meridian

A Windows File Explorer replacement built with WPF on .NET 11.

## Features

 * Tabbed browsing with independent navigation per tab
 * Back/Forward/Up navigation
 * File operations: Cut, Copy, Paste, Delete, Rename
 * Address bar with autocomplete
 * Folder tree with lazy loading
 * Column sorting (Name, Date Modified, Type, Size)
 * Shell replacement capability (register as default Windows shell)
 * Live directory monitoring via FileSystemWatcher
 * Virtualized file list for large directory performance

## Requirements

 * Windows 10 or later
 * .NET 11

## Shell Replacement

To register Meridian as the default Windows shell:

```
Meridian.exe --register-shell
```

To restore the default Windows Explorer shell:

```
Meridian.exe --unregister-shell
```
