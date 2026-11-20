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
    GracefulClose
}

public sealed record FocusReport(
    bool IsEngaged,
    int ThrottledProcessCount,
    int ClosedAppCount,
    IReadOnlyList<string> ClosedAppNames)
{
    public static FocusReport Empty { get; } = new(false, 0, 0, []);
}

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
    bool AutoSleepOnEnd);

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
    public bool FocusCloseSelectedApps { get; set; } = false;
    public List<string> FocusAppsToClose { get; set; } = ["Discord", "Spotify", "Steam"];
    public List<string> CustomAgentWhitelist { get; set; } = [];
}

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
    FocusReport Focus);
