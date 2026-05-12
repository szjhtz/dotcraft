# DotCraft Features

DotCraft is more than a chat entry point. It is a project-scoped Agent Harness that keeps sessions, memory, skills, automation maintenance, multi-protocol adapters, integration SDKs, and social channel connections inside one durable workspace context.

## Automatic Project Memory

DotCraft can turn stable information from successful sessions into workspace long-term memory and historical records. Later sessions can continue with project background, user preferences, recent decisions, and recurring problems instead of starting from zero.

This is designed for long-running project work: product discussions, code changes, debugging paths, and team conventions can gradually become part of the shared project context.

Related docs: [Getting Started](./getting-started.md) · [Desktop Guide](./desktop_guide.md)

## Self-Learning Skills with Safe Variants

DotCraft lets agents save successful workflows as workspace skills, so reusable procedures become operational knowledge for future tasks. For existing skills, learned improvements are kept as workspace-safe adaptations first, avoiding accidental damage to the original skill.

If an adaptation stops being useful, users can restore the original skill. Self-learning can improve repeated work while keeping a clear recovery path.

Related docs: [Agent Skill Self-Learning](./skills/agent-self-learning.md) · [Search and Install Skills](./skills/marketplace.md)

## Dreams for Background Memory Organization

Dreams is DotCraft's background workspace memory organization capability. While AppServer is running, it can periodically inspect recent workspace activity and generate reviewable passive project memory stores. Those stores enter future low-authority context only after the user applies them in Dashboard. When auto-update Dreams is enabled, future successful runs are automatically applied as the active Dream store.

Dreams does not replace explicit long-term memory in `.craft/memory/MEMORY.md` and `HISTORY.md`. It adds a low-friction background loop for recent focus areas, open questions, project conventions, and low-signal context that future agents should ignore.

Related docs: [Desktop Guide](./desktop_guide.md) · [Automations Guide](./automations_guide.md)

## Multi-Protocol Adaptation (API + AG-UI)

DotCraft can project the same workspace capability through multiple protocol shapes. API mode provides an OpenAI-compatible HTTP interface for existing toolchains, server apps, and scripts. AG-UI mode connects frontend agent UIs through event streams, so interfaces such as CopilotKit can reuse DotCraft's runtime capabilities.

These protocol entry points share the same underlying configuration, models, tools, and workspace context instead of requiring a separate agent loop for each client.

Related docs: [API Mode Guide](./api_guide.md) · [AG-UI Mode Guide](./agui_guide.md)

## Integration-Ready Protocols and SDKs

DotCraft provides a complete path from local runtime management to client protocols. Hub discovers and manages local workspace runtimes. AppServer Protocol exposes sessions, events, approvals, and workspace capability over JSON-RPC via stdio or WebSocket. The Python and TypeScript SDKs help build clients, bots, and external adapters.

This lets DotCraft act as a project-level agent runtime embedded into IDEs, desktop apps, automation systems, social bots, or custom services.

Related docs: [Hub Guide](./hub_guide.md) · [AppServer Guide](./appserver_guide.md) · [AppServer Protocol](./reference/appserver-protocol.md) · [Python SDK](./sdk/python.md) · [TypeScript SDK](./sdk/typescript.md)

## One-Click Social Channel Connection

DotCraft Desktop's channel management can discover bundled and external social channel modules. After filling in the platform credentials, callback URLs, allowlists, or QR authentication required by each platform, you can enable QQ, WeCom, Feishu, Telegram, Weixin, and other channels with one click.

These channels reuse the same workspace and session core: bots, Desktop, terminal, and automation tasks can collaborate around one project context instead of maintaining separate agent state.

Related docs: [SDK Overview](./sdk/index.md) · [TypeScript SDK](./sdk/typescript.md) · [QQ](./sdk/typescript-qq.md) · [WeCom](./sdk/typescript-wecom.md) · [Feishu](./sdk/typescript-feishu.md) · [Telegram](./sdk/typescript-telegram.md) · [Weixin](./sdk/typescript-weixin.md)
