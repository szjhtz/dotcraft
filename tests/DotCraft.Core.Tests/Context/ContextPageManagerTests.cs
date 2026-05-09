using DotCraft.Context;

namespace DotCraft.Tests.Context;

public sealed class ContextPageManagerTests
{
    [Fact]
    public void GetOrAdd_KeepsStablePageUntilReleased()
    {
        var manager = new ContextPageManager();
        var key = new ContextPageKey("scope", "name", "variant");
        var value = "v1";

        Assert.Equal("v1", manager.GetOrAdd("thread", key, ContextPageLifecycle.StableUntilCompaction, () => value).Content);

        value = "v2";
        manager.MarkDirty(key);

        Assert.Equal("v1", manager.GetOrAdd("thread", key, ContextPageLifecycle.StableUntilCompaction, () => value).Content);

        manager.ReleaseStablePages("thread");

        Assert.Equal("v2", manager.GetOrAdd("thread", key, ContextPageLifecycle.StableUntilCompaction, () => value).Content);
    }

    [Fact]
    public void GetOrAdd_CachesPerThread()
    {
        var manager = new ContextPageManager();
        var key = new ContextPageKey("scope", "name", "variant");
        var value = "v1";

        Assert.Equal("v1", manager.GetOrAdd("thread-a", key, ContextPageLifecycle.StableUntilCompaction, () => value).Content);

        value = "v2";

        Assert.Equal("v1", manager.GetOrAdd("thread-a", key, ContextPageLifecycle.StableUntilCompaction, () => value).Content);
        Assert.Equal("v2", manager.GetOrAdd("thread-b", key, ContextPageLifecycle.StableUntilCompaction, () => value).Content);
    }

    [Fact]
    public void ForgetThread_RemovesStablePages()
    {
        var manager = new ContextPageManager();
        var key = new ContextPageKey("scope", "name", "variant");
        var value = "v1";

        Assert.Equal("v1", manager.GetOrAdd("thread", key, ContextPageLifecycle.StableUntilCompaction, () => value).Content);

        value = "v2";
        manager.ForgetThread("thread");

        Assert.Equal("v2", manager.GetOrAdd("thread", key, ContextPageLifecycle.StableUntilCompaction, () => value).Content);
    }

    [Fact]
    public void ImmediateLifecycle_AlwaysReloads()
    {
        var manager = new ContextPageManager();
        var key = new ContextPageKey("scope", "name", "variant");
        var value = "v1";

        Assert.Equal("v1", manager.GetOrAdd("thread", key, ContextPageLifecycle.Immediate, () => value).Content);

        value = "v2";

        Assert.Equal("v2", manager.GetOrAdd("thread", key, ContextPageLifecycle.Immediate, () => value).Content);
    }
}
