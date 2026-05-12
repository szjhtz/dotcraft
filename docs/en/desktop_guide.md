# DotCraft Desktop Guide

Desktop is the recommended first entry point for DotCraft. It works as an AppServer client and provides a visual UI for workspaces, sessions, diffs, plans, model configuration, automation review, and runtime status.

## Quick Start

### Use a Release

1. Download an installer from [GitHub Releases](https://github.com/DotHarness/dotcraft/releases).
2. Start DotCraft Desktop.
3. Choose a project folder as the workspace.
4. Configure an OpenAI-compatible API key or CLIProxyAPI endpoint.
5. Create a session and send the first request.

### Run from Source

```bash
cd desktop
npm install
npm run dev
```

When running from source, Desktop looks for `dotcraft` on `PATH`. If it cannot find it, set the AppServer / `dotcraft` binary path in settings.

## Configuration

Common Desktop settings:

| Setting | Description |
|---------|-------------|
| Workspace | Current project folder; `.craft/` state is stored here |
| AppServer binary | `dotcraft` or a custom AppServer binary path |
| Model provider | OpenAI-compatible API key, model name, endpoint |
| Dashboard / Automations | Visual debugging and automation review |

In **Settings -> Personalization**, you can enable or disable long-term memory and Dreams. Long-term memory consolidates explicit session memory during conversation turns. Dreams periodically organize passive workspace context in the background, even when no conversation is open.

The Dreams section shows the latest run state and includes **Run now** plus **Auto-update Dreams**. With auto-update off, new Dreams runs create pending stores first. With auto-update on, future successful runs automatically become the active Dream store. **Manage Dreams** in Desktop shows lightweight run history and opens the Dashboard review page for each run; detailed diff, trace, apply, discard, cancel, and archive actions live in Dashboard. **Reset memory** deletes the current workspace's `MEMORY.md`, `HISTORY.md`, Dreams stores under `.craft/dreams`, and derived memory caches. Resetting memory does not delete sessions, configuration, skills, or automation tasks.

You can also override startup values:

```bash
DotCraft Desktop --app-server /path/to/dotcraft
DotCraft Desktop --workspace /path/to/project
```

## Usage Examples

| Scenario | Desktop path |
|----------|--------------|
| First-time setup | Choose workspace -> configure model -> create session |
| Inspect agent work | Open session detail, diff, trace, or Dashboard |
| Review automation tasks | Open Automations and inspect pending review items |
| Switch projects | Choose another workspace so config and tasks stay project-scoped |

## Advanced Topics

- Desktop consumes the AppServer Wire Protocol, so it can share the same session core with TUI, ACP, and external channels.
- Image attachments are stored under `.craft/attachments/images/` and can be restored after restart.
- Installer builds can be produced with `npm run dist`; outputs are written under `desktop/dist/`.

## Troubleshooting

### No session is available after startup

Confirm a workspace is selected and the AppServer / `dotcraft` binary can run.

### Settings changes do not apply

Model and workspace fields usually affect new sessions immediately. AppServer, port, and entry-point settings require restarting Desktop or the background host.

### The Automations panel is empty

Use Gateway or a host that loads Automations, and enable the relevant task source in configuration. See the [Automations Guide](./automations_guide.md).
