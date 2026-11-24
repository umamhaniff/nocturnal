using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Nocturnal.App.Interop;
using Nocturnal.App.Services;
using Nocturnal.App.Storage;
using Nocturnal.App.UI;
using Nocturnal.Core;

namespace Nocturnal.App;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;
    private SessionEngine? _engine;
    private AppUpdateService? _updateService;
    private TrayIconService? _trayIcon;
    private MainPopupWindow? _popup;
    private DispatcherTimer? _timer;
    private DispatcherTimer? _updateTimer;
    private int _tickCount;
    private bool _isExiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(true, "Nocturnal_SingleInstance_Mutex", out var createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        _updateService = new AppUpdateService();
        _updateService.CleanupStaleUpdateFiles();

        _engine = new SessionEngine(
            new Win32PowerController(),
            new WindowsSystemMonitor(),
            new FileJournalStore(),
            new FileConfigStore(),
            focusController: new WindowsAgentFocusController());

        // Restore any stale power state from a previous crash, then populate initial telemetry.
        _engine.RecoverIfNeeded();
        _engine.Tick();

        SystemEvents.SessionEnding += OnSessionEnding;
        SessionEnding += (_, _) => ExitApplication();
        Exit += (_, _) => SafeRestoreOnCrash();
        DispatcherUnhandledException += (_, _) => SafeRestoreOnCrash();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => SafeRestoreOnCrash();

        _popup = new MainPopupWindow(
            _engine,
            _updateService,
            ExitApplication,
            PrepareForInstallerExit,
            () => Dispatcher.BeginInvoke(() => Shutdown()));
        _trayIcon = new TrayIconService(_engine, () => _popup.ToggleNearTray(), ExitApplication);

        _updateService.NewUpdateDetected += release =>
        {
            Dispatcher.BeginInvoke(() => _trayIcon?.ShowUpdateAvailable(release));
        };

        _engine.StatusChanged += status =>
        {
            Dispatcher.Invoke(() =>
            {
                _trayIcon?.Update(status);
                _popup?.UpdateStatus(status, _engine.LatestSnapshot);
            });
        };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            _tickCount++;
            // Run full system snapshot & safety check every 10s; update countdown every 1s.
            if (_tickCount % 10 == 0)
            {
                _engine.Tick();
            }
            else
            {
                var status = _engine.GetStatus();
                if (status.IsActive && status.Remaining <= TimeSpan.Zero)
                {
                    _engine.Tick();
                }
                else
                {
                    _trayIcon?.Update(status);
                    if (_popup?.IsVisible == true)
                    {
                        _popup.UpdateStatus(status, _engine.LatestSnapshot);
                    }
                }
            }
        };
        _timer.Start();

        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _updateTimer.Tick += async (_, _) =>
        {
            if (_updateTimer is not null)
            {
                _updateTimer.Interval = TimeSpan.FromHours(12);
            }

            if (_engine?.Config.AutoCheckForUpdates == true && _updateService is not null)
            {
                await _updateService.CheckForUpdatesAsync(isBackgroundCheck: true);
            }
        };
        _updateTimer.Start();

        _trayIcon.Update(_engine.GetStatus());
    }

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e) => ExitApplication();

    private void SafeRestoreOnCrash()
    {
        try
        {
            _engine?.Stop(StopReason.AppExiting);
        }
        catch
        {
            // Best-effort restore before process terminates.
        }
    }

    private void PrepareForInstallerExit()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(PrepareForInstallerExit);
            return;
        }

        if (_isExiting)
        {
            return;
        }
        _isExiting = true;

        SystemEvents.SessionEnding -= OnSessionEnding;
        _timer?.Stop();
        _updateTimer?.Stop();
        _engine?.Stop(StopReason.AppExiting);
        _trayIcon?.Dispose();
        _popup?.ForceClose();
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch
        {
            // ReleaseMutex may throw if invoked outside the owning thread; Dispose still closes the handle cleanly.
        }
        _singleInstanceMutex?.Dispose();
    }

    private void ExitApplication()
    {
        PrepareForInstallerExit();
        Shutdown();
    }
}
