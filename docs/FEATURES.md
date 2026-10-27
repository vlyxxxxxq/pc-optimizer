# Inside Rig

[Download Rig 2.0.0](https://github.com/vlyxxxxxq/rig/releases/download/v2.0.0/Rig.exe) · [Back to the overview](../README.md)

Rig groups its tools into Gaming, Media, Productivity, Files, and System. Turn off unused modules in Settings.
Pin tools to Home, arrange the cards, or choose a compact preset.

## Home and search

Home brings together pinned tools, maintenance findings, live CPU and memory, storage, Game Mode,
and optional cards for timers, screen time, input counts, media playback, and free games.
Maintenance findings show what was checked, when it ran, and what couldn't be read.

The command palette, **Ctrl+Alt+P**, finds tools and settings, launches installed apps and games,
restores window layouts, controls playback, and handles quick calculations. It also supports units,
currency conversion, emoji, time zones, timers, and reminders. Features that need network data can be unavailable offline.

## Gaming

| Tool | What it does |
|---|---|
| **Game Mode** | Detect supported games from Steam, Epic, GOG, Ubisoft, EA, Riot, Xbox, or a custom list. Apply chosen power-plan, priority, sleep, and background-app preferences. |
| **Per-game profiles** | Choose individual preferences, including resolution and refresh rate. Restoration work is recorded and can be retried if it fails. |
| **Game Library** | Browse installed Steam, Epic, and GOG games, launch them, check playtime and news, and find free-to-keep games. |
| **Crosshair** | Customize a click-through crosshair and choose the games in which it appears. |
| **Controller Tester** | Inspect XInput buttons, sticks, triggers, drift, and rumble. |
| **Input Stats** | Optional key, click, scroll, and cursor-travel counts, heatmaps, and per-game APM. Typed text is not recorded. |
| **Overlay and session recap** | View supported performance information and a recap after a session. FPS capture requires administrator access; overlays work in supported windowed/borderless games. |

Game Mode requests graceful app closure by default. Force-closing is a separate choice and can lose unsaved work.
Power-plan and priority changes are preferences to evaluate on your PC, not guaranteed performance improvements.

## Media

- **Similar Photos:** separate byte-for-byte duplicates from visual look-alikes. See the match reason and choose a keeper.
  Only exact copies are preselected; look-alikes require your review. Selected photos go to the Recycle Bin.
- **Color Picker & Ruler:** freeze the screen, magnify a pixel, copy its color, or drag to measure a region.
- **Clips & Recording:** screenshots, recent clips through Game Bar, and region recording to MP4 or GIF.
  The built-in recorder captures video without audio.

## Productivity

- **Clipboard & Notes:** optional in-memory text history, a quick-history popup, and locally saved notes.
- **Screen Time:** optional time per foreground app, plus break reminders.
- **Window Layouts:** save window arrangements and restore them later.
- **Timers & Reminders:** countdowns, timed reminders, and a stopwatch. Saved timers survive restarts;
  overdue reminders are handled when Rig is next running.
- **Now Playing:** sidebar playback controls for supported Windows media sessions.

## Files

- **Downloads:** preview sorting loose files into categories, undo moves, and review old installers. Automatic sorting is optional.
- **Disk Space:** inspect folder sizes, a treemap, the largest files, and duplicate candidates.
- **Junk Cleaner:** review temporary files, browser caches, thumbnails, shader caches, and crash dumps.
  The Recycle Bin is a separate, unselected category. Optional automatic cleaning skips active gaming sessions.
- **Batch Rename:** preview replacement, numbering, prefixes/suffixes, case, and extension changes.
- **File Recovery:** review interrupted batches and undo conflicts; retry, keep both files, or leave a conflict unresolved.
- **Uninstaller:** run applications' own uninstallers and review removable Windows extras.

Preview and recovery don't make every deletion reversible. Read each action's scope, especially Recycle Bin and browser-data cleanup.

## System

Startup apps, running processes, Windows tweaks, Explorer context-menu entries, app installation and updates through winget,
hardware information and benchmarks, network checks and DNS tools, Defender status, repair tools, Windows Update pause,
power timers, and optional scheduled maintenance.

Some actions require administrator access, supported hardware, Windows components, or a network connection.
Tweaks and repairs should be evaluated individually for the PC on which they run.

## Your preferences

Dark/light themes and six accents; Windows contrast-theme support; configurable Home cards and pins; module switches;
sign-in startup; close-to-tray behavior; notifications; privacy choices; and keyboard shortcuts.

Export a shareable setup or include personal history in a full backup. Import previews separate preferences,
personal data, tweaks, and proposed app installs, and save a backup of the existing setup before applying changes.

Settings and enabled history live in `%AppData%\Rig`. Clipboard history stays in memory and clears on exit.
