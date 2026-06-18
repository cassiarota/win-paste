# Settings Tabs and Hotkey Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix direct plain-text paste and simplify the cross-platform settings and history-maintenance UX.

**Architecture:** Keep the existing hotkey/store boundaries, adding modifier-release gating only to direct-hotkey paste. Recompose both settings views with native tab controls and move destructive history clearing to the application icon menu.

**Tech Stack:** C# / WPF / Win32, Swift / SwiftUI / AppKit.

---

### Task 1: Prevent the plain-paste hotkey from retriggering the popup

**Files:**
- Modify: `src/FineClipboard/Interop/NativeMethods.cs`
- Modify: `src/FineClipboard/Services/PasteService.cs`
- Modify: `mac/Sources/FineClipboard/AppDelegate.swift`

- [x] Add platform modifier-state checks.
- [x] Wait for the triggering modifiers to be released before injecting paste.
- [x] Confirm popup and plain-paste hotkey registrations still map to separate callbacks.

### Task 2: Replace scrolling settings with tabs

**Files:**
- Modify: `src/FineClipboard/Views/SettingsWindow.xaml`
- Modify: `src/FineClipboard/Views/SettingsWindow.xaml.cs`
- Modify: `mac/Sources/FineClipboard/Views/SettingsView.swift`
- Modify: `mac/Sources/FineClipboard/AppDelegate.swift`

- [x] Create six fixed settings tabs on each platform.
- [x] Give all three hotkey rows the same grid sizing and button behavior.
- [x] Show only the initial “设置密码” action; after configuration show status text only.

### Task 3: Move history clearing to the app icon menu

**Files:**
- Modify: `src/FineClipboard/App.xaml.cs`
- Modify: `src/FineClipboard/Views/PopupWindow.xaml`
- Modify: `src/FineClipboard/Views/PopupWindow.xaml.cs`
- Modify: `mac/Sources/FineClipboard/AppDelegate.swift`
- Modify: `mac/Sources/FineClipboard/Views/PopupView.swift`
- Modify: `mac/Sources/FineClipboard/Views/PopupModel.swift`

- [x] Remove “清空历史” from item context menus.
- [x] Restore dynamic list tabs with right-click create/delete and item assignment.
- [x] Add a confirmed “清空剪贴板历史” action to tray/status menus.
- [x] Refresh an existing popup after clearing.

### Task 4: Verify

- [ ] Run available project builds or parsers.
- [ ] Search the edited surfaces for removed controls and old scrolling layout.
- [ ] Run `git diff --check` and inspect the final diff.
