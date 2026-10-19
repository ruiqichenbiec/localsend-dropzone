using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Dropzone.App.Drag;
using Dropzone.App.SystemIntegration;
using Dropzone.App.Tray;
using Dropzone.Core.Discovery;
using Dropzone.Core.Files;
using Dropzone.Core.Firewall;
using Dropzone.Core.Original;
using Dropzone.Core.Protocol;
using Dropzone.Core.Receive;
using Dropzone.Core.Send;
using Dropzone.Core.Settings;

namespace Dropzone.App;

/// <summary>
/// Composition root: single instance, settings, first-run checks, the dedicated receive server, the history time
/// fixer, discovery and the tray icon. All fields are changed on the UI thread only; background code reads the
/// settings and the official app's identity through <see cref="Volatile"/>.
/// </summary>
public partial class App : Application
{
    private const string MutexName = @"Local\localsend-dropzone";
    private const string NotificationTitle = "LocalSend Dropzone";
    private const string OriginalNotRunningText = "原版 LocalSend 没在运行，手机发来的文件会收不到";
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>After a start with Windows, how long the official app gets to start before "not running" is shown.</summary>
    private static readonly TimeSpan OriginalStartGrace = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan OriginalStartPoll = TimeSpan.FromSeconds(5);

    private static readonly string DataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "localsend-dropzone");

    private readonly string _exePath =
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "LocalSendDropzone.exe");

    // A saved private port equal to the one the official app is configured for counts as invalid.
    private readonly SettingsStore _store = new(
        SettingsStore.DefaultPath, () => OriginalSettingsReader.TryRead(OriginalSettingsReader.DefaultPath)?.Port);

    // The serial region for the firewall check, the receive server and the discovery stack (SyncPortAsync).
    private readonly SemaphoreSlim _portGate = new(1, 1);

    // Cancelled on exit but never disposed: background work may still read its token.
    private readonly CancellationTokenSource _stop = new();

    private readonly OriginalIdentityTracker _identity = new();

    private Mutex? _mutex;

    // The official app's settings as last read successfully; written by the identity loop only.
    private OriginalSettings? _originalSettings;
    private int _historyOffNotified;
    private TrayIcon? _tray;
    private Notifier? _notifier;
    private X509Certificate2? _certificate;
    private string? _privateFingerprint;
    private PrivateReceiveServer? _server;
    private int _serverPort;
    private bool _listening;
    private bool _paused;
    private HistoryTimeFixer? _fixer;
    private DeviceRegistry? _registry;
    private KnownHostsStore? _knownHosts;
    private DiscoveryStack? _discovery;
    private SettingsWindow? _settingsWindow;
    private DragWatcher? _dragWatcher;
    private DropZoneWindow? _dropZone;
    private bool _exiting;

    /// <summary>The services the drop zone uses; null until startup has finished.</summary>
    public AppServices? Services { get; private set; }

    /// <summary>Raised on the UI thread once <see cref="Services"/> is set.</summary>
    public event Action<AppServices>? ServicesReady;

    /// <summary>The settings in force. Safe to read from any thread.</summary>
    private AppSettings CurrentSettings => _store.Current;

    /// <summary>
    /// The official app's identity while it answers, else null. Gates everything that tells other devices this PC
    /// can receive: announcing, registering to known hosts and subnets, and answering announcements.
    /// </summary>
    private DeviceInfo? LiveIdentity => _identity.Live;

    /// <summary>The last identity the official app answered with: the <c>info</c> that sends carry.</summary>
    private DeviceInfo? LastKnownIdentity => _identity.LastKnown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The elevated instance FirewallSetup starts: adds the rules and quits, before the single-instance check.
        if (FirewallCommands.TryParseElevatedArguments(e.Args, out int firewallPort))
        {
            Shutdown(FirewallSetup.RunElevated(_exePath, firewallPort));
            return;
        }

        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Error("Unhandled exception.", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Warn("Unobserved task exception.", args.Exception);
            args.SetObserved();
        };

        bool background = e.Args.Contains(AutoStart.BackgroundArgument, StringComparer.OrdinalIgnoreCase);
        AppLog.Info($"Starting {_exePath}{(background ? " (background)" : string.Empty)}.");
        AppLog.Info($"Settings: {_store.Load()}.");

        // The tray icon comes first so that every notification below has an icon to show on.
        _tray = new TrayIcon();
        _notifier = new Notifier(_tray.NotifyIcon, Dispatcher);
        _tray.SettingsRequested += ShowSettings;
        _tray.OpenDownloadsRequested += OpenDownloads;
        _tray.PauseChanged += OnPauseChanged;
        _tray.ExitRequested += OnExitRequested;

        _ = StartAsync(background);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_mutex is null)
        {
            // A second instance, or the elevated firewall instance, quitting at once: nothing was started.
            base.OnExit(e);
            return;
        }

        if (!_exiting)
        {
            // Windows is ending the session, or Shutdown was called elsewhere.
            _exiting = true;
            // Windows are already closed by now; only the mouse hook needs removing.
            _dragWatcher?.Dispose();
            _dragWatcher = null;
            try
            {
                Task.Run(StopServicesAsync).Wait(ExitTimeout);
            }
            catch (AggregateException error)
            {
                AppLog.Warn("Stopping services failed.", error);
            }
        }

        _notifier?.Dispose();
        _tray?.Dispose();
        if (_mutex is not null)
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }

        AppLog.Info("Exited.");
        base.OnExit(e);
    }

    /// <summary>
    /// Startup after the tray icon: checks on the official app (in the background), certificate, history time fixer,
    /// firewall rules, then the receive server and discovery.
    /// </summary>
    private async Task StartAsync(bool background)
    {
        try
        {
            Task firstIdentity = StartIdentityLoop();

            bool firstRun = !CurrentSettings.FirstRunDone;
            Task originalChecks = Task.Run(() => RunOriginalChecksAsync(background));

            ApplyAutoStart(CurrentSettings.AutoStart);

            string certificatePath = Path.Combine(DataDirectory, "cert.bin");
            _certificate = await Task.Run(() => CertificateStore.LoadOrCreate(certificatePath));
            _privateFingerprint = CertificateStore.Fingerprint(_certificate);

            _fixer = new HistoryTimeFixer(
                OriginalSettingsReader.DefaultPath,
                new SeenHistoryStore(Path.Combine(DataDirectory, "seen-history.json")));
            _fixer.FileFixed += path => AppLog.Info($"Set the modification time of {path}.");
            await Task.Run(_fixer.Start);

            // The firewall check, then the receive server.
            await SyncPortAsync();
            if (_exiting)
            {
                return;
            }

            _registry = new DeviceRegistry(OwnFingerprints);
            _knownHosts = new KnownHostsStore(Path.Combine(DataDirectory, "known-hosts.json"));

            // Scans and announcements need the official app's identity; give the first lookup its 2 seconds.
            await firstIdentity;
            if (_exiting)
            {
                return;
            }

            await StartDiscoveryAsync();
            if (_exiting || _discovery is not { } discovery)
            {
                return;
            }

            Services = new AppServices(
                _registry, discovery.Service, discovery.Client, () => _store.Current, _notifier!, Dispatcher);
            ServicesReady?.Invoke(Services);
            StartDropZone(Services);
            AppLog.Info("Started.");

            if (firstRun)
            {
                if (string.IsNullOrWhiteSpace(CurrentSettings.Password))
                {
                    ShowSettings();
                }

                await originalChecks;

                // Only this one field changes, applied to whatever is current now (the settings window may have
                // saved a password meanwhile).
                await Dispatcher.InvokeAsync(() => UpdateSettings(s => s with { FirstRunDone = true }));
            }
        }
        catch (Exception error)
        {
            AppLog.Error("Startup failed.", error);
            Notify("启动失败，详情见日志");
        }
    }

    /// <summary>
    /// Installs the global drag watcher and creates the (hidden) drop-zone window. A failure is logged and leaves the
    /// rest of the app running.
    /// </summary>
    private void StartDropZone(AppServices services)
    {
        try
        {
            _dropZone = new DropZoneWindow(services);
            _dragWatcher = new DragWatcher(Dispatcher, () => services.Settings.Corner);
            _dragWatcher.NearCorner += _dropZone.ShowAt;
            _dragWatcher.Released += _dropZone.OnReleased;
        }
        catch (Exception error)
        {
            AppLog.Error("Starting the drop zone failed.", error);
            StopDropZone();
            Notify("发送区无法启动，详情见日志");
        }
    }

    /// <summary>Removes the mouse hook and closes the drop-zone window. UI thread.</summary>
    private void StopDropZone()
    {
        _dragWatcher?.Dispose();
        _dragWatcher = null;
        _dropZone?.Close();
        _dropZone = null;
    }

    /// <summary>
    /// Checks on the official app at every start: running, and starting with Windows. Runs off the UI thread; the only
    /// setting it changes (on the UI thread) is <see cref="AppSettings.OriginalAutoStart"/>. After a start with Windows
    /// the official app may still be on its way, so "not running" is only reported when it has not shown up within
    /// 2 minutes. The start-with-Windows hint is shown only when that state turns off, not at every start.
    /// </summary>
    private async Task RunOriginalChecksAsync(bool background)
    {
        try
        {
            bool running = OriginalChecks.IsRunning();
            if (!running && background)
            {
                DateTime deadline = DateTime.UtcNow + OriginalStartGrace;
                while (!running && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(OriginalStartPoll, _stop.Token);
                    running = OriginalChecks.IsRunning() || LiveIdentity is not null;
                }
            }

            AppLog.Info($"Official app running: {(running ? "yes" : "no")}.");
            if (!running)
            {
                Notify(OriginalNotRunningText);
            }

            bool autoStart = OriginalChecks.StartsWithWindows();
            bool? lastSeen = CurrentSettings.OriginalAutoStart;
            AppLog.Info($"Official app starts with Windows: {(autoStart ? "yes" : "no")}.");
            if (OriginalAutoStartHint.ShouldShow(lastSeen, autoStart))
            {
                Notify("建议在原版设置里打开开机启动");
            }

            if (lastSeen != autoStart)
            {
                await Dispatcher.InvokeAsync(() => UpdateSettings(s => s with { OriginalAutoStart = autoStart }));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            AppLog.Warn("Checking the official app failed.", error);
        }
    }

    private async Task EnsureFirewallAsync(int port)
    {
        try
        {
            FirewallResult result = await FirewallSetup.EnsureAsync(_exePath, port);
            AppLog.Info($"Firewall rules for port {port}: {result}.");
            if (result is FirewallResult.Blocked)
            {
                Notify("Windows 防火墙阻止了本程序，请在“允许应用通过防火墙”里放行 LocalSendDropzone");
            }
            else if (result is FirewallResult.Declined or FirewallResult.TimedOut or FirewallResult.Failed)
            {
                Notify("没有添加防火墙规则，其他电脑可能连不上");
            }
        }
        catch (Exception error)
        {
            AppLog.Warn("Firewall check failed.", error);
        }
    }

    /// <summary>
    /// Looks up the official app's identity off the UI thread: every 5 seconds until it answers (and after any failed
    /// lookup), then every 60 seconds (<see cref="OriginalIdentityTracker.NextDelay"/>). A failing round is logged and
    /// the loop goes on; only exiting ends it. The returned task completes after the first attempt.
    /// </summary>
    private Task StartIdentityLoop()
    {
        TaskCompletionSource first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken ct = _stop.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        await RefreshIdentityAsync(ct);
                    }
                    catch (Exception error) when (!ct.IsCancellationRequested)
                    {
                        AppLog.Warn("Looking up the official app's identity failed.", error);
                    }

                    first.TrySetResult();
                    await Task.Delay(_identity.NextDelay, ct);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                first.TrySetResult();
            }
        });
        return first.Task;
    }

    private async Task RefreshIdentityAsync(CancellationToken ct)
    {
        OriginalSettings? read = OriginalSettingsReader.TryRead(OriginalSettingsReader.DefaultPath);
        if (read is not null)
        {
            Volatile.Write(ref _originalSettings, read);
            CheckSaveToHistory(read);
        }

        // Unreadable for a moment (the official app is writing it): the last good copy still says where to look.
        OriginalSettings? original = read ?? Volatile.Read(ref _originalSettings);
        DeviceInfo? identity = original is null ? null : await OriginalIdentity.ResolveAsync(original, ct);

        // A lookup cut short by exiting says nothing about the official app.
        ct.ThrowIfCancellationRequested();
        switch (_identity.Record(identity))
        {
            case IdentityChange.Found:
            case IdentityChange.Changed:
                AppLog.Info($"Official app identity: alias \"{identity!.Alias}\", port {identity.Port}.");
                break;
            case IdentityChange.Lost:
                AppLog.Warn("The official app stopped answering: this PC is no longer announced or registered.");
                Notify(OriginalNotRunningText);
                break;
        }
    }

    /// <summary>Once per run: tells the user when the official app's "save to history" switch is off.</summary>
    private void CheckSaveToHistory(OriginalSettings original)
    {
        if (!original.SaveToHistory && Interlocked.Exchange(ref _historyOffNotified, 1) == 0)
        {
            AppLog.Warn("The official app's \"save to history\" is off: modification times cannot be fixed.");
            Notify("原版 LocalSend 关闭了“保存到历史”，收到文件的修改时间不会被修正");
        }
    }

    /// <summary>The port the official app is configured for, when known; the private port must not be it.</summary>
    private int? OriginalPort() => Volatile.Read(ref _originalSettings)?.Port ?? LastKnownIdentity?.Port;

    /// <summary>The official app's fingerprint (uppercase) and this program's private one (lowercase).</summary>
    private IReadOnlySet<string> OwnFingerprints()
    {
        HashSet<string> own = new(StringComparer.OrdinalIgnoreCase);
        if (LastKnownIdentity?.Fingerprint is { Length: > 0 } original)
        {
            own.Add(original.ToUpperInvariant());
        }

        if (Volatile.Read(ref _privateFingerprint) is { } mine)
        {
            own.Add(mine.ToLowerInvariant());
        }

        return own;
    }

    private PrivateReceiveServer CreateServer(int port)
    {
        PrivateReceiveServer server = new(new PrivateServerOptions(
            port,
            _certificate!,
            () => CurrentSettings.Password,
            KnownFolders.GetDownloads(),
            () => DateTimeOffset.UtcNow,
            IPAddress.Any));
        server.FileReceived += OnFileReceived;
        server.SessionCompleted += OnSessionCompleted;
        return server;
    }

    /// <summary>Starts listening; false (logged, and the user told) when the port cannot be used.</summary>
    private async Task<bool> StartServerAsync(PrivateReceiveServer server, int port)
    {
        try
        {
            await Task.Run(() => server.StartAsync(_stop.Token));
            AppLog.Info($"Receive server listening on port {port}.");
            return true;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            AppLog.Error($"Receive server did not start on port {port}.", error);
            Notify($"专用端口 {port} 无法使用，请在设置里换一个");
            return false;
        }
    }

    /// <summary>
    /// The one serial region for everything that depends on the private port: the firewall check, the receive server
    /// and the discovery stack. Brings them in line with the current port and pause state. Startup, a port change and
    /// pause/resume all come through here one at a time, so nothing listens on a port before the firewall check for
    /// that port has finished, even when the port is changed while the startup check is still waiting for UAC.
    /// UI thread. Exiting does not wait for it (a UAC prompt may be up); it stops at the next step instead.
    /// </summary>
    private async Task SyncPortAsync()
    {
        await _portGate.WaitAsync();
        try
        {
            // Before startup has a certificate there is no server to make; startup comes here itself afterwards.
            if (_exiting || _certificate is null)
            {
                return;
            }

            int port = CurrentSettings.PrivatePort;
            if (_server is null || port != _serverPort)
            {
                if (_server is not null)
                {
                    AppLog.Info($"Private port changed to {port}.");
                }

                // Every start and every new port, before anything listens on it: if the server or the multicast
                // listener were up first, Windows would show its own firewall prompt, whose default answer leaves
                // block rules behind. This also upgrades old private-only rules and follows a moved exe.
                await Task.Run(() => EnsureFirewallAsync(port));
                if (_exiting)
                {
                    return;
                }

                PrivateReceiveServer? old = _server;
                _server = CreateServer(port);
                _serverPort = port;
                _listening = false;
                if (old is not null)
                {
                    await old.DisposeAsync();
                }

                if (_discovery is not null && !_exiting)
                {
                    _discovery.Dispose();
                    DiscoveryStack fresh = BuildDiscovery(port);
                    _discovery = fresh;
                    await Task.Run(fresh.Service.Start);
                }

                if (_exiting)
                {
                    return;
                }
            }

            if (_paused && _listening)
            {
                await _server.StopAsync();
                _listening = false;
            }
            else if (!_paused && !_listening)
            {
                _listening = await StartServerAsync(_server, _serverPort);
            }
        }
        finally
        {
            _portGate.Release();
        }
    }

    /// <summary>
    /// Builds and starts the discovery stack for the port the server was set up on, inside the port gate so a port
    /// change cannot rebuild it halfway. UI thread.
    /// </summary>
    private async Task StartDiscoveryAsync()
    {
        await _portGate.WaitAsync();
        try
        {
            if (_exiting || _server is null)
            {
                return;
            }

            DiscoveryStack discovery = BuildDiscovery(_serverPort);
            _discovery = discovery;
            await Task.Run(discovery.Service.Start);
        }
        finally
        {
            _portGate.Release();
        }
    }

    private DiscoveryStack BuildDiscovery(int privatePort)
    {
        TransferClient client = new(() => LastKnownIdentity, () => CurrentSettings.Password, privatePort);
        SubnetScanner scanner = new(_registry!, client, () => LiveIdentity);
        MulticastListener listener = new(
            _registry!,
            () => LiveIdentity,
            register: scanner.RegisterAsync,
            onError: error => AppLog.Warn("Multicast loop error.", error));
        DiscoveryService service = new(_registry!, listener, scanner, client, _knownHosts);
        return new DiscoveryStack(client, scanner, service);
    }

    private static void OnFileReceived(ReceivedFile file) =>
        AppLog.Info($"Received {Path.GetFileName(file.FullPath)} from {file.SenderAlias}.");

    /// <summary>
    /// One notification per receive session, which also says how many files failed. Clicking it selects the file, or
    /// for several files opens the folder that holds them all.
    /// </summary>
    private void OnSessionCompleted(ReceivedSession session)
    {
        AppLog.Info($"Receive session from {session.SenderAlias} ended with {session.Files.Count} file(s), "
                    + $"{session.FailedCount} failed.");
        string failed = session.FailedCount > 0 ? $"，另有 {session.FailedCount} 个失败" : string.Empty;
        if (session.Files.Count == 1)
        {
            string path = session.Files[0];
            _notifier?.Show("已收到文件", $"{Path.GetFileName(path)}（来自 {session.SenderAlias}）{failed}",
                () => RevealInExplorer(path));
            return;
        }

        string folder = session.CommonDirectory;
        _notifier?.Show("已收到文件", $"已收到 {session.Files.Count} 个文件（来自 {session.SenderAlias}）{failed}",
            () => OpenFolder(folder));
    }

    private void ShowSettings()
    {
        if (_exiting)
        {
            return;
        }

        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(CurrentSettings, ApplySettingsAsync, OriginalPort);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    /// <summary>
    /// Saves and applies the four fields the settings window edits, on top of the current settings (never the copy
    /// the window was opened with). Runs on the UI thread.
    /// </summary>
    private async Task<bool> ApplySettingsAsync(AppSettings requested)
    {
        AppSettings previous = CurrentSettings;
        AppSettings? next = UpdateSettings(s => s with
        {
            Password = requested.Password,
            PrivatePort = requested.PrivatePort,
            Corner = requested.Corner,
            AutoStart = requested.AutoStart,
        });
        if (next is null)
        {
            return false;
        }

        if (next.AutoStart != previous.AutoStart)
        {
            ApplyAutoStart(next.AutoStart);
        }

        if (next.PrivatePort != previous.PrivatePort)
        {
            await SyncPortAsync();
        }

        if (Services is not null && _discovery is not null)
        {
            Services.Update(_discovery.Service, _discovery.Client);
        }

        return true;
    }

    /// <summary>
    /// The only way settings are changed: on the UI thread, <paramref name="change"/> is applied to the current
    /// settings and the result written (<see cref="SettingsStore.Update"/>, under its lock). Nobody writes back a
    /// copy taken earlier. Returns the new settings, or null (logged) when the file could not be written.
    /// </summary>
    private AppSettings? UpdateSettings(Func<AppSettings, AppSettings> change)
    {
        Dispatcher.VerifyAccess();
        try
        {
            AppSettings next = _store.Update(change);
            AppLog.Info($"Settings saved: {next}.");
            return next;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
        {
            AppLog.Error("Saving settings failed.", error);
            return null;
        }
    }

    private void ApplyAutoStart(bool enabled)
    {
        try
        {
            AutoStart.Apply(enabled, _exePath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            AppLog.Warn("Updating start-with-Windows failed.", error);
        }
    }

    private async void OnPauseChanged(bool paused)
    {
        _paused = paused;
        AppLog.Info(paused ? "Receiving paused." : "Receiving resumed.");
        try
        {
            // Applies whatever _paused is by the time the port gate is free.
            await SyncPortAsync();
        }
        catch (Exception error)
        {
            AppLog.Warn("Pausing or resuming failed.", error);
        }
    }

    private async void OnExitRequested()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _settingsWindow?.Close();
        StopDropZone();
        await StopServicesAsync();
        Shutdown();
    }

    /// <summary>Stops discovery, the time fixer and the server, then disposes the certificate.</summary>
    private async Task StopServicesAsync()
    {
        _stop.Cancel();
        try
        {
            _discovery?.Dispose();
            _discovery = null;
            _fixer?.Dispose();
            _fixer = null;
            if (_server is not null)
            {
                await _server.DisposeAsync().ConfigureAwait(false);
                _server = null;
            }

            _certificate?.Dispose();
            _certificate = null;
        }
        catch (Exception error)
        {
            AppLog.Warn("Stopping services failed.", error);
        }
    }

    private void Notify(string text) => _notifier?.Show(NotificationTitle, text);

    private static void OpenDownloads() => OpenFolder(KnownFolders.GetDownloads());

    private static void OpenFolder(string folder) => StartShell("explorer.exe", $"\"{folder}\"");

    private static void RevealInExplorer(string path) => StartShell("explorer.exe", $"/select,\"{path}\"");

    private static void StartShell(string fileName, string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo(fileName, arguments) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Warn($"Could not run {fileName}.", error);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("Unhandled UI exception.", e.Exception);
        e.Handled = true;
    }

    /// <summary>The parts that depend on the private port: rebuilt together when it changes.</summary>
    private sealed record DiscoveryStack(TransferClient Client, SubnetScanner Scanner, DiscoveryService Service) : IDisposable
    {
        /// <summary>The service disposes the multicast listener it owns; the scanner is disposed here.</summary>
        public void Dispose()
        {
            Service.Dispose();
            Scanner.Dispose();
        }
    }
}
