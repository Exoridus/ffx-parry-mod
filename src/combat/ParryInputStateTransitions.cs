namespace Fahrenheit.Mods.Parry;

/// <summary>
///     Pure transition decisions for the <see cref="ParryInputState"/> machine.
///
///     Centralises every state-gate rule so the parry input flow can be unit-tested
///     without instantiating <c>ParryModule</c> (which is FFX-coupled via the
///     <c>FhCallHandle</c> hook installs in its constructor).
///
///     Every method is pure, allocation-free, and side-effect-free. Callers in
///     <c>ParryModule.Combat.cs</c> and <c>ParryModule.cs</c> apply the side effects
///     (timers, sounds, logs, telemetry) AROUND the decision returned here.
///
///     Reference: <c>FINAL_PARRY_SPEC.md</c>.
/// </summary>
public static class ParryInputStateTransitions
{
    /// <summary>
    ///     Outcome of an R1-press attempt.
    /// </summary>
    /// <param name="Accepted">
    ///     <c>true</c> when the press should open a fresh window.
    ///     <c>false</c> when the press is rejected — the caller must NOT touch
    ///     state and should log <see cref="RejectReason"/> verbatim.
    /// </param>
    /// <param name="NextState">
    ///     The state to transition to. <see cref="ParryInputState.Open"/> on accept;
    ///     unchanged from <c>currentState</c> on reject.
    /// </param>
    /// <param name="RejectReason">
    ///     Stable identifier for the rejection cause. Empty when accepted.
    ///     Values: <c>no_parryable_cue</c>, <c>window_already_open</c>,
    ///     <c>current_attack_already_parried</c>, <c>in_guard_recovery</c>.
    /// </param>
    public readonly record struct PressDecision(
        bool Accepted,
        ParryInputState NextState,
        string RejectReason);

    /// <summary>
    ///     Decide whether an R1 press should open a window.
    ///
    ///     A press is accepted only when the current state is
    ///     <see cref="ParryInputState.Ready"/> AND a parryable cue exists. Every
    ///     other case is rejected with a specific reason — there is no implicit
    ///     "re-arm" path. <see cref="ParryInputState.Open"/> rejects so the
    ///     player cannot extend or refresh an already-open window;
    ///     <see cref="ParryInputState.Resolved"/> rejects so a press during
    ///     post-resolution carryover does not silently re-trigger;
    ///     <see cref="ParryInputState.WhiffLockout"/> rejects to honour the
    ///     committed guard-recovery animation approximation.
    /// </summary>
    public static PressDecision DecidePress(
        ParryInputState currentState,
        bool hasParryableCue)
    {
        if (!hasParryableCue)
        {
            return new PressDecision(
                Accepted: false,
                NextState: currentState,
                RejectReason: "no_parryable_cue");
        }

        return currentState switch
        {
            ParryInputState.Ready
                => new PressDecision(true, ParryInputState.Open, string.Empty),
            ParryInputState.Open
                => new PressDecision(false, currentState, "window_already_open"),
            ParryInputState.Resolved
                => new PressDecision(false, currentState, "current_attack_already_parried"),
            ParryInputState.WhiffLockout
                => new PressDecision(false, currentState, "in_guard_recovery"),
            _
                => new PressDecision(false, currentState, "unknown_state")
        };
    }

    /// <summary>
    ///     Decide which state an Open window should transition into when its
    ///     timer elapses without a hit landing.
    ///
    ///     Returns <see cref="ParryInputState.WhiffLockout"/> when the
    ///     animation-approximated lockout is enabled (a positive
    ///     <paramref name="lockoutSeconds"/>), <see cref="ParryInputState.Ready"/>
    ///     otherwise. The latter exists so the user-facing "whiff lockout"
    ///     setting can be turned off without leaving the player stuck.
    /// </summary>
    public static ParryInputState DecideWindowExpiry(float lockoutSeconds)
    {
        return lockoutSeconds > 0f
            ? ParryInputState.WhiffLockout
            : ParryInputState.Ready;
    }

    /// <summary>
    ///     Decide the state after a successful parry resolution. The window
    ///     stays open behaviourally (waiting for turn end) but the input gate
    ///     transitions to <see cref="ParryInputState.Resolved"/> so a second
    ///     press inside the same attack cannot re-arm.
    ///
    ///     Resolutions outside the <see cref="ParryInputState.Open"/> state are
    ///     a no-op on the input machine — the calling site uses other state
    ///     (mask, telemetry) for its own bookkeeping.
    /// </summary>
    public static ParryInputState DecideOnResolve(ParryInputState currentState)
    {
        return currentState == ParryInputState.Open
            ? ParryInputState.Resolved
            : currentState;
    }

    /// <summary>
    ///     Decide the state after the WhiffLockout recovery timer elapses.
    ///     Always <see cref="ParryInputState.Ready"/>.
    /// </summary>
    public static ParryInputState DecideLockoutComplete()
    {
        return ParryInputState.Ready;
    }
}
