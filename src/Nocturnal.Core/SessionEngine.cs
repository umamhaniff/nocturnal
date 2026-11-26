using System.Globalization;

namespace Nocturnal.Core;

/// <summary>
/// Coordinates timed awake sessions, process-watch triggers, write-ahead crash recovery journaling,
/// lid-aware Agent Focus mode, Night Shift schedules, Wi-Fi failover, smart auto-sleep, and safety rules.
/// </summary>
public sealed class SessionEngine
{
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
    private readonly IUserPresenceMonitor _presence;
    private readonly IAppLogger _logger;
    private readonly TimeSpan _watchGracePeriod;

    private JournalEntry? _activeJournal;
    private FocusReport _activeFocus = FocusReport.Empty;
    private StopReason _lastStopReason = StopReason.None;
    private int? _startBatteryPercent;
    private DateTimeOffset? _watchMissingSinceUtc;
    private string? _activeProfileName;
    private SessionOverrides? _sessionOverrides;
    private IReadOnlyList<SessionHistoryRecord>? _historyCache;
    private bool _wifiFailoverTriggered;
    private int _consecutiveOfflineTicks;
    private DateTimeOffset? _lastWifiFailoverAttemptUtc;
    private DateOnly? _lastNightShiftDate;
    private bool _isLidClosed;
    private bool _clamshellApplyFailed;
    private bool _focusArmedForLidClose;
    private LidActionBackup? _pendingLidRestoreRetry;
    private DateTimeOffset? _pendingSleepAtUtc;

