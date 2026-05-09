# DotCraft 完整配置参考

本页集中列出配置字段。第一次配置请先读 [配置指南](../config_guide.md)，安全策略请读 [安全配置](../config/security.md)。

## 基础配置

| 配置项 | 说明 | 默认值 |
|--------|------|--------|
| `ApiKey` | OpenAI-compatible API Key | 空 |
| `Model` | 默认模型名称 | `gpt-4o-mini` |
| `EndPoint` | API 端点地址 | `https://api.openai.com/v1` |
| `Language` | 界面语言：`Chinese` / `English` | `Chinese` |
| `MaxToolCallRounds` | 主 Agent 最大工具调用轮数 | `100` |
| `SubagentMaxToolCallRounds` | 子 Agent 最大工具调用轮数 | `50` |
| `SubagentMaxConcurrency` | 最大并发子 Agent 数量 | `3` |
| `MaxSessionQueueSize` | 每个 Session 最大排队请求数；`0` 表示无限制 | `3` |
| `ConsolidationModel` | 记忆整合专用模型，空值使用主模型 | 空 |
| `DebugMode` | 控制台不截断工具调用参数输出 | `false` |
| `EnabledTools` | 全局启用的工具名称列表，为空时启用所有工具 | `[]` |

## Memory

| 配置项 | 说明 | 默认值 |
|--------|------|--------|
| `Memory.AutoConsolidateEnabled` | 启用长期记忆自动沉淀 | `true` |
| `Memory.ConsolidateEveryNTurns` | 每个线程成功完成多少轮后触发一次长期记忆沉淀 | `5` |

## Compaction

| 配置项 | 说明 | 默认值 |
|--------|------|--------|
| `Compaction.AutoCompactEnabled` | 启用基于阈值的自动压缩 | `true` |
| `Compaction.ReactiveCompactEnabled` | 启用对 `prompt_too_long` 错误的反应式压缩 | `true` |
| `Compaction.ContextWindow` | 模型上下文窗口（Token）。未显式配置时，会按当前有效模型（含线程级 model override）从模型上下文窗口映射表推导；未知模型使用 `256000` | 模型映射值 / `256000` |
| `Compaction.MaxContextWindow` | 从模型映射表推导上下文窗口时使用的上限；显式配置 `Compaction.ContextWindow` 时保留显式值 | `256000` |
| `Compaction.SummaryReserveTokens` | 为摘要输出预留的 Token | `20000` |
| `Compaction.AutoCompactBufferTokens` | 低于硬上限多少 Token 时触发自动压缩 | `13000` |
| `Compaction.WarningBufferTokens` | 到达自动阈值前多少 Token 发出 warning | `20000` |
| `Compaction.ErrorBufferTokens` | 到达自动阈值前多少 Token 发出 error | `10000` |
| `Compaction.ManualCompactBufferTokens` | 低于硬上限多少 Token 时仍允许手动 `/compact` | `3000` |
| `Compaction.KeepRecentMinTokens` | 局部摘要后尾部至少保留的 Token 数 | `10000` |
| `Compaction.KeepRecentMinGroups` | 局部摘要后尾部至少保留的 API 轮次数 | `3` |
| `Compaction.KeepRecentMaxTokens` | 局部摘要后尾部最多保留的 Token 数 | `40000` |
| `Compaction.MicrocompactEnabled` | 启用微压缩 | `true` |
| `Compaction.MicrocompactTriggerCount` | 可压缩工具结果数量达到该值时触发微压缩 | `30` |
| `Compaction.MicrocompactKeepRecent` | 微压缩时保留的最近工具结果数 | `8` |
| `Compaction.MicrocompactGapMinutes` | 距离上次助理消息超过该分钟数也触发微压缩；`0` 表示禁用 | `20` |
| `Compaction.MaxConsecutiveFailures` | 连续失败次数达到该值时熔断 | `3` |

### 模型上下文窗口映射

DotCraft 内置一份常见模型的上下文窗口映射表，并会在 `Compaction.ContextWindow` 未显式配置时按当前有效模型自动选择；线程级 model override 会覆盖工作区默认模型参与匹配。推导出的模型窗口会被 `Compaction.MaxContextWindow` 限制，默认最高使用 `256000`。可以通过 JSON 文件补充或覆盖映射：

- 全局：`~/.craft/model-context-windows.json`
- 工作区：`.craft/model-context-windows.json`

