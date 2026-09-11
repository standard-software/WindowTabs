<img src="README_Image/LargeIcon.png" width="60" height="60" alt="icon" align="left" />

# WindowTabs

**Language:** [Japanese/日本語](README_Japanese.md)


![Tabs](README_Image/Tabs.png)


WindowTabs is a tool that extends Windows productivity by letting you manage any window through a tabbed user interface (UI).

<details>
<summary>Read more about WindowTabs</summary>

### I'd like WindowTabs to greatly enhance how you use Windows

WindowTabs is a tool that greatly enhances Windows productivity.

In an era when smartphones and tablets can handle web browsing and entertainment, using a Windows PC probably means you're doing some kind of creative work: accounting, customer support, presentations, business management, legal paperwork, electronic medical records, video editing, illustration, or software development.

If that describes your daily work and you want Windows to be easier to use, I really hope WindowTabs can help you work more comfortably and efficiently. I've developed its features over many versions with those needs in mind.

### Why a tab UI?

As you can feel from using a browser every day, a tabbed interface is intuitive and comfortable.

WindowTabs brings that same ease of use to windows across your desktop. Once you try it, I think you'll appreciate how much it helps you get work done. Switching between windows takes less effort, leaving you free to focus on the task at hand.

### Microsoft once prototyped this, but the project was abandoned

Microsoft once aimed to bring tabbed management to all windows through an experimental Windows feature called **Sets**.
It was discontinued. I don't know the exact reason, but my guess is that integrating it into the OS while preserving backward compatibility proved too difficult, and the benefits were judged too small for the complexity.

Although the project fell through, I think its vision was wonderful.
Using WindowTabs every day shows me just how comfortable switching windows can be, and how much it helps me work efficiently.
WindowTabs provides an excellent tabbed interface for almost any window without being built into the OS itself.

### About me (satoshi-yamamoto, the author of the ss_ version)

I've been a paid user of WindowTabs since before it was open-sourced, and I've always loved its tabbed interface. I've long wanted more people to experience this way of working, and I've tried many similar tools over the years.

So I'm delighted to have the opportunity to improve WindowTabs as open source and share it with others.

I keep improving WindowTabs little by little, asking myself: "How can I make working with windows more efficient for more people — in other words, how can I help more people get their work done faster?"

### How to use it

Everyone uses Windows differently, so I imagine each person will find their own useful ways to use WindowTabs. Here are some typical examples:

- Operate Chrome, Edge, and Firefox — along with each one's incognito / private windows — all as a single browser window.
- Manage multiple Excel windows, or multiple Word and PowerPoint windows, as a single office app window.

I (satoshi-yamamoto) am a software developer; in my day job I build web applications. I've built things like a browser-based drawing tool, an in-browser car navigation app, and business chat tools.

I group multiple VSCode, Visual Studio, Terminal, WinMerge, file explorer, image viewer, Excel, and browser windows with WindowTabs, snapping them to the left and right of each display. Even apps with their own tabs can be managed at the window level, creating multiple levels of tab organization. Giving tabs from the same project matching colors lets me manage a great many windows with little effort.

### What's enhanced in version ss_

In addition to tabbed window management, WindowTabs version ss_... lets you snap windows to any screen edge or move them to another display in one action. Its snapping features go beyond those built into Windows, letting you change window layouts without dragging and dropping. This is particularly useful with multiple displays.

### I'd be truly happy if you'd recommend it to people around you

If you know someone who works by switching between lots of windows, I'd be delighted if you'd suggest, "Why not give WindowTabs a try?" Some of its convenience only becomes clear when you use it yourself.

I'm building this primarily because I want it for myself. But if it can be useful to others and bring even a small positive impact to their work, that would make me very happy as a software developer. My thanks go to all of you who use it.

If you have any feedback, I'd love to hear it on GitHub Issues.

</details>

<br />

