// SPDX-License-Identifier: MIT

namespace Fahrenheit.Mods.Parry;

public sealed partial class ParryModule
{
    private readonly object _vrrSimulationGate = new();
    private object? _vrrSimulationOwner;
    private Func<float, bool>? _vrrDeferPreUpdate;
    private Func<bool>? _vrrReadyQuery;
    private Action<long, long, float>? _vrrTickDispatch;
    private bool _vrrSimulationReady, _vrrSimulationFaulted, _vrrSimulationChecking,
        _vrrSimulationPending, _vrrSimulationDispatching;
    private int _vrrSimulationThread;
    private long _vrrSimulationEpoch = -1, _vrrSimulationTick = -1;

    /// <summary>
    /// Binds version 1 simulation ownership, optionally before initialization. Repeating the same
    /// owner and provider instance returns the same delegates; another binding is refused.
    /// The provider returns true to relinquish that PreUpdate. One tick may then run on its thread,
    /// after qualified input publication, with increasing epoch/tick identity and positive delta.
    /// Provider failure retains standalone updating and revokes readiness. A dispatched tick is
    /// consumed before work; its failure revokes readiness and propagates without retry.
    /// This cooperation does not qualify native replay or independent presentation.
    /// </summary>
    public bool TryBindVrrSimulation(int version, object owner, Func<float, bool> deferPreUpdate,
        out Func<bool>? ready, out Action<long, long, float>? tick)
    {
        ready = null;
        tick = null;
        lock (_vrrSimulationGate)
        {
            if (version != 1 || owner is null || deferPreUpdate is null) return false;
            if (_vrrSimulationOwner is not null && (!ReferenceEquals(owner, _vrrSimulationOwner)
                || !ReferenceEquals(deferPreUpdate, _vrrDeferPreUpdate))) return false;
            _vrrSimulationOwner = owner;
            _vrrDeferPreUpdate = deferPreUpdate;
            ready = _vrrReadyQuery ??= vrr_simulation_ready;
            tick = _vrrTickDispatch ??= dispatch_vrr_simulation;
            return true;
        }
    }

    private bool vrr_simulation_ready()
    {
        lock (_vrrSimulationGate)
            return _vrrSimulationReady && !_vrrSimulationFaulted && _vrrSimulationOwner is not null;
    }

    private bool try_defer_vrr_simulation(float delta)
    {
        Func<float, bool> provider;
        lock (_vrrSimulationGate)
        {
            if (_vrrSimulationChecking || _vrrSimulationDispatching)
                throw new InvalidOperationException("Parry simulation ownership reentered.");
            _vrrSimulationPending = false;
            if (!_vrrSimulationReady || _vrrSimulationFaulted || _vrrDeferPreUpdate is null) return false;
            provider = _vrrDeferPreUpdate;
            _vrrSimulationChecking = true;
        }
        bool deferred = false;
        try { deferred = provider(delta); }
        catch
        {
            lock (_vrrSimulationGate) _vrrSimulationFaulted = true;
        }
        lock (_vrrSimulationGate)
        {
            _vrrSimulationChecking = false;
            if (!deferred || _vrrSimulationFaulted) return false;
            int thread = Environment.CurrentManagedThreadId;
            if (_vrrSimulationThread != 0 && _vrrSimulationThread != thread)
            {
                _vrrSimulationFaulted = true;
                return false;
            }
            _vrrSimulationThread = thread;
            _vrrSimulationPending = true;
            return true;
        }
    }

    private void dispatch_vrr_simulation(long epoch, long tick, float delta)
    {
        if (epoch < 0 || tick < 0 || !float.IsFinite(delta) || delta <= 0)
            throw new ArgumentOutOfRangeException(nameof(delta), "Simulation identity and delta must be valid.");
        lock (_vrrSimulationGate)
        {
            if (!_vrrSimulationReady || _vrrSimulationFaulted || !_vrrSimulationPending
                || _vrrSimulationChecking || _vrrSimulationDispatching
                || _vrrSimulationThread != Environment.CurrentManagedThreadId
                || epoch < _vrrSimulationEpoch || (epoch == _vrrSimulationEpoch && tick <= _vrrSimulationTick))
                throw new InvalidOperationException("Parry simulation tick is not owned or is out of order.");
            _vrrSimulationPending = false;
            _vrrSimulationEpoch = epoch;
            _vrrSimulationTick = tick;
            _vrrSimulationDispatching = true;
        }
        try { on_simulation_update(delta); }
        catch
        {
            lock (_vrrSimulationGate) _vrrSimulationFaulted = true;
            throw;
        }
        finally
        {
            lock (_vrrSimulationGate) _vrrSimulationDispatching = false;
        }
    }
}