工作区映射优先于全局映射；两者都会覆盖内置映射。模型名按最长前缀匹配，因此 `gpt-4o` 可匹配 `gpt-4o-mini`，`kimi-k2-` 可匹配同系列后续版本。OpenAI-compatible 网关常见的 `provider/model-name`、`provider/org/model-name` 形式会额外尝试匹配斜杠后的各级后缀。

```json
{
  "defaultContextWindow": 256000,
  "models": {
    "my-256k-model": 256000,
    "custom-long-context-": 1048576
  }
}
```

## Reasoning

| 配置项 | 说明 | 默认值 |
|--------|------|--------|
| `Reasoning.Enabled` | 是否请求 Provider 的推理支持 | `false` |
| `Reasoning.Effort` | 推理深度：`None` / `Low` / `Medium` / `High` / `ExtraHigh` | `Medium` |
| `Reasoning.Output` | 推理内容是否暴露在响应中：`None` / `Summary` / `Full` | `Full` |

使用 OpenAI-compatible DeepSeek endpoint 时，只要 `EndPoint` host 或 `Model` 包含 `deepseek`，DotCraft 会自动启用 DeepSeek thinking/tool-call 兼容层。工具调用轮次里，上一条 assistant 的 `reasoning_content` 会随 `content` 和 `tool_calls` 一起回传，满足 DeepSeek thinking mode 的要求；不需要新增 `Provider` 配置。

```json
{
  "ApiKey": "sk-...",
  "EndPoint": "https://api.deepseek.com/v1",
  "Model": "deepseek-reasoner",
  "Reasoning": {
    "Enabled": true,
    "Effort": "Medium",
    "Output": "Full"
  }
}
```

## PromptCaching

用于无法修改 LiteLLM proxy 配置时，在 DotCraft 客户端侧为 OpenAI-compatible Claude 请求注入 Anthropic/LiteLLM prompt cache marker。DotCraft 会把 `cache_control` 放到当前请求尾部的最后一个可缓存文本位置上：优先标记最新 user request 或 assistant response，跳过 tool result 以避免改变工具结果的序列化形态。单文本消息会保持原始字符串内容；已有 content-block 数组的消息会在文本 block 上附加 marker。v1 每次只放一个 tail breakpoint；如果单轮产生超过约 20 个 content blocks，后续可能需要更细的多 breakpoint 策略。

| 配置项 | 说明 | 默认值 |
|--------|------|--------|
| `PromptCaching.Enabled` | 是否为匹配模型注入 prompt cache marker | `true` |
| `PromptCaching.ModelPatterns` | 大小写不敏感的模型名片段；为空则不匹配任何模型 | `["claude"]` |
| `PromptCaching.Placement` | marker 放置策略；当前仅支持 `ConversationTail` | `ConversationTail` |
| `PromptCaching.Ttl` | Anthropic cache TTL；为空使用默认 5 分钟，`1h` 使用长缓存 | 空 |

## 入口与服务

| 配置项 | 说明 | 默认值 |
|--------|------|--------|
| `Api.Enabled` | 是否启用 API 模式 | `false` |
| `Api.Host` | HTTP 服务监听地址 | `127.0.0.1` |
| `Api.Port` | HTTP 服务监听端口 | `8080` |
| `Api.ApiKey` | API 访问密钥（Bearer Token），为空时不验证 | 空 |
| `Api.AutoApprove` | 是否自动批准所有文件/Shell 操作 | `true` |
| `AgUi.Enabled` | 是否启用 AG-UI 服务 | `false` |
| `AgUi.Host` | HTTP 服务监听地址 | `127.0.0.1` |
| `AgUi.Port` | HTTP 服务监听端口 | `5100` |
| `AgUi.Path` | SSE 端点路径 | `/ag-ui` |
| `AgUi.RequireAuth` | 是否启用 Bearer Token 认证 | `false` |
| `AgUi.ApiKey` | Bearer Token 值 | 空 |
| `AgUi.ApprovalMode` | 工具操作审批模式：`interactive` / `auto` | `interactive` |
| `Acp.Enabled` | 是否启用 ACP 模式 | `false` |
| `DashBoard.Enabled` | 是否启用 Dashboard | `false` |
| `DashBoard.Host` | Dashboard 监听地址 | `127.0.0.1` |
| `DashBoard.Port` | Dashboard 监听端口 | `8080` |
| `Gateway.Enabled` | 是否启用 Gateway Host | `false` |

## 自动化与工作流

