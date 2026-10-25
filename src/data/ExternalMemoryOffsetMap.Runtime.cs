namespace Fahrenheit.Mods.Parry;

public static partial class ExternalMemoryOffsetMap
{
    public static class FrameAndRng
    {
        // Global frame counter.
        public const int FrameCounter = 0x0088FDD8;

        // RNG index table base (4-byte entries).
        public const int RngBase = 0x00D35ED8;
    }

    public static class StartupState
    {
        // Expected type: byte
        public const int MenuState = 0x00F407E4;
        
        // Expected type: uint
        public const int MoviePlay = 0x00D2A008;

        // Expected type: uint
        public const int StateD36FA0 = 0x00D36FA0;

        // Expected type: uint
        public const int StateD36FA4 = 0x00D36FA4;
    }

    public static class Functions
    {
        // Expected type: Action<byte, int, int> (MsDamageSetMotion)
        public const int MsDamageSetMotion = 0x0038CA20;
        
        // Expected type: Func<Chr*, Chr*, Command*, int, int*, int, int> (DmgCalcArmored)
        public const int DmgCalcArmored = 0x0038AAC0;
        
        // Expected type: Func<int, nint, int, nint, nint, int, nint, nint, nint, nint, int, int> (MsCalcDamageInternal)
        public const int MsCalcDamageInternal = 0x0038E630;

        // Expected type: Func<int, byte, int, int, int, int> (MsSetDamageInternal)
        // The only per-target damage apply in the engine, and the mod's authoritative
        // interception point: both commit passes (p5=0 and p5=1024) run through here.
        public const int MsSetDamageInternal = 0x0038F060;

        // Expected type: Func<uint, char*> (AtelGetEventName)
        public const int AtelGetEventName = 0x00479760;

        // MsAtelRequestCamera at FFX.exe+0x397BD0 — gate for in-game camera changes
        // (called from 12 sites including battle camera setup, scene transitions).
        // Used by the enemy-turn camera-lock feature to suppress camera moves while
        // the active actor is an enemy (so the player keeps the default view that
        // shows incoming attacks clearly).
        // Expected signature: int (int, int, int, int, int, int, int, int) — 8 params, return value unused at every observed call site.
        public const int MsAtelRequestCamera = 0x00397BC0;

        // MsBattleSpecialCameraPause at FFX.exe+0x39DDD0 (absolute 0x0079DDD0) —
        // engine's "enter cinematic camera mode" entry point. Boss / overdrive-class
        // enemy attacks (high-cue commands like 0x40XX) route through this path
        // instead of MsAtelRequestCamera, bypassing the existing camera-lock hooks.
        //
        // Signature: void (byte mode)
        //
        // Used by the Battle Camera Lock feature to cover the cinematic-camera path
        // alongside MsAtelRequestCamera and MsAtelRequestMagicCamera.
        //
        // Safety note: hook only Pause (not Free). Pause sets btl._24_1_=1 to enter
        // special mode; Free has an `if (btl._24_1_ != 0)` early guard so when we
        // suppress Pause, Free becomes a no-op naturally — no soft-lock risk.
        public const int MsBattleSpecialCameraPause = 0x0039DDB0;

        // DO NOT skip-orig FUN_007be090 (FFX.exe+0x3BE090) to hold the camera. It is not the
        // renderer apply — it is the camera op-queue interpreter + per-frame interpolator. The
        // renderer rebuilds its view matrix in FUN_007bc090 (0x7bc090) from the slot fields
        // regardless, so skipping the interpreter leaves half-interpolated dir/up vectors (=
        // camera angles that cannot occur in normal play) and stalls the op queue, which then
        // drains in a single frame and snaps. The engine's own freeze path is MsSetCameraMatrix
        // (0x7c0650): it sets ms_matrix_flag, FUN_007bc090 then skips the rebuild and the
        // interpreter writes its slot fields back from the same preset — both stay coherent.

