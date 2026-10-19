# LocalSend Dropzone 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 做一个和原版 LocalSend 并排运行的 Windows 托盘程序：屏幕角落拖放发送、自己几台电脑之间凭共享密码自动接收、把原版收到的文件的修改时间改成接收时间。

**Architecture:** 三个工程。`dropzone-core` 是不含界面的类库，放协议模型、密码令牌、安全路径、原版配置读取、专用接收服务器（Kestrel）、发送客户端、设备发现，全部有自动化测试。`dropzone-app` 是 WPF 托盘程序，负责托盘、设置窗口、角落检测、发送区窗口，靠用户目测验收。本程序发送时冒用原版的身份（别名、指纹、端口），所以手机上不会多出一个条目；接收只走自己的专用端口，原版的 53317 一点不碰。

**Tech Stack:** C# / .NET 10（`net10.0-windows`），ASP.NET Core Kestrel（专用端口 HTTPS），WPF + WinForms `NotifyIcon`（托盘和通知），xUnit，`System.Security.Cryptography.ProtectedData`（DPAPI）。

**Spec:** `docs/superpowers/specs/2026-10-19-localsend-dropzone-design.md`（执行者必须先读完）。

## Global Constraints

- 只支持 Windows 11；所有工程 `TargetFramework` 为 `net10.0-windows`；发布产物是一个自包含单文件 `LocalSendDropzone.exe`。
- 原版不动：不写 `%APPDATA%\LocalSend\` 下的任何文件，不改原版设置，不停原版进程，不绑定 TCP 53317。
- UDP 53317 只能共享绑定（`ExclusiveAddressUse = false`、`ReuseAddress = true`），组播组 `224.0.0.167`。
- 专用端口默认 `53319`，可在设置里改，所有电脑必须一致；不做组播公告。
- 不能让这台电脑在别的设备上多出条目：本程序从不以自己的身份 announce 或 register，发给普通设备的 `info` 一律用原版的 `alias`、`fingerprint`、`port`、`protocol`。
- 密码令牌：`base64url(HMAC-SHA256(key=PBKDF2-HMAC-SHA256(UTF8(密码), salt=UTF8("localsend-dropzone/v1"), 600000 次迭代, 32 字节), data=UTF8("lsdz1|" + 接收方指纹小写十六进制 + "|" + Unix分钟数)) 的前 16 字节)`，接收方接受前后 2 分钟；令牌只发往专用端口，只放在 `pin` 查询参数里。
- 密码用 DPAPI（`DataProtectionScope.CurrentUser`）加密后存盘，不进日志，不进任何 URL 以外的明文位置（URL 里只有令牌）。
- 接收目录取 Windows 已知文件夹 `FOLDERID_Downloads`（`{374DE290-123F-4565-9164-39C4925E467B}`），不写死路径。
- 同名改名格式 `名字 (n).扩展名`，从 1 开始。
- 只改修改时间（`LastWriteTimeUtc`），值为接收完成时刻；创建时间不动；只碰刚收到的文件。
- 防火墙规则对“专用”和“公用”配置文件都生效（用户 2026-10-19 决定，和原版一样）；从不修改网络类别。
- 发送区：平时没有常驻窗口，不拦截点击，位置在显示器工作区（任务栏以内）的角上。
- 目录名小写加连字符；界面文字用中文，文案以本计划为准。
- 程序自己的数据放 `%LOCALAPPDATA%\localsend-dropzone\`（`settings.json`、`seen-history.json`、`cert.bin`、`logs\`）。

## Review Focus

1. 发送方给的 `fileName` 不安全（`../`、绝对路径、`CON` 之类的保留名、非法字符）：文件必须仍落在“下载”里，越界的直接 400 → Task 3、Task 7 的测试。
2. 传到一半断开或取消：不能留下带正式文件名的残缺文件，临时文件要删掉 → Task 7 `Upload_AbortedMidway_LeavesNoFile`。
3. 原版的 `settings.json` 正在被写或被锁：读取返回 `null` 而不是崩溃，之后重试，绝不碰旧文件 → Task 5 `TryRead_LockedFile_ReturnsNull`、Task 6 `ProcessOnce_UnreadableSettings_ChangesNothing`。
4. 两台电脑时钟差超过 2 分钟：令牌被拒，自动改走对方原版并通知用户 → Task 2 边界测试、Task 8 `Send_WrongPassword_FallsBackToOriginal`。
5. 列表里的设备在松手前消失了（手机关了 App）：几秒内报“发送失败”，发送区不卡住 → Task 8 `Send_TargetOffline_FailsFast`。

---

## 文件结构

```
localsend-dropzone.sln
src/dropzone-core/                 RootNamespace Dropzone.Core
  Security/PasswordToken.cs        Task 2
  Files/SafePaths.cs               Task 3
  Files/KnownFolders.cs            Task 3
  Protocol/Models.cs               Task 4
  Protocol/LocalSendJson.cs        Task 4
  Original/OriginalSettings.cs     Task 5（读原版 settings.json、接收历史）
  Original/OriginalIdentity.cs     Task 5（原版身份：别名、端口、证书指纹）
  Original/HistoryTimeFixer.cs     Task 6
  Original/SeenHistoryStore.cs     Task 6
  Receive/CertificateStore.cs      Task 7
  Receive/PrivateReceiveServer.cs  Task 7
  Discovery/DiscoveredDevice.cs    Task 8
  Send/FileEnumerator.cs           Task 8
  Send/TransferClient.cs           Task 8
  Discovery/DeviceRegistry.cs      Task 9
  Discovery/MulticastListener.cs   Task 9
  Discovery/SubnetScanner.cs       Task 9
  Discovery/DiscoveryService.cs    Task 9
  Settings/AppSettings.cs          Task 10
  Settings/SettingsStore.cs        Task 10
  Ui/CornerGeometry.cs             Task 11（纯计算，可测）
src/dropzone-app/                  AssemblyName LocalSendDropzone, RootNamespace Dropzone.App
  App.xaml(.cs)、Tray/…、SettingsWindow、PinDialog   Task 10
  Drag/DragWatcher.cs、DropZoneWindow.xaml(.cs)、OriginalLauncher.cs   Task 11
