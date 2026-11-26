namespace Nocturnal.Core;

public enum TrayUiStyle
{
    Pills,
    Compact,
    Minimal,
    Popover
}

public enum StopReason
{
    None,
    Manual,
    TimerExpired,
    BatteryCritical,
    ThermalCritical,
    WatchedProcessExited,
    CrashRecovery,
    AppExiting
}

public enum ThermalLevel
{
    Unknown,
    Normal,
    Warm,
    Hot,
    Critical
}

public enum ProcessAction
{
    Protect,
    ThrottleEcoQos,
    DeepPause,
    GracefulClose
}

public enum SelectedAppMode
{
    Off,
    DeepPause,
    GracefulClose
}

public sealed record SuspendedProcessRecord(
    int Pid,
    DateTimeOffset StartTimeUtc);

public sealed record ProcessNode(
    int Pid,
    int ParentPid,
    string Name,
    int SessionId,
    string? ImagePath);

public sealed record ProcessWatchTarget(
    string ProcessName,
    int? Pid = null);

public sealed record RunningAppInfo(
    string ProcessName,
    int InstanceCount,
    long TotalWorkingSetBytes,
    bool IsProtectedByDefault);

public sealed record WebhookAlertEvent(
    string EventType,
    string Title,
    string Message,
    DateTimeOffset TimestampUtc,
    int? BatteryPercent,
    double ReclaimedRamMb);

public sealed record SessionProfile(
    string Name,
    int DurationMinutes,
    bool ClamshellEnabled,
    bool AgentFocusEnabled,
    SelectedAppMode SelectedAppMode,
    bool KeepDisplayAwake,
    bool AutoSleepOnCompletion);

public sealed record FocusReport(
    bool IsEngaged,
    int ThrottledProcessCount,
    int PausedProcessCount,
    int ClosedAppCount,
    IReadOnlyList<string> AffectedAppNames,
    long ReclaimedRamBytes = 0)
{
    public static FocusReport Empty { get; } = new(false, 0, 0, 0, [], 0);
}

public sealed record SessionHistoryRecord(
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc,
    TimeSpan ActualDuration,
    StopReason StopReason,
    bool ClamshellEnabled,
    int? StartBatteryPercent,
    int? EndBatteryPercent,
    long ReclaimedRamBytes,
    string? WatchedProcessName);

public sealed record SystemPowerSnapshot(
    bool IsOnAcPower,
    int BatteryPercent,
    bool HasBattery,
    ThermalLevel ThermalLevel,
    double? TemperatureCelsius);

public sealed record LidActionBackup(
    Guid SchemeGuid,
    int OriginalAcLidAction,
    int OriginalDcLidAction);

public sealed record JournalEntry(
    Guid SessionId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    bool ClamshellEnabled,
    LidActionBackup? LidBackup,
    bool LowPowerEngagedByApp,
    bool AutoSleepOnEnd,
    IReadOnlyList<SuspendedProcessRecord>? SuspendedProcesses = null,
    ProcessWatchTarget? WatchTarget = null);

public sealed class AppConfig
{
    public TrayUiStyle UiStyle { get; set; } = TrayUiStyle.Pills;
    public int DefaultAddMinutes { get; set; } = 30;
    public bool ClamshellEnabled { get; set; } = true;
    public bool AutoSleepOnCompletion { get; set; } = true;
    public bool KeepDisplayAwake { get; set; } = false;
    public int LowPowerBatteryThresholdPercent { get; set; } = 40;
    public int CriticalBatteryPercent { get; set; } = 10;
    public bool EnableThermalProtection { get; set; } = true;
    public double WarmTempCelsius { get; set; } = 80.0;
    public double CriticalTempCelsius { get; set; } = 95.0;
    public bool LaunchAtStartup { get; set; } = false;
    public bool AgentFocusEnabled { get; set; } = false;
    public bool FocusEcoQosEnabled { get; set; } = true;
    public bool FocusRamTrimEnabled { get; set; } = true;
    public SelectedAppMode FocusSelectedAppMode { get; set; } = SelectedAppMode.Off;
    public bool FocusCloseSelectedApps { get; set; } = false;
    public List<string> FocusAppsToClose { get; set; } = ["Discord", "Spotify", "Steam"];
    public List<string> CustomAppsToManage { get; set; } = [];
    public List<string> CustomAgentWhitelist { get; set; } = [];
    public bool AutoCheckForUpdates { get; set; } = true;
    public bool AlertsEnabled { get; set; } = false;
    public string AlertWebhookUrl { get; set; } = string.Empty;
    public bool AlertOnCompletion { get; set; } = true;
    public bool AlertOnSafetyGuard { get; set; } = true;
    public bool WifiFailoverEnabled { get; set; } = false;
    public string BackupWifiSsid { get; set; } = string.Empty;
    public bool NightShiftEnabled { get; set; } = false;
    public string NightShiftStartTime { get; set; } = "22:00";
    public int NightShiftDurationMinutes { get; set; } = 240;
    public bool NightShiftRequireAcPower { get; set; } = true;
    public string NightShiftProfileName { get; set; } = "AI Overnight";
}

public sealed record UpdateReleaseInfo(
    Version Version,
    string TagName,
    string ReleaseUrl,
    string? InstallerDownloadUrl,
    string? InstallerFileName,
    long? InstallerSizeBytes,
    string? ExpectedSha256Hex,
    string? ReleaseNotes);

public sealed record SessionStatus(
    bool IsActive,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    TimeSpan TotalDuration,
    TimeSpan Remaining,
    double Progress,
    bool ClamshellEnabled,
    bool LowPowerEngaged,
    StopReason LastStopReason,
    FocusReport Focus,
    ProcessWatchTarget? WatchTarget = null,
    string? ActiveProfileName = null,
    bool WifiFailoverTriggered = false);
