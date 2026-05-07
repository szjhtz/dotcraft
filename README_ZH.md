<div align="center">

# DotCraft

[English](./README.md) · [官方文档](https://dotharness.github.io/dotcraft/) · [快速开始](https://dotharness.github.io/dotcraft/getting-started) · [下载 Release](https://github.com/DotHarness/dotcraft/releases) · [DeepWiki](https://deepwiki.com/DotHarness/dotcraft) · [License](./LICENSE)

最适合您项目的 Agent Harness。你想要的所有功能尽在工作区内。

![intro](https://github.com/DotHarness/resources/raw/master/dotcraft/intro.png)

</div>

## 简介

DotCraft 是一个 .NET 10 / C# Agent Harness。它围绕真实项目目录组织 AI 工作流，让多个入口共享同一套会话核心、配置、技能、工具、任务和可观测能力。

- 项目为先：插件、技能、会话与记忆随项目走，Agent 更能理解你的项目。
- 统一会话模型：CLI、Desktop、TUI 聊天机器人等等，所有应用复用同一执行引擎。
- 可观测与治理：审批、Trace、Dashboard、Hooks 和沙箱配置让 agent 工作流更容易检查和约束。
- 扩展与集成：AppServer、SDK 与插件体系支持自定义入口和业务工作流。

## 快速开始

第一次使用建议从 Desktop 开始：

1. 前往 [GitHub Releases](https://github.com/DotHarness/dotcraft/releases) 下载桌面应用。
2. 选择一个真实项目目录作为工作区。
3. 配置 OpenAI-compatible API Key 或 CLIProxyAPI。
4. 新建会话，发送你的第一次仓库理解请求。

完整图文流程见 [快速开始](https://dotharness.github.io/dotcraft/getting-started)。

## 文档

| 目标 | 文档 |
|------|------|
| 第一次安装、配置和运行 | [快速开始](https://dotharness.github.io/dotcraft/getting-started) |
| 使用图形化桌面客户端 | [Desktop 指南](https://dotharness.github.io/dotcraft/desktop_guide) |
| 在终端里使用完整界面 | [TUI 指南](https://dotharness.github.io/dotcraft/tui_guide) |
| 配置模型、工具、审批和安全策略 | [配置指南](https://dotharness.github.io/dotcraft/config_guide) |
| 查看 Trace、工具调用和配置合并结果 | [Dashboard 指南](https://dotharness.github.io/dotcraft/dash_board_guide) |
| 运行本地自动化任务 | [Automations 指南](https://dotharness.github.io/dotcraft/automations_guide) |
| 接入外部客户端、机器人或自定义适配器 | [SDK 总览](https://dotharness.github.io/dotcraft/sdk/) |
| 查找完整文档路径 | [文档索引](https://dotharness.github.io/dotcraft/reference) |

## 贡献代码

欢迎提交代码、文档与集成相关贡献。开始前请阅读 [CONTRIBUTING.md](./CONTRIBUTING.md)。

## 致谢

本项目受 [nanobot](https://github.com/HKUDS/nanobot) 与 [codex](https://github.com/openai/codex) 启发，并构建在 [agent-framework](https://github.com/microsoft/agent-framework) 之上。

特别感谢：

- [HKUDS/nanobot](https://github.com/HKUDS/nanobot)
- [openai/codex](https://github.com/openai/codex)
- [microsoft/agent-framework](https://github.com/microsoft/agent-framework)
- [alibaba/OpenSandbox](https://github.com/alibaba/OpenSandbox)
- [modelcontextprotocol/csharp-sdk](https://github.com/modelcontextprotocol/csharp-sdk)
- [openai/symphony](https://github.com/openai/symphony)
- [router-for-me/CLIProxyAPI](https://github.com/router-for-me/CLIProxyAPI)

## License

Apache License 2.0
