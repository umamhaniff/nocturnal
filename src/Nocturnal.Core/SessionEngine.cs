namespace Nocturnal.Core;

/// <summary>
/// Coordinates timed awake sessions, process-watch triggers, write-ahead crash recovery journaling,
/// clamshell state, Night Shift schedules, Wi-Fi failover, webhook alerts, session history, and safety rules.
/// </summary>
public sealed class SessionEngine
{
    private sealed record ProfileBaseline(
        bool ClamshellEnabled,
        bool AgentFocusEnabled,
        SelectedAppMode FocusSelectedAppMode,
        bool FocusCloseSelectedApps,
        bool KeepDisplayAwake,
        bool AutoSleepOnCompletion);

    private readonly IPowerController _power;
    private readonly ISystemMonitor _monitor;
    private readonly IJournalStore _journal;
    private readonly IConfigStore _configStore;
    private readonly IClock _clock;
    private readonly IAgentFocusController _focus;
    private readonly IProcessWatcher _watcher;
    private readonly ISessionHistoryStore _historyStore;
    private readonly IAlertNotifier _alerts;
    private readonly INetworkFailoverController _networkFailover;
    private readonly IRecoveryHook _recoveryHook;
    private readonly TimeSpan _watchGracePeriod;

    private JournalEntry? _activeJournal;
    private FocusReport _activeFocus = FocusReport.Empty;
    private StopReason _lastStopReason = StopReason.None;
    private int? _startBatteryPercent;
    private DateTimeOffset? _watchMissingSinceUtc;
    private string? _activeProfileName;
    private ProfileBaseline? _profileBaseline;
    private IReadOnlyList<SessionHistoryRecord>? _historyCache;
    private bool _wifiFailoverTriggered;
    private int _consecutiveOfflineTicks;
    private DateTimeOffset? _lastWifiFailoverAttemptUtc;
    private DateOnly? _lastNightShiftDate;

    public AppConfig Config { get; private set; }
    public SystemPowerSnapshot? LatestSnapshot { get; private set; }

    public event Action<SessionStatus>? StatusChanged;

    public SessionEngine(
        IPowerController power,
        ISystemMonitor monitor,
        IJournalStore journal,
        IConfigStore configStore,
        IClock? clock = null,
        IAgentFocusController? focusController = null,
        IProcessWatcher? processWatcher = null,
        ISessionHistoryStore? historyStore = null,
        TimeSpan? watchGracePeriod = null,
        IAlertNotifier? alertNotifier = null,
        INetworkFailoverController? networkFailover = null,
        IRecoveryHook? recoveryHook = null)
    {
        _power = power;
        _monitor = monitor;
        _journal = journal;
        _configStore = configStore;
        _clock = clock ?? new SystemClock();
        _focus = focusController ?? new NoOpAgentFocusController();
        _watcher = processWatcher ?? new NoOpProcessWatcher();
        _historyStore = historyStore ?? new NoOpSessionHistoryStore();
        _alerts = alertNotifier ?? new NoOpAlertNotifier();
        _networkFailover = networkFailover ?? new NoOpNetworkFailoverController();
        _recoveryHook = recoveryHook ?? new NoOpRecoveryHook();
        _watchGracePeriod = watchGracePeriod ?? TimeSpan.FromSeconds(15);
        Config = _configStore.Load();
    }

    public IReadOnlyList<SessionHistoryRecord> GetRecentHistory() =>
        _historyCache ??= _historyStore.Load();

    public IReadOnlyList<RunningAppInfo> ListRunningSessionApps() => _watcher.ListRunningSessionApps();

    public bool IsProcessCurrentlyRunning(string processName, int? pid = null)
    {
        var normalized = ProcessFocusPolicy.NormalizeProcessName(processName);
        if (string.IsNullOrEmpty(normalized))
        {
            return false;
        }

        return _watcher.IsProcessRunning(new ProcessWatchTarget(normalized, pid));
    }

