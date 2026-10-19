using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Dropzone.App.Tray;

/// <summary>The notification-area icon and its menu. Create and use on the UI thread.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly IntPtr _iconHandle;
    private readonly Icon _icon;
    private readonly ToolStripMenuItem _pauseItem;

    public TrayIcon()
    {
        _iconHandle = DrawIcon();
        _icon = Icon.FromHandle(_iconHandle);

        ContextMenuStrip menu = new();
        menu.Items.Add("设置", null, (_, _) => SettingsRequested?.Invoke());
        menu.Items.Add("打开“下载”文件夹", null, (_, _) => OpenDownloadsRequested?.Invoke());
        _pauseItem = new ToolStripMenuItem("暂停接收") { CheckOnClick = true };
        _pauseItem.CheckedChanged += (_, _) => PauseChanged?.Invoke(_pauseItem.Checked);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitRequested?.Invoke());

        NotifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "LocalSend Dropzone",
            ContextMenuStrip = menu,
            Visible = true,
        };
        NotifyIcon.DoubleClick += (_, _) => SettingsRequested?.Invoke();
    }

    /// <summary>The underlying icon; <see cref="Notifier"/> shows its balloons through it.</summary>
    public NotifyIcon NotifyIcon { get; }

    public event Action? SettingsRequested;

    public event Action? OpenDownloadsRequested;

    /// <summary>"暂停接收" was ticked (true) or unticked (false).</summary>
    public event Action<bool>? PauseChanged;

    public event Action? ExitRequested;

    public void Dispose()
    {
        NotifyIcon.Visible = false;
        NotifyIcon.ContextMenuStrip?.Dispose();
        NotifyIcon.Dispose();
        _icon.Dispose();
        DestroyIcon(_iconHandle);
    }

    /// <summary>A blue rounded square with a white arrow dropping into a tray.</summary>
    private static IntPtr DrawIcon()
    {
        using Bitmap bitmap = new(32, 32);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using (GraphicsPath square = RoundedRectangle(new Rectangle(1, 1, 30, 30), 7))
            using (SolidBrush blue = new(Color.FromArgb(0x1E, 0x7B, 0xE6)))
            {
                g.FillPath(blue, square);
            }

            using Pen white = new(Color.White, 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLine(white, 16, 6, 16, 18);
            g.DrawLines(white, [new PointF(10.5f, 13), new PointF(16, 18.5f), new PointF(21.5f, 13)]);
            g.DrawLines(white, [new PointF(8, 20), new PointF(8, 25), new PointF(24, 25), new PointF(24, 20)]);
        }

        return bitmap.GetHicon();
    }

    private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        int d = radius * 2;
        GraphicsPath path = new();
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
