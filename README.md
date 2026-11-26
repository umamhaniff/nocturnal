<div align="center">

<img src="assets/crescent-pulse.svg" alt="Nocturnal Logo" width="76" height="76" />

<h1>Nocturnal</h1>

<p>
  <strong>Keep your Windows laptop awake with the lid closed—safely, on a schedule, and tuned for autonomous AI coding agents.</strong>
</p>

<p>
  <a href="https://github.com/umamhaniff/nocturnal/releases/latest">
    <img src="https://img.shields.io/github/v/release/umamhaniff/nocturnal?style=flat-square&color=10b981" alt="Latest Release" />
  </a>
  <img src="https://img.shields.io/badge/platform-Windows%2010%20%2F%2011%20x64-0ea5e9?style=flat-square" alt="Platform Windows 10 / 11 x64" />
  <img src="https://img.shields.io/badge/.NET-9.0%20WPF-6366f1?style=flat-square" alt=".NET 9.0 WPF" />
  <a href="LICENSE">
    <img src="https://img.shields.io/badge/license-MIT-64748b?style=flat-square" alt="MIT License" />
  </a>
  <a href="https://github.com/umamhaniff/nocturnal/stargazers">
    <img src="https://img.shields.io/github/stars/umamhaniff/nocturnal?style=flat-square&color=f59e0b" alt="GitHub Stars" />
  </a>
</p>

<p>
  <a href="#features">Features</a> ·
  <a href="#agent-focus-mode">Agent Focus Mode</a> ·
  <a href="#installation">Installation</a> ·
  <a href="#safety--crash-recovery">Safety Architecture</a> ·
  <a href="#build-from-source">Build from Source</a> ·
  <a href="#-support">Support</a>
</p>

</div>

---

## Why Nocturnal?

Closing a Windows laptop lid immediately suspends long builds, web scrapers, local servers, and autonomous AI coding runs. Standard keep-awake utilities either keep the lid-sleep trigger enabled, leave Windows power settings altered after an unexpected crash, or allow idle desktop apps to exhaust tight 8 GB RAM budgets.

**Nocturnal** solves all three from a lightweight Windows System Tray app (~48 MB private / ~105 MB working set idle RAM, automatically trimmed when hidden):

1. Holds native Windows power execution assertions and temporarily switches lid-close behavior to *Do Nothing* (`AC` & `DC`) for your chosen timer.
2. Logs original Windows power states and any paused processes to disk *before* applying changes, recovering automatically after crashes or reboots.
3. Reclaims RAM and CPU headroom for active AI agents, builds, and remote desktop sessions without terminating your workspace blindly.

