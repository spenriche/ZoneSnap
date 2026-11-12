# ZoneSnap Focus Hopping Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Holding the navigation modifier and pressing Left/Right moves keyboard focus to the topmost window in the neighbouring zone, highlighting the destination; no windows move.

**Architecture:** A hidden form receives `WM_HOTKEY` from `RegisterHotKey` (no hooks). The handler enumerates eligible top-level windows in z-order, maps each to the zone it overlaps most (DWM frame bounds vs. `Overlay.Hit`), orders all zones by centre X then Y, and focuses the next non-empty zone's topmost window via `SetForegroundWindow` with `AttachThreadInput` and Alt-key fallbacks. A 50 ms timer that runs only during a hop sequence polls the modifier and hides the overlay on release.

**Tech Stack:** C# 4 / .NET Framework 4 WinForms, single file `ZoneSnap.cs`, built by `build.cmd` with the Windows-bundled `csc.exe`.

**Spec:** `docs/superpowers/specs/2026-11-12-zonesnap-focus-hopping-design.md`

## Global Constraints

- One source file (`ZoneSnap.cs`); no new dependencies; must build with `build.cmd`.
- No keyboard or mouse hooks; no always-on timers — idle CPU stays zero.
- Default hotkey Ctrl+Alt+Left/Right; `navkey = ctrl` opts into plain Ctrl; any other value falls back to `ctrl+alt`.
- This feature never moves or resizes windows.
- Do not commit; the user has not asked for commits.

## Review Focus

- Foreground window unsnapped → nearest zone by centre, then step in the pressed direction; nothing happens if that side is empty. Pinned by the manual checklist in Task 3.
- Empty zones between windows → skipped, never highlighted. Pinned by the manual checklist in Task 3.
- Multiple windows in one zone → the topmost in z-order is focused. Pinned by the manual checklist in Task 3.
- Minimized, cloaked, tool, no-activate, untitled, and ZoneSnap's own windows → never targeted. Pinned by `Eligible` checks in Task 2 and the checklist in Task 3.
- Hotkey already registered by another app → one tray balloon, app keeps running. Pinned by `EnsureHotkeys` in Task 2 and the checklist in Task 3.

---

### Task 1: `navkey` setting

**Files:**
- Modify: `ZoneSnap.cs` (`Config`)
- Modify: `README.md` (Settings section)

**Interfaces:**
- Produces: `Config.NavKey` (string, `"ctrl+alt"` or `"ctrl"`)

- [ ] **Step 1: Add the field and parser**

In `Config`, beside `Key`:

```csharp
public string NavKey = "ctrl+alt";           // ctrl+alt | ctrl
```

In `Load`'s settings switch:

```csharp
case "navkey": NavKey = val.Trim().ToLowerInvariant() == "ctrl" ? "ctrl" : "ctrl+alt"; break;
```

- [ ] **Step 2: Write it from `Save`**

After the `key = ` line, append:

```csharp
sb.AppendLine("# navkey: ctrl+alt or ctrl - held with Left/Right to hop focus between windows");
sb.AppendLine("navkey = " + NavKey);
```

- [ ] **Step 3: Update `README.md` Settings** to mention `navkey = ctrl+alt|ctrl`.

- [ ] **Step 4: Compile**

Run: `build.cmd nopause norun`
Expected: `Build OK.` (Kills the running instance; Task 3 step 2 starts it again.)

### Task 2: Hotkeys and focus hopping

**Files:**
- Modify: `ZoneSnap.cs` (`Native`, `Overlay`, new `HotkeyForm`, `App`)
- Modify: `README.md` (Use and "How it stays light")

**Interfaces:**
- Consumes: `Config.NavKey`
- Produces: `App.Hop(int dir)`; `HotkeyForm.Hotkey` event (`Action<int>`, id 1 = Left, 2 = Right); `Overlay.TargetRect` (`Rectangle`, screen coords)

- [ ] **Step 1: Add P/Invokes to `Native`**