tests/dropzone-core-tests/         xUnit，每个 Core 文件一个 *Tests.cs
docs/notes/2026-10-19-localsend-findings.md   Task 1
README.md                          Task 12
```

---

### Task 1: 装 SDK，验证原版 LocalSend 的实际行为

后面的任务依赖这里查到的几件事。写一个**一次性**控制台小程序做探测，放在仓库外（如 `%TEMP%\lsdz-probe`），不提交；只提交结论文档。

已知（2026-10-19 在本机查到）：原版 1.18.2 进程 `localsend_app.exe` 监听 TCP 和 UDP 53317（`0.0.0.0` 和 `::`）；配置在 `%APPDATA%\LocalSend\settings.json`，键 `flutter.ls_alias`（本机为 `MyPC`）、`flutter.ls_port`（53317）、`flutter.ls_receive_history`（字符串数组，每项是一个 JSON 字符串，字段 `id`、`fileName`、`fileType`、`path`（完整路径，消息为空）、`savedToGallery`、`isMessage`、`fileSize`、`senderAlias`、`timestamp`（UTC ISO-8601），最新的在前）；收到文件时 `settings.json` 会被重写；默认保存到 `%USERPROFILE%\Downloads`；对 127.0.0.1:53317 做 TLS 握手能拿到证书（DER 的 SHA-256 为 `01234567…cdef`），但 `GET /api/localsend/v2/info` 被对方直接断开。`flutter.ls_security_context` 含私钥，**不要读取或输出**。

**Files:**
- Create: `docs/notes/2026-10-19-localsend-findings.md`

- [ ] **Step 1: 装 .NET 10 SDK**

Run: `winget install --id Microsoft.DotNet.SDK.10 -e --accept-source-agreements --accept-package-agreements`，然后 `dotnet --list-sdks`
Expected: 列出 `10.x.x`。

- [ ] **Step 2: F1 原版指纹格式。** 探测程序共享绑定 UDP 53317、加入 `224.0.0.167`，打印收到的公告 JSON。请用户在原版“发送”页点一下刷新。记录原版公告里 `fingerprint` 的写法（大小写、是否等于证书 DER 的 SHA-256 十六进制）、`version`、`deviceModel`、`deviceType`、`protocol`。

- [ ] **Step 3: F2 共享 UDP 是否可行。** Step 2 能收到公告即说明可行；再请用户用手机给这台电脑发一个小文件，确认原版照常弹窗接收（证明没干扰原版）。

- [ ] **Step 4: F3 register 冒用原版身份。** 请用户在手机上打开 LocalSend；探测程序用原版身份（Step 2 记下的字段）向手机 IP 的 53317 发 `POST /api/localsend/v2/register`，打印响应。请用户看手机的设备列表里这台电脑是否**只出现一次**。

- [ ] **Step 5: F4 历史时间戳的含义。** 请用户用手机发一个至少 200 MB 的文件给原版并接受。对比历史记录的 `timestamp`、传输开始时刻、文件最后写入时刻，确定 `timestamp` 是开始还是完成。

- [ ] **Step 6: F5 命令行交文件给正在运行的原版。** 原版在托盘运行时执行 `& "C:\Program Files\LocalSend\localsend_app.exe" "<一个测试文件的完整路径>"`，请用户确认原版窗口是否出现、文件是否已在待发送列表里。

- [ ] **Step 7: F6 文件夹的 `fileName` 格式。** 读 `github.com/localsend/localsend` 源码里发送文件夹时生成 `fileName` 的代码，记录格式（例如 `folder/sub/file.txt`，分隔符是什么）。

- [ ] **Step 8: 写结论文档并判断去留**

把 F1–F6 写进 `docs/notes/2026-10-19-localsend-findings.md`，每条写“结论 + 证据”。去留规则：
- F3 手机上出现两个条目 → **停下**，向用户报告（违反反目标）。
- F4 历史拿不到可用的路径或时间 → **停下**，向用户报告（规格里的退路需要用户手动改原版设置）。
- F4 时间戳是开始时刻 → Task 6 改用“`settings.json` 那次写入的 `LastWriteTimeUtc`”作为完成时刻，在文档里写明。
- F2 不可行 → Task 9 去掉 `MulticastListener`，只靠扫描，在文档里写明。
- F5 不行 → Task 11 用降级方案（已在规格中定好）。

- [ ] **Step 9: Commit**

```bash
git add docs/notes/2026-10-19-localsend-findings.md
git commit -m "docs: record LocalSend 1.18.2 behavior findings"
```

---

### Task 2: 解决方案骨架 + 密码令牌

**Files:**
- Create: `localsend-dropzone.sln`、`src/dropzone-core/dropzone-core.csproj`、`tests/dropzone-core-tests/dropzone-core-tests.csproj`、`.gitignore`（`dotnet new gitignore`）
- Create: `src/dropzone-core/Security/PasswordToken.cs`
- Test: `tests/dropzone-core-tests/PasswordTokenTests.cs`

`dropzone-core.csproj`：`net10.0-windows`，`<RootNamespace>Dropzone.Core</RootNamespace>`，`<AssemblyName>Dropzone.Core</AssemblyName>`，`<Nullable>enable</Nullable>`，`<FrameworkReference Include="Microsoft.AspNetCore.App" />`，包 `System.Security.Cryptography.ProtectedData`。测试工程用 `dotnet new xunit` 生成后改 TFM 为 `net10.0-windows` 并引用 Core。

**Interfaces:**
- Produces: `Dropzone.Core.Security.PasswordToken`
  - `static string Create(string password, string receiverFingerprint, DateTimeOffset now)`
  - `static bool Verify(string? token, string password, string ownFingerprint, DateTimeOffset now)`

- [ ] **Step 1: Write the failing tests**

```csharp
// fp = "AB12…"（64 位十六进制），t0 = 2026-10-19T12:00:30Z
Create_ThenVerify_SameMinute_True
Verify_WrongPassword_False
Verify_DifferentFingerprint_False
Verify_FingerprintCaseInsensitive_True          // Create 用大写，Verify 用小写
Verify_TwoMinutesLater_True                    // now = t0 + 2 min
Verify_ThreeMinutesLater_False                 // now = t0 + 3 min
Verify_ThreeMinutesEarlier_False
Verify_NullOrGarbage_False                     // null、""、"not-base64!!"
Create_DoesNotContainPassword                  // Assert.DoesNotContain("hunter2", token)
Create_IsUrlSafe                               // 只含 [A-Za-z0-9_-]，长度 22
```

- [ ] **Step 2: Run** `dotnet test --filter PasswordTokenTests`，Expected: 编译失败（类型不存在）。

- [ ] **Step 3: 实现 `PasswordToken`。** 公式见 Global Constraints；比较用 `CryptographicOperations.FixedTimeEquals`；分钟数 = `now.ToUnixTimeSeconds() / 60`。

- [ ] **Step 4: Run** `dotnet test`，Expected: 全部 PASS。

- [ ] **Step 5: Commit** `git add -A && git commit -m "feat(core): solution skeleton and password token"`

---

### Task 3: 安全路径、同名改名、“下载”目录

**Files:**
- Create: `src/dropzone-core/Files/SafePaths.cs`、`src/dropzone-core/Files/KnownFolders.cs`
- Test: `tests/dropzone-core-tests/SafePathsTests.cs`

**Interfaces:**
- Produces: `Dropzone.Core.Files.SafePaths`
  - `static string? ToSafeRelativePath(string fileName)` —— 返回用 `\` 连接的相对路径；不安全或为空返回 `null`
  - `static string GetUniquePath(string directory, string relativePath)` —— 返回不存在的完整路径
- Produces: `Dropzone.Core.Files.KnownFolders.GetDownloads() : string`（`SHGetKnownFolderPath` P/Invoke）

- [ ] **Step 1: Write the failing tests**

```csharp
[Theory] ToSafeRelativePath_Cases:
  "报告.pdf"            -> "报告.pdf"
  "a/b.txt"             -> @"a\b.txt"
  @"a\b.txt"            -> @"a\b.txt"
  "a/./b.txt"           -> @"a\b.txt"
  "/etc/passwd"         -> @"etc\passwd"
  @"C:\Windows\x.dll"   -> @"C_\Windows\x.dll"     // 冒号是非法字符，换成 _
  "CON.txt"             -> "_CON.txt"              // 保留名加前缀
  "a/lpt1"              -> @"a\_lpt1"
  "name. "              -> "name"                  // 去掉末尾的点和空格
  "x<y>.txt"            -> "x_y_.txt"
  "../evil.txt"         -> null
  "a/../../evil"        -> null
  "" / "///"            -> null