        // AtelCameraPolarSet — FUN_007bad30 at FFX.exe+0x3BAD30 (absolute 0x007BAD30).
        // The actor-relative POLAR camera writer: the single body behind all six of
        // camSetBtlPolar / refSetBtlPolar / camSetBtlPolar2 / refSetBtlPolar2 /
        // camSetChrPolar / camSetChrPolar2 (wrappers at 0x007B8690..0x007B8730).
        //
        // Signature: int (AtelBasicWorker* worker, int p2, AtelStack* stack, int isCam, int variant) __cdecl
        //
        // The six script floats are NOT parameters — they are popped off the ATEL stack
        // inside (4x AtelPopStackFloat, then 2x AtelPopStackInteger; pops run reverse to
        // pushes, giving Square Enix's own prototype `(int, int, float, float, float, float)`).
        // A hook at entry therefore sees only the two wrapper constants, which are enough
        // to identify the opcode exactly:
        //     isCam=1 variant=1 -> camSetBtlPolar     isCam=0 variant=1 -> refSetBtlPolar
        //     isCam=1 variant=2 -> camSetBtlPolar2    isCam=0 variant=2 -> refSetBtlPolar2
        //     isCam=1 variant=3 -> camSetChrPolar     isCam=1 variant=4 -> camSetChrPolar2
        //
        // This — not MsAtelRequestCamera and not FUN_007bddd0 — is where a monster attack
        // script puts the camera. FUN_007bddd0/FUN_007bd7e0 only write per-axis tween
        // descriptors (mode, duration, elapsed); gating those makes the camera snap, not stop.
        public const int AtelCameraPolarSet = 0x003BADB0;

        // AtelCameraPosSet — FUN_007bb620 at FFX.exe+0x3BB620 (absolute 0x007BB620).
        // The absolute-position sibling, behind camSetPos (wrapper 0x007B91A0, passes p4=1).
        // Signature: void (AtelBasicWorker* worker, int p2, int* stack, int p4) __cdecl.
        // Same stack discipline: coordinates are popped inside, not passed.
        public const int AtelCameraPosSet = 0x003BB6A0;

        // ATEL stack pop primitives — needed to *suppress* a camera writer cleanly. A writer pops
        // its script args off the ATEL stack inside the callee; skipping the writer without those
        // pops desyncs the VM. Replicating them (same count, same order, same pop fn per value) keeps
        // the stack balanced. `size` is at offset 0 of AtelStack, so the AtelStack* doubles as the
        // int* the float pop wants — one pointer serves both.
        //   float AtelPopStackFloat  (AtelBasicWorker* worker, int*       ref_size) __cdecl  (decomp L991484)
        //   int   AtelPopStackInteger(AtelBasicWorker* work,   AtelStack* stack)    __cdecl  (decomp L991522)
        public const int AtelPopStackFloat   = 0x0046DE50;  // FUN_0086dde0 (absolute 0x0086dde0)
        public const int AtelPopStackInteger = 0x0046DF00;  // FUN_0086de90 (absolute 0x0086de90)

        // Freecam write path. MsCameraSetRect stamps a 4-float vec into a camera bank; param_2 bit 0
        // selects the eye bank (camSetPos, mode 1) vs the look-at/ref bank (mode 0). It validates the
        // id via MsCameraIDcheck and no-ops on a bad one, so a stale id cannot crash. camId comes from
        // the ATEL camera work slot: AtelGetCameraWorkAdrs(worker) returns a pointer whose *value is
        // the resolved id (0 until the game runs a real camera write).
        //   void MsCameraSetRect     (uint camId, uint mode, float* vec4) __cdecl  (decomp L874617)
        //   int  AtelGetCameraWorkAdrs(AtelBasicWorker* worker)           __cdecl  (returns int*)
        public const int MsCameraSetRect       = 0x003BF940;  // FUN_007bf8c0 (absolute 0x007bf8c0)
        public const int AtelGetCameraWorkAdrs = 0x0046AE50;  // FUN_0086ade0 (absolute 0x0086ade0)

        // int MsGetBattleScene(void) — the current battle scene/map id (e.g. 0xd40000). Tagged onto
        // a captured freecam angle so a hand-found camera can be pinned to the map it was found on.
        public const int MsGetBattleScene = 0x00382970;  // FUN_00782a30 (absolute 0x00782a30)

        // MsLimitUp at FFX.exe+0x3B15A0 (absolute 0x007B15A0) — the engine's overdrive charge
        // primitive, and the only correct way to add gauge. Signature (decomp L863356):
        //   uint MsLimitUp(uint chr_id, Chr* chr, uint amount)  __cdecl, returns the applied amount
        // Writes Chr+0x5BC, clamps against Chr+0x5BD, early-returns on btl.debug.never_charge_
        // overdrive, and applies the Double/Triple-Overdrive and aura multipliers internally
        // (L863374-863405). A raw write to limit_charge bypasses all of that.
        // NOTE: Fahrenheit's generated `void MsLimitUp()` (call.g.cs) has the wrong arity.
        public const int MsLimitUp = 0x003B1590;

