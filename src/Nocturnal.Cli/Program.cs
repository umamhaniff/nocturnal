using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Nocturnal.Core;

namespace Nocturnal.Cli;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static int Main(string[] args)
    {
        if (args.Length == 0 || args.Any(a => a is "-h" or "--help" or "/?"))
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        var jsonOutput = args.Any(a => string.Equals(a, "--json", StringComparison.OrdinalIgnoreCase));
        var filteredArgs = args
            .Where(a => !string.Equals(a, "--json", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (!IpcCommandParser.TryParseArgs(filteredArgs, out var request, out var parseError) || request is null)
        {
            Console.Error.WriteLine($"Error: {parseError}");
            PrintUsage();
            return 1;
        }

        IpcResponse? response;
        try
        {
            using var pipe = new NamedPipeClientStream(".", IpcProtocol.PipeName, PipeDirection.InOut);
            pipe.Connect(timeout: 2000);

            var line = JsonSerializer.Serialize(request) + "\n";
            var bytes = Encoding.UTF8.GetBytes(line);
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();

            using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            var rawResponse = reader.ReadLine();
            response = string.IsNullOrWhiteSpace(rawResponse)
                ? null
                : JsonSerializer.Deserialize<IpcResponse>(rawResponse, JsonOptions);
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine("Nocturnal is not running in the System Tray. Start Nocturnal first.");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to communicate with Nocturnal: {ex.Message}");
            return 2;
        }

        if (response is null)
        {
            Console.Error.WriteLine("No response received from Nocturnal.");
            return 2;
        }

        if (jsonOutput)
        {
            Console.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
            return response.Ok ? 0 : 1;
        }

        if (!response.Ok)
        {
            Console.Error.WriteLine($"Error: {response.Message}");
            return 1;
        }

        if (request.Action == "history" && response.History is not null)
        {
            PrintHistory(response.History);
            return 0;
        }

        if (request.Action == "status" && response.Status is { } st)
        {
            PrintStatus(st);
            return 0;
        }

        Console.WriteLine(response.Message);
        return 0;
    }

    private static void PrintStatus(IpcStatusPayload st)
    {
        if (!st.IsActive)
        {
            Console.WriteLine($"State: Idle (Last stop: {st.LastStopReason})");
        }
        else
        {
            var rem = TimeSpan.FromSeconds(st.RemainingSeconds);
            var remStr = rem.TotalHours >= 1
                ? $"{(int)rem.TotalHours}h {rem.Minutes:D2}m {rem.Seconds:D2}s"
                : $"{rem.Minutes}m {rem.Seconds:D2}s";
            var watch = string.IsNullOrWhiteSpace(st.WatchProcess) ? "" : $", Watching={st.WatchProcess}";
            Console.WriteLine($"State: Awake ({remStr} remaining, Clamshell={st.ClamshellEnabled}, Focus={st.AgentFocusEnabled}{watch})");
        }

        if (st.AgentFocusEnabled && st.IsActive)
        {
            var mb = Math.Round(st.ReclaimedRamBytes / (1024.0 * 1024.0), 1);
            Console.WriteLine($"Focus: {st.ThrottledCount} throttled, {st.PausedCount} paused, ~{mb} MB RAM trimmed");
        }

        if (st.BatteryPercent.HasValue)
        {
            var source = st.IsOnAcPower == true ? "AC" : "Battery";
            Console.WriteLine($"Power: {source}, {st.BatteryPercent}%");
        }
    }

    private static void PrintHistory(IReadOnlyList<SessionHistoryRecord> records)
    {
        if (records.Count == 0)
        {
            Console.WriteLine("No recorded sessions yet.");
            return;
        }

        foreach (var rec in records)
        {
            var mb = Math.Round(rec.ReclaimedRamBytes / (1024.0 * 1024.0), 1);
            var dur = rec.ActualDuration.TotalHours >= 1
                ? $"{(int)rec.ActualDuration.TotalHours}h {rec.ActualDuration.Minutes}m"
                : $"{Math.Max(1, (int)Math.Round(rec.ActualDuration.TotalMinutes))}m";
            var watch = string.IsNullOrWhiteSpace(rec.WatchedProcessName) ? "" : $" | watch={rec.WatchedProcessName}";
            var batt = rec.StartBatteryPercent.HasValue && rec.EndBatteryPercent.HasValue
                ? $" | batt {rec.StartBatteryPercent}%->{rec.EndBatteryPercent}%"
                : "";
            Console.WriteLine($"{rec.EndedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm} | {dur} | {rec.StopReason} | ~{mb} MB{watch}{batt}");
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
        nocturnal-cli — Control Nocturnal from terminal or AI agent workflows

        Usage:
          nocturnal-cli start <duration> [--clamshell|--no-clamshell] [--focus|--no-focus]
          nocturnal-cli extend <duration>
          nocturnal-cli watch <process> [--pid <id>] [--max <duration>] [--clamshell|--no-clamshell] [--focus|--no-focus]
          nocturnal-cli stop
          nocturnal-cli status [--json]
          nocturnal-cli history [--json]
          nocturnal-cli screen-off

        Durations: 30s, 15m, 2h, 1d, 1h30m (or integer minutes)
        """);
    }
}
