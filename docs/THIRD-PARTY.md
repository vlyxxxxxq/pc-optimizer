# Recording components

Rig uses NAudio 2.2.1 (MIT) for Windows audio capture and FFmpeg (LGPL shared build) as a separate media process.
The release includes the pinned engine so capture works without an extra installation or first-use download.
The engine archive is generated during the build; it is not checked into Git.

- [NAudio source](https://github.com/naudio/NAudio) and [pinned NuGet package](https://www.nuget.org/packages/NAudio/2.2.1).
- [Pinned BtbN FFmpeg build](https://github.com/BtbN/FFmpeg-Builds/releases/tag/autobuild-2026-10-26-14-25).
- [FFmpeg source commit](https://github.com/FFmpeg/FFmpeg/tree/6882969ce9).
- [Build scripts and dependency source recipes](https://github.com/BtbN/FFmpeg-Builds).
- [Included licence notices](../Rig/Assets/THIRD-PARTY-NOTICES.txt).

The native archive's SHA-256 is checked before packaging. Extracted engine files are checked against the packaged
manifest. Capture and export processes are tied to a Windows job that terminates them when Rig exits.