GetUniquePath_NoConflict_ReturnsSame
GetUniquePath_OneConflict_AppendsOne        // 已有 报告.pdf -> 报告 (1).pdf
GetUniquePath_TwoConflicts_AppendsTwo       // 已有 报告.pdf、报告 (1).pdf -> 报告 (2).pdf
GetUniquePath_NoExtension                   // README -> README (1)
GetUniquePath_Dotfile                       // .gitignore -> .gitignore (1)
GetUniquePath_Nested                        // dir\a.txt 冲突 -> dir\a (1).txt
KnownFolders_GetDownloads_IsExistingDirectory
```

- [ ] **Step 2: Run** `dotnet test --filter SafePathsTests`，Expected: FAIL。
- [ ] **Step 3: 实现两个类。** 保留名：`CON PRN AUX NUL COM1-9 LPT1-9`（不分大小写，比对去掉扩展名后的部分）。
- [ ] **Step 4: Run** `dotnet test`，Expected: PASS。
- [ ] **Step 5: Commit** `git add -A && git commit -m "feat(core): safe relative paths and unique names"`

---

### Task 4: LocalSend 协议模型

**Files:**
- Create: `src/dropzone-core/Protocol/Models.cs`、`src/dropzone-core/Protocol/LocalSendJson.cs`
- Test: `tests/dropzone-core-tests/ProtocolJsonTests.cs`

**Interfaces:**
- Produces（命名空间 `Dropzone.Core.Protocol`，JSON 用 camelCase，忽略未知字段，null 不输出）：
  - `record DeviceInfo(string Alias, string Version, string? DeviceModel, string? DeviceType, string Fingerprint, int Port, string Protocol, bool Download)`
  - `record Announcement(... DeviceInfo 的全部字段 ..., bool Announce)`，提供 `DeviceInfo ToDeviceInfo()`
  - `record FileMetadata(string? Modified, string? Accessed)`
  - `record FileDto(string Id, string FileName, long Size, string FileType, string? Sha256, string? Preview, FileMetadata? Metadata)`
  - `record PrepareUploadRequest(DeviceInfo Info, Dictionary<string, FileDto> Files)`
  - `record PrepareUploadResponse(string SessionId, Dictionary<string, string> Files)`
  - `record HelloResponse(string App, string Version)` —— 专用端口的探测响应，`App` 固定为 `"localsend-dropzone"`
  - `static class LocalSendJson { static JsonSerializerOptions Options { get; } }`
  - `static class FileTypes { static string FromPath(string path) }`（单独文件 `Protocol/FileTypes.cs`）—— 返回 MIME 类型（协议 v2 的 `fileType` 是 MIME），如 `image/jpeg`、`video/mp4`、`application/pdf`、`text/plain`、`application/vnd.android.package-archive`，未知为 `application/octet-stream`

- [ ] **Step 1: Write the failing tests**

```csharp
Deserialize_ProtocolReadmePrepareUploadExample   // 把协议 README 里 prepare-upload 的示例 JSON 原样放进测试，断言 alias、files 数量、size
Deserialize_AnnouncementWithUnknownFields_Ok     // 多一个 "foo": 1 不报错
Serialize_UsesCamelCase_AndOmitsNulls            // 断言含 "fileName"，不含 "sha256"
FileDto_SizeAboveInt32_RoundTrips                // Size = 5_000_000_000
FileTypes_FromPath                               // a.jpg->image/jpeg, a.mp4->video/mp4, a.pdf->application/pdf, a.txt->text/plain, a.zip->application/zip, a.exe->application/octet-stream
```

- [ ] **Step 2: Run** `dotnet test --filter ProtocolJsonTests`，Expected: FAIL。
- [ ] **Step 3: 实现。**
- [ ] **Step 4: Run** `dotnet test`，Expected: PASS。
- [ ] **Step 5: Commit** `git add -A && git commit -m "feat(core): LocalSend protocol models"`

---

### Task 5: 读原版的配置、接收历史和身份

**Files:**
- Create: `src/dropzone-core/Original/OriginalSettings.cs`、`src/dropzone-core/Original/OriginalIdentity.cs`
- Test: `tests/dropzone-core-tests/OriginalSettingsTests.cs`、`tests/dropzone-core-tests/Fixtures/localsend-settings.json`（**自己编的数据**，不要复制用户真实的 settings.json）

**Interfaces:**
- Consumes: `DeviceInfo`（Task 4）
- Produces（命名空间 `Dropzone.Core.Original`）：
  - `record HistoryEntry(string Id, string FileName, string Path, bool IsMessage, long FileSize, string SenderAlias, DateTimeOffset Timestamp)`
  - `record OriginalSettings(string Alias, int Port, IReadOnlyList<HistoryEntry> ReceiveHistory, DateTime FileLastWriteUtc)`
  - `static class OriginalSettingsReader { static string DefaultPath { get; } static OriginalSettings? TryRead(string path); }` —— 用 `FileShare.ReadWrite | FileShare.Delete` 打开；IO 或 JSON 错误返回 `null`；坏的历史项跳过；不读 `flutter.ls_security_context`
  - `static class OriginalIdentity { static Task<DeviceInfo?> ResolveAsync(OriginalSettings settings, CancellationToken ct); }` —— 对 `127.0.0.1:Port` 做 TLS 握手取证书，指纹按 Task 1 F1 的格式；`Version`、`DeviceModel`、`DeviceType`、`Protocol` 按 F1 记下的原版公告取值；握手失败返回 `null`

- [ ] **Step 1: Write the failing tests**

```csharp
TryRead_Fixture_ParsesAliasPortAndHistory    // alias、port、3 条历史，顺序与文件一致
TryRead_MalformedHistoryEntry_IsSkipped      // fixture 里一条是坏 JSON
TryRead_MissingFile_ReturnsNull
TryRead_LockedFile_ReturnsNull               // 测试里先用 FileShare.None 打开
TryRead_TruncatedJson_ReturnsNull            // 模拟写到一半
ResolveAsync_AgainstTestTlsServer_ReturnsFingerprintOfCert   // 测试里起一个 Kestrel HTTPS（自签证书，端口取空闲端口），断言指纹 == 该证书 DER 的 SHA-256（格式按 F1）
ResolveAsync_NothingListening_ReturnsNull
```

- [ ] **Step 2: Run** `dotnet test --filter OriginalSettingsTests`，Expected: FAIL。
- [ ] **Step 3: 实现。**
- [ ] **Step 4: Run** `dotnet test`，Expected: PASS。
- [ ] **Step 5: Commit** `git add -A && git commit -m "feat(core): read original LocalSend settings and identity"`

---

### Task 6: 修正原版收到的文件的修改时间

**Files:**
- Create: `src/dropzone-core/Original/SeenHistoryStore.cs`、`src/dropzone-core/Original/HistoryTimeFixer.cs`
- Test: `tests/dropzone-core-tests/HistoryTimeFixerTests.cs`

**Interfaces:**
- Consumes: `OriginalSettingsReader`、`HistoryEntry`（Task 5）
- Produces（命名空间 `Dropzone.Core.Original`）：
  - `sealed class SeenHistoryStore(string path)`：`bool IsEmpty`、`bool Contains(string id)`、`void Add(string id)`、`void Save()`（JSON 字符串数组，只保留最近 500 个）
  - `sealed class HistoryTimeFixer(string settingsPath, SeenHistoryStore store) : IDisposable`：`int ProcessOnce()`（返回本次改了几个文件）、`void Start()`（`FileSystemWatcher` 盯 `settings.json`，防抖 500 ms 后调 `ProcessOnce`；另有 10 秒一次的兜底定时器）、`event Action<string>? FileFixed`

规则：存储为空（第一次运行）→ 把当前所有 id 记为已处理，一个文件都不改。新条目里 `IsMessage` 为真或 `Path` 为空 → 记为已处理。文件不存在 → 记为已处理。文件被锁（`IOException`）→ 不记，下次再试，同一 id 最多试 10 次。完成时刻：按 Task 1 F4 的结论，用 `entry.Timestamp` 或 `settings.FileLastWriteUtc`。只设 `File.SetLastWriteTimeUtc`。

- [ ] **Step 1: Write the failing tests**（临时目录里放假的 settings.json 和文件）

```csharp
ProcessOnce_FirstRun_MarksExistingWithoutTouching   // 旧文件 mtime 不变，返回 0
ProcessOnce_NewEntry_SetsMtimeToCompletion          // 断言 LastWriteTimeUtc == 期望完成时刻
ProcessOnce_NewEntry_KeepsCreationTime
ProcessOnce_MessageEntry_Ignored
ProcessOnce_MissingFile_MarkedSeen
ProcessOnce_SecondCall_DoesNotTouchAgain            // 第二次前把 mtime 改掉，断言不被改回
ProcessOnce_SeenSurvivesNewInstance                 // 新建 store+fixer 实例，不重复处理
ProcessOnce_UnreadableSettings_ChangesNothing       // settings.json 被锁，返回 0，store 不变
ProcessOnce_LockedTarget_RetriedLater               // 目标文件先锁住，解锁后下一次 ProcessOnce 改成功
```

- [ ] **Step 2: Run** `dotnet test --filter HistoryTimeFixerTests`，Expected: FAIL。
- [ ] **Step 3: 实现。**
- [ ] **Step 4: Run** `dotnet test`，Expected: PASS。
- [ ] **Step 5: Commit** `git add -A && git commit -m "feat(core): fix mtime of files received by original LocalSend"`

---

### Task 7: 专用接收服务器

**Files:**
- Create: `src/dropzone-core/Receive/CertificateStore.cs`、`src/dropzone-core/Receive/PrivateReceiveServer.cs`
- Test: `tests/dropzone-core-tests/PrivateReceiveServerTests.cs`、`tests/dropzone-core-tests/TestSupport/FreePort.cs`

**Interfaces:**
- Consumes: `PasswordToken`（Task 2）、`SafePaths`（Task 3）、协议模型（Task 4）
- Produces（命名空间 `Dropzone.Core.Receive`）：
  - `static class CertificateStore { static X509Certificate2 LoadOrCreate(string path); static string Fingerprint(X509Certificate2 cert); }` —— 自签 RSA 2048，CN=`localsend-dropzone`，10 年；PFX 字节用 DPAPI 加密后存 `cert.bin`；指纹 = DER 的 SHA-256 小写十六进制
  - `record PrivateServerOptions(int Port, X509Certificate2 Certificate, Func<string?> Password, string DownloadsDirectory, Func<DateTimeOffset> Clock, IPAddress BindAddress)`（应用传 `IPAddress.Any`，测试传 `IPAddress.Loopback`）
  - `record ReceivedFile(string FullPath, string SenderAlias)`
  - `sealed class PrivateReceiveServer(PrivateServerOptions options) : IAsyncDisposable`：`Task StartAsync(CancellationToken ct)`、`Task StopAsync()`、`string Fingerprint { get; }`、`event Action<ReceivedFile>? FileReceived`

接口与行为：
- `GET /api/lsdz/v1/hello` → 200 `HelloResponse("localsend-dropzone", 程序版本)`。
- `POST /api/localsend/v2/prepare-upload?pin=<令牌>`：本机没设密码或令牌不对 → 401；同一 IP 60 秒内失败 5 次 → 之后 5 分钟都回 429；已有进行中的会话 → 409；body 无效或任一 `fileName` 经 `ToSafeRelativePath` 为 `null` → 400；否则 200，返回 `sessionId` 和每个文件的 token。会话绑定发起方 IP。
- `POST /api/localsend/v2/upload?sessionId&fileId&token`：参数缺失 → 400；token 不对或 IP 不对 → 403。正文流式写入同目录的临时文件 `.<guid>.lsdz-part`；写完长度 ≠ `Size` → 删临时文件，回 400；否则 `GetUniquePath` 得到正式路径（需要时建子目录），`File.Move`，再 `SetLastWriteTimeUtc(Clock())`，触发 `FileReceived`。
- `POST /api/localsend/v2/cancel?sessionId` → 删掉该会话所有临时文件，结束会话。会话在全部文件收完、取消或空闲 2 分钟后结束；连接中断时删临时文件。
- Kestrel `MaxRequestBodySize = null`；只监听 `IPAddress.Any`；不做任何组播。

- [ ] **Step 1: Write the failing tests**（服务器跑在空闲端口；`Clock` 固定为 `2026-10-19T12:00:00Z`；客户端 `HttpClient` 接受自签证书）

```csharp
Hello_Returns200WithAppName
Prepare_NoPin_401
Prepare_WrongToken_401
Prepare_NoLocalPassword_401
Prepare_ValidToken_200WithFileTokens
Prepare_UnsafeFileName_400_AndNothingWritten        // "../evil.txt"
Prepare_SizeAboveInt32_Accepted                     // Size = 5_000_000_000，只 prepare 不上传
Prepare_SixthFailureFromSameIp_429
Prepare_WhileSessionActive_409
Upload_WritesFile_WithClockMtime                    // 内容一致，LastWriteTimeUtc == 12:00:00Z
Upload_NameConflict_SavedAsOne                      // 已有 报告.pdf -> 报告 (1).pdf，旧文件内容不变
Upload_NestedName_CreatesSubdirectory               // "dir/a.txt" -> Downloads\dir\a.txt
Upload_WrongToken_403
Upload_LengthMismatch_400_NoFile
Upload_AbortedMidway_LeavesNoFile                   // 发一半就断开连接，目录里没有正式文件也没有 .lsdz-part
Cancel_RemovesPartFiles
FileReceived_RaisedWithFinalPath
```

- [ ] **Step 2: Run** `dotnet test --filter PrivateReceiveServerTests`，Expected: FAIL。
- [ ] **Step 3: 实现。**
- [ ] **Step 4: Run** `dotnet test`，Expected: PASS。
- [ ] **Step 5: Commit** `git add -A && git commit -m "feat(core): private receive server with password tokens"`

---

### Task 8: 发送客户端

**Files:**
- Create: `src/dropzone-core/Discovery/DiscoveredDevice.cs`、`src/dropzone-core/Send/FileEnumerator.cs`、`src/dropzone-core/Send/TransferClient.cs`
- Test: `tests/dropzone-core-tests/TransferClientTests.cs`、`tests/dropzone-core-tests/TestSupport/FakeLocalSendServer.cs`（测试用的假原版：可配置“需要 PIN 1234”“拒绝”“忙”，把收到的文件存到临时目录，并记录收到的 `info`）

**Interfaces:**
- Consumes: `PasswordToken`、协议模型、`PrivateReceiveServer`（测试里）、`DiscoveredDevice`（定义在本任务，Task 9 复用）
- Produces（命名空间 `Dropzone.Core.Send`，`DiscoveredDevice` 放在 `Dropzone.Core.Discovery`）：
  - `record DiscoveredDevice(string Alias, string? DeviceType, string Fingerprint, IPAddress Ip, int Port, string Protocol, string? PrivateFingerprint, DateTimeOffset LastSeen)`
  - `static class FileEnumerator { static IReadOnlyList<(string FullPath, string RelativeName)> Expand(IEnumerable<string> paths); }` —— 文件夹递归展开，`RelativeName` 按 Task 1 F6 的格式（含顶层文件夹名）
  - `enum SendOutcome { Sent, SentAfterPasswordMismatch, Rejected, Busy, Cancelled, Failed }`
  - `record SendResult(SendOutcome Outcome, string? Detail)`
  - `sealed class TransferClient(Func<DeviceInfo?> originalIdentity, Func<string?> password, int privatePort, Func<DateTimeOffset>? clock = null)`（`clock` 默认 `DateTimeOffset.UtcNow`，测试用来制造时钟差）：
    - `Task<string?> ProbePrivateAsync(IPAddress ip, CancellationToken ct)` —— `GET https://ip:privatePort/api/lsdz/v1/hello`，`App == "localsend-dropzone"` 时返回本次 TLS 证书的指纹，否则 `null`；超时 1.5 秒
    - `Task<SendResult> SendAsync(DiscoveredDevice target, IReadOnlyList<string> paths, Func<Task<string?>> askPin, CancellationToken ct)`

