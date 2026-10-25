// SPDX-License-Identifier: MIT

namespace Fahrenheit.Mods.Parry;

// =============================================================================
// The Fahrenheit hook seam — migrated.
//
// Every hook body reaches the original native function through one of the private
// helpers below instead of touching a stored handle. `FhCallHandle<T>`
// is a `ref struct`, so it can no longer be a class field: each helper constructs a
// fresh handle from a `FhCallLocation` at the point of use and walks the rest of
// the chain via `chain_from(hook).fnptr`.
//
//   loc_xxx()   — builds the FhCallLocation for a hooked function from this mod's
//                 own ExternalMemoryOffsetMap offsets (int, cast to nint).
//   orig_xxx()  — builds a transient FhCallHandle at loc_xxx(), retargets it past
//                 this mod's own hook (h_xxx) with chain_from, and invokes fnptr.
//   install_hook — shared installer: hook() returns bool (it does not
//                 throw), so each old try/catch collapses to one guarded line.
//
// Call sites in ParryModule.cs / ParryModule.Stage1Probes.cs
// are untouched by the migration: they already call orig_xxx(...), never a handle.
//
// Do not reference `FhCall.*` here: this mod deliberately no longer depends on that
// surface and builds its handles from ExternalMemoryOffsetMap offsets instead.
// =============================================================================
public unsafe sealed partial class ParryModule
{
    // ── shared installer ─────────────────────────────────────────────────────

    // `hook` is passed a cached delegate field (_dXxx), never an instance method
    // group. chain_from() below keys a Dictionary<Delegate, nint>, and converting an
    // instance method group to a delegate allocates a fresh delegate on EVERY
    // conversion (Roslyn only caches the conversion for static targets). install_hook
    // and orig_xxx must therefore hand chain_from the *same* delegate instance — the
    // one assigned once in the ParryModule() constructor — so a hot per-hit call like
    // orig_ms_set_damage_internal keys the dictionary without allocating garbage.
    private void install_hook<T>(FhCallLocation loc, T hook, string label) where T : Delegate
    {
        if (!new FhCallHandle<T>(loc).hook(this, hook))
            _logger.Warning($"[Parry] Could not hook {label}.");
    }

    // Delegate for an unhooked game function at an FFX.exe offset. Fahrenheit caches the
    // delegate per address inside the handle, so calling this per use allocates nothing.
    private static T get_fptr<T>(int offset) where T : Delegate
        => new FhCallHandle<T>(new FhCallLocation("FFX.exe", (nint)offset)).fnptr!;

    // ── cached hook delegates ────────────────────────────────────────────────
    // One per hooked function, assigned once in the ParryModule() constructor. A
    // FhCallHandle<T>/FhCallLocation is a ref struct and cannot be a field, but a
    // delegate is a class and can be — so the delegate (the dictionary key) is what we
    // cache. See install_hook above for why this removes a per-call allocation.
    private readonly MsExeInputCueProbe                      _dMsExeInputCue;
    private readonly MsSetDamageProbe                        _dMsSetDamage;
    private readonly MsDamageSetMotionProbe                  _dMsDamageSetMotion;
    private readonly MsCalcDamageProbe                       _dMsCalcDamage;
    private readonly DmgCalcArmoredProbe                     _dDmgCalcArmored;
    private readonly MsCalcDamageInternalProbe               _dMsCalcDamageInternal;
    private readonly MsSetDamageInternalProbe                _dMsSetDamageInternal;
    private readonly MsAtelRequestCameraProbe                _dMsAtelRequestCamera;
    private readonly MsAtelRequestMagicCameraProbe           _dMsAtelRequestMagicCamera;
    private readonly MsBattleSpecialCameraPauseProbe         _dMsBattleSpecialCameraPause;
    private readonly AtelCameraPolarSetProbe                 _dAtelCameraPolarSet;
    private readonly AtelCameraPosSetProbe                   _dAtelCameraPosSet;
    private readonly MsDmgCalcCheckHitProbe                  _dMsDmgCalcCheckHit;
    private readonly MsEffectEndMotionProbe                  _dMsEffectEndMotion;
    private readonly MsActionRequestProbeDelegate            _dStage1MsActionRequest;
    private readonly MsCalcCommandProbeDelegate              _dStage1MsCalcCommand;
    private readonly MsCheckStatusBeforeActionProbeDelegate  _dStage1MsCheckStatusBeforeAction;
    private readonly MsLimitTypeDamageCheckProbeDelegate     _dStage1MsLimitTypeDamageCheck;
    private readonly OpEtBattleGenkoCounterGetProbeDelegate  _dStage1OpEtBattleGenkoCounterGet;
    private readonly MsSetMotionProbeDelegate                _dStage1MsSetMotion;

    // ── ParryModule.cs (14 handles) ──────────────────────────────────────────

    private static FhCallLocation loc_ms_exe_input_cue()
        => new("FFX.exe", (nint)ExternalMemoryOffsetMap.Functions.MsExeInputCue);