*Windows counterpart inspired by [`krishhgg/Insomnia`](https://github.com/krishhgg/Insomnia) (macOS, MIT). Built by **Hans x Gravi**.*

---

## Features

| Capability | What It Does |
| :--- | :--- |
| **Timed Awake Sessions** | Custom `Days / Hours / Minutes` durations (up to 30d), quick presets (`+15m`, `+30m`, `+1h`, `+4h`), **Watch Process** ("awake until process exits"), and smart lid/idle-aware auto-sleep with a 60s cancel banner if you are active. |
| **Safe Clamshell Mode** | Temporarily sets Windows `LIDACTION` to *Do Nothing* on active AC and DC power schemes via `PowrProf.dll` with read-back verification, restoring exact baseline values on stop. |
| **One-Click Display Off** | Turns the display panel off instantly via `SC_MONITORPOWER` without triggering sleep—ideal for lid-open overnight runs or remote desktop. |
| **Battery & Thermal Guards** | Engages Windows Energy Saver below `40%` battery (configurable) and forces an emergency stop + sleep at critical battery (`10%`) or configured thermal limits (graceful fallback when OEM firmware locks ACPI sensors). |
| **Durable Write-Ahead Recovery** | Flushes baseline power configuration, `.bak` backup, and suspended `(PID, StartTimeUtc)` entries **before** mutating OS state, recovering via startup / `RunOnce` even after sudden power loss. |
| **CLI & Local IPC (`nocturnal-cli`)** | Control Nocturnal from any terminal or AI coding agent (`start`, `extend`, `watch`, `profile`, `stop`, `status --json`, `history --json`, `screen-off`) with isolated session flags. |
| **Live Telemetry & Session History** | Displays working set memory trimmed during Focus Mode plus recent session logs (duration, stop reason, battery delta, and trimmed working set) in `%LocalAppData%\Nocturnal\history.json`. |
| **Live Running-App Picker** | Browse active Session 1 processes with live RAM badges to pick targets for Watch Process, Extra Apps, or Agent Whitelist without typing executable names manually. |
| **Quick Profiles & Night Shift** | Launch non-destructive presets (`AI Overnight`, `Build / Scraper`, `Presentation`) in one click or schedule automatic daily Night Shift activation when plugged into AC power while awake. |
| **Wi-Fi / Hotspot Failover** | Monitors HTTP connectivity during active sessions and switches to a saved Wi-Fi profile / hotspot (`netsh wlan connect`) if the primary connection drops. |
| **Encrypted Webhook Alerts** | Opt-in DPAPI-protected HTTPS alerts via Discord, Telegram, or generic webhooks when a session finishes or triggers a low-battery/thermal safety guard. |
| **Midnight Glass Tray UI** | Borderless WPF flyout with a radial countdown ring, custom toggle switches, `Timer` / `Focus` / `Settings` tabs, 4 tray styles (`Pills`, `Compact`, `Minimal`, `Popover`), and dynamic **Crescent Pulse** tray icon states. |
| **Verified Self-Updates** | Silently checks GitHub Releases for newer versions (opt-out in Settings), displays an instant tray alert and top update banner, **fails closed** on missing/mismatched SHA-256 checksums, downloads over HTTPS, and upgrades silently. |
| **Ultra-Light Footprint** | Pure `.NET 9` WPF + Win32 P/Invoke architecture—zero Electron, zero WebView2, low memory overhead on 8 GB systems. |

---

## Agent Focus Mode

Built for laptops running AI coding assistants, local LLMs, CI/build jobs, or scrapers unattended while keeping RAM usage under control:

### 1. Process-Tree & System Path Protection
- Inspects parent-child trees via `CreateToolhelp32Snapshot` and binary paths via `QueryFullProcessImageNameW`.
- Universally protects Windows OS/driver directories (`\Windows\System32\`, `\Windows\SysWOW64\`, `\Windows\SystemApps\`, `\Windows\ImmersiveControlPanel\`, `\DriverStore\`) across all OEM hardware.
- Protects remote access hosts (`Chrome Remote Desktop`, `Tailscale`, `RustDesk`, `AnyDesk`, `TeamViewer`, `Parsec`) so unattended access stays responsive even with the lid closed.
- Protects entire AI agent, IDE, terminal, and dev runtime process trees (`agy`, `claude`, `codex`, `gemini`, `Code`, `Cursor`, `node`, `python`, `dotnet`, `docker`, `wsl`)—stopping inheritance at shell launchers (`explorer`, `svchost`) so agent-spawned MCP servers or Playwright browsers keep full speed while normal user apps remain manageable.

### 2. Windows 11 EcoQoS + Working Set Trimming
- Moves non-agent Session 1 applications into Windows 11 Efficiency Mode (`ProcessPowerThrottling`) and trims working set memory (`K32EmptyWorkingSet`) to move idle pages to standby without closing windows or losing state.

### 3. Lid-Aware Deep Pause (Auto-Resume) or Graceful Close
- Choose `Off`, `Pause`, or `Close` for selected background apps (`Discord`, `Spotify`, `Steam`, `WhatsApp`, `Telegram`, `Teams`, `Slack`, user-only `Browsers`, or custom `Extra Apps`).
- **Pause / Close Only When Lid Closes (Default ON):** Keeps music/browsers running normally while your laptop screen is open, automatically freezing or closing selected apps when you close the lid (`GUID_LIDSWITCH_STATE_CHANGE`) and resuming everything as soon as you lift it.
- **Deep Pause** journals each `(PID, StartTimeUtc)` **before** calling `NtSuspendProcess`, trims its working set, and resumes every verified child process cleanly when the session stops or recovers from a crash.

---

## Safety & Crash Recovery

```text
Start Session
   ├── 1. Read active LIDACTION (AC & DC) + Energy Saver baseline
   ├── 2. Flush durable journal.json (+ .bak) & arm HKCU RunOnce recovery hook
   ├── 3. Apply PowerCreateRequest / SetThreadExecutionState + verified Clamshell LIDACTION
   ├── 4. Engage Agent Focus Mode (journaling each PID before NtSuspendProcess)
   └── On Stop / Timer Expiry / Guard Trigger / Crash Recovery Launch:
          └── Fault-tolerant restore: Resume PIDs -> Restore EcoQoS -> Restore LIDACTION -> Clear journal -> Flush alerts -> Smart Sleep
```

- **100% Local & Private:** Zero telemetry, zero analytics, and zero tracking. Configuration, encrypted webhook URLs (Windows DPAPI), journal, and session history stay strictly inside `%LocalAppData%\Nocturnal\`.
- **PID-Reuse Safe Recovery:** Before resuming suspended PIDs or restoring EcoQoS, Nocturnal matches both `PID` and exact process `StartTimeUtc` (±2s).
- **Unsigned Open-Source Builds:** Because this open-source project is distributed without a paid Authenticode certificate, Windows SmartScreen may show a prompt on first launch; updater downloads fail closed if the release SHA-256 digest is missing or mismatched.

---

## Installation

### Option 1: Windows Installer (Recommended — No .NET Runtime Required)
1. Download the latest `Nocturnal-Setup-1.3.2.exe` from [**GitHub Releases**](https://github.com/umamhaniff/nocturnal/releases/latest).
2. Run the installer—installs per-user by default (no administrator prompt required), registers a Start Menu shortcut, adds `nocturnal-cli` to your user `PATH`, optional startup autostart, and a clean uninstaller.

Silent per-user installation / upgrade:

```powershell
.\Nocturnal-Setup-1.3.2.exe /VERYSILENT /CURRENTUSER
```

### Terminal & AI Agent CLI (`nocturnal-cli`)

Control the running tray daemon from PowerShell, batch scripts, or AI coding agents:

```powershell
nocturnal-cli status --json
nocturnal-cli start 2h --focus
nocturnal-cli watch python --max 6h
nocturnal-cli extend 30m
nocturnal-cli screen-off
nocturnal-cli history
nocturnal-cli stop
```

### Option 2: Portable / Build from Source

<details>
<summary>Build, test, package, and publish with .NET 9 SDK</summary>

**Prerequisites:** [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) on Windows 10/11 (`x64`), plus optional [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`) for the installer.

```powershell
# Build solution
dotnet build Nocturnal.sln

# Run unit tests
dotnet run --project tests/Nocturnal.Tests

# Run from source
dotnet run --project src/Nocturnal.App

# Build self-contained win-x64 installer -> dist\Nocturnal-Setup-1.3.2.exe
.\scripts\build-installer.ps1

# Or publish framework-dependent single-file portable executable
dotnet publish src/Nocturnal.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

</details>

---

## Project Structure

```text
Nocturnal/
├── assets/                   # Crescent Pulse SVG brand assets
├── installer/                # Inno Setup 6 script (Nocturnal.iss)
├── scripts/                  # Build & packaging automation (build-installer.ps1)
├── src/
│   ├── Nocturnal.Core/       # Domain models, process-tree focus policy, IPC parser, guards, engine
│   ├── Nocturnal.App/        # WPF Midnight Glass UI, NotifyIcon tray, Named Pipe IPC, Win32 interop
│   └── Nocturnal.Cli/        # Lightweight terminal CLI client (`nocturnal-cli.exe`)
└── tests/
    └── Nocturnal.Tests/      # Unit test suite (power recovery, guards, process trees, crash resume)
```

---

## ☕ Support

Semua *project* di repository ini gratis dan *open-source*. Kalau kamu merasa terbantu dan ingin mentraktir kopi secara sukarela, bisa mampir ke sini:

[![Trakteer](https://img.shields.io/badge/Trakteer-Traktir%20Kopi-be1e2d?style=flat-square&logo=buy-me-a-coffee&logoColor=white)](https://trakteer.id/umamhaniff/tip)

---

## License

Released under the [MIT License](LICENSE).  
Built by **Hans x Gravi** · Inspired by [`krishhgg/Insomnia`](https://github.com/krishhgg/Insomnia).
