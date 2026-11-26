using System.Diagnostics;
using Nocturnal.Core;

namespace Nocturnal.App.Interop;

public sealed class WindowsProcessWatcher : IProcessWatcher
{
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
                    // Treat inaccessible live process handle as running rather than falsely expired.
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
}