This version (ss_yyyy.mm.dd) is forked from payaneco's repository and incorporates some code implementations from leafOfTree's version. Maintained by [Satoshi Yamamoto (@standard-software)](https://github.com/standard-software).

<details>
<summary>Read more about the project history and the lineage of forks</summary>

WindowTabs was originally developed by Maurice Flanagan in 2009 and was offered as both free and paid editions at the time. The original author has since open-sourced it.

- https://github.com/mauricef/WindowTabs (404 Not Found)

Mr./Ms. redgis forked it and migrated to VS2017 / .NET 4.0.

- https://github.com/redgis/WindowTabs

Mr./Ms. medlir hosts the source code.
- https://github.com/medlir/WindowTabs

Looking at the commit log, Mossy Flanagan made the early commits.
- https://github.com/mossy-xyz

Mr./Ms. payaneco forked medlir/WindowTabs's source code.
- https://github.com/payaneco/WindowTabs
- https://github.com/payaneco/WindowTabs/network/members
- https://ja.stackoverflow.com/a/53822

Mr./Ms. leafOfTree also created a fork with various improvements:
- https://github.com/leafOfTree/WindowTabs
- https://github.com/leafOfTree/WindowTabs/network/members

</details>

## Index
- [Version](#Version)
- [Download](#Download)
- [Installation](#Installation)
- [Usage](#Usage)
- [Features](#Features)
- [Settings](#Settings)
- [Building from Source](#Building-from-Source)
- [Links](#Links)
- [License](#License)
- [Comments](#Comments)

## Version

Latest version: **ss_2026.09.12**

See [version.md](version.md) for details.


## Download

**Supported OS:** Windows 10, Windows 11

<a href="https://github.com/standard-software/WindowTabs/releases">![GitHub Downloads (all assets, all releases)](https://img.shields.io/endpoint?url=https%3A%2F%2Fraw.githubusercontent.com%2Fstandard-software%2FWindowTabs%2Fbadge-data%2Fdownloads.json)</a>

Download the installer or the zip containing the exe from the [releases](https://github.com/standard-software/WindowTabs/releases) page.

- **WtSetup.msi** - Windows Installer package with automatic installation and uninstallation support
- **WindowTabs.zip** - Portable version that can be extracted and run from any location

## Installation

### Using the MSI Installer (WtSetup.msi)

Run `WtSetup.msi` and follow the installation wizard (default: `Program Files\WindowTabs`).

### Using the Portable Version (WindowTabs.zip)

Extract `WindowTabs.zip` to your preferred location and run `WindowTabs.exe`.


## Usage

- Right-click the tray icon, select "Settings" from the menu, then choose programs to tab in the "Programs" tab.
- Drag and drop tabs to group them, and right-click for further actions.

![Task Tray Menu](README_Image/TaskTrayMenuImage.png)

![Settings Programs](README_Image/SettingsPrograms.png)

## Features

### Multi-Select Tabs

- Hold Ctrl and click tabs to add them to / remove them from the selection.
- Hold Shift and click a tab to select the range from the active tab.
- Once selected, the right-click context menu and tab drag operations act on the whole selection.

### Tab Drag and Drop
- Drag tabs to reorder within the same group.
  - Multi-select: the contiguous selected range (= active tab + adjacent selected tabs sharing the same pin state and alignment) moves together as a single block, preserving the normal Chrome-style overlap. Smart-pin auto-converts the group on zone entry; the selection persists across a successful drag.
- Drag tabs to split into a new window or link to another group.
  - Multi-select: the whole selection (active + selected tabs) travels together — either into the target group, or as a new tab group when detached.

### Tab Context Menu

- Target tab : (tab name) — a display-only caption showing which tab the menu acts on (not selectable)
- New tab : execute (exe name)
  - Right of this tab
  - Position (same submenu as "Position Move")
  - Link to another group
- Position Move
  - Snap Left / Snap Right / Snap Top / Snap Bottom
  - Snap 90% / 70% / 50% / 30% (each)
    - Left / Right / Top / Bottom
    - Top Left / Top Right / Bottom Left / Bottom Right
    - Center / Center Horizontally / Center Vertically
  - Snap Display
  - Snap Desktop (multi-monitor only)
  - Move
    - Left Edge / Right Edge / Top Edge / Bottom Edge
    - Top Left / Top Right / Bottom Left / Bottom Right
- Link this tab group to another group (submenu lists other tab groups; choose the destination)
- Detach this tab
  - Position (same submenu as "Position Move")
  - Link to another group
- Close Tab
  - Close this tab
  - Close {N} tabs to the left
  - Close {N} tabs to the right
  - Close other tabs
  - Close all tabs
- Tab Margin When Snapping
  - Add margin at top
- Tab Alignment
  - Align all tabs to Left
  - Align all tabs to Right
  - Align this tab to Left
  - Align this tab to Right
- Tab Pin
  - Pin this tab
  - Unpin this tab
  - Pin all tabs
  - Unpin all tabs
- Tab Color Settings
  - This tab color
    - Red / Blue / Green / Yellow / Purple / Orange / Pink
    - (same 7 colors, Underline variants)
    - (same 7 colors, Border variants)
  - Clear this tab color
  - Clear color settings on all tabs
- Tab Name
  - Rename this tab : (tab name)
  - Reset this tab name : (name after reset)
- System
  - Copy (exe name) path
  - Copy window title : (window title)
  - Open folder of (exe name)
  - Force kill this process
- Settings...

In multi-select, per-tab items show "Selected {N} tabs..." and operate on the active tab plus the selected tabs. Items that require a single target tab are disabled.

### New Tab (New Launch)

- Launch the same exe as the target tab, placing it to the right, in a new window at a specified position, or in another tab group.

![Popup Menu](README_Image/PopupMenu.png)

### Position Move

- Snap moves the group to a screen edge, keeping its width / height; percentage snaps resize it relative to the display.
- Move to screen edges or corners, or maximize across a display or the desktop.
- On multi-monitor setups, the position menus ("Position Move", the new-tab "Position", and the detach "Position") appear once per display, e.g. "Position Move Display Left"; the display the window currently sits on is marked with a "(here)" suffix. The other displays' menus start with a "Same position on this display" item.

![Popup Menu Move Other](README_Image/PopupMenuMoveOther.png)

### Link this tab group to another group

- Move all tabs of the current tab group into another existing tab group.

![Link this tab group to another group](README_Image/MoveTabGroupToGroup.png)

### Detach Tab

- Detach the selected tab ("Detach this tab") and reposition it, or link it to another tab group.
- To detach multiple tabs together, use [Multi-Select Tabs](#multi-select-tabs) first and then run "Detach {N} selected tabs".

### Close Tab

![Popup Menu Close Tab](README_Image/PopupMenuCloseTab.png)

### Per-Tab Alignment

- Align tabs left / right individually or all at once.
- Drag to the other half of the strip to change alignment, even in a single-tab group. Dragging outside the strip detaches the tab.
- Optionally (Behavior tab setting, on by default), snapping a uniformly-aligned group left or right (including x% snaps) realigns its tabs to the snap side.

### Pinned Tabs

- Display pinned tabs as icons only, or at a specified width with a pin button.
- Pinned tabs are placed leftmost within their (left- or right-aligned) group.

![Pinned Tabs Icon](README_Image/PinnedTabIcon.png)
![Pinned Tabs Width](README_Image/PinnedTabWidth.png)

### Tab Color

- Color-code tabs with backgrounds, underlines, or borders.

![Pinned Tab Color Tab](README_Image/PinnedColorTab.png)

### Dark Mode / Light Mode

- The tab / tray-icon context menus (popup menus) and the settings dialog can be switched to dark mode.

### Multi-Display and DPI Support

- Tabs, settings, and confirmation dialogs adapt when moved between displays with different DPI scales
- Sharp rendering at 125% / 150% / 175% and other scales, with tab and button hover / click positions matching their appearance
- Automatic window resizing when dropped to prevent exceeding monitor dimensions

### Virtual Desktop Support

- Tab groups are preserved when switching virtual desktops (Win+Tab)
- Tab group state is restored across all virtual desktops on WindowTabs restart

### UWP Application Support

- Supports UWP (Universal Windows Platform) applications
- All UWP apps are collectively treated as a single exe, supporting tabbing and auto-grouping
- Properly detects the state of apps on other virtual desktops

### Multi-Language Support

- English, Japanese, Chinese Simplified, Chinese Traditional, Korean, French, German, Italian, Spanish, Portuguese, Turkish, Polish, Vietnamese, and Indonesian language support
- Japanese Kansai and Tohoku dialect files included
- Switch languages via the tray menu without restarting

![Task Tray Menu](README_Image/TaskTrayMenuImage.png)

### Settings Files

- `Settings\` beside `WindowTabs.exe` holds these files
    - `Settings\VersionFolder.json`
      - Applications whose path changes with every version are treated as one application
    - `Settings\WindowMargin.json`
      - Settings for applications with thick window frames
    - `Settings\Language\FileList.json`
      - Which language files are loaded
    - `Settings\Language\<language>.json`
- The files under `<exePath>\Settings\`
  - are the defaults
  - are replaced by every upgrade
- To change a setting or a translation
  - do not edit the files under `<exePath>\Settings\`: an upgrade would overwrite them
  - put a file of the same name under `%APPDATA%\WindowTabs\Settings\` instead
  - it is laid over the shipped one entry by entry
- To add a language, put `MyLanguage.json` and a `FileList.json` that lists it under `%APPDATA%\WindowTabs\Settings\Language`
  - To correct a shipped string, put a file of the same name there holding only the keys to change; every other string stays as shipped, and strings added by a later version still arrive
  - `FileList.json` replaces the shipped list whole, so it can also hide languages from the tray menu

### Settings files from ss_2026.09.02 and earlier

- Up to ss_2026.09.02 the language files sat in `Language\` beside `WindowTabs.exe` and the margins in `Settings\Window_Margin.json`, and those files were edited in place
- The current version does not read either place. The MSI removes both on upgrade; the zip leaves them
- If you edited them, move your copies before upgrading: language files to `%APPDATA%\WindowTabs\Settings\Language\`, and `Window_Margin.json` to `%APPDATA%\WindowTabs\Settings\WindowMargin.json`

### Check for Updates

- "Check for Updates" in the tray menu checks the latest GitHub release.
- The check runs only when you click the menu item — WindowTabs never checks automatically (e.g. at startup).
- If a newer version exists, it can be installed on the spot after a confirmation dialog: the MSI install runs the installer, and the zip install replaces the executable in place and restarts automatically.

### Disable Feature

- All tab functionality can be temporarily disabled without quitting WindowTabs.
- Useful when using an app in full-screen mode.

### Tab Group Persistence

- WindowTabs preserves your tab group configuration across restarts and when disabled.
- State is saved every 10 seconds. After a force-quit, saved state can be restored.
- Groups also come back after a Windows restart or a logoff, when every window has been closed and reopened with a handle of its own. Windows are recognised again by their application and their title.
- A tab's name, pin, colours, left/right alignment and its place in the group come back with its window.
- A window whose title has not settled yet - Excel before a workbook has loaded, say - takes its place once its real title appears.
- What was saved for a window that has not been reopened is kept for thirty days; for a tab closed by hand, eight.

### Watchdog Auto-Restart

- If WindowTabs ever becomes unresponsive, a watchdog detects the frozen state and automatically restarts the application; tab group configuration is preserved and restored.
- The freeze that used to occur when changing the number of displays, the resolution, or waking from sleep has been fixed, so the watchdog now remains only as a safety net.

## Settings

Access settings by right-clicking the tray icon and selecting "Settings" or by right-clicking on a tab and selecting "Settings...".

Tray actions are disabled while settings or another dialog is open to prevent overlapping operations.

### Programs Tab

- **Tabs**: Enable/disable tabbing for each program
- **Auto Grouping**: When enabled, windows of the same program are automatically grouped into the same tab group
- **Category 1-10**: Programs in the same category are automatically grouped together, even across different applications
  - For example, assign Word, Excel, PowerPoint, etc. to the same category to auto-group Office apps together
  - Category columns are only visible when Auto Grouping is enabled for a program
- Switching Auto Grouping or a Category **on** also gathers the windows that are already open, as though each of them had just been opened. A tab pulled out by hand while the setting was already on stays out.
- **Show all settings**: Checkbox to display settings for programs not currently running
- **Delete button [x]**: Remove settings for non-running processes

![Settings Programs](README_Image/SettingsPrograms.png)

### Appearance Tab

If you create a nice color theme, please share it at [GitHub Issues](https://github.com/standard-software/WindowTabs/issues). Your theme may be included as a preset theme.

![Settings Appearance](README_Image/SettingsAppearance.png)
![Settings AppearanceColorTheme](README_Image/SettingsAppearanceColorTheme.png)
![Settings AppearanceColorThemeClipboard](README_Image/SettingsAppearanceColorThemeClipboard.png)

### Behavior Tab

- **Tab position on left/right snap**: snapping a uniformly aligned group left or right (including x% snaps) realigns its tabs to that side. "Don't change" is also available.
- **Vertical tab direction**: choose "Up, or down when it would be off-screen" (default) or "Always down".
- Configure hiding tabs for full-screen windows, while moving windows, and when tabs face downward.

![Settings Behavior](README_Image/SettingsBehavior.png)

### Shortcut Keys Tab

- Configure keys for selecting tabs 1–9, switching to the next / previous tab, and adding a new tab.
- Use Ctrl+1–9, Alt+1–9, or individual assignments for numbered tabs. Shortcuts are active only while working in a target window.

### Workspace Tab

- Save and restore tab group layouts, including tab decorations, pins, names, and alignment.

## Building from Source

### Prerequisites

- Visual Studio 2026 Community Edition
- WiX Toolset v3.11 or newer (for building the MSI installer)

### Build Scripts

A build script is provided in the project root:

- **build_release.bat** - Builds both the MSI installer and the portable ZIP distribution
  - Output: `exe\installer\WtSetup.msi`
  - Output: `exe\zip\WindowTabs.zip`

Simply run the batch file to create the distribution packages.

## Links

### English Resources

- WindowTabs - Download  
  https://www.softpedia.com/get/Desktop-Enhancements/ssWindowTabs.shtml

### Japanese Resources

- WindowTabs のダウンロード・使い方 - フリーソフト100  
  https://freesoft-100.com/review/windowtabs.html

- どんなウィンドウもタブにまとめられる「WindowTabs」に日本語派生プロジェクトが誕生（窓の杜） - Yahoo!ニュース  
  https://news.yahoo.co.jp/articles/523e4c5b9db424bb1edfc582d647c1624a9b7502 (404 Not Found)

- どんなウィンドウもタブにまとめられる「WindowTabs」に日本語派生プロジェクトが誕生 - 窓の杜  
  https://forest.watch.impress.co.jp/docs/news/2067165.html

- WindowTabs のダウンロードと使い方 - ｋ本的に無料ソフト・フリーソフト  
  https://www.gigafree.net/utility/window/WindowTabs.html

- C# - WindowTabs というオープンソースを改良してみたいのですがビルドができません。何か必要なものがありますか？ - スタック・オーバーフロー  
  https://ja.stackoverflow.com/questions/53770/windowtabs-というオープンソースを改良してみたいのですがビルドができません-何か必要なものがありますか

- 全Windowタブ化。Setsで頓挫した夢の操作性をオープンソースのWindowTabsで再現する。 #Windows - Qiita  
  https://qiita.com/standard-software/items/dd25270fa3895365fced

## License

This project is open source and licensed under the MIT License.

## Comments

If you have any issues, please contact us via GitHub Issues or email: `standard.software.net@gmail.com`
