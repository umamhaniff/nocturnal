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
| Menu Bar UI (Pills, Compact, Minimal, Popover) | System Tray Icon (dynamic GDI+ **Crescent Pulse** icon dengan ring progres saat aktif & overlay `/` slash saat nonaktif) + Borderless **Midnight Glass Flyout** WPF (3-tab Timer / Focus / Settings, radial countdown ring, custom toggle switches) |
| Agent Isolation / Background Throttling | **Agent Focus Mode**: Windows 11 EcoQoS Efficiency Mode (`SetProcessInformation`) + optional graceful `CloseMainWindow()` untuk RAM-hog apps di luar whitelist AI agent & sistem |
| Hotspot Handoff / Tmux / Browser Helper | Modular automation hooks (Phase 2+) |

---

## 3. Repository Structure

```text
Nocturnal/
├── gemini.md
├── LICENSE
├── README.md
├── Nocturnal.sln
├── assets/                     # Crescent Pulse SVG brand assets (active & off)
├── installer/                  # Inno Setup 6 script (Nocturnal.iss -> dist/Nocturnal-Setup-1.0.0.exe)
├── scripts/                    # Installer build automation (build-installer.ps1)
├── src/
│   ├── Nocturnal.Core/         # Pure domain models, safety rules, session engine
│   └── Nocturnal.App/          # WPF + Tray Application, Assets/nocturnal.ico & Power Engine
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
