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
public sealed class MaintenanceForkRunner(IChatClient chatClient)
{
    /// <summary>
    /// Runs a maintenance fork and returns the assistant text, or a fallback reason.
    /// </summary>
    public async Task<MaintenanceForkResult> RunAsync(
        PromptRequestSnapshot snapshot,
        MaintenanceForkTask task,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await chatClient.GetResponseAsync(
                BuildMessages(snapshot, task),
                BuildOptions(snapshot),
                cancellationToken);
            TokenUsageSnapshot? usage = response.Usage is null
                ? null
                : TokenUsageExtractor.FromResponse(response);
            return new MaintenanceForkResult(
                task.Kind,
                response.Text,
                string.IsNullOrWhiteSpace(response.Text) ? "empty_response" : null,
                usage);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new MaintenanceForkResult(task.Kind, null, ex.Message, null);
        }
    }

    internal static IReadOnlyList<ChatMessage> BuildMessages(
        PromptRequestSnapshot snapshot,
        MaintenanceForkTask task)
    {
        var messages = snapshot.Messages.Select(message => message.Clone()).ToList();
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
}
