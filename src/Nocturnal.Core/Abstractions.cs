namespace Nocturnal.Core;

public interface IPowerController
{
    void EnableAwakeAssertion(bool keepDisplayAwake);
    void DisableAwakeAssertion();
    LidActionBackup? ReadLidActionBackup();
    void ApplyClamshellMode(Guid schemeGuid);
    void RestoreClamshellMode(LidActionBackup backup);
    bool TrySetPowerSaver(bool enabled);
    void TurnOffDisplay();
    void SleepSystem();
}

public interface ISystemMonitor
{
    SystemPowerSnapshot GetSnapshot();
}

public interface IJournalStore
{
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
    FocusReport Engage(AppConfig config);
    IReadOnlyList<SuspendedProcessRecord> GetSuspendedProcesses();
    void Restore();
    void RecoverSuspended(IReadOnlyList<SuspendedProcessRecord> records);
}

public interface IProcessWatcher
{
    bool IsProcessRunning(ProcessWatchTarget target);
}

public interface ISessionHistoryStore
{
    IReadOnlyList<SessionHistoryRecord> Load();
    void Append(SessionHistoryRecord record, int maxEntries = 10);
}

public sealed class NoOpAgentFocusController : IAgentFocusController
{
    public FocusReport Engage(AppConfig config) => FocusReport.Empty;
    public IReadOnlyList<SuspendedProcessRecord> GetSuspendedProcesses() => [];
    public void Restore() { }
    public void RecoverSuspended(IReadOnlyList<SuspendedProcessRecord> records) { }
}

public sealed class NoOpProcessWatcher : IProcessWatcher
{
    public bool IsProcessRunning(ProcessWatchTarget target) => true;
}

public sealed class NoOpSessionHistoryStore : ISessionHistoryStore
{
    public IReadOnlyList<SessionHistoryRecord> Load() => [];
    public void Append(SessionHistoryRecord record, int maxEntries = 10) { }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
