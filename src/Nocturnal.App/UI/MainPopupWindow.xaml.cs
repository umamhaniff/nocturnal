using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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
    private static readonly SolidColorBrush IdleHeaderTextBrush = CreateFrozenBrush(0x8A, 0x99, 0xB0);

    private readonly SessionEngine _engine;
    private readonly AppUpdateService _updateService;
    private readonly HttpAlertNotifier _alertNotifier;
    private readonly Action _exitApp;
    private readonly Action _prepareForInstallerExit;
    private readonly Action _finalizeExit;
    private readonly DispatcherTimer _idleTrimTimer;
    private readonly DispatcherTimer _bannerTimer;
    private bool _isInitializing = true;
    private bool _allowClose;
    private bool _suppressDeactivateHide;
    private bool _isWebhookSync;
    private bool _webhookRevealed;
    private bool? _nightShiftExpanded;
    private bool? _wifiFailoverExpanded;
    private bool? _alertsExpanded;
    private string? _pendingUnrunningWatch;
    private DateTimeOffset? _confirmExitUntilUtc;
    private DateTimeOffset? _confirmUpdateUntilUtc;
    private (bool IsActive, StopReason Reason, int HistoryCount)? _lastHistoryRenderKey;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool K32EmptyWorkingSet(IntPtr hProcess);

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public MainPopupWindow(
        SessionEngine engine,
        AppUpdateService updateService,
        HttpAlertNotifier alertNotifier,
        Action exitApp,
        Action prepareForInstallerExit,
        Action finalizeExit)
    {
        _engine = engine;
        _updateService = updateService;
        _alertNotifier = alertNotifier;
        _exitApp = exitApp;
        _prepareForInstallerExit = prepareForInstallerExit;
        _finalizeExit = finalizeExit;

        InitializeComponent();
        _bannerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _bannerTimer.Tick += (_, _) =>
        {
            _bannerTimer.Stop();
            ValidationBannerBorder.Visibility = Visibility.Collapsed;
        };
        _idleTrimTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _idleTrimTimer.Tick += (_, _) =>
        {
            _idleTrimTimer.Stop();
            if (!IsVisible && !_allowClose)
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false);
                K32EmptyWorkingSet(GetCurrentProcess());
            }
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                _idleTrimTimer.Stop();
            }
            else if (!_allowClose)
            {
                _idleTrimTimer.Stop();
                _idleTrimTimer.Start();
            }
        };

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
        _idleTrimTimer.Stop();
        _bannerTimer.Stop();
        Close();
    }

    private void ShowValidationBanner(string message)
    {
        ValidationBannerText.Text = message;
        ValidationBannerBorder.Visibility = Visibility.Visible;
        _bannerTimer.Stop();
        _bannerTimer.Start();
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

        ShowNearTray();
    }

    public void ShowNearTray()
    {
        _lastHistoryRenderKey = null;
        SyncFromConfig();
        UpdateStatus(_engine.GetStatus(), _engine.LatestSnapshot);

        if (!IsVisible)
        {
            Show();
        }
        RepositionNearTray();
        Activate();
    }

    public void SyncFromConfigIfVisible()
    {
        if (!IsVisible)
        {
            return;
        }

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
        ScopeAllNonAiRadio.IsChecked = cfg.FocusApplyToAllNonAiApps;
        ScopeSelectedRadio.IsChecked = !cfg.FocusApplyToAllNonAiApps;
        SelectedAppsCard.Visibility = cfg.FocusApplyToAllNonAiApps ? Visibility.Collapsed : Visibility.Visible;
        FocusLidOnlySwitch.IsChecked = cfg.FocusPauseOnlyWhenLidClosed;

        AutoSleepSwitch.IsChecked = cfg.AutoSleepOnCompletion;
        KeepDisplaySwitch.IsChecked = cfg.KeepDisplayAwake;
        ThermalGuardSwitch.IsChecked = cfg.EnableThermalProtection;
        StartupSwitch.IsChecked = StartupManager.IsLaunchAtStartupEnabled();
        AutoUpdateSwitch.IsChecked = cfg.AutoCheckForUpdates;
        _isInitializing = false;
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

            HeaderStatusText.Text = status.ClamshellEnabled
                ? "Awake • Clamshell"
                : status.ClamshellApplyFailed
                    ? "Awake • Lid Failed"
                    : "Awake";
            HeaderStatusText.Foreground = ActiveHeaderTextBrush;

            StatusTitleText.Text = FormatRemaining(status.Remaining);
            StatusSubText.Text = status.WatchTarget is { } watch
                ? $"Watching: {watch.ProcessName} (max {status.ExpiresAtUtc?.ToLocalTime():HH:mm})"
                : status.ActiveProfileName is { } profName
                    ? $"{profName} • Ends {status.ExpiresAtUtc?.ToLocalTime():HH:mm}"
                    : $"Ends {status.ExpiresAtUtc?.ToLocalTime():HH:mm}";
            StatusSubText.ToolTip = StatusSubText.Text;
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
                StopReason.WatchedProcessExited => "Watched process exited",
                StopReason.BatteryCritical => "Battery critical stop",
                StopReason.ThermalCritical => "Thermal critical stop",
                StopReason.CrashRecovery => "Recovered power state",
                _ => "Pick duration"
            };
            StatusSubText.ToolTip = null;
            UpdateRingArc(0);
            StopButton.Visibility = Visibility.Collapsed;
        }

        if (status.PendingSleepSeconds is { } sleepSecs)
        {
            PendingSleepText.Text = $"Session ended — sleeping in {sleepSecs}s";
            PendingSleepBannerBorder.Visibility = Visibility.Visible;
        }
        else
        {
            PendingSleepBannerBorder.Visibility = Visibility.Collapsed;
        }

        FocusSummaryText.Text = BuildFocusSummary(status);
        UpdateFocusTelemetryBanner(status);

        var historyKey = (status.IsActive, status.LastStopReason, _engine.GetRecentHistory().Count);
        if (_lastHistoryRenderKey != historyKey)
        {
            _lastHistoryRenderKey = historyKey;
            UpdateHistorySummary();
        }

        if (snapshot is not null)
        {
            var powerSource = snapshot.IsOnAcPower ? "AC" : "Battery";
            var batt = snapshot.HasBattery ? $"{snapshot.BatteryPercent}%" : "No Battery";
            var temp = snapshot.TemperatureCelsius.HasValue
                ? $"{snapshot.TemperatureCelsius:F0}°C"
                : "Thermal N/A";
            var wifiTag = status.WifiFailoverTriggered ? " • Backup Wi-Fi" : "";
            TelemetryText.Text = $"{powerSource} • {batt} • {temp}{wifiTag}";

            ThermalGuardSubText.Text = snapshot.TemperatureCelsius.HasValue
                ? $"{_engine.Config.WarmTempCelsius:F0}°C warn / {_engine.Config.CriticalTempCelsius:F0}°C sleep"
                : "Thermal sensor unavailable on this laptop";
        }
    }

    private string BuildFocusSummary(SessionStatus status)
    {
        if (!status.IsActive || !status.Focus.IsEngaged)
        {
            if (!_engine.Config.AgentFocusEnabled)
            {
                return "Throttle non-agent apps";
            }

            if (_engine.Config.FocusPauseOnlyWhenLidClosed)
            {
                return _engine.Config.FocusApplyToAllNonAiApps
                    ? "Armed: pauses all non-AI apps on lid close"
                    : "Armed: pauses selected apps on lid close";
            }

            return "Ready on session start";
        }

        var parts = new List<string> { $"{status.Focus.ThrottledProcessCount} throttled" };
        if (status.FocusArmedForLidClose)
        {
            parts.Add(_engine.Config.FocusApplyToAllNonAiApps
                ? "armed (all non-AI on lid close)"
                : "armed (selected on lid close)");
        }
        else if (status.Focus.PausedProcessCount > 0)
        {
            parts.Add($"{status.Focus.PausedProcessCount} paused");
        }
        if (status.Focus.ClosedAppCount > 0)
        {
            parts.Add($"{status.Focus.ClosedAppCount} closed");
        }
        if (status.Focus.ReclaimedRamBytes > 0)
        {
            var mb = Math.Round(status.Focus.ReclaimedRamBytes / (1024.0 * 1024.0), 0);
            parts.Add($"~{mb} MB working set trimmed");
        }
        return "Active: " + string.Join(", ", parts);
    }

    private void UpdateFocusTelemetryBanner(SessionStatus status)
    {
        if (status.IsActive && status.Focus.IsEngaged)
        {
            var mb = Math.Round(status.Focus.ReclaimedRamBytes / (1024.0 * 1024.0), 1);
            var pausePart = status.FocusArmedForLidClose
                ? (_engine.Config.FocusApplyToAllNonAiApps ? "all non-AI armed for lid close" : "selected armed for lid close")
                : $"{status.Focus.PausedProcessCount} paused";
            FocusTelemetryText.Text = $"Trimmed ~{mb} MB working set • {pausePart} • {status.Focus.ThrottledProcessCount} EcoQoS";
            FocusTelemetryBorder.Visibility = Visibility.Visible;
        }
        else
        {
            FocusTelemetryBorder.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateHistorySummary()
    {
        var history = _engine.GetRecentHistory();
        if (history.Count == 0)
        {
            LastSessionText.Text = "No recent sessions";
            LastSessionText.ToolTip = null;
            return;
        }

        var latest = history[0];
        var mb = Math.Round(latest.ReclaimedRamBytes / (1024.0 * 1024.0), 0);
        var durStr = latest.ActualDuration.TotalHours >= 1
            ? $"{(int)latest.ActualDuration.TotalHours}h {latest.ActualDuration.Minutes}m"
            : $"{Math.Max(1, (int)Math.Round(latest.ActualDuration.TotalMinutes))}m";
        var reasonStr = latest.StopReason switch
        {
            StopReason.TimerExpired => "Completed",
            StopReason.WatchedProcessExited => $"Watched {latest.WatchedProcessName ?? "process"}",
            StopReason.BatteryCritical => "Battery critical",
            StopReason.ThermalCritical => "Thermal critical",
            StopReason.AppExiting => "App closed",
            _ => "Stopped"
        };
        var ramPart = mb > 0 ? $" • ~{mb} MB trimmed" : "";
        LastSessionText.Text = $"Last: {durStr} • {reasonStr}{ramPart}";

        var tipLines = history.Take(5).Select(h =>
        {
            var d = h.ActualDuration.TotalHours >= 1
                ? $"{(int)h.ActualDuration.TotalHours}h {h.ActualDuration.Minutes}m"
                : $"{Math.Max(1, (int)Math.Round(h.ActualDuration.TotalMinutes))}m";
            var rMb = Math.Round(h.ReclaimedRamBytes / (1024.0 * 1024.0), 1);
            var w = string.IsNullOrWhiteSpace(h.WatchedProcessName) ? "" : $" [{h.WatchedProcessName}]";
            return $"{h.EndedAtUtc.ToLocalTime():MM/dd HH:mm} — {d} ({h.StopReason}){w}, ~{rMb} MB";
        });
        LastSessionText.ToolTip = string.Join(Environment.NewLine, tipLines);
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
        ScopeAllNonAiRadio.IsChecked = cfg.FocusApplyToAllNonAiApps;
        ScopeSelectedRadio.IsChecked = !cfg.FocusApplyToAllNonAiApps;
        SelectedAppsCard.Visibility = cfg.FocusApplyToAllNonAiApps ? Visibility.Collapsed : Visibility.Visible;
        FocusLidOnlySwitch.IsChecked = cfg.FocusPauseOnlyWhenLidClosed;

        AutoSleepSwitch.IsChecked = cfg.AutoSleepOnCompletion;
        KeepDisplaySwitch.IsChecked = cfg.KeepDisplayAwake;
        ThermalGuardSwitch.IsChecked = cfg.EnableThermalProtection;
        StartupSwitch.IsChecked = StartupManager.IsLaunchAtStartupEnabled();
        AutoUpdateSwitch.IsChecked = cfg.AutoCheckForUpdates;

        LowPowerPctBox.Text = cfg.LowPowerBatteryThresholdPercent.ToString();
        CriticalPctBox.Text = cfg.CriticalBatteryPercent.ToString();
        CustomAppsBox.Text = string.Join(", ", cfg.CustomAppsToManage);
        CustomWhitelistBox.Text = string.Join(", ", cfg.CustomAgentWhitelist);

        NightShiftSwitch.IsChecked = cfg.NightShiftEnabled;
        NightShiftTimeBox.Text = cfg.NightShiftStartTime;
        NightShiftDurationBox.Text = cfg.NightShiftDurationMinutes.ToString();
        NightShiftAcSwitch.IsChecked = cfg.NightShiftRequireAcPower;
        NsAiRadio.IsChecked = string.Equals(cfg.NightShiftProfileName, "AI Overnight", StringComparison.OrdinalIgnoreCase);
        NsBuildRadio.IsChecked = string.Equals(cfg.NightShiftProfileName, "Build / Scraper", StringComparison.OrdinalIgnoreCase);
        NsPresentRadio.IsChecked = string.Equals(cfg.NightShiftProfileName, "Presentation", StringComparison.OrdinalIgnoreCase);
        if (NsAiRadio.IsChecked != true && NsBuildRadio.IsChecked != true && NsPresentRadio.IsChecked != true)
        {
            NsAiRadio.IsChecked = true;
        }

        WifiFailoverSwitch.IsChecked = cfg.WifiFailoverEnabled;
        BackupSsidBox.Text = cfg.BackupWifiSsid;

        AlertsSwitch.IsChecked = cfg.AlertsEnabled;
        _isWebhookSync = true;
        WebhookUrlBox.Text = cfg.AlertWebhookUrl;
        WebhookPasswordBox.Password = cfg.AlertWebhookUrl;
        _isWebhookSync = false;
        UpdateWebhookPlaceholder();
        AlertOnCompletionCheck.IsChecked = cfg.AlertOnCompletion;
        AlertOnGuardCheck.IsChecked = cfg.AlertOnSafetyGuard;

        ApplySettingsCardExpansions(cfg);
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
        WatchProcessPanel.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;
        LastSessionText.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;
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
        if (_suppressDeactivateHide)
        {
            return;
        }

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

    private void ProfileAi_Click(object sender, RoutedEventArgs e)
    {
        _engine.ApplyProfile("AI Overnight", startImmediately: true);
        SyncFromConfig();
        ShowClamshellFailedBannerIfNeeded();
    }

    private void ProfileBuild_Click(object sender, RoutedEventArgs e)
    {
        _engine.ApplyProfile("Build / Scraper", startImmediately: true);
        SyncFromConfig();
        ShowClamshellFailedBannerIfNeeded();
    }

    private void ProfilePresent_Click(object sender, RoutedEventArgs e)
    {
        _engine.ApplyProfile("Presentation", startImmediately: true);
        SyncFromConfig();
    }

    private void StartCustom_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(DaysBox.Text, out var days))
        {
            days = 0;
        }
        if (!int.TryParse(HoursBox.Text, out var hours))
        {
            hours = 0;
        }
        if (!int.TryParse(MinutesBox.Text, out var mins))
        {
            mins = 0;
        }

        long totalMinutes = (Math.Clamp((long)days, 0, 30) * 1440L)
            + (Math.Clamp((long)hours, 0, 720) * 60L)
            + Math.Clamp((long)mins, 0, 43200);
        totalMinutes = Math.Min(totalMinutes, 30L * 1440L);

        if (totalMinutes > 0)
        {
            _engine.Start(TimeSpan.FromMinutes(totalMinutes));
            ShowClamshellFailedBannerIfNeeded();
        }
        else
        {
            ShowValidationBanner("Enter a positive duration (days, hours, or minutes).");
        }
    }

    private void WatchProcess_Click(object sender, RoutedEventArgs e) => StartWatchFromInput();

    private void PickWatchProcess_Click(object sender, RoutedEventArgs e)
    {
        if (PickProcessName() is { } picked)
        {
            WatchProcessBox.Text = picked;
        }
    }

    private void PickExtraApp_Click(object sender, RoutedEventArgs e)
    {
        if (PickProcessName() is { } picked)
        {
            var items = ParseProcessList(CustomAppsBox.Text);
            if (!items.Contains(picked, StringComparer.OrdinalIgnoreCase))
            {
                items.Add(picked);
                CustomAppsBox.Text = string.Join(", ", items);
                _engine.UpdateConfig(c => c.CustomAppsToManage = items);
                _engine.ReapplyAgentFocusIfActive();
            }
        }
    }

    private void PickWhitelistApp_Click(object sender, RoutedEventArgs e)
    {
        if (PickProcessName() is { } picked)
        {
            var items = ParseProcessList(CustomWhitelistBox.Text);
            if (!items.Contains(picked, StringComparer.OrdinalIgnoreCase))
            {
                items.Add(picked);
                CustomWhitelistBox.Text = string.Join(", ", items);
                _engine.UpdateConfig(c => c.CustomAgentWhitelist = items);
                _engine.ReapplyAgentFocusIfActive();
            }
        }
    }

    private string? PickProcessName()
    {
        _suppressDeactivateHide = true;
        try
        {
            var apps = _engine.ListRunningSessionApps();
            var picker = new AppPickerWindow(apps) { Owner = this };
            return picker.ShowDialog() == true ? picker.SelectedProcessName : null;
        }
        finally
        {
            _suppressDeactivateHide = false;
            Activate();
        }
    }

    private void WatchProcessBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            StartWatchFromInput();
        }
    }

    private void StartWatchFromInput()
    {
        var raw = WatchProcessBox.Text;
        var norm = ProcessFocusPolicy.NormalizeProcessName(raw);
        if (string.IsNullOrEmpty(norm))
        {
            _pendingUnrunningWatch = null;
            ShowValidationBanner("Enter a process name to watch (or click Pick).");
            return;
        }

        if (!_engine.IsProcessCurrentlyRunning(norm)
            && !string.Equals(_pendingUnrunningWatch, norm, StringComparison.OrdinalIgnoreCase))
        {
            _pendingUnrunningWatch = norm;
            ShowValidationBanner($"'{norm}' is not running — click Watch again to start anyway.");
            return;
        }

        try
        {
            _pendingUnrunningWatch = null;
            ValidationBannerBorder.Visibility = Visibility.Collapsed;
            _engine.StartWatchingProcess(norm);
            ShowClamshellFailedBannerIfNeeded();
        }
        catch (ArgumentException)
        {
            ShowValidationBanner("Enter a valid process name.");
        }
    }

    private void ShowClamshellFailedBannerIfNeeded()
    {
        if (_engine.GetStatus().ClamshellApplyFailed)
        {
            ShowValidationBanner("Could not change OS lid-close action (blocked by Windows/OEM policy). Clamshell is OFF.");
        }
    }

    private void CancelPendingSleep_Click(object sender, RoutedEventArgs e) => _engine.CancelPendingSleep();

    private async void DisplayOff_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        await Task.Delay(250);
        _engine.TurnOffDisplay();
    }

    private void ClamshellSwitch_Click(object sender, RoutedEventArgs e)
    {
        _engine.SetClamshell(ClamshellSwitch.IsChecked == true);
        SyncFromConfigIfVisible();
        ShowClamshellFailedBannerIfNeeded();
    }

    private void AgentFocusSwitch_Click(object sender, RoutedEventArgs e) =>
        _engine.SetAgentFocus(AgentFocusSwitch.IsChecked == true);

    private void FocusLidOnlySwitch_Click(object sender, RoutedEventArgs e)
    {
        _engine.UpdateConfig(c => c.FocusPauseOnlyWhenLidClosed = FocusLidOnlySwitch.IsChecked == true);
        _engine.ReapplyAgentFocusIfActive();
    }

    private void FocusScopeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || sender is not System.Windows.Controls.RadioButton radio || radio.Tag is not string tag)
        {
            return;
        }

        var applyAll = string.Equals(tag, "All", StringComparison.OrdinalIgnoreCase);
        SelectedAppsCard.Visibility = applyAll ? Visibility.Collapsed : Visibility.Visible;
        _engine.UpdateConfig(c => c.FocusApplyToAllNonAiApps = applyAll);
        _engine.ReapplyAgentFocusIfActive();
    }

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
        if (items.SequenceEqual(_engine.Config.CustomAppsToManage, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

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
        if (items.SequenceEqual(_engine.Config.CustomAgentWhitelist, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

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

    private void NightShiftHeader_Click(object sender, MouseButtonEventArgs e)
    {
        _nightShiftExpanded = NightShiftBody.Visibility != Visibility.Visible;
        ApplySettingsCardExpansions(_engine.Config);
    }

    private void WifiFailoverHeader_Click(object sender, MouseButtonEventArgs e)
    {
        _wifiFailoverExpanded = WifiFailoverBody.Visibility != Visibility.Visible;
        ApplySettingsCardExpansions(_engine.Config);
    }

    private void AlertsHeader_Click(object sender, MouseButtonEventArgs e)
    {
        _alertsExpanded = AlertsBody.Visibility != Visibility.Visible;
        ApplySettingsCardExpansions(_engine.Config);
    }

    private void ApplySettingsCardExpansions(AppConfig cfg)
    {
        var nsOpen = _nightShiftExpanded ?? cfg.NightShiftEnabled;
        NightShiftBody.Visibility = nsOpen ? Visibility.Visible : Visibility.Collapsed;
        NightShiftChevron.Text = nsOpen ? "▾ " : "▸ ";

        var wifiOpen = _wifiFailoverExpanded ?? cfg.WifiFailoverEnabled;
        WifiFailoverBody.Visibility = wifiOpen ? Visibility.Visible : Visibility.Collapsed;
        WifiFailoverChevron.Text = wifiOpen ? "▾ " : "▸ ";

        var alertsOpen = _alertsExpanded ?? cfg.AlertsEnabled;
        AlertsBody.Visibility = alertsOpen ? Visibility.Visible : Visibility.Collapsed;
        AlertsChevron.Text = alertsOpen ? "▾ " : "▸ ";
    }

    private void NightShiftSwitch_Click(object sender, RoutedEventArgs e)
    {
        var enabled = NightShiftSwitch.IsChecked == true;
        if (enabled)
        {
            _nightShiftExpanded = true;
        }
        _engine.UpdateConfig(c => c.NightShiftEnabled = enabled);
        ApplySettingsCardExpansions(_engine.Config);
    }

    private void NightShiftAcSwitch_Click(object sender, RoutedEventArgs e) =>
        _engine.UpdateConfig(c => c.NightShiftRequireAcPower = NightShiftAcSwitch.IsChecked == true);

    private void NightShiftBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        var timeText = SchedulePolicy.TryParseTimeOfDay(NightShiftTimeBox.Text, out var parsed)
            ? $"{(int)parsed.TotalHours:D2}:{parsed.Minutes:D2}"
            : _engine.Config.NightShiftStartTime;
        var mins = int.TryParse(NightShiftDurationBox.Text, out var m)
            ? Math.Clamp(m, 5, 1440)
            : _engine.Config.NightShiftDurationMinutes;

        NightShiftTimeBox.Text = timeText;
        NightShiftDurationBox.Text = mins.ToString();
        _engine.UpdateConfig(c =>
        {
            c.NightShiftStartTime = timeText;
            c.NightShiftDurationMinutes = mins;
        });
    }

    private void NightShiftProfile_Checked(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || sender is not System.Windows.Controls.RadioButton radio || radio.Tag is not string tag)
        {
            return;
        }

        _engine.UpdateConfig(c => c.NightShiftProfileName = tag);
    }

    private void WifiFailoverSwitch_Click(object sender, RoutedEventArgs e)
    {
        SaveBackupSsid();
        var enabled = WifiFailoverSwitch.IsChecked == true;
        if (enabled)
        {
            _wifiFailoverExpanded = true;
        }
        _engine.UpdateConfig(c => c.WifiFailoverEnabled = enabled);
        ApplySettingsCardExpansions(_engine.Config);
    }

    private void SaveBackupSsid_Click(object sender, RoutedEventArgs e) => SaveBackupSsid();
    private void BackupSsidBox_LostFocus(object sender, RoutedEventArgs e) => SaveBackupSsid();

    private void SaveBackupSsid()
    {
        if (_isInitializing)
        {
            return;
        }

        var ssid = (BackupSsidBox.Text ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(ssid) && !NetworkFailoverPolicy.IsValidSsid(ssid))
        {
            BackupSsidBox.Text = _engine.Config.BackupWifiSsid;
            return;
        }

        _engine.UpdateConfig(c => c.BackupWifiSsid = ssid);
    }

    private void AlertsSwitch_Click(object sender, RoutedEventArgs e)
    {
        SaveWebhookUrl();
        var enabled = AlertsSwitch.IsChecked == true;
        if (enabled && !WebhookPolicy.IsValidWebhookUrl(_engine.Config.AlertWebhookUrl))
        {
            AlertsSwitch.IsChecked = false;
            _alertsExpanded = true;
            ApplySettingsCardExpansions(_engine.Config);
            ShowValidationBanner("Enter and save a valid https:// webhook URL first.");
            return;
        }

        if (enabled)
        {
            _alertsExpanded = true;
        }
        _engine.UpdateConfig(c => c.AlertsEnabled = enabled);
        ApplySettingsCardExpansions(_engine.Config);
    }

    private void AlertFilterCheck_Click(object sender, RoutedEventArgs e) =>
        _engine.UpdateConfig(c =>
        {
            c.AlertOnCompletion = AlertOnCompletionCheck.IsChecked == true;
            c.AlertOnSafetyGuard = AlertOnGuardCheck.IsChecked == true;
        });

    private void WebhookPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || _isWebhookSync)
        {
            return;
        }

        _isWebhookSync = true;
        WebhookUrlBox.Text = WebhookPasswordBox.Password;
        _isWebhookSync = false;
        UpdateWebhookPlaceholder();
    }

    private void ToggleWebhookReveal_Click(object sender, RoutedEventArgs e)
    {
        _isWebhookSync = true;
        var current = CurrentWebhookInput();
        _webhookRevealed = !_webhookRevealed;
        if (_webhookRevealed)
        {
            WebhookUrlBox.Text = current;
            WebhookPasswordBox.Visibility = Visibility.Collapsed;
            WebhookUrlBox.Visibility = Visibility.Visible;
            ToggleWebhookRevealBtn.Content = "Hide";
            WebhookUrlBox.Focus();
            WebhookUrlBox.CaretIndex = WebhookUrlBox.Text.Length;
        }
        else
        {
            WebhookPasswordBox.Password = current;
            WebhookUrlBox.Visibility = Visibility.Collapsed;
            WebhookPasswordBox.Visibility = Visibility.Visible;
            ToggleWebhookRevealBtn.Content = "Show";
            WebhookPasswordBox.Focus();
        }
        _isWebhookSync = false;
        UpdateWebhookPlaceholder();
    }

    private string CurrentWebhookInput() =>
        _webhookRevealed ? (WebhookUrlBox.Text ?? string.Empty) : (WebhookPasswordBox.Password ?? string.Empty);

    private void UpdateWebhookPlaceholder()
    {
        WebhookPlaceholder.Visibility = !_webhookRevealed && string.IsNullOrEmpty(WebhookPasswordBox.Password)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void SaveWebhookUrl_Click(object sender, RoutedEventArgs e) => SaveWebhookUrl();
    private void WebhookUrlBox_LostFocus(object sender, RoutedEventArgs e) => SaveWebhookUrl();

    private void SaveWebhookUrl()
    {
        if (_isInitializing || _isWebhookSync)
        {
            return;
        }

        var raw = CurrentWebhookInput().Trim();
        _isWebhookSync = true;
        WebhookUrlBox.Text = raw;
        if (WebhookPasswordBox.Password != raw)
        {
            WebhookPasswordBox.Password = raw;
        }
        _isWebhookSync = false;
        UpdateWebhookPlaceholder();

        if (!string.IsNullOrEmpty(raw) && !WebhookPolicy.IsValidWebhookUrl(raw))
        {
            AlertStatusText.Text = "Must be an https:// non-local URL";
            return;
        }

        _engine.UpdateConfig(c => c.AlertWebhookUrl = raw);
        AlertStatusText.Text = string.IsNullOrEmpty(raw)
            ? "Discord, Telegram, or HTTPS webhook"
            : $"Saved ({WebhookPolicy.DetectProvider(raw)})";
    }

    private async void TestWebhook_Click(object sender, RoutedEventArgs e)
    {
        SaveWebhookUrl();
        TestWebhookBtn.IsEnabled = false;
        AlertStatusText.Text = "Sending test alert...";
        try
        {
            var (_, msg) = await _alertNotifier.SendTestAlertAsync(_engine.Config);
            AlertStatusText.Text = msg;
        }
        finally
        {
            TestWebhookBtn.IsEnabled = true;
        }
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

    private async Task StartUpdateInstallAsync()
    {
        if (_engine.GetStatus().IsActive
            && (_confirmUpdateUntilUtc is null || DateTimeOffset.UtcNow > _confirmUpdateUntilUtc.Value))
        {
            _confirmUpdateUntilUtc = DateTimeOffset.UtcNow.AddSeconds(5);
            ShowValidationBanner("Session active — installing update will stop it. Click Update again to confirm.");
            return;
        }

        _confirmUpdateUntilUtc = null;
        await _updateService.DownloadAndInstallAsync(_prepareForInstallerExit, _finalizeExit);
    }

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

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        if (_engine.GetStatus().IsActive
            && (_confirmExitUntilUtc is null || DateTimeOffset.UtcNow > _confirmExitUntilUtc.Value))
        {
            _confirmExitUntilUtc = DateTimeOffset.UtcNow.AddSeconds(5);
            ShowValidationBanner("Session active — click Power/Quit again within 5s to stop and exit.");
            return;
        }

        _exitApp();
    }

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
