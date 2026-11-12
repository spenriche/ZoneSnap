# ZoneSnap focus hopping — design

Date: 2026-11-12
Status: approved

## Intent

Keyboard navigation of open windows by zone: hold Ctrl+Alt and press Left/Right to move focus to the window in the neighbouring zone. Windows never move.

## Behaviour

- Hotkeys: Ctrl+Alt+Left / Ctrl+Alt+Right via `RegisterHotKey` (no keyboard hook). Configurable with `navkey` in `zones.ini`: `ctrl+alt` (default) or `ctrl`.
- On press: enumerate visible, non-minimized, non-cloaked, titled, non-tool, non-noactivate top-level windows, excluding ZoneSnap's own windows.
- Map each window to the zone it overlaps most (DWM extended frame bounds vs. zone hit rectangles); z-order (EnumWindows) decides ties for the topmost window in a zone.
- Order all zones across all monitors by centre X, ties by centre Y.
- Start from the foreground window's zone; if it is unsnapped, use the nearest zone by centre. Move one step in the arrow direction, skipping zones with no window. Clamp at the ends (no wrap).
- Focus the target zone's topmost window using `SetForegroundWindow`, with `AttachThreadInput` and Alt-key fallbacks. If focus still fails (e.g. elevated window), do nothing visible.
- While the modifier is held, show the overlay, highlight the target zone, and draw a 3px outline around the target window. A 50 ms timer runs only during the hop sequence and hides the overlay when the modifier is released. Idle CPU stays zero.

## Non-goals

- Moving or resizing windows, cross-monitor window transfer, wrap-around.
- Keyboard hooks; always-on polling.
- Automated tests (no test infrastructure); verification is compile, hotkey-registration probe, and a manual checklist.

## Config

`zones.ini` gains `navkey = ctrl+alt|ctrl` in `[settings]`. Existing files keep the default.

## Rejected alternatives

- Low-level keyboard hook: breaks the app's hook-free design; risk of input lag and stuck keys.
- Always-on timer polling `GetAsyncKeyState`: violates zero idle CPU.
