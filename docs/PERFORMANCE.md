# Performance baseline

Measured on 2026-10-27 with the Release build (`dotnet build Rig -c Release`, not single-file), a fresh isolated
profile, on the second monitor (1920×1080, 100 % scaling).

Machine: AMD Ryzen 7 7800X3D (8 cores / 16 threads), 31 GB RAM, Windows 11 Pro build 26300.

How: `measure.ps1` (UI Automation) launches Rig, waits for Home's PC health card, opens the command palette by clicking
the sidebar search box, opens five pages, then closes the window to the tray and samples CPU time for 30 s. Rig's own
timings come from `RIG_TEST_TIMINGS=<file>` (off by default): each is measured from the action until the UI is idle
(layout and rendering done). Cold start is the median of 3 runs.

| Measure | Before (2.0 + file/settings fixes) | After (static settings index, lifecycle fixes) |
|---|---|---|
| Cold start to Home visible (external) | 1,475 ms | 1,446 ms |
| Start to first render (app timing) | 1,055 ms | 1,041 ms |
| First palette open (app timing) | 300 ms | 162 ms |
| Repeat palette open (app timing) | 70 ms | 67 ms |
| Page navigation, title visible (external, median / max) | 65 / 115 ms | 69 / 124 ms |
| Memory idle on Home | 229 MB | 229 MB |
| Memory after first palette open | 291 MB | 273 MB |
| CPU while hidden in the tray, per 30 s | 234 ms | 47 ms |
| Memory while hidden in the tray | 306 MB | 297 MB |

What changed: the palette no longer builds every page to index settings (it uses `SettingsIndex`), Home's charts and
the now-playing check stop while they aren't on screen, and the crosshair's foreground check only runs when there's a
game to watch.

## Regression thresholds

Rerun the measurement after changes to startup, the palette, page lifecycle or background services. Treat these as
failures on comparable hardware (roughly 1.3× the "after" numbers, rounded):

- Start to first render: over 1,400 ms
- First palette open: over 250 ms; repeat: over 120 ms
- Page navigation median: over 150 ms
- CPU hidden in the tray: over 100 ms per 30 s with default modules (Game Mode detection, screen time and clipboard
  history are deliberate tray features and are included)

Not measured yet: a large Similar Photos or Disk Space scan on a real library (fixture timings only), and 150 % /
200 % display scaling.
