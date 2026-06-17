# FineClipboard UI Refresh Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Refresh FineClipboard on Windows and macOS with restrained Apple-style glass UI while removing paste stack and moving lower-frequency controls into Settings.

**Architecture:** Keep the existing WPF and SwiftUI/AppKit application structure. Add small focused UI helpers for icon and screenshot preview behavior, and reuse existing store, sync, vault, hotkey, and monitor services instead of changing persistence or backend contracts.

**Tech Stack:** .NET 8 WPF, WinForms NotifyIcon, SwiftUI/AppKit, SQLite-backed existing stores, existing screenshot and clipboard services.

---

## File Structure

- Modify `src/FineClipboard/App.xaml`: add shared WPF glass styles.
- Modify `src/FineClipboard/App.xaml.cs`: remove paste stack, simplify tray menu, expose settings actions, trigger screenshot previews.
- Modify `src/FineClipboard/Views/PopupWindow.xaml`: remove stack context item, restyle popup and context menu.
- Modify `src/FineClipboard/Views/PopupWindow.xaml.cs`: remove stack handler.
- Modify `src/FineClipboard/Views/SettingsWindow.xaml`: rebuild categorized settings UI.
- Modify `src/FineClipboard/Views/SettingsWindow.xaml.cs`: wire pause, sync, lock, screenshot hotkey display.
- Create `src/FineClipboard/Views/ScreenshotPreviewWindow.xaml`: image preview window.
- Create `src/FineClipboard/Views/ScreenshotPreviewWindow.xaml.cs`: save/close preview actions.
- Create `src/FineClipboard/Services/AppIconFactory.cs`: shared runtime icon/image source generation.
- Modify `src/FineClipboard/Services/TrayIconFactory.cs`: delegate to the new icon factory.
- Modify `mac/Sources/FineClipboard/AppDelegate.swift`: remove paste stack, simplify menu, trigger screenshot previews, provide Settings actions.
- Modify `mac/Sources/FineClipboard/Views/PopupModel.swift`: remove stack protocol method.
- Modify `mac/Sources/FineClipboard/Views/PopupView.swift`: remove stack menu item and restyle popup.
- Modify `mac/Sources/FineClipboard/Views/SettingsView.swift`: categorized material-style settings.
- Create `mac/Sources/FineClipboard/Views/ScreenshotPreviewView.swift`: preview UI.
- Modify `README.md`: remove paste stack and update settings/menu descriptions.

## Tasks

### Task 1: Remove Paste Stack From Windows

**Files:**
- Modify: `src/FineClipboard/App.xaml.cs`
- Modify: `src/FineClipboard/Views/PopupWindow.xaml`
- Modify: `src/FineClipboard/Views/PopupWindow.xaml.cs`

- [x] Remove `PasteStack` field, stack hotkey id/default, stack hotkey registration, `PasteNextFromStack`, `AddToPasteStack`, `UpdateStackMenu`, and tray stack menu rows.
- [x] Remove `加入粘贴堆栈` menu item from `PopupWindow.xaml`.
- [x] Remove `MenuAddToStack_Click` and stack visibility logic from `PopupWindow.xaml.cs`.
- [x] Run `rg -n "PasteStack|粘贴堆栈|HotkeyStack|AddToPasteStack|MenuAddToStack|stack" src/FineClipboard` and confirm only harmless storage key or removed service file references remain.

### Task 2: Refresh Windows Tray Menu And Settings Actions

**Files:**
- Modify: `src/FineClipboard/App.xaml.cs`
- Modify: `src/FineClipboard/Views/SettingsWindow.xaml`
- Modify: `src/FineClipboard/Views/SettingsWindow.xaml.cs`

- [x] Remove top-level tray rows for `打开历史`, `云同步`, `暂停记录`, `锁定密码`, and `开机自启`.
- [x] Keep tray rows for `设置...`, `截图`, `检查更新...`, separator, `退出`.
- [x] Add internal methods on `App` for `ShowSyncSettings`, `SetRecordingPaused`, `IsRecordingPaused`, `LockVault`, and screenshot launch wrappers.
- [x] Rebuild `SettingsWindow.xaml` into categorized sections: 通用, 历史与隐私, 外观, 快捷键, 同步, 密码, 维护.
- [x] Wire pause recording, cloud sync, lock password, startup, sound, max items, expiry, exclusions, theme, popup size, clear history, and existing hotkey capture.
- [x] Add screenshot hotkey display as read-only unless full recorder support is added in code.

