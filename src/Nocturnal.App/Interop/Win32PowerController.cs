using System.Runtime.InteropServices;
using Nocturnal.Core;

namespace Nocturnal.App.Interop;

public sealed class Win32PowerController : IPowerController
{
    [Flags]
    private enum ExecutionState : uint
    {
        EsAwaymodeRequired = 0x00000040,
        EsContinuous = 0x80000000,
        EsDisplayRequired = 0x00000002,
        EsSystemRequired = 0x00000001
    }

    private static readonly Guid SubButtons = new("4f971e89-eebd-4455-a8de-9e59040e7347");
    private static readonly Guid LidAction = new("5ca83367-6e45-459f-a27b-476b1d01c936");
    private static readonly Guid SubEnergySaver = new("de830923-a562-41af-a086-e3a2c6bad2da");
    private static readonly Guid EsBattThreshold = new("e69653ca-cf7f-4f05-aa73-cb833fa90ad4");

    private uint? _originalEnergySaverThreshold;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState esFlags);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerReadACValueIndex(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        ref Guid subGroupOfPowerSettingsGuid,
        ref Guid powerSettingGuid,
        out uint acValueIndex);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerReadDCValueIndex(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        ref Guid subGroupOfPowerSettingsGuid,
        ref Guid powerSettingGuid,
        out uint dcValueIndex);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerWriteACValueIndex(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        ref Guid subGroupOfPowerSettingsGuid,
        ref Guid powerSettingGuid,
        uint acValueIndex);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerWriteDCValueIndex(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        ref Guid subGroupOfPowerSettingsGuid,
        ref Guid powerSettingGuid,
        uint dcValueIndex);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SendNotifyMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public void EnableAwakeAssertion(bool keepDisplayAwake)
    {
        var flags = ExecutionState.EsContinuous | ExecutionState.EsSystemRequired;
        if (keepDisplayAwake)
        {
            flags |= ExecutionState.EsDisplayRequired;
        }
        SetThreadExecutionState(flags);
    }

    public void DisableAwakeAssertion()
    {
        SetThreadExecutionState(ExecutionState.EsContinuous);
    }

    public LidActionBackup? ReadLidActionBackup()
    {
        if (!TryGetActiveScheme(out var schemeGuid))
        {
            return null;
        }

        var sub = SubButtons;
        var lid = LidAction;

        if (PowerReadACValueIndex(IntPtr.Zero, ref schemeGuid, ref sub, ref lid, out var origAc) != 0)
        {
            return null;
        }

        if (PowerReadDCValueIndex(IntPtr.Zero, ref schemeGuid, ref sub, ref lid, out var origDc) != 0)
        {
            return null;
        }

        return new LidActionBackup(schemeGuid, (int)origAc, (int)origDc);
    }

    public void ApplyClamshellMode(Guid schemeGuid)
    {
        var sub = SubButtons;
        var lid = LidAction;

        // 0 = Do nothing when lid is closed
        PowerWriteACValueIndex(IntPtr.Zero, ref schemeGuid, ref sub, ref lid, 0);
        PowerWriteDCValueIndex(IntPtr.Zero, ref schemeGuid, ref sub, ref lid, 0);
        PowerSetActiveScheme(IntPtr.Zero, ref schemeGuid);
    }

    public void RestoreClamshellMode(LidActionBackup backup)
    {
        var schemeGuid = backup.SchemeGuid;
        var sub = SubButtons;
        var lid = LidAction;

        PowerWriteACValueIndex(IntPtr.Zero, ref schemeGuid, ref sub, ref lid, (uint)backup.OriginalAcLidAction);
        PowerWriteDCValueIndex(IntPtr.Zero, ref schemeGuid, ref sub, ref lid, (uint)backup.OriginalDcLidAction);
        PowerSetActiveScheme(IntPtr.Zero, ref schemeGuid);
    }

    public bool TrySetPowerSaver(bool enabled)
    {
        try
        {
            if (!TryGetActiveScheme(out var schemeGuid))
            {
                return false;
            }

            var sub = SubEnergySaver;
            var setting = EsBattThreshold;

            if (enabled)
            {
                if (_originalEnergySaverThreshold is null
                    && PowerReadDCValueIndex(IntPtr.Zero, ref schemeGuid, ref sub, ref setting, out var orig) == 0)
                {
                    _originalEnergySaverThreshold = orig;
                }

                var res = PowerWriteDCValueIndex(IntPtr.Zero, ref schemeGuid, ref sub, ref setting, 100);
                PowerSetActiveScheme(IntPtr.Zero, ref schemeGuid);
                return res == 0;
            }

            var restore = _originalEnergySaverThreshold ?? 20u;
            _originalEnergySaverThreshold = null;
            var restoreRes = PowerWriteDCValueIndex(IntPtr.Zero, ref schemeGuid, ref sub, ref setting, restore);
            PowerSetActiveScheme(IntPtr.Zero, ref schemeGuid);
            return restoreRes == 0;
        }
        catch
        {
            return false;
        }
    }

    public void TurnOffDisplay()
    {
        const uint WmSysCommand = 0x0112;
        const int ScMonitorPower = 0xF170;
        const int MonitorOff = 2;
        var hwndBroadcast = new IntPtr(0xFFFF);

        SendNotifyMessageW(hwndBroadcast, WmSysCommand, new IntPtr(ScMonitorPower), new IntPtr(MonitorOff));
    }

    public void SleepSystem()
    {
        SetSuspendState(hibernate: false, forceCritical: false, disableWakeEvent: false);
    }

    private static bool TryGetActiveScheme(out Guid schemeGuid)
    {
        schemeGuid = Guid.Empty;
        if (PowerGetActiveScheme(IntPtr.Zero, out var ptr) != 0 || ptr == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            schemeGuid = Marshal.PtrToStructure<Guid>(ptr);
            return schemeGuid != Guid.Empty;
        }
        finally
        {
            LocalFree(ptr);
        }
    }
}
