# Night Tech Tools

In-house software for **Night Tech Solutions** (https://nighttech.solutions), built on one shared foundation.

| Project | What it is | Status |
|---|---|---|
| `src/NightTech.Core` | Shared library: branding, logging, the imaging engine, case files and reports | v0.1 |
| `src/NightTech.Recovery` | **Night Tech Recovery**, the shop's data recovery tool. Step 1: read-only drive imager | v0.1 |
| `src/NightTech.Wipe` | **Night Tech Wipe**: erases a whole drive, checks every sector, writes the record for the Hub's wipe certificate | v0.1 |
| `src/NightTech.FieldKit` | **Night Tech Field Kit**, a computer health check that runs from a USB stick and leaves a customer report | v0.1 |
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

## Night Tech Field Kit: computer health check

Runs on a customer's computer, from a USB stick or installed, and in about a minute checks:

- **Drives:** Windows' health verdict; SSD wear, temperature and read errors (as administrator); free space; BitLocker (as administrator)
- **Windows:** version, Windows 10 end of support, newest update installed, time since restart
- **Hardware:** make, model, serial, processor, each memory stick, battery wear and charge cycles
- **Stability:** unexpected shutdowns, blue screens and disk errors in the Windows log (last 7 days)
- **Security and network:** antivirus on and up to date; router, internet and name lookup (DNS)
- **Software:** programs that start with Windows (skipping ones turned off), installed programs

It turns the readings into plain-language findings (Problem, Watch, Note, Good) and saves three reports named after the
ticket: **.html** to show or print for the customer, **.txt** to attach to the ticket in Night Tech Hub, **.json** for tools.
The Tools tab opens Windows' own repair tools (Device Manager, Disk Management, sfc, DISM, read-only chkdsk, memory test,
battery report and more) and Night Tech Recovery when it's on the stick.

**Read only.** The checks only look: no settings, files or programs are changed.
**Reports stay on the stick.** Run from USB, a `portable.txt` file next to the program keeps its reports and logs on the stick instead of the customer's computer.

## Night Tech Wipe: drive erase

Overwrites every sector of a drive once (zeros, or random data), then reads every sector back to check it.
Bad sectors are retried one at a time and listed. The record (.json for Night Tech Hub, .txt to read) states the
method, times, problem sectors and plain limits (hidden HPA/DCO areas; SSD spare memory). In the Hub, the
certificate's "checked" box is ticked only for a complete, fully checked wipe.

**Safety:** the drives holding Windows, this PC's boot partition, the program itself and the reports folder are
protected and can't be picked. Erasing needs a ticket number, the customer's-agreement box, the drive's serial
typed in, and a final yes. Right before writing, the drive is re-read to make sure it's still the same one, and
every volume on it is locked and dismounted (if any is in use, nothing is erased). Night Tech Recovery stays
read-only: only Wipe has code that writes to a drive.

## Install

From this repository's **Releases** page: `NightTechRecovery-Setup.exe`, `NightTechFieldKit-Setup.exe`, or `NightTechFieldKit-USB.zip` (Field Kit and Recovery ready to copy to a USB stick; see its README.txt).

Each installer installs per user (no admin needed to install). Night Tech Recovery asks for administrator rights when it starts, because reading a whole drive requires them. The installer isn't code-signed yet, so Windows SmartScreen may warn: click **More info, then Run anyway**.

## Build

**Automatic (GitHub):** every push to `main` runs the tests and builds the installer on GitHub's Windows machines (Actions tab, download the artifact). Publishing a Release on GitHub builds everything again and attaches the installers and the USB zip, each with its SHA-256 file, to that Release.

**On a Windows PC:**
1. Install the .NET 8 SDK (`winget install Microsoft.DotNet.SDK.8`) and Inno Setup 6 (`winget install JRSoftware.InnoSetup`).
2. Run `powershell -ExecutionPolicy Bypass -File .\build.ps1` (each tool's version comes from its `.csproj`)
3. The installer is in `installer\Output\`.

Open `NightTech.Tools.sln` in Visual Studio 2022 to work on the code.

## Releasing a new version

1. Raise `<Version>` in the `.csproj` of each tool that changed (for example `src/NightTech.FieldKit/NightTech.FieldKit.csproj`) and push.
2. On GitHub: Releases, Draft a new release, create a tag such as `fieldkit-v0.1.0` or `v0.2.0`, Publish.

The workflow then builds and tests everything and attaches `NightTechRecovery-Setup.exe`, `NightTechFieldKit-Setup.exe` and
`NightTechFieldKit-USB.zip` to the Release. Download links such as
`https://github.com/<owner>/NightTech-Tools/releases/latest/download/NightTechFieldKit-USB.zip` always point to the newest version.

Copyright (c) Night Tech Solutions. All rights reserved.
