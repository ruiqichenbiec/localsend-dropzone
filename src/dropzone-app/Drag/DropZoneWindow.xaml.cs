using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Dropzone.App.SystemIntegration;
using Dropzone.Core.Discovery;
using Dropzone.Core.Send;
using Dropzone.Core.Settings;
using Dropzone.Core.Ui;
using Rectangle = System.Drawing.Rectangle;

namespace Dropzone.App.Drag;

/// <summary>
/// The corner drop zone. Hidden until a drag nears the corner (<see cref="ShowAt"/>); dragging onto the zone pops out
/// the nearby devices beside it. Dropping on a device sends to it in the background; dropping on the zone hands the
/// files to the official app. The window never takes focus, and its transparent parts let clicks through.
/// Use on the UI thread.
/// </summary>
public partial class DropZoneWindow : Window
{
    private const string NotificationTitle = "LocalSend Dropzone";
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const uint SwpNoActivate = 0x0010;
    private static readonly IntPtr HwndTopmost = new(-1);

    /// <summary>A drop arrives through OLE just after the button goes up; wait this long before giving up on it.</summary>
    private static readonly TimeSpan DropGrace = TimeSpan.FromSeconds(1);

    private static readonly Brush ZoneBrush = Frozen(Color.FromArgb(0xE6, 0x1E, 0x22, 0x27));
    private static readonly Brush CardBrush = Frozen(Color.FromArgb(0xF0, 0x2A, 0x2E, 0x35));
    private static readonly Brush HighlightBrush = Frozen(Color.FromRgb(0x13, 0x8A, 0x8A));
    private static readonly Brush EdgeBrush = Frozen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
    private static readonly Brush GrayText = Frozen(Color.FromRgb(0x9A, 0xA0, 0xA6));

    private readonly AppServices _services;
    private readonly CancellationTokenSource _stop = new();
    private readonly DispatcherTimer _hideTimer;
    private readonly List<FrameworkElement> _cards = [];

    private Rectangle _workArea;
    private Corner _corner;
    private bool _expanded;
    private FrameworkElement? _hovered;

    /// <summary>An OLE drag (DragEnter/DragOver) reached the window since it was shown; only then can a drop follow.</summary>
    private bool _oleDragSeen;

    /// <summary>Zone and panel as last laid out by <see cref="Arrange"/>, in physical pixels.</summary>
    private Rectangle _zoneRect;
    private Rectangle _panelRect;

    /// <summary>Sends still running (UI thread only); <see cref="_stop"/> is disposed once the window is closed and this reaches 0.</summary>
    private int _sending;
    private bool _closed;

