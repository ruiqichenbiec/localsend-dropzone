# LocalSend Dropzone

**把文件拖到屏幕角落，松手在手机或电脑上，就发出去了。**
一个 Windows 托盘小程序，给 [LocalSend](https://localsend.org) 加上拖放发送。

> 非官方工具，与 LocalSend 项目没有关联，也未获其认可。仍然需要安装官方 LocalSend。

语言：[English](README.md) | 简体中文

## 能做什么

- **发文件不用先打开任何窗口。** 拿着文件往屏幕右下角拖，选设备，松手。不用再去托盘或开始菜单里找 LocalSend。
- **自己的几台电脑之间不用点“接受”。** 每台 Windows 电脑都装上 Dropzone，填同一个密码。它们之间互发的文件直接进“下载”文件夹。
- **收到的文件按收到的时间排。** LocalSend 收到的文件，“修改日期”会改成收到的时刻，“下载”文件夹按日期排序时，新文件就在最上面。

手机照常用原版 LocalSend，什么都不用改。

## 长什么样

```
                          ┌──────────────┐
                          │ Pixel 8      │
                          │ 我的笔记本   │  ┌───────────┐
                          │ 办公室电脑   │  │ LocalSend │
                          └──────────────┘  └───────────┘
═══════════════════════════════════════════════════════════ 任务栏
```

1. 拿着一个或多个文件往右下角拖，出现 **LocalSend** 小方块。平时角落里什么都没有。
2. 拖到小方块上先别松手，旁边弹出局域网里的设备。
3. 在某个设备上松手就发给它；在小方块本身上松手，会打开 LocalSend 窗口并选好这些文件；拖到别处松手就是取消。

发送结果（已发送、被拒绝、失败）用 Windows 通知告诉你。

## 需要什么

- Windows 11（64 位）。
- 装好并运行着官方 [LocalSend](https://localsend.org)。手机发来的文件还是由它接收，所以请在它的设置里打开开机启动，并让它一直待在托盘里。
- 所有设备在同一个局域网里。

## 安装

1. 从 [Releases](https://github.com/ruiqichenbiec/localsend-dropzone/releases) 下载 `LocalSendDropzone.exe`，或者自己编译（见[从源码编译](#从源码编译)）。
   - 这个 exe 没有代码签名，Windows SmartScreen 可能会拦一下，点 **更多信息 → 仍要运行**。
2. 把它放到一个固定的文件夹（比如 `%USERPROFILE%\Apps\LocalSend Dropzone\`），双击运行，托盘里出现图标。
   - 以后挪了位置也没关系，在新位置运行一次就行。
3. 让它通过 Windows 防火墙，别的电脑和手机才能连进来：
   - 如果弹出“Windows 安全中心警报”，把**专用网络和公用网络都勾上**，点“允许访问”。
   - Dropzone 自己也可能弹一次管理员确认（UAC），用来添加防火墙规则，点“是”。
4. 右键托盘图标 → **设置**，填一个**共享密码**（至少 12 个字符）。
5. 在你的其他 Windows 电脑上重复一遍，填**同一个密码**。

完成。默认会随 Windows 自动启动。

## 日常使用

| 想做什么 | 怎么做 |
|---|---|
| 发给手机或电脑 | 把文件拖到角落，停在小方块上，在设备上松手 |
| 在 LocalSend 自己的窗口里选设备 | 在小方块本身上松手 |
| 发给 iPhone | 先在 iPhone 上打开 LocalSend（iOS 只允许它开着的时候接收） |
| 接收自己其他电脑发来的 | 什么都不用做，文件进“下载”，弹一条通知 |
| 接收手机发来的 | 和以前一样，在电脑上的 LocalSend 窗口里点接受 |
| 暂时不想自动接收 | 托盘菜单 → **暂停接收**（下次启动自动恢复） |
| 把小方块换到别的角 | 托盘菜单 → **设置** → 发送区位置 |

对方要求 PIN 时，Dropzone 会弹一个小框让你输入。

## 遇到问题

**列表里没有某个设备。**
在那台设备上打开 LocalSend，确认连的是同一个 Wi-Fi，等几秒。每次开始拖动时列表都会刷新。

**手机上看不到这台电脑。**
这台电脑上的官方 LocalSend 必须开着。它没开时 Dropzone 会弹通知提醒你。

**自己的另一台电脑还是要我点接受。**
确认两台电脑上 Dropzone 都在运行、密码一样。密码不一样时会弹“密码不一致，已改为普通发送”，文件改走普通流程。另外，两台电脑的时钟相差不能超过 2 分钟。

**弹出“Windows 防火墙阻止了本程序……”。**
Windows 里有一条阻止 Dropzone 的规则，通常是防火墙对话框没点允许就关掉了。打开 控制面板 → Windows Defender 防火墙 → **允许应用通过防火墙**，找到 LocalSendDropzone，把专用和公用都勾上。

## 需要知道的

- **只能在同一个局域网里用**，不能跨网络或经过互联网传。
- **手机发给电脑，每次都要在电脑上点一下接受。** 免确认只在装了 Dropzone 的电脑之间。
- **接收方电脑要有人登录**，免确认接收才能工作。
- **“收到的时间”依赖 LocalSend 的接收历史。** 如果在 LocalSend 里关掉了“保存到历史”，就改不了（Dropzone 会提醒一次）。收到后你改过的文件不会动。

## 它在背后做了什么

Dropzone 不改 LocalSend 的设置、端口和文件。在你的电脑和网络上，它会：

- **读取 LocalSend 的设置文件**（`%APPDATA%\LocalSend\settings.json`），从中得知这台电脑在 LocalSend 里的名字、端口和接收历史。从不写入；文件里存着的私钥，从不提取、记录或发送。
- **以这台电脑的 LocalSend 的名义出现。** 发文件时，以及 LocalSend 运行期间每 30 秒，它都用 LocalSend 自己的名字和证书指纹，所以别的设备上这台电脑还是只有一个条目，不会出现两个。它还会联系最近 7 天见过的设备，保持列表是新的。
- **在 TCP 53319 端口上等你其他电脑发来的文件。** 每次传输都要带一个由共享密码算出来的校验码，密码本身不会在网络上传。端口可在设置里改（所有电脑要一致）。
- **和 LocalSend 共用 UDP 53317 端口**，用来听设备的广播，不会抢走 LocalSend 的端口。
- **为自己添加 Windows 防火墙规则**（专用和公用网络），需要你同意。
- **只改 LocalSend 刚收到的文件的“修改日期”。**
- **为当前用户添加开机启动项**（可在设置里关掉）。
- **数据都放在 `%LOCALAPPDATA%\localsend-dropzone\`**：设置（密码用 Windows DPAPI 加密）、它自己的证书、最近见过的设备列表、日志（`logs\app.log`，保留 14 天，不记密码和校验码）。

## 从源码编译

需要 .NET 10 SDK。

```
dotnet test
dotnet publish src/dropzone-app -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

得到 `publish\LocalSendDropzone.exe`（单个文件，自带 .NET 运行时）。

代码结构：`src/dropzone-core`（核心逻辑，有完整测试）、`src/dropzone-app`（托盘程序和发送区）、`tests/dropzone-core-tests`。设计文档和协议实测记录在 `docs/`。

## 许可证

Apache License 2.0。见 [LICENSE](LICENSE)、[NOTICE](NOTICE) 和 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