    public bool RecoverIfNeeded()
    {
        var stale = _journal.Load();
        if (stale is null)
        {
            _recoveryHook.SetRecoveryArmed(false);
            return false;
        }

        if (stale.LidBackup is not null)
        {
            _power.RestoreClamshellMode(stale.LidBackup);
        }

        _power.DisableAwakeAssertion();

        if (stale.LowPowerEngagedByApp)
        {
            _power.TrySetPowerSaver(false, stale.OriginalEnergySaverThreshold);
        }

        if (stale.SuspendedProcesses is { Count: > 0 })
        {
            _focus.RecoverSuspended(stale.SuspendedProcesses);
        }

        _focus.Restore();
        _activeFocus = FocusReport.Empty;

        _journal.Clear();
        _recoveryHook.SetRecoveryArmed(false);
        _lastStopReason = StopReason.CrashRecovery;
        NotifyChanged();
        return true;
    }

    public void Start(TimeSpan duration, bool? clamshellOverride = null) =>
        StartInternal(duration, clamshellOverride, watchTarget: null, profileName: null);

    public SessionProfile? ApplyProfile(string profileName, bool startImmediately = false, int? overrideDurationMinutes = null)
    {
        var profile = SchedulePolicy.FindProfile(profileName);
        if (profile is null)
        {
            return null;
        }

        if (startImmediately)
        {
            var baseline = _profileBaseline ?? CaptureBaseline(Config);
            if (_activeJournal is not null)
            {
                StopInternal(StopReason.Manual, allowSleep: false);
            }

            _profileBaseline = baseline;
            ApplyProfileFields(Config, profile);

            var mins = overrideDurationMinutes is > 0
                ? overrideDurationMinutes.Value
                : profile.DurationMinutes;
            StartInternal(TimeSpan.FromMinutes(mins), profile.ClamshellEnabled, watchTarget: null, profileName: profile.Name);
            return profile;
        }

        _profileBaseline = null;
        ApplyProfileFields(Config, profile);
        _configStore.Save(Config);

        if (_activeJournal is not null)
        {
            _activeProfileName = profile.Name;
            _power.EnableAwakeAssertion(Config.KeepDisplayAwake);
            ReapplyAgentFocusIfActive();
        }
        else
        {
            NotifyChanged();
        }

        return profile;
    }

    private static ProfileBaseline CaptureBaseline(AppConfig cfg) =>
        new(
            cfg.ClamshellEnabled,
            cfg.AgentFocusEnabled,
            cfg.FocusSelectedAppMode,
            cfg.FocusCloseSelectedApps,
            cfg.KeepDisplayAwake,
            cfg.AutoSleepOnCompletion);

    private static void ApplyProfileFields(AppConfig cfg, SessionProfile profile)
    {
        cfg.ClamshellEnabled = profile.ClamshellEnabled;
        cfg.AgentFocusEnabled = profile.AgentFocusEnabled;
        cfg.FocusSelectedAppMode = profile.SelectedAppMode;
        cfg.FocusCloseSelectedApps = profile.SelectedAppMode == SelectedAppMode.GracefulClose;
        cfg.KeepDisplayAwake = profile.KeepDisplayAwake;
        cfg.AutoSleepOnCompletion = profile.AutoSleepOnCompletion;
    }

