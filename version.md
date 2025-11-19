# WindowTabs Standard-Software Version

## version ss_jp_2025.11.20
- Add tab width toggle feature per tab group
  - Added "Make tabs wider" / "Make tabs narrower" menu items to tab context menu
  - Tab width can be toggled individually for each tab group
  - Added "Toggle tab width on active tab icon double-click" setting in Behavior tab
- Remove "Hide after specified time when maximized only" option from hide tabs settings
- Fix tab rename floating textbox positioning on high-DPI displays
- Add "Detach and link tab to another group" and "Link tab group to another group" features to tab context menu
- Update README.md documentation

## version ss_jp_2025.11.14
- Reorganize tab context menu structure
  - Rename "Move tab" → "Move tab to another group"
  - Rename "Detach tab / Move window" → "Detach tab"
  - Add "Reposition tab group" menu with display edge positioning options
- Update README.md documentation

## version ss_jp_2025.11.13
- Add Windows Installer (MSI) with build scripts (build_installer.bat, build_release_zip.bat)
- Enable ILRepack to merge DLLs into single executable (WindowTabs.exe, WindowTabs.exe.config, version.md, README.md)
- Add Windows Terminal UWP support in "New Window" menu
- Remove unnecessary folders and files from repository
- Change distribution format from exe/WindowTabs/ to WindowTabs.zip

## version ss_jp_2025.11.10
- Add menu dark mode support
  - Added "Menu Dark Mode" checkbox in Appearance settings
  - Enables dark mode for popup menus (tab context menu, drag-and-drop menu)
- Implement runtime language switching without restart
  - Replaced .NET resource system with code-based localization (Localization.fs, Localization_en.fs, Localization_ja-JP.fs)
  - Language changes take effect immediately, removed dependency on resx files and WindowTabs.resources.dll
- Enhance tab detach functionality with multi-display support
  - Display-specific submenus with DPI-aware percentage-based positioning
  - Current display menu is disabled, and "Same position and size" changed to "Same position"

## version ss_jp_2025.10.09
- Fix tab drag and drop for all alignment settings (left/center/right)
  - Tab reordering within same group: cursor position follows tab correctly
  - Tab separation with preview: cursor grabs scaled preview at correct position
  - Window drop position: respects tab alignment when placing window from preview
- Change UI controls in Behavior tab from ComboBox to RadioButton
  - Tab position setting
  - Hide tabs when positioned at bottom setting
- Improve tab drag and drop to limit window size to display size
  - Automatically resize window if it exceeds monitor dimensions when dropped

## version ss_jp_2025.09.26
- Add icons to "Move tab" menu items
  - Display exe icon from first tab of each group
- Improve "Move tab" menu filtering logic
  - Check if moving tab actually belongs to a group using hwnd list
  - Fix issue where groups with same-named tabs were incorrectly excluded

## version ss_jp_2025.09.25
- Improve "Move tab" menu to always show latest state
  - Update all group infos synchronously when menu is opened
  - Validate window handles with IsWindow API to prevent showing non-existent groups
  - Exclude single-tab groups that only contain the tab being moved
  - Remove all unnecessary update calls from individual operations

## version ss_jp_2025.09.23
- Fix "Move tab" menu not updating properly
  - Remove group info when last tab closes
  - Update group info after detach/drag/drop operations

## version ss_jp_2025.09.22
- Improve default settings management
  - Made these 3 settings instantly apply from settings dialog instead of tab group:
    - Tab width (narrow/wide) setting
    - Tab position (left/center/right) setting
    - Hide tabs when positioned at bottom setting
- Fix appearance settings to apply immediately
  - Height (pixels)
  - Distance from edge when tabs up
  - Distance from edge when tabs down
- Add tab detach functionality
  - Added "Detach tab" submenu to context menu
  - Can detach tab at same position and size
- Enhance tab detach menu with positioning options
  - Added options to move to display edges (right/left/top/bottom)
- Improve tab drag and drop behavior
  - Keep dropped windows within display boundaries
- Add "Move tab" menu to transfer tabs between groups
  - Shows other groups with tab names (adaptive truncation: 1-22, 2-9, 3+-5 chars)
  - Multi-language support with proper thread safety and error handling

## version ss_jp_2025.09.04
- Improve DPI handling for tab drag and drop
  - Changed drop operation from SetWindowPlacement to SetWindowPos API
  - Added window state restoration before positioning in both hide and drop operations
- Improve DPI handling for tab docking
  - Changed docking operation from MoveWindow API to SetWindowPos API
  - Implemented dynamic DPI change detection using GetDpiForWindow API
- Fix appearance settings not applying correctly
  - Fixed field order mismatch causing color settings to be offset
  - Preserves internal fields like tabHeightOffset when updating appearance
- Fix reset button not immediately applying color changes
  - Added event suppression to prevent race conditions during UI updates
  - Colors now reset correctly on first button click