        // MsAtelRequestMagicCamera at FFX.exe+0x398010 (absolute 0x00798010) — sibling
        // of MsAtelRequestCamera, called from 6 sites during magic spell casts to
        // request the spell-specific camera animation. Bypasses MsAtelRequestCamera
        // entirely, so a separate hook is needed for the enemy-turn camera lock to
        // cover spell casts as well as normal attacks.
        //
        // Signature: byte (int p1, int p2, uint p3, int p4, int p5, int p6, uint p7, int p8, int p9)
        // Returns a byte camera-id; engine's "no camera" sentinel is 0xFF (engine line
        // 841782 default). Suppression branch must return 0xFF, not 0.
        public const int MsAtelRequestMagicCamera = 0x00398000;

        // MsBtlSetHitEffect at FFX.exe+0x39EC60 (absolute 0x0079EC60) — engine's
        // "registered hit effect" emitter. Global-handle sibling of 0x0039EBC0:
        // both route through the same dispatch core (FUN_0079EB30), but this one
        // resolves the effect ID against the global handle `btl._140_4_` rather
        // than the per-character handle `chr->field_0x4c`. That means the effect
        // table is universal across all actors — which is why it works on PC slots
        // where the per-character variant crashes.
        //
        // Signature: void (byte chr_id, int p2, int effect_id [, int extra]) —
        // same call shape as the per-character variant; the existing 4-arg delegate
        // continues to work.
        //
        // Script-side equivalent: opcode `Battle.btlSetHitEffReg [70E6h]`
        // (declared in target/audio/ath/btlatel.ath:234).
        //
        // Used to fire the Sentinel barrier visual (effect 0x4A) on the parrying
        // character on a successful parry.
        public const int MsBtlSetHitEffect = 0x0039EC40;

        // MsScreenSetShake — FUN_007c5680 at FFX.exe+0x3C5680 (Ghidra VA 0x007c5680). The
        // engine's native screen shake, exposed to ATEL scripts as the camSetShake family
        // (camSetShake / camSetShakeB / camSetScreenShake / …; the B variants differ only in
        // the `mode` the opcode passes). Signature (cdecl, recovered from the opcode handler
        // FUN_007bb7c0 and the evaluator FUN_007c5080):
        //   void MsScreenSetShake(uint screen_id, uint axis_mask, byte mode, float freq,
        //                         ushort duration, ushort amplitude, byte randomness)
        // screen_id < 3 (bounds-checked); axis_mask & 1 = axis A, & 2 = axis B.
        // The evaluator computes, per frame and per axis:
        //   jitter = randomness ? (randomness>>1) - brnd(9) % randomness : 0
        //   offset = sin(phase) * amplitude * (32 + jitter)/32 * envelope
        //   mode 1: envelope = remaining/total          -> decays to zero  (impact)
        //   mode 2: envelope = (total - remaining)/total -> ramps up
        // `duration` is stored as both remaining and total, which is what drives the envelope.
        // `freq` is the per-frame phase step, not an amplitude. The applier (FUN_007bc090) runs
        // every frame and stops on its own once the mode byte is cleared — a mod only has to
        // fire it once; no per-frame driving, no cleanup.
        public const int MsScreenSetShake = 0x003C56F0;

        // MsScreenResetShake — FFX.exe+0x3C5650 (Ghidra VA 0x007c5650). Clears mode + phase for
        // both axes of a screen. ATEL: camResetShake. Used to cancel a shake early.
        public const int MsScreenResetShake = 0x003C56C0;

        // MsSetMotion — FUN_007ab380 at FFX.exe+0x3AB380 (Ghidra VA 0x007ab380). The
        // engine's battler motion setter. Signature (cdecl):
        //   undefined4 MsSetMotion(int slot, int motion_id, int chr_id, byte p4,
        //                          int p5, int p6, int p7)
        // Engine's own Defend code (MsDefenseStartProcess) calls it as
        //   MsSetMotion(slot, 0x3C|statusbit, 0, 0, 1, 0, 0)   // 0x3C/0x3D = guard brace
        //   MsSetMotion(slot, 0x34,           0, 0, 1, 0, 0)   // 0x34     = covered pose
        // i.e. the last two args are 0 (no context / no out-ptr) — that exact pattern is
        // the safe call shape used by the FX/Motion lab to preview arbitrary motion ids.
        public const int MsSetMotion = 0x003AB360;

        // MsSetChrVisible — FUN_00796670 at FFX.exe+0x396670 (Ghidra VA 0x00796670).
        // Dedicated battler-visibility setter: void MsSetChrVisible(int slot, int visible)
        // (thin wrapper over FUN_00797090(slot, 2, visible)). Engine usage shows (slot, 0)
        // to hide and (slot, flag) to show. Used by the FX lab's experimental "Restore char"
        // button to re-show a model that a status/death effect (e.g. petrify-shatter) hid.
        public const int MsSetChrVisible = 0x00396660;

