using System;
using System.Collections.Generic;
using System.Windows.Forms;
using System.Windows.Threading;
using Dropzone.App.SystemIntegration;

namespace Dropzone.App.Tray;

/// <summary>
/// Balloon notifications on the tray icon. <see cref="Show"/> may be called from any thread: it is marshalled to the
/// UI thread, and notifications that arrive together are shown one after another (a new balloon would otherwise
/// replace the one still on screen). Clicking a balloon runs the action given with it.
/// </summary>
public sealed class Notifier : IDisposable
{
    private const int TitleLimit = 63;
    private const int TextLimit = 255;
    private static readonly TimeSpan Spacing = TimeSpan.FromSeconds(4);

    private readonly NotifyIcon _icon;
    private readonly Dispatcher _dispatcher;
    private readonly Queue<(string Title, string Text, Action? OnClick)> _pending = new();
    private readonly DispatcherTimer _spacingTimer;
    private Action? _currentOnClick;
    private bool _disposed;

    public Notifier(NotifyIcon icon, Dispatcher dispatcher)
    {
        _icon = icon;
        _dispatcher = dispatcher;
        _spacingTimer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = Spacing };
        _spacingTimer.Tick += (_, _) => ShowNext();
        _icon.BalloonTipClicked += OnBalloonClicked;
    }

    /// <summary>Shows a notification; <paramref name="onClick"/> runs on the UI thread when it is clicked.</summary>
    public void Show(string title, string text, Action? onClick = null)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => Show(title, text, onClick));
            return;
        }

        if (_disposed)
        {
            return;
        }

        _pending.Enqueue((Truncate(title, TitleLimit), Truncate(text, TextLimit), onClick));
        if (!_spacingTimer.IsEnabled)
        {
            ShowNext();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _spacingTimer.Stop();
        _pending.Clear();
        _icon.BalloonTipClicked -= OnBalloonClicked;
    }

    private void ShowNext()
    {
        if (_disposed || !_pending.TryDequeue(out (string Title, string Text, Action? OnClick) next))
        {
            _spacingTimer.Stop();
            return;
        }

        _currentOnClick = next.OnClick;
        _icon.ShowBalloonTip((int)Spacing.TotalMilliseconds, next.Title, next.Text, ToolTipIcon.Info);
        _spacingTimer.Stop();
        _spacingTimer.Start();
    }

    private void OnBalloonClicked(object? sender, EventArgs e)
    {
        try
        {
            _currentOnClick?.Invoke();
        }
        catch (Exception error)
        {
            AppLog.Warn("Notification click action failed.", error);
        }
    }

    private static string Truncate(string value, int limit) => value.Length <= limit ? value : value[..(limit - 1)] + "…";
}