    public DropZoneWindow(AppServices services)
    {
        InitializeComponent();
        _services = services;
        _hideTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = DropGrace };
        _hideTimer.Tick += (_, _) => HideZone();
        SourceInitialized += (_, _) => AddExtendedStyles();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (IsVisible)
            {
                Arrange();
            }
        });
        _services.Registry.Changed += OnRegistryChanged;
        Closed += (_, _) =>
        {
            _services.Registry.Changed -= OnRegistryChanged;
            _closed = true;
            _stop.Cancel();
            if (_sending == 0)
            {
                _stop.Dispose();
            }
        };
    }

    /// <summary>A drag reached the corner of the monitor with <paramref name="workArea"/>: show the zone only.</summary>
    public void ShowAt(Rectangle workArea)
    {
        _hideTimer.Stop();
        _workArea = workArea;
        _corner = _services.Settings.Corner;
        _expanded = false;
        _oleDragSeen = false;
        SetHovered(null);
        RefreshDiscovery();

        Arrange();
        if (!IsVisible)
        {
            Show();
            // Show may settle the window on the monitor's DPI; place it again in physical pixels.
            Arrange();
        }
    }

    /// <summary>The mouse button went up at <paramref name="point"/> (physical pixels, from the hook).</summary>
    public void OnReleased(System.Drawing.Point point)
    {
        if (!IsVisible)
        {
            return;
        }

        bool over = _zoneRect.Contains(point) || (_expanded && _panelRect.Contains(point));
        if (_oleDragSeen && over)
        {
            // OnDrop hides at once when the drop comes; this only covers a drop that never does.
            _hideTimer.Stop();
            _hideTimer.Start();
        }
        else
        {
            // Released elsewhere, or not a file drag at all (a window or a text selection): no drop will come.
            HideZone();
        }
    }

    private void RefreshDiscovery()
    {
        try
        {
            // Announces and scans in the background; never awaited here.
            _services.Discovery.RefreshNowAsync().ContinueWith(
                t => AppLog.Warn("Refreshing discovery failed.", t.Exception),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }
        catch (ObjectDisposedException)
        {
            // The discovery stack was being replaced by a settings change.
        }
    }

    private void OnRegistryChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (IsVisible)
        {
            Arrange();
        }
    });

    private IReadOnlyList<DiscoveredDevice> Devices() =>
        _services.Registry.Snapshot(DateTimeOffset.UtcNow).Take(CornerGeometry.MaxPanelItems(_workArea)).ToList();

    /// <summary>Sizes the window to the zone plus the panel (physical pixels) and lays out the zone and the cards.</summary>
    private void Arrange()
    {
        IReadOnlyList<DiscoveredDevice> devices = Devices();
        Rectangle zone = CornerGeometry.ZoneRect(_workArea, _corner);
        Rectangle panel = CornerGeometry.PanelRect(_workArea, _corner, devices.Count);
        Rectangle bounds = Rectangle.Union(zone, panel);
        _zoneRect = zone;
        _panelRect = panel;

        IntPtr hwnd = new WindowInteropHelper(this).EnsureHandle();
        SetWindowPos(hwnd, HwndTopmost, bounds.X, bounds.Y, bounds.Width, bounds.Height, SwpNoActivate);

        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        Place(ZoneHost, zone, bounds, scale);

        bool zoneHovered = ReferenceEquals(_hovered, ZoneHost);
        string? hoveredFingerprint = (_hovered?.Tag as DiscoveredDevice)?.Fingerprint;
        foreach (FrameworkElement card in _cards)
        {
            Root.Children.Remove(card);
        }

        _cards.Clear();
        if (_expanded)
        {
            if (devices.Count == 0)
            {
                AddCard(null, Row(panel, 0), bounds, scale);
            }

            for (int i = 0; i < devices.Count; i++)
            {
                AddCard(devices[i], Row(panel, i), bounds, scale);
            }
        }

        // Keep the highlight on the zone, or on the same device, across a rebuild (the old cards are gone).
        FrameworkElement? again = zoneHovered
            ? ZoneHost
            : _cards.FirstOrDefault(c => c.Tag is DiscoveredDevice d && hoveredFingerprint is not null
                && string.Equals(d.Fingerprint, hoveredFingerprint, StringComparison.OrdinalIgnoreCase));
        _hovered = zoneHovered ? ZoneHost : null;
        SetHovered(again);
    }

    private static Rectangle Row(Rectangle panel, int index) => new(
        panel.X,
        panel.Y + index * (CornerGeometry.ItemHeight + CornerGeometry.ItemSpacing),
        panel.Width,
        CornerGeometry.ItemHeight);

    private void AddCard(DiscoveredDevice? device, Rectangle rect, Rectangle bounds, double scale)
    {
        TextBlock name = new()
        {
            Text = device?.Alias ?? "没有发现设备",
            Foreground = device is null ? GrayText : Brushes.White,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        Grid content = new() { IsHitTestVisible = false };
        if (device is not null)
        {
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            content.ColumnDefinitions.Add(new ColumnDefinition());
            TextBlock icon = new()
            {
                Text = string.Equals(device.DeviceType, "mobile", StringComparison.OrdinalIgnoreCase) ? "" : "",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 18,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(name, 1);
            name.Margin = new Thickness(0, 0, 10, 0);
            content.Children.Add(icon);
        }
        else
        {
            name.Margin = new Thickness(12, 0, 10, 0);
        }

        content.Children.Add(name);

        Border box = new()
        {
            Width = rect.Width,
            Height = rect.Height,
            CornerRadius = new CornerRadius(8),
            Background = CardBrush,
            BorderBrush = EdgeBrush,
            BorderThickness = new Thickness(1),
            Child = content,
        };

        Viewbox card = new() { Stretch = Stretch.Fill, Child = box, Tag = device };
        Place(card, rect, bounds, scale);
        Root.Children.Add(card);
        _cards.Add(card);
    }

    /// <summary>Puts <paramref name="element"/> at <paramref name="rect"/> (physical pixels) inside the window.</summary>
    private static void Place(FrameworkElement element, Rectangle rect, Rectangle bounds, double scale)
    {
        Canvas.SetLeft(element, (rect.X - bounds.X) / scale);
        Canvas.SetTop(element, (rect.Y - bounds.Y) / scale);
        element.Width = rect.Width / scale;
        element.Height = rect.Height / scale;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        _oleDragSeen = true;
        FrameworkElement? target = TargetAt(e);
        if (ReferenceEquals(target, ZoneHost) && !_expanded)
        {
            _expanded = true;
            Arrange();
            // Arrange moved the window and the zone inside it; lay out before hit-testing again.
            UpdateLayout();
            target = TargetAt(e);
        }

        SetHovered(target);
        e.Effects = target is not null && e.Data.GetDataPresent(DataFormats.FileDrop) && IsDropTarget(target)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        // Also raised when moving between elements; the next DragOver sets the highlight again.
        SetHovered(null);
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        FrameworkElement? target = TargetAt(e);
        string[]? paths = ReadPaths(e.Data);
        e.Effects = target is not null && paths is { Length: > 0 } && IsDropTarget(target)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
        HideZone();
        if (e.Effects == DragDropEffects.None || paths is null)
        {
            return;
        }

        // The data object is only valid during the drop: keep a copy of the paths.
        string[] copy = [.. paths];
        if (target!.Tag is DiscoveredDevice device)
        {
            AppLog.Info($"Dropped {copy.Length} path(s) on {device.Alias}.");
            _sending++;
            CancellationToken ct = _stop.Token;
            _ = Task.Run(() => SendAsync(device, copy, ct));
        }
        else
        {
            AppLog.Info($"Dropped {copy.Length} path(s) on the zone.");
            _ = Task.Run(() => OriginalLauncher.OpenWithFiles(copy, Notify));
        }
    }

    private static string[]? ReadPaths(IDataObject data)
    {
        try
        {
            return data.GetData(DataFormats.FileDrop) as string[];
        }
        catch (Exception error) when (error is COMException or InvalidOperationException)
        {
            AppLog.Warn("Reading the dropped paths failed.", error);
            return null;
        }
    }

    private bool IsDropTarget(FrameworkElement target) =>
        ReferenceEquals(target, ZoneHost) || target.Tag is DiscoveredDevice;

    /// <summary>The zone or the card under the drag, or null.</summary>
    private FrameworkElement? TargetAt(DragEventArgs e)
    {
        DependencyObject? hit = Root.InputHitTest(e.GetPosition(Root)) as DependencyObject;
        while (hit is not null)
        {
            if (hit is FrameworkElement element && (ReferenceEquals(element, ZoneHost) || _cards.Contains(element)))
            {
                return element;
            }

            hit = VisualTreeHelper.GetParent(hit);
        }

        return null;
    }

    private void SetHovered(FrameworkElement? target)
    {
        if (ReferenceEquals(target, _hovered))
        {
            return;
        }

        if (_hovered is not null)
        {
            SetBackground(_hovered, ReferenceEquals(_hovered, ZoneHost) ? ZoneBrush : CardBrush);
        }

        _hovered = target is not null && IsDropTarget(target) ? target : null;
        if (_hovered is not null)
        {
            SetBackground(_hovered, HighlightBrush);
        }
    }

    private static void SetBackground(FrameworkElement element, Brush brush)
    {
        if (element is Viewbox { Child: Border border })
        {
            border.Background = brush;
        }
    }

    private void HideZone()
    {
        _hideTimer.Stop();
        SetHovered(null);
        _expanded = false;
        if (IsVisible)
        {
            Hide();
        }
    }

    /// <summary>Runs on a background thread; the PIN prompt and notifications are marshalled to the UI thread.</summary>
    private async Task SendAsync(DiscoveredDevice device, string[] paths, CancellationToken ct)
    {
        try
        {
            SendResult result = await _services.TransferClient
                .SendAsync(device, paths, () => _services.AskPinAsync(device.Alias), ct)
                .ConfigureAwait(false);
            AppLog.Info($"Send to {device.Alias}: {result.Outcome}{(result.Detail is null ? string.Empty : $" ({result.Detail})")}.");
            switch (result.Outcome)
            {
                case SendOutcome.Sent:
                    Notify($"已发送到 {device.Alias}");
                    break;
                case SendOutcome.SentAfterPasswordMismatch:
                    _services.Notifier.Show($"已发送到 {device.Alias}", "密码不一致，已改为普通发送");
                    break;
                case SendOutcome.Rejected:
                    Notify($"{device.Alias} 拒绝了文件");
                    break;
                case SendOutcome.Busy:
                    Notify($"{device.Alias} 正忙，稍后再试");
                    break;
                case SendOutcome.Cancelled:
                    break;
                default:
                    NotifyFailed(device, result.Detail);
                    break;
            }
        }
        catch (Exception error)
        {
            AppLog.Error($"Sending to {device.Alias} failed.", error);
            NotifyFailed(device, null);
        }
        finally
        {
            _ = Dispatcher.BeginInvoke(SendFinished);
        }
    }

    /// <summary>UI thread: one send ended; disposes <see cref="_stop"/> once the window is closed and none are left.</summary>
    private void SendFinished()
    {
        _sending--;
        if (_closed && _sending == 0)
        {
            _stop.Dispose();
        }
    }

    private void NotifyFailed(DiscoveredDevice device, string? detail)
    {
        string text = $"发送到 {device.Alias} 失败";
        if (string.IsNullOrWhiteSpace(detail))
        {
            Notify(text);
        }
        else
        {
            _services.Notifier.Show(text, detail);
        }
    }

    private void Notify(string text) => _services.Notifier.Show(NotificationTitle, text);

    private void AddExtendedStyles()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        int style = GetWindowLong(hwnd, GwlExStyle);
        SetWindowLong(hwnd, GwlExStyle, style | WsExToolWindow | WsExNoActivate);
    }

    private static Brush Frozen(Color color)
    {
        SolidColorBrush brush = new(color);
        brush.Freeze();
        return brush;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
