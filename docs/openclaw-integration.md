# OpenClaw 原生 Gateway 集成

适用版本：**Harness 5.3.10**（2026-11-23）。当前 Release 面向 **Windows 11 24H2（版本 26100）及以上**，更早的 Windows 版本尚未验证。

Harness 保留 WebView2 中的 OpenClaw Control UI，用独立的 .NET WebSocket 连接补充原生会话控制。聊天渲染、输入、附件、文件和网页中的工具界面继续由上游页面负责。更换 Chromium 嵌入方案不会带来这些 Gateway 能力，因此本次没有更换浏览器内核，也没有打包 Chromium。

自包含 Release 包含 .NET 和 Windows App SDK，但 **WebView2 Runtime 仍是外部依赖**，需要系统已安装或另行安装；SDK 随构建引入并不等于打包浏览器运行时。

## 当前工作区预览

以下更新在本地预览中提供，尚未发布为新的 GitHub Release：

- 会话搜索交给 Gateway 执行，分页保持相同查询，旧搜索的迟到回包不能覆盖新查询。
- 切换期间的结束事件会保留并安排补刷；临时读请求失败按有限退避重试，保留快照并暂时禁用依赖新鲜状态的写操作。
- 模型与推理选择区分服务器值和用户草稿；无未提交编辑时跟随服务器设置。
- 会话详情展示 Gateway 返回的 Agent、最近更新时间和有效 Token 用量，并可复制已确认的运行 ID。
- 会话标签支持修改或留空清除。标签和模型操作均携带 `expectedSessionId`，修改前重新确认网页所处会话。
- 模型设置增加推理强度，只展示 Gateway 模型目录声明的可选项；切换模型后丢弃不兼容的选择。模型与推理强度通过一次 `sessions.patch` 应用，默认值使用显式 `null` 清除覆盖。
- 旧会话的历史请求、旧连接的回包和较早的快照不会覆盖当前会话；停止请求 ACK 晚于终态事件时不会把界面退回“停止中”。
- 连接错误中的配置入口直达当前环境。已经打开的设置窗口保留正在编辑的草稿。

设置页的 Windows 启动状态来自后台只读查询；保存期间关闭设置会等待保存，退出整个程序也会等待保存与必要的回滚结束。WebView 浏览器主进程退出会按自动恢复设置申请控件重建，手工 Retry/Reload 仍可使用；辅助进程故障不再一律退役整个页面。切到后台会取消尚未完成的前台恢复检查。

预览新增回归命令：

```powershell
pwsh -NoProfile -File tools/verify-openclaw-workspace.ps1
pwsh -NoProfile -File tools/verify-settings-reliability.ps1
pwsh -NoProfile -File tools/verify-webview-lifecycle.ps1
pwsh -NoProfile -File tools/verify-tray-recovery.ps1
```

OpenClaw 工作区回归使用真实回环 WebSocket 和产品 ViewModel 方法，WebView/WinUI 边缘使用替身；设置与生命周期回归使用产品方法及确定性平台替身。它们覆盖请求内容和异步顺序，不代表实际 VPS、Cloudflare 登录、浏览器进程崩溃或界面动画验收。

2026-11-16 本轮验证：Core 99 项、OpenClaw 工作区 59 项、设置可靠性 40 项、WebView 生命周期 52 项、托盘恢复 9 项通过；Debug x64 构建零警告、零错误，格式检查与原生 WebView2 回归通过（Runtime `157.0.4341.46`）。四个工作区/生命周期验证已接入 CI 与 Release 工作流，使用固定 PowerShell 7.6.9 执行；尚未在 GitHub runner 上运行本轮改动。

会话搜索通过现有监视循环合并，输入稳定至少 300ms 后发起，通常最多约 2 秒再加 Gateway 响应时间。后续可在独立列表刷新中缩短等待，不应为搜索而重载网页。

## 当前能力与边界

