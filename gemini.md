# Nocturnal — Project Context (`gemini.md`)

> **Nocturnal** adalah aplikasi System Tray Windows (port/counterpart dari [`krishhgg/Insomnia`](https://github.com/krishhgg/Insomnia) macOS, lisensi MIT) untuk menjaga laptop Windows tetap menyala secara aman dalam durasi terjadwal—termasuk saat tutup laptop (*lid*) ditutup—khususnya untuk menjalankan AI coding agents, build panjang, atau background tasks.
>
> **Collaboration:** Hans x Gravi

---

## 1. Tech Stack & Constraints

* **Runtime / Framework:** C# / .NET 9 (`net9.0-windows`)
* **UI:** WPF + built-in `System.Windows.Forms.NotifyIcon` (System Tray, tanpa window utama/taskbar icon)
* **Hardware Constraint:** **STRICT 8GB RAM LIMIT** — target konsumsi RAM idle di System Tray ~20–35 MB, hindari WebView2/Electron, minimalkan alokasi background polling.
* **License:** MIT (dengan atribusi ke `krishhgg/Insomnia`).

---

## 2. Architecture & Windows API Mapping

| Fitur `Insomnia` (macOS) | Implementasi `Nocturnal` (Windows) |
| :--- | :--- |
| `IOPMAssertion` / `caffeinate` | `SetThreadExecutionState` / `PowerCreateRequest` (`kernel32.dll`) |
| `pmset` Clamshell (Lid-Closed) | `PowrProf.dll` / `powercfg` (`LIDACTION` AC & DC pada Active Power Scheme) |
| `launchd` + Disk Journal | `%LocalAppData%\Nocturnal\journal.json` (write-ahead sebelum ubah power state) + Startup/Scheduled Task recovery check |
| Battery Rules (<40% LPM, <10% Sleep) | `GetSystemPowerStatus` + Windows Energy Saver / Power Plan + `SetSuspendState` |
| Thermal Rules | WMI (`MSAcpi_ThermalZoneTemperature` / fallback graceful jika sensor dikunci OEM) |
| Menu Bar UI (Pills, Compact, Minimal, Popover) | System Tray Icon (dynamic GDI+ **Crescent Pulse** icon dengan ring progres saat aktif & overlay `/` slash saat nonaktif) + Borderless **Midnight Glass Flyout** WPF (3-tab Timer / Focus / Settings, radial countdown ring, custom toggle switches, top auto-update banner, footer matte **Star on GitHub** + **Support** `trakteer.id/umamhaniff/tip`) |
| Agent Isolation / Background Throttling | **Agent Focus Mode v1.1**: Process-Tree Inheritance (`CreateToolhelp32Snapshot`, berhenti di `explorer`/`svchost`) + Path-Based OS/Driver Protection (`QueryFullProcessImageNameW`), Windows 11 EcoQoS + `K32EmptyWorkingSet` RAM Trim, serta opt-in **Deep Pause** (`NtSuspendProcess`/`NtResumeProcess` + crash-safe `SuspendedProcesses` journal) atau **Graceful Close** (`CloseMainWindow`) |
| Self-Update | **Auto-Detect + One-Click Update**: `UpdatePolicy` (`Nocturnal.Core`) + `AppUpdateService` memeriksa GitHub Releases `/latest` saat startup (+8s, lalu tiap 12 jam; toggleable di Settings), memverifikasi domain HTTPS + SHA-256 digest asset `Nocturnal-Setup-*.exe`, men-download ke `%LocalAppData%\Nocturnal\updates\`, menghentikan sesi & melepas mutex secara aman, lalu menjalankan Inno Setup `/VERYSILENT /RELAUNCH=1` |
| Terminal / AI Agent Control & Display Off | **v1.2 Suite**: `nocturnal-cli.exe` + `NocturnalIpcServer` (`Nocturnal_Ipc_v1` current-user Named Pipe), **Process-Watcher** (`WindowsProcessWatcher`, grace period 15s, safety ceiling 12h), **One-Click Display Off** (`SendNotifyMessageW SC_MONITORPOWER`), serta **Live RAM Reclaimed Telemetry & Session History** (`%LocalAppData%\Nocturnal\history.json`) |
| Unattended Remote & UX Publik | **v1.3 Suite**: **Phone/Webhook Alerts** (`WebhookPolicy` + `HttpAlertNotifier` Discord/Telegram/HTTPS), **Live Running-App Picker** (`AppPickerWindow` modal), **Auto Wi-Fi / Hotspot Failover** (`NetworkFailoverPolicy` + `WindowsWifiFailoverController`), serta **Scheduled Night Shift & Quick Profiles** (`SchedulePolicy`: `AI Overnight`, `Build / Scraper`, `Presentation`) |
| Hotspot Handoff / Tmux / Browser Helper | Modular automation hooks (Phase 3+) |

---

## 3. Repository Structure

```text
Nocturnal/
├── gemini.md
├── LICENSE
├── README.md
├── Nocturnal.sln
├── assets/                     # Crescent Pulse SVG brand assets (active & off)
├── .github/                    # GitHub FUNDING.yml (Trakteer)
├── installer/                  # Inno Setup 6 script (Nocturnal.iss -> dist/Nocturnal-Setup-1.3.0.exe)
├── scripts/                    # Installer build automation (build-installer.ps1)
├── src/
│   ├── Nocturnal.Core/         # Pure domain models, IPC protocol, safety rules, session engine
│   ├── Nocturnal.App/          # WPF + Tray Application, Named Pipe server, & Power Engine
│   └── Nocturnal.Cli/          # Terminal/Agent CLI (`nocturnal-cli.exe`)
└── tests/
    └── Nocturnal.Tests/        # Unit tests (Journal, Timer, Safety Rules, Fakes)
```

---

## 4. Development & Verification Commands

```powershell
dotnet build
dotnet run --project tests/Nocturnal.Tests
.\scripts\build-installer.ps1
dotnet publish src/Nocturnal.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

> **Mandatory Pre-Push Audit (`pre-push-audit`):** Sebelum push commit versi baru atau publish GitHub Release, wajib jalankan skill `pre-push-audit` (Code, Security, Data/Journal, Product/UX Flow, dan Artifact/Defender verification) hingga bebas `P0 Blocker`.

---

## 5. Release Status & Technical Learnings (v1.3.1)

* **Repository:** `https://github.com/umamhaniff/nocturnal` (branch `main`)
* **Current Release (`v1.3.1`):** `Nocturnal-Setup-1.3.1.exe` (`50,933,275` bytes / `48.57 MB`, self-contained `win-x64`, `45/45` passing unit tests, Windows Defender `0` threats)
* **Previous Releases:** `v1.3.0` (`e545e15`), `v1.2.0` (`fb95a72`), `v1.1.1` (`6e431ef`), `v1.1.0` (`3294ebf`), `v1.0.0` (`2015c04`)
* **Key Technical Learnings:**
  * **Modern Standby S0ix & Journaled Energy Saver (`v1.3.1`):** `Win32PowerController` holds both `PowerCreateRequest` (`SystemRequired` + `ExecutionRequired`, padded 32-byte x64 `REASON_CONTEXT`) and `SetThreadExecutionState`; `OriginalEnergySaverThreshold` is persisted in `JournalEntry` + `HKCU\...\RunOnce` (`WindowsRecoveryHook`) recovers lid/paused state even when autostart is off.
  * **Non-Destructive Profiles, Async Failover & Alert Flush (`v1.3.1`):** Quick Profiles (`AI Overnight`, `Build / Scraper`, `Presentation`) apply session overrides in memory without overwriting `config.json` and restore user baseline on stop; Wi-Fi failover checks real WAN reachability (`1.1.1.1:53`) + spawns `netsh` off the UI thread; webhook alerts flush up to `3.2s` before `SleepSystem()`.
  * **UI/UX Hardening (`v1.3.1`):** Dark thin scrollbars, masked `WebhookPasswordBox` with Show/Hide, collapsible advanced Settings cards, safe `ListBoxItem` click handling + clamped positioning in `AppPickerWindow`, anti-typo guard on Watch Process, and 3s delayed idle `K32EmptyWorkingSet` trim.
  * **Zero-Blocker Installer & CLI PATH (`v1.3.1`):** `ChangesEnvironment=yes` broadcasts `WM_SETTINGCHANGE` immediately; exact `;dir;` matching prevents prefix corruption in `RemoveFromUserPath`; `InitializeUninstall` closes running tray instances via `Nocturnal_GracefulExit_Event`.
  * **WPF Thread Affinity on Async Self-Update:** Guard `PrepareForInstallerExit()` with `if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(PrepareForInstallerExit); return; }` because `AppUpdateService.DownloadAndInstallAsync` uses `.ConfigureAwait(false)` before tearing down WPF windows, timers, `NotifyIcon`, and the single-instance mutex; start the installer **before** tearing down the tray so UAC cancellation (`1223`) never leaves a zombie process.
  * **Single-Instance Tray UX:** Use `Nocturnal_ActivatePopup_Event` (`EventWaitHandle` + `ThreadPool.RegisterWaitForSingleObject`) so launching shortcuts while already running wakes/opens the Midnight Glass flyout instead of exiting silently; manual launch opens the flyout while Windows startup uses `--minimized`.
  * **Global Pre-Push Audit Governance:** Skill `pre-push-audit` (`C:\Users\HP\.agents\skills\pre-push-audit\SKILL.md`) and permanent rule `#10` in `C:\Users\HP\.gemini\config\AGENTS.md` enforce 6-gate release verification before every `git push` or public release.
  * **Inno Setup Silent Mode:** Gunakan `PrivilegesRequiredOverridesAllowed=commandline` (bukan `dialog`) agar `/VERYSILENT` tidak pernah hang menunggu dialog mode instalasi di background.
  * **WPF Tray Window Lifecycle:** Override `OnClosing` untuk `e.Cancel = true; Hide();` kecuali saat dipanggil lewat `ForceClose()`, supaya `Alt+F4` tidak menghancurkan flyout singleton; ikat tombol `Esc` untuk menyembunyikan flyout.
  * **Live Startup Sync:** Baca `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run\Nocturnal` secara langsung (`StartupManager.IsLaunchAtStartupEnabled()`) daripada mengandalkan nilai default JSON setelah autostart dipasang oleh installer.
  * **True Write-Ahead Journaling & Native Power API:** Pisahkan `ReadLidActionBackup()` → `_journal.Save()` → `ApplyClamshellMode()` agar backup journal selalu ditulis sebelum mutasi power OS; gunakan `PowrProf.dll` langsung untuk `ESBATTTHRESHOLD` alih-alih spawn `powercfg.exe`, serta cache `_acpiWmiUnavailable` + `Marshal.FinalReleaseComObject` untuk query thermal WMI.
  * **Payload Trimming (8GB RAM / Hemat Disk `C:\`):** `<SatelliteResourceLanguages>en</SatelliteResourceLanguages>` + `[InstallDelete]` untuk 13 folder budaya asing memangkas file terpasang dari `471` → `250` dan menjaga kompresi Inno Setup tetap ringan (`Compression=lzma2`).
  * **PowerShell 5.1 & Implicit Usings:** Gunakan `;` dengan `$ErrorActionPreference = 'Stop'` sebagai pengganti `&&`, serta kualifikasi tipe WPF (`System.Windows.Media.Color`, `System.Windows.Point`, `System.Windows.Size`) saat `<UseWindowsForms>true</UseWindowsForms>` aktif.
