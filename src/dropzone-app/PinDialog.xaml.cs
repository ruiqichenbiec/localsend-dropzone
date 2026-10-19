using System.Threading.Tasks;
using System.Windows;

namespace Dropzone.App;

/// <summary>Asks for the PIN another device wants before it accepts files.</summary>
public partial class PinDialog : Window
{
    private readonly TaskCompletionSource<string?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _pin;

    private PinDialog(string deviceAlias)
    {
        InitializeComponent();
        Prompt.Text = $"“{deviceAlias}”需要 PIN 才能接收文件";
        Loaded += (_, _) =>
        {
            Activate();
            PinBox.Focus();
        };
        Closed += (_, _) => _result.TrySetResult(_pin);
    }

    /// <summary>
    /// Shows the dialog (not modal, so the drop zone and other dialogs stay usable) and completes with the PIN, or
    /// null when the user cancels, closes it or leaves it empty. Call on the UI thread
    /// (<see cref="AppServices.AskPinAsync"/> does that from any thread).
    /// </summary>
    public static Task<string?> AskAsync(string deviceAlias)
    {
        PinDialog dialog = new(deviceAlias);
        dialog.Show();
        return dialog._result.Task;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        string pin = PinBox.Text.Trim();
        _pin = pin.Length == 0 ? null : pin;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