    private static FhCallLocation loc_ms_set_damage()
        => new("FFX.exe", (nint)ExternalMemoryOffsetMap.Functions.MsSetDamage);

    private static FhCallLocation loc_ms_damage_set_motion()
        => new("FFX.exe", (nint)ExternalMemoryOffsetMap.Functions.MsDamageSetMotion);

    private static FhCallLocation loc_ms_calc_damage()
        => new("FFX.exe", (nint)ExternalMemoryOffsetMap.Functions.MsCalcDamage);

    private static FhCallLocation loc_dmg_calc_armored()
        => new("FFX.exe", (nint)ExternalMemoryOffsetMap.Functions.DmgCalcArmored);

    private static FhCallLocation loc_ms_calc_damage_internal()
        => new("FFX.exe", (nint)ExternalMemoryOffsetMap.Functions.MsCalcDamageInternal);

    private static FhCallLocation loc_ms_set_damage_internal()
        => new("FFX.exe", (nint)ExternalMemoryOffsetMap.Functions.MsSetDamageInternal);

    private static FhCallLocation loc_ms_atel_request_camera()
        => new("FFX.exe", (nint)ExternalMemoryOffsetMap.Functions.MsAtelRequestCamera);

    private static FhCallLocation loc_ms_atel_request_magic_camera()
        => new("FFX.exe", (nint)ExternalMemoryOffsetMap.Functions.MsAtelRequestMagicCamera);

    private static FhCallLocation loc_ms_battle_special_camera_pause()
        => new("FFX.exe", (nint)ExternalMemoryOffsetMap.Functions.MsBattleSpecialCameraPause);

    private static FhCallLocation loc_atel_camera_polar_set()
        => new("FFX.exe", (nint)ExternalMemoryOffsetMap.Functions.AtelCameraPolarSet);

    private static FhCallLocation loc_atel_camera_pos_set()
        => new("FFX.exe", (nint)ExternalMemoryOffsetMap.Functions.AtelCameraPosSet);

    private static FhCallLocation loc_ms_dmg_calc_check_hit()
        => new("FFX.exe", (nint)ExternalMemoryOffsetMap.Functions.MsDmgCalcCheckHit);

    private static FhCallLocation loc_ms_effect_end_motion()
        => new("FFX.exe", (nint)ExternalMemoryOffsetMap.Functions.MsEffectEndMotion);

    private uint orig_ms_exe_input_cue(uint actorId, byte mode)
        => new FhCallHandle<MsExeInputCueProbe>(loc_ms_exe_input_cue()).chain_from(_dMsExeInputCue).fnptr!(actorId, mode);

    private int orig_ms_set_damage(byte param_1, int param_2, int param_3)
        => new FhCallHandle<MsSetDamageProbe>(loc_ms_set_damage()).chain_from(_dMsSetDamage).fnptr!(param_1, param_2, param_3);

    private void orig_ms_damage_set_motion(byte target, int p2, int p3)
        => new FhCallHandle<MsDamageSetMotionProbe>(loc_ms_damage_set_motion()).chain_from(_dMsDamageSetMotion).fnptr!(target, p2, p3);

    private int orig_ms_calc_damage(
        int user_id, nint user_chr, int target_id, nint target_chr,
        nint command, int command_id,
        nint p7, nint p8, nint p9, nint p10, int p11)
        => new FhCallHandle<MsCalcDamageProbe>(loc_ms_calc_damage()).chain_from(_dMsCalcDamage).fnptr!(
            user_id, user_chr, target_id, target_chr,
            command, command_id,
            p7, p8, p9, p10, p11);

    private int orig_dmg_calc_armored(Chr* user, Chr* target, Command* command, int p4, int* p5, int damage)
        => new FhCallHandle<DmgCalcArmoredProbe>(loc_dmg_calc_armored()).chain_from(_dDmgCalcArmored).fnptr!(user, target, command, p4, p5, damage);

    private int orig_ms_calc_damage_internal(
        int user_id, nint user_chr, int target_id, nint target_chr,
        nint command, int command_id,
        nint p7, nint p8, nint p9, nint p10, int p11)
        => new FhCallHandle<MsCalcDamageInternalProbe>(loc_ms_calc_damage_internal()).chain_from(_dMsCalcDamageInternal).fnptr!(
            user_id, user_chr, target_id, target_chr,
            command, command_id,
            p7, p8, p9, p10, p11);

    private int orig_ms_set_damage_internal(int param_1, byte param_2, int param_3, int param_4, int param_5)
        => new FhCallHandle<MsSetDamageInternalProbe>(loc_ms_set_damage_internal()).chain_from(_dMsSetDamageInternal).fnptr!(param_1, param_2, param_3, param_4, param_5);

    private int orig_ms_atel_request_camera(int p1, int p2, int p3, int p4, int p5, int p6, int p7, int p8)
        => new FhCallHandle<MsAtelRequestCameraProbe>(loc_ms_atel_request_camera()).chain_from(_dMsAtelRequestCamera).fnptr!(p1, p2, p3, p4, p5, p6, p7, p8);

