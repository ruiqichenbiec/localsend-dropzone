using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Dropzone.Core.Settings;

namespace Dropzone.App;

/// <summary>
/// Shared password, dedicated port, send-area corner and start-with-Windows. Saving hands the new settings to the
/// app, which applies them at once (a changed port restarts the receive server).
/// </summary>
public partial class SettingsWindow : Window
{
    public const int MinPasswordLength = 12;

    private const int MinPort = 1024;
    private const int MaxPort = 65535;
    private const int OriginalPort = 53317;

    // ComboBox order in the XAML.
    private static readonly Corner[] Corners = [Corner.BottomRight, Corner.BottomLeft, Corner.TopRight, Corner.TopLeft];

    private readonly AppSettings _current;
    private readonly Func<AppSettings, Task<bool>> _apply;
    private readonly Func<int?> _originalPort;

    /// <param name="apply">Saves and applies the settings; false when saving failed.</param>
    /// <param name="originalPort">The port the official app is configured for, when known (read at save time).</param>
    public SettingsWindow(AppSettings current, Func<AppSettings, Task<bool>> apply, Func<int?> originalPort)
    {
        InitializeComponent();
        _current = current;
        _apply = apply;
        _originalPort = originalPort;
        PasswordInput.Password = current.Password ?? string.Empty;
        PortInput.Text = current.PrivatePort.ToString(CultureInfo.InvariantCulture);
        CornerInput.SelectedIndex = Math.Max(0, Array.IndexOf(Corners, current.Corner));
        AutoStartInput.IsChecked = current.AutoStart;
    }

    /// <summary>
    /// An empty or whitespace password means "no password"; otherwise it needs at least 12 non-space characters.
    /// </summary>
    public static bool IsPasswordTooShort(string password) =>
        !string.IsNullOrWhiteSpace(password) && password.Count(c => !char.IsWhiteSpace(c)) < MinPasswordLength;

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        PasswordError.Visibility = Visibility.Collapsed;
        PortError.Visibility = Visibility.Collapsed;
        SaveError.Visibility = Visibility.Collapsed;

        string password = PasswordInput.Password;
        bool valid = true;
        if (IsPasswordTooShort(password))
        {
            PasswordError.Visibility = Visibility.Visible;
            valid = false;
        }

        int? configured = _originalPort();
        string taken = configured is int other && other != OriginalPort ? $"{OriginalPort} 或 {other}" : $"{OriginalPort}";
        if (!int.TryParse(PortInput.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int port)
            || port is < MinPort or > MaxPort || port == OriginalPort || port == configured)
        {
            PortError.Text = $"端口要在 {MinPort}–{MaxPort} 之间，且不能是 {taken}";
            PortError.Visibility = Visibility.Visible;
            valid = false;
        }

        if (!valid)
        {
            return;
        }

        AppSettings next = _current with
        {
            Password = string.IsNullOrWhiteSpace(password) ? null : password,
            PrivatePort = port,
            Corner = Corners[Math.Max(0, CornerInput.SelectedIndex)],
            AutoStart = AutoStartInput.IsChecked == true,
        };

        SaveButton.IsEnabled = false;
        try
        {
            if (await _apply(next))
            {
                Close();
                return;
            }
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }

        SaveError.Text = "保存失败，详情见日志";
        SaveError.Visibility = Visibility.Visible;
    }
}
