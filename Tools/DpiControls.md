# Settings controls and modal DPI fixes

Source version: ss_2026.09.08_next16_build7, dev/preload-settings-dialog.

- SettingsDpi.reassertAfterShow restored design Padding after dark-mode code had reserved space for enlarged glyphs. The paint handler derived the glyph size from that padding, so it returned to 13 pixels. Reapply glyph/tree fixups after layout restoration.
- Light-mode checkbox/radio glyphs explicitly scale their themed pixels. Tree checkbox sizes use the tree's recorded scale rather than the last dialog's shared scale. Theme images have a small, bounded state cache.
- AppDialog and the shared workspace editor form now use DpiAwareDialog. WM_DPICHANGED rescales controls and fonts using the previous per-form scale and places the dialog at the suggested rectangle. Closing a child restores the previous shared scale.
- Workspace layout coordinates and sizing rules are unchanged; only AutoScroll on the two outer layout wrappers is disabled (the inner compact form already disabled it).
- Only language-change messages use showReservedEnglish. Other confirmations continue to translate OK/Cancel.

## Validation

Build Debug with OutputPath=bin/Debug/DpiChoiceValidation/ and run Tools/DpiControls.Tests.fsx with .NET Framework FSI. The test uses in-memory settings, never shows a window and does not manipulate the user's desktop.

53 checks passed: light/dark glyph reservation and height through 100/150/175 percent transitions; independence of tree DPI from another dialog; synthetic WM_DPICHANGED font/button resizing across 100/175 percent; scrollbar policy; translated versus fixed-English button captions; themed glyph pixel extents; tree header layout/overlay and fresh/moved height agreement.

Build7 resets the native tree header height at 100% as well as enlarged DPI. Previously, returning from 175% left the enlarged native header beneath a smaller dark overpaint. The new regression failed against build6 and passes after this fix. Fresh 100% layout also uses the shared overlay height (at least 24 pixels rather than the native default 20), avoiding overlap with the first content row. Light mode shares the same layout metric.

Offscreen control snapshots at 175 percent were visually inspected: unite/dpi-choice-dark.png and unite/dpi-choice-light.png. These are rendering probes, not screenshots of the live app.

Physical monitor dragging, real modal placement and all workspace editing variants still require user confirmation. No complete four-candidate benchmark was repeated for build6; the recorded benchmark remains a build4 result.