    private byte orig_ms_atel_request_magic_camera(int p1, int p2, uint p3, int p4, int p5, int p6, uint p7, int p8, int p9)
        => new FhCallHandle<MsAtelRequestMagicCameraProbe>(loc_ms_atel_request_magic_camera()).chain_from(_dMsAtelRequestMagicCamera).fnptr!(p1, p2, p3, p4, p5, p6, p7, p8, p9);

    private void orig_ms_battle_special_camera_pause(byte mode)
        => new FhCallHandle<MsBattleSpecialCameraPauseProbe>(loc_ms_battle_special_camera_pause()).chain_from(_dMsBattleSpecialCameraPause).fnptr!(mode);

    private int orig_atel_camera_polar_set(int worker, int p2, int stack, int isCam, int variant)
        => new FhCallHandle<AtelCameraPolarSetProbe>(loc_atel_camera_polar_set()).chain_from(_dAtelCameraPolarSet).fnptr!(worker, p2, stack, isCam, variant);

    private void orig_atel_camera_pos_set(int worker, int p2, int stack, int p4)
        => new FhCallHandle<AtelCameraPosSetProbe>(loc_atel_camera_pos_set()).chain_from(_dAtelCameraPosSet).fnptr!(worker, p2, stack, p4);

    private int orig_ms_dmg_calc_check_hit(Chr* user, Chr* target, Command* command, void* info, int counter)
        => new FhCallHandle<MsDmgCalcCheckHitProbe>(loc_ms_dmg_calc_check_hit()).chain_from(_dMsDmgCalcCheckHit).fnptr!(user, target, command, info, counter);

    private void orig_ms_effect_end_motion(uint chr_id, int mode)
        => new FhCallHandle<MsEffectEndMotionProbe>(loc_ms_effect_end_motion()).chain_from(_dMsEffectEndMotion).fnptr!(chr_id, mode);

    // ── ParryModule.Stage1Probes.cs (6 handles) ──────────────────────────────

    private static FhCallLocation loc_stage1_ms_action_request()
        => new("FFX.exe", (nint)ExternalMemoryOffsetMap.Functions.MsActionRequest);

    private static FhCallLocation loc_stage1_ms_calc_command()
        => new("FFX.exe", (nint)ExternalMemoryOffsetMap.Functions.MsCalcCommand);

    private static FhCallLocation loc_stage1_ms_check_status_before_action()
        => new("FFX.exe", (nint)ExternalMemoryOffsetMap.Functions.MsCheckStatusBeforeAction);

    private static FhCallLocation loc_stage1_ms_limit_type_damage_check()
        => new("FFX.exe", (nint)ExternalMemoryOffsetMap.Functions.MsLimitTypeDamageCheck);

    private static FhCallLocation loc_stage1_op_et_battle_genko_counter_get()
        => new("FFX.exe", (nint)ExternalMemoryOffsetMap.Functions.OpEtBattleGenkoCounterGet);

    private static FhCallLocation loc_stage1_ms_set_motion()
        => new("FFX.exe", (nint)ExternalMemoryOffsetMap.Functions.MsSetMotion);

    private uint orig_stage1_ms_action_request(int target_id, int attacker_id, int p3, int p4, int p5, int p6)
        => new FhCallHandle<MsActionRequestProbeDelegate>(loc_stage1_ms_action_request()).chain_from(_dStage1MsActionRequest).fnptr!(target_id, attacker_id, p3, p4, p5, p6);

    private void orig_stage1_ms_calc_command(nint cue, int commandIndex)
        => new FhCallHandle<MsCalcCommandProbeDelegate>(loc_stage1_ms_calc_command()).chain_from(_dStage1MsCalcCommand).fnptr!(cue, commandIndex);

    private int orig_stage1_ms_check_status_before_action(int actorId, nint actor)
        => new FhCallHandle<MsCheckStatusBeforeActionProbeDelegate>(loc_stage1_ms_check_status_before_action()).chain_from(_dStage1MsCheckStatusBeforeAction).fnptr!(actorId, actor);

    private int orig_stage1_ms_limit_type_damage_check(
        int attacker_id, nint attacker, int target_id, nint target, int p5, int p6, int p7)
        => new FhCallHandle<MsLimitTypeDamageCheckProbeDelegate>(loc_stage1_ms_limit_type_damage_check()).chain_from(_dStage1MsLimitTypeDamageCheck).fnptr!(
            attacker_id, attacker, target_id, target, p5, p6, p7);

    private int orig_stage1_op_et_battle_genko_counter_get()
        => new FhCallHandle<OpEtBattleGenkoCounterGetProbeDelegate>(loc_stage1_op_et_battle_genko_counter_get()).chain_from(_dStage1OpEtBattleGenkoCounterGet).fnptr!();

    private int orig_stage1_ms_set_motion(int p1, int p2, int chr_id, byte p4, int p5, int p6, int p7)
        => new FhCallHandle<MsSetMotionProbeDelegate>(loc_stage1_ms_set_motion()).chain_from(_dStage1MsSetMotion).fnptr!(p1, p2, chr_id, p4, p5, p6, p7);
}