发送逻辑：
1. `target.PrivateFingerprint` 不为空且本机设了密码 → 用固定指纹校验 TLS（证书指纹 ≠ `PrivateFingerprint` 就当连不上），`pin` = `PasswordToken.Create(密码, PrivateFingerprint, now)`，走专用端口。200 → 逐个上传 → `Sent`。401 → 走第 2 步，成功则结果为 `SentAfterPasswordMismatch`。连不上 → 走第 2 步。
2. 普通路线：`{Protocol}://{Ip}:{Port}/api/localsend/v2/prepare-upload`，`info` = `originalIdentity()`（为 `null` 时直接 `Failed`）。204 → `Sent`；401 → `askPin()`，返回 `null` → `Cancelled`，否则带 `?pin=` 重试一次；403 → `Rejected`；409 → `Busy`；200 → 上传。
3. 上传用 `StreamContent`（不整读进内存）；任一文件失败 → 调 `cancel`，返回 `Failed`。连接超时 5 秒。`metadata.modified` 填文件的修改时间（ISO-8601）。

- [ ] **Step 1: Write the failing tests**

```csharp
Probe_PrivateServer_ReturnsItsFingerprint
Probe_FakeOriginal_ReturnsNull
Send_SamePassword_UsesPrivateChannel_Sent           // 文件出现在 PrivateReceiveServer 的目录
Send_WrongPassword_FallsBackToOriginal              // 结果 SentAfterPasswordMismatch，文件出现在假原版目录
Send_ClockSkewThreeMinutes_FallsBackToOriginal      // 发送方 clock + 3 min
Send_PinRequired_AsksAndRetries_Sent                // askPin 返回 "1234"
Send_PinRequired_UserCancels_Cancelled
Send_Rejected_403
Send_Busy_409
Send_UsesOriginalIdentityAsInfo                     // 假原版记录的 info.alias/fingerprint/port == originalIdentity()
Send_TargetOffline_FailsFast                        // 目标端口没人监听，<= 6 秒内返回 Failed
Send_PrivateFingerprintMismatch_DoesNotSendToken    // 专用端口换了证书：不发令牌，改走普通路线
Expand_Folder_RelativeNamesIncludeFolder            // 按 F6 格式
Token_NeverAppearsWithPassword                      // 假服务器记录的所有 URL 和正文里都不含密码
```

