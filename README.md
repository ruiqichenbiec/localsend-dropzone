# LocalSend Dropzone

LocalSend Dropzone is an app that extends and enhances the LocalSend desktop app, offering a way to send with LocalSend that is triggered by drag and drop. It is a Windows tray program that runs alongside the official [LocalSend](https://localsend.org) app; it neither replaces nor modifies the official app. Device discovery and file transfer follow the LocalSend protocol, so phones and other devices need no additional software.

> Unofficial. Not affiliated with or endorsed by the LocalSend project. Dropzone requires the official LocalSend app to be installed.

Language: English | [简体中文](README.zh-CN.md)

## Features

Dropzone provides three features.

### 1. Drag-and-drop sending

Dropzone detects drag operations through a global mouse hook. When the cursor of a drag enters the trigger area in a screen corner (bottom-right by default, above the taskbar), the **drop zone** appears. While the cursor is over the drop zone, a **device list** opens beside it, showing the devices discovered on the local network (this PC excluded).

```
                          ┌──────────────┐
                          │ Pixel 8      │   ← device list
                          │ My laptop    │  ┌───────────┐
                          │ Office PC    │  │ LocalSend │  ← drop zone
                          └──────────────┘  └───────────┘
═══════════════════════════════════════════════════════════ taskbar
```

Where the mouse button is released determines the action:

| Released on | Result |
|---|---|
| A device in the device list | The files are sent to that device |
| The drop zone itself | The official LocalSend window opens with the files already selected |
| Anywhere else | Cancelled |

When no drag is in progress, nothing is shown on screen and no mouse clicks are intercepted. The outcome of each send (sent, declined, failed) is shown as a Windows notification.

### 2. Prompt-free receiving between your own PCs

Between Windows PCs that run Dropzone with the same **shared password**, the receiving PC shows no confirmation prompt; files are written directly to the Downloads folder.

This works over a **private channel** that is separate from the official LocalSend app: on each PC, Dropzone runs an HTTPS receiver on a private port (TCP 53319 by default). Every transfer carries a code derived from the shared password, and the receiver accepts the files only if the code verifies. A code is valid for ±2 minutes; the password itself never crosses the network.

If verification fails (for example, because the two PCs have different passwords), the sender automatically falls back to the regular LocalSend flow, and the receiver's official app asks for confirmation.

### 3. Receive-time correction

For files received by the official LocalSend app, the "date modified" is set to the moment the transfer completed, so newly received files come first when sorted by date. The date created and the file contents are unchanged; files modified after they were received are left alone.

The completion time is taken from the official app's receive history, so this feature depends on the official app's "save to history" option (on by default).

## Division of work with the official app

| Direction | Sender | Receiver | Confirmation |
|---|---|---|---|
| PC → phone | Dropzone | LocalSend on the phone | On the phone |
| PC → PC with only the official app | Dropzone | The official LocalSend on that PC | On that PC |
| PC → PC with Dropzone and the same password | Dropzone | Dropzone on that PC | None |
| Phone → PC | LocalSend on the phone | The official LocalSend on this PC | On this PC |

Dropzone presents itself to other devices with the name and certificate fingerprint of this PC's official LocalSend app, so this PC appears as a single entry on other devices. While the official app is not running, Dropzone stops announcing, so other devices do not list a PC that cannot receive files.

## Requirements

- Windows 11, 64-bit.
- The official LocalSend app, installed and running. Files sent from phones to the PC are received by the official app, so it should be set to start with Windows and stay in the tray.
- All devices on the same local network.

## Installation and setup

1. **Get the program.** Download `LocalSendDropzone.exe` from [Releases](https://github.com/ruiqichenbiec/localsend-dropzone/releases), or build it from source (see [Build from source](#build-from-source)). The program is a single executable with the .NET runtime included; no installation is needed. It is not code-signed, so Windows SmartScreen may show a warning on first run; choose **More info → Run anyway**.
2. **Location.** Run the program from a permanent folder. The firewall rules and the start-with-Windows entry record the program's path; after moving the program, running it once from the new location updates them.
3. **Firewall.** The private channel and device discovery must accept connections from the local network, so Dropzone checks the Windows Firewall rules at every start. On first run, two prompts may appear:
   - Windows' own "Windows Security Alert": tick both **Private networks** and **Public networks** and allow access;
   - Dropzone's request for administrator approval (UAC) to add its own firewall rules: once approved, the rules apply to both Private and Public networks.
4. **Shared password.** Set it under **设置 (Settings)** in the tray menu; it must be at least 12 characters. All PCs that should exchange files without prompts must use the same password and the same private port (53319 by default).
5. **Other PCs.** Repeat these steps on each Windows PC that should take part in prompt-free transfers.

Dropzone starts with Windows by default; this can be turned off in Settings.

## Operation reference

| Task | How |
|---|---|
| Send files or folders | Drag toward the screen corner, hover over the drop zone, release on a device |
| Choose the device in the official LocalSend window | Release on the drop zone itself |
| Send to an iPhone | Open LocalSend on the iPhone first; iOS lets LocalSend receive only while it is in the foreground |
| Enter a PIN required by the receiver | Dropzone shows an input box during the send |
| Pause private-channel receiving | Tray menu → **暂停接收 (Pause receiving)**; resumes on the next start; the official app is not affected |
| Change the screen corner | Tray menu → **设置 (Settings)** |
| Open the Downloads folder | Tray menu → **打开“下载”文件夹 (Open Downloads)** |

For each transfer received over the private channel, one notification is shown: for a single file it shows the file name, and clicking it selects the file in Explorer; for several files it shows the count, and clicking it opens their folder. Files with a name that already exists are renamed to "name (1).ext"; existing files are never overwritten.

## Troubleshooting

| Symptom | Cause | Resolution |
|---|---|---|
| A device is missing from the device list | LocalSend is not open on that device, or it is on another network | Open LocalSend on the device and check its network; the list refreshes when a drag starts |
| Phones do not show this PC | The official LocalSend app on this PC is not running | Start the official app; Dropzone shows a notification about this |
| Another of your PCs still asks for confirmation | Dropzone is not running there, the passwords differ, or the two clocks differ by more than 2 minutes | Check Dropzone and the password on that PC; a password mismatch is reported as "密码不一致，已改为普通发送" |
| Notification "Windows 防火墙阻止了本程序…" (Windows Firewall is blocking this app) | Windows has a rule that blocks Dropzone, usually created when the firewall dialog was closed without allowing it; block rules take precedence over allow rules | In Control Panel → Windows Defender Firewall → **Allow an app through Windows Firewall**, tick Private and Public for LocalSendDropzone |
| Receive times are not corrected | "Save to history" is off in the official app | Turn it back on in the official app; Dropzone reports this once |

## Limitations

- Works only within one local network; no transfers across networks or over the internet.
- Transfers from phones to the PC always need confirmation on the PC; prompt-free receiving applies only between PCs that run Dropzone.
- While the receiving PC is on but nobody is logged in, the private channel does not receive files.
- The official app's receive history keeps only the latest 30 entries; when many files arrive at once, earlier entries may be dropped before they are corrected.
- The tray menu, Settings window and notifications are currently in Chinese only.

## Effects on your system and network

Dropzone does not change the official LocalSend app's settings, ports or files. Its complete footprint is:

- **Reads the official app's settings file** `%APPDATA%\LocalSend\settings.json`, using only the name, port, receive history and the "save to history" option. It never writes to the file and never extracts, logs or sends the private key stored in it.
- **LAN announcements and registration:** while the official app is running, it sends a multicast announcement (UDP 53317) with the official app's identity every 30 seconds and periodically contacts devices seen in the last 7 days.
- **Ports:** listens on TCP 53319 (the private port, configurable); shares UDP 53317 with the official app without taking it over.
- **Firewall:** with the user's approval, adds inbound allow rules for itself (Private and Public networks); it manages only rules under its own name and never deletes other rules.
- **Files:** changes only the "date modified" of files the official app has just received; files received over the private channel are written to the Downloads folder.
- **Start with Windows:** adds an entry under the current user's `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.
- **Local data:** everything is stored under `%LOCALAPPDATA%\localsend-dropzone\`: settings (the shared password encrypted with Windows DPAPI), the private channel's certificate, the list of recently seen devices, and logs (`logs\app.log`, kept for 14 days, never containing passwords or codes).

## Build from source

Requires the .NET 10 SDK.

```
dotnet test
dotnet publish src/dropzone-app -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

The output is `publish\LocalSendDropzone.exe`.

Code layout:

| Directory | Contents |
|---|---|
| `src/dropzone-core` | Core logic: protocol, sending, private-channel receiving, device discovery, time correction |
| `src/dropzone-app` | Tray app, Settings window, drop zone |
| `tests/dropzone-core-tests` | Tests for the core logic |
| `docs/` | Design notes and LocalSend protocol findings (in Chinese) |

## License

Apache License 2.0. See [LICENSE](LICENSE), [NOTICE](NOTICE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
