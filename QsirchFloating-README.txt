Qsirch Floating Search v10

License and attribution:
- This GUI is part of a fork of the upstream Qsirch CLI/API project:
  https://github.com/iios-co/qsirch
- Upstream CLI/API implementation copyright (c) 2026 IIOS Pty Ltd.
- Upstream and GUI additions are distributed under the MIT License.
- The PayPal donation button in Settings > About is optional donationware support and does not change the license.

Build:
Run build.bat.

Expected output:
  dist\QsirchFloating\QsirchFloating.exe
  dist\QsirchFloating\config.json

Portable deployment:
- Put the whole QsirchFloating folder on the share.
- Create workstation shortcuts to that EXE.
- Shared history defaults to history.json beside the EXE/config.
- History is enabled by default and stores saved result records with hostname, local IPv4, last-used time, and use count.
- The visible history filter defaults to This machine, so each workstation sees its own hostname's saved results first.
- Empty search shows saved results. Double-click a saved result to open it.
- Typed searches check saved result history first by result name/path before querying the NAS.
- Settings > History can clear saved result history for only the current workstation.

Appearance / behavior:
- Settings has an Appearance / Behavior tab.
- Show in taskbar is enabled by default. Disable it for tray-only/tool-window behavior.
- The global hide/unhide shortcut defaults to Ctrl+Space and can be changed without restarting.
- If Windows reports the shortcut is already owned by another app, Settings shows a warning.

Path mapping:
- No drive/share mapping is hard-coded.
- Add mappings in Settings > Path Mapping.
- Add mappings in Settings > Path Mapping. No sample server name or drive letter is shipped as a default.

Exclusions:
- Safe QNAP/Qsync defaults are included.
- Folder and file exclusions support wildcards.
- Folder wildcard patterns such as @recycle\* exclude anything under that folder.

Tray and window behavior:
- Close and minimize hide the app to the tray.
- Tray menu has Show Search, Settings, and Exit.
- The Pin button controls whether the floating window stays always on top.
- Drag the window from the background/status/hint area.

Startup speed:
- v10 uses a portable folder build instead of a one-file EXE. This avoids the slow PyInstaller one-file unpack step and should open faster on modern machines.

Future TODO:
- Explore Explorer integration or shell extension packaging after the portable build stabilizes.
- Investigate SMB Multichannel benefits for mapped-drive file opening and any future SMB-side cache/enrichment layer. Qsirch search queries themselves use the NAS HTTP API, so SMB Multichannel does not directly accelerate the current query call.