- [ ] **Step 2: Run** `dotnet test --filter TransferClientTests`，Expected: FAIL。
- [ ] **Step 3: 实现。**
- [ ] **Step 4: Run** `dotnet test`，Expected: PASS。
- [ ] **Step 5: Commit** `git add -A && git commit -m "feat(core): transfer client with private channel and fallback"`

---

### Task 9: 设备发现

**Files:**
- Create: `src/dropzone-core/Discovery/DeviceRegistry.cs`、`MulticastListener.cs`、`SubnetScanner.cs`、`DiscoveryService.cs`
- Test: `tests/dropzone-core-tests/DiscoveryTests.cs`

**Interfaces:**
- Consumes: `DeviceInfo`、`Announcement`、`DiscoveredDevice`、`TransferClient.ProbePrivateAsync`
- Produces（命名空间 `Dropzone.Core.Discovery`）：
  - `sealed class DeviceRegistry(Func<IReadOnlySet<string>> ownFingerprints)`：`void Upsert(DeviceInfo info, IPAddress ip, DateTimeOffset now)`、`void SetPrivate(IPAddress ip, string? privateFingerprint)`、`IReadOnlyList<DiscoveredDevice> Snapshot(DateTimeOffset now)`（按别名排序，去掉 3 分钟没见到的，去掉本机）、`event Action<DiscoveredDevice>? DeviceAdded`、`event Action? Changed`。以 `Fingerprint` 为键，指纹比较一律不分大小写；同一指纹换了 IP 就更新 IP。
  - `static class AnnouncementParser { static Announcement? TryParse(ReadOnlySpan<byte> datagram); }`
  - `sealed class MulticastListener(DeviceRegistry registry) : IDisposable`：`void Start()` —— 共享绑定 UDP 53317，在每个可用 IPv4 接口上加入 `224.0.0.167`；只收不发
  - `static class SubnetMath { static IEnumerable<IPAddress> Hosts(IPAddress address, int prefixLength); }` —— 只处理前缀 ≥ 24，否则返回空；不含网络地址、广播地址和自己
  - `sealed class SubnetScanner(DeviceRegistry registry, TransferClient client, Func<DeviceInfo?> originalIdentity, HttpMessageHandler? handler = null)`：`Task ScanAsync(IEnumerable<IPAddress> hosts, int port, CancellationToken ct)` —— 用原版身份 `POST /api/localsend/v2/register`，并发 64，单个超时 300 ms；对有回应的主机再调 `ProbePrivateAsync` 并 `SetPrivate`
  - `sealed class DiscoveryService(DeviceRegistry registry, MulticastListener? listener, SubnetScanner scanner, TransferClient client) : IDisposable`：`void Start()`（启动监听；每 45 秒扫描一次本机各个已连接、非虚拟网卡的子网；`DeviceAdded` 时对该设备调 `ProbePrivateAsync`，所以只靠组播发现的设备也会被探测）、`Task RefreshNowAsync()`（立即扫描一次，正在扫描时直接返回那次的任务）

