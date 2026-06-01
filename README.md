<img src="README_Image/LargeIcon.png" width="60" height="60" alt="icon" align="left" />

# WindowTabs

**Language:** [Japanese/日本語](README_Japanese.md)


![Tabs](README_Image/Tabs.png)


WindowTabs is a tool that extends Windows productivity by letting you manage any window through a tabbed user interface (UI).

<details>
<summary>Read more about WindowTabs</summary>

### Who this tool is for

WindowTabs is a tool for people doing some kind of creative work on a PC. For example: accounting, customer support, slide deck preparation, business management, legal paperwork, electronic medical records, video editing, illustration, or — like me — software development. Doing this kind of work on a PC is itself a creative activity.

In an era where smartphones and tablets cover most web browsing and entertainment, the fact that you go out of your way to use a Windows PC probably means you are engaged in something creative like that. If you are interested in improving how you operate Windows, I think WindowTabs can serve you as a good tool. I have built its features and shipped versions over time specifically to meet that need.

### Why a tab UI?

Do you remember the early design of web browsers? Back then there was no tab UI. As browsers evolved, however, the value of the tab UI became universally recognized. Today every major browser ships with tabs, and their entire feature design is built on the assumption of tabs.

Just as with browsers, managing all of your Windows windows through a tab UI is genuinely convenient. It is an important kind of usability for getting work done crisply. Windows usability rises in one step, and the cost of switching attention to the task you actually want to do drops dramatically.

Microsoft also once prototyped a feature called **Sets** as an OS-level extension intended to manage every Windows window with tabs. The project was, however, discontinued. The exact reason isn't public, but I suspect integrating it into the OS internals was simply too difficult given backward-compatibility constraints. The experience they were aiming for, though, was excellent. WindowTabs has — since well before Sets — delivered that tab UI without touching the OS internals.

### About me (satoshi-yamamoto, the author of the ss_jp version)

I have been a paid user of WindowTabs since before it was open-sourced, and have always been very fond of its tab UI. I have long wished that this kind of operation would spread to more people, and I've also tried out various similar tools over the years.

This is part of why I keep updating WindowTabs as open source. These days I keep asking myself: "What kind of operation is most efficient for the most people? How can I help work get done faster?" — and I'm gradually improving WindowTabs along those lines. I believe the people who use WindowTabs are operating Windows more efficiently because of it.

### How I use it

I (satoshi-yamamoto) am a software developer, and I normally build web applications for work. I've built things like a browser-based drawing tool, an in-browser car navigation app, and business chat tools.

As a developer, I run 7 or 8 instances of VSCode, plus Visual Studio, several Windows Terminals, several WinMerges, a file explorer, an image viewer, Excel — and browsers (Edge, Chrome, private mode) — all managed under WindowTabs. Most of those apps already have their own tab UI, but I use WindowTabs to bundle them together at the window level. I snap them to the left and right of each display in a multi-display setup. I color-code the VSCode / Terminal / WinMerge tabs of related projects with the same color so the relationships stand out. I keep all of those running rather than launching them again, and just switch between them.

There are many ways WindowTabs can be useful for many people. Typical examples include:
- Operate Chrome, Edge, and Firefox — including each one's incognito / private windows — as a single window.
- Manage multiple Excel windows, or multiple Word and PowerPoint windows, as a single window.

Just as every user has their own way of using Windows, each user will find their own convenient way to use it.

### What's enhanced in version ss_jp_

WindowTabs version ss_jp_... lets you snap windows to the left/right/top/bottom of a display, and even jump a window across displays — all in a single action. The Windows-native snap feature has been refined to be much easier to use. In a multi-display environment, you can switch window placement without resorting to drag-and-drop. It's quite convenient, and I'm sure it will be useful for you too.

### I'd be truly happy if you'd recommend it to people around you

The real convenience of WindowTabs is something you can only fully feel once you've tried it. Screenshots and words alone can't really convey how much it eases your day-to-day Windows work to be able to "manage things with tabs." That's exactly why there is value that only existing users can convey to others.

If you happen to see a coworker, friend, or family member shuffling between tons of windows on Windows every day, please tell them: "How about trying WindowTabs? It turns your windows into tabs — Windows might get a little easier to use." The kind of usability a tab UI brings is the kind you can't go back from once you get used to it. I'd love for as many people as possible to experience the same "wait, Windows can feel this comfortable?" surprise that I've felt all this time.

A single tweet on social media, a blog post or article that mentions it, a shared link in your team chat, simply showing it to the person at the next desk — any of those would be truly appreciated. A casual "there's a nice tool I want to show you" from you might make someone's daily PC work much easier.

