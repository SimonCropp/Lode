# Lode

A tabbed file manager for Windows that keeps the classic right-click menu, shipped as a [dotnet tool](https://learn.microsoft.com/en-us/dotnet/core/tools/global-tools). It can also stand in for File Explorer.

See the [full documentation](https://github.com/SimonCropp/Lode#readme) for more details.


## Installation

Needs the [.NET Desktop Runtime](https://dotnet.microsoft.com/download) 11 or later.

```
dotnet tool install -g Lode
```


## Usage

```
lode
lode D:\Code
lode D:\Code\readme.md
```

With no arguments the tabs from last time are reopened. Each folder opens in a tab. A file opens its folder with the file selected.


## Replacing File Explorer

```
lode --replace-explorer
lode --restore-explorer
```

`--replace-explorer` makes Lode what opens folders and drives (double-clicks, folder shortcuts, other apps opening a folder) and what Win+E starts. `--restore-explorer` undoes it. The same two actions are buttons under **Options** in Lode's menu.

 * It applies to the current Windows account only, and needs no administrator rights.
 * Explorer's own **Open** stays on every folder's right-click menu.
 * The taskbar's File Explorer button, and apps that start `explorer.exe` themselves or ask it to show a file selected, still open Explorer.
 * Run `lode --restore-explorer` before `dotnet tool uninstall -g Lode`. Otherwise folders keep pointing at a Lode that is no longer there.
