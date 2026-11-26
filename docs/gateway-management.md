# Harness：VPS Gateway 远程管理范围

核对日期：2026-11-25。目标是通过官方 Gateway 契约管理 VPS 上的 **Hermes Agent 和 OpenClaw**，同时覆盖 Agent 会话/任务与服务配置/日志。其他产品只作为普通 Web 应用承载。

这是功能范围与接入设计，区分上游可用能力和 Harness 已有实现；不是远程管理已经交付的声明。此前新增的 Hermes 网页 DOM 适配只属于辅助显示，不能代替 Hermes Gateway 接入。

## 产品结构

每个环境保留独立的 Gateway 地址、产品、远端身份、认证与连接状态。原生管理面分为：

1. **概览**：Gateway 版本、协议、健康状态、活动任务、通道、授权与能力。
2. **会话与任务**：会话搜索、历史、创建、提交任务、事件进度、精确中断、模型、审批及完成通知。
3. **配置**：读取配置与 schema、受支持字段编辑、变更预览、冲突检测、保存/应用状态。
4. **日志**：远端 Gateway 日志、过滤、游标续读；与 Harness 本地日志分开。
5. **自动化**：定时任务、运行历史；技能/通道作为后续产品专属管理项。
6. **服务**：在线重启、更新状态和结果确认；Gateway 已停止时的启动/修复归入独立主机维护通道。

WebView 可以继续显示官方复杂界面。上述原生管理操作及状态不依赖网页是否打开，也不以 DOM、按钮文字或网页心跳作为 VPS 服务状态的事实来源。

## 功能依据与启用条件

| Harness 特性 | OpenClaw Gateway | Hermes Gateway | 选定范围 |
|---|---|---|---|
| 连接与能力发现 | v4 握手、服务端身份、methods/events/capabilities、授予的 scopes | `hermes-gateway-v1`、`runtime.describe`、profile/instance/authority 身份、接入方式对应的认证 | 两种独立协议适配器，共享界面与连接状态模型 |
| 会话浏览与历史 | `sessions.list`、`chat.history` | `session.list`、`session.transcript`/`session.history`、`session.resume` | 原生列表、搜索、分页；保存会话列表与订阅当前运行分开 |
| 提交任务与执行进度 | `chat.send` 与 chat/agent 事件 | `session.create`、`prompt.submit` 与 message/tool/session 事件 | 提交确认、排队、运行、完成、失败分别展示；不把 ACK 当完成 |
| 停止当前/后台任务 | `chat.abort`，确认 session 和 run 身份 | `session.interrupt`/对应 authority 控制，确认 session、运行代次和授权 | 目标明确后开放；不通过输入 `/stop` 实现原生管理 |
| 模型与会话设置 | `models.list`、`sessions.patch` | `model.options`、session 创建参数与 `session.mutate`；兼容路径使用声明支持的 `command.dispatch` | 区分 Profile 默认值与活动会话覆盖；按服务端目录、revision 和参数能力展示 |
| 审批与澄清 | 对应审批 RPC/事件与审批 scope | authority 的 `approval.respond`/`clarify.respond`；协议声明的 server→client 请求另外处理 | 绑定 prompt/运行代次；正确声明客户端能力并处理撤回/重放；未知请求及时明确拒绝 |
| 健康与状态 | `health`、`status`、`diagnostics.stability` | Gateway 运行状态/管理 HTTP、`gateway.ping`、authority runtime 信息 | 分开显示网络可达、认证成功、Gateway 就绪与任务执行状态 |
| 配置读取/编辑 | `config.get/schema/schema.lookup`；`set/patch/apply` | `GET /api/config`、`/api/config/schema`、`PUT /api/config`；WS `config.set` 不是全量配置编辑契约 | 优先有 schema/明确字段的表单；写入单独授权，按远端能力判断生效时机 |
| 远端日志 | `logs.tail(cursor, limit, maxBytes)` | `GET /api/logs`，支持文件/级别/组件/搜索/Profile，每次最多 500 行 | OpenClaw 使用游标，Hermes 限量轮询；不把 Hermes 尾部读取声称为 SSE，也不宣称读取任意系统日志 |
| 定时任务 | `cron.get/list/status/runs` 与授权的新增/修改/运行接口 | authority `cron.status/submit/cancel/recover` 与管理 HTTP 的任务配置接口 | 列表、运行历史、按能力开放编辑/运行；区分执行控制与计划编辑 |
| 技能与通道 | `skills.status/install/update`、对应 channels 方法 | 技能/通道管理面；部分旧 RPC 仍是兼容 handler | 后续产品专属页；不把名称相同当作跨产品相同协议 |
| 在线重启 | `gateway.restart.request`，可能等待活动工作 | `POST /api/gateway/restart` 当前执行 `restart --interrupt`；另有 `/api/gateway/drain` | Hermes 必须明确显示会中断任务；排空与重启分开处理；两边都在重连后核实恢复，ACK 不等于成功 |
| 在线更新 | `update.run/status`，可用版本的 update run 查询 | `GET /api/hermes/update/check`、`POST /api/hermes/update`，受安装类型约束 | 查询可更新性和进度；完成后核对实际运行版本，不能强制更新 Docker/Nix 安装 |
| 停服后启动、首次安装、服务损坏修复 | 主机 CLI/service manager，不是通用在线 Gateway RPC | 官方 `gateway attach --start`/ensure 与服务管理；远程执行需要仍可用的宿主通道 | 主机维护单列；Gateway 自己离线时不能承诺靠原连接恢复进程 |

