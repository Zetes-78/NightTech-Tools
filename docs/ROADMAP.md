# Night Tech in-house tools roadmap

Everything is built in-house on one foundation (C#/.NET 8 + WPF, `NightTech.Core`), with Night Tech branding,
a Windows installer, and a GitHub workflow that tests and packages every release. Night Tech Hub (Python) is the
shop's office software and lives in its own repository.

## Phase 1: what the shop needs now
- [x] **Shared foundation** (`NightTech.Core`): branding, logging, reports
- [x] **Night Tech Recovery, step 1: drive imager** (read only, 3 passes, resume, SHA-256, reports)
- [x] **Night Tech Hub upgrades** (Hub 1.1): intake photos, condition notes, on-screen signature, status-text templates, quote builder with Square link (6% SC tax), attach Recovery reports to tickets
- [ ] **Night Tech Wipe**: HDD overwrite with verification; SSD/NVMe built-in secure erase; results sent to the Hub's wipe certificate

## Phase 2: next 1 to 3 months
- [ ] **Recovery step 2: file carver** (JPEG, PNG, HEIC, MP4/MOV, PDF, Office/ZIP) working on the image
- [ ] **Recovery step 3: undelete and partition recovery** (NTFS, FAT32, exFAT; lost partitions)
- [ ] **Recovery step 4**: thumbnails, sorting, customer report, case tracking tied to the Hub ticket
- [ ] **Burn-in tester**: CPU, memory, drive health and battery, with a printed pass/fail sheet
- [x] **Night Tech Field Kit v1**: USB health check, customer report, Windows tool launchers, Recovery launcher
- [ ] **Field Kit v2**: launchers for Wipe and Burn-in; send reports straight to the Hub ticket

## Phase 3: business clients and monthly income
- [ ] **Network documenter**
- [ ] **Panoptes Lite**: read-only monitoring (drive health, disk space, backups) with alerts
- [ ] **Refurb inventory** (in the Hub)

## Phase 4: later
- [ ] **Mac reinstall tooling** (PowerPC G5s need old install media; Intel Macs use Apple's bootable USB installers)
- [ ] **Remote support and client password vault**: use established products until the suite is mature
- [ ] **Free apps**: finish Backup Buddy; publish it and NightKey on the Microsoft Store

## Standing rules
- Recovery always works from an image, never the customer's original drive.
- Clicking, grinding or undetected drives go to a specialist lab, not through our tools.
- Data recovery results are never guaranteed; the wipe certificate documents the work performed, not a guarantee data can't be recovered.
- Build our own versions of tools from public specifications. Never copy another product's code, look or name.
