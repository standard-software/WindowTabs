# WindowTabs Standard-Software Version

## version ss_2026.07.14_next10
- Added 6 language files: Korean, French, German, Italian, Spanish, Portuguese (Brazilian)
- Multi-monitor position menus: the current display is now marked with a localized "(here)" suffix instead of " *"
- Fixed: after a display was turned on/off, context menus could lose the display direction names and the "(here)" mark, and screen-targeted snaps could land at the main display's top-left (stale WinForms screen cache; now refreshed on every menu build)
- Fixed: "Align all tabs to Left/Right" (and the snap-triggered realignment) reversed the tab order; the tabs now keep their relative order

## version ss_2026.07.14
- Group minimize/restore: only the operated window animates, no flash on the others; fixed glitches — restored window not coming to front, shrinking windows, minimize/restore oscillation, stuck hover color, brief freeze
- Tray menu: added "Check for Updates" — on-demand check of the latest GitHub release, install after confirmation (MSI runs the installer; zip updates in place and restarts)
- Tray menu reorganized: version at top, bold "Settings" as default (double-click) action, update check grouped with restart/exit
- Tab context menu: "Target tab : <name>" header added; per-tab items no longer repeat the tab name ({TabName} or {0} in translations restores it)
- Drag preview: shows the window's actual look (was a Windows 7 style frame); right-aligned tabs drawn right-aligned
- Closed-tab restore: a window closed and reopened while WindowTabs runs returns to its former group/position with color, pin, rename, alignment (cleared on restart)
- Fixed: pin/unpin could leave the saved tab order stale, restoring in the wrong order after restart
- Fixed under high load: closing a grouped window could shrink the others to minimum (#13); clicking a resize edge could remove the others from the group (#12)
- Fixed: a second instance could show an error dialog instead of the "already running" message
- README: download badge now shows the exact count (e.g. "6,512", not "6.5k"), recounted by GitHub Actions every 6 hours and on each release

## version ss_2026.07.10
- Fixed: WindowTabs could freeze (and be auto-restarted by the watchdog) when the display count or resolution changed
- Multi-monitor: position menus now appear once per display (the current display is marked with " *")
- Context menus reorganized: the "Snap Other" submenu was flattened into the position menu, and the launch / detach / rename items now show their target tab name (e.g. "Detach this tab : <name>")
- New Behavior setting: on a left/right snap, tabs of a uniformly-aligned group realign to the snap side (default on)
- The tab of a single-tab group can now be dragged inside the strip to change its left/right alignment

## version ss_2026.07.06
- Version prefix changed from `ss_jp_` to `ss_`
  - The `jp` originally indicated a Japanese-language build, but this fork has since gained multi-language support and is no longer Japanese-only, so the `jp` suffix no longer makes sense
  - Past releases keep their original `ss_jp_...` names (release tags, historical entries in this file); only new releases from 2026.07.06 onward use the `ss_...` prefix
- Fixed: tab tooltip could stay on screen after the cursor left the strip
  - Happened when the cursor moved off via a screen edge or to another display, so no mouse-leave event fired
  - A 200 ms cursor-position poll now force-hides the tooltip
- Context menu: once a submenu opens to the left, its child submenus also open to the left
  - Previously a deeper submenu could wrap back to the right and cover earlier levels
  - The position is corrected before the submenu becomes visible (CBT hook + WM_WINDOWPOSCHANGING rewrite), so there is no flicker

## version ss_jp_2026.06.24
- Tab-group state is now saved every 10 seconds (previously only on graceful shutdown), so a force-quit (Task Manager kill, crash, power loss) no longer loses the session
  - Atomic write (temp file + replace) so a force-quit mid-write cannot corrupt the settings file
  - Dirty check skips the disk write when nothing has changed, keeping the steady-state cost negligible
- A tab joining an existing group now inherits the alignment of that group's last tab, regardless of the joining window's previous per-tab alignment, and lands at the rightmost slot accordingly
  - All-left group → joiner becomes left-aligned; any right-aligned tab in the group → joiner becomes right-aligned
  - Applies to auto-grouping and to the menu commands "Move tab to another group", "Link tab group to another group", and "Detach and link tab to another group" (the latter two route through the move-tab path)
  - "New Tab" (which still matches the invoker tab) and startup restore (which uses saved values) are unchanged

## version ss_jp_2026.06.22
- High-DPI: fixed several hit-test and rendering bugs at non-integer DPI scales (150% / 175% / etc.) and on multi-monitor mixed-DPI setups
  - Fixed: Close/Pin button hover and click were offset from the visible button at non-integer DPI scales
  - Fixed: tab-rename input box was offset and oversized at non-integer DPI
  - Fixed: at non-integer DPI the rightmost right-aligned tab lost hover at its right edge (incl. the Close button) because the draw-correction left a transparent right margin that the layered strip passed through as click-through; that margin is now an invisible hit-testable fill, and the hover no longer vanishes on a spurious WM_MOUSELEAVE while the cursor is still over a tab
- Tab hit-test (independent of DPI):
  - Fixed: hovering/clicking a tab behind an overlapping tab (incl. its Close/Pin button) was stolen by the front tab
  - Fixed: the last tab in a group had a wider hover area than the others (it extended to the trapezoid's slanted bottom edge); its hit width is now trimmed by half the overlap to match every other tab

## version ss_jp_2026.06.06
- Fixed: dragging the only tab of a single-tab group could crash WindowTabs (regression from the 2026.06.02 multi-select drag-reorder).
- Build script: retry temp folder cleanup with a 5-second wait (avoids leftover folder from sync locks).

## version ss_jp_2026.06.02
- Multi-select tab drag-reorder: dragging a selected tab now moves the whole contiguous selected range together (= active tab + adjacent selected tabs that share the same pin state and alignment) as a single block.
  - Visual: the group slides as one block during drag, preserving the normal Chrome-style overlap between members; non-dragged tabs slide aside for the whole group, not just one tab.
  - Swap timing: the group's leading edge crossing the next tab's center triggers the swap; right-aligned multi-tab uses the group center to match the visual expectation.
  - Smart-pin (also unified for single-tab drag): an all-unpinned group whose right neighbor is pinned becomes pinned; an all-pinned group whose left neighbor is unpinned becomes unpinned; otherwise the pin state is preserved.
  - Selection persists across a successful drag; an accidental drag that ended without moving any tab still falls back to the click-to-reduce-selection behavior.

## version ss_jp_2026.05.27
- Fixed: incorrect behavior on maximize for apps configured in Window_Margin.json
- Color theme settings: added missing "Selected" row label localization across all languages (was showing the raw key name)
- Color theme: added "Dark Mono 2" preset (placed between "Dark Mono" and "Dark Red Frame" in the Appearance tab combo box)
- Multi-select tab: added the feature; per-tab context menu items become selection-aware and operate on the active tab plus selected tabs when a selection is active
  - Tab Pin / Tab Position: split into separate items with enable/disable; Left/Right items removed
  - Tab Color Settings: Left/Right submenus removed; added "Clear all tabs"
  - Close Tab: "Close other tabs" becomes "Close N unselected tabs"; Left/Right items disabled
  - Tab Detach: renamed from "Tab Detach and Split"; Left/Right split items removed (covered by multi-select detach; ~1180 lines of impl removed)
  - System: Copy path / Copy window title operate on all selected tabs (CR-LF joined); Open folder / Force kill disabled
  - Single-tab only: Rename, Reset name, New launch

## version ss_jp_2026.05.01
- Restructured the Position Move submenu (used by tab group / detach / split menus).
- Settings dialog: added dark mode support, toggled via the new "Dark Mode (Menu and Settings Dialog)" option on the View tab.
- Settings dialog: fixed bugs that could leave the dialog un-openable after certain tray-menu operations.
- Color editor: invalid hex input (empty / non-hex / too long) now silently falls back to 000000 instead of showing an error dialog.

## version ss_jp_2026.04.22
- Bug fix
  - Build script: include the Settings folder in the portable ZIP distribution
  - Performance: reduced CPU load for heavy apps like LibreOffice; improved tab-strip tracking and rendering
- Installer: Language/Settings files are always rewritten with the new defaults on install/upgrade/repair; user-edited files are backed up to `Backup_YYYY-MM-DD_HH-MM-SS/` first (no folder when nothing changed; kept after uninstall)
- Detach/Split position submenu: Snap Left/Right at top, rest under "Position Other"
- Tab menu: "New tab" replaced by a "New launch : exe" submenu with 3 variants (in-group tab, new window with position, new window linked to another group; new-window variants bypass auto-grouping)
- Pin Tab submenu: removed "Pin all tabs"/"Unpin all tabs" items and added a separator between the left-tab and right-tab groups
- Tab Color submenu: added "Clear color" items next to this/left/right color submenus; removed the in-submenu "Clear color setting" item and the "All tabs color" submenu

## version ss_jp_2026.04.12
- Restructured Tab Position menu: "Align all" at top level, individual tab alignment in submenu
  - Individual tab alignment menu now shows only the opposite alignment option (e.g. "Align this tab to Right" when currently left-aligned)
- Unified tab name truncation across all menus (12 half-width chars, full-width counts as 2)
- Renamed localization keys: TabPositionMenu→TabAlignMenu, AlignGroupTopLeft/Right→AlignTopLeft/Right, AlignLeftTabsFormat/Right→AlignLeftTabsChange/Right
- Restructured Move/Snap menu: Snap Left/Right at top level, Move submenu inside "Position Other"
- Removed "Combined Move and Snap Menu" setting and all related split menu code
- Internal: unified the redundant `lorder` and `visualOrder` tab list representations into a single `visualOrder` list, kept canonically sorted as left-pinned, left-unpinned, right-pinned, right-unpinned so the stored order always matches the on-screen order
- Restructured Tab Color Settings menu: color selection in submenus per scope (this tab, left/right tabs, all tabs) with shared "Clear color setting" item
  - Checkmarks and toggle-off behavior now apply only inside the "this tab" submenu; left/right/all-tab submenus always apply the clicked color without showing checkmarks
  - Renamed "Reset" to "Clear color setting" across all languages
- Renamed "Move Other" menu to "Position Other"
- Replaced "Corner" submenu with "Move" submenu containing edge and corner positions
- TopMost windows are no longer excluded from tab management
- Added System submenu: copy exe path, copy window title, open exe folder, force kill process
- Restructured Pin Tab submenu
  - Items are ordered as pin/unpin this tab, paired pin/unpin left and right tab actions, then pin/unpin all tabs
  - Left/right tab counts include all tabs on the specified side regardless of alignment or pin state (including the target tab)

## version ss_jp_2026.03.25
- Fixed: Changing tab alignment now correctly repositions pinned/unpinned tabs within the new alignment group
- Simplified pin/unpin and align left/right tab menu text by removing alignment group name
- Removed "Reset color" text variant from apply color left/right menu (always shows "Apply color" regardless of current tab's color state)

## version ss_jp_2026.03.24
- Fixed: "New Tab" did not move to the correct position when the invoking tab was right-aligned
  - New tabs were created with the default alignment, causing incorrect behavior
- Fixed: Pin/unpin tab position now correctly stays within the same alignment group
- "New Tab" created from a pinned tab is now automatically pinned

## version ss_jp_2026.03.23
- Fixed: Tab drag & drop position calculation was too sensitive, causing tabs to swap with minimal movement
  - Restored original step-based calculation where tab position only changes when dragged tab's center passes the next tab's start position
- Fixed: Auto-pin/unpin on drag now only considers tabs in the same alignment group
  - Previously, dragging an unpinned tab between pinned tabs of different alignment groups would incorrectly pin the tab

## version ss_jp_2026.03.22
- Removed Center tab alignment option (Left and Right only)
  - Existing Center settings are automatically converted to Left
- Added per-tab left/right alignment feature
  - Drag & drop to switch between left and right alignment
  - Close left/right tabs, pin/unpin left/right tabs, apply color to left/right tabs, split left/right tabs now correctly account for per-tab alignment
- Tab Position menu: renamed "Left"/"Right" to "Align Left"/"Align Right", added "Align all tabs to Left/Right" options
- Tab Position menu: added "Align X left/right tab(s) in [group] to [target]" menu items
  - Operates within same alignment group, similar to pin left/right tabs
- Apply color left/right menu shows "Reset color" text when current tab has no color settings

## version ss_jp_2026.03.18
- Added "Apply color to X left tab(s)" and "Apply color to X right tab(s)" menu items
- Changed Pin tab menu: "Pin/Unpin left/right tabs" now toggles based on current tab's pin state
- Changed per-exe margin from single value to 4-direction (top, left, right, bottom) support
  - Added Settings/Window_Margin.json for user-configurable per-exe margin settings
  - Margins are loaded at startup from the JSON file next to the executable
  - Installer preserves existing settings file on upgrade (NeverOverwrite); creates if not exists
- Fixed: Auto-grouping now triggers on window activation (HSHELL_WINDOWACTIVATED) in addition to window creation

## version ss_jp_2026.03.07
- Added "Same position" option to Detach, Split Right, and Split Left position menus
- Fixed tab color and pinned state not being preserved when splitting, detaching, or linking tabs to another group
  - Tab color and pinned state now use global HWND-keyed storage (same pattern as renamed tab names)
- Tab colors: Red, Blue, Green, Yellow, Purple, Orange, Pink
- Added underline color type and border color type
- Tab Color menu
  - Checkmark overlay on the color icon when the tab's current color matches
  - "Reset this tab color" is disabled when the tab has no color set
  - "Reset all tab colors" is disabled when no tabs in the group have colors

## version ss_jp_2026.03.01
- Added per-tab color fill feature
  - Tab context menu: "Tab Color Change" submenu with 6 color options (Red, Blue, Green, Yellow, Purple, Orange)
  - Semi-transparent (40% transparent / 60% opaque) color overlay on tab background
  - "Reset this tab color : tab name" and "Reset all tab colors" options

## version ss_jp_2026.02.26
- Added pinned tab feature
  - Pinned tabs display as icon-only with narrow width (50px) and are positioned on the left side
  - Context menu: "Tab Pin" submenu with Pin/Unpin this tab, Pin all, Unpin all, Pin left tabs, Unpin right tabs
  - VSCode-style cross-zone drag and drop
    - Dragging a tab into the pinned zone automatically pins it
    - Dragging a tab into the unpinned zone automatically unpins it
  - Pinned tabs show unpin button (VSCode-style tilted pushpin icon) always visible
  - Pinned Tab Width setting uses radio buttons: "Icon Only" or "Specify Width" with numeric input
  - Pin icon uses Segoe MDL2 Assets system font (E718 glyph) for high-quality rendering
- Settings UI: renamed appearance labels for clarity (Tab Height, Tab Width (Max), Pinned Tab Width, Tab Overlap)
- Removed "Tab Width" (Icon Only / Icon and Text) feature from context menu and settings
  - Removed "Toggle tab width on active tab icon double-click" setting from Behavior tab
- Changed "hide on active tab double-click" to single-click (icon click) when tabs are at bottom

## version ss_jp_2026.02.23
- Programs tab: "Show all settings" checkbox to display settings for programs not currently running
  - Added [x] delete button on non-running process rows to remove settings
- Per-tab-group tab position: each tab group can have its own Left/Center/Right alignment via context menu
  - Tab position setting in Behavior tab changed from radio buttons to ComboBox
- "Add tab height margin when snapping" is now a per-tab-group setting
- Snap percent menu: added Center, Center Horizontally, Center Vertically options
- Snap Maximize: changed from submenu to direct menu items (Snap Display / Snap Desktop)
- Tooltip: immediately update to new tab's content when switching tabs (no delay)
  - Tooltip stays visible when mouse moves to tab's rounded edge area
- Tab border area at screen edge now responds to mouse clicks (no longer passes through to desktop)

## version ss_jp_2026.02.21
- Tab close button is now only shown on hover (hidden when not hovered)
  - Tab title text uses fade-out gradient instead of ellipsis ("...") when text overflows
- Snap percent menu order reversed: 90% 70% 50% 30% (most useful first)
- Fix: icon double-click now works on transparent areas of the icon
- New setting: "Add tab height margin when snapping" offsets snap/move-top positions by tab height
- Active tab double-click now opens tab rename UI
  - When "Hide when double-clicking active tab" option is on and tabs at bottom, double-click hides tabs instead

## version ss_jp_2026.02.20
- Auto-grouping: new tabs are now placed next to existing tabs of the same exe instead of at the end
  - Works for both category-based and same-process auto-grouping
- "New Tab" menu: new tab is now inserted to the right of the invoking tab instead of at the end

## version ss_jp_2026.02.19
- Change settings JSON keys from camelCase to PascalCase for consistency with localization keys
  - Backward compatible: reads settings case-insensitively so old camelCase files still work
- Keep tab strip visible during window move/resize
  - Tabs now follow the window position in real-time while dragging
  - Background windows are still hidden off-screen during move as before
- Slightly reduce internal overhead when closing tabs
- Rename WtSetup\README.md to README.txt to avoid duplicate README.md in project
- Major restructuring of context menu layout
  - Place move items at top level
  - Move close tab items into a submenu
  - Replace tab width toggle menu item with "Tab Width" submenu
- Combine move and snap menu items into split items with left/right click detection
  - Left click = move to edge, right click = snap
  - Add "Combined Move and Snap Menu" checkbox to Appearance settings
    - Default ON: split items with left/right click detection and hover effect
    - OFF: separate move and snap items (classic layout)
- Use format strings ({0}) for "New Tab" and "Close Tab" menu items
- Improve tab name editing
  - Replace rename/restore items with "Tab Name" submenu (reset is always shown but disabled when not renamed)
  - Stop rendering renamed tab names in italic font
  - Confirm rename when input field loses focus (instead of canceling)
- Update README.md and README_Japanese.md

## version ss_jp_2026.02.12
- Re-release: The ZIP in version ss_jp_2026.02.11 contained a non-working exe.
  No code changes were made. Both ZIP and MSI have been rebuilt and replaced.
- Merge build_installer.bat and build_release_zip.bat into a single build_release.bat
  - Add ILRepack merge verification to prevent shipping unmerged exe
  - Use /t:Rebuild to ensure clean build state

## version ss_jp_2026.02.11
- Hide Auto Grouping checkbox in Programs tab when Tabs checkbox is OFF
- Add category-based auto-grouping
  - Automatically group apps with the same category
    - E.g., Auto-group MS Office apps into the same tab group
    - E.g., Auto-group Chrome, Edge, Firefox into the same group
  - Add category columns to Programs settings tab
  - Sort Programs tab items by category for better visibility

## version ss_jp_2026.02.10
- Add Windows virtual desktop (Win+Tab) support
  - UWP apps (Settings, Calculator, etc.) are properly hidden when on other virtual desktops
  - Tab group state is preserved across all virtual desktops during WindowTabs restart

## version ss_jp_2026.02.05
- Redesign reposition menu structure
  - Top level: Same position, Move Left/Right, Snap Left/Right
  - "Move Other" submenu: Top, Bottom, Top-Left, Top-Right, Bottom-Left, Bottom-Right
  - "Snap Other" submenu: Left/Right/Top/Bottom with 30-90% options
- Fix tab drag bug where mouse button release was not detected
  - Use GetAsyncKeyState API to check physical mouse button state

## version ss_jp_2026.02.02
- Rename color theme property keys
- Add text color and border color for each tab state
- Redesign color settings GUI layout
  - Display colors in grid format
- Add custom color theme features
  - Save/edit/delete custom themes
  - Import/export themes via clipboard
- Simplify tab group save/restore to use window handle only
  - Tab groups, order, and renamed tab names all restored by hwnd matching

## version ss_jp_2026.01.30
- New Window from tab menu now always docks to current group
  - Regardless of auto-grouping settings, new window launched from tab context menu will dock to the same tab group
- Renamed tab names are now preserved across WindowTabs restart
  - User-defined tab names are saved to settings and restored on startup
- Rename "New Window" menu item to "New Tab" in tab context menu

## version ss_jp_2026.01.29
- Add watchdog to detect UI freeze and auto-restart
  - Monitors UI thread responsiveness every 10 seconds
  - Auto-restarts WindowTabs if UI is frozen for 30 seconds
  - Preserves tab group configuration before restart when possible
  - On watchdog restart, restores last saved state from previous normal shutdown

## version ss_jp_2026.01.28
- Fix excessive window switching when closing/restarting/disabling WindowTabs
  - Skip "activate next tab" behavior during shutdown/restart/disable operations
- Fix WindowTabs tabs appearing for invisible UWP apps (Settings, etc.)
  - Add cloaked window detection using DwmGetWindowAttribute API
  - UWP apps in cloaked state (suspended, virtual desktop, etc.) are now properly excluded
- Fix virtual desktop switch causing return to previous desktop
  - Skip "activate next tab" when window is cloaked (moved to another virtual desktop)
- Preserve tab group configuration when disabling/enabling WindowTabs
  - Tab order and separate groups are now restored after re-enabling
- Preserve tab group configuration across WindowTabs restart
  - Tab groups are saved on exit and restored on next startup

## version ss_jp_2026.01.27
- Add "Tab Detach and Split" parent submenu in tab context menu
  - Add tab split functionality (split tabs from selected tab to right/left and reposition or link to another group)
- Fix Installer: Language folder backup may not work in previous version
  - Fixed timing and method to ensure backup works on upgrade and reinstall

## version ss_jp_2026.01.24
- Separate reset buttons per control in Appearance tab settings
- Change color theme switching UI
- Add "Hide tabs when window is fullscreen" option in Behavior tab
- Rename background color labels for clarity (Normal → Inactive, Highlight → Mouse Over)
- Reorganize tab context menu structure (tab operations first, then separator, then tab group operations)
- Installer: Backup Language folder with timestamp (Backup_Language_YYYY-MM-DD_HH-MM-SS) before install

## version ss_jp_2025.12.09
- update README
- build_release_zip.bat,build_installer.bat support VS2026
- Support JSONC format for all JSON files (FileList.json, language files, settings) - allows // and /* */ comments
- Add multi-language support
  - Add Chinese Simplified and Chinese Traditional language support
  - Add Japanese Kansai and Tohoku dialect language files
  - Language files can be customized to support any language

## version ss_jp_2025.11.24
- Added "Disable" checkbox menu item in tray icon context menu
  - When enabled, hides all tab groups and stops tab grouping
  - Settings menu becomes disabled when Disable is enabled
  - Disable state is now saved to settings file and persists across restarts
- Fix background window resize visual glitch and improve performance
  - Background windows now move and resize simultaneously instead of sequentially
  - DPI-aware logic: Same DPI uses fast single-step move for better performance, different DPI uses position-first approach to handle scaling correctly
  - Significantly faster window resizing and movement operations
- Fix window size issue when linking tabs to maximized group across different DPI displays
- Improve Windows Installer to prevent duplicate entries and preserve installation path after uninstall

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