- Update appearance settings UI buttons
  - Changed "Dark Mode" to "Dark Color" and "Dark Mode (Blue)" to "Dark Blue Color"
  - Added "Light Color" button for applying light theme colors only
  - Reset button now only resets size settings, preserving color choices

## version ss_jp_2025.08.30
- Add "Hide when double-clicking active tab" option
  - Added new option to "Hide tabs when positioned at bottom" in Behavior settings
  - Hides tabs when double-clicking the active tab (tabs must be positioned at bottom)
  - Shows tabs immediately when mouse hovers over hidden tab area
  - Prevents hiding when double-clicking inactive tabs (only works on already active tab)

## version ss_jp_2025.08.24
- Activate next window when closing active tab
- Modify tab context menu display to show tab names and counts

## version ss_jp_2025.08.20
- Unify tab alignment setting across window inside/outside positions
  - Single setting for tab alignment (left/center/right) regardless of tab position
- Improve tab auto-hide functionality  
  - Increased delay from 100ms to 1000ms when mouse leaves tabs
  - Changed to "Hide tabs when down" with three modes: Never/Maximized only/Always
  - Added context menu submenu and default setting in Behavior tab
  - Backward compatibility with old boolean settings
- Add default settings in Behavior tab
  - "Default: Make tabs narrower" - new tab groups start with narrower tabs
  - "Default: Tab position" - dropdown for Left/Center/Right default position
- Rename Appearance tab indent options
  - "Indent for Tabs Down" and "Indent for Tabs Up"
- Prevent tab switching flash when tabs are inside window
  - Temporarily set TOPMOST flag during tab switch for smooth transition
- Improve clarity of settings labels
  - Made tab hide function labels clearer and simpler
  - Added pixel unit labels to appearance settings
  - Renamed and repositioned indent settings for better understanding
- Improve settings dialog layout consistency
  - Unified row height and column width across all settings tabs
  - Increased label column width to prevent text wrapping
- Fix context menu closing immediately due to tooltip conflict
  - Hide tooltip when right-clicking to prevent interference with context menu
- Add "Restart WindowTabs" menu item
  - Added restart option in tray menu above "Close WindowTabs"
  - Shared restart logic with language change functionality
- Add configurable delay for auto-hide tabs feature
  - Added "Delay before hiding tabs" setting in Behavior tab (default 3000ms)
  - Replaced hardcoded 1000ms delays with configurable value

## version ss_jp_2025.08.07
- Disable tab rename on double-click
  - Removed double-click rename functionality from tabs
  - Tab rename can still be accessed via right-click context menu
  - Reduces accidental tab renaming

## version ss_jp_2025.08.06
- Add language switching functionality
  - Added Language submenu in tray icon context menu (English/Japanese)
  - Auto-restart application with confirmation dialog when language is changed
  - Language setting saved to configuration file
- Rename "Indent Flipped" to "Indent (Window Inside)" and "Indent Normal" to "Indent (Window Outside)"
- Fix issue where WindowTabs tabs go behind UWP applications
  - Added TOPMOST flag for all UWP app windows regardless of tab position
  - Handle UWP app Z-order changes to maintain tab visibility
  - Automatically remove TOPMOST flag when non-UWP window or window outside group gets focus
  - Insert tabs after the new foreground window when removing TOPMOST
- Prevent multiple settings dialogs from opening simultaneously
  - Settings dialog closes existing instance before opening new one
  - Only one settings dialog can be open at a time

## version ss_jp_2025.08.04
- Remove "Combine icons in taskbar" feature
  - This feature is not supported on Windows 11
  - Removed combineIconsInTaskbar setting from all related files
  - Always pass false to createGroup() to disable SuperBarPlugin
- Remove ALT+TAB replacement and task switcher features
  - Removed replaceAltTab setting that replaced ALT+TAB with WindowTabs task switcher
  - Removed groupWindowsInSwitcher setting that grouped windows in task switcher
  - Deleted TaskSwitch.fs file and removed from project
  - Removed all related UI controls and settings
- Delete: Fix tabs overlap the minimize button when align right
  - This item can be configured in the settings, so no source code modifications are necessary.
- Improve Japanese translations for tab context menu
- Add "Close tabs to the right" feature
  - Added new menu item "Close tabs to the right"
  - Closes all tabs positioned to the right of the current tab
  - Added onCloseRightTabWindows method in TabStripDecorator.fs
- Remove "Close all tabs of specific process" feature
  - Removed menu item "Close all %s tabs in this window"
  - Deleted onCloseAllExeWindows method
  - Simplified tab closing options in context menu
- Add "Close tabs to the left" feature
  - Added new menu item "Close tabs to the left"
  - Closes all tabs positioned to the left of the current tab
  - Added onCloseLeftTabWindows method in TabStripDecorator.fs
  - Menu item positioned right after "Close tabs to the right"