`EnumWindows` (+ `EnumWindowsProc` delegate), `IsWindowVisible`, `IsIconic`, `GetWindowTextLength`, `GetWindowLong`, `GetForegroundWindow`, `SetForegroundWindow`, `GetWindowThreadProcessId`, `AttachThreadInput`, `keybd_event`, `RegisterHotKey`, `UnregisterHotKey`, `GetCurrentThreadId`, and a `DwmGetWindowAttribute` overload with `out int` (for `DWMWA_CLOAKED = 14`).

- [ ] **Step 2: Add `Overlay.TargetRect` and paint its outline**

Public field; in `OnPaint`, after the zone loop, draw a 3px white rectangle around `TargetRect` converted to client coordinates when it is not `Empty`.

- [ ] **Step 3: Add `HotkeyForm`**

Hidden `Form` (never shown) that forces `Handle` in its constructor and raises `Hotkey(m.WParam.ToInt32())` on `WM_HOTKEY` (`0x0312`) in `WndProc`.

- [ ] **Step 4: Wire registration in `App`**

Fields: `Timer navTimer`, `HotkeyForm hotkeyForm`, `uint hotMods`, `bool hotkeysOk`, `bool navWarned`, `static readonly uint MyPid`. In the constructor create the form (`Hotkey` → `OnHotkey`), start `navTimer` (50 ms, `NavTick`), call `EnsureHotkeys()` after the tray icon exists. `EnsureHotkeys()` unregisters ids 1/2, registers with `MOD_CONTROL|MOD_ALT` or `MOD_CONTROL`, retries if the setting changed, and shows one warning balloon on failure. `OnHotkey` ignores presses while dragging or while the editor is open.

- [ ] **Step 5: Implement `Hop(int dir)` and helpers**

`BuildOverlays()` (split out of `ShowOverlays` so zones exist without painting), `Eligible(IntPtr)`, `VisibleRect(IntPtr)`, `BestZone(List<ZoneRef>, Rectangle)`, `NearestZone(...)`, `ForceForeground(IntPtr)`, `NavKeyHeld()`, `NavTick()`. `Hop` reloads config, ensures hotkeys, builds overlays, enumerates windows, maps zones, steps `dir`, focuses, then shows overlays with the target zone hot and `TargetRect` set; only then starts `navTimer`. Clamp, skip empties, return silently on failure.

- [ ] **Step 6: Cleanup paths**

`HideOverlays` clears `TargetRect`; `BeginDrag` stops `navTimer` and clears hot/target state; `Quit` unregisters both hotkeys and disposes `hotkeyForm`.

- [ ] **Step 7: Comments, tray text, README**

Header comment mentions the hotkey; tray text becomes `ZoneSnap - Shift-drag snaps, Ctrl+Alt+Left/Right hops`; README Use gets the hop bullet; "How it stays light" says the timer runs during drags or short hop sequences.

- [ ] **Step 8: Compile**

Run: `build.cmd nopause norun`
Expected: `Build OK.`

### Task 3: Verify running app

- [ ] **Step 1: Build and restart**

Run: `build.cmd nopause`
Expected: `Build OK.` and ZoneSnap restarted.

- [ ] **Step 2: Confirm the process is running**

Run: `Get-Process ZoneSnap | Select-Object Id, Path`
Expected: one process at `C:\Users\12\Documents\ZoneSnap\ZoneSnap.exe`.

- [ ] **Step 3: Confirm the hotkeys are registered**

Run a PowerShell `Add-Type` probe calling `RegisterHotKey(IntPtr.Zero, 1, 0x3, 0x25)` and id 2 with `0x27`; expected: both return `False` with error 1409 (already registered by ZoneSnap). If they return `True`, ZoneSnap failed to register its hotkeys.

- [ ] **Step 4: Manual checklist (needs a human at the desktop)**

1. Two windows snapped to different zones; Ctrl+Alt+Right focuses the right one; overlay highlights its zone and outlines the window; release hides the overlay.
2. Ctrl+Alt+Left returns to the first.
3. Press again at the far edge: nothing happens (no wrap).
4. Leave an empty zone between two windows: hop skips it.
5. Stack two windows in one zone: hop lands on the topmost.
6. Foreground window unsnapped: hop starts from the nearest zone.
7. Minimized windows never receive focus.
