using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Dropzone.App.Tray;
using Dropzone.Core.Discovery;
using Dropzone.Core.Send;
using Dropzone.Core.Settings;

namespace Dropzone.App;

/// <summary>
/// What the rest of the app (the drop zone) needs from the composition root. <see cref="Discovery"/> and
/// <see cref="TransferClient"/> are replaced when the private port changes, and <see cref="Settings"/> always returns
/// the settings in force, so read them each time they are used rather than keeping them; <see cref="Changed"/> says
/// when the settings window saved. Readable from any thread.
/// </summary>
public sealed class AppServices
{
    private readonly Dispatcher _dispatcher;
    private readonly Func<AppSettings> _settings;
    private DiscoveryService _discovery;
    private TransferClient _transferClient;

    internal AppServices(
        DeviceRegistry registry,
        DiscoveryService discovery,
        TransferClient transferClient,
        Func<AppSettings> settings,
        Notifier notifier,
        Dispatcher dispatcher)
    {
        Registry = registry;
        _discovery = discovery;
        _transferClient = transferClient;
        _settings = settings;
        Notifier = notifier;
        _dispatcher = dispatcher;
    }

    /// <summary>Raised on the UI thread after settings were saved (and the discovery stack possibly replaced).</summary>
    public event Action? Changed;

    /// <summary>The devices seen on the network. The same instance for the whole run.</summary>
    public DeviceRegistry Registry { get; }

    public DiscoveryService Discovery => Volatile.Read(ref _discovery);

    public TransferClient TransferClient => Volatile.Read(ref _transferClient);

    /// <summary>The settings in force right now (read only; changes go through the settings window).</summary>
    public AppSettings Settings => _settings();

    /// <summary>Tray notifications; <see cref="Notifier.Show"/> is safe from any thread.</summary>
    public Notifier Notifier { get; }

    /// <summary>Shows <see cref="PinDialog"/> on the UI thread; usable as <c>askPin</c> for <see cref="TransferClient.SendAsync"/>.</summary>
    public Task<string?> AskPinAsync(string deviceAlias) =>
        _dispatcher.InvokeAsync(() => PinDialog.AskAsync(deviceAlias)).Task.Unwrap();

    internal void Update(DiscoveryService discovery, TransferClient transferClient)
    {
        Volatile.Write(ref _discovery, discovery);
        Volatile.Write(ref _transferClient, transferClient);
        Changed?.Invoke();
    }
}
