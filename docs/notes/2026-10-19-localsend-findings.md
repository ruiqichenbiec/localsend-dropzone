# 原版 LocalSend 1.18.2 实测结论

日期：2026-10-19
环境：Windows 11（10.0.26200），原版 1.18.2 装在 `C:\Program Files\LocalSend\`，在托盘运行；手机（安卓，原版 LocalSend）。
证据来源：本机探测工具（一次性，`%TEMP%\lsdz-probe`，未提交）、用户在手机和电脑上目测、原版源码（tag `v1.18.2`，commit `af0416b`）。

## F1 原版身份和指纹格式

**结论**：指纹是 TLS 证书 DER 字节的 SHA-256，**大写**十六进制，不带分隔符。原版公告字段：`version` = `"2.2"`，`deviceModel` = `"Windows"`，`deviceType` = `"desktop"`，`protocol` = `"https"`，`port` = 53317，`download` = `false`。

**证据**：
- 源码 `packages/core/src/crypto/cert.rs:101-106`：`format!("{byte:02X}")`。
- 本机收到的原版公告原文：`{"alias":"MyPC","version":"2.2","deviceModel":"Windows","deviceType":"desktop","fingerprint":"0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF","port":53317,"protocol":"https","download":false,"announce":true}`，指纹与探测工具对 127.0.0.1:53317 握手得到的证书哈希一致。
- 安卓原版的公告用 `"protocol":"http"`，所以发送端两种协议都要支持。

## F2 共享 UDP 53317

**结论**：可行。原版在 Windows 上用 `SO_REUSEADDR` 绑定组播端口；本程序用 `ExclusiveAddressUse = false` 加 `ReuseAddress = true` 也能绑定 0.0.0.0:53317，并在每个 IPv4 接口上加入 224.0.0.167，收到了手机和原版的公告。

共享绑定期间原版是否照常工作，只验证了一部分：第二次监听（本地时间 19:38:10–19:42:10，共 240 秒）期间，原版持续发出公告（两个来源地址各 21 次），F5 的命令行转交（经原版 TCP 53317 的 `/show` 接口）成功了，F7 补发公告之后原版也列出了手机。**没有**在共享绑定期间实测“手机发文件、原版弹窗接收”，这一项留到 Task 10 第 5 步验证。

**证据**：源码 `packages/core/src/multicast/socket.rs:95-105`；探测工具 `listen` 的输出里有来自 `198.51.100.203`（手机）的公告；时间对照（本地时间）：register 手机的响应头 `date: Mon, 19 Oct 2026 18:40:02 GMT` 即手机时钟的 19:40:02；命令行转交用的测试文件 `lsdz-handoff-test.txt` 是在启动转交之前写入的，本机记录的写入时间为 19:40:08，转交在其后 3 秒内完成；从 Wi-Fi 补发的公告在监听输出里的时间是 19:41:56。三者都在 19:38:10–19:42:10 之内。

## F3 冒用原版身份 register

**结论**：可行，不会多出条目。以原版的身份（F1 的字段）向手机 `POST /api/localsend/v2/register`，手机返回 200 和它自己的信息；用户确认手机上 MyPC **只有一个**。

**证据**：用 `curl`（手机用的是 `http`，探测工具的 register 命令只支持 https）向 `http://198.51.100.203:53317/api/localsend/v2/register` 发送后得到 200，响应里是手机的 `alias`、`fingerprint`、`deviceModel`；用户目测确认。

## F4 接收历史的时间戳

**结论**：每个文件**收完时**写入一条历史，`timestamp` = 收完那一刻的 `DateTime.now().toUtc()`，可以直接当作接收完成时刻用。历史最多保留 30 条；原版设置里如果关掉了“保存到历史”，就不会写入。

**证据**：源码 `app/lib/provider/network/server/controller/receive_controller.dart:322-370`（只在成功分支写入，写入时调用 `DateTime.now()`）、`app/lib/provider/receive_history_provider.dart`（最多 30 条）；本机最新一条的 `timestamp` 与 `settings.json` 的最后写入时间相差约 1 毫秒，与“收完时写入”一致；但那一条是 120 字节的消息，本身分不出开始和完成。计划里“发一个 200 MB 文件、对比开始和完成时刻”的实测**没有做**，“是完成时刻”这一结论只依据源码。

