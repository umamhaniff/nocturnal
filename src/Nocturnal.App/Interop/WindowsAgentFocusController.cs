using System.Diagnostics;
using System.Runtime.InteropServices;
using Nocturnal.Core;

namespace Nocturnal.App.Interop;

public sealed class WindowsAgentFocusController : IAgentFocusController
{
    private const uint ProcessSetInformation = 0x0200;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ProcessPowerThrottlingClass = 4;
    private const uint PowerThrottlingProcessExecutionSpeed = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(
        IntPtr hProcess,
        int processInformationClass,
        ref ProcessPowerThrottlingState processInformation,
        int processInformationSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private readonly Dictionary<int, ProcessPriorityClass> _throttled = [];

    public FocusReport Engage(AppConfig config)
    {
        Restore();

        var currentPid = Environment.ProcessId;
        int currentSessionId;
        using (var self = Process.GetCurrentProcess())
        {
            currentSessionId = self.SessionId;
        }

        var closedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var closedNames = new List<string>();

        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                if (proc.Id == currentPid)
                {
                    continue;
                }

                var action = ProcessFocusPolicy.Evaluate(
                    proc.ProcessName,
                    proc.SessionId,
                    currentSessionId,
                    proc.MainWindowHandle != IntPtr.Zero,
                    config);

                switch (action)
                {
                    case ProcessAction.GracefulClose:
                        if (proc.CloseMainWindow() && closedSet.Add(proc.ProcessName))
                        {
                            closedNames.Add(proc.ProcessName);
                        }
                        break;

                    case ProcessAction.ThrottleEcoQos:
                        var origPriority = proc.PriorityClass;
                        var ecoApplied = TrySetEcoQos(proc.Id, enable: true);
                        var priorityLowered = false;
                        if (origPriority == ProcessPriorityClass.Normal)
                        {
                            proc.PriorityClass = ProcessPriorityClass.BelowNormal;
                            priorityLowered = true;
                        }
                        if (ecoApplied || priorityLowered)
                        {
                            _throttled[proc.Id] = origPriority;
                        }
                        break;
                }
            }
            catch
            {
                // Protected or exiting processes cannot be modified; skip safely.
            }
            finally
            {
                proc.Dispose();
            }
        }

        return new FocusReport(true, _throttled.Count, closedNames.Count, closedNames);
    }

    public void Restore()
    {
        foreach (var (pid, origPriority) in _throttled)
        {
            TrySetEcoQos(pid, enable: false);
            try
            {
                using var proc = Process.GetProcessById(pid);
                proc.PriorityClass = origPriority;
            }
            catch
            {
                // Process may have exited during the session.
            }
        }

        _throttled.Clear();
    }

    private static bool TrySetEcoQos(int pid, bool enable)
    {
        var handle = OpenProcess(ProcessSetInformation | ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var state = new ProcessPowerThrottlingState
            {
                Version = 1,
                ControlMask = PowerThrottlingProcessExecutionSpeed,
                StateMask = enable ? PowerThrottlingProcessExecutionSpeed : 0
            };

            return SetProcessInformation(
                handle,
                ProcessPowerThrottlingClass,
                ref state,
                Marshal.SizeOf<ProcessPowerThrottlingState>());
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}