- [ ] **Step 1: Write the failing tests**

```csharp
Registry_Upsert_ThenSnapshot_ContainsDevice
Registry_ExcludesOwnFingerprints                     // 原版指纹和专用证书指纹都排除
Registry_ExpiresAfterThreeMinutes
Registry_SameFingerprintNewIp_Updates
Registry_SetPrivate_AttachesToDeviceAtIp
Parser_ValidAnnouncement_Parses
Parser_Garbage_ReturnsNull
SubnetMath_Slash24_Has253HostsExcludingSelf
SubnetMath_Slash16_ReturnsEmpty
Scanner_RegistersUsingOriginalIdentity               // 假服务器记录的 register 正文 == originalIdentity()
Scanner_RespondingHost_AddedToRegistry
Scanner_SilentHosts_DoNotBlockBeyondTimeout          // 20 个不响应的地址，整体 < 2 秒
```

- [ ] **Step 2: Run** `dotnet test --filter DiscoveryTests`，Expected: FAIL。
- [ ] **Step 3: 实现。** F2 不可行时按 Task 1 结论去掉 `MulticastListener`。
- [ ] **Step 4: Run** `dotnet test`，Expected: PASS。
- [ ] **Step 5: Commit** `git add -A && git commit -m "feat(core): device discovery without claiming port 53317"`

