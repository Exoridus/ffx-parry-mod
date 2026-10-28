namespace Fahrenheit.Mods.Parry;

public sealed partial class ParryModule
{
    public int Updates, Edges;
    public float Elapsed;
    public bool Pressed;
    private bool _previousPressed;
    public Action? DuringUpdate;
    public void Initialize() { lock (_vrrSimulationGate) _vrrSimulationReady = true; }
    public void PreUpdate(float delta)
    {
        if (!try_defer_vrr_simulation(delta)) on_simulation_update(delta);
    }
    private void on_simulation_update(float delta)
    {
        Updates++;
        Elapsed += delta;
        if (Pressed && !_previousPressed) Edges++;
        _previousPressed = Pressed;
        DuringUpdate?.Invoke();
    }
}
