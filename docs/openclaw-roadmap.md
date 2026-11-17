# OpenClaw 深度整合路线

审查日期：2026-11-16。目标是轻量 OpenClaw 客户端：保留 WebView2 承载上游聊天、附件和工具界面，原生侧负责跨会话操作、任务状态、连接诊断和桌面体验。

## 本轮已处理

- 会话切换期间保留终态与列表变化信号，按连接、会话和目标代次重新协调。
- 临时读请求失败保留快照，有限退避重试；未确认的新鲜状态不能用于修改会话或精准停止。
- 区分服务器模型/推理设置与用户未提交草稿，避免刷新后把旧值重新写回服务器。
- 模型手动选择遵守协商的 `model-selection-policy`，不把缺失的许可字段理解成允许。
- 会话搜索使用 `sessions.list(search)`，分页携带相同查询；旧查询结果和错误不能覆盖新查询。
- 多实例配置合并后统一应用运行时设置；空环境退役旧网页；清理浏览器会话失败不再显示成功。
- WebView2 同源弹窗复用导航恢复路径；初始化等待有总超时，晚到结果不能接管新宿主。
- Explorer 重启后恢复托盘，恢复失败时显示隐藏的主窗口。

这些改动属于本地工作区预览，不表示已发布新的 GitHub Release。

## 下一阶段优先级

| 顺序 | 功能 | Gateway 依据与实现边界 | 验收重点 |
| --- | --- | --- | --- |
| 1 | 跨会话任务列表 | 以 `sessions.list` 的 `hasActiveRun`、`activeRunIds` 和订阅事件协调运行状态；可支持的部署再启用 `activeOnly`、`agentId`、`includeActivitySummary` | 后台会话结束也更新；重连后以快照修复；排队、运行、完成、失败含义明确；只有确定的 runId 可以停止 |
| 2 | 通知点击定位会话 | 通知携带环境 ID、完整 session key、物理 session ID；通过现有会话 URL 契约导航 | 切环境、会话重置、删除后不误跳；有草稿/附件时先处理草稿；不以标题作为身份 |
| 3 | Agent / 运行中 / 归档 / 置顶筛选 | 扩展 `sessions.list` 类型化查询，按实际部署 schema 和能力启用字段；筛选只改变列表视图 | 服务端筛选在分页前生效；清空/改变筛选取消旧查询；当前聊天和列表筛选相互独立 |
| 4 | 连接诊断 | 分别呈现网页、原生 WebSocket、Cloudflare Access、Gateway 身份、设备配对及协议能力 | 能区分入口 401/403、配对等待、scope 不足、网络断线；诊断草稿连接不更改已保存凭据 |
| 5 | 模型状态解释 | 展示 `available`、`unavailableReason`、`unavailableUntil`、上下文限制和服务端思考选项 | 不可用模型可解释但不能误选；冷却时间到期重新读取；不凭模型名称推测能力 |

任务列表先覆盖当前连接、当前环境。跨直连与 Cloudflare 环境去重需要明确它们是否属于同一 Gateway，不能按 URL 相似度自动合并凭据和任务身份。

## 协议与实现约束

- 当前客户端针对协议 v4。相同 wire version 不保证新增 RPC 字段都存在；以 `hello.features.methods`、能力协商、实际发行版 schema 和返回 scope 共同决定可用功能。
- `sessions.subscribe` 可返回初始列表；后续可重用这份快照减少重复读。事件订阅的覆盖范围不能由列表筛选条件推断。
- `sessions.changed` 与 chat/agent 事件需要幂等归并。流式 delta 不应触发每条消息都刷新全部列表或模型目录。
- 模型目录应独立于会话历史刷新，按 agent/session scope 缓存并失效，避免频繁切换会话等待模型发现。
- 网页认证、Cloudflare Access 与原生 Gateway 设备身份分别诊断。不得默认网页已登录就意味着原生连接已配对。
- 浏览器 profile 后续应按稳定环境 ID 管理，迁移旧名称目录前等待旧宿主释放。该改造尚未实施。
- Agent 管理、定时任务编辑和服务端配置写入放在上述日常功能稳定之后，按 scope 与 schema 单独设计；不通过通用 JSON 编辑器绕过权限或版本约束。

## 验证与仍需实机覆盖

自动化覆盖 Core、实际 ViewModel 与回环 Gateway、确定性设置/WebView/托盘生命周期，以及原生 WebView2 桥接。平台替身无法证明真实 Explorer 重启、浏览器进程挂死、WinUI 动画流畅度或 VPS/Cloudflare 授权体验。

实际部署验收应包括：直连与 Cloudflare 两个环境；已认证页面切换会话、生成时切换、精准停止；其他客户端修改模型；短暂断网后的恢复；有草稿时切换；设置保存/关闭与退出；清理被占用 profile 的提示。

## 参考

- [官方文档](https://docs.openclaw.ai/)
- [Gateway 协议](https://docs.openclaw.ai/gateway/protocol)
- [握手、身份和权限](https://docs.openclaw.ai/gateway/protocol/handshake)
- [会话控制 RPC](https://docs.openclaw.ai/gateway/protocol/rpc-session-control)
- [当前固定的上游源码](https://github.com/openclaw/openclaw/tree/7ecb9cec67740e1afedc0a8e20542c6cfa456358)
- [已有集成说明](openclaw-integration.md)