---

### Task 10: 托盘程序、设置、首次运行

**Files:**
- Create: `src/dropzone-core/Settings/AppSettings.cs`、`src/dropzone-core/Settings/SettingsStore.cs`
- Test: `tests/dropzone-core-tests/SettingsStoreTests.cs`
- Create: `src/dropzone-app/dropzone-app.csproj`（`WinExe`，`net10.0-windows`，`UseWPF` + `UseWindowsForms`，`AssemblyName` = `LocalSendDropzone`，`app.manifest` 声明 PerMonitorV2 DPI）、`App.xaml(.cs)`、`Tray/TrayIcon.cs`、`Tray/Notifier.cs`、`SettingsWindow.xaml(.cs)`、`PinDialog.xaml(.cs)`、`System/AutoStart.cs`、`System/FirewallSetup.cs`、`System/OriginalChecks.cs`、`System/AppLog.cs`

**Interfaces:**
- Produces（Core，命名空间 `Dropzone.Core.Settings`）：
  - `enum Corner { BottomRight, BottomLeft, TopRight, TopLeft }`
  - `record AppSettings(string? Password, int PrivatePort = 53319, Corner Corner = Corner.BottomRight, bool AutoStart = true, bool FirstRunDone = false)`
  - `sealed class SettingsStore(string path)`：`AppSettings Load()`（文件不存在返回默认值）、`void Save(AppSettings s)`（`Password` 用 DPAPI 加密成 base64 存 `passwordProtected` 字段，明文不落盘）
- Produces（App）：`Notifier.Show(string title, string text, Action? onClick = null)`（`NotifyIcon.ShowBalloonTip` + `BalloonTipClicked`）；`PinDialog.Ask(string deviceAlias) : string?`

程序启动顺序：单实例（`Mutex` 名 `Local\localsend-dropzone`，已有实例就退出）→ 读设置 → 首次运行检查 → `CertificateStore.LoadOrCreate` → `PrivateReceiveServer.StartAsync`（端口被占则通知“专用端口 {端口} 被占用，请在设置里换一个”）→ `HistoryTimeFixer.Start` → `DiscoveryService.Start` → 托盘图标。

首次运行（`FirstRunDone == false`）：
- 防火墙：用 `netsh advfirewall firewall show rule name="LocalSend Dropzone"` 检查；没有就用 `Verb = "runas"` 启动一个 `cmd /c` 依次添加两条入站规则，规则名都是 `LocalSend Dropzone`，`program=<exe 路径>`，`profile=private,public`，一条 `protocol=TCP localport=<专用端口>`，一条 `protocol=UDP localport=53317`。用户拒绝提权 → 通知“没有添加防火墙规则，其他电脑可能连不上”。每次启动和改端口时都检查（通过防火墙 COM 接口读取，不解析本地化的 netsh 输出）。
- 网络类别：不检查（用户决定规则对公用网络也生效）。
- 原版：没有 `localsend_app` 进程 → 通知“原版 LocalSend 没在运行，手机发来的文件会收不到”；`HKCU\…\Run` 里没有指向 `localsend_app.exe` 的值 → 通知“建议在原版设置里打开开机启动”。
- 没设密码 → 打开设置窗口。

