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
    private static readonly IntPtr InvalidHandleValue = new(-1);

    private const uint PowerRequestContextVersion = 0;
    private const uint PowerRequestContextSimpleString = 0x00000001;

    private enum PowerRequestType
    {
        DisplayRequired = 0,
        SystemRequired = 1,
        AwayModeRequired = 2,
        ExecutionRequired = 3
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerRequestContext
    {
        public uint Version;
        public uint Flags;
        public IntPtr SimpleReasonString;
        public ulong Reserved1;
        public IntPtr Reserved2;
    }

    private uint? _originalEnergySaverThreshold;
    private IntPtr _powerRequestHandle;
    private bool _executionRequestActive;
    private bool _displayRequestActive;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState esFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr PowerCreateRequest(ref PowerRequestContext context);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerSetRequest(IntPtr powerRequest, PowerRequestType requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerClearRequest(IntPtr powerRequest, PowerRequestType requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

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

        EnsurePowerRequestHandle();
        if (_powerRequestHandle != IntPtr.Zero)
        {
            if (!_executionRequestActive)
            {
                PowerSetRequest(_powerRequestHandle, PowerRequestType.SystemRequired);
                PowerSetRequest(_powerRequestHandle, PowerRequestType.ExecutionRequired);
                _executionRequestActive = true;
            }

            if (keepDisplayAwake && !_displayRequestActive)
            {
                PowerSetRequest(_powerRequestHandle, PowerRequestType.DisplayRequired);
                _displayRequestActive = true;
            }
            else if (!keepDisplayAwake && _displayRequestActive)
            {
                PowerClearRequest(_powerRequestHandle, PowerRequestType.DisplayRequired);
                _displayRequestActive = false;
            }
        }
    }

    public void DisableAwakeAssertion()
    {
        if (_powerRequestHandle != IntPtr.Zero)
        {
            try
            {
                if (_displayRequestActive)
                {
                    PowerClearRequest(_powerRequestHandle, PowerRequestType.DisplayRequired);
                }

                if (_executionRequestActive)
                {
                    PowerClearRequest(_powerRequestHandle, PowerRequestType.ExecutionRequired);
                    PowerClearRequest(_powerRequestHandle, PowerRequestType.SystemRequired);
                }
            }
            catch
            {
                // Ignore clear failures during shutdown/sleep transitions.
            }
            finally
            {
                CloseHandle(_powerRequestHandle);
                _powerRequestHandle = IntPtr.Zero;
                _displayRequestActive = false;
                _executionRequestActive = false;
            }
        }

        SetThreadExecutionState(ExecutionState.EsContinuous);
    }

    private void EnsurePowerRequestHandle()
    {
        if (_powerRequestHandle != IntPtr.Zero)
        {
            return;
        }

        var reasonPtr = IntPtr.Zero;
        try
        {
            reasonPtr = Marshal.StringToHGlobalUni("Nocturnal active keep-awake session");
            var context = new PowerRequestContext
            {
                Version = PowerRequestContextVersion,
                Flags = PowerRequestContextSimpleString,
                SimpleReasonString = reasonPtr
            };

            var handle = PowerCreateRequest(ref context);
            if (handle != IntPtr.Zero && handle != InvalidHandleValue)
            {
                _powerRequestHandle = handle;
            }
        }
        catch
        {
            _powerRequestHandle = IntPtr.Zero;
        }
        finally
        {
            if (reasonPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(reasonPtr);
            }
        }
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

    public int? ReadEnergySaverThreshold()
    {
        try
        {
            if (!TryGetActiveScheme(out var schemeGuid))
            {
                return null;
            }

            var sub = SubEnergySaver;
            var setting = EsBattThreshold;
            return PowerReadDCValueIndex(IntPtr.Zero, ref schemeGuid, ref sub, ref setting, out var orig) == 0
                ? (int)orig
                : null;
        }
        catch
        {
            return null;
        }
    }

    public bool TrySetPowerSaver(bool enabled, int? restoreThreshold = null)
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

            var restore = restoreThreshold is >= 0 and <= 100
                ? (uint)restoreThreshold.Value
                : (_originalEnergySaverThreshold ?? 20u);
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