历史里文件条目的 `path` 是完整路径（本机 4 条文件记录都在 `%USERPROFILE%\Downloads\` 下，这些文件现在都不在记录的路径上（原因没有查））；消息条目的 `path` 为空。

**给 Task 6 的风险**：历史最多 30 条，一次收进超过 30 个文件（比如一个大文件夹）时，较早的条目可能在本程序处理前就被挤掉；用户如果在原版里关掉“保存到历史”，就收不到任何条目。

## F5 命令行把文件交给正在运行的原版

**结论**：可行。第二次启动 `localsend_app.exe <文件路径>` 时，参数会转交给正在运行的原版，第二个进程以退出码 0 退出；原版窗口弹到前面，“发送”页里已经选好了该文件。用户已目测确认。

**证据**：源码 `packages/localsend_isolates/lib/util/show_instance.dart:20-50`（第二个进程通过 `POST /api/localsend/v2/show?token=…` 转交参数）；实测时只剩一个 `localsend_app` 进程。

**没测的情况**：只测了一个文件。一次传多个文件、传文件夹、以及原版没在运行时（这时会启动一个新的原版）都没有实测，Task 11 目测时要覆盖。

## F6 文件夹的 `fileName` 格式

**结论**：`<顶层文件夹名>/<子目录>/<文件名>`，分隔符是 `/`，包含顶层文件夹名。接收端会拒绝 `..` 和绝对路径。

**证据**：源码 `app/lib/provider/selection/selected_sending_files_provider.dart:149-175`；`packages/localsend_isolates/lib/src/task/server/file_saver.dart:160-186`。

## F7（计划外）原版的广播没有走 Wi-Fi

**观察到的现象**：
- 之前：用户在电脑原版和手机上都刷新后，报告两边**互相看不到**。
- 同一时段，原版自己的公告只从一个 VPN 叠加网络（192.0.2.82）和一条直连网线（“Ethernet 2”，203.0.113.1）发出，没有一次从 Wi-Fi（198.51.100.170，网段 /20，被 Windows 标为“公用网络”）发出。
- 手机的公告**能**到达这台电脑（探测工具在 0.0.0.0:53317 收到了手机的公告），但原版仍然没有列出手机。
- 以原版的身份 register 到手机后，手机列出了 MyPC；再以原版的身份从 Wi-Fi 补发 3 次公告后，用户确认电脑上的原版也列出了手机。

**推断（未证实）**：手机看不到电脑，是因为原版的公告没有走 Wi-Fi。原版为什么不从 Wi-Fi 发公告、为什么收到了手机公告也不列出手机（可能只在部分接口上加入组播组，也可能跟“公用网络”有关），原因**未知**。原版设置里有“网络接口”白名单 / 黑名单（按 IP 模式匹配，见 `packages/localsend_isolates/lib/util/network_interfaces.dart:34-60`，设置页 `app/lib/pages/settings/network_interfaces_page.dart`），可能可以让它只用 Wi-Fi，但没试。

**证据**：第二次监听（240 秒）的输出（第一次 180 秒的监听在用户操作之前就结束了，什么都没收到）；电脑到手机的 TCP 53317 和 ping 都通；用户前后两次目测。

## 对后续任务的影响

- Task 5：指纹按 F1 用大写；`OriginalIdentity` 的固定字段取 F1 的值。
- Task 6：完成时刻用历史里的 `timestamp`（依据源码，大文件未实测）。
- Task 8：文件夹的 `RelativeName` 按 F6；普通路线要支持 `http` 和 `https`。
- Task 9：保留组播监听；另外按 F7 定期以原版身份在每个 IPv4 接口上补发公告；Wi-Fi 是 /20 网段，子网扫描不会覆盖，需要别的办法把见过的设备再找回来（见 SDD 记录里的裁定）。
- Task 11：在发送区本身上松手时，直接用命令行把文件交给原版（单个文件已实测；多文件、文件夹、原版未运行这几种情况在 Task 11 目测时确认）；规格“发送”第 5 条的降级方案保留给转交失败的情况。
- 停止条件（F3 出现两个条目、F4 历史不可用）都没有触发。