托盘菜单（中文）：`设置`、`打开“下载”文件夹`、`暂停接收`（勾选项：勾上时 `PrivateReceiveServer.StopAsync`，取消时重新启动）、`退出`。
自动收到文件：通知标题 `已收到文件`，正文 `{文件名}（来自 {发送方}）`，点击执行 `explorer.exe /select,"<路径>"`。
设置窗口：共享密码（`PasswordBox`）、专用端口、发送区位置（右下角 / 左下角 / 右上角 / 左上角）、开机自启；保存后立即生效（端口变了就重启服务器）。
开机自启：`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 值名 `LocalSendDropzone`，数据 `"<exe>" --background`。
日志：`%LOCALAPPDATA%\localsend-dropzone\logs\app.log`，按天滚动，绝不写密码和令牌。

- [ ] **Step 1: Write the failing tests**

```csharp
Load_MissingFile_ReturnsDefaults           // PrivatePort 53319, BottomRight, AutoStart true
SaveThenLoad_RoundTrips
Save_DoesNotWritePlaintextPassword         // 文件文本里不含 "hunter2"
Load_CorruptFile_ReturnsDefaults
```

- [ ] **Step 2: Run** `dotnet test --filter SettingsStoreTests`，Expected: FAIL。
- [ ] **Step 3: 实现 `SettingsStore` 和整个 App 工程。**
- [ ] **Step 4: Run** `dotnet test` 和 `dotnet build src/dropzone-app`，Expected: 测试 PASS，构建 0 错误。
- [ ] **Step 5: 目测验证（请用户配合）。** 运行 `dotnet run --project src/dropzone-app`：托盘出现图标；首次运行弹一次 UAC；`netsh advfirewall firewall show rule name="LocalSend Dropzone"` 显示两条规则且 `Profiles: Private`；设置窗口能保存，`%LOCALAPPDATA%\localsend-dropzone\settings.json` 里没有明文密码；`Get-NetTCPConnection -LocalPort 53319 -State Listen` 属于本程序；原版仍占 53317；用手机发一个文件给原版，接受后该文件修改时间变成接收时间。
- [ ] **Step 6: Commit** `git add -A && git commit -m "feat(app): tray app, settings and first-run setup"`

---

### Task 11: 角落发送区

**Files:**
- Create: `src/dropzone-core/Ui/CornerGeometry.cs`
- Test: `tests/dropzone-core-tests/CornerGeometryTests.cs`
- Create: `src/dropzone-app/Drag/DragWatcher.cs`、`src/dropzone-app/Drag/DropZoneWindow.xaml(.cs)`、`src/dropzone-app/Drag/OriginalLauncher.cs`

**Interfaces:**
- Consumes: `Corner`、`AppSettings`（Task 10）、`DeviceRegistry`、`DiscoveryService.RefreshNowAsync`（Task 9）、`TransferClient.SendAsync`、`FileEnumerator`（Task 8）、`Notifier`、`PinDialog`（Task 10）
- Produces：
  - Core `static class CornerGeometry`（命名空间 `Dropzone.Core.Ui`，矩形用 `System.Drawing.Rectangle`，单位为物理像素）：`Rectangle TriggerRegion(Rectangle workArea, Corner corner)`（角上 150×150）、`Rectangle ZoneRect(Rectangle workArea, Corner corner)`（68×68，离两条边各 12）、`Rectangle PanelRect(Rectangle workArea, Corner corner, int deviceCount)`（宽 160，每项高 40、间距 8，贴着发送区朝屏幕内侧展开，底边或顶边与发送区对齐）
  - App `sealed class DragWatcher : IDisposable`：`event Action<Rectangle /*workArea*/>? NearCorner`、`event Action? Released`。`WH_MOUSE_LL` 钩子；左键按下后移动超过 10 像素、且光标进入光标所在显示器 `Screen.WorkingArea` 的 `TriggerRegion` 时触发一次 `NearCorner`；左键抬起触发 `Released`。钩子回调只记录并 `Dispatcher.BeginInvoke`，不做耗时操作。
  - App `static class OriginalLauncher`：`void OpenWithFiles(IReadOnlyList<string> paths)` —— exe 路径取卸载注册表里 LocalSend 的 `InstallLocation` + `localsend_app.exe`，取不到用 `C:\Program Files\LocalSend\localsend_app.exe`；按 F5 结论传参数或降级（只启动 exe + 通知“已打开 LocalSend，请把文件拖进窗口”）

窗口与交互：
- `DropZoneWindow`：`WindowStyle=None`、`AllowsTransparency=true`、`Background=Transparent`、`Topmost`、`ShowInTaskbar=false`、`ShowActivated=false`，扩展样式加 `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`。窗口只覆盖发送区加设备面板的范围，透明处不可命中。`CornerGeometry` 给的是物理像素，摆放窗口时按该显示器的 DPI 换算成 WPF 单位。
- `PinDialog.Ask` 和通知都要在 UI 线程上调用（发送在后台线程跑）。
- `NearCorner` → 显示窗口（只有发送区），同时 `RefreshNowAsync()`；`Released` → 隐藏。
- 拖着东西进入发送区 → 展开设备面板（`DeviceRegistry.Snapshot`）；没有设备时显示一行灰字 `没有发现设备`。停在某个设备上 → 高亮。
- 在设备上松手 → 隐藏窗口，后台 `FileEnumerator.Expand` + `SendAsync`，`askPin` 用 `PinDialog.Ask`。结果通知：
  - `Sent`：`已发送到 {设备}`
  - `SentAfterPasswordMismatch`：`已发送到 {设备}`，正文 `密码不一致，已改为普通发送`
  - `Rejected`：`{设备} 拒绝了文件`
  - `Busy`：`{设备} 正忙，稍后再试`
  - `Cancelled`：不通知
  - `Failed`：`发送到 {设备} 失败`
- 在发送区本身上松手 → `OriginalLauncher.OpenWithFiles`。别处松手 → 隐藏，什么都不做。
- 样式参考规格里的示意：圆角方块、发送图标加 `LocalSend` 字样，设备卡片含设备类型图标和名字（`mobile` 用手机图标，其他用电脑图标）。

- [ ] **Step 1: Write the failing tests**

```csharp
// workArea = (0,0,1920,1040)（任务栏高 40）
TriggerRegion_BottomRight == (1770, 890, 150, 150)
ZoneRect_BottomRight == (1840, 960, 68, 68)
ZoneRect_TopLeft == (12, 12, 68, 68)
PanelRect_BottomRight_ThreeDevices == (1668, 892, 160, 136)   // 左侧 12 间距，底边与发送区对齐（1028）
PanelRect_TopLeft_ThreeDevices == (92, 12, 160, 136)
ZoneRect_SecondMonitor_BottomRight == (4400, 1320, 68, 68)     // workArea = (1920, 0, 2560, 1400)
AllRects_InsideWorkArea                                        // 四个角、0–10 个设备都在工作区内
```

- [ ] **Step 2: Run** `dotnet test --filter CornerGeometryTests`，Expected: FAIL。
- [ ] **Step 3: 实现 `CornerGeometry`，跑测试到 PASS。**
- [ ] **Step 4: 实现 `DragWatcher`、`DropZoneWindow`、`OriginalLauncher` 并接入 `App`。**
- [ ] **Step 5: 目测验证（请用户配合，必要时截图）。** 从资源管理器拖文件到右下角：发送区立刻出现且在任务栏上方；进入后展开设备；在手机上松手，手机收到请求，发送方名字是 `MyPC`；在发送区本身松手打开原版（或降级通知）；拖窗口经过角落不误弹或松手即消失；平时角落和“显示桌面”可正常点击；在设置里换到左上角后生效。
- [ ] **Step 6: Commit** `git add -A && git commit -m "feat(app): corner drop zone"`

---

### Task 12: 发布与整体验收

**Files:**
- Create: `README.md`（中文：这是什么、安装步骤、首次运行会弹 UAC、在每台电脑填同一个密码和端口、原版要开机启动、开发需要 .NET 10 SDK、构建命令）

- [ ] **Step 1: 发布**

Run: `dotnet publish src/dropzone-app -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish`
Expected: `publish\LocalSendDropzone.exe` 存在；`publish\` 加进 `.gitignore`。

- [ ] **Step 2: 整体验收。** 和用户一起按规格“验收”一节第 1–11 条逐条跑，需要第二台电脑的（4、5、7）请用户把 exe 拷过去、设同样的密码。第 11 条：在第二台电脑上看 `logs\app.log` 和发送方的请求日志，确认只有令牌没有密码。每条记下结果，未通过的回到对应任务修。

- [ ] **Step 3: Commit** `git add -A && git commit -m "docs: README and release build"`