- Remove "Don't use tabs for %s" and "Auto-group %s" menu items
  - Removed menu item "Don't use tabs for %s"
  - Removed menu item "Auto-group %s"
  - These settings can be easily configured in the settings dialog
  - Simplified tab context menu by removing redundant options
- Reorganize tab context menu order for better user experience
- Disable mouse wheel tab switching functionality
  - Removed MouseScrollPlugin from Desktop.fs
  - Deleted MouseScrollPlugin.fs file and removed from project
  - Mouse wheel scrolling over tabs no longer switches between tabs

## version ss_jp_2025.08.03
- Fix null exception when toggling Fade out option
  - leafOfTree commit: https://github.com/leafOfTree/WindowTabs/commit/dce4f67
- Update Resources.ja-JP.resx hideInactiveTabs 
- Fix desktop Programs title missing issue (from leafOfTree)
  - Added missing "Programs" value in Resources.resx
  - leafOfTree commit: https://github.com/leafOfTree/WindowTabs/commit/4314877
- Add Font resource for UI consistency
  - Added "Font" resource with value "Segoe UI" in Resources.resx
  - leafOfTree commit: https://github.com/leafOfTree/WindowTabs/commit/ac0df82
- Re-add SmoothNodeTextBox implementation for better text rendering
  - Added SmoothNodeTextBox class with ClearTypeGridFit text rendering
  - Updated TaskSwitch.fs to use SmoothNodeTextBox and increased RowHeight (36→48)
  - Updated ProgramView.fs to use SmoothNodeTextBox and increased RowHeight (18→24)
  - Updated WorkspaceView.fs to use SmoothNodeTextBox and increased RowHeight (18→24)
  - leafOfTree commit: https://github.com/leafOfTree/WindowTabs/commit/a62c0d6
- Add option to deactivate hotkeys ctrl+1,ctrl+2
  - Added enableCtrlNumberHotKey setting to control numeric tab hotkeys
  - leafOfTree commit: https://github.com/leafOfTree/WindowTabs/commit/c416a49
- Update option default values and text
  - Changed enableCtrlNumberHotKey Japanese text to "Activate tabs with Ctrl+1...+9"
  - Changed enableCtrlNumberHotKey default value to false
  - Changed hideInactiveTabs default value to false
- Remove all peek code to fix alt-tab error
  - Removed DWM preview functionality from TaskSwitch.fs
  - Removed peekTimer, doShowPeek, and peekSelected method
  - leafOfTree commit: https://github.com/leafOfTree/WindowTabs/commit/4fed82a
- Add New window item to tab context menu
  - Added "New window" option to tab right-click menu
  - Launches a new instance of the same application
  - leafOfTree commit: https://github.com/leafOfTree/WindowTabs/commit/96c6387
- Improve task switch form appearance and window filtering
  - Apply FormBorderStyle.None for modern borderless appearance
  - Filter out windows with empty text and 'Microsoft Text Input Application'
  - Enhance Alt+Tab experience
  - leafOfTree commit: https://github.com/leafOfTree/WindowTabs/commit/3b4fd83
- Add diagnostic view improvements
  - Add button to copy settings file to exe path for easier troubleshooting
  - Add toolbar separator for better UI organization
  - Enhance support capabilities
  - leafOfTree commits: https://github.com/leafOfTree/WindowTabs/commit/faf7623, https://github.com/leafOfTree/WindowTabs/commit/cf3089f
- Fix WindowTabs own alt+tab collapse when there is no window
  - leafOfTree commit: https://github.com/leafOfTree/WindowTabs/commit/5cb3cf5
- Add a text color option to the setting appearance panel
  - leafOfTree commit: https://github.com/leafOfTree/WindowTabs/commit/fce3a8d
  - Added tabTextColor property to TabAppearanceInfo
- Update text color in Resources.ja-JP.resx
- Add color theme dark mode and dark mode blue variant
  - leafOfTree commit: https://github.com/leafOfTree/WindowTabs/commit/d582a4f
  - Added Dark Mode and Dark Mode (Blue) appearance options
- Adjust dark mode blue colors
  - leafOfTree commit: https://github.com/leafOfTree/WindowTabs/commit/e3f1df0
  - Adjusted Dark Mode (Blue) color scheme for better visibility
- Support mouse hover to active tab
  - leafOfTree commit: https://github.com/leafOfTree/WindowTabs/commit/34d8dd1
  - Added option to activate tabs on mouse hover

## version ss_jp_2025.08.02
- Fix Window Title Icon Size
- Add tooltip support

## version ss_jp_2025.07.19
- Support compiles with VS2022 Community Edition.
- Place WindowTabs.exe and required DLLs in the exe folder.
- Multi-display support, multi-DPI support.

## version ss_jp_2020.08.03
- Japanese text support
- Default tab alignment set to right
- Default auto-hide set to false
- ./exe/WindowTabs/WindowTabs.exe
- Support compiles with VS2017 Community Edition.
