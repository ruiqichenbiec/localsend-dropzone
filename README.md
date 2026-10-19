# LocalSend Dropzone

> **Unofficial.** LocalSend Dropzone is an unofficial companion to [LocalSend](https://localsend.org). It is not affiliated with or endorsed by the LocalSend project.

Language: English | [简体中文](README.zh-CN.md)

A Windows tray app that runs alongside the official LocalSend app. It does not replace the official app, and it does not change the official app's ports, settings or files.

## What it does

- **Runs alongside the official app.** The official app keeps running in the tray, keeps using UDP/TCP 53317, keeps being discovered by phones, and keeps receiving files from phones with its own confirmation prompt.
- **Corner drag-and-drop sending.** Drag files to a screen corner (bottom-right by default). A drop zone appears; hover over it to pick a device, release to send. No window has to be opened first.
- **Prompt-free receiving between your own PCs.** Give each PC the same shared password. Between PCs that run this program, the receiving PC does not prompt; files go straight into the Downloads folder.
- **Modified time.** For files the official app has received, the modified time is set to the moment reception finished. The created time is unchanged.

## What it does on your machine and network

- **Reads the official app's settings file.** It reads `%APPDATA%\LocalSend\settings.json` and never writes to it. It uses only the alias, port, receive history and the save-to-history flag. The TLS private key stored in that file (`flutter.ls_security_context`) is never extracted, logged or sent. The program reads the file into memory only to find those few values, then zeroes the buffer.
- **Announces and registers on the LAN with the official app's identity.** Only while the official app is running, it uses the official app's alias and certificate fingerprint. It multicasts an announcement every 30 seconds on each IPv4 adapter (224.0.0.167, UDP 53317). Every 45 seconds it sends register requests to hosts seen in the last 7 days and to the devices on the local subnet (/24 or smaller). When the official app is not running, it stops announcing and registering, so phones do not list a PC they cannot send files to.
- **Listens on TCP 53319 and shares UDP 53317.** It listens on TCP 53319 (the private port, configurable in Settings) for transfers between PCs that run this program. It shares UDP 53317 with the official app through address reuse; it does not take that port over.
- **Windows Firewall.** At each start it checks for its inbound rules and, if they are missing, adds inbound allow rules for the private port and UDP 53317 on the Private and Public profiles. Adding them needs administrator approval (UAC); the prompt names LocalSendDropzone. It only manages rules with its own name and never deletes other rules.
- **File modified times.** For files the official app has just received, it sets the modified time to the completion time, using the official app's receive history. It changes only the modified time, and it leaves files you have edited since then alone.
- **Start with Windows.** On by default. It adds a per-user Run entry under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. You can turn this off in Settings.
- **Local data.** Everything it writes is under `%LOCALAPPDATA%\localsend-dropzone\` (see "Data and logs"). The shared password is encrypted with Windows DPAPI for your user account.

## Install

1. Put `publish\LocalSendDropzone.exe` in a fixed folder of your choice (for example `%USERPROFILE%\Apps\localsend-dropzone\publish\`) and run it. The firewall rules and the auto-start entry record the exe's path, so if you move the exe, run it once from its new location.
2. Each start checks the firewall inbound rules first; listening begins only after they are in place. On the first run, or after the exe has moved, Windows may show one UAC prompt (the program named in it is LocalSendDropzone). It adds inbound rules that allow the private port and UDP 53317 on Private and Public networks, as the official LocalSend does. If you decline, other PCs may be unable to reach this one, and the program shows a notification.
   - If the Windows Security Alert dialog appears, tick both Private networks and Public networks, then click Allow access.
   - If Windows already has a rule that blocks this program (for example, the dialog was closed with Private unticked), an allow rule will not help. The program notifies you to allow LocalSendDropzone in "Allow an app through Windows Firewall". The program never deletes other firewall rules.
3. Open Settings from the tray icon and fill in on each PC:
   - **Shared password:** at least 12 characters, the same on every PC. It is saved with Windows DPAPI for the current user, so the file contains no plain text.
   - **Private port:** default 53319. It must be the same on every PC, and it must not be 53317 or the port set in the official app's settings.
   - The drop zone position and start-with-Windows (on by default) are also set here.
4. The official LocalSend app must keep running and be set to start with Windows. Each start checks both (when the program starts with Windows, it gives the official app two minutes to launch first). If either is not met, the program only shows a notification; it never changes the official app's settings. The start-with-Windows reminder appears once, when it is first found off or changes from on to off. The program also notifies you once if the official app exits while running, and once if "save to history" is turned off in the official app.

## Use

- **Send:** drag one or more files to the screen corner and the drop zone appears. Hover over it and the device list opens toward the inside of the screen (this PC is not listed). Release on a device to send at once; the result appears as a Windows notification.
  - Another PC running this program: sent over the private port; the receiver accepts automatically.
  - A phone, or a PC running only the official app: the normal official flow, where the recipient confirms in its app. If the recipient requires a PIN, a small box asks you for it.
  - If the other PC's private port answers "wrong password", the send switches to the normal flow. You get a notice ("password mismatch, switched to normal send"), and the recipient's official app prompts.
- **Receive:** after files arrive over the private channel, you get one notification per transfer. For one file it shows the file name (clicking selects it in Explorer); for several, "N files received" (clicking opens the folder).
- **Hand to the official app:** release on the drop zone itself and the files are handed to the running official app window, where you pick a device. If the handover fails, the official window comes to the front and you are asked to drag the files into it.
- **Cancel:** release somewhere else; the drop zone hides.
- **Tray menu:**
  - Settings
  - Open the "Downloads" folder
  - Pause receiving: pauses only the private-channel receiving; the official app is not affected. The pause is not remembered; receiving resumes after an exit or restart.
  - Exit

## Known limitations

- **iPhone:** an iPhone can receive files, and appears in the device list, only while LocalSend is open in the foreground.
- **Phone to PC:** every transfer needs a confirmation on the PC.
- **Logged-off PCs:** when a PC is on but no one is logged in, the private channel cannot receive. The official path is unaffected and still works under its own settings.
- **Modified times:** these depend on the official app's "save to history". If that is turned off in the official app, modified times are not corrected. History keeps only the latest 30 entries; if you receive many files at once, earlier entries may be pushed out before this program processes them. Files you changed after receiving them are left alone.
- **Network:** only works on a local network; no cross-subnet or internet use.
- **System:** Windows only.
- **Language:** the tray menu, the Settings window and the notifications are currently in Chinese.

## Data and logs

The data folder is `%LOCALAPPDATA%\localsend-dropzone\`:

- `settings.json`: settings. The shared password is stored only in DPAPI-encrypted form.
- `cert.bin`: the self-signed certificate for the private channel, also stored encrypted.
- `seen-history.json`, `known-hosts.json`: internal records of this program.
- `logs\app.log`: the log, rolled daily and kept for 14 days. The shared password is never logged, and PIN and token values are masked.

This program only reads the official app's `%APPDATA%\LocalSend\settings.json`; it never writes to it.

## Build

- Requires the .NET 10 SDK.
- Layout: `src/dropzone-core` (core library), `src/dropzone-app` (tray app), `tests/dropzone-core-tests` (tests).
- Design notes and test findings (in Chinese) are in `docs/superpowers/specs/` and `docs/notes/`.
- Run the tests from the repository root:

  ```
  dotnet test
  ```

- Publish:

  ```
  dotnet publish src/dropzone-app -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
  ```

  The output is `publish\LocalSendDropzone.exe`. `publish\` is listed in `.gitignore`.

## License

Apache License 2.0. See [LICENSE](LICENSE), [NOTICE](NOTICE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
