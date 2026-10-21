# PC Optimizer

A native Windows desktop app (C# / WPF, .NET 10, no third-party packages). Runs in the notification
area so Game Mode can kick in automatically. Inspired by Chris Titus Tech's WinUtil, game boosters
like Razer Cortex and Process Lasso, and Windows' own maintenance tools.

## Download

Grab `PCOptimizer.exe` from the [latest release](../../releases/latest). It's a single file with
everything built in: no installer and no .NET needed. Works on 64-bit Windows 10 and 11.

The EXE isn't code-signed, so the first time you run it Windows SmartScreen may say
"Windows protected your PC". Click **More info → Run anyway**.

## Features

- **Dashboard** – health score (0–100) on launch with one-click fixes, live CPU/memory charts, drives.
- **Game Mode** – toggle it yourself (page, sidebar chip or tray menu) or let it turn on automatically.
  - Detects games only when one is the window you're using and it lives in a game library
    (Steam incl. all library folders, Epic, GOG, Ubisoft, EA, Riot, Xbox, `X:\Games`) or your own list.
    Background apps such as Wallpaper Engine never count, and known non-game Steam apps (Lossless Scaling,
    SteamVR, Soundpad, Proton…) are filtered out. "Not a game" ignores anything that slips through.
  - Boosts (each optional): Ultimate/High performance power plan, high CPU priority for the game,
    block sleep/screensaver, close background apps and reopen them afterwards.
  - Apps to close: known background hogs that are actually running (sync clients, Teams, torrent clients…)
    with a reason for each, plus one-click adding of any other open app.
  - Everything is undone when the game closes, including after a crash (the old power plan is restored on next launch).
  - Windows notifications when Game Mode turns on or off.
- **Junk Cleaner** – temp files, browser caches, Recycle Bin, thumbnails, shader cache, crash dumps.
- **Disk Space** – parallel scan for the largest folders and files; deletes go to the Recycle Bin.
- **Startup Apps** – icons and publishers; reversible enable/disable (same flags as Task Manager).
- **Processes** – live CPU/memory, grouped by app, End task with critical processes protected.
- **Tweaks** – 24 reversible gaming, privacy and Explorer tweaks showing their real current state,
  "Apply recommended", and restore point creation.
- **Apps** – winget-powered: see and install updates for everything, or batch-install popular apps.
- **Uninstaller** – every installed program with icon, size and install date; search, tick several and
  uninstall them in one go with each program's own uninstaller. Plus removable Windows extras (Clipchamp, News, Solitaire…).
- **Tools** – power plans (+ add Ultimate Performance), flush DNS, restart Explorer, repair Windows
  (DISM/SFC, network reset, Windows Update fix, chkdsk), shortcuts to Windows tools.
- **Settings** – start with Windows (in the tray), close to tray, notifications.

Deliberately left out: "RAM cleaners" (trimming other apps' memory just makes them page back in from disk)
and registry cleaners (no measurable benefit, real risk).

## Build and run

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download). From the `PCOptimizer` folder:

```
dotnet run -c Release
```

Build the standalone EXE into `App\PCOptimizer.exe`. It bundles .NET, so it runs on any 64-bit
Windows 10/11 PC and can be copied anywhere. Close the app first:

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o ..\App
```

Settings live in `%AppData%\PCOptimizer\settings.json`. Set `PCOPTIMIZER_SETTINGS` to another path to use a
separate profile (useful for testing).
