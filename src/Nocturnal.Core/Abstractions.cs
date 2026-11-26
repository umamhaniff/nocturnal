namespace Nocturnal.Core;

public interface IPowerController
{
    void EnableAwakeAssertion(bool keepDisplayAwake);
    void DisableAwakeAssertion();
    LidActionBackup? ReadLidActionBackup();
    bool ApplyClamshellMode(Guid schemeGuid);
    bool RestoreClamshellMode(LidActionBackup backup);
    bool RestoreDefaultLidAction();
    int? ReadEnergySaverThreshold();
    bool TrySetPowerSaver(bool enabled, int? restoreThreshold = null);
    void TurnOffDisplay();
    void SleepSystem();
}

public interface ISystemMonitor
{
    SystemPowerSnapshot GetSnapshot();
}

public interface IJournalStore
{
    bool WasLastLoadCorrupt { get; }
    JournalEntry? Load();
    void Save(JournalEntry entry);
    void Clear();
}

public interface IConfigStore
{
    AppConfig Load();
    void Save(AppConfig config);
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public interface IAgentFocusController
{
    FocusReport Engage(AppConfig config, Action<SuspendedProcessRecord>? onAboutToSuspend = null);
    IReadOnlyList<SuspendedProcessRecord> GetSuspendedProcesses();
    void Restore();
    void RecoverSuspended(IReadOnlyList<SuspendedProcessRecord> records);
}

public interface IProcessWatcher
{
    bool IsProcessRunning(ProcessWatchTarget target);
    IReadOnlyList<RunningAppInfo> ListRunningSessionApps();
}

public interface ISessionHistoryStore
{
    IReadOnlyList<SessionHistoryRecord> Load();
    void Append(SessionHistoryRecord record, int maxEntries = 10);
}

public interface IAlertNotifier
{
    void Notify(AppConfig config, WebhookAlertEvent alert, bool waitForDelivery = false);
}

public interface IRecoveryHook
{
    void SetRecoveryArmed(bool armed);
}

public interface IUserPresenceMonitor
{
    TimeSpan GetIdleDuration();
}

public interface IAppLogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? ex = null);
}

public interface INetworkFailoverController
{
    bool HasInternetConnectivity();
    bool TryConnectToProfile(string ssid);
}

public sealed class NoOpAgentFocusController : IAgentFocusController
{
    public FocusReport Engage(AppConfig config, Action<SuspendedProcessRecord>? onAboutToSuspend = null) => FocusReport.Empty;
    public IReadOnlyList<SuspendedProcessRecord> GetSuspendedProcesses() => [];
    public void Restore() { }
    public void RecoverSuspended(IReadOnlyList<SuspendedProcessRecord> records) { }
}

public sealed class NoOpProcessWatcher : IProcessWatcher
{
    public bool IsProcessRunning(ProcessWatchTarget target) => true;
    public IReadOnlyList<RunningAppInfo> ListRunningSessionApps() => [];
}

public sealed class NoOpSessionHistoryStore : ISessionHistoryStore
{
    public IReadOnlyList<SessionHistoryRecord> Load() => [];
    public void Append(SessionHistoryRecord record, int maxEntries = 10) { }
}

public sealed class NoOpAlertNotifier : IAlertNotifier
{
    public void Notify(AppConfig config, WebhookAlertEvent alert, bool waitForDelivery = false) { }
}

public sealed class NoOpRecoveryHook : IRecoveryHook
{
    public void SetRecoveryArmed(bool armed) { }
}

public sealed class NoOpUserPresenceMonitor : IUserPresenceMonitor
{
    public TimeSpan GetIdleDuration() => TimeSpan.FromHours(1);
}

public sealed class NoOpAppLogger : IAppLogger
{
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message, Exception? ex = null) { }
}

public sealed class NoOpNetworkFailoverController : INetworkFailoverController
{
    public bool HasInternetConnectivity() => true;
    public bool TryConnectToProfile(string ssid) => false;
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
