namespace Nocturnal.Core;

public static class IpcProtocol
{
    public const string PipeName = "Nocturnal_Ipc_v1";
}

public sealed record IpcRequest(
    string Action,
    int? DurationSeconds = null,
    bool? Clamshell = null,
    bool? AgentFocus = null,
    string? WatchProcess = null,
    int? WatchPid = null,
    string? ProfileName = null);

public sealed record IpcStatusPayload(
    bool IsActive,
    double RemainingSeconds,
    DateTimeOffset? ExpiresAtUtc,
    bool ClamshellEnabled,
    bool AgentFocusEnabled,
    int ThrottledCount,
    int PausedCount,
    long ReclaimedRamBytes,
    string? WatchProcess,
    string LastStopReason,
    int? BatteryPercent,
    bool? IsOnAcPower,
    bool IsLidClosed = false,
    bool FocusArmedForLidClose = false,
    bool ClamshellApplyFailed = false,
    int? PendingSleepSeconds = null);

public sealed record IpcResponse(
    bool Ok,
    string Message,
    IpcStatusPayload? Status = null,
    IReadOnlyList<SessionHistoryRecord>? History = null);

public static class IpcCommandParser
{
    public const int MaxDurationSeconds = 30 * 86400;
    private static readonly TimeSpan MaxAllowedDuration = TimeSpan.FromDays(30);

    public static bool TryParseDuration(string? token, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var trimmed = token.Trim();
        if (int.TryParse(trimmed, out var bareMinutes))
        {
            if (bareMinutes <= 0)
            {
                return false;
            }

            duration = TimeSpan.FromMinutes(bareMinutes);
            return duration <= MaxAllowedDuration;
        }

        var totalSeconds = 0.0;
        var i = 0;
        var matchedUnit = false;

        while (i < trimmed.Length)
        {
            if (!char.IsDigit(trimmed[i]))
            {
                return false;
            }

            var numStart = i;
            while (i < trimmed.Length && char.IsDigit(trimmed[i]))
            {
                i++;
            }

            if (!int.TryParse(trimmed[numStart..i], out var value) || value < 0 || i >= trimmed.Length)
            {
                return false;
            }

            var unit = char.ToLowerInvariant(trimmed[i]);
            i++;
            matchedUnit = true;

            double segmentSeconds;
            switch (unit)
            {
                case 'd':
                    segmentSeconds = value * 86400.0;
                    break;
                case 'h':
                    segmentSeconds = value * 3600.0;
                    break;
                case 'm':
                    segmentSeconds = value * 60.0;
                    break;
                case 's':
                    segmentSeconds = value;
                    break;
                default:
                    return false;
            }

            totalSeconds += segmentSeconds;
        }

        if (!matchedUnit || totalSeconds <= 0)
        {
            return false;
        }

        duration = TimeSpan.FromSeconds(totalSeconds);
        return duration <= MaxAllowedDuration;
    }