此实现针对 OpenClaw Gateway 协议 **v4**，按上游提交 [`7ecb9cec67740e1afedc0a8e20542c6cfa456358`](https://github.com/openclaw/openclaw/tree/7ecb9cec67740e1afedc0a8e20542c6cfa456358) 的公开协议及客户端源码核对。其他协议版本会显示不兼容，不降级猜测报文。

原生面板支持会话列表、分页、Gateway 服务端搜索、新建空会话、切换网页会话、读取运行状态、选择允许手动使用的模型、精确停止一个已确认的运行，以及对观察到的运行完成发送原生通知。模型可选性和可用性由 Gateway 返回；“默认模型”通过清除会话覆盖实现。后续跨会话任务、通知定位和连接诊断的范围见 [深度整合路线](openclaw-roadmap.md)。

本阶段不提供原生消息编辑器或聊天记录渲染，也不从原生面板发送消息。Core 保留通用请求接口并包含历史、模型和会话的类型化方法，但这不代表所有 Gateway 管理接口已有界面。

会话控制以 `agentId + key + sessionId` 为目标。`key` 在会话重置后可能保持不变，因此修改模型携带 `expectedSessionId`，停止运行携带精确的 `runId` 和 `sessionId`。聚合的忙碌标志、历史观察过的运行或本地超时都不能作为停止目标。停止请求的 ACK 也不直接等同于运行已结束；后续事件或新快照负责确认。

## 在设置中启用

1. 打开 **设置 → 环境**，选择或添加目标环境，后端选择 **OpenClaw**。
2. 填写网页工作空间地址，在 **OpenClaw 连接** 中打开 **启用 Gateway 控制**。
3. Gateway WebSocket 地址可留空，使用工作空间地址推导出的 `wss://` 地址；若反向代理的网页路径与 WebSocket 路径不同，填写真实 WebSocket 地址。远程入口要求 WSS；明文 WS 仅允许回环地址。
4. 选择 Gateway 令牌、Gateway 密码或已有受信代理授权。后者要求服务器已经正确配置受信代理，不会在客户端伪造代理身份。需要令牌或密码时，在密码框中填写；留空保留原有凭据。
5. 如果入口额外受 Cloudflare Access 保护，可在可选区域填写通过既有登录流程取得的 **Access 用户令牌**，或勾选移除已保存令牌。最后选择 **保存并关闭**。

首次连接可能要求在 Gateway 一侧批准 Harness 的设备配对。原生连接使用独立的设备身份，网页已有登录并不自动批准它。配对错误会保留可安全展示的请求 ID；具体批准操作遵循当前 Gateway 的设备管理流程。

## 网页与原生连接的隔离

网页工作空间地址与 Gateway WebSocket 地址分别服务于页面和原生 RPC。每个环境有自己的连接代次与 WebView 用户数据；切换环境会使旧请求和旧事件失效。直连入口和 Cloudflare 入口可分别配置为两个环境，不自动切换路径或把一条路径的凭据带到另一条路径。

网页的 Cookie、Cloudflare 登录会话和原生 WebSocket 凭据彼此独立。原生客户端不读取浏览器 Cookie、不把 Gateway 令牌注入网页，也不跟随 WebSocket 升级时的 HTTP 重定向。HTTP 401、403 或重定向会报告入口授权问题并停止自动重试；这与升级之后的 Gateway 拒绝或设备配对问题不同。

Cloudflare Access 用户令牌通过 `Cf-Access-Token` 发送，只绑定到配置的同一 HTTPS origin，并只用于 WSS。它仍可能过期，届时需重新登录并更新令牌。Core 同时支持服务令牌的 `CF-Access-Client-Id` 和 `CF-Access-Client-Secret`，但设置界面当前仅提供用户令牌。两种方式互斥，服务令牌的两个字段必须同时提供。入口 Access 授权不能替代 Gateway 本身的认证。

## 网页会话身份确认

旧版 Control UI 通过其公开应用状态读取当前 `sessionKey` 和 `chatRunId`。新版 Control UI 使用上游会话 URL 契约，支持带部署 base path 的 `/chat/{agent}/{key-segments}` 和显式字面量 `~key` 路径，保留 Unicode、点号及波浪号转义。

原生面板导航使用完整键生成精确路径，并读取目标的 Gateway 历史以确认会话身份。`/chat/{agent}` 主会话路径只在当前连接的 `hello.snapshot.sessionDefaults` 明确给出主会话键时解析，不假定主键一定为 `main`。友好标题、缩短的会话 ID、全局别名、无法识别的路径和缺少足够身份信息的页面不会被猜测成某个可写会话；面板可保留会话列表，目标不确定时禁用修改和停止。页面存在草稿或附件时，切换会话会提示先处理草稿。

旧启动链接 `/chat?session=agent:...` 仍由当前上游 bootstrap 接收并转成现代路径，但最终可能成为需要服务端解析的短链接。Harness 的 legacy query 解析仅接受完整、规范的 `agent:` 会话键，不把 `main` 等未限定别名猜测成当前 agent。

## 协议与凭据实现

- 原生客户端使用 `ClientWebSocket`，单接收循环、串行发送、请求 ID 关联和连接代次隔离。请求超时或取消只结束本地等待，不隐式调用 `chat.abort`。
- 握手等待 `connect.challenge`，以其时间和 nonce 构造 v3 设备签名。客户端标识为 `gateway-client`，模式 `ui`，角色 `operator`；声明读写 scope 和已实现的 `tool-events`、`model-selection-policy` capability。
- 共享令牌放在 `auth.token`，设备令牌放在 `auth.deviceToken`，密码放在 `auth.password`。签名绑定共享令牌，未提供共享令牌时绑定设备令牌，遵循当前上游客户端实现。
- 收发消息有限制，分片累计也计入限制。外层事件序号缺口会结束本代连接并重建订阅与快照；运行 payload 内的局部序号不当作全局序号。揭示缺口的终态事件先交付，再结束连接。
- 密钥、Gateway 凭据和设备令牌由当前 Windows 用户的 DPAPI 保护，设置文件只保留引用。设备身份及设备令牌按环境和规范化 endpoint 隔离；修改地址需重新输入凭据。远端错误文本和响应头不进入用户提示或日志，错误仅保留有界协议标识。

## 验证

在仓库根目录运行：

```powershell
dotnet test tests/Harness.Core.Tests/Harness.Core.Tests.csproj -v:minimal
dotnet build src/Harness/Harness.csproj -c Debug -p:Platform=x64 -r win-x64 -v:minimal
pwsh -STA -File tools/verify-native-webview.ps1 -OutputDirectory artifacts/native-webview-regression
```

Core 测试使用真实回环 TCP/WebSocket 服务端，覆盖握手字段、签名绑定、并发回包、超时后晚到响应、断线及代次、事件缺口、协议版本拒绝、错误脱敏、精确停止、空值补丁、消息大小、入口授权响应及路径/运行目标策略。它们不等同于对部署中的 OpenClaw、Cloudflare 登录或真实会话进行端到端验收。交付时应分别报告 Core 测试、Windows 构建、原生 WebView 回归和真实服务联调结果。

Debug 程序位于 `bin/Debug/5.3.10/net10.0-windows10.0.26100.0/win-x64/Harness.exe`。交付前需检查输出中的 Windows App Runtime 原生 DLL 和 `Harness.pri`；编译成功本身不证明依赖完整或程序已通过启动验证。

5.3.8 对 bridge 的 DOM 探测进行了有界文本采样、模型控件命中后提前返回和变更检查合并，并停止仅因已连接运行的可见输出静默而触发的自动重同步/刷新。性能验证使用包含 **1000 条消息**的合成 WebView2 页面，衡量 bridge 探测开销，不代表真实登录页面输入、滚动或折叠的交互帧率。实际 VPS、Cloudflare Access、设备批准、会话切换、流式输出及断线恢复仍需用户在自己的部署中完成端到端测试。

主要上游参考：

- [Gateway 协议](https://docs.openclaw.ai/gateway/protocol)
- [握手与设备认证](https://docs.openclaw.ai/gateway/protocol/handshake)
- [会话控制 RPC](https://docs.openclaw.ai/gateway/protocol/rpc-session-control)
- [实际连接认证字段](https://github.com/openclaw/openclaw/blob/7ecb9cec67740e1afedc0a8e20542c6cfa456358/packages/gateway-client/src/connect-auth.ts)
- [会话 URL 构造与解析契约](https://github.com/openclaw/openclaw/tree/7ecb9cec67740e1afedc0a8e20542c6cfa456358/packages/session-url-contract/src)
- [旧 session query 的 bootstrap 兼容](https://github.com/openclaw/openclaw/blob/7ecb9cec67740e1afedc0a8e20542c6cfa456358/ui/src/app/bootstrap-location.ts)
