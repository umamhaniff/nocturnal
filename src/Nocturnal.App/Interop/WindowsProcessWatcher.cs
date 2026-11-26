using System.Diagnostics;
using System.Runtime.InteropServices;
using Nocturnal.Core;

namespace Nocturnal.App.Interop;

public sealed class WindowsProcessWatcher : IProcessWatcher
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(
        IntPtr hProcess,
        int dwFlags,
        [Out] char[] lpExeName,
        ref int lpdwSize);

    public bool IsProcessRunning(ProcessWatchTarget target)
    {
        var expectedNorm = ProcessFocusPolicy.NormalizeProcessName(target.ProcessName);
        if (string.IsNullOrEmpty(expectedNorm))
        {
            return false;
        }

        if (target.Pid is { } pid)
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                return !proc.HasExited
                    && string.Equals(
                        ProcessFocusPolicy.NormalizeProcessName(proc.ProcessName),
                        expectedNorm,
                        StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        var matches = Process.GetProcessesByName(expectedNorm);
        try
        {
            foreach (var proc in matches)
            {
                try
                {
                    if (!proc.HasExited)
                    {
                        return true;
                    }
                }
                catch
                {
                    return true;
                }
            }
            return false;
        }
        finally
        {
            foreach (var proc in matches)
            {
                proc.Dispose();
            }
        }
    }

    public IReadOnlyList<RunningAppInfo> ListRunningSessionApps()
    {
        int currentSessionId;
        using (var self = Process.GetCurrentProcess())
        {
            currentSessionId = self.SessionId;
        }

        var processes = Process.GetProcesses();
        var grouped = new Dictionary<string, (int Count, long RamBytes, bool Protected)>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var proc in processes)
            {
                try
                {
                    if (proc.SessionId != currentSessionId)
                    {
                        continue;
                    }

                    var norm = ProcessFocusPolicy.NormalizeProcessName(proc.ProcessName);
                    if (string.IsNullOrEmpty(norm))
                    {
                        continue;
                    }

                    var ram = proc.WorkingSet64;
                    var path = TryQueryImagePath(proc.Id);
                    var isProtected = ProcessFocusPolicy.IsDefaultProtected(norm, path);

                    if (grouped.TryGetValue(norm, out var existing))
                    {
                        grouped[norm] = (existing.Count + 1, existing.RamBytes + ram, existing.Protected || isProtected);
                    }
                    else
                    {
                        grouped[norm] = (1, ram, isProtected);
                    }
                }
                catch
                {
                    // Skip transient or protected processes.
                }
            }

            return grouped
                .Select(kvp => new RunningAppInfo(kvp.Key, kvp.Value.Count, kvp.Value.RamBytes, kvp.Value.Protected))
                .OrderBy(a => a.IsProtectedByDefault ? 1 : 0)
                .ThenByDescending(a => a.TotalWorkingSetBytes)
                .ToList();
        }
        finally
        {
            foreach (var proc in processes)
            {
                proc.Dispose();
            }
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
}
