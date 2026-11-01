# Night Tech Tools

In-house software for **Night Tech Solutions** (https://nighttech.solutions), built on one shared foundation.

| Project | What it is | Status |
|---|---|---|
| `src/NightTech.Core` | Shared library: branding, logging, the imaging engine, case files and reports | v0.1 |
| `src/NightTech.Recovery` | **Night Tech Recovery**, the shop's data recovery tool. Step 1: read-only drive imager | v0.1 |
| `tests/NightTech.Core.Tests` | Automated tests for the engine (run on every push) | |

See `docs/ROADMAP.md` for everything planned next.

## Night Tech Recovery: drive imager

Makes a sector-by-sector copy (image) of a customer's drive. **All recovery work then happens on the image, never on the original.**

- **Read only.** The drive is opened with read access only; Windows refuses any write through that handle.
- **Three passes**, like professional imagers: (1) fast copy in 1 MB blocks that skips past damaged areas, (2) sector-by-sector pass over the areas that failed, (3) retry of sectors that still failed. Unreadable sectors are zero-filled and listed.
- **Pause and resume.** A map file records progress every 10 seconds. Stop any time, or survive an unplugged drive or crash, and start the same case again to continue without re-reading good areas.
- **Proof.** Finished images get a SHA-256 checksum. A text report (for the customer file) and a JSON report (for Night Tech Hub) are written next to the image.
- **Safety checks.** Won't save onto the drive being copied, warns when the selected drive is the PC's own Windows drive, checks free space, and makes the tech confirm the drive isn't clicking or failing physically (those go to a recovery lab).

Each case is one folder: `<case>.img`, `<case>.map.json`, `<case>-report.txt`, `<case>-report.json`.

**Before connecting a customer drive:** use a hardware write blocker, or turn off Windows automount (`mountvol /N` in an administrator prompt, `mountvol /E` to turn it back on). Night Tech Recovery itself never writes to the drive, but Windows can when it mounts one.

## Install

Download `NightTechRecovery-Setup.exe` from this repository's **Releases** page. It installs per user (no admin needed to install). The app asks for administrator rights when it starts, because reading a whole drive requires them. The installer isn't code-signed yet, so Windows SmartScreen may warn: click **More info, then Run anyway**.

## Build

**Automatic (GitHub):** every push to `main` runs the tests and builds the installer on GitHub's Windows machines (Actions tab, download the artifact). Publishing a Release (tag like `v0.1.0`) on GitHub builds the installer again and attaches it, with its SHA-256 file, to that Release.

**On a Windows PC:**
1. Install the .NET 8 SDK (`winget install Microsoft.DotNet.SDK.8`) and Inno Setup 6 (`winget install JRSoftware.InnoSetup`).
2. Run `powershell -ExecutionPolicy Bypass -File .\build.ps1 -Version 0.1.0`
3. The installer is in `installer\Output\`.

Open `NightTech.Tools.sln` in Visual Studio 2022 to work on the code.

## Releasing a new version

On GitHub: Releases, Draft a new release, create a tag such as `v0.1.1`, Publish. Or from the command line:

```
gh release create v0.1.1 --title "Night Tech Recovery 0.1.1" --notes "What changed"
```

The workflow then builds, tests and attaches `NightTechRecovery-Setup.exe` to a new Release. The download link
`https://github.com/<owner>/NightTech-Tools/releases/latest/download/NightTechRecovery-Setup.exe` always points to the newest version.

Copyright (c) Night Tech Solutions. All rights reserved.
