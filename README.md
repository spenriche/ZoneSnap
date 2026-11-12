# ZoneSnap

A tiny window zone-snapping tool for Windows. One C# source file, no dependencies, no network code.

## Use

- Hold **Shift** while dragging a window. Zones appear; drop the window on one to snap it.
- Hold **Ctrl+Alt** and press **Left/Right** to hop focus between open windows by zone. Windows stay put.
- Right-click the tray icon to switch layouts, edit zones visually, create or delete layouts,
  open the settings file, or toggle "Start with Windows".

### Zone editor

- Drag empty space: new zone
- Drag a zone: move it
- Drag an edge or corner: resize
- Right-click: delete
- `1`-`9`: that many columns, `Shift+1`-`9`: rows, `G`: 2x2 grid
- `Enter`: save, `Esc`: cancel

## Settings

`zones.ini` sits next to the exe and is created with defaults if missing. It holds the trigger
key (`shift`, `ctrl`, `alt` or `none`), the focus-hop key (`navkey = ctrl+alt` or `ctrl`), gap,
opacity, colours, and every zone as `x, y, width, height` in percent of the monitor. Edits are
picked up on the next drag or hop.

## Build

Run `build.cmd`. It uses the C# compiler that ships with Windows (.NET Framework 4), rebuilds
`ZoneSnap.exe`, and restarts it.

## How it stays light

No keyboard or mouse hooks. Windows reports when a window starts and stops being moved; only
during a drag does a 30 ms timer poll the cursor and the trigger key. Focus hopping uses
registered hotkeys and a 50 ms timer that runs only during a short hop sequence. Idle CPU
use is zero.
