using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Nocturnal.Core;

namespace Nocturnal.App.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly SessionEngine _engine;
    private readonly Action _togglePopup;
    private readonly Action _exitApp;

    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _clamshellItem;
    private readonly ToolStripMenuItem _focusItem;
    private readonly ToolStripMenuItem _stopItem;
    private readonly ToolStripMenuItem _updateItem;
    private readonly Dictionary<TrayUiStyle, ToolStripMenuItem> _styleItems = [];

    private bool? _lastRenderedActive;
    private int _lastRenderedProgressBucket = -1;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public TrayIconService(SessionEngine engine, Action togglePopup, Action exitApp)
    {
        _engine = engine;
        _togglePopup = togglePopup;
        _exitApp = exitApp;

        _statusItem = new ToolStripMenuItem("Nocturnal: Idle") { Enabled = false };
        _clamshellItem = new ToolStripMenuItem("Clamshell (Lid-Closed Awake)")
        {
            Checked = _engine.Config.ClamshellEnabled,
            CheckOnClick = true
        };
        _clamshellItem.Click += (_, _) => _engine.SetClamshell(_clamshellItem.Checked);

        _focusItem = new ToolStripMenuItem("Agent Focus Mode (EcoQoS)")
        {
            Checked = _engine.Config.AgentFocusEnabled,
            CheckOnClick = true
        };
        _focusItem.Click += (_, _) => _engine.SetAgentFocus(_focusItem.Checked);

        _stopItem = new ToolStripMenuItem("Stop Session");
        _stopItem.Click += (_, _) => _engine.Stop(StopReason.Manual);

        _updateItem = new ToolStripMenuItem("Update Available") { Visible = false };
        _updateItem.Click += (_, _) => _togglePopup();

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(CreatePresetItem("Stay Awake +15m", TimeSpan.FromMinutes(15)));
        menu.Items.Add(CreatePresetItem("Stay Awake +30m", TimeSpan.FromMinutes(30)));
        menu.Items.Add(CreatePresetItem("Stay Awake +1h", TimeSpan.FromHours(1)));
        menu.Items.Add(CreatePresetItem("Stay Awake +2h", TimeSpan.FromHours(2)));
        menu.Items.Add(CreatePresetItem("Stay Awake +4h", TimeSpan.FromHours(4)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_clamshellItem);
        menu.Items.Add(_focusItem);
        menu.Items.Add(CreateStyleMenu());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_stopItem);
        menu.Items.Add(_updateItem);
        menu.Items.Add("Open Nocturnal", null, (_, _) => _togglePopup());
        menu.Items.Add("Exit", null, (_, _) => _exitApp());

        _notifyIcon = new NotifyIcon
        {
            Visible = true,
            Text = "Nocturnal — Idle",
            ContextMenuStrip = menu,
            Icon = RenderCrescentPulseIcon(isActive: false, progress: 0)
        };
        _lastRenderedActive = false;
        _lastRenderedProgressBucket = -1;

        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                if (_engine.Config.UiStyle == TrayUiStyle.Minimal)
                {
                    _engine.AddTime(TimeSpan.FromMinutes(_engine.Config.DefaultAddMinutes));
                }
                else
                {
                    _togglePopup();
                }
            }
        };
    }

    public void Update(SessionStatus status)
    {
        _clamshellItem.Checked = status.ClamshellEnabled;
        _focusItem.Checked = _engine.Config.AgentFocusEnabled;
        _stopItem.Enabled = status.IsActive;
        foreach (var (style, item) in _styleItems)
        {
            item.Checked = _engine.Config.UiStyle == style;
        }

        if (status.IsActive)
        {
            var rem = FormatRemaining(status.Remaining);
            _statusItem.Text = $"Awake: {rem} left";
            _notifyIcon.Text = TruncateTooltip($"Nocturnal — {rem} left" + (status.ClamshellEnabled ? " (Clamshell)" : ""));
        }
        else
        {
            _statusItem.Text = "Nocturnal: Idle";
            _notifyIcon.Text = "Nocturnal — Idle";
        }

        var progressBucket = status.IsActive
            ? (int)Math.Round(Math.Clamp(status.Progress, 0.0, 1.0) * 100.0)
            : -1;

        if (_lastRenderedActive != status.IsActive || _lastRenderedProgressBucket != progressBucket)
        {
            _lastRenderedActive = status.IsActive;
            _lastRenderedProgressBucket = progressBucket;

            var oldIcon = _notifyIcon.Icon;
            _notifyIcon.Icon = RenderCrescentPulseIcon(status.IsActive, status.Progress);
            oldIcon?.Dispose();
        }
    }

    private ToolStripMenuItem CreatePresetItem(string label, TimeSpan duration)
    {
        var item = new ToolStripMenuItem(label);
        item.Click += (_, _) => _engine.AddTime(duration);
        return item;
    }

    private ToolStripMenuItem CreateStyleMenu()
    {
        var parent = new ToolStripMenuItem("UI Style");
        foreach (var style in Enum.GetValues<TrayUiStyle>())
        {
            var item = new ToolStripMenuItem(style.ToString())
            {
                Checked = _engine.Config.UiStyle == style
            };
            item.Click += (_, _) => _engine.UpdateConfig(c => c.UiStyle = style);
            _styleItems[style] = item;
            parent.DropDownItems.Add(item);
        }
        return parent;
    }

    private static string FormatRemaining(TimeSpan ts)
    {
        if (ts.TotalDays >= 1)
        {
            return $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";
        }
        if (ts.TotalHours >= 1)
        {
            return $"{(int)ts.TotalHours}h {ts.Minutes:D2}m";
        }
        return $"{ts.Minutes}m {ts.Seconds:D2}s";
    }

    private static string TruncateTooltip(string text) =>
        text.Length <= 63 ? text : text[..63];

    private static Icon RenderCrescentPulseIcon(bool isActive, double progress)
    {
        using var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        if (isActive)
        {
            // Progress ring around the crescent pulse
            using var trackPen = new Pen(Color.FromArgb(65, 255, 255, 255), 2.0f);
            g.DrawEllipse(trackPen, 1.5f, 1.5f, 29f, 29f);

            var sweep = (float)(360.0 * Math.Clamp(1.0 - progress, 0.0, 1.0));
            if (sweep > 1f)
            {
                using var ringPen = new Pen(Color.FromArgb(110, 231, 183), 2.3f)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                };
                g.DrawArc(ringPen, 1.5f, 1.5f, 29f, 29f, -90f, sweep);
            }

            // Draw crescent on a scratch bitmap so the cutout does not erase the progress ring
            using (var scratch = new Bitmap(32, 32))
            using (var sg = Graphics.FromImage(scratch))
            {
                sg.SmoothingMode = SmoothingMode.AntiAlias;
                sg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                sg.Clear(Color.Transparent);

                using var crescentBrush = new SolidBrush(Color.FromArgb(52, 211, 153));
                sg.FillEllipse(crescentBrush, 7.0f, 7.5f, 17.0f, 17.0f);

                sg.CompositingMode = CompositingMode.SourceCopy;
                sg.FillEllipse(Brushes.Transparent, 12.6f, 6.2f, 14.4f, 14.4f);

                g.CompositingMode = CompositingMode.SourceOver;
                g.DrawImageUnscaled(scratch, 0, 0);
            }

            using var pulsePen = new Pen(Color.FromArgb(236, 253, 245), 2.1f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            g.DrawLines(pulsePen,
            [
                new PointF(10.5f, 17.5f),
                new PointF(14.2f, 17.5f),
                new PointF(16.4f, 11.8f),
                new PointF(18.8f, 20.8f),
                new PointF(20.8f, 16.2f),
                new PointF(24.5f, 16.2f)
            ]);
        }
        else
        {
            using var crescentBrush = new SolidBrush(Color.FromArgb(148, 163, 184));
            g.FillEllipse(crescentBrush, 5.0f, 5.8f, 20.4f, 20.4f);

            g.CompositingMode = CompositingMode.SourceCopy;
            g.FillEllipse(Brushes.Transparent, 11.6f, 4.2f, 17.2f, 17.2f);

            g.CompositingMode = CompositingMode.SourceOver;
            using var pulsePen = new Pen(Color.FromArgb(203, 213, 225), 2.1f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            g.DrawLines(pulsePen,
            [
                new PointF(9.5f, 17.8f),
                new PointF(13.8f, 17.8f),
                new PointF(16.4f, 11.2f),
                new PointF(19.2f, 21.4f),
                new PointF(21.6f, 16.2f),
                new PointF(26.0f, 16.2f)
            ]);

            // Diagonal '/' slash overlay with clean transparent knockout channel
            g.CompositingMode = CompositingMode.SourceCopy;
            using (var knockoutPen = new Pen(Color.Transparent, 5.2f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            })
            {
                g.DrawLine(knockoutPen, 5.5f, 26.5f, 26.5f, 5.5f);
            }

            g.CompositingMode = CompositingMode.SourceOver;
            using var slashPen = new Pen(Color.FromArgb(248, 113, 113), 2.8f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            g.DrawLine(slashPen, 5.5f, 26.5f, 26.5f, 5.5f);
        }

        var hIcon = bmp.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(hIcon).Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    public void ShowUpdateAvailable(UpdateReleaseInfo release)
    {
        _updateItem.Text = $"Update Available ({release.TagName})";
        _updateItem.Visible = true;
        _notifyIcon.ShowBalloonTip(
            5000,
            "Nocturnal Update Available",
            $"{release.TagName} is ready. Open Nocturnal and click Update.",
            ToolTipIcon.Info);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        var icon = _notifyIcon.Icon;
        _notifyIcon.Icon = null;
        icon?.Dispose();
        _notifyIcon.Dispose();
    }
}
