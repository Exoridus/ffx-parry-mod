namespace Fahrenheit.Mods.Parry;

public unsafe sealed partial class ParryModule
{
    // ── Stage-1 native observe probes ────────────────────────────────────────
    //
    // Log-only hooks for six of the seven entry points listed in the KB probe plan
    // (docs/fahrenheit-parry-probe-plan.md in ffx-knowledge-base):
    //
    //   MsActionRequest, MsCalcCommand, MsCheckStatusBeforeAction,
    //   MsLimitTypeDamageCheck, op_et_battle_genko_counter_get, MsSetMotion
    //
    // Each hook body MUST: call orig exactly once, preserve its return value,
    // not mutate args, and not touch parry runtime state. Output is gated by
    // _optionNativeProbeLogging — when the option is OFF (the default), the
    // probe handles are not even hooked in init(), so probe-disabled play is
    // identical to a build without this file.
    //
    // Native signatures must preserve stack arguments even when logging is off;
    // an installed observational hook still forwards every call to the engine.
    // MsSetMotion is cdecl/int, matching the active step-out caller.

    // The command and status probes preserve both native stack arguments. The
    // counter getter is a zero-argument cdecl function with a zero-extended return.

    /// <summary>
    ///     Per-probe per-frame ceiling. With seven probes this is a worst-case
    ///     56 entries per frame against a 4096-entry ring — comfortably under
    ///     the ~3-frame budget called out in <see cref="NativeProbeRing"/>.
    /// </summary>
    private const int Stage1ProbeMaxPerFrame = 8;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint MsActionRequestProbeDelegate(
        int target_id, int attacker_id, int p3, int p4, int p5, int p6);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MsLimitTypeDamageCheckProbeDelegate(
        int attacker_id, nint attacker, int target_id, nint target, int p5, int p6, int p7);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int OpEtBattleGenkoCounterGetProbeDelegate();

