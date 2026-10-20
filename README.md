# LocalSend Dropzone

**Drag a file to the corner of your screen, drop it on a phone or PC, done.**
A small Windows tray app that adds drag-and-drop sending to [LocalSend](https://localsend.org).

> Unofficial. Not affiliated with or endorsed by the LocalSend project. You still need the official LocalSend app.

Language: English | [简体中文](README.zh-CN.md)

## What you get

- **Send without opening anything.** Drag files toward the bottom-right corner of the screen, pick a device, release. No need to find and open LocalSend first.
- **No "Accept?" prompts between your own PCs.** Install Dropzone on each of your Windows PCs and give them the same password. Files sent between them go straight into Downloads.
- **Received files show when they arrived.** Files that LocalSend receives get the arrival time as their "date modified", so sorting Downloads by date puts the new ones on top.

Phones keep using the normal LocalSend app. Nothing changes for them.

## How it looks

```
                          ┌──────────────┐
                          │ Pixel 8      │
                          │ My laptop    │  ┌───────────┐
                          │ Office PC    │  │ LocalSend │
                          └──────────────┘  └───────────┘
═══════════════════════════════════════════════════════════ taskbar
```

1. Drag one or more files toward the bottom-right corner. The **LocalSend** box appears. (Nothing is shown at other times.)
2. Hover over the box. The devices on your network pop out next to it.
3. Release on a device to send to it. Release on the box itself to open LocalSend with the files already selected. Release anywhere else to cancel.

The result (sent, declined, failed) appears as a Windows notification.

## Requirements

- Windows 11, 64-bit.
- The official [LocalSend](https://localsend.org) app, installed and running. It still does the receiving from phones, so turn on its "start with Windows" option and keep it running in the tray.
- All devices on the same local network.

## Install

1. Download `LocalSendDropzone.exe` from [Releases](https://github.com/ruiqichenbiec/localsend-dropzone/releases), or build it yourself (see [Build from source](#build-from-source)).
   - The exe is not code-signed, so Windows SmartScreen may warn you. Click **More info → Run anyway**.
2. Put it in a folder where it can stay, for example `%USERPROFILE%\Apps\LocalSend Dropzone\`, and run it. A tray icon appears.
   - Moving the exe later is fine; just run it once from the new place.
3. Let it through Windows Firewall. Other PCs and phones need to reach it.
   - If Windows shows "Windows Security Alert", tick **both** Private and Public networks and click **Allow access**.
   - Dropzone may also ask for administrator approval (UAC) once to add its own firewall rules. Click **Yes**.
4. Right-click the tray icon → **设置 (Settings)** and set a **shared password** (at least 12 characters).
5. Repeat on your other Windows PCs with the **same password**.

That's it. Start with Windows is on by default.

## Everyday use

| You want to… | Do this |
|---|---|
| Send to a phone or PC | Drag files to the corner, hover the box, release on the device. |
| Choose the device in LocalSend's own window | Release on the box itself. |
| Send to an iPhone | Open LocalSend on the iPhone first. iOS only lets it receive while it is open. |
| Receive from your own PCs | Nothing to do. Files land in Downloads and you get one notification. |
| Receive from a phone | As before: accept in the LocalSend window on the PC. |
| Stop auto-receiving for a while | Tray menu → **暂停接收 (Pause receiving)**. It resumes on the next start. |
| Move the box to another corner | Tray menu → **设置 (Settings)** → corner. |

If a phone asks for a PIN, Dropzone shows a small box for it.

## Troubleshooting

**A device is missing from the list.**
Open LocalSend on that device and make sure it is on the same Wi-Fi. Wait a few seconds; the list refreshes when you start dragging.

**My phone can't see this PC.**
The official LocalSend app on this PC must be running. Dropzone shows a notification when it is not.

**My other PC still asks me to accept.**
Check that Dropzone is running on both PCs and that the passwords match. When they don't, you get a "密码不一致" (password mismatch) notification and the file is sent the normal way instead. The clocks of the two PCs must also be within 2 minutes of each other.

**"Windows 防火墙阻止了本程序…" (Windows Firewall is blocking this app).**
Windows has a rule that blocks Dropzone, usually because the firewall dialog was closed without allowing it. Open Control Panel → Windows Defender Firewall → **Allow an app through Windows Firewall**, find LocalSendDropzone, and tick Private and Public.

## Good to know

- **Only works inside your local network.** No internet or cross-network transfers.
- **Phone → PC always needs a click on the PC.** Auto-receive works only between PCs that run Dropzone.
- **Auto-receive needs someone logged in** on the receiving PC.
- **Arrival times rely on LocalSend's history.** If you turn off "save to history" in LocalSend, Dropzone can't fix the times (it tells you once). Files you edit after receiving them are left alone.
- **The menus and notifications are in Chinese** for now.

## What it does behind the scenes

Dropzone never changes LocalSend's settings, ports or files. On your PC and network it:

- **Reads LocalSend's settings file** (`%APPDATA%\LocalSend\settings.json`) to learn this PC's LocalSend name, port and receive history. It never writes to it, and never extracts, logs or sends the private key stored in it.
- **Introduces itself as this PC's LocalSend.** When it sends, and every 30 seconds while LocalSend is running, it uses LocalSend's own name and certificate fingerprint, so other devices still see one entry for this PC, not two. It also contacts devices it has seen in the last 7 days to keep the list fresh.
- **Listens on TCP port 53319** for files from your other PCs. Each transfer must carry a code derived from the shared password; the password itself never goes over the network. The port can be changed in Settings (it must be the same on all your PCs).
- **Shares UDP port 53317** with LocalSend to hear devices announce themselves. It does not take over LocalSend's ports.
- **Adds Windows Firewall rules** for itself (Private and Public networks), with your approval.
- **Changes only the "date modified"** of files LocalSend has just received.
- **Adds a start-with-Windows entry** for your user account (can be turned off in Settings).
- **Keeps its data in `%LOCALAPPDATA%\localsend-dropzone\`**: settings (password encrypted with Windows DPAPI), its own certificate, a list of recently seen devices, and logs (`logs\app.log`, kept 14 days, no passwords or codes in them).

## Build from source

Requires the .NET 10 SDK.

```
dotnet test
dotnet publish src/dropzone-app -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

The result is `publish\LocalSendDropzone.exe` (a single file with the .NET runtime included).

Code layout: `src/dropzone-core` (logic, fully tested), `src/dropzone-app` (tray app and drop zone), `tests/dropzone-core-tests`. Design notes and protocol findings (in Chinese) are in `docs/`.

## License

Apache License 2.0. See [LICENSE](LICENSE), [NOTICE](NOTICE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
