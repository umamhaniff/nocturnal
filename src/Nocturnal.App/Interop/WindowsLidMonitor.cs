using System.Runtime.InteropServices;
using System.Windows.Forms;
using Nocturnal.Core;

namespace Nocturnal.App.Interop;

public sealed class WindowsLidMonitor : NativeWindow, IDisposable
{
    private const int WmPowerBroadcast = 0x0218;
    private const int PbtPowerSettingChange = 0x8013;
    private const uint DeviceNotifyWindowHandle = 0;
    private const int WsExToolWindow = 0x00000080;
    private static readonly Guid GuidLidSwitchStateChange = new("BA3E0F4D-B817-4094-A2D1-D56379E6A0F3");

    private readonly IAppLogger? _logger;
    private IntPtr _notificationHandle;
    private bool _disposed;

    public bool IsLidClosed { get; private set; }
    public event Action<bool>? LidStateChanged;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(
        IntPtr hRecipient,
        ref Guid powerSettingGuid,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    public WindowsLidMonitor(IAppLogger? logger = null)
    {
        _logger = logger;
        try
        {
            // Hidden tool top-level window (never HWND_MESSAGE) so Windows 11 delivers WM_POWERBROADCAST reliably.
            CreateHandle(new CreateParams
            {
                Parent = IntPtr.Zero,
                Caption = "NocturnalLidMonitor",
                X = 0,
                Y = 0,
                Width = 0,
                Height = 0,
                Style = 0,
                ExStyle = WsExToolWindow
            });

            var guid = GuidLidSwitchStateChange;
            _notificationHandle = RegisterPowerSettingNotification(Handle, ref guid, DeviceNotifyWindowHandle);
            _logger?.Info($"WindowsLidMonitor registered power setting notification (registered={_notificationHandle != IntPtr.Zero}).");
        }
        catch (Exception ex)
        {
            _logger?.Warn($"WindowsLidMonitor failed to register lid sensor: {ex.Message}");
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmPowerBroadcast
            && m.WParam.ToInt32() == PbtPowerSettingChange
            && m.LParam != IntPtr.Zero)
        {
            try
            {
                var settingGuid = Marshal.PtrToStructure<Guid>(m.LParam);
                if (settingGuid == GuidLidSwitchStateChange)
                {
                    var dataLen = Marshal.ReadInt32(m.LParam, 16);
                    var value = dataLen >= 4
                        ? Marshal.ReadInt32(m.LParam, 20)
                        : Marshal.ReadByte(m.LParam, 20);
                    var closed = value == 0;
                    _logger?.Info($"WM_POWERBROADCAST lid event received: {(closed ? "Closed" : "Opened")} (raw={value}).");
                    if (IsLidClosed != closed)
                    {
                        IsLidClosed = closed;
                        LidStateChanged?.Invoke(closed);
                    }
                }
            }
            catch
            {
                // Ignore malformed broadcast payloads.
            }
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        if (_notificationHandle != IntPtr.Zero)
        {
            UnregisterPowerSettingNotification(_notificationHandle);
            _notificationHandle = IntPtr.Zero;
        }

        if (Handle != IntPtr.Zero)
        {
            DestroyHandle();
        }
    }
}

public sealed class WindowsUserPresenceMonitor : IUserPresenceMonitor
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetLastInputInfo(ref LastInputInfo plii);

    public TimeSpan GetIdleDuration()
    {
        try
        {
            var info = new LastInputInfo
            {
                cbSize = (uint)Marshal.SizeOf<LastInputInfo>()
            };
            if (!GetLastInputInfo(ref info))
            {
                return TimeSpan.FromHours(1);
            }

            uint elapsedMs = unchecked((uint)Environment.TickCount - info.dwTime);
            return TimeSpan.FromMilliseconds(elapsedMs);
        }
        catch
        {
            return TimeSpan.FromHours(1);
        }
    }
}