## OpenClaw 接入决策

保留现有原生 WebSocket 客户端。当前 Harness 固定 operator/UI 协议 v4，默认请求 `operator.read`、`operator.write`；这不包含所有管理权限。

- `health/status/logs.tail/config.get/schema` 等读取以实际 `operator.read` 权限为基础；敏感状态字段可能要求 admin。
- `config.set/patch/apply`、技能安装/更新、`update.run/status`、`gateway.restart.request` 等管理操作要求 `operator.admin`。管理模式必须经过相应授权/设备权限升级，不能沿用单一“可写”布尔值。
- 定时任务存在全局 admin 与较新版本受限 `operator.cron` 的不同授权路径；审批也使用专属 scope。每个操作分别判定。
- 配置写入携带 `baseHash`，处理其他客户端并发修改。断连或超时后先读取核对，不自动重复提交配置/更新/重启。
- 更新保留 `update.run` 返回的 durable `runId`，重连后查询操作结果并核对实际运行版本；重新连上不等于更新成功。
- `gateway.restart.preflight` 已弃用，选用正式 `gateway.restart.request`。内部存在的 stop handler 不等于稳定通用停服/开服契约。
- `hello.features.methods` 是保守发现信息；协议版本相同不保证全部新方法可用。结合明确支持的服务器版本、方法/能力信息和实际授权判断，不机械地把未知能力当可用。

## Hermes 接入决策

主接入对象是拥有 session authority 的共享 Gateway。官方文档同时描述共享 Gateway、独立 `tui_gateway`、旧 dashboard/serve 和 API Server；这些不能混成一个后端或共用一份 Token。

### 官方 SSH 客户端路径

