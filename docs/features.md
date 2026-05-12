# DotCraft 特色功能

DotCraft 不只是一个对话入口。它是围绕项目工作区运行的 Agent Harness，会把会话、记忆、技能、自动化维护、多协议适配、集成 SDK 和社交渠道连接放在同一个项目上下文里持续积累。

## 项目记忆自动整理

DotCraft 会把成功会话中的稳定信息沉淀为工作区长期记忆和历史记录。后续会话可以继续理解项目背景、用户偏好、近期决策和反复出现的问题，而不是每次都从零开始。

这适合长期项目协作：需求讨论、代码修改、排障过程和约定都可以逐步进入同一份项目上下文。

相关入口：[快速开始](./getting-started.md) · [Desktop 指南](./desktop_guide.md)

## Skill 自学习与安全变体

DotCraft 允许 agent 将成功经验保存为工作区 skill，让可复用流程变成之后可以调用的操作知识。对于已有 skill，学到的改进会优先作为工作区安全适配保存，避免直接破坏原始 skill。

如果某个适配不再合适，用户可以恢复原始 skill。这样自学习带来的收益可以保留，风险也有清晰的回退路径。

相关入口：[Agent Skill 自学习](./skills/agent-self-learning.md) · [Skills 搜索与安装](./skills/marketplace.md)

## Dreams 后台记忆整理

Dreams 是 DotCraft 的后台工作区记忆整理能力。AppServer 运行时，它可以定时检查近期工作区活动，生成可审阅的被动项目记忆 store；用户在 Dashboard 应用后，这些内容才会进入后续会话的低权威上下文。开启自动更新梦境后，未来成功运行会自动应用为 active Dream store。

Dreams 不替代 `.craft/memory/MEMORY.md` 和 `HISTORY.md` 的显式长期记忆，而是补充一个低打扰的后台整理循环，用来保留近期焦点、开放问题、项目约定和需要避免的低信号上下文。

相关入口：[Desktop 指南](./desktop_guide.md) · [Automations 指南](./automations_guide.md)

## 多协议适配（API + AG-UI）

DotCraft 可以把同一个工作区能力投射到不同协议形态。API 模式提供 OpenAI-compatible HTTP 接口，适合接入已有工具链、服务端应用或脚本；AG-UI 模式通过事件流接入前端 agent UI，让 CopilotKit 等界面复用 DotCraft 的运行时能力。

这些协议入口共享底层配置、模型、工具和工作区上下文，不需要为每个客户端重新维护一套 agent 流程。

相关入口：[API 模式指南](./api_guide.md) · [AG-UI 模式指南](./agui_guide.md)

## 面向集成的协议与 SDK

DotCraft 为外部集成提供从本机运行时到客户端协议的完整路径。Hub 用于发现和管理本机工作区运行时；AppServer Protocol 通过 JSON-RPC over stdio/WebSocket 暴露会话、事件、审批和工作区能力；Python 与 TypeScript SDK 用于构建客户端、机器人和外部适配器。

这让 DotCraft 可以作为项目级 agent runtime 被嵌入到 IDE、桌面应用、自动化系统、社交机器人或自定义服务中。

相关入口：[Hub 指南](./hub_guide.md) · [AppServer 指南](./appserver_guide.md) · [AppServer Protocol](./reference/appserver-protocol.md) · [Python SDK](./sdk/python.md) · [TypeScript SDK](./sdk/typescript.md)

## 一键连接社交渠道

DotCraft Desktop 的渠道管理可以发现内置和外部社交渠道模块。填写平台需要的 token、回调地址、白名单或扫码认证后，可以一键启用 QQ、企业微信、飞书、Telegram、微信等渠道。

这些渠道复用同一个工作区和会话核心：机器人、Desktop、终端和自动化任务可以围绕同一项目上下文协作，而不是各自维护分散的 agent 状态。

相关入口：[SDK 总览](./sdk/index.md) · [TypeScript SDK](./sdk/typescript.md) · [QQ](./sdk/typescript-qq.md) · [企业微信](./sdk/typescript-wecom.md) · [飞书](./sdk/typescript-feishu.md) · [Telegram](./sdk/typescript-telegram.md) · [微信](./sdk/typescript-weixin.md)
