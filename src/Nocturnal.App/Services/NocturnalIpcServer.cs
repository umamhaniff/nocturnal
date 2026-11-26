using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using Nocturnal.Core;

namespace Nocturnal.App.Services;

public sealed class NocturnalIpcServer : IDisposable
{
    private const int MaxRequestBytes = 4096;
    private static readonly TimeSpan ClientIoTimeout = TimeSpan.FromSeconds(3);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly SessionEngine _engine;
    private readonly Dispatcher _dispatcher;
    private readonly Action? _onConfigMutated;
    private readonly SemaphoreSlim _clientGate = new(4, 4);
    private readonly CancellationTokenSource _cts = new();
    private Task? _loopTask;

    public NocturnalIpcServer(SessionEngine engine, Dispatcher dispatcher, Action? onConfigMutated = null)
    {
        _engine = engine;
        _dispatcher = dispatcher;
        _onConfigMutated = onConfigMutated;
    }

    public void Start()
    {
        if (_loopTask is not null)
        {
            return;
        }
        _loopTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            var enteredGate = false;
            try
            {
                await _clientGate.WaitAsync(ct).ConfigureAwait(false);
                enteredGate = true;

                server = new NamedPipeServerStream(
                    IpcProtocol.PipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 4,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                var connectedPipe = server;
                server = null;
                enteredGate = false;

                _ = Task.Run(() => HandleClientAndDisposeAsync(connectedPipe, ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                if (server is not null)
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                }
                if (enteredGate)
                {
                    _clientGate.Release();
                }
                break;
            }
            catch
            {
                if (server is not null)
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                }
                if (enteredGate)
                {
                    _clientGate.Release();
                }

                // Transient pipe error; back off briefly so loop never spins hot.
                try
                {
                    await Task.Delay(150, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task HandleClientAndDisposeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            using var ioCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            ioCts.CancelAfter(ClientIoTimeout);
            await HandleClientAsync(pipe, ioCts.Token).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Ignore dispose errors on disconnected pipe.
            }

            try
            {
                _clientGate.Release();
            }
            catch (ObjectDisposedException)
            {
                // Server is shutting down.
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        IpcResponse response;
        try
        {
            var rawLine = await ReadBoundedLineAsync(pipe, ct).ConfigureAwait(false);
            var req = string.IsNullOrWhiteSpace(rawLine)
                ? null
                : JsonSerializer.Deserialize<IpcRequest>(rawLine, JsonOptions);

            response = req is null
                ? new IpcResponse(false, "Invalid or empty IPC request.")
                : _dispatcher.Invoke(() => ExecuteRequest(req));
        }
        catch (Exception ex)
        {
            response = new IpcResponse(false, $"Request failed: {ex.Message}");
        }

        try
        {
            var payload = JsonSerializer.Serialize(response, JsonOptions) + "\n";
            var bytes = Encoding.UTF8.GetBytes(payload);
            await pipe.WriteAsync(bytes, ct).ConfigureAwait(false);
            await pipe.FlushAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // Ignore broken pipe if client disconnected early.
        }
    }

    private static async Task<string?> ReadBoundedLineAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxRequestBytes];
        var offset = 0;
        var single = new byte[1];

        while (offset < MaxRequestBytes)
        {
            var read = await stream.ReadAsync(single.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (single[0] == (byte)'\n')
            {
                break;
            }

            if (single[0] != (byte)'\r')
            {
                buffer[offset++] = single[0];
            }
        }

        return offset == 0 ? null : Encoding.UTF8.GetString(buffer, 0, offset);
    }

    private IpcResponse ExecuteRequest(IpcRequest req)
    {
        var action = (req.Action ?? string.Empty).Trim().ToLowerInvariant();
        switch (action)
        {
            case "status":
                return new IpcResponse(true, "OK", BuildStatusPayload());

            case "history":
                return new IpcResponse(true, "OK", BuildStatusPayload(), _engine.GetRecentHistory());

            case "stop":
                _engine.Stop(StopReason.Manual);
                return new IpcResponse(true, "Session stopped and power restored.", BuildStatusPayload());

            case "extend":
            {
                if (req.DurationSeconds is not > 0 || req.DurationSeconds > IpcCommandParser.MaxDurationSeconds)
                {
                    return new IpcResponse(false, "Duration must be between 1s and 30d.");
                }

                _engine.AddTime(TimeSpan.FromSeconds(req.DurationSeconds.Value));
                var status = BuildStatusPayload();
                return new IpcResponse(true, $"Extended session. Remaining: {FormatDuration(TimeSpan.FromSeconds(status.RemainingSeconds))}.", status);
            }

            case "start":
            {
                if (req.DurationSeconds is not > 0 || req.DurationSeconds > IpcCommandParser.MaxDurationSeconds)
                {
                    return new IpcResponse(false, "Duration must be between 1s and 30d.");
                }

                var overrides = BuildOverrides(req);
                _engine.Start(TimeSpan.FromSeconds(req.DurationSeconds.Value), overrides);
                _onConfigMutated?.Invoke();
                var status = BuildStatusPayload();
                return new IpcResponse(true, $"Awake for {FormatDuration(TimeSpan.FromSeconds(status.RemainingSeconds))}.", status);
            }

            case "watch":
            {
                if (string.IsNullOrWhiteSpace(req.WatchProcess))
                {
                    return new IpcResponse(false, "Process name is required.");
                }

                if (req.DurationSeconds is > IpcCommandParser.MaxDurationSeconds)
                {
                    return new IpcResponse(false, "Max duration cannot exceed 30d.");
                }

                var isRunning = _engine.IsProcessCurrentlyRunning(req.WatchProcess, req.WatchPid);
                var overrides = BuildOverrides(req);
                TimeSpan? maxDur = req.DurationSeconds is > 0
                    ? TimeSpan.FromSeconds(req.DurationSeconds.Value)
                    : null;

                _engine.StartWatchingProcess(req.WatchProcess, req.WatchPid, maxDur, overrides);
                _onConfigMutated?.Invoke();
                var status = BuildStatusPayload();
                var msg = isRunning
                    ? $"Watching process '{status.WatchProcess}'."
                    : $"Watching process '{status.WatchProcess}' (not currently running — stops after 15s grace unless started).";
                return new IpcResponse(true, msg, status);
            }

            case "profile":
            {
                if (string.IsNullOrWhiteSpace(req.ProfileName))
                {
                    return new IpcResponse(false, "Profile name is required.");
                }

                var applied = _engine.ApplyProfile(req.ProfileName, startImmediately: true);
                if (applied is null)
                {
                    return new IpcResponse(false, $"Unknown profile '{req.ProfileName}'.");
                }

                _onConfigMutated?.Invoke();
                var status = BuildStatusPayload();
                return new IpcResponse(true, $"Profile '{applied.Name}' started ({applied.DurationMinutes}m).", status);
            }

            case "screen-off":
                _ = Task.Run(async () =>
                {
                    await Task.Delay(250);
                    _dispatcher.Invoke(() => _engine.TurnOffDisplay());
                });
                return new IpcResponse(true, "Turning display off...", BuildStatusPayload());

            default:
                return new IpcResponse(false, $"Unsupported IPC action '{req.Action}'.");
        }
    }

    private static SessionOverrides? BuildOverrides(IpcRequest req)
    {
        if (req.Clamshell is null && req.AgentFocus is null)
        {
            return null;
        }

        return new SessionOverrides(
            ClamshellEnabled: req.Clamshell,
            AgentFocusEnabled: req.AgentFocus);
    }

    private IpcStatusPayload BuildStatusPayload()
    {
        var st = _engine.GetStatus();
        var snap = _engine.LatestSnapshot;
        return new IpcStatusPayload(
            IsActive: st.IsActive,
            RemainingSeconds: Math.Round(st.Remaining.TotalSeconds, 1),
            ExpiresAtUtc: st.ExpiresAtUtc,
            ClamshellEnabled: st.ClamshellEnabled,
            AgentFocusEnabled: st.IsActive ? (st.Focus.IsEngaged || st.FocusArmedForLidClose || _engine.Config.AgentFocusEnabled) : _engine.Config.AgentFocusEnabled,
            ThrottledCount: st.Focus.ThrottledProcessCount,
            PausedCount: st.Focus.PausedProcessCount,
            ReclaimedRamBytes: st.Focus.ReclaimedRamBytes,
            WatchProcess: st.WatchTarget?.ProcessName,
            LastStopReason: st.LastStopReason.ToString(),
            BatteryPercent: snap?.HasBattery == true ? snap.BatteryPercent : null,
            IsOnAcPower: snap?.IsOnAcPower,
            IsLidClosed: st.IsLidClosed,
            FocusArmedForLidClose: st.FocusArmedForLidClose,
            ClamshellApplyFailed: st.ClamshellApplyFailed,
            PendingSleepSeconds: st.PendingSleepSeconds);
    }

    private static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
        {
            return $"{(int)ts.TotalHours}h {ts.Minutes:D2}m";
        }
        return $"{Math.Max(1, (int)Math.Ceiling(ts.TotalMinutes))}m";
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