    public void StartWatchingProcess(
        string processName,
        int? pid = null,
        TimeSpan? maxDuration = null,
        bool? clamshellOverride = null)
    {
        var normalized = ProcessFocusPolicy.NormalizeProcessName(processName);
        if (string.IsNullOrEmpty(normalized))
        {
            throw new ArgumentException("Process name cannot be empty.", nameof(processName));
        }

        var ceiling = maxDuration ?? TimeSpan.FromHours(12);
        if (ceiling <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDuration), "Max duration must be positive.");
        }

        StartInternal(ceiling, clamshellOverride, new ProcessWatchTarget(normalized, pid), profileName: null);
    }

    public void TurnOffDisplay()
    {
        if (_activeJournal is not null && Config.KeepDisplayAwake)
        {
            _power.EnableAwakeAssertion(keepDisplayAwake: false);
        }

        _power.TurnOffDisplay();
    }

    private void StartInternal(
        TimeSpan duration,
        bool? clamshellOverride,
        ProcessWatchTarget? watchTarget,
        string? profileName)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be positive.");
        }

        if (_activeJournal is not null)
        {
            StopInternal(StopReason.Manual, allowSleep: false);
        }

        var now = _clock.UtcNow;
        var clamshell = clamshellOverride ?? Config.ClamshellEnabled;
        LidActionBackup? lidBackup = clamshell ? _power.ReadLidActionBackup() : null;

        var entry = new JournalEntry(
            SessionId: Guid.NewGuid(),
            StartedAtUtc: now,
            ExpiresAtUtc: now.Add(duration),
            ClamshellEnabled: clamshell,
            LidBackup: lidBackup,
            LowPowerEngagedByApp: false,
            AutoSleepOnEnd: Config.AutoSleepOnCompletion,
            WatchTarget: watchTarget);

        // True write-ahead journal before applying any OS power mutation.
        _journal.Save(entry);
        _activeJournal = entry;
        _lastStopReason = StopReason.None;
        _watchMissingSinceUtc = null;
        _activeProfileName = profileName;
        _wifiFailoverTriggered = false;
        _consecutiveOfflineTicks = 0;

        if (clamshell || Config.AgentFocusEnabled)
        {
            _recoveryHook.SetRecoveryArmed(true);
        }

        if (clamshell && lidBackup is not null)
        {
            _power.ApplyClamshellMode(lidBackup.SchemeGuid);
        }

        _power.EnableAwakeAssertion(Config.KeepDisplayAwake);
        _activeFocus = Config.AgentFocusEnabled
            ? _focus.Engage(Config)
            : FocusReport.Empty;

        if (Config.AgentFocusEnabled)
        {
            var suspended = _focus.GetSuspendedProcesses();
            if (suspended.Count > 0)
            {
                _activeJournal = _activeJournal with { SuspendedProcesses = suspended };
                _journal.Save(_activeJournal);
            }
        }

        LatestSnapshot = _monitor.GetSnapshot();
        _startBatteryPercent = LatestSnapshot.HasBattery ? LatestSnapshot.BatteryPercent : null;
        NotifyChanged();
    }

    public void AddTime(TimeSpan delta)
    {
        if (delta <= TimeSpan.Zero)
        {
            return;
        }

        if (_activeJournal is null)
        {
            Start(delta);
            return;
        }

        _activeJournal = _activeJournal with
        {
            ExpiresAtUtc = _activeJournal.ExpiresAtUtc.Add(delta)
        };
        _journal.Save(_activeJournal);
        NotifyChanged();
    }

    public void SetClamshell(bool enabled)
    {
        Config.ClamshellEnabled = enabled;
        _configStore.Save(Config);

        if (_activeJournal is null || _activeJournal.ClamshellEnabled == enabled)
        {
            NotifyChanged();
            return;
        }

        LidActionBackup? oldBackup = _activeJournal.LidBackup;
        LidActionBackup? newBackup = enabled ? _power.ReadLidActionBackup() : null;

        _activeJournal = _activeJournal with
        {
            ClamshellEnabled = enabled,
            LidBackup = newBackup
        };
        _journal.Save(_activeJournal);

        if (enabled && newBackup is not null)
        {
            _power.ApplyClamshellMode(newBackup.SchemeGuid);
        }
        else if (!enabled && oldBackup is not null)
        {
            _power.RestoreClamshellMode(oldBackup);
        }

        NotifyChanged();
    }

    public void UpdateConfig(Action<AppConfig> mutate)
    {
        mutate(Config);
        _configStore.Save(Config);
        if (_activeJournal is not null)
        {
            _activeJournal = _activeJournal with
            {
                AutoSleepOnEnd = Config.AutoSleepOnCompletion
            };
            _journal.Save(_activeJournal);
            _power.EnableAwakeAssertion(Config.KeepDisplayAwake);
        }
        NotifyChanged();
    }

    public void SetAgentFocus(bool enabled)
    {
        Config.AgentFocusEnabled = enabled;
        _configStore.Save(Config);

        if (_activeJournal is not null)
        {
            if (enabled)
            {
                _activeFocus = _focus.Engage(Config);
                _activeJournal = _activeJournal with
                {
                    SuspendedProcesses = _focus.GetSuspendedProcesses()
                };
                _journal.Save(_activeJournal);
            }
            else
            {
                _focus.Restore();
                _activeFocus = FocusReport.Empty;
                _activeJournal = _activeJournal with { SuspendedProcesses = null };
                _journal.Save(_activeJournal);
            }
        }

        NotifyChanged();
    }

    public void ReapplyAgentFocusIfActive()
    {
        if (_activeJournal is null || !Config.AgentFocusEnabled)
        {
            return;
        }

        _focus.Restore();
        _activeFocus = _focus.Engage(Config);
        _activeJournal = _activeJournal with
        {
            SuspendedProcesses = _focus.GetSuspendedProcesses()
        };
        _journal.Save(_activeJournal);
        NotifyChanged();
    }

    public void Tick()
    {
        LatestSnapshot = _monitor.GetSnapshot();

        if (_activeJournal is null)
        {
            var localNow = _clock.UtcNow.ToLocalTime();
            if (SchedulePolicy.ShouldTriggerNightShift(
                Config,
                localNow,
                isSessionActive: false,
                isOnAcPower: LatestSnapshot.IsOnAcPower,
                _lastNightShiftDate,
                out var triggerDate))
            {
                _lastNightShiftDate = triggerDate;
                ApplyProfile(
                    Config.NightShiftProfileName,
                    startImmediately: true,
                    overrideDurationMinutes: Config.NightShiftDurationMinutes);
                return;
            }

            NotifyChanged();
            return;
        }

        if (_clock.UtcNow >= _activeJournal.ExpiresAtUtc)
        {
            StopInternal(StopReason.TimerExpired, allowSleep: _activeJournal.AutoSleepOnEnd);
            return;
        }

        if (EvaluateSafetyRules(LatestSnapshot))
        {
            return;
        }

        if (_activeJournal.WatchTarget is { } watchTarget)
        {
            if (_watcher.IsProcessRunning(watchTarget))
            {
                _watchMissingSinceUtc = null;
            }
            else
            {
                _watchMissingSinceUtc ??= _clock.UtcNow;
                if (_clock.UtcNow - _watchMissingSinceUtc.Value >= _watchGracePeriod)
                {
                    StopInternal(StopReason.WatchedProcessExited, allowSleep: _activeJournal.AutoSleepOnEnd);
                    return;
                }
            }
        }

        if (Config.WifiFailoverEnabled && NetworkFailoverPolicy.IsValidSsid(Config.BackupWifiSsid))
        {
            var isOnline = _networkFailover.HasInternetConnectivity();
            if (NetworkFailoverPolicy.ShouldAttemptFailover(
                isOnline,
                ref _consecutiveOfflineTicks,
                _clock.UtcNow,
                _lastWifiFailoverAttemptUtc))
            {
                _lastWifiFailoverAttemptUtc = _clock.UtcNow;
                _wifiFailoverTriggered = _networkFailover.TryConnectToProfile(Config.BackupWifiSsid.Trim());
            }
        }

        NotifyChanged();
    }

    public void Stop(StopReason reason = StopReason.Manual)
    {
        if (reason == StopReason.AppExiting && _activeJournal is null)
        {
            return;
        }

        StopInternal(reason, allowSleep: false);
    }

    public SessionStatus GetStatus()
    {
        if (_activeJournal is null)
        {
            return new SessionStatus(
                IsActive: false,
                StartedAtUtc: null,
                ExpiresAtUtc: null,
                TotalDuration: TimeSpan.Zero,
                Remaining: TimeSpan.Zero,
                Progress: 0,
                ClamshellEnabled: Config.ClamshellEnabled,
                LowPowerEngaged: false,
                LastStopReason: _lastStopReason,
                Focus: FocusReport.Empty,
                WatchTarget: null,
                ActiveProfileName: null,
                WifiFailoverTriggered: false);
        }

        var now = _clock.UtcNow;
        var total = _activeJournal.ExpiresAtUtc - _activeJournal.StartedAtUtc;
        var remaining = _activeJournal.ExpiresAtUtc - now;
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        var progress = total.TotalSeconds > 0
            ? Math.Clamp(1.0 - (remaining.TotalSeconds / total.TotalSeconds), 0.0, 1.0)
            : 1.0;

        return new SessionStatus(
            IsActive: true,
            StartedAtUtc: _activeJournal.StartedAtUtc,
            ExpiresAtUtc: _activeJournal.ExpiresAtUtc,
            TotalDuration: total,
            Remaining: remaining,
            Progress: progress,
            ClamshellEnabled: _activeJournal.ClamshellEnabled,
            LowPowerEngaged: _activeJournal.LowPowerEngagedByApp,
            LastStopReason: _lastStopReason,
            Focus: _activeFocus,
            WatchTarget: _activeJournal.WatchTarget,
            ActiveProfileName: _activeProfileName,
            WifiFailoverTriggered: _wifiFailoverTriggered);
    }

    private bool EvaluateSafetyRules(SystemPowerSnapshot snapshot)
    {
        if (_activeJournal is null)
        {
            return false;
        }

        // Battery rules (only apply when running on battery)
        if (snapshot.HasBattery && !snapshot.IsOnAcPower)
        {
            if (Config.CriticalBatteryPercent > 0 && snapshot.BatteryPercent <= Config.CriticalBatteryPercent)
            {
                StopInternal(StopReason.BatteryCritical, allowSleep: true);
                return true;
            }

            if (Config.LowPowerBatteryThresholdPercent > 0
                && snapshot.BatteryPercent <= Config.LowPowerBatteryThresholdPercent
                && !_activeJournal.LowPowerEngagedByApp)
            {
                EngageLowPowerMode();
            }
        }

        // Thermal rules
        if (Config.EnableThermalProtection)
        {
            var isCriticalTemp = snapshot.ThermalLevel == ThermalLevel.Critical
                || (snapshot.TemperatureCelsius.HasValue && snapshot.TemperatureCelsius.Value >= Config.CriticalTempCelsius);

            if (isCriticalTemp)
            {
                StopInternal(StopReason.ThermalCritical, allowSleep: true);
                return true;
            }

            var isWarmTemp = snapshot.ThermalLevel is ThermalLevel.Warm or ThermalLevel.Hot
                || (snapshot.TemperatureCelsius.HasValue && snapshot.TemperatureCelsius.Value >= Config.WarmTempCelsius);

            if (isWarmTemp && !_activeJournal.LowPowerEngagedByApp)
            {
                EngageLowPowerMode();
            }
        }

        return false;
    }

    private void EngageLowPowerMode()
    {
        if (_activeJournal is null)
        {
            return;
        }

        var originalThreshold = _power.ReadEnergySaverThreshold();
        if (_power.TrySetPowerSaver(true))
        {
            _activeJournal = _activeJournal with
            {
                LowPowerEngagedByApp = true,
                OriginalEnergySaverThreshold = originalThreshold
            };
            _journal.Save(_activeJournal);
            TrySendAlert(
                "LowPowerEngaged",
                "Low Power Engaged",
                "Battery or thermal guard engaged Windows Power Saver.",
                isGuardAlert: true,
                LatestSnapshot,
                _activeFocus.ReclaimedRamBytes);
        }
    }

    private void StopInternal(StopReason reason, bool allowSleep)
    {
        var current = _activeJournal;
        var focusSnapshot = _activeFocus;
        var startBattery = _startBatteryPercent;

        _activeJournal = null;
        _startBatteryPercent = null;
        _watchMissingSinceUtc = null;
        _activeProfileName = null;
        _wifiFailoverTriggered = false;
        _consecutiveOfflineTicks = 0;
        _lastStopReason = reason;

        if (current is not null && reason != StopReason.CrashRecovery)
        {
            var endedAt = _clock.UtcNow;
            var actual = endedAt > current.StartedAtUtc
                ? endedAt - current.StartedAtUtc
                : TimeSpan.Zero;
            var endBattery = LatestSnapshot?.HasBattery == true
                ? LatestSnapshot.BatteryPercent
                : (int?)null;

            var record = new SessionHistoryRecord(
                StartedAtUtc: current.StartedAtUtc,
                EndedAtUtc: endedAt,
                ActualDuration: actual,
                StopReason: reason,
                ClamshellEnabled: current.ClamshellEnabled,
                StartBatteryPercent: startBattery,
                EndBatteryPercent: endBattery,
                ReclaimedRamBytes: focusSnapshot.ReclaimedRamBytes,
                WatchedProcessName: current.WatchTarget?.ProcessName);

            _historyStore.Append(record);
            if (_historyCache is not null)
            {
                var updated = new List<SessionHistoryRecord>(10) { record };
                foreach (var item in _historyCache)
                {
                    if (updated.Count >= 10)
                    {
                        break;
                    }
                    updated.Add(item);
                }
                _historyCache = updated;
            }

            if (reason is StopReason.BatteryCritical or StopReason.ThermalCritical)
            {
                TrySendAlert(
                    reason.ToString(),
                    "Safety Guard Stop",
                    $"Session stopped early ({reason}); system entering sleep.",
                    isGuardAlert: true,
                    LatestSnapshot,
                    focusSnapshot.ReclaimedRamBytes,
                    waitForDelivery: allowSleep);
            }
            else if (reason is StopReason.TimerExpired or StopReason.WatchedProcessExited)
            {
                var summary = reason == StopReason.WatchedProcessExited
                    ? $"Watched process '{current.WatchTarget?.ProcessName}' exited."
                    : "Scheduled awake timer completed.";
                TrySendAlert(
                    reason.ToString(),
                    "Session Completed",
                    summary,
                    isGuardAlert: false,
                    LatestSnapshot,
                    focusSnapshot.ReclaimedRamBytes,
                    waitForDelivery: allowSleep);
            }
        }

        if (current?.LidBackup is not null)
        {
            _power.RestoreClamshellMode(current.LidBackup);
        }

        _power.DisableAwakeAssertion();

        if (current?.LowPowerEngagedByApp == true)
        {
            _power.TrySetPowerSaver(false, current.OriginalEnergySaverThreshold);
        }

        _focus.Restore();
        _activeFocus = FocusReport.Empty;

        if (_profileBaseline is { } baseline)
        {
            Config.ClamshellEnabled = baseline.ClamshellEnabled;
            Config.AgentFocusEnabled = baseline.AgentFocusEnabled;
            Config.FocusSelectedAppMode = baseline.FocusSelectedAppMode;
            Config.FocusCloseSelectedApps = baseline.FocusCloseSelectedApps;
            Config.KeepDisplayAwake = baseline.KeepDisplayAwake;
            Config.AutoSleepOnCompletion = baseline.AutoSleepOnCompletion;
            _profileBaseline = null;
        }

        _journal.Clear();
        _recoveryHook.SetRecoveryArmed(false);
        NotifyChanged();

        if (allowSleep)
        {
            _power.SleepSystem();
        }
    }

    private void TrySendAlert(
        string eventType,
        string title,
        string message,
        bool isGuardAlert,
        SystemPowerSnapshot? snap,
        long reclaimedBytes,
        bool waitForDelivery = false)
    {
        if (!Config.AlertsEnabled
            || !WebhookPolicy.IsValidWebhookUrl(Config.AlertWebhookUrl))
        {
            return;
        }

        if (isGuardAlert && !Config.AlertOnSafetyGuard)
        {
            return;
        }

        if (!isGuardAlert && !Config.AlertOnCompletion)
        {
            return;
        }

        var mb = Math.Round(reclaimedBytes / (1024.0 * 1024.0), 1);
        var batt = snap?.HasBattery == true ? snap.BatteryPercent : (int?)null;
        _alerts.Notify(
            Config,
            new WebhookAlertEvent(eventType, title, message, _clock.UtcNow, batt, mb),
            waitForDelivery);
    }

    private void NotifyChanged() => StatusChanged?.Invoke(GetStatus());
}
