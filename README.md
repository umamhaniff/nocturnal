# Nocturnal

> A lightweight Windows System Tray utility to safely keep your laptop awake for timed sessions—even with the lid closed.
>
> *Windows counterpart inspired by [`krishhgg/Insomnia`](https://github.com/krishhgg/Insomnia) (macOS, MIT).*  
> *Built by **Hans x Gravi**.*

---

## Features

- **Timed Awake Sessions:** Set custom durations (Days / Hours / Minutes) or quick presets (`+15m`, `+30m`, `+1h`, `+4h`).
- **Clamshell Mode (Lid-Closed Awake):** Temporarily sets Windows `LIDACTION` to *Do Nothing* on active power schemes (AC & DC) and restores your exact previous settings when the session ends.
- **Auto-Sleep on Completion:** Automatically puts Windows to sleep when the timer expires (configurable).
- **Battery & Thermal Safety Guards:**
  - Engages Power Saver when battery drops below `40%` (configurable).
  - Stops the session and puts the system to sleep at critical battery (`10%`) or critical thermal thresholds.
- **Crash-Resilient Disk Journaling:** Writes power state changes to `%LocalAppData%\Nocturnal\journal.json` *before* applying them, automatically recovering stale states on next startup if a crash or unexpected reboot occurs.
- **Crescent Pulse Tray Indicator:** Glowing emerald Crescent Pulse + countdown ring when active; muted Crescent Pulse overlaid with a `/` slash when idle/off.
- **Midnight Glass Flyout UI:** Borderless Windows 11 flyout with a radial countdown ring, custom toggle switches, segmented `Timer` / `Focus` / `Settings` tabs, and 4 tray UI styles (`Pills`, `Compact`, `Minimal`, `Popover`).
- **Agent Focus Mode (8GB RAM Guard):** Automatically lowers non-agent windowed apps into Windows 11 **EcoQoS Efficiency Mode** during active sessions while protecting AI agents (`Claude`, `Gemini`, `Antigravity`, `Codex`, `Node`, `Python`, `Docker`, `Terminal`, `VS Code`, `Cursor`) and optionally sending graceful close requests to selected RAM-heavy apps (`Discord`, `Spotify`, `Steam`, etc.).
- **Ultra-Light Footprint:** Native .NET 9 WPF + Win32 Interop (~20–35 MB RAM idle, no Electron/WebView2).

---

## Build & Run

Requirements: [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) on Windows 10/11.

```powershell
dotnet build Nocturnal.sln
dotnet run --project src/Nocturnal.App
```

### Publish Single-File `.exe`

```powershell
dotnet publish src/Nocturnal.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

---

## License

[MIT](LICENSE)