    // Cdecl/int, matching MsSetMotionProbe in ParryModule.cs — the shape the dodge step-out and the
    // FX lab call every battle. Ghidra reported __stdcall/uint here, which cannot both be right:
    // a stdcall detour on a cdecl function unbalances the stack on the first enemy motion.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MsSetMotionProbeDelegate(
        int p1, int p2, int chr_id, byte p4, int p5, int p6, int p7);

    private PerFrameProbeThrottle _throttleMsActionRequest;
    private PerFrameProbeThrottle _throttleMsCalcCommand;
    private PerFrameProbeThrottle _throttleMsCheckStatusBeforeAction;
    private PerFrameProbeThrottle _throttleMsLimitTypeDamageCheck;
    private PerFrameProbeThrottle _throttleOpEtBattleGenkoCounterGet;
    private PerFrameProbeThrottle _throttleMsSetMotion;

    private bool _stage1ProbesInstalled;

    /// <summary>
    ///     Lazily install the Stage-1 probe handles when the user has opted
    ///     in to native probe logging. Skipped entirely when the option is
    ///     off, so default play installs zero extra hooks.
    /// </summary>
    /// <remarks>
    ///     Must be called from <c>init()</c> AFTER <c>load_persistent_settings()</c>
    ///     so <c>_optionNativeProbeLogging</c> reflects on-disk state.
    ///     Each install is wrapped in its own try/catch and a failure for one
    ///     probe must not block the others — matches the convention used for
    ///     existing damage hooks.
    /// </remarks>
    private void install_stage1_probes()
    {
        if (_stage1ProbesInstalled) return;
        if (!_optionNativeProbeLogging)
        {
            _logger.Info("[Parry] Stage-1 native probes inert (NativeProbeLogging=false).");
            return;
        }

        install_hook(loc_stage1_ms_action_request(),            _dStage1MsActionRequest,            "MsActionRequest");
        install_hook(loc_stage1_ms_calc_command(),              _dStage1MsCalcCommand,              "MsCalcCommand");
        install_hook(loc_stage1_ms_check_status_before_action(), _dStage1MsCheckStatusBeforeAction, "MsCheckStatusBeforeAction");
        install_hook(loc_stage1_ms_limit_type_damage_check(),   _dStage1MsLimitTypeDamageCheck,   "MsLimitTypeDamageCheck");
        install_hook(loc_stage1_op_et_battle_genko_counter_get(), _dStage1OpEtBattleGenkoCounterGet, "OpEtBattleGenkoCounterGet");
        install_hook(loc_stage1_ms_set_motion(),                _dStage1MsSetMotion,                "MsSetMotion");

        _stage1ProbesInstalled = true;
        _logger.Info("[Parry] Stage-1 native probes installed (NativeProbeLogging=true). Output is in the session debug log.");
    }

    private uint h_stage1_ms_action_request(int target_id, int attacker_id, int p3, int p4, int p5, int p6)
    {
        uint result = orig_stage1_ms_action_request(target_id, attacker_id, p3, p4, p5, p6);

        if (_optionNativeProbeLogging
            && _throttleMsActionRequest.ShouldEmit(_debugFrameIndex, Stage1ProbeMaxPerFrame))
        {
            try
            {
                string args = $"target_id={target_id} attacker_id={attacker_id} p3=0x{p3:X} p4=0x{p4:X} p5=0x{p5:X} p6=0x{p6:X} ret=0x{result:X}";
                enqueue_probe_event(Stage1ProbeFormatter.Format(
                    "MsActionRequest", args, _debugFrameIndex,
                    _runtime.InputState, _runtime.CurrentAttackerId, _runtime.ParryWindowActive));
            }
            catch (Exception ex)
            {
                enqueue_probe_event(Stage1ProbeFormatter.FormatFailure("MsActionRequest", _debugFrameIndex, ex.Message));
            }
        }

        return result;
    }

    private void h_stage1_ms_calc_command(nint cue, int commandIndex)
    {
        orig_stage1_ms_calc_command(cue, commandIndex);

        if (_optionNativeProbeLogging
            && _throttleMsCalcCommand.ShouldEmit(_debugFrameIndex, Stage1ProbeMaxPerFrame))
        {
            try
            {
                enqueue_probe_event(Stage1ProbeFormatter.Format(
                    "MsCalcCommand", string.Empty, _debugFrameIndex,
                    _runtime.InputState, _runtime.CurrentAttackerId, _runtime.ParryWindowActive));
            }
            catch (Exception ex)
            {
                enqueue_probe_event(Stage1ProbeFormatter.FormatFailure("MsCalcCommand", _debugFrameIndex, ex.Message));
            }
        }
    }

    private int h_stage1_ms_check_status_before_action(int actorId, nint actor)
    {
        int result = orig_stage1_ms_check_status_before_action(actorId, actor);

        if (_optionNativeProbeLogging
            && _throttleMsCheckStatusBeforeAction.ShouldEmit(_debugFrameIndex, Stage1ProbeMaxPerFrame))
        {
            try
            {
                string args = $"ret=0x{result:X}";
                enqueue_probe_event(Stage1ProbeFormatter.Format(
                    "MsCheckStatusBeforeAction", args, _debugFrameIndex,
                    _runtime.InputState, _runtime.CurrentAttackerId, _runtime.ParryWindowActive));
            }
            catch (Exception ex)
            {
                enqueue_probe_event(Stage1ProbeFormatter.FormatFailure("MsCheckStatusBeforeAction", _debugFrameIndex, ex.Message));
            }
        }

        return result;
    }

    private int h_stage1_ms_limit_type_damage_check(
        int attacker_id, nint attacker, int target_id, nint target, int p5, int p6, int p7)
    {
        int result = orig_stage1_ms_limit_type_damage_check(
            attacker_id, attacker, target_id, target, p5, p6, p7);

        if (_optionNativeProbeLogging
            && _throttleMsLimitTypeDamageCheck.ShouldEmit(_debugFrameIndex, Stage1ProbeMaxPerFrame))
        {
            try
            {
                string args = $"attacker_id={attacker_id} target_id={target_id} attacker_ptr=0x{attacker:X} target_ptr=0x{target:X} p5=0x{p5:X} p6=0x{p6:X} p7=0x{p7:X} ret={result}";
                enqueue_probe_event(Stage1ProbeFormatter.Format(
                    "MsLimitTypeDamageCheck", args, _debugFrameIndex,
                    _runtime.InputState, _runtime.CurrentAttackerId, _runtime.ParryWindowActive));
            }
            catch (Exception ex)
            {
                enqueue_probe_event(Stage1ProbeFormatter.FormatFailure("MsLimitTypeDamageCheck", _debugFrameIndex, ex.Message));
            }
        }

        return result;
    }

    private int h_stage1_op_et_battle_genko_counter_get()
    {
        int result = orig_stage1_op_et_battle_genko_counter_get();

        if (_optionNativeProbeLogging
            && _throttleOpEtBattleGenkoCounterGet.ShouldEmit(_debugFrameIndex, Stage1ProbeMaxPerFrame))
        {
            try
            {
                string args = $"ret=0x{result:X}";
                enqueue_probe_event(Stage1ProbeFormatter.Format(
                    "op_et_battle_genko_counter_get", args, _debugFrameIndex,
                    _runtime.InputState, _runtime.CurrentAttackerId, _runtime.ParryWindowActive));
            }
            catch (Exception ex)
            {
                enqueue_probe_event(Stage1ProbeFormatter.FormatFailure("op_et_battle_genko_counter_get", _debugFrameIndex, ex.Message));
            }
        }

        return result;
    }

    private int h_stage1_ms_set_motion(
        int p1, int p2, int chr_id, byte p4, int p5, int p6, int p7)
    {
        int result = orig_stage1_ms_set_motion(p1, p2, chr_id, p4, p5, p6, p7);

        if (_optionNativeProbeLogging
            && _throttleMsSetMotion.ShouldEmit(_debugFrameIndex, Stage1ProbeMaxPerFrame))
        {
            try
            {
                string args = $"p1=0x{p1:X} p2=0x{p2:X} chr_id={chr_id} p4=0x{p4:X} p5=0x{p5:X} p6=0x{p6:X} p7=0x{p7:X} ret=0x{result:X}";
                enqueue_probe_event(Stage1ProbeFormatter.Format(
                    "MsSetMotion", args, _debugFrameIndex,
                    _runtime.InputState, _runtime.CurrentAttackerId, _runtime.ParryWindowActive));
            }
            catch (Exception ex)
            {
                enqueue_probe_event(Stage1ProbeFormatter.FormatFailure("MsSetMotion", _debugFrameIndex, ex.Message));
            }
        }

        return result;
    }
}
