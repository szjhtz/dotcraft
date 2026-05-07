using System.Text;
using System.Text.Json;
using DotCraft.Memory;
using Microsoft.Extensions.AI;

namespace DotCraft.Context;

/// <summary>
/// Memory consolidator that prefers a same-model prompt fork and falls back to
/// the legacy consolidator when the fork contract is unavailable.
/// </summary>
public sealed class MemoryForkConsolidator(
    MaintenanceForkRunner forkRunner,
    IMemoryConsolidator fallback,
    MemoryStore memoryStore,
    string? mainModelId,
    string? consolidationModelId) : IMemoryForkConsolidator
{
    /// <inheritdoc />
    public Task<MemoryConsolidationResult> ConsolidateAsync(
        IReadOnlyList<ChatMessage> messagesToArchive,
        CancellationToken cancellationToken = default) =>
        ConsolidateAsync(messagesToArchive, snapshot: null, cancellationToken);

    /// <inheritdoc />
    public async Task<MemoryConsolidationResult> ConsolidateAsync(
        IReadOnlyList<ChatMessage> messagesToArchive,
        PromptRequestSnapshot? snapshot,
        CancellationToken cancellationToken = default)
    {
        if (messagesToArchive.Count == 0)
            return MemoryConsolidationResult.Skipped("empty_snapshot");

        var fallbackReason = GetFallbackReason(snapshot);
        if (fallbackReason is not null)
            return await fallback.ConsolidateAsync(messagesToArchive, cancellationToken);

        var currentMemory = memoryStore.ReadLongTerm();
        var result = await forkRunner.RunAsync(
            snapshot!,
            new MaintenanceForkTask(
                MaintenanceForkTaskKind.MemoryConsolidation,
                BuildTaskInstructions(messagesToArchive, currentMemory)),
            cancellationToken);

        if (result.FallbackReason is not null)
            return await fallback.ConsolidateAsync(messagesToArchive, cancellationToken);

        if (!TryParseStructuredResult(result.Text, out var historyEntry, out var memoryUpdate))
            return await fallback.ConsolidateAsync(messagesToArchive, cancellationToken);

        var write = memoryStore.SaveConsolidation(historyEntry, memoryUpdate);
        return write.AnyWritten
            ? MemoryConsolidationResult.Succeeded(write.MemoryWritten, write.HistoryWritten)
            : MemoryConsolidationResult.Skipped("no_memory_changes");
    }

    private string? GetFallbackReason(PromptRequestSnapshot? snapshot)
    {
        if (snapshot is null)
            return "snapshot_unavailable";

        if (!string.IsNullOrWhiteSpace(mainModelId)
            && !string.IsNullOrWhiteSpace(consolidationModelId)
            && !string.Equals(mainModelId, consolidationModelId, StringComparison.Ordinal))
        {
            return "different_consolidation_model";
        }

        return null;
    }

    private static string BuildTaskInstructions(
        IReadOnlyList<ChatMessage> messagesToArchive,
        string currentMemory)
    {
        return $$"""
Consolidate durable memory from the completed conversation.

Allowed output:
- Return JSON text only.
- Do not call ordinary workspace tools.
- Use this exact object shape:
  {"history_entry":"[YYYY-MM-DD HH:MM] 2-5 sentence grep-searchable event paragraph","memory_update":"full updated MEMORY.md markdown"}
- If nothing new was learned, set memory_update to the current memory unchanged and leave history_entry empty.

## Current MEMORY.md
{{(string.IsNullOrWhiteSpace(currentMemory) ? "(empty)" : currentMemory)}}

## Completed conversation snapshot
{{FormatMessages(messagesToArchive)}}
""";
    }

    private static bool TryParseStructuredResult(
        string? text,
        out string? historyEntry,
        out string? memoryUpdate)
    {
        historyEntry = null;
        memoryUpdate = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(ExtractJsonObject(text));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            if (root.TryGetProperty("history_entry", out var historyElement))
                historyEntry = historyElement.GetString();
            if (root.TryGetProperty("memory_update", out var memoryElement))
                memoryUpdate = memoryElement.GetString();

            return !string.IsNullOrWhiteSpace(historyEntry)
                || !string.IsNullOrWhiteSpace(memoryUpdate);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string ExtractJsonObject(string text)
    {
        var trimmed = text.Trim();
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        return start >= 0 && end >= start
            ? trimmed[start..(end + 1)]
            : trimmed;
    }

    private static string FormatMessages(IReadOnlyList<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        var now = DateTime.Now;
        foreach (var message in messages)
        {
            if (string.IsNullOrWhiteSpace(message.Text))
                continue;

            var role = message.Role == ChatRole.User ? "USER"
                : message.Role == ChatRole.Assistant ? "ASSISTANT"
                : message.Role.ToString().ToUpperInvariant();
            sb.AppendLine($"[{now:yyyy-MM-dd}] {role}: {message.Text.Trim()}");
        }

        return sb.ToString();
    }
}
