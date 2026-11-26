using System.Diagnostics;
using System.Runtime.InteropServices;
using Nocturnal.Core;

namespace Nocturnal.App.Interop;

public sealed class WindowsAgentFocusController : IAgentFocusController
{
    private const uint ProcessSetInformation = 0x0200;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessSetQuota = 0x0100;
    private const uint ProcessSuspendResume = 0x0800;
    private const int ProcessPowerThrottlingClass = 4;
    private const uint PowerThrottlingProcessExecutionSpeed = 0x1;
    private const uint Th32csSnapProcess = 0x00000002;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(
        IntPtr hProcess,
        int dwFlags,
        [Out] char[] lpExeName,
        ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool K32EmptyWorkingSet(IntPtr hProcess);

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(IntPtr processHandle);

    private readonly Dictionary<int, (ProcessPriorityClass Priority, DateTimeOffset StartTimeUtc)> _throttled = [];
    private readonly List<SuspendedProcessRecord> _suspended = [];

    public FocusReport Engage(AppConfig config, Action<SuspendedProcessRecord>? onAboutToSuspend = null)
    {
        Restore();

        var currentPid = Environment.ProcessId;
        int currentSessionId;
        using (var self = Process.GetCurrentProcess())
        {
            currentSessionId = self.SessionId;
        }

        var parentMap = CaptureParentPidMap();
        var processes = Process.GetProcesses();
        var nodes = new List<ProcessNode>(processes.Length);
        var byPid = new Dictionary<int, Process>(processes.Length);

        try
        {
            foreach (var proc in processes)
            {
                try
                {
                    var pid = proc.Id;
                    byPid[pid] = proc;
                    parentMap.TryGetValue(pid, out var parentPid);
                    var imagePath = TryQueryImagePath(pid);
                    nodes.Add(new ProcessNode(pid, parentPid, proc.ProcessName, proc.SessionId, imagePath));
                }
                catch
                {
                    // Skip transient or protected processes.
                }
            }

            var actions = ProcessFocusPolicy.EvaluateTree(nodes, currentSessionId, currentPid, config);
            var affectedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var affectedNames = new List<string>();
            var closedCount = 0;
            long reclaimedRamBytes = 0;

            foreach (var node in nodes)
            {
                if (!actions.TryGetValue(node.Pid, out var action)
                    || action == ProcessAction.Protect
                    || !byPid.TryGetValue(node.Pid, out var proc))
                {
                    continue;
                }

                try
                {
                    switch (action)
                    {
                        case ProcessAction.DeepPause:
                            var startUtc = proc.StartTime.ToUniversalTime();
                            var suspRecord = new SuspendedProcessRecord(proc.Id, startUtc);
                            onAboutToSuspend?.Invoke(suspRecord);
                            if (TrySuspendProcess(proc.Id))
                            {
                                _suspended.Add(suspRecord);
                                if (config.FocusRamTrimEnabled)
                                {
                                    reclaimedRamBytes += TryTrimAndMeasureReclaimed(proc);
                                }
                                if (affectedSet.Add(node.Name))
                                {
                                    affectedNames.Add(node.Name);
                                }
                            }
                            break;

                        case ProcessAction.GracefulClose:
                            if (proc.CloseMainWindow())
                            {
                                closedCount++;
                                if (affectedSet.Add(node.Name))
                                {
                                    affectedNames.Add(node.Name);
                                }
                            }
                            break;

                        case ProcessAction.ThrottleEcoQos:
                            var origPriority = proc.PriorityClass;
                            var procStartUtc = proc.StartTime.ToUniversalTime();
                            var ecoApplied = TrySetEcoQos(proc.Id, enable: true);
                            var priorityLowered = false;
                            if (origPriority is ProcessPriorityClass.Normal or ProcessPriorityClass.AboveNormal)
                            {
                                proc.PriorityClass = ProcessPriorityClass.BelowNormal;
                                priorityLowered = true;
                            }
                            if (config.FocusRamTrimEnabled)
                            {
                                reclaimedRamBytes += TryTrimAndMeasureReclaimed(proc);
                            }
                            if (ecoApplied || priorityLowered)
                            {
                                _throttled[proc.Id] = (origPriority, procStartUtc);
                            }
                            break;
                    }
                }
                catch
                {
                    // Protected or exiting processes cannot be modified; skip safely.
                }
            }

            return new FocusReport(true, _throttled.Count, _suspended.Count, closedCount, affectedNames, reclaimedRamBytes);
        }
        finally
        {
            foreach (var proc in processes)
            {
                proc.Dispose();
            }
        }
    }

    public IReadOnlyList<SuspendedProcessRecord> GetSuspendedProcesses() =>
        _suspended.Count == 0 ? [] : _suspended.ToList();

    public void Restore()
    {
        if (_suspended.Count > 0)
        {
            RecoverSuspended(_suspended);
            _suspended.Clear();
        }

        foreach (var (pid, info) in _throttled)
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                var startUtc = proc.StartTime.ToUniversalTime();
                if (Math.Abs((startUtc - info.StartTimeUtc).TotalSeconds) <= 2.0)
                {
                    TrySetEcoQos(pid, enable: false);
                    proc.PriorityClass = info.Priority;
                }
            }
            catch
            {
                // Process may have exited or PID was reused.
            }
        }

        _throttled.Clear();
    }

    public void RecoverSuspended(IReadOnlyList<SuspendedProcessRecord> records)
    {
        foreach (var record in records)
        {
            try
            {
                using var proc = Process.GetProcessById(record.Pid);
                var startUtc = proc.StartTime.ToUniversalTime();
                if (Math.Abs((startUtc - record.StartTimeUtc).TotalSeconds) <= 2.0)
                {
                    TryResumeProcess(record.Pid);
                }
            }
            catch
            {
                // Process already exited or PID no longer matches.
            }
        }
    }

    private static Dictionary<int, int> CaptureParentPidMap()
    {
        var map = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == InvalidHandleValue)
        {
            return map;
        }

        try
        {
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32FirstW(snapshot, ref entry))
            {
                return map;
            }

            do
            {
                map[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
            }
            while (Process32NextW(snapshot, ref entry));

            return map;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    private static string? TryQueryImagePath(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new char[512];
            var size = buffer.Length;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) && size > 0
                ? new string(buffer, 0, size)
                : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static long TryTrimAndMeasureReclaimed(Process proc)
    {
        try
        {
            var before = proc.WorkingSet64;
            if (!TryEmptyWorkingSet(proc.Id))
            {
                return 0;
            }

            proc.Refresh();
            var after = proc.WorkingSet64;
            return before > after ? before - after : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static bool TryEmptyWorkingSet(int pid)
    {
        var handle = OpenProcess(ProcessSetQuota | ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return K32EmptyWorkingSet(handle);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static bool TrySuspendProcess(int pid)
    {
        var handle = OpenProcess(ProcessSuspendResume, false, pid);
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return NtSuspendProcess(handle) == 0;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static bool TryResumeProcess(int pid)
    {
        var handle = OpenProcess(ProcessSuspendResume, false, pid);
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return NtResumeProcess(handle) == 0;
        }
        finally
        {
            CloseHandle(handle);
        }
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
                ControlMask = enable ? PowerThrottlingProcessExecutionSpeed : 0u,
                StateMask = enable ? PowerThrottlingProcessExecutionSpeed : 0u
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
