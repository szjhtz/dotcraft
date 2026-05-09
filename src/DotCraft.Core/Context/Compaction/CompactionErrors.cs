namespace DotCraft.Context.Compaction;

internal static class CompactionErrors
{
    public static bool IsPromptTooLong(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            var message = current.Message;
            if (message.Contains("prompt_too_long", StringComparison.OrdinalIgnoreCase)
                || message.Contains("context_length_exceeded", StringComparison.OrdinalIgnoreCase)
                || message.Contains("maximum context length", StringComparison.OrdinalIgnoreCase)
                || message.Contains("context window", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
