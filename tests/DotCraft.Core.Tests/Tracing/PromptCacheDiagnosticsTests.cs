using DotCraft.Tracing;

namespace DotCraft.Tests.Tracing;

public sealed class PromptCacheDiagnosticsTests
{
    [Fact]
    public void RecordSessionMetadata_RecordsBaselineOnceAndSkipsUnchangedMetadata()
    {
        var store = new TraceStore();
        var collector = new TraceCollector(store);

        collector.RecordSessionMetadata("session", "system", ["ReadFile", "EditFile"]);
        collector.RecordSessionMetadata("session", "system", ["ReadFile", "EditFile"]);

        var metadata = Events(store, "session", TraceEventType.SessionMetadata);
        var evt = Assert.Single(metadata);
        Assert.Equal(PromptCacheEventKinds.Baseline, evt.PromptCacheEventKind);
        Assert.False(evt.PromptDriftDetected);
        Assert.Equal(0, store.GetSession("session")!.PromptDriftCount);
    }

    [Fact]
    public void RecordSessionMetadata_SystemPromptChangeRecordsDrift()
    {
        var store = new TraceStore();
        var collector = new TraceCollector(store);

        collector.RecordSessionMetadata("session", "system-v1", ["ReadFile"]);
        collector.RecordSessionMetadata("session", "system-v2", ["ReadFile"]);

        var drift = Events(store, "session", TraceEventType.SessionMetadata).Last();
        Assert.Equal(PromptCacheEventKinds.Drift, drift.PromptCacheEventKind);
        Assert.NotNull(drift.PromptCacheChangedFields);
        Assert.Equal([PromptCacheChangedFields.Prompt], drift.PromptCacheChangedFields);
        Assert.True(drift.PromptDriftDetected);
        Assert.NotEqual(drift.PreviousSystemPromptHash, drift.CurrentSystemPromptHash);
        Assert.Equal(1, store.GetSession("session")!.PromptDriftCount);
    }

    [Theory]
    [InlineData("ReadFile")]
    [InlineData("EditFile|ReadFile")]
    [InlineData("WriteFile|EditFile")]
    public void RecordSessionMetadata_NonAppendOnlyToolChangeRecordsDrift(string newToolsCsv)
    {
        var store = new TraceStore();
        var collector = new TraceCollector(store);

        collector.RecordSessionMetadata("session", "system", ["ReadFile", "EditFile"]);
        var newTools = newToolsCsv.Split('|');
        collector.RecordSessionMetadata("session", "system", newTools);

        var drift = Events(store, "session", TraceEventType.SessionMetadata).Last();
        Assert.Equal(PromptCacheEventKinds.Drift, drift.PromptCacheEventKind);
        Assert.NotNull(drift.PromptCacheChangedFields);
        Assert.Equal([PromptCacheChangedFields.Tools], drift.PromptCacheChangedFields);
        Assert.True(drift.PromptDriftDetected);
        Assert.Equal(1, store.GetSession("session")!.PromptDriftCount);
    }

    [Fact]
    public void RecordSessionMetadata_AppendOnlyToolChangeRecordsToolExtension()
    {
        var store = new TraceStore();
        var collector = new TraceCollector(store);

        collector.RecordSessionMetadata("session", "system", ["ReadFile"]);
        collector.RecordSessionMetadata("session", "system", ["ReadFile", "EditFile", "WriteFile"]);

        var extension = Events(store, "session", TraceEventType.SessionMetadata).Last();
        Assert.Equal(PromptCacheEventKinds.ToolExtension, extension.PromptCacheEventKind);
        Assert.NotNull(extension.PromptCacheChangedFields);
        Assert.NotNull(extension.ChangedToolNames);
        Assert.Equal([PromptCacheChangedFields.Tools], extension.PromptCacheChangedFields);
        Assert.Equal(["EditFile", "WriteFile"], extension.ChangedToolNames);
        Assert.False(extension.PromptDriftDetected);
        Assert.Equal(0, store.GetSession("session")!.PromptDriftCount);
        Assert.Equal(PromptCacheEventKinds.ToolExtension, store.GetSession("session")!.LastPromptCacheChangeKind);
    }

    private static IReadOnlyList<TraceEvent> Events(TraceStore store, string sessionKey, TraceEventType type) =>
        store.GetEvents(sessionKey).Where(e => e.Type == type).ToList();
}