    public static bool TryParseArgs(string[] args, out IpcRequest? request, out string? error)
    {
        request = null;
        error = null;

        if (args.Length == 0)
        {
            error = "Missing command.";
            return false;
        }

        var cmd = args[0].Trim().ToLowerInvariant();
        switch (cmd)
        {
            case "stop":
                request = new IpcRequest("stop");
                return true;

            case "status":
                request = new IpcRequest("status");
                return true;

            case "history":
                request = new IpcRequest("history");
                return true;

            case "screen-off":
                request = new IpcRequest("screen-off");
                return true;

            case "extend":
                if (args.Length < 2 || !TryParseDuration(args[1], out var extendDur))
                {
                    error = "Invalid duration for 'extend'. Example: nocturnal-cli extend 30m";
                    return false;
                }

                request = new IpcRequest("extend", DurationSeconds: (int)Math.Ceiling(extendDur.TotalSeconds));
                return true;

            case "start":
                if (args.Length < 2 || !TryParseDuration(args[1], out var startDur))
                {
                    error = "Invalid duration for 'start'. Example: nocturnal-cli start 2h --focus";
                    return false;
                }

                if (!TryParseSessionFlags(args.Skip(2), allowMax: false, out var clamshell, out var focus, out _, out error))
                {
                    return false;
                }

                request = new IpcRequest(
                    "start",
                    DurationSeconds: (int)Math.Ceiling(startDur.TotalSeconds),
                    Clamshell: clamshell,
                    AgentFocus: focus);
                return true;

            case "watch":
                if (args.Length < 2)
                {
                    error = "Missing process name for 'watch'. Example: nocturnal-cli watch python --max 6h";
                    return false;
                }

                var procName = ProcessFocusPolicy.NormalizeProcessName(args[1]);
                if (string.IsNullOrEmpty(procName))
                {
                    error = "Invalid process name for 'watch'.";
                    return false;
                }

                if (!TryParseWatchFlags(args.Skip(2), out var watchPid, out var maxDur, out var watchClamshell, out var watchFocus, out error))
                {
                    return false;
                }

                request = new IpcRequest(
                    "watch",
                    DurationSeconds: maxDur.HasValue ? (int)Math.Ceiling(maxDur.Value.TotalSeconds) : null,
                    Clamshell: watchClamshell,
                    AgentFocus: watchFocus,
                    WatchProcess: procName,
                    WatchPid: watchPid);
                return true;

            case "profile":
                if (args.Length < 2)
                {
                    error = "Missing profile name. Options: AI Overnight, Build / Scraper, Presentation";
                    return false;
                }

                var profileName = string.Join(' ', args.Skip(1)).Trim();
                if (SchedulePolicy.FindProfile(profileName) is null)
                {
                    error = $"Unknown profile '{profileName}'. Options: AI Overnight, Build / Scraper, Presentation";
                    return false;
                }

                request = new IpcRequest("profile", ProfileName: profileName);
                return true;

            default:
                error = $"Unknown command '{args[0]}'.";
                return false;
        }
    }

    private static bool TryParseSessionFlags(
        IEnumerable<string> flags,
        bool allowMax,
        out bool? clamshell,
        out bool? focus,
        out TimeSpan? maxDuration,
        out string? error)
    {
        clamshell = null;
        focus = null;
        maxDuration = null;
        error = null;

        using var it = flags.GetEnumerator();
        while (it.MoveNext())
        {
            var flag = it.Current.Trim().ToLowerInvariant();
            switch (flag)
            {
                case "--clamshell":
                    clamshell = true;
                    break;
                case "--no-clamshell":
                    clamshell = false;
                    break;
                case "--focus":
                    focus = true;
                    break;
                case "--no-focus":
                    focus = false;
                    break;
                case "--max" when allowMax:
                    if (!it.MoveNext() || !TryParseDuration(it.Current, out var parsedMax))
                    {
                        error = "Invalid value after '--max'. Example: --max 6h";
                        return false;
                    }
                    maxDuration = parsedMax;
                    break;
                default:
                    error = $"Unknown option '{it.Current}'.";
                    return false;
            }
        }

        return true;
    }

    private static bool TryParseWatchFlags(
        IEnumerable<string> args,
        out int? pid,
        out TimeSpan? maxDuration,
        out bool? clamshell,
        out bool? focus,
        out string? error)
    {
        pid = null;
        maxDuration = null;
        clamshell = null;
        focus = null;
        error = null;

        var list = args.ToList();
        var filtered = new List<string>(list.Count);
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], "--pid", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= list.Count || !int.TryParse(list[i + 1], out var parsedPid) || parsedPid <= 0)
                {
                    error = "Invalid process ID after '--pid'.";
                    return false;
                }
                pid = parsedPid;
                i++;
                continue;
            }

            filtered.Add(list[i]);
        }

        return TryParseSessionFlags(filtered, allowMax: true, out clamshell, out focus, out maxDuration, out error);
    }
}
