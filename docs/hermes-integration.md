# Hermes 网页辅助适配记录

Harness 的目标是管理 VPS 上的 Hermes/OpenClaw Gateway。功能范围与协议依据见 [Gateway 远程管理设计](gateway-management.md)。本页记录 Harness 5.3.12 的网页辅助适配，**不代表已完成 Hermes 原生 Gateway 接入**。

Harness 5.3.12（2026-11-26）仅为 **Hermes Agent** 和 **OpenClaw** 提供 Agent 专用适配。其他产品，包括 DeepSeek，统一作为普通 Web 应用承载。发布范围见 [5.3.12 发布说明](releases/5.3.12.md)。

## 如何配置

在设置 → 环境中填写实际部署的 HTTP(S) 网页地址，再选择应用类型：

| 类型 | 行为 |
|---|---|
| Hermes Agent | 识别官方 `hermes webapp` 浏览器界面，读取当前模型与运行状态，并通过当前聊天输入区的停止按钮取消运行 |
| OpenClaw Gateway | 读取 Control UI 的会话、模型和聊天状态；可另行启用 Gateway v4 原生会话、模型和精确运行控制 |
| 普通 Web 应用 | 独立登录、窗口、托盘、快捷键、导航刷新和连接探测；不读取 Agent 状态或向页面发送 Agent 命令 |
| 自动检测 | 依据受支持界面的明确标记识别 Hermes/OpenClaw；未识别时使用普通 Web 行为。原生 OpenClaw Gateway 仍需手动选择 OpenClaw 并显式启用 |

[Hermes 官方站](https://hermes-agent.nousresearch.com/) 是产品与文档入口，不能代替您部署的聊天页面地址。Harness 不安装或启动 Hermes 服务，也不提供 Hermes 终端界面的原生重写。

Hermes 的适配对象是官方仓库 `apps/desktop` 中的浏览器版 **`hermes webapp`**。旧的 `hermes dashboard` PTY 页面及第三方 Hermes 前端可作为网页打开，但没有相同的模型、忙碌状态和停止控件契约；不能把它们视为已验证的专用适配。

OpenClaw 的独立 Gateway 设置只对 OpenClaw 显示。Hermes 不使用 OpenClaw 的 Token、配对命令、WebSocket 协议或专用探测路径。详见 [OpenClaw 集成](openclaw-integration.md)。

## 适配依据与边界

Hermes 实现核对了官方仓库提交 [`010f119d4829c28a955c2e91845c5dc8803de2ce`](https://github.com/NousResearch/hermes-agent/tree/010f119d4829c28a955c2e91845c5dc8803de2ce/apps/desktop)：

- 浏览器宿主标记 `html[data-hermes-desktop-host="browser"]` 用于确认产品身份。
- 主聊天输入区使用 `data-tour="composer"` 和 `data-slot="composer-root"`；模型来自该输入区的 `data-tour="model-pill"` 按钮。
- 运行取消只使用当前输入区可见且可用的停止按钮。没有停止能力时不向其他输入框写入 `/stop`。
- Harness 不读取 React 私有状态，也不猜测 Hermes 的后端 API。聊天、附件、模型切换与历史列表由官方网页负责。
- Hermes 的耗时工具运行不会仅因 30 秒没有 DOM 输出而触发 stale-busy 自动刷新。出现多个主聊天输入区时，停止控制保持不可用，避免停止错误会话。

专用页面改动或不同前端可能使模型/停止能力不可用；此时保留网页承载，不伪造连接或运行能力。普通网页中的模型名称、停止按钮、报错示例和正文文字不会作为 Agent 状态证据。

Hermes、普通 Web 及自动模式的 HTTP 延迟探测使用配置的页面 URL，不擅自补斜线或访问 OpenClaw 的 `__openclaw__/a2ui/`。显式 OpenClaw 使用其专用路径；专用路径出错会如实显示探测失败。

## 旧配置

保存的枚举值保持兼容：`Auto=0`、`OpenClaw=1`、旧 `DeepSeek=2`、`Generic=3`、新增 `Hermes=4`。旧 DeepSeek 配置读取后规范化为 Generic，保留环境 ID、名称、URL 和浏览器会话目录。后端选择列表与持久化枚举值独立，避免把旧 DeepSeek 误解释成 Hermes。

变更应用类型会重建网页桥接状态。同一 URL 切换类型也会重启延迟探测，避免继续使用上一种后端的路径。

## 验证

自动回归覆盖配置迁移、后端切换、页面身份识别、模型与停止状态，以及普通网页不触发 Agent 命令。OpenClaw 原生回归使用回环 Gateway 和产品 ViewModel；脚本模拟与平台替身不能代替真实服务联调。

实际部署验收仍应检查：

1. 在官方 Hermes webapp 登录，确认模型展示；提交任务后查看忙碌状态，停止后确认服务端确实停止。
2. 切换会话、模型、语言以及重新加载页面，确认状态与停止能力跟随当前页面，草稿不会被覆盖。
3. 将同一 URL 改为“普通 Web 应用”，确认不再显示 Agent 模型或执行 Agent 停止命令。
4. 打开普通网页，包含 `Stop`、模型名称或 OpenClaw 错误示例时仍只按网页处理。
5. 对 OpenClaw 启用原生 Gateway，检查会话浏览、模型修改及按运行 ID 停止；切回 Hermes 或普通 Web 后关闭原生控制。

当前尚未连接用户的真实 Hermes 或 OpenClaw 部署，因此不声称已完成登录、网络代理、流式输出和服务端取消的端到端验收。
