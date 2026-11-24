using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Nocturnal.App.Services;
using Nocturnal.Core;

namespace Nocturnal.App.UI;

public partial class MainPopupWindow : Window
{
    private const string GitHubRepoUrl = "https://github.com/umamhaniff/nocturnal";
    private const string SupportCoffeeUrl = "https://trakteer.id/umamhaniff/tip";
    private static readonly SolidColorBrush ActiveCrescentBrush = CreateFrozenBrush(0x34, 0xD3, 0x99);
    private static readonly SolidColorBrush ActivePulseBrush = CreateFrozenBrush(0xEC, 0xFD, 0xF5);
    private static readonly SolidColorBrush InactiveCrescentBrush = CreateFrozenBrush(0x64, 0x74, 0x8B);
    private static readonly SolidColorBrush InactivePulseBrush = CreateFrozenBrush(0x94, 0xA3, 0xB8);
    private static readonly SolidColorBrush ActiveHeaderTextBrush = CreateFrozenBrush(0x6E, 0xE7, 0xB7);
    private static readonly SolidColorBrush IdleHeaderTextBrush = CreateFrozenBrush(0x64, 0x74, 0x8B);

    private readonly SessionEngine _engine;
    private readonly AppUpdateService _updateService;
    private readonly Action _exitApp;
    private readonly Action _prepareForInstallerExit;
    private readonly Action _finalizeExit;
    private bool _isInitializing = true;
    private bool _allowClose;

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public MainPopupWindow(
        SessionEngine engine,
        AppUpdateService updateService,
        Action exitApp,
        Action prepareForInstallerExit,
        Action finalizeExit)
    {
        _engine = engine;
        _updateService = updateService;
        _exitApp = exitApp;
        _prepareForInstallerExit = prepareForInstallerExit;
        _finalizeExit = finalizeExit;

        InitializeComponent();
        _updateService.StateChanged += OnUpdateStateChanged;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Hide();
                e.Handled = true;
            }
        };
        SizeChanged += (_, _) =>
        {
            if (IsVisible)
            {
                RepositionNearTray();
            }
        };
        SyncFromConfig();
        ApplyUpdateState(_updateService.State);
        _isInitializing = false;
    }

    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _updateService.StateChanged -= OnUpdateStateChanged;
        base.OnClosing(e);
    }

    public void ToggleNearTray()
    {
        if (IsVisible)
        {
            Hide();
            return;
        }

        SyncFromConfig();
        UpdateStatus(_engine.GetStatus(), _engine.LatestSnapshot);

        Show();
        RepositionNearTray();
        Activate();
    }

    private void RepositionNearTray()
    {
        UpdateLayout();
        var workArea = SystemParameters.WorkArea;
        Left = Math.Max(workArea.Left + 12, workArea.Right - ActualWidth - 12);
        Top = Math.Max(workArea.Top + 12, workArea.Bottom - ActualHeight - 12);
    }

    public void UpdateStatus(SessionStatus status, SystemPowerSnapshot? snapshot)
    {
        if (status.IsActive)
        {
            LogoCrescent.Fill = ActiveCrescentBrush;
            LogoPulse.Stroke = ActivePulseBrush;
            LogoSlashKnockout.Visibility = Visibility.Collapsed;
            LogoSlash.Visibility = Visibility.Collapsed;

            HeaderStatusText.Text = status.ClamshellEnabled ? "Awake • Clamshell" : "Awake";
            HeaderStatusText.Foreground = ActiveHeaderTextBrush;

            StatusTitleText.Text = FormatRemaining(status.Remaining);
            StatusSubText.Text = $"Ends {status.ExpiresAtUtc?.ToLocalTime():HH:mm}";
            UpdateRingArc(1.0 - status.Progress);
            StopButton.Visibility = Visibility.Visible;
        }
        else
        {
            LogoCrescent.Fill = InactiveCrescentBrush;
            LogoPulse.Stroke = InactivePulseBrush;
            LogoSlashKnockout.Visibility = Visibility.Visible;
            LogoSlash.Visibility = Visibility.Visible;

            HeaderStatusText.Text = "Idle";
            HeaderStatusText.Foreground = IdleHeaderTextBrush;

            StatusTitleText.Text = "Idle";
            StatusSubText.Text = status.LastStopReason switch
            {
                StopReason.TimerExpired => "Session completed",
                StopReason.BatteryCritical => "Battery critical stop",
                StopReason.ThermalCritical => "Thermal critical stop",
                StopReason.CrashRecovery => "Recovered power state",
                _ => "Pick duration"
            };
            UpdateRingArc(0);
            StopButton.Visibility = Visibility.Collapsed;
        }

        FocusSummaryText.Text = BuildFocusSummary(status);

        if (snapshot is not null)
        {
            var powerSource = snapshot.IsOnAcPower ? "AC" : "Battery";
            var batt = snapshot.HasBattery ? $"{snapshot.BatteryPercent}%" : "No Battery";
            var temp = snapshot.TemperatureCelsius.HasValue
                ? $"{snapshot.TemperatureCelsius:F0}°C"
                : "Thermal N/A";
            TelemetryText.Text = $"{powerSource} • {batt} • {temp}";
        }
    }

    private string BuildFocusSummary(SessionStatus status)
    {
        if (!status.IsActive || !status.Focus.IsEngaged)
        {
            return _engine.Config.AgentFocusEnabled
                ? "Ready on session start"
                : "Throttle non-agent apps";
        }

        var parts = new List<string> { $"{status.Focus.ThrottledProcessCount} throttled" };
        if (status.Focus.PausedProcessCount > 0)
        {
            parts.Add($"{status.Focus.PausedProcessCount} paused");
        }
        if (status.Focus.ClosedAppCount > 0)
        {
            parts.Add($"{status.Focus.ClosedAppCount} closed");
        }
        return "Active: " + string.Join(", ", parts);
    }

    private void UpdateRingArc(double remainingFraction)
    {
        var fraction = Math.Clamp(remainingFraction, 0.0, 1.0);
        if (fraction <= 0.001)
        {
            CountdownRingFull.Visibility = Visibility.Collapsed;
            CountdownRingArc.Visibility = Visibility.Collapsed;
            return;
        }

        if (fraction >= 0.999)
        {
            CountdownRingArc.Visibility = Visibility.Collapsed;
            CountdownRingFull.Visibility = Visibility.Visible;
            return;
        }

        CountdownRingFull.Visibility = Visibility.Collapsed;
        CountdownRingArc.Visibility = Visibility.Visible;

        const double cx = 73.0;
        const double cy = 73.0;
        const double r = 66.0;

        var angleRad = (fraction * 360.0 - 90.0) * Math.PI / 180.0;
        var endX = cx + (r * Math.Cos(angleRad));
        var endY = cy + (r * Math.Sin(angleRad));

        CountdownArcFigure.StartPoint = new System.Windows.Point(cx, cy - r);
        CountdownArcSegment.Point = new System.Windows.Point(endX, endY);
        CountdownArcSegment.Size = new System.Windows.Size(r, r);
        CountdownArcSegment.IsLargeArc = fraction > 0.5;
        CountdownArcSegment.SweepDirection = SweepDirection.Clockwise;
    }

    private void SyncFromConfig()
    {
        _isInitializing = true;

        var cfg = _engine.Config;
        ClamshellSwitch.IsChecked = cfg.ClamshellEnabled;
        AgentFocusSwitch.IsChecked = cfg.AgentFocusEnabled;
        EcoQosSwitch.IsChecked = cfg.FocusEcoQosEnabled;

        var effectiveMode = cfg.FocusSelectedAppMode != SelectedAppMode.Off
            ? cfg.FocusSelectedAppMode
            : (cfg.FocusCloseSelectedApps ? SelectedAppMode.GracefulClose : SelectedAppMode.Off);
        ModeOffRadio.IsChecked = effectiveMode == SelectedAppMode.Off;
        ModePauseRadio.IsChecked = effectiveMode == SelectedAppMode.DeepPause;
        ModeCloseRadio.IsChecked = effectiveMode == SelectedAppMode.GracefulClose;

        AutoSleepSwitch.IsChecked = cfg.AutoSleepOnCompletion;
        KeepDisplaySwitch.IsChecked = cfg.KeepDisplayAwake;
        ThermalGuardSwitch.IsChecked = cfg.EnableThermalProtection;
        StartupSwitch.IsChecked = StartupManager.IsLaunchAtStartupEnabled();
        AutoUpdateSwitch.IsChecked = cfg.AutoCheckForUpdates;

        LowPowerPctBox.Text = cfg.LowPowerBatteryThresholdPercent.ToString();
        CriticalPctBox.Text = cfg.CriticalBatteryPercent.ToString();
        CustomAppsBox.Text = string.Join(", ", cfg.CustomAppsToManage);
        CustomWhitelistBox.Text = string.Join(", ", cfg.CustomAgentWhitelist);

        SyncAppChips(cfg);
        SyncStyleRadios(cfg.UiStyle);
        ApplyUiStyleLayout(cfg.UiStyle);
        ApplyUpdateState(_updateService.State);

        _isInitializing = false;
    }

    private void SyncAppChips(AppConfig cfg)
    {
        var configured = new HashSet<string>(
            cfg.FocusAppsToClose.Select(ProcessFocusPolicy.NormalizeProcessName),
            StringComparer.OrdinalIgnoreCase);

        foreach (var child in AppChipsPanel.Children)
        {
            if (child is ToggleButton chip && chip.Tag is string tag)
            {
                var targets = SplitTags(tag);
                chip.IsChecked = targets.Any(configured.Contains);
            }
        }
    }

    private void SyncStyleRadios(TrayUiStyle style)
    {
        StylePillsRadio.IsChecked = style == TrayUiStyle.Pills;
        StyleCompactRadio.IsChecked = style == TrayUiStyle.Compact;
        StyleMinimalRadio.IsChecked = style == TrayUiStyle.Minimal;
        StylePopoverRadio.IsChecked = style == TrayUiStyle.Popover;
    }

    private void ApplyUiStyleLayout(TrayUiStyle style)
    {
        var isCompact = style == TrayUiStyle.Compact;
        TabBarBorder.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;
        CustomDurationPanel.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;
        CompactSettingsBtn.Visibility = isCompact ? Visibility.Visible : Visibility.Collapsed;
        CommunityFooterBorder.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;

        if (isCompact)
        {
            TimerTabBtn.IsChecked = true;
            TimerPanel.Visibility = Visibility.Visible;
            FocusPanel.Visibility = Visibility.Collapsed;
            SettingsPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void CompactSettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        _engine.UpdateConfig(c => c.UiStyle = TrayUiStyle.Pills);
        SyncStyleRadios(TrayUiStyle.Pills);
        ApplyUiStyleLayout(TrayUiStyle.Pills);
        SettingsTabBtn.IsChecked = true;
    }

    private void NavTab_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        TimerPanel.Visibility = TimerTabBtn.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        FocusPanel.Visibility = FocusTabBtn.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = SettingsTabBtn.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (IsVisible)
        {
            Hide();
        }
    }

    private void TrayStyleRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || sender is not System.Windows.Controls.RadioButton radio || radio.Tag is not string tag)
        {
            return;
        }

        if (Enum.TryParse<TrayUiStyle>(tag, out var style))
        {
            _engine.UpdateConfig(c => c.UiStyle = style);
            ApplyUiStyleLayout(style);
        }
    }

    private void Preset15m_Click(object sender, RoutedEventArgs e) => _engine.AddTime(TimeSpan.FromMinutes(15));
    private void Preset30m_Click(object sender, RoutedEventArgs e) => _engine.AddTime(TimeSpan.FromMinutes(30));
    private void Preset1h_Click(object sender, RoutedEventArgs e) => _engine.AddTime(TimeSpan.FromHours(1));
    private void Preset4h_Click(object sender, RoutedEventArgs e) => _engine.AddTime(TimeSpan.FromHours(4));

    private void StartCustom_Click(object sender, RoutedEventArgs e)
    {
        _ = int.TryParse(DaysBox.Text, out var days);
        _ = int.TryParse(HoursBox.Text, out var hours);
        _ = int.TryParse(MinutesBox.Text, out var mins);

        var duration = new TimeSpan(Math.Max(0, days), Math.Max(0, hours), Math.Max(0, mins), 0);
        if (duration > TimeSpan.Zero)
        {
            _engine.Start(duration);
        }
        else
        {
            StatusSubText.Text = "Enter duration > 0";
        }
    }

    private void ClamshellSwitch_Click(object sender, RoutedEventArgs e) =>
        _engine.SetClamshell(ClamshellSwitch.IsChecked == true);

    private void AgentFocusSwitch_Click(object sender, RoutedEventArgs e) =>
        _engine.SetAgentFocus(AgentFocusSwitch.IsChecked == true);

    private void EcoQosSwitch_Click(object sender, RoutedEventArgs e)
    {
        var enabled = EcoQosSwitch.IsChecked == true;
        _engine.UpdateConfig(c =>
        {
            c.FocusEcoQosEnabled = enabled;
            c.FocusRamTrimEnabled = enabled;
        });
        _engine.ReapplyAgentFocusIfActive();
    }

    private void SelectedAppModeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || sender is not System.Windows.Controls.RadioButton radio || radio.Tag is not string tag)
        {
            return;
        }

        if (Enum.TryParse<SelectedAppMode>(tag, out var mode))
        {
            _engine.UpdateConfig(c =>
            {
                c.FocusSelectedAppMode = mode;
                c.FocusCloseSelectedApps = mode == SelectedAppMode.GracefulClose;
            });
            _engine.ReapplyAgentFocusIfActive();
        }
    }

    private void SaveCustomApps_Click(object sender, RoutedEventArgs e) => SaveCustomApps();
    private void CustomAppsBox_LostFocus(object sender, RoutedEventArgs e) => SaveCustomApps();

    private void CustomAppsBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SaveCustomApps();
        }
    }

    private void SaveCustomApps()
    {
        if (_isInitializing)
        {
            return;
        }

        var items = ParseProcessList(CustomAppsBox.Text);
        _engine.UpdateConfig(c => c.CustomAppsToManage = items);
        _engine.ReapplyAgentFocusIfActive();
    }

    private void AppChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton chip || chip.Tag is not string tag)
        {
            return;
        }

        var targets = SplitTags(tag);
        var isChecked = chip.IsChecked == true;

        _engine.UpdateConfig(c =>
        {
            var set = new HashSet<string>(
                c.FocusAppsToClose.Select(ProcessFocusPolicy.NormalizeProcessName),
                StringComparer.OrdinalIgnoreCase);

            foreach (var target in targets)
            {
                if (isChecked)
                {
                    set.Add(target);
                }
                else
                {
                    set.Remove(target);
                }
            }

            c.FocusAppsToClose = set.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        });

        _engine.ReapplyAgentFocusIfActive();
    }

    private void SaveWhitelist_Click(object sender, RoutedEventArgs e) => SaveCustomWhitelist();
    private void CustomWhitelistBox_LostFocus(object sender, RoutedEventArgs e) => SaveCustomWhitelist();

    private void CustomWhitelistBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SaveCustomWhitelist();
        }
    }

    private void SaveCustomWhitelist()
    {
        if (_isInitializing)
        {
            return;
        }

        var items = ParseProcessList(CustomWhitelistBox.Text);

        _engine.UpdateConfig(c => c.CustomAgentWhitelist = items);
        _engine.ReapplyAgentFocusIfActive();
    }

    private void StarGitHub_Click(object sender, RoutedEventArgs e) => OpenUrl(GitHubRepoUrl);
    private void SupportCoffee_Click(object sender, RoutedEventArgs e) => OpenUrl(SupportCoffeeUrl);

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Ignore shell launch failure if no default browser is registered.
        }
    }

    private static List<string> ParseProcessList(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ProcessFocusPolicy.NormalizeProcessName)
            .Where(x => !string.IsNullOrEmpty(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private void AutoSleepSwitch_Click(object sender, RoutedEventArgs e) =>
        _engine.UpdateConfig(c => c.AutoSleepOnCompletion = AutoSleepSwitch.IsChecked == true);

    private void KeepDisplaySwitch_Click(object sender, RoutedEventArgs e) =>
        _engine.UpdateConfig(c => c.KeepDisplayAwake = KeepDisplaySwitch.IsChecked == true);

    private void ThermalGuardSwitch_Click(object sender, RoutedEventArgs e) =>
        _engine.UpdateConfig(c => c.EnableThermalProtection = ThermalGuardSwitch.IsChecked == true);

    private void StartupSwitch_Click(object sender, RoutedEventArgs e)
    {
        var enabled = StartupSwitch.IsChecked == true;
        _engine.UpdateConfig(c => c.LaunchAtStartup = enabled);
        StartupManager.SetLaunchAtStartup(enabled);
    }

    private async void AutoUpdateSwitch_Click(object sender, RoutedEventArgs e)
    {
        var enabled = AutoUpdateSwitch.IsChecked == true;
        _engine.UpdateConfig(c => c.AutoCheckForUpdates = enabled);
        if (enabled && _updateService.State.Phase == UpdatePhase.Idle)
        {
            await _updateService.CheckForUpdatesAsync(isBackgroundCheck: true);
        }
    }

    private async void CheckOrInstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_updateService.State.Phase == UpdatePhase.Available
            || (_updateService.State.Phase == UpdatePhase.Error && _updateService.State.Release is not null))
        {
            await StartUpdateInstallAsync();
            return;
        }

        await _updateService.CheckForUpdatesAsync(isBackgroundCheck: false);
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e) =>
        await StartUpdateInstallAsync();

    private async Task StartUpdateInstallAsync() =>
        await _updateService.DownloadAndInstallAsync(_prepareForInstallerExit, _finalizeExit);

    private void OnUpdateStateChanged(UpdateUiState state) =>
        Dispatcher.BeginInvoke(() => ApplyUpdateState(state));

    private void ApplyUpdateState(UpdateUiState state)
    {
        var ver = state.CurrentVersion;
        VersionText.Text = $"Nocturnal v{ver.Major}.{ver.Minor}.{Math.Max(0, ver.Build)}";

        var canRetryDownload = state.Phase == UpdatePhase.Error && state.Release is not null;
        var showBanner = state.Phase is UpdatePhase.Available or UpdatePhase.Downloading or UpdatePhase.Installing
            || canRetryDownload;
        UpdateBannerBorder.Visibility = showBanner ? Visibility.Visible : Visibility.Collapsed;

        var isBusy = state.Phase is UpdatePhase.Checking or UpdatePhase.Downloading or UpdatePhase.Installing;
        UpdateBannerBtn.IsEnabled = state.Phase == UpdatePhase.Available || canRetryDownload;
        CheckUpdateBtn.IsEnabled = !isBusy;

        if (state.Release is { } release)
        {
            UpdateBannerTitle.Text = $"Update {release.TagName} available";
            UpdateBannerSub.Text = state.Phase switch
            {
                UpdatePhase.Downloading => state.Message ?? "Downloading...",
                UpdatePhase.Installing => state.Message ?? "Launching installer...",
                UpdatePhase.Error => $"{state.Message ?? "Download failed"} — click Retry",
                _ => string.IsNullOrWhiteSpace(release.InstallerDownloadUrl)
                    ? "Open release page to download"
                    : "Ready for one-click install"
            };
            UpdateBannerBtn.Content = state.Phase switch
            {
                UpdatePhase.Downloading => $"{(int)Math.Round(state.DownloadProgress * 100)}%",
                UpdatePhase.Installing => "Installing",
                UpdatePhase.Error => "Retry",
                _ => "Update"
            };
        }

        UpdateStatusText.Text = state.Phase switch
        {
            UpdatePhase.Checking => "Checking for updates...",
            UpdatePhase.UpToDate => "Up to date",
            UpdatePhase.Available => $"Update {state.Release?.TagName} ready",
            UpdatePhase.Downloading => state.Message ?? "Downloading...",
            UpdatePhase.Installing => state.Message ?? "Launching installer...",
            UpdatePhase.Error => state.Message ?? "Update check failed",
            _ => "Check GitHub Releases"
        };

        CheckUpdateBtn.Content = state.Phase switch
        {
            UpdatePhase.Available => "Update",
            UpdatePhase.Error when state.Release is not null => "Retry",
            UpdatePhase.Checking => "Checking",
            UpdatePhase.Downloading => "Downloading",
            UpdatePhase.Installing => "Installing",
            _ => "Check"
        };
    }

    private void BatteryThreshold_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        var low = int.TryParse(LowPowerPctBox.Text, out var l) ? Math.Clamp(l, 0, 99) : _engine.Config.LowPowerBatteryThresholdPercent;
        var crit = int.TryParse(CriticalPctBox.Text, out var cVal) ? Math.Clamp(cVal, 0, 99) : _engine.Config.CriticalBatteryPercent;
        if (low > 0 && crit >= low)
        {
            crit = Math.Max(0, low - 1);
        }

        LowPowerPctBox.Text = low.ToString();
        CriticalPctBox.Text = crit.ToString();

        _engine.UpdateConfig(c =>
        {
            c.LowPowerBatteryThresholdPercent = low;
            c.CriticalBatteryPercent = crit;
        });
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _engine.Stop(StopReason.Manual);
    private void Exit_Click(object sender, RoutedEventArgs e) => _exitApp();

    private static string[] SplitTags(string tag) =>
        tag.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string FormatRemaining(TimeSpan ts)
    {
        if (ts.TotalDays >= 1)
        {
            return $"{(int)ts.TotalDays}d {ts.Hours:D2}h {ts.Minutes:D2}m";
        }
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
    }
}