        // MsResetBindEffect — FUN_00788f20 at FFX.exe+0x388F20 (Ghidra VA 0x00788f20).
        // Per-character effect reset: void MsResetBindEffect(byte slot). Clears the slot's
        // own effect object (field_0x1f=0; Ch_EffectSetEffectLevel(obj,0); op_et_bindeff_off_signal).
        // This is the engine's own per-char teardown (called from MsBtlChrFree) — a TARGETED
        // clear, unlike the global op_et_battle_effect_free/init (MsEtEffectStop/Set) that
        // depends on battle-lifecycle state and crashed. Worst case here is a no-op, not a crash.
        public const int MsResetBindEffect = 0x00388E60;

        // MsEffectEndMotion — FUN_00787a10 at FFX.exe+0x387A10 (Ghidra VA 0x00787a10).
        // The engine's "a battler's motion just finished" handler: void(uint chr_id, int mode).
        // Hooked observe-only (call orig + log) to measure how long a played motion runs — the
        // data we need to decide whether the parry window / whiff recovery should be driven by
        // the real animation length instead of the static FINAL_PARRY_SPEC windows.
        public const int MsEffectEndMotion = 0x00387950;

        // MsInsertBtlCommand at FFX.exe+0x3929D0 — engine-public "queue a battle
        // command for a chr to execute as the next available action".
        //
        // Signature: int (AttackCue *cue, int param_2, int param_3, int chr_id)
        //   - cue: a fully-populated AttackCue (size 0x48 bytes — see
        //     Fahrenheit.FFX.Battle.AttackCue). Required fields:
        //       cue->attacker_id           = chr who will execute the command
        //       cue->command_count         = 1 (single command)
        //       cue->command_list[0].(*)   = command_ids[0] = command id (e.g.
        //         0x4000 for basic Attack); command_ids[1] = 0xFF sentinel;
        //         targets = bitmask of slot ids
        //   - param_2: 0 (unknown — always 0 in the engine's own usage)
        //   - param_3: 0 or 1 (other values rejected by an internal guard)
        //   - chr_id:  the target/context chr id (the slot the cue is "responding
        //     to"; engine's auto-counter passes the original incoming attacker)
        //   - returns 0 on success, -1 on validation failure.
        //
        // Discovered call pattern: see MsAutoRelifeProcess (FFX.exe+0x38C780) at
        // line 832786 in the decompile snapshot — that's the engine's own auto-
        // counter implementation. We follow the same convention for the streak
        // counter-attack feature so the cue interleaves cleanly with CTB.
        public const int MsInsertBtlCommand = 0x003929B0;

        // MsDmgCalc_CheckHit at FFX.exe+0x38A950 (absolute 0x0078A950) — engine's
        // accuracy/evasion roll inside the damage modifier pipeline. Returns
        // CheckHitResult enum (HIT / MISS / MISS_ALIVE). Called from MsDmgCalc only
        // (1 caller). Body reads command->flags_misc>>3&7 as hit_formula selector;
        // uses target.evasion vs user.accuracy plus aim/reflex/luck/jinx stacks.
        // Honors btl.debug.always_hit / never_hit. counter == EVADE_COUNTER forces
        // MISS (Counter / Reflex auto-ability path).
        //
        // Signature: int (Chr* user, Chr* target, Command* command, DamageInfo* info, int counter)
        //
        // Used by the disable-native-evasion feature to override MISS → HIT for PC
        // targets only (aeons and monsters fall through to vanilla evade).
        public const int MsDmgCalcCheckHit = 0x0038A890;

        // ---------------------------------------------------------------------
        // Adopted from Fahrenheit's generated `FhFfx.FhCall.__addr_*` constants so the
        // mod owns every address it hooks. Upstream is renaming the FhCall surface ahead
        // of alpha11 (`h_METHOD` -> `METHOD`, delegates gain a `d_` prefix); by carrying
        // these ourselves the rename cannot reach us. Values are byte-identical to
        // alpha10's call.g.cs.
        // ---------------------------------------------------------------------
        public const int MsExeInputCue             = 0x003B2290;
        public const int MsSetDamage               = 0x0038D980;
        public const int MsCalcDamage              = 0x00389740;
        public const int MsActionRequest           = 0x003ACEB0;
        public const int MsCalcCommand             = 0x003892E0;
        public const int MsCheckStatusBeforeAction = 0x003AF4F0;
        public const int MsLimitTypeDamageCheck    = 0x003B0D50;
        public const int OpEtBattleGenkoCounterGet = 0x003FB240;
    }
}