| 配置项 | 说明 | 默认值 |
|--------|------|--------|
| `Automations.Enabled` | 是否启用 Automations 编排器 | `true` |
| `Automations.LocalTasksRoot` | 本地任务根目录，留空使用 `.craft/tasks/` | 空 |
| `Automations.PollingInterval` | 轮询间隔 | `00:00:30` |
| `Automations.MaxConcurrentTasks` | 本地任务最大并发数 | `3` |
| `Automations.TurnTimeout` | 单轮对话超时时间 | `00:30:00` |
| `Automations.StallTimeout` | 停顿超时时间 | `00:10:00` |
| `Automations.MaxRetries` | 最大重试次数 | `3` |
| `Automations.RetryInitialDelay` | 重试初始延迟 | `00:00:30` |
| `Automations.RetryMaxDelay` | 重试最大延迟 | `00:10:00` |
| `Hooks.Enabled` | 是否启用 Hooks | `true` |
| `Hooks.Events` | Hook 事件配置列表 | `[]` |
| `Cron.Enabled` | 是否启用 Cron 定时任务服务 | `true` |
| `Heartbeat.Enabled` | 是否启用心跳服务 | `false` |
| `Heartbeat.IntervalSeconds` | 检查间隔（秒） | `1800` |
| `Heartbeat.NotifyAdmin` | 社交渠道下是否将结果通知管理员 | `true` |

## MCP 与 LSP

| 配置项 | 说明 | 默认值 |
|--------|------|--------|
| `McpServers` | MCP 服务配置集合 | `{}` |
| `Tools.DeferredLoading.Enabled` | 是否启用工具延迟加载 | `false` |
| `Tools.DeferredLoading.AlwaysLoadedTools` | 始终加载的工具名列表 | `[]` |
| `LspServers` | LSP 服务配置集合 | `{}` |
| `Tools.Lsp.Enabled` | 是否启用内置 LSP 工具 | `false` |

## SubAgent

入门说明和示例见 [SubAgent 配置指南](../subagents_guide.md)。

| 配置项 | 说明 | 默认值 |
|--------|------|--------|
| `SubAgent.MaxDepth` | session-backed SubAgent 最大生成深度；第一级子代理深度为 `1` | `1` |
| `SubAgent.Model` | DotCraft 原生 SubAgent 使用的模型；空值继承当前线程的有效主模型 | 空 |
| `SubAgent.EnableExternalCliSessionResume` | 是否允许支持 resume 的 external CLI profile 复用已保存外部会话 | `false` |
| `SubAgent.DisabledProfiles` | 当前工作区隐藏和禁用的 SubAgent profile 名称列表 | `[]` |
| `SubAgent.Roles` | 工作区自定义 SubAgent role；同名条目覆盖内置 role | `[]` |

`SubAgent.Roles` 的条目字段：

| 字段 | 说明 |
|------|------|
| `Name` | role 名称，也是 `SpawnAgent.agentRole` 使用的值 |
| `Description` | role 简短说明，会暴露给主 Agent |
| `ToolAllowList` | 精确工具允许列表；为空表示不额外限制候选工具 |
| `ToolDenyList` | 精确工具拒绝列表，会在工具集合构建完成后移除 |
| `AgentControlToolAccess` | AgentTools 策略：`Disabled` / `Full` / `AllowList` |
| `AllowedAgentControlTools` | `AgentControlToolAccess` 为 `AllowList` 时允许的 AgentTools 名称 |
| `PromptProfile` | 原生 SubAgent 提示词 profile：`subagent-light` / `full` |
| `Instructions` | 追加到 SubAgent prompt 的 role instructions |
| `Mode` | 可选 mode 覆盖 |
| `Model` | 可选 model 覆盖 |
| `OverrideBasePrompt` | 是否用 `Instructions` 覆盖基础 prompt；默认追加而不是覆盖 |

## 外部渠道

QQ、企业微信等 TypeScript 外部渠道通过 `ExternalChannels` 注册：

```json
{
  "AppServer": {
    "Mode": "WebSocket",
    "WebSocket": {
      "Host": "127.0.0.1",
      "Port": 9100,
      "Token": ""
    }
  },
  "ExternalChannels": {
    "qq": {
      "enabled": true,
      "transport": "websocket"
    },
    "wecom": {
      "enabled": true,
      "transport": "websocket"
    }
  }
}
```

平台连接、权限白名单和审批超时等渠道专属设置分别放在 `.craft/qq.json`、`.craft/wecom.json` 等适配器配置文件中。

## 自定义命令

`CustomCommands` 可把常用提示词或工作流保存为命令。命令内容通常放在工作区 `.craft/commands/` 或对应配置项中，供 CLI、Desktop 或其他入口复用。