官方已有 [Gateway over SSH client contract](https://hermes-agent.nousresearch.com/docs/developer-guide/gateway-ssh-clients)，Harness 可以据此接入 VPS，无需发明远端常驻代理：

1. 通过 SSH 运行 `hermes gateway tickets --probe --json`，读取协议与支持的 ticket purpose。
2. 按用户选择的连接/启动动作执行 `hermes gateway attach --json` 或 `--start`，读取真实 loopback endpoint、profile、instance 和能力，建立端口转发。
3. WebSocket 使用 `/api/ws`、`hermes-gateway-v1` 与 attach 返回的单次、绑定 Profile 的 WS ticket。服务端声明支持 `interactive` purpose 时，可经 tickets 通道领取支持该 Gateway 所服务多个 Profile 的 WS ticket，申请时不传 `profile`。每次重连重新取票；先读取 `runtime.describe` 再创建/恢复会话。
4. 保持一个 `hermes gateway tickets --serve --json` 通道，管理 HTTP 每次请求单独领取 `native-http` ticket，放入 `X-Hermes-Gateway-Ticket`；不用 WebSocket ticket 认证 HTTP。
5. 校验 attach 与 ticket channel 的 `instance_id`；服务重启后废弃旧 ticket、订阅和请求。管理 HTTP 访问不同 Profile 时必须使用对应 Profile 的票据及权限；跨 Profile 的 interactive WS ticket 不沿用这一限制，但每帧目标仍须属于该 Gateway 服务的 Profile。

官方 tickets 契约要求 loopback、无 Origin、短期单次票据；它不是可以把票据直接发送到任意公网 URL 的认证模式。普通连接动作与可能启动远端服务的 `--start` 应在 Harness 中清楚区分。

### 已公开的 HTTPS Gateway 路径

对于已配置认证的公开 Gateway，先查询 `/api/status` 和认证能力。支持 `native_pkce` 时，通过系统浏览器和官方 `/auth/native/authorize`、`/auth/native/token`、`/auth/native/refresh` 完成原生登录；access token 用于 REST 与获取 WebSocket 单次票据。必须处理刷新轮换及会话失效。

旧部署可能只有 cookie 登录或旧独立 dashboard 后端；单独显示兼容状态，不把“status HTTP 200”当成聊天、管理和共享 authority 全部就绪。`/api/status` 可以是公共接口，必须另行确认受认证的管理调用和 WS 会话契约。

### 管理面与协议边界

本次核对的 Hermes 提交 `010f119d4829c28a955c2e91845c5dc8803de2ce` 中，`gateway/run_api.py` 明确由 Gateway 自身的 HTTP/WS listener 提供 native session WS，并挂载 dashboard 管理 API；不会因此另启第二个 agent 或 scheduler。配置、日志、状态等应优先使用这个 Gateway 所属的管理面。

`runtime.describe` 提供 `instance_id`、`profile_id`、`authority_epoch`、`capabilities` 和 `session_create.sources/parameters`，没有完整 REST 方法清单。因此 REST 能力仍需按明确支持的版本/部署和受认证的实际响应判定。不能硬编码 SSH listener 的端口，也不能从 Webapp 常见端口 9120 或旧 dashboard 9119 推断实例。

配置写入 `PUT /api/config` 接受 `{config:{...}, profile?}` 并深合并持久化；不能照搬 OpenClaw `baseHash`，也不能把保存成功当作运行中全部配置已更新。环境变量/密钥默认保持脱敏读取，不默认调用密钥揭示接口。

服务动作还包括 `/api/gateway/start`、`/api/gateway/stop` 和 `/api/actions/{name}/status`。停止遇到活动任务默认拒绝，显式 interrupt 才中断；共享 Gateway 的被服务 Profile 不能独立开停宿主。重启接口当前是强制中断式实现，UI 必须如实标识。服务进程与管理 listener 一起停止后，通过 SSH/宿主服务通道恢复。

任务提交使用稳定 submission ID；结果不确定时读取 `prompt.receipt`，不能重连即重发。会话写入、中断和审批按适用契约校验 revision、`execution_generation`、prompt/request 身份。事件保存 replay epoch/seq，遇到 `snapshot_required` 拉新快照。

`/v1/chat/completions`、`/v1/responses`、`/v1/runs` 与 `API_SERVER_KEY` 属于可选的 OpenAI-compatible API Server。它们可以服务特定前端，但不能替代共享 Gateway 的完整管理契约，也不能将其密钥作为 Gateway 通用认证。

官方 shared Gateway 与旧独立 TUI 的方法不同：例如共享 Gateway 使用 `session.resume/detach`，不能照搬 `session.activate/close`。OpenRPC 文件列出的 canonical method 名单也不是当前服务器完整能力清单。未被 authority 明确接管的兼容 handler 遇到 `-32601` 应显示不支持。

## Harness 当前差距

| 层次 | 当前代码实际具备 | 必须补齐 |
|---|---|---|
| OpenClaw | 原生握手、设备身份、DPAPI 凭据、会话列表/创建/历史/模型/标签/精确停止、订阅恢复 | 任务发送、管理权限、状态/配置/日志/cron/restart/update 的类型化客户端和面板 |
| Hermes | 后端分类和网页辅助适配 | 独立 Gateway transport、认证/ticket 生命周期、能力发现、会话/事件/管理接口 |
| 共享管理层 | 环境隔离、窗口、通知、WebView 与设置基础 | 与 WebView 解耦的连接快照、能力模型、任务目标身份、管理页面 |
| 日志 | 本机 Harness 日志 | 明确标识 VPS/环境的远端 Gateway 日志 |

现有 OpenClaw 会话写入会核对当前 WebView 所处会话，这适合网页协同，但不能直接用作后台任务或服务管理的授权条件。远程操作以环境 ID、Gateway 身份、连接代次、profile/session/run 为目标；网页导航保护继续独立保留。

## 实施顺序

1. **连接与可观测性**：两种独立 Gateway adapter、认证、能力状态、服务概览、远端日志、只读配置。完成后才能把 Hermes 标为“原生 Gateway 已接入”。
2. **会话与任务**：独立原生会话目标、任务发送/事件进度/中断、模型、审批、后台通知与重连补齐。
3. **配置与日常管理**：schema 表单、差异/版本冲突、任务调度、权限升级、在线重启/更新及结果核验。
4. **主机维护与产品扩展**：Gateway 离线时的启动/修复、技能与通道等。按实际管理入口和部署类型开放。

每项能力记录“支持且授权 / 权限不足 / 服务端版本不支持 / 当前离线”，不使用一个 `CanManage` 覆盖全部操作。秘密存储按产品、环境、endpoint 与授权边界隔离；事件与异步回包不能跨连接/服务实例/会话生效。

验收需要协议 fixture 验证权限、过期票据、断连/重启、旧回包和写入冲突，再对用户实际 VPS 版本执行联调。本次核实官方文档与源码，没有连接或修改用户 VPS。

## 官方依据

- OpenClaw：[Gateway 协议](https://docs.openclaw.ai/gateway/protocol)、[客户端契约](https://docs.openclaw.ai/gateway/clients)、[Operator scopes](https://docs.openclaw.ai/gateway/operator-scopes)。
- OpenClaw：[会话 RPC](https://docs.openclaw.ai/gateway/protocol/rpc-session-control)、[配置/更新 RPC](https://docs.openclaw.ai/gateway/protocol/rpc-talk-config-and-agents)、[系统/日志/终端 RPC](https://docs.openclaw.ai/gateway/protocol/rpc-system-and-channels)、[自动化/技能 RPC](https://docs.openclaw.ai/gateway/protocol/rpc-devices-nodes-and-approvals)。
- OpenClaw：[Control UI 功能对应接口](https://docs.openclaw.ai/web/control-ui/feature-reference)、[主机服务](https://docs.openclaw.ai/cli/gateway/service)、[权限表源码](https://github.com/openclaw/openclaw/blob/main/src/gateway/methods/core-descriptors.ts)。
- Hermes：[Gateway over SSH](https://hermes-agent.nousresearch.com/docs/developer-guide/gateway-ssh-clients)、[Gateway 内部与 attach 契约](https://hermes-agent.nousresearch.com/docs/developer-guide/gateway-internals)、[Programmatic Integration](https://hermes-agent.nousresearch.com/docs/developer-guide/programmatic-integration)。
- Hermes：[原生登录](https://hermes-agent.nousresearch.com/docs/guides/desktop-native-signin)、[管理功能](https://hermes-agent.nousresearch.com/docs/user-guide/features/web-dashboard)、[Gateway Monitoring](https://hermes-agent.nousresearch.com/docs/developer-guide/gateway-monitoring)。
- Hermes：[本次固定源码](https://github.com/NousResearch/hermes-agent/tree/010f119d4829c28a955c2e91845c5dc8803de2ce)、[Gateway HTTP/WS listener](https://github.com/NousResearch/hermes-agent/blob/010f119d4829c28a955c2e91845c5dc8803de2ce/gateway/run_api.py)。
- Hermes：[配置路由](https://github.com/NousResearch/hermes-agent/blob/010f119d4829c28a955c2e91845c5dc8803de2ce/hermes_cli/web_routers/config_env.py)、[状态和日志路由](https://github.com/NousResearch/hermes-agent/blob/010f119d4829c28a955c2e91845c5dc8803de2ce/hermes_cli/web_routers/status.py)、[服务动作与状态](https://github.com/NousResearch/hermes-agent/blob/010f119d4829c28a955c2e91845c5dc8803de2ce/hermes_cli/web_routers/actions.py)。
