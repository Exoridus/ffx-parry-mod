using Fahrenheit.Mods.Parry;
using Xunit;

public sealed class VrrSimulationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SupportsBothInitializationOrdersAndStandalone(bool bindFirst)
    {
        var module = new ParryModule();
        if (!bindFirst) module.Initialize();
        Assert.True(module.TryBindVrrSimulation(1, new object(), _ => true, out var ready, out var tick));
        Assert.Equal(!bindFirst, ready!());
        module.PreUpdate(.02f);
        Assert.Equal(bindFirst ? 1 : 0, module.Updates);
        module.Initialize();
        Assert.True(ready());
        module.PreUpdate(.02f);
        int before = module.Updates;
        tick!(1, 1, .02f);
        Assert.Equal(before + 1, module.Updates);
    }

    [Fact]
    public void RejectsWrongVersionAndOtherOwnersButRepeatsExactBinding()
    {
        var module = new ParryModule();
        object owner = new();
        Func<float, bool> provider = _ => true;
        Assert.False(module.TryBindVrrSimulation(2, owner, provider, out var missing, out var absent));
        Assert.Null(missing);
        Assert.Null(absent);
        Assert.True(module.TryBindVrrSimulation(1, owner, provider, out var ready, out var tick));
        Assert.True(module.TryBindVrrSimulation(1, owner, provider, out var again, out var same));
        Assert.Same(ready, again);
        Assert.Same(tick, same);
        Assert.False(module.TryBindVrrSimulation(1, new object(), provider, out _, out _));
        Assert.False(module.TryBindVrrSimulation(1, owner, _ => false, out _, out _));
    }

    [Fact]
    public void StandaloneAndProviderDeclineKeepOriginalUpdates()
    {
        var module = new ParryModule();
        module.PreUpdate(.02f);
        module.Initialize();
        module.TryBindVrrSimulation(1, new object(), _ => false, out _, out _);
        module.PreUpdate(.03f);
        Assert.Equal(2, module.Updates);
        Assert.Equal(.05f, module.Elapsed, 6);
    }

    [Fact]
    public void ProviderFailureFallsBackAndRevokesReadiness()
    {
        var module = new ParryModule();
        module.Initialize();
        module.TryBindVrrSimulation(1, new object(), _ => throw new Exception("provider"), out var ready, out var tick);
        module.PreUpdate(.02f);
        Assert.Equal(1, module.Updates);
        Assert.False(ready!());
        Assert.Throws<InvalidOperationException>(() => tick!(1, 1, .02f));
        module.PreUpdate(.02f);
        Assert.Equal(2, module.Updates);
    }

    [Fact]
    public void RequiresOneGrantAndMonotonicEpochTickAndValidDelta()
    {
        var module = new ParryModule();
        module.Initialize();
        module.TryBindVrrSimulation(1, new object(), _ => true, out _, out var tick);
        Assert.Throws<InvalidOperationException>(() => tick!(1, 1, .02f));
        module.PreUpdate(.02f);
        Assert.Throws<ArgumentOutOfRangeException>(() => tick!(1, 1, float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => tick!(1, 1, 0));
        tick!(1, 1, .02f);
        Assert.Throws<InvalidOperationException>(() => tick(1, 1, .02f));
        module.PreUpdate(.02f);
        Assert.Throws<InvalidOperationException>(() => tick(1, 1, .02f));
        Assert.Throws<InvalidOperationException>(() => tick(0, 2, .02f));
        tick(1, 2, .02f);
        module.PreUpdate(.02f);
        tick(2, 1, .02f);
        Assert.Equal(3, module.Updates);
    }

    [Fact]
    public void RejectsForeignThreadAndReentryWithoutRetryingPartialUpdate()
    {
        var module = new ParryModule();
        module.Initialize();
        module.TryBindVrrSimulation(1, new object(), _ => true, out var ready, out var tick);
        module.PreUpdate(.02f);
        Exception? foreign = null;
        var thread = new Thread(() => foreign = Record.Exception(() => tick!(1, 1, .02f)));
        thread.Start();
        thread.Join();
        Assert.IsType<InvalidOperationException>(foreign);
        module.DuringUpdate = () => tick!(1, 2, .02f);
        Assert.Throws<InvalidOperationException>(() => tick!(1, 1, .02f));
        Assert.Equal(1, module.Updates);
        Assert.False(ready!());
        Assert.Throws<InvalidOperationException>(() => tick!(1, 1, .02f));
        Assert.Equal(1, module.Updates);
    }

    [Fact]
    public void ProviderReentryRunsStandaloneOnceAndRevokesReadiness()
    {
        var module = new ParryModule();
        module.Initialize();
        module.TryBindVrrSimulation(1, new object(), delta => { module.PreUpdate(delta); return true; },
            out var ready, out _);
        module.PreUpdate(.02f);
        Assert.Equal(1, module.Updates);
        Assert.False(ready!());
    }

    [Fact]
    public void NewPreUpdateReplacesUnusedGrantAndDeclineRevokesIt()
    {
        var module = new ParryModule();
        module.Initialize();
        bool defer = true;
        module.TryBindVrrSimulation(1, new object(), _ => defer, out _, out var tick);
        module.PreUpdate(.02f);
        module.PreUpdate(.02f);
        Assert.Equal(0, module.Updates);
        tick!(1, 1, .02f);
        Assert.Equal(1, module.Updates);
        module.PreUpdate(.02f);
        defer = false;
        module.PreUpdate(.02f);
        Assert.Throws<InvalidOperationException>(() => tick(1, 2, .02f));
        Assert.Equal(2, module.Updates);
    }

    [Fact]
    public void CallbackFailureConsumesIdentityAndPropagatesExactlyOnce()
    {
        var module = new ParryModule();
        module.Initialize();
        module.TryBindVrrSimulation(1, new object(), _ => true, out var ready, out var tick);
        module.PreUpdate(.02f);
        var failure = new Exception("simulation");
        module.DuringUpdate = () => throw failure;
        Assert.Same(failure, Assert.Throws<Exception>(() => tick!(1, 1, .02f)));
        Assert.False(ready!());
        Assert.Throws<InvalidOperationException>(() => tick!(1, 1, .02f));
        Assert.Equal(1, module.Updates);
    }

    [Fact]
    public void HeldLevelHandoverDoesNotCreateAnotherPress()
    {
        var module = new ParryModule { Pressed = true };
        module.PreUpdate(.02f);
        module.Initialize();
        bool defer = true;
        module.TryBindVrrSimulation(1, new object(), _ => defer, out _, out var tick);
        module.PreUpdate(.02f);
        tick!(1, 1, .02f);
        Assert.Equal(1, module.Edges);
        defer = false;
        module.PreUpdate(.02f);
        Assert.Equal(1, module.Edges);
        module.Pressed = false;
        module.PreUpdate(.02f);
        defer = true;
        module.PreUpdate(.02f);
        module.Pressed = true;
        tick(1, 2, .02f);
        Assert.Equal(2, module.Edges);
    }
}