### Task 3: Add Windows Glass Styling And Icon

**Files:**
- Modify: `src/FineClipboard/App.xaml`
- Modify: `src/FineClipboard/Services/ThemeManager.cs`
- Create: `src/FineClipboard/Services/AppIconFactory.cs`
- Modify: `src/FineClipboard/Services/TrayIconFactory.cs`
- Modify: Windows XAML windows touched during implementation.

- [x] Add shared brushes and control styles for buttons, text boxes, combo boxes, check boxes, list rows, context menus, and section panels.
- [x] Update `ThemeManager` to use translucent glass colors and restrained blue accents.
- [x] Generate a unified app icon at runtime with dark glass base, clipboard mark, and blue accent.
- [x] Apply the icon to Settings, Sync, Popup preview, and tray surfaces where possible.
- [x] Ensure controls do not use default gray Windows button styling in the main settings/popup surfaces.

### Task 4: Add Windows Screenshot Preview

**Files:**
- Modify: `src/FineClipboard/App.xaml.cs`
- Create: `src/FineClipboard/Views/ScreenshotPreviewWindow.xaml`
- Create: `src/FineClipboard/Views/ScreenshotPreviewWindow.xaml.cs`

- [x] Add a pending screenshot preview flag set by FineClipboard screenshot menu/hotkey actions.
- [x] When `OnItemCaptured` receives the next image item while the flag is set, clear the flag and open `ScreenshotPreviewWindow`.
- [x] Build preview window with image display, close button, save button, and always-visible shape controls labeled 矩形, 箭头, 画笔, 马赛克 as non-destructive UI affordances.
- [x] Keep OCR and popup refresh behavior unchanged.

### Task 5: Remove Paste Stack And Simplify Menus On macOS

**Files:**
- Modify: `mac/Sources/FineClipboard/AppDelegate.swift`
- Modify: `mac/Sources/FineClipboard/Views/PopupModel.swift`
- Modify: `mac/Sources/FineClipboard/Views/PopupView.swift`

- [x] Remove `pasteStack`, stack menu item, stack hotkey id/registration, stack actions, and `addToStack`.
- [x] Remove the `加入粘贴堆栈` context menu item.
- [x] Remove top-level menu rows for `打开历史`, `云同步`, `暂停记录`, `锁定密码`, `开机自启`, and paste stack rows.
- [x] Keep menu rows for `设置...`, `截图`, `检查更新...`, separator, `退出`.

### Task 6: Refresh macOS Settings, Popup, Icon, And Screenshot Preview

**Files:**
- Modify: `mac/Sources/FineClipboard/AppDelegate.swift`
- Modify: `mac/Sources/FineClipboard/Views/SettingsView.swift`
- Modify: `mac/Sources/FineClipboard/Views/PopupView.swift`
- Create: `mac/Sources/FineClipboard/Views/ScreenshotPreviewView.swift`

- [x] Add AppControl actions needed by Settings: pause get/set, show sync, lock vault, and screenshot display support.
- [x] Rebuild Settings as categorized SwiftUI sections using material-style cards.
- [x] Restyle Popup with material background, rounded rows, quiet blue selected state, and restrained spacing.
- [x] Update menu-bar icon to a custom generated clipboard symbol that remains template-compatible.
- [x] Open screenshot preview when the next captured image arrives after a FineClipboard screenshot action.

### Task 7: Documentation And Verification

**Files:**
- Modify: `README.md`

- [x] Remove paste stack feature and shortcut documentation.
- [x] Update tray/menu-bar and Settings documentation for sync, pause, and lock controls.
- [ ] Run `dotnet build src/FineClipboard/FineClipboard.csproj`.
- [x] Run `rg -n "粘贴堆栈|加入粘贴堆栈|HotkeyStack|PasteNextFromStack|addToStack" .` and inspect remaining hits.
- [x] If Swift toolchain is unavailable on Windows, run `swift --version` and report that macOS build could not be verified locally.