    public AppConfig Config { get; private set; }
    public SystemPowerSnapshot? LatestSnapshot { get; private set; }
    public bool IsLidClosed => _isLidClosed;

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
        IRecoveryHook? recoveryHook = null,
        IUserPresenceMonitor? presenceMonitor = null,
        IAppLogger? logger = null)
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
        _presence = presenceMonitor ?? new NoOpUserPresenceMonitor();
        _logger = logger ?? new NoOpAppLogger();
        _watchGracePeriod = watchGracePeriod ?? TimeSpan.FromSeconds(15);

        Config = _configStore.Load();
        Config.Normalize();

        if (!string.IsNullOrWhiteSpace(Config.LastNightShiftDate)
            && DateOnly.TryParseExact(
                Config.LastNightShiftDate,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var savedDate))
        {
            _lastNightShiftDate = savedDate;
        }
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
            if (_journal.WasLastLoadCorrupt)
            {
                _logger.Error("Corrupt journal detected during recovery; resetting OS lid action to default Sleep.");
                TryRestore(() => _power.RestoreDefaultLidAction(), "RestoreDefaultLidAction");
                TryRestore(() => _power.DisableAwakeAssertion(), "DisableAwakeAssertion");
                TryRestore(() => _recoveryHook.SetRecoveryArmed(false), "ClearRecoveryHook");
                _lastStopReason = StopReason.CrashRecovery;
                NotifyChanged();
                return true;
            }

            TryRestore(() => _recoveryHook.SetRecoveryArmed(false), "ClearRecoveryHook");
            return false;
        }

        var clamshellRestored = true;
        if (stale.LidBackup is not null)
        {
            clamshellRestored = TryRestore(
                () => _power.RestoreClamshellMode(stale.LidBackup),
                "RecoverClamshellMode");
            if (!clamshellRestored)
            {
                _pendingLidRestoreRetry = stale.LidBackup;
            }
        }

        TryRestore(() => _power.DisableAwakeAssertion(), "RecoverDisableAwake");

        if (stale.LowPowerEngagedByApp)
        {
            TryRestore(
                () => _power.TrySetPowerSaver(false, stale.OriginalEnergySaverThreshold),
                "RecoverPowerSaver");
        }

        if (stale.SuspendedProcesses is { Count: > 0 })
        {
            TryRestore(() => _focus.RecoverSuspended(stale.SuspendedProcesses), "RecoverSuspendedProcesses");
        }

        TryRestore(() => _focus.Restore(), "RecoverFocusRestore");
        _activeFocus = FocusReport.Empty;

        if (clamshellRestored)
        {
            TryRestore(() => _journal.Clear(), "RecoverClearJournal");
        }

        TryRestore(() => _recoveryHook.SetRecoveryArmed(false), "ClearRecoveryHook");
        _lastStopReason = StopReason.CrashRecovery;
        NotifyChanged();
        return true;
    }

    public void Start(TimeSpan duration, bool? clamshellOverride = null) =>
        Start(duration, clamshellOverride.HasValue ? new SessionOverrides(ClamshellEnabled: clamshellOverride) : null);

    public void Start(TimeSpan duration, SessionOverrides? overrides) =>
        StartInternal(duration, overrides, watchTarget: null, profileName: null);

    public SessionProfile? ApplyProfile(string profileName, bool startImmediately = false, int? overrideDurationMinutes = null)
    {
        var profile = SchedulePolicy.FindProfile(profileName);
        if (profile is null)
        {
            return null;
        }

        var profileOverrides = new SessionOverrides(
            ClamshellEnabled: profile.ClamshellEnabled,
            AgentFocusEnabled: profile.AgentFocusEnabled,
            SelectedAppMode: profile.SelectedAppMode,
            KeepDisplayAwake: profile.KeepDisplayAwake,
            AutoSleepOnCompletion: profile.AutoSleepOnCompletion);

        if (startImmediately)
        {
            var mins = overrideDurationMinutes is > 0
                ? overrideDurationMinutes.Value
                : profile.DurationMinutes;
            StartInternal(TimeSpan.FromMinutes(mins), profileOverrides, watchTarget: null, profileName: profile.Name);
            return profile;
        }

        Config.ClamshellEnabled = profile.ClamshellEnabled;
        Config.AgentFocusEnabled = profile.AgentFocusEnabled;
        Config.FocusSelectedAppMode = profile.SelectedAppMode;
        Config.FocusCloseSelectedApps = profile.SelectedAppMode == SelectedAppMode.GracefulClose;
        Config.KeepDisplayAwake = profile.KeepDisplayAwake;
        Config.AutoSleepOnCompletion = profile.AutoSleepOnCompletion;
        _configStore.Save(Config);

        if (_activeJournal is not null)
        {
            _activeProfileName = profile.Name;
            _sessionOverrides = null;
            _power.EnableAwakeAssertion(GetEffectiveKeepDisplayAwake());
            ReapplyAgentFocusIfActive();
        }
        else
        {
            NotifyChanged();
        }

        return profile;
    }

    public void StartWatchingProcess(
        string processName,
        int? pid = null,
        TimeSpan? maxDuration = null,
        bool? clamshellOverride = null) =>
        StartWatchingProcess(
            processName,
            pid,
            maxDuration,
            clamshellOverride.HasValue ? new SessionOverrides(ClamshellEnabled: clamshellOverride) : null);

    public void StartWatchingProcess(
        string processName,
        int? pid,
        TimeSpan? maxDuration,
        SessionOverrides? overrides)
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

        StartInternal(ceiling, overrides, new ProcessWatchTarget(normalized, pid), profileName: null);
    }

    public void TurnOffDisplay()
    {
        if (_activeJournal is not null && GetEffectiveKeepDisplayAwake())
        {
            _power.EnableAwakeAssertion(keepDisplayAwake: false);
        }

        _power.TurnOffDisplay();
    }

    public void OnLidStateChanged(bool isLidClosed)
    {
        if (_isLidClosed == isLidClosed)
        {
            return;
        }

        _isLidClosed = isLidClosed;
        _logger.Info($"Lid state changed: {(isLidClosed ? "Closed" : "Opened")}.");

        if (_activeJournal is not null && GetEffectiveAgentFocus() && Config.FocusPauseOnlyWhenLidClosed)
        {
            _activeFocus = EngageFocusWithWriteAhead();
        }

        NotifyChanged();
    }

    public void CancelPendingSleep()
    {
        if (_pendingSleepAtUtc is null)
        {
            return;
        }

        _pendingSleepAtUtc = null;
        _logger.Info("Pending auto-sleep cancelled by user.");
        NotifyChanged();
    }

    public void CheckPendingSleep()
    {
        if (_pendingSleepAtUtc is not { } sleepAt)
        {
            return;
        }

        if (_clock.UtcNow >= sleepAt)
        {
            _pendingSleepAtUtc = null;
            NotifyChanged();
            TryRestore(() => _power.SleepSystem(), "DeferredSleepSystem");
            return;
        }

        NotifyChanged();
    }

    private void StartInternal(
        TimeSpan duration,
        SessionOverrides? overrides,
        ProcessWatchTarget? watchTarget,
        string? profileName)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be positive.");
        }

        _pendingSleepAtUtc = null;

        if (_activeJournal is not null)
        {
            StopInternal(StopReason.Manual, allowSleep: false);
        }

        _sessionOverrides = overrides;
        _clamshellApplyFailed = false;

        var now = _clock.UtcNow;
        var wantsClamshell = GetEffectiveClamshell();
        LidActionBackup? lidBackup = wantsClamshell ? _power.ReadLidActionBackup() : null;

        var entry = new JournalEntry(
            SessionId: Guid.NewGuid(),
            StartedAtUtc: now,
            ExpiresAtUtc: now.Add(duration),
            ClamshellEnabled: wantsClamshell && lidBackup is not null,
            LidBackup: lidBackup,
            LowPowerEngagedByApp: false,
            AutoSleepOnEnd: GetEffectiveAutoSleep(),
            WatchTarget: watchTarget);

        // True write-ahead journal before applying any OS power mutation.
        _journal.Save(entry);
        _activeJournal = entry;
        _lastStopReason = StopReason.None;
        _watchMissingSinceUtc = null;
        _activeProfileName = profileName;
        _wifiFailoverTriggered = false;
        _consecutiveOfflineTicks = 0;

        if (wantsClamshell)
        {
            var applied = lidBackup is not null && _power.ApplyClamshellMode(lidBackup.SchemeGuid);
            if (!applied)
            {
                _clamshellApplyFailed = true;
                _logger.Warn("Failed to apply/verify Clamshell lid-close mode; reporting Clamshell OFF.");
                _activeJournal = _activeJournal with { ClamshellEnabled = false, LidBackup = null };
                _journal.Save(_activeJournal);
            }
        }

        if (_activeJournal.ClamshellEnabled || GetEffectiveAgentFocus())
        {
            _recoveryHook.SetRecoveryArmed(true);
        }

        _power.EnableAwakeAssertion(GetEffectiveKeepDisplayAwake());

        if (GetEffectiveAgentFocus())
        {
            _activeFocus = EngageFocusWithWriteAhead();
        }
        else
        {
            _activeFocus = FocusReport.Empty;
            _focusArmedForLidClose = false;
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
        if (_sessionOverrides is not null)
        {
            _sessionOverrides = _sessionOverrides with { ClamshellEnabled = null };
        }

        if (_activeJournal is null || _activeJournal.ClamshellEnabled == enabled)
        {
            _clamshellApplyFailed = false;
            NotifyChanged();
            return;
        }

        LidActionBackup? oldBackup = _activeJournal.LidBackup;
        LidActionBackup? newBackup = enabled ? _power.ReadLidActionBackup() : null;

        _activeJournal = _activeJournal with
        {
            ClamshellEnabled = enabled && newBackup is not null,
            LidBackup = newBackup
        };
        _journal.Save(_activeJournal);

        if (enabled)
        {
            var applied = newBackup is not null && _power.ApplyClamshellMode(newBackup.SchemeGuid);
            if (!applied)
            {
                _clamshellApplyFailed = true;
                _logger.Warn("SetClamshell failed to apply/verify OS lid-close action.");
                _activeJournal = _activeJournal with { ClamshellEnabled = false, LidBackup = null };
                _journal.Save(_activeJournal);
            }
            else
            {
                _clamshellApplyFailed = false;
                _recoveryHook.SetRecoveryArmed(true);
            }
        }
        else
        {
            _clamshellApplyFailed = false;
            if (oldBackup is not null)
            {
                _power.RestoreClamshellMode(oldBackup);
            }
        }

        NotifyChanged();
    }

    public void UpdateConfig(Action<AppConfig> mutate)
    {
        mutate(Config);
        Config.Normalize();
        _configStore.Save(Config);
        if (_activeJournal is not null)
        {
            _activeJournal = _activeJournal with
            {
                AutoSleepOnEnd = GetEffectiveAutoSleep()
            };
            _journal.Save(_activeJournal);
            _power.EnableAwakeAssertion(GetEffectiveKeepDisplayAwake());
        }
        NotifyChanged();
    }

    public void SetAgentFocus(bool enabled)
    {
        Config.AgentFocusEnabled = enabled;
        _configStore.Save(Config);
        if (_sessionOverrides is not null)
        {
            _sessionOverrides = _sessionOverrides with { AgentFocusEnabled = null };
        }

        if (_activeJournal is not null)
        {
            if (enabled)
            {
                _activeFocus = EngageFocusWithWriteAhead();
                _recoveryHook.SetRecoveryArmed(true);
            }
            else
            {
                _focus.Restore();
                _activeFocus = FocusReport.Empty;
                _focusArmedForLidClose = false;
                _activeJournal = _activeJournal with { SuspendedProcesses = null };
                _journal.Save(_activeJournal);
            }
        }

        NotifyChanged();
    }

    public void ReapplyAgentFocusIfActive()
    {
        if (_activeJournal is null || !GetEffectiveAgentFocus())
        {
            return;
        }

        _activeFocus = EngageFocusWithWriteAhead();
        NotifyChanged();
    }

    public void Tick()
    {
        if (_pendingLidRestoreRetry is { } retryBackup && _activeJournal is null)
        {
            if (TryRestore(() => _power.RestoreClamshellMode(retryBackup), "RetryRestoreClamshellMode"))
            {
                _pendingLidRestoreRetry = null;
                TryRestore(() => _journal.Clear(), "RetryClearJournal");
            }
        }

        CheckPendingSleep();

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
                Config.LastNightShiftDate = triggerDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                _configStore.Save(Config);
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
        if (reason == StopReason.AppExiting && _activeJournal is null && _pendingSleepAtUtc is null)
        {
            return;
        }

        if (reason == StopReason.Manual && _activeJournal is null && _pendingSleepAtUtc is not null)
        {
            CancelPendingSleep();
            return;
        }

        StopInternal(reason, allowSleep: false);
    }

    public SessionStatus GetStatus()
    {
        int? pendingSleepSecs = null;
        if (_pendingSleepAtUtc is { } sleepAt)
        {
            var left = (int)Math.Ceiling(Math.Max(0, (sleepAt - _clock.UtcNow).TotalSeconds));
            pendingSleepSecs = left;
        }

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
                WifiFailoverTriggered: false,
                IsLidClosed: _isLidClosed,
                ClamshellApplyFailed: _clamshellApplyFailed,
                FocusArmedForLidClose: false,
                PendingSleepSeconds: pendingSleepSecs);
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
            WifiFailoverTriggered: _wifiFailoverTriggered,
            IsLidClosed: _isLidClosed,
            ClamshellApplyFailed: _clamshellApplyFailed,
            FocusArmedForLidClose: _focusArmedForLidClose,
            PendingSleepSeconds: pendingSleepSecs);
    }

    private FocusReport EngageFocusWithWriteAhead()
    {
        TryRestore(() => _focus.Restore(), "PreEngageFocusRestore");
        if (_activeJournal is not null && _activeJournal.SuspendedProcesses is { Count: > 0 })
        {
            _activeJournal = _activeJournal with { SuspendedProcesses = null };
            _journal.Save(_activeJournal);
        }

        var effectiveCfg = BuildEffectiveFocusConfig(_isLidClosed);
        var report = _focus.Engage(effectiveCfg, record =>
        {
            if (_activeJournal is null)
            {
                return;
            }

            var updated = _activeJournal.SuspendedProcesses?.ToList() ?? [];
            updated.Add(record);
            _activeJournal = _activeJournal with { SuspendedProcesses = updated };
            _journal.Save(_activeJournal);
        });

        return report;
    }

    private AppConfig BuildEffectiveFocusConfig(bool isLidClosed)
    {
        var baseMode = _sessionOverrides?.SelectedAppMode
            ?? (Config.FocusSelectedAppMode != SelectedAppMode.Off
                ? Config.FocusSelectedAppMode
                : (Config.FocusCloseSelectedApps ? SelectedAppMode.GracefulClose : SelectedAppMode.Off));

        var effectiveMode = baseMode;
        if (Config.FocusPauseOnlyWhenLidClosed && !isLidClosed && baseMode != SelectedAppMode.Off)
        {
            effectiveMode = SelectedAppMode.Off;
            _focusArmedForLidClose = true;
        }
        else
        {
            _focusArmedForLidClose = false;
        }

        return new AppConfig
        {
            FocusEcoQosEnabled = Config.FocusEcoQosEnabled,
            FocusRamTrimEnabled = Config.FocusRamTrimEnabled,
            FocusSelectedAppMode = effectiveMode,
            FocusCloseSelectedApps = effectiveMode == SelectedAppMode.GracefulClose,
            FocusPauseOnlyWhenLidClosed = Config.FocusPauseOnlyWhenLidClosed,
            FocusAppsToClose = Config.FocusAppsToClose.ToList(),
            CustomAppsToManage = Config.CustomAppsToManage.ToList(),
            CustomAgentWhitelist = Config.CustomAgentWhitelist.ToList()
        };
    }

    private bool GetEffectiveClamshell() => _sessionOverrides?.ClamshellEnabled ?? Config.ClamshellEnabled;
    private bool GetEffectiveAgentFocus() => _sessionOverrides?.AgentFocusEnabled ?? Config.AgentFocusEnabled;
    private bool GetEffectiveKeepDisplayAwake() => _sessionOverrides?.KeepDisplayAwake ?? Config.KeepDisplayAwake;
    private bool GetEffectiveAutoSleep() => _sessionOverrides?.AutoSleepOnCompletion ?? Config.AutoSleepOnCompletion;

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

        // Thermal rules strictly honor configured thresholds when numeric temperature is available.
        if (Config.EnableThermalProtection)
        {
            var isCriticalTemp = snapshot.TemperatureCelsius.HasValue
                ? snapshot.TemperatureCelsius.Value >= Config.CriticalTempCelsius
                : snapshot.ThermalLevel == ThermalLevel.Critical;

            if (isCriticalTemp)
            {
                StopInternal(StopReason.ThermalCritical, allowSleep: true);
                return true;
            }

            var isWarmTemp = snapshot.TemperatureCelsius.HasValue
                ? snapshot.TemperatureCelsius.Value >= Config.WarmTempCelsius
                : snapshot.ThermalLevel is ThermalLevel.Warm or ThermalLevel.Hot;

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
        _sessionOverrides = null;
        _wifiFailoverTriggered = false;
        _consecutiveOfflineTicks = 0;
        _focusArmedForLidClose = false;
        _clamshellApplyFailed = false;
        _lastStopReason = reason;

        // 1. Fault-tolerant power & focus restoration ALWAYS executes first.
        var clamshellRestored = true;
        if (current?.LidBackup is not null)
        {
            clamshellRestored = TryRestore(
                () => _power.RestoreClamshellMode(current.LidBackup),
                "RestoreClamshellMode");
            if (!clamshellRestored)
            {
                _pendingLidRestoreRetry = current.LidBackup;
            }
        }

        TryRestore(() => _power.DisableAwakeAssertion(), "DisableAwakeAssertion");

        if (current?.LowPowerEngagedByApp == true)
        {
            TryRestore(
                () => _power.TrySetPowerSaver(false, current.OriginalEnergySaverThreshold),
                "RestorePowerSaver");
        }

        TryRestore(() => _focus.Restore(), "RestoreAgentFocus");
        _activeFocus = FocusReport.Empty;

        if (clamshellRestored)
        {
            TryRestore(() => _journal.Clear(), "ClearJournal");
        }

        TryRestore(() => _recoveryHook.SetRecoveryArmed(false), "DisarmRecoveryHook");

        // 2. Smart auto-sleep decision: sleep immediately if lid closed, emergency guard, or user idle >= 2m.
        var sleepNow = false;
        if (allowSleep)
        {
            sleepNow = reason is StopReason.BatteryCritical or StopReason.ThermalCritical
                || _isLidClosed
                || _presence.GetIdleDuration() >= TimeSpan.FromMinutes(2);

            if (!sleepNow)
            {
                _pendingSleepAtUtc = _clock.UtcNow.AddSeconds(60);
                _logger.Info($"Session ended ({reason}) while user active; deferring auto-sleep by 60s.");
            }
        }
        else
        {
            _pendingSleepAtUtc = null;
        }

        // 3. History + Webhook alerts AFTER hardware restoration completes.
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
                    waitForDelivery: sleepNow);
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
                    waitForDelivery: sleepNow);
            }
        }

        NotifyChanged();

        if (sleepNow)
        {
            TryRestore(() => _power.SleepSystem(), "SleepSystem");
        }
    }

    private bool TryRestore(Action action, string stepName)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"Restore step '{stepName}' threw an exception.", ex);
            return false;
        }
    }

    private bool TryRestore(Func<bool> action, string stepName)
    {
        try
        {
            var ok = action();
            if (!ok)
            {
                _logger.Warn($"Restore step '{stepName}' returned false.");
            }
            return ok;
        }
        catch (Exception ex)
        {
            _logger.Error($"Restore step '{stepName}' threw an exception.", ex);
            return false;
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
