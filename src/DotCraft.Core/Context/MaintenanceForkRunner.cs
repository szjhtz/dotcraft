using DotCraft.Tracing;
using Microsoft.Extensions.AI;

namespace DotCraft.Context;

/// <summary>
/// Maintenance task kinds that may run by forking a stable prompt request prefix.
/// </summary>
public enum MaintenanceForkTaskKind
{
    /// <summary>Summarize conversation context for history compaction.</summary>
    ContextCompaction,

    /// <summary>Extract durable user/project memory from recent conversation context.</summary>
    MemoryConsolidation
}

/// <summary>
/// A maintenance task appended to a prompt request snapshot.
/// </summary>
/// <param name="Kind">The task kind.</param>
/// <param name="Instructions">Task-specific instructions appended at the tail.</param>
public sealed record MaintenanceForkTask(
    MaintenanceForkTaskKind Kind,
    string Instructions);

/// <summary>
/// Result returned from a maintenance fork attempt.
/// </summary>
public sealed record MaintenanceForkResult(
    MaintenanceForkTaskKind TaskKind,
    string? Text,
    string? FallbackReason,
    TokenUsageSnapshot? TokenUsage);

/// <summary>
/// Runs provider-agnostic maintenance requests by reusing a captured prompt
/// request prefix and appending only a tail task message.
/// </summary>
public sealed class MaintenanceForkRunner(IChatClient chatClient, TraceCollector? traceCollector = null)
{
    /// <summary>
    /// Runs a maintenance fork and returns the assistant text, or a fallback reason.
    /// </summary>
    public async Task<MaintenanceForkResult> RunAsync(
        PromptRequestSnapshot snapshot,
        MaintenanceForkTask task,
        CancellationToken cancellationToken = default)
    {
        return await RunAsync(
            snapshot,
            task,
            messagesBeforeTask: null,
            cancellationToken);
    }

    /// <summary>
    /// Runs a maintenance fork with extra messages appended after the cached
    /// snapshot prefix and before the maintenance task.
    /// </summary>
    public async Task<MaintenanceForkResult> RunAsync(
        PromptRequestSnapshot snapshot,
        MaintenanceForkTask task,
        IReadOnlyList<ChatMessage>? messagesBeforeTask,
        CancellationToken cancellationToken = default)
    {
        var messages = BuildMessages(snapshot, task, messagesBeforeTask);
        var options = BuildOptions(snapshot);
        var sessionKey = ResolveTraceSessionKey(snapshot);
        var taskPrompt = FormatTask(task);
        traceCollector?.RecordMaintenanceForkRequest(
            sessionKey,
            task.Kind,
            taskPrompt,
            snapshot.ThreadId,
            snapshot.TurnId,
            snapshot.Mode,
            snapshot.ModelId,
            snapshot.ProviderId,
            snapshot.Messages.Count,
            messagesBeforeTask?.Count ?? 0,
            snapshot.Tools,
            snapshot.BaseInstructionsFingerprint,
            snapshot.ToolFingerprint);

        try
        {
            var response = await chatClient.GetResponseAsync(
                messages,
                options,
                cancellationToken);
            TokenUsageSnapshot? usage = response.Usage is null
                ? null
                : TokenUsageExtractor.FromResponse(response);
            var fallbackReason = ClassifyFallbackReason(response);
            traceCollector?.RecordMaintenanceForkResponse(
                sessionKey,
                task.Kind,
                response,
                fallbackReason);
            return new MaintenanceForkResult(
                task.Kind,
                response.Text,
                fallbackReason,
                usage);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            traceCollector?.RecordMaintenanceForkResponse(
                sessionKey,
                task.Kind,
                ex.Message);
            return new MaintenanceForkResult(task.Kind, null, ex.Message, null);
        }
    }

    internal static IReadOnlyList<ChatMessage> BuildMessages(
        PromptRequestSnapshot snapshot,
        MaintenanceForkTask task,
        IReadOnlyList<ChatMessage>? messagesBeforeTask = null)
    {
        var messages = snapshot.Messages.Select(message => message.Clone()).ToList();
        if (messagesBeforeTask is { Count: > 0 })
            messages.AddRange(messagesBeforeTask.Select(message => message.Clone()));
        messages.Add(new ChatMessage(ChatRole.User, FormatTask(task)));
        return messages;
    }

    internal static ChatOptions BuildOptions(PromptRequestSnapshot snapshot)
    {
        return new ChatOptions
        {
            Instructions = snapshot.BaseInstructions,
            ModelId = snapshot.ModelId,
            Tools = snapshot.Tools.ToList(),
            Reasoning = snapshot.Reasoning,
            ResponseFormat = snapshot.ResponseFormat,
            MaxOutputTokens = snapshot.MaxOutputTokens,
            AllowMultipleToolCalls = snapshot.AllowMultipleToolCalls,
            ToolMode = snapshot.ToolMode
        };
    }

    private static string FormatTask(MaintenanceForkTask task)
    {
        return $"""
<system-reminder>
## Maintenance Task
Task: {FormatKind(task.Kind)}

{task.Instructions}
</system-reminder>
""";
    }

    private static string FormatKind(MaintenanceForkTaskKind kind) => kind switch
    {
        MaintenanceForkTaskKind.ContextCompaction => "context_compaction",
        MaintenanceForkTaskKind.MemoryConsolidation => "memory_consolidation",
        _ => kind.ToString()
    };

    private static string ResolveTraceSessionKey(PromptRequestSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.ThreadId))
            return snapshot.ThreadId!;

        var active = TracingChatClient.CurrentSessionKey ?? TracingChatClient.GetActiveSessionKey();
        if (!string.IsNullOrWhiteSpace(active))
            return active!;

        return "maintenance:" + Guid.NewGuid().ToString("N")[..12];
    }

    private static string? ClassifyFallbackReason(ChatResponse response)
    {
        if (!string.IsNullOrWhiteSpace(response.Text))
            return null;

        return ResponseContainsToolCall(response)
            ? "tool_call_without_text"
            : "empty_response";
    }

    private static bool ResponseContainsToolCall(ChatResponse response)
    {
        foreach (var message in response.Messages)
        {
            if (message.Contents.OfType<FunctionCallContent>().Any())
                return true;
        }

        return false;
    }
}
