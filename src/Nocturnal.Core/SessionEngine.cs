namespace Nocturnal.Core;

/// <summary>
/// Coordinates timed awake sessions, write-ahead crash recovery journaling,
/// clamshell (lid-closed) state, and battery/thermal safety rules.
/// </summary>
public sealed class SessionEngine
{
    private readonly IPowerController _power;
    private readonly ISystemMonitor _monitor;
    private readonly IJournalStore _journal;
    private readonly IConfigStore _configStore;
    private readonly IClock _clock;
    private readonly IAgentFocusController _focus;

    private JournalEntry? _activeJournal;
    private FocusReport _activeFocus = FocusReport.Empty;
    private StopReason _lastStopReason = StopReason.None;

    public AppConfig Config { get; private set; }
    public SystemPowerSnapshot? LatestSnapshot { get; private set; }

    public event Action<SessionStatus>? StatusChanged;

    public SessionEngine(
        IPowerController power,
        ISystemMonitor monitor,
        IJournalStore journal,
        IConfigStore configStore,
        IClock? clock = null,
        IAgentFocusController? focusController = null)
    {
        _power = power;
        _monitor = monitor;
        _journal = journal;
        _configStore = configStore;
        _clock = clock ?? new SystemClock();
        _focus = focusController ?? new NoOpAgentFocusController();
        Config = _configStore.Load();
    }

    public bool RecoverIfNeeded()
    {
        var stale = _journal.Load();
        if (stale is null)
        {
            return false;
        }

        if (stale.LidBackup is not null)
        {
            _power.RestoreClamshellMode(stale.LidBackup);
        }

        _power.DisableAwakeAssertion();

        if (stale.LowPowerEngagedByApp)
        {
            _power.TrySetPowerSaver(false);
        }

        _focus.Restore();
        _activeFocus = FocusReport.Empty;

        _journal.Clear();
        _lastStopReason = StopReason.CrashRecovery;
        NotifyChanged();
        return true;
    }

    public void Start(TimeSpan duration, bool? clamshellOverride = null)
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
            AutoSleepOnEnd: Config.AutoSleepOnCompletion);

        // True write-ahead journal before applying any OS power mutation.
        _journal.Save(entry);
        _activeJournal = entry;
        _lastStopReason = StopReason.None;

        if (clamshell && lidBackup is not null)
        {
            _power.ApplyClamshellMode(lidBackup.SchemeGuid);
        }

        _power.EnableAwakeAssertion(Config.KeepDisplayAwake);
        _activeFocus = Config.AgentFocusEnabled
            ? _focus.Engage(Config)
            : FocusReport.Empty;

        LatestSnapshot = _monitor.GetSnapshot();
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
            }
            else
            {
                _focus.Restore();
                _activeFocus = FocusReport.Empty;
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
        NotifyChanged();
    }

    public void Tick()
    {
        LatestSnapshot = _monitor.GetSnapshot();

        if (_activeJournal is null)
        {
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

        NotifyChanged();
    }

    public void Stop(StopReason reason = StopReason.Manual)
    {
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
                Focus: FocusReport.Empty);
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
            Focus: _activeFocus);
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

        if (_power.TrySetPowerSaver(true))
        {
            _activeJournal = _activeJournal with { LowPowerEngagedByApp = true };
            _journal.Save(_activeJournal);
        }
    }

    private void StopInternal(StopReason reason, bool allowSleep)
    {
        var current = _activeJournal;
        _activeJournal = null;
        _lastStopReason = reason;

        if (current?.LidBackup is not null)
        {
            _power.RestoreClamshellMode(current.LidBackup);
        }

        _power.DisableAwakeAssertion();

        if (current?.LowPowerEngagedByApp == true)
        {
            _power.TrySetPowerSaver(false);
        }

        _focus.Restore();
        _activeFocus = FocusReport.Empty;

        _journal.Clear();
        NotifyChanged();

        if (allowSleep)
        {
            _power.SleepSystem();
        }
    }

    private void NotifyChanged() => StatusChanged?.Invoke(GetStatus());
}