I'm building this primarily because I want it for myself. But if it can be useful to others and bring even a small positive impact to their work, that would make me very happy as a software developer. My thanks go to all of you who use it.

If you have any feedback, I'd love to hear it on GitHub Issues.

</details>

<br />

This version (ss_jp_yyyy.mm.dd) is forked from payaneco's repository and incorporates some code implementations from leafOfTree's version. Maintained by [Satoshi Yamamoto (@standard-software)](https://github.com/standard-software).

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

Latest version: **ss_jp_2026.05.27_next1**

See [version.md](version.md) for details.


## Download

**Supported OS:** Windows 10, Windows 11

<a href="https://github.com/standard-software/WindowTabs/releases">![GitHub Downloads (all assets, all releases)](https://img.shields.io/github/downloads/standard-software/windowtabs/total)</a>

Download the installer or the zip containing the exe from the [releases](https://github.com/standard-software/WindowTabs/releases) page.

- **WtSetup.msi** - Windows Installer package with automatic installation and uninstallation support
- **WindowTabs.zip** - Portable version that can be extracted and run from any location

## Installation

### Using the MSI Installer (WtSetup.msi)

1. Download `WtSetup.msi` from the [Releases](https://github.com/standard-software/WindowTabs/releases) page
2. Run the installer and follow the installation wizard
3. Choose the installation directory (default: Program Files\WindowTabs)
4. Desktop shortcut and Start Menu shortcut will be created automatically
5. Optionally launch WindowTabs at the end of installation

### Using the Portable Version (WindowTabs.zip)

1. Download `WindowTabs.zip` from the [Releases](https://github.com/standard-software/WindowTabs/releases) page
2. Extract the archive to your preferred location
3. Run `WindowTabs.exe` to launch it.


## Usage

- Right-click the WindowTabs tray icon to open the menu and access the settings dialog.
- In the [Programs] tab of settings, choose programs you want tabs for.
- Tabs will appear on those programs' windows.
- Right-click on tabs to access the tab-specific menu.
- Drag and drop tabs to combine them into tab groups.

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

- New launch : execute (exe name)
  - New tab : right of this tab ((exe name))
  - New window (position) (same submenu as "Position Move", with a leading "Same position" item)
  - New window (link to group)
- Position Move
  - Snap Left
  - Snap Right
  - Snap Other
    - Snap Top
    - Snap Bottom
    - Snap 90% / 70% / 50% / 30% (each)
      - Left / Right / Top / Bottom
      - Top Left / Top Right / Bottom Left / Bottom Right
      - Center / Center Horizontally / Center Vertically
    - Snap Display
    - Snap Desktop
  - Move
    - Left Edge / Right Edge / Top Edge / Bottom Edge
    - Top Left / Top Right / Bottom Left / Bottom Right
  - (per-display submenus, with leading "Same position on this display")
- Link to another tab group (submenu lists other tab groups; choose the destination)
- Tab Detach
  - Detach this tab and reposition (same submenu as "Position Move")
  - Detach this tab and link to another group
- Close Tab
  - Close tab : (tab name)
  - Close {N} tabs to the left
  - Close {N} tabs to the right
  - Close other tabs
  - Close all tabs
- Tab Margin When Snapping
  - Add margin at top
- Tab Position
  - Align all tabs to Left
  - Align all tabs to Right
  - Align this tab to Left : (tab name)
  - Align this tab to Right : (tab name)
- Tab Pin
  - Pin this tab : (tab name)
  - Unpin this tab : (tab name)
  - Pin all tabs
  - Unpin all tabs
- Tab Color Settings
  - This tab color : (tab name)
    - Red / Blue / Green / Yellow / Purple / Orange / Pink
    - (same 7 colors, Underline variants)
    - (same 7 colors, Border variants)
  - Clear this tab color
  - Clear color settings on all tabs
- Tab Name
  - Rename tab
  - Reset tab name
- System
  - Copy (exe name) path
  - Copy window title : (window title)
  - Open folder of (exe name)
  - Force kill this process
- Settings...

In multi-select, per-tab items show "Selected {N} tabs..." and operate on the active tab plus the selected tabs; items that depend on a single pivot or a single process (Left/Right close, Open folder, Force kill) are grayed out.

### New Launch

- Launch a new instance of the same exe as the target tab.
- You can launch as a new tab to the right of the target, as a new standalone window, or linked to another tab group.

![Popup Menu](README_Image/PopupMenu.png)

### Position Move

- Move a tab group's position.
- Snap keeps the current width / height and snaps to a screen edge. Snap Left and Snap Right are commonly used so they sit at the top of the menu for quick access.
- Snap with a percentage resizes the width / height to the specified portion of the display and snaps to an edge.
- Move to a display edge or corner, and snap-to-display / snap-to-desktop maximize-style options are also available.

![Popup Menu Move Other](README_Image/PopupMenuMoveOther.png)

### Link to another tab group

- Move all tabs of the current tab group into another existing tab group.
- Other tab groups can be distinguished by their leading tab icon, tab name, and tab count.

![Link to another tab group](README_Image/MoveTabGroupToGroup.png)

### Detach Tab

- Detach the selected tab and reposition it, or link it to another tab group.
- To detach multiple tabs together, use [Multi-Select Tabs](#multi-select-tabs) first and then run "Detach selected N tabs and reposition" / "Detach selected N tabs and link to another group".

### Close Tab

- Close the selected tab, the tabs to its left or right, the other tabs in the group, or all tabs.

![Popup Menu Close Tab](README_Image/PopupMenuCloseTab.png)

### Per-Tab Alignment

- Each tab can be individually set to left- or right-aligned within a tab group.
- The "align all tabs to left / right" menu items are placed first for quick batch alignment.

### Pinned Tabs

- A pinned tab can be displayed as an icon-only tab.
- It can also be configured with a specified width and show a pin button.
- Pinned tabs are placed leftmost within their (left- or right-aligned) group.
- The selected tab, or the left-side / right-side tabs, can be pinned together.

![Pinned Tabs Icon](README_Image/PinnedTabIcon.png)
![Pinned Tabs Width](README_Image/PinnedTabWidth.png)

### Tab Color

- Apply a color to the selected tab, or to all left-side / right-side tabs.
- Choose between background fill, underline, or border color types.

![Pinned Tab Color Tab](README_Image/PinnedColorTab.png)

### Dark Mode / Light Mode

- The tab / tray-icon context menus (popup menus) and the settings dialog can be switched to dark mode.

### Multi-Display and DPI Support

- Multi-display support with proper window positioning
- DPI-aware window placement
- Automatic window resizing when dropped to prevent exceeding monitor dimensions

### Virtual Desktop Support

- Tab groups are preserved when switching virtual desktops (Win+Tab)
- Tab group state is restored across all virtual desktops on WindowTabs restart

### UWP Application Support

- Supports UWP (Universal Windows Platform) applications
- All UWP apps are collectively treated as a single exe, supporting tabbing and auto-grouping
- Properly detects the state of apps on other virtual desktops

### Multi-Language Support

- English, Japanese, Chinese Simplified, and Chinese Traditional language support
- Japanese Kansai and Tohoku dialect files included
- Any language can be supported by adding a language file
- Runtime language switching without restart
- Switch languages via tray menu

![Task Tray Menu](README_Image/TaskTrayMenuImage.png)

### Disable Feature

- All tab functionality can be temporarily disabled without quitting WindowTabs.
- Useful when using an app in full-screen mode.

### Tab Group Persistence

- WindowTabs preserves your tab group configuration across restarts and when disabled.

### Watchdog Auto-Restart

- WindowTabs may occasionally freeze in the following situations; in those cases the watchdog mechanism detects the unresponsive state and automatically restarts the application.
  - Switching monitors
  - Waking from sleep or hibernate
  - Changing Windows display settings
- Tab group configuration is preserved and restored on restart.

## Settings

Access settings by right-clicking the tray icon and selecting "Settings" or by right-clicking on a tab and selecting "Settings...".

### Programs Tab

Configure programs to use tabs and auto-grouping.

- **Tabs**: Enable/disable tabbing for each program
- **Auto Grouping**: When enabled, windows of the same program are automatically grouped into the same tab group
- **Category 1-10**: Assign programs to a category for cross-application auto-grouping
  - Programs in the same category are automatically grouped together regardless of the executable
  - For example, assign Word, Excel, PowerPoint, etc. to the same category to auto-group Office apps together
  - Category columns are only visible when Auto Grouping is enabled for a program
- **Show all settings**: Checkbox to display settings for programs not currently running
- **Delete button [x]**: Remove settings for non-running processes

![Settings Programs](README_Image/SettingsPrograms.png)

### Appearance Tab

- Customize the visual appearance of tabs.
- Custom color theme features
  - If you create a nice color theme, please share it at [GitHub Issues](https://github.com/standard-software/WindowTabs/issues). Your theme may be included as a preset theme.

![Settings Appearance](README_Image/SettingsAppearance.png)
![Settings AppearanceColorTheme](README_Image/SettingsAppearanceColorTheme.png)
![Settings AppearanceColorThemeClipboard](README_Image/SettingsAppearanceColorThemeClipboard.png)

### Behavior Tab

- Configure tab behavior.

![Settings Behavior](README_Image/SettingsBehavior.png)

### Workspace Tab

- Save the layout of currently displayed tab groups and restore it later.

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

