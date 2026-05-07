using DotCraft.Agents;
using DotCraft.Protocol;
using Microsoft.Extensions.AI;

namespace DotCraft.Context;

/// <summary>
/// Builds the system reminder runtime context block that is appended to each user message.
/// Keeping dynamic values (time, per-message sender) out of the system prompt
/// ensures the system prompt prefix stays stable across requests, enabling
/// LLM prompt cache reuse.
/// </summary>
public static class RuntimeContextBuilder
{
    /// <summary>
    /// Appends a system reminder <see cref="TextContent"/> with optional turn initiator and mode metadata.
    /// </summary>
    public static IList<AIContent> AppendRuntimeContext(
        this IList<AIContent> contents,
        TurnInitiatorContext? initiator = null,
        AgentModeManager? modeManager = null,
        string? workspacePath = null,
        bool hasActivePlan = false)
    {
        var block = BuildBlock(initiator, modeManager, workspacePath, hasActivePlan);
        contents.Add(new TextContent($"\n{block}"));
        if (modeManager?.JustSwitchedFromPlan == true)
            modeManager.AcknowledgeTransition();
        return contents;
    }

    internal static string BuildBlock(
        TurnInitiatorContext? initiator = null,
        AgentModeManager? modeManager = null,
        string? workspacePath = null,
        bool hasActivePlan = false)
    {
        var mode = modeManager?.CurrentMode ?? AgentMode.Agent;
        var transition = modeManager?.JustSwitchedFromPlan == true ? "PlanToAgent" : "None";
        var actionProfile = mode == AgentMode.Plan ? "ReadOnlyObserve" : "FullWorkspace";
        var planState = hasActivePlan ? "Active" : "Absent";
        var runtimeLines = new List<string>();

        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm (dddd)");
        runtimeLines.Add($"CurrentTime: {now}");
        runtimeLines.Add($"TimeZone: {TimeZoneInfo.Local.DisplayName}");
        if (!string.IsNullOrWhiteSpace(workspacePath))
            runtimeLines.Add($"WorkingDirectory: {Path.GetFullPath(workspacePath)}");
        runtimeLines.Add($"CurrentMode: {mode}");
        runtimeLines.Add($"ModeTransition: {transition}");
        runtimeLines.Add($"AllowedActionProfile: {actionProfile}");
        runtimeLines.Add($"PlanState: {planState}");

        var sections = new List<string>
        {
            "## Runtime Context\n" + string.Join("\n", runtimeLines)
        };

        var providerLines = ChatContextRegistry.All
            .SelectMany(provider => provider.GetRuntimeContextLines())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();
        if (providerLines.Count > 0)
            sections.Add("## Additional Runtime Context\n" + string.Join("\n", providerLines));

        var initiatorLines = BuildInitiatorLines(initiator);
        if (initiatorLines.Count > 0)
            sections.Add("## Turn Initiator\n" + string.Join("\n", initiatorLines));

        if (transition == "PlanToAgent")
        {
            sections.Add(
"""
## Mode Transition
You have exited plan mode for this turn. You now have full workspace access subject to the normal approval and sandbox policy.
""");
        }

        sections.Add("## Mode Action\n" + BuildModeAction(mode, transition, hasActivePlan));

        return "<system-reminder>\n" + string.Join("\n\n", sections) + "\n</system-reminder>";
    }

    private static List<string> BuildInitiatorLines(TurnInitiatorContext? initiator)
    {
        var lines = new List<string>();
        if (initiator is null)
            return lines;

        AddLine(lines, "Channel", initiator.ChannelName);
        AddLine(lines, "ChannelContext", initiator.ChannelContext);
        AddLine(lines, "SenderId", initiator.UserId);
        AddLine(lines, "SenderName", initiator.UserName);
        AddLine(lines, "SenderRole", initiator.UserRole);
        AddLine(lines, "GroupChatId", initiator.GroupId);
        return lines;
    }

    private static void AddLine(List<string> lines, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            lines.Add($"{label}: {value}");
    }

    private static string BuildModeAction(AgentMode mode, string transition, bool hasActivePlan)
    {
        if (mode == AgentMode.Plan)
        {
            return
"""
Plan mode is active. You must not edit files, run mutating commands, change configuration, commit, push, or otherwise modify the workspace. Read, search, reason, ask clarifying questions when useful, and call CreatePlan when the implementation plan is ready.
""";
        }

        if (transition == "PlanToAgent")
        {
            return
"""
Follow the active or approved plan. Before starting each planned task, update its progress when task tracking is active. After completing each task, mark it completed. Use workspace-changing tools when appropriate and continue until the planned work is complete or blocked.
""";
        }

        if (hasActivePlan)
        {
            return
"""
Agent mode is active and a saved plan is available. Follow the plan when it applies, keep task progress current for non-trivial work, and use workspace-changing tools when appropriate under the normal approval and sandbox policy.
""";
        }

        return
"""
Agent mode is active. You may use read, write, shell, and other configured tools when appropriate under the normal approval and sandbox policy. Use task tools only when the work genuinely benefits from structured tracking.
""";
    }
}
