# Building Rig

[Back to the overview](../README.md)

## Requirements

- Windows 10 or 11, 64-bit.
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

The application uses C# and WPF, with Windows Forms used for the notification-area icon.
It has no third-party runtime packages. The test project uses xUnit and the .NET test SDK.

## Run from source

From the repository root:

```powershell
dotnet run --project Rig -c Release
```

To inspect the setup flow without installing, saving preferences, or starting background features:

```powershell
dotnet run --project Rig -c Release -- --setup-preview
dotnet run --project Rig -c Release -- --setup-preview --light
```

## Build and test

```powershell
./scripts/Prepare-CaptureEngine.ps1
dotnet build Rig -c Release -warnaserror
dotnet test Rig.Tests -c Release --filter "Category!=Desktop"
```

Domain tests use disposable files and isolated profiles. The `Desktop` test category captures a small screen region
and requires an interactive Windows desktop and Media Foundation; run it deliberately in a suitable session.

For manual testing, use a separate profile instead of changing your normal setup:

```powershell
$env:RIG_SETTINGS = Join-Path $PWD 'App\test-profile\settings.json'
$env:RIG_TEST_INSTALL_ROOT = Join-Path $PWD 'App\test-system'
dotnet run --project Rig -c Release
```

`RIG_SETTINGS` relocates settings and feature data. `RIG_TEST_INSTALL_ROOT` redirects installation folders,
shortcuts, and registration beneath `HKCU\Software\RigTest`; scheduled-task commands are logged instead of run.
These hooks do not simulate every Windows operation: don't invoke live tweaks, process termination, display changes,
or cleanup as though they were harmless test actions.

Performance baselines are recorded in [PERFORMANCE.md](PERFORMANCE.md).

## Build the release download

There is one supported public download: **Rig.exe**, a self-contained Windows x64 application.

```powershell
dotnet publish Rig -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o Release\rig
```

The installer supports this single-file download and local framework-dependent development builds.
Do not distribute just the small launcher from a normal development build: it depends on the adjacent assembly and runtime metadata.

## Releases

Set the project version in `Rig/Rig.csproj`, write `docs/release-notes/v<version>.md`, and push the corresponding tag.
The release workflow checks the tag, builds, runs domain tests, publishes one self-contained `Rig.exe`, verifies its
version, uploads a draft, and then makes the release public. A failed build or test must stop publication.

The application updater always selects `Rig.exe`. Detached release signatures can be enabled as described in
[SIGNING.md](SIGNING.md); they are verification files, not another app edition. Authenticode is a separate setup.

The public lineup has one release and one app download. Keep migration support for existing settings even though legacy-named downloads are no longer published. Earlier experimental builds already numbered 2.1.0 need a manual download of the current 2.1.0 release.

## Clean interface previews

Export the actual WPF views without a desktop cursor or personal profile:

```powershell
dotnet run --project Rig -c Release -- --export-previews App/previews
```

This exits after writing Home (dark and light), Game Mode, and setup PNGs. Metrics and findings are sample data;
no app services start, personal settings are not loaded, and setup choices are not saved. Label public previews as
sample data. This renderer complements hands-on checks; it doesn't prove that live Windows changes work.

## Capture studio

Prepare the pinned native engine before building or testing. The release workflow does this automatically.
See [recording components](THIRD-PARTY.md). Audio uses NAudio 2.2.1; FFmpeg runs as a child process tied to Rig's lifetime.
Replay uses two-second video segments in the local capture cache. Export reads completed segments and writes a new
MP4 before announcing success. A manual recording's marker stays until a verified final file exists.

`RIG_TEST_CAPTURE_SYNTHETIC=1` replaces display/audio capture with generated video and a test tone. Pair it with
`RIG_SETTINGS` and `RIG_TEST_INSTALL_ROOT` for UI checks without recording personal activity.
`RIG_TEST_INPUT_SYNTHETIC=1` produces test input counts when Input Stats is enabled in an isolated profile.
Neither mode is enabled by a normal release launch.

FFmpeg display capture currently uses GDI; hardware encoding availability varies by GPU and driver. Test real games,
long recordings, audio sync, mixed-DPI displays, device changes and exclusive fullscreen before claiming broad coverage.
