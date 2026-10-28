// SPDX-License-Identifier: MIT

namespace Fahrenheit.Mods.Parry;

[FhLoad(FhGameId.FFX)]
public unsafe sealed partial class ParryModule : FhModule
{
    private const int PartyActorCapacity = 10;
    private const int EnemyActorCapacity = 10;
    private const uint PlayerTargetMask = (1u << PartyActorCapacity) - 1u;
    private const int MaxAttackCueScan = 64;
    private const float BattleFrameRate = 30f;
    private const float FrameDurationSeconds = 1f / BattleFrameRate;
    private const float ParriedTextSeconds = 1.0f;
    private const float ParryMissedTextSeconds = 1.0f;
    // Number of consecutive parries on a single slot at which the observe-only
    // streak path emits a "STREAK READY" log entry. Tuning point for the future
    // counter-attack feature; kept const for now so the threshold is one obvious
    // edit away when we wire MsInsertBtlCommand.
    private const byte ParryStreakObserveThreshold = 2;
    private const int DebugLogRingCapacity = 500;
    private const int CueHistoryRingCapacity = 64;
    private const int DebugTurnRowCapacity = 500;
    // Community-confirmed signature: int __cdecl MsSetDamage(byte param_1, int param_2, int param_3)
    // param_1 = attacker battler slot
    // param_2 = target party slot (>= 0) for the actual damage call, or -5 for setup/finalization
    // param_3 = 0 for setup/target calls, 0x400 (1024) for finalization (triggers MsAfterDamageProcess)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MsSetDamageProbe(byte param_1, int param_2, int param_3);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint MsExeInputCueProbe(uint actorId, byte mode);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MsDamageSetMotionProbe(byte target, int p2, int p3);
    // Community-confirmed signature for MsCalcDamage (March 2026 Discord findings).
    // Pointer params use nint — hook does not dereference them.
    // p11 = hit count per target.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MsCalcDamageProbe(
        int user_id, nint user_chr, int target_id, nint target_chr,
        nint command, int command_id,
        nint p7, nint p8, nint p9, nint p10, int p11);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DmgCalcArmoredProbe(Chr* user, Chr* target, Command* command, int p4, int* p5, int damage);

    // Community name: MsCalcDamageInternal / FUN_0078e680 at FFX.exe+0x38e680.
    // Inner per-hit damage calculator called from within MsCalcDamage.
    // Ghidra signature: 11 params matching MsCalcDamage forwarding shape.
    // Active interception hook: returns 0 (skipping orig) when a parry expiry is active,
    // preventing both damage_hp and native DamageInfo buffer writes per hit.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MsCalcDamageInternalProbe(
        int user_id, nint user_chr, int target_id, nint target_chr,
        nint command, int command_id,
        nint p7, nint p8, nint p9, nint p10, int p11);

    // FUN_0078f0b0 at FFX.exe+0x38F0B0 — per-target native commit point.
    // Internally calls MsSubHP → MsDamageCheckDeath → MsDamageSetMotion for each targeted slot.
    // Returning early atomically prevents HP reduction, death-latch, status commit, and flinch.
    // Phase 1 reactive hook: returns early when _parryExpiry[param_3] is active for the target slot.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MsSetDamageInternalProbe(int param_1, byte param_2, int param_3, int param_4, int param_5);

    // MsAtelRequestCamera — central gate for camera change requests during gameplay.
    // 8 params; return value unused at every observed call site. We intercept this
    // when an enemy turn is in progress (and the lock is enabled) to keep the camera
    // pinned to the player view so incoming attacks remain readable for parry timing.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MsAtelRequestCameraProbe(int p1, int p2, int p3, int p4, int p5, int p6, int p7, int p8);

    // MsAtelRequestMagicCamera — sibling of MsAtelRequestCamera for magic-spell
    // camera changes. 9 params, returns byte camera-id (0xFF = "no camera"
    // sentinel). Hooked alongside MsAtelRequestCamera so the enemy-turn camera
    // lock covers both normal-attack and magic-spell camera paths.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte MsAtelRequestMagicCameraProbe(int p1, int p2, uint p3, int p4, int p5, int p6, uint p7, int p8, int p9);

    // MsBattleSpecialCameraPause — engine entry point for cinematic camera mode
    // (boss / overdrive attacks). Hooked alongside MsAtelRequestCamera /
    // MsAtelRequestMagicCamera so the Battle Camera Lock covers all three camera
    // paths. Signature: void (byte mode). __cdecl. Suppression branch is a bare
    // `return` — no call to the original, no return value to fake.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MsBattleSpecialCameraPauseProbe(byte mode);

    // FUN_007bad30 — the actor-relative polar camera writer shared by all six
    // camSetBtlPolar/refSetBtlPolar/camSetChrPolar variants. Observe-only: this is
    // where a monster attack script actually moves the camera, so the probe tells us
    // which opcode drives a given pan. The six script floats live on the ATEL stack,
    // not in the parameter list — `isCam` and `variant` are the wrapper constants and
    // identify the opcode on their own. See ExternalMemoryOffsetMap.AtelCameraPolarSet.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AtelCameraPolarSetProbe(int worker, int p2, int stack, int isCam, int variant);

    // FUN_007bb620 — the absolute-position sibling, behind camSetPos. Observe-only.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AtelCameraPosSetProbe(int worker, int p2, int stack, int p4);

    // ATEL stack pops, called (not hooked) to balance the stack when a camera writer is suppressed.
    // `size` is at offset 0 of AtelStack, so the same pointer serves the float pop's int* and the
    // int pop's AtelStack*. Cached in _popStackFloat/_popStackInteger so the per-frame writers skip the
    // handle lookup entirely.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate float AtelPopStackFloatFn(int worker, int stack);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AtelPopStackIntegerFn(int worker, int stack);

    // MsBtlSetHitEffect — engine's registered-hit-effect emitter (global handle).
    // Used directly (no hook) on parry success to fire the Sentinel barrier
    // visual (effect 0x4A) on the parrying character. Routes through the global
    // particle handle so it is safe on PC slots.
    // Script analog: Battle.btlSetHitEffReg [70E6h].
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MsBtlSetHitEffectProbe(byte chr_id, int p1, int effect_id, int p3);

    // MsScreenSetShake — the engine's native screen shake (ATEL: camSetShake family).
    // The callee truncates mode/duration/amplitude/randomness to byte/ushort itself; they are
    // declared 32-bit here because that is what the cdecl stack slots actually are.
    // See ExternalMemoryOffsetMap.Functions.MsScreenSetShake for the recovered semantics.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MsScreenSetShakeProbe(
        uint screen_id, uint axis_mask, uint mode, float freq, uint duration, uint amplitude, uint randomness);

    // MsSetMotion — battler motion setter. It enqueues an ATEL motion script onto the actor's own
    // execspace (slot+5) plus a shared control execspace (3); different actors therefore animate in
    // parallel, and a second call on the same actor *restarts* its script rather than queueing.
    //
    // The 3rd parameter is NOT a chr_id. Ghidra names it that only because MsDamageSetMotion happens
    // to pass its own chr_id through; MsSetMotion merely tests it against zero (FFX.exe.c:857945):
    //   != 0  -> "hold": writes field_0xdf2 and holds field_0x432 (motion-active) until MsTerminateMotion
    //   == 0  -> clears field_0x432 again at the tail
    // The engine's Defend code passes 0 (a guard brace should not make anyone wait); the native
    // damage reaction passes non-zero. We inherited the 0 from the Defend call shape.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MsSetMotionProbe(int slot, int motion_id, int hold_motion_active, byte p4, int p5, int p6, int p7);

    // MsSetChrVisible(slot, visible) — re-show a battler model hidden by a status/death effect.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MsSetChrVisibleProbe(int slot, int visible);

    // MsResetBindEffect(slot) — engine's per-character effect reset (used in MsBtlChrFree).
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MsResetBindEffectProbe(byte slot);

    // MsLimitUp(chr_id, chr, amount) — native overdrive charge. Returns the amount actually
    // applied (after Double/Triple-Overdrive and aura multipliers). See ExternalMemoryOffsetMap.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint MsLimitUpProbe(uint chr_id, Chr* chr, uint amount);

    // MsEffectEndMotion(chr_id, mode) — engine's "battler motion finished" handler. Hooked
    // observe-only to measure played-motion durations (animation-driven-timing research).
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MsEffectEndMotionProbe(uint chr_id, int mode);

    // MsInsertBtlCommand — engine call to queue a battle command for a chr to
    // execute as the next available action. Used directly (no hook) by the
    // streak counter-attack feature to inject a basic Attack from the parrier
    // onto the originally-attacking enemy at cue-clear time.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MsInsertBtlCommandProbe(AttackCue* cue, int param_2, int param_3, int chr_id);

    // MsDmgCalc_CheckHit — engine accuracy/evasion roll. Hooked to override
    // MISS → HIT for real PCs (not aeons) so native evasion is disabled while
    // the manual dodge system replaces it. RNG state is preserved by always
    // invoking the original first; only the return value is overridden.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MsDmgCalcCheckHitProbe(Chr* user, Chr* target, Command* command, void* info, int counter);

    private enum CommandIdSource
    {
        None,
        CueCommandInfo,
        CueOffsetCandidate,
        LastComFallback
    }

    private enum CommandIdConfidence
    {
        None,
        Low,
        Medium,
        High
    }


    private readonly struct ResolvedCommandInfo
    {
        public readonly ushort CommandId;
        public readonly string Label;
        public readonly string Kind;
        public readonly string DamageType;
        public readonly CommandIdSource Source;
        public readonly CommandIdConfidence Confidence;

        public bool HasCommandId => CommandId != 0;
        public bool HasLabel => !string.IsNullOrWhiteSpace(Label);

        public ResolvedCommandInfo(
            ushort commandId,
            string label,
            string kind,
            string damageType,
            CommandIdSource source,
            CommandIdConfidence confidence)
        {
            CommandId = commandId;
            Label = label ?? string.Empty;
            Kind = kind ?? string.Empty;
            DamageType = damageType ?? string.Empty;
            Source = source;
            Confidence = confidence;
        }

        public static ResolvedCommandInfo None => new(
            commandId: 0,
            label: string.Empty,
            kind: string.Empty,
            damageType: string.Empty,
            source: CommandIdSource.None,
            confidence: CommandIdConfidence.None);
    }

    private readonly struct ParryInputContext
    {
        public readonly bool HasParryableCue;
        public readonly AttackCue Cue;
        public readonly byte CueIndex;
        public readonly uint PartyMask;

        public ParryInputContext(bool hasParryableCue, AttackCue cue, byte cueIndex, uint partyMask)
        {
            HasParryableCue = hasParryableCue;
            Cue = cue;
            CueIndex = cueIndex;
            PartyMask = partyMask;
        }

        public static ParryInputContext None => new(false, default, 0, 0);
    }

    /// <summary>
    ///     Per-slot telemetry record for one attack turn.
    ///     Reset after flush at turn end. Written unconditionally; flush output is gated on _optionLogging.
    /// </summary>
    private struct AttackTelemetry
    {
        public bool CalcDamageFired;
        public bool CalcDamageIntercepted;
        public bool SetMotionFired;        // MsDamageSetMotion p2=5 (party flinch)
        public bool SetDamageTargetFired;  // MsSetDamage p2=target
        public uint HpBeforeFinalization;
        public uint HpAfterFinalization;
        public int CommandId;
    }


    // Runtime-only mutable state lives here to keep transitions centralized and auditable.
    private struct ParryRuntimeState
    {
        // Canonical press-based parry state machine (FINAL_PARRY_SPEC.md).
        // All R1-press gating goes through InputState. ParryWindowActive is a legacy
        // convenience mirror kept true while InputState == Open.
        public ParryInputState InputState;
        public bool ParryWindowActive;
        public byte CurrentAttackerId;
        public byte CurrentCueIndex;
        public uint CurrentPartyTargetMask;
        public uint CurrentCueSignature;
        public int ParryWindowRemainingTicks;
        // Whiff recovery lockout: countdown (in battle ticks) that approximates the "return to
        // normal stance" animation commitment. Non-zero only while InputState == WhiffLockout.
        public int WhiffLockoutRemainingTicks;
        public int WhiffLockoutTotalTicks;
        public bool AwaitingTurnEnd;
        public int ParryWindowElapsedTicks;
        public bool ParryWindowSucceeded;
        public bool SuccessIndicatorActive;

        public bool AttackCueClampWarned;
        public float ParriedTextRemainingSeconds;
        public float ParryMissedTextRemainingSeconds;
        public uint LastParriedTargetMask;
        public ulong LastDispatchConsumedFrame;
        public byte LastDispatchConsumedAttackerId;
        public byte LastDispatchConsumedQueueIndex;
        public ulong LastCorrelationSkipFrame;

        public ulong CueFirstSeenFrame;
        public ulong WindowOpenFrame;
        public float WindowOpenTimestampSeconds;
        public float WindowDurationSecondsAtOpen;

        // Set when an impact is detected for this turn but the parry window was not open.
        // Prevents the Anfunkeln finalization fallback from resolving as PARRIED when the
        // player opens the window after damage was already dealt and the poll missed it.
        public bool TurnImpactMissedSeen;
        // Attacker ID recorded when TurnImpactMissedSeen was set. Used to avoid clearing
        // the flag when the window opens for the same attacker that already dealt damage.
        public byte TurnImpactMissedAttackerId;

        // Status-block display: shown where "Parried" would appear when a hit is silently
        // skipped because the target's battle status prevents them from being parried.
        public float StatusBlockTextRemainingSeconds;
        public string StatusBlockLabel;

        public static ParryRuntimeState CreateDefault() => new()
        {
            InputState = ParryInputState.Ready,
            LastParriedTargetMask = 0,
            LastDispatchConsumedQueueIndex = 0xFF,
            StatusBlockLabel = string.Empty
        };
    }

    // Not a toggle: damage negation IS the mod. With it off a successful parry does nothing,
    // which is what the "enabled" master switch is for. Kept as a named constant so the guard
    // sites keep documenting where negation applies.
    private const bool _optionNegateDamage = true;
    // Native-engine probe channel. When false (default), probe queue is inert
    // and no native-probe events are recorded. When true, probe-tagged events
    // are pushed onto _probeRingBuffer during hook execution and drained once
    // per pre-update tick into the session debug log. Independent of
    // _optionLogging — enabling probes does NOT enable other logging, and
    // turning logging on does NOT enable probes.
    //
    // No production hook currently emits probe events. The wiring is in place
    // ahead of Stage-1 observe probes per the KB probe plan.
    // NOTE: leave OFF by default. Enabling this installs 7 additional Stage-1 native probe hooks
    // (install_stage1_probes) that are a separate research feature and crash at battle start when
    // untested. Camera/overlay debugging does NOT need this — use _optionCameraProbe + logging.
    // Controls which turns trigger battle-camera suppression. Off passes every call through to
    // the engine unchanged.
    //
    // AllTurns is the default, because EnemyTurnsOnly leaves the two pans that hurt most. The
    // camera-writer probe shows why: across three logged fights, not one cam*/ref* opcode ever
    // fired while an enemy action was in flight — every write landed at turn_active=False. The
    // pans a player actually complains about are a finishing blow (the player's own turn) and an
    // item-use swing, and both are invisible to a lock that only watches enemy turns. You cannot
    // time an attack you are not looking at.
    // Visual feedback effect on a successful parry: fires the Sentinel barrier
    // visual (effect 0x4A — golden ring / shield-of-air spatial particle) on
    // the parrying character via the global-handle emitter MsBtlSetHitEffect
    // (0x0039EC60). Effect is PC-safe: the engine fires this exact call on party
    // actors when an attack lands on a Sentinel-statused PC (forensic ref:
    // FUN_0079E530). Default-on.

    // Deterministic motion termination for the dodge. The dodge's ATEL motion script is restarted by
    // every press and, left alone, only ends when it runs out — which is why spamming the button used
    // to stall the enemy's charging cast. Instead of swallowing presses, we guarantee that every dodge
    // *terminates*: on a new press (clean restart), on the resolving hit, and on window expiry.
    //
    // MsEffectEndMotion is the engine's own end-of-motion entry. It is a NO-OP unless the ATEL worker
    // has actually started the motion, which it signals by setting Chr+0x3f3 (motion-disable) — so we
    // gate on that byte rather than guessing.
    private const int ChrMotionDisableOffset = 0x3f3;

    // MsEffectEndMotion's `mode`: 3 issues the return-to-idle motion but SKIPS MsSetChrMoveFlag(chr,0)
    // (FFX.exe.c:827958). That matters because motion != move: the step-out and walk-back are driven by
    // the move machine (Chr+0x415), not by the animation. Mode 3 should end the animation while leaving
    // the walk-back running; mode 0 would clear the move flag and risk stranding the actor. Untested —
    // this const exists so both can be compared in-game.
    private const int DodgeEndMotionMode = 3;

    // Impact screen shake: fire the engine's own decaying screen shake when a hit is *met* —
    // on a successful parry, and only there. Every dodge avoids the hit, PERFECT included; PERFECT
    // merely reports that the dodge landed inside the parry window. Do NOT read
    // CombatLabelPalette.preciseTiming (which tints PARRIED and PERFECT alike) as "both met the
    // hit" — it groups them by timing readability, not by impact. Default-on.

    // Shake parameters for MsScreenSetShake. The engine evaluates, per axis:
    //   offset = sin(phase) * amplitude * (32 + jitter)/32 * (remaining/total)
    // with `phase += freq` once per frame and per-axis phase/freq slots (+0x13C/+0x144 and
    // +0x140/+0x148). Both phases start at 0, so firing ONE call with axis_mask = 3 gives both axes
    // the same phase, frequency and amplitude — the offset then traces a straight 45° line, not a
    // shake. That is the bug the first version shipped; it read as "very vertical, both-sided".
    //
    // We therefore fire the two axes SEPARATELY with decorrelated frequencies. The shape of the
    // values follows standard game-feel practice for an impact shake (Squirrel Eiserloh, GDC 2016,
    // "Juicing Your Cameras With Math"): short, high-frequency, low-amplitude, decaying, with
    // independent axes. Cinemachine's default impulse is likewise ~0.2 s.
    //
    // Converting to this engine: Hz = freq * 30 / (2*PI) at the 30 fps battle tick, and Nyquist caps
    // us at 15 Hz — so the usual 10-20 Hz impact band has to sit at its lower edge.
    private const uint ImpactShakeScreenId   = 0;      // screen_id must be < 3
    private const uint ImpactShakeAxisA      = 1;      // axis_mask bit 0
    private const uint ImpactShakeAxisB      = 2;      // axis_mask bit 1
    private const uint ImpactShakeModeDecay  = 1;      // envelope = remaining/total → fades out

    private const float ImpactShakeFreqA     = 1.7f;   // ~8.1 Hz
    private const float ImpactShakeFreqB     = 2.3f;   // ~11.0 Hz — ratio 1.35, so the axes never relock
    private const uint ImpactShakeAmpA       = 9;      // a parry is a lateral impact: favour one axis
    private const uint ImpactShakeAmpB       = 5;
    private const uint ImpactShakeDuration   = 12;     // ticks at 30 fps ≈ 0.40 s — a single-slot parry (fires often; kept snappy)
    private const uint ImpactShakeRandomness = 8;      // the engine's own jitter: ±4 around the amplitude

    // A whole-party parry (all three active PCs parried the same attack) fires a longer, heavier
    // shake to sell the moment. 24 ticks ≈ 0.80 s — double the single-parry shake.
    private const uint ImpactShakeDurationWholeParty = 24;  // ticks @30fps ≈ 0.80 s
    private const int FullPartyParryCount = 3;

    // Streak counter attack: when a slot completes a defensive streak (every
    // targeted slot in a cue parried at least once and cumulative streak ≥
    // ParryStreakObserveThreshold), queues a basic Attack from the parrier
    // onto the original attacker via MsInsertBtlCommand. Default-off pending
    // in-game verification of the MsInsertBtlCommand call address and AttackCue
    // layout. Off → log-only behaviour (the observation still runs and the
    // debug overlay still surfaces "streak ready" events).
    // Disable native FFX evasion for real player characters (chr->ram.is_aeon == false
    // and chr->chr_id < 0x14). Aeons and monsters keep vanilla evasion. The hook always
    // invokes the original MsDmgCalc_CheckHit (so the engine's RNG advance is preserved),
    // then overrides MISS → HIT for PC targets when this option is enabled and we've
    // cached the HIT enum integer value (auto-discovered via observation; see
    // _checkHitObserved*).
    //
    // Native PC evasion is unconditionally disabled by h_ms_dmg_calc_check_hit — the manual dodge
    // system replaced it, and a PC that evades natively never reaches our impact path. There is
    // deliberately no toggle: turning it back on is a bug state, not a game mode.
    // Counter for suppressed camera requests, reset on mode change — surfaced
    // in debug logging only when both _optionLogging and _optionBattleCameraLockMode
    // is not Off, to avoid log spam in release builds.
    private long _enemyCameraLockSuppressCount;
    private int _enemyMagicCameraLockSuppressCount = 0;
    private int _battleSpecialCameraLockSuppressCount = 0;
    private long _cameraWriterSuppressCount;
    // Cached ATEL stack-pop function pointers, resolved once on first camera-writer suppression.
    private AtelPopStackFloatFn? _popStackFloat;
    private AtelPopStackIntegerFn? _popStackInteger;
    // CheckHitResult enum auto-discovery. The enum has 3 members (HIT/MISS/MISS_ALIVE)
    // but the integer values weren't exported by Ghidra's datatype dumper. We learn
    // them by observation:
    //   - First N>=5 PC-target invocations returning the same value → assume HIT (most
    //     attacks land in normal play).
    //   - Any subsequent PC-target return that differs from cached HIT → MISS.
    //     (MISS_ALIVE is rare — only fires for status-only commands against non-zombie
    //     targets, command->flags_misc & 0x800000.)
    //   - Once both HIT and MISS are known, the override fires.
    // Settings file may also pre-seed these via persisted values (set by the user
    // after observing logs). Default null = unknown.
    /// <summary>
    ///     The value _DmgCalc_CheckHit returns for a hit. Named in the Ghidra type export rather
    ///     than discovered at runtime:
    ///
    ///     <code>typedef enum CheckHitResult_4 { HIT=0, MISS=1, MISS_ALIVE=2 } CheckHitResult_4;</code>
    ///
    ///     and the function's own body returns those symbols rather than numbers, so the decompiler
    ///     resolved the enum from two directions. This replaced an auto-discovery that watched the
    ///     return value for real PC targets and accepted it after five consecutive agreements -
    ///     which meant the first battles of a session behaved differently from the later ones, and
    ///     had to be persisted to hide that.
    /// </summary>
    private const int CheckHitHit = 0;
    private long _checkHitOverrideCount = 0;
    private long _checkHitObservationCount = 0;
    // ── Dodge / native-evade (Circle ○) ─────────────────────────────────────
    // Reactive dodge on Circle: on press, the step-out MOVEMENT starts immediately for each
    // targeted PC — we call the engine's own case-1 evade (MsDamageSetMotion param_2=1 → avoid
    // move-mode ram.field_0x425 via FUN_0078f090 → positional step-back + motion 0xC). A cue-based
    // window is armed at the same time; if a hit from that attacker lands while the window is
    // valid, the MsDamageSetMotion hook NEGATES the damage + suppresses the flinch (the movement
    // already plays, so no second trigger). Attacker-keyed → AoE. Never feeds the streak/counter
    // path (no counterattack). The engine drives the return/walk-back.
    private bool _dodgeWindowActive = false;
    private int _dodgeWindowRemainingTicks = 0;
    // Ticks left before another step-out is accepted. Armed from the difficulty model after a
    // successful one; zero on Debug. Counts down in on_pre_update, unconditionally — a cooldown
    // that only ran while a cue was live would not survive the gap between two enemy actions.
    private int _dodgeCooldownRemainingTicks = 0;
    private byte _dodgeArmedAttackerId = 0;
    // CueFirstSeenFrame of the attack the dodge was armed for — the negation only applies to THIS
    // attack instance, so a multi-hit of the same attack is fully dodged but a fresh attack from
    // the same attacker landing in the still-open window is NOT auto-dodged without a new press.
    private ulong _dodgeArmedCueFrame = 0;
    // Party slots the dodge was armed for, snapshotted at press time from the cue's filtered,
    // parryable target mask (the same set that receives the step-out). A slot may resolve as
    // evaded at impact only if its bit is set here — the engine drives the p5=0/motion commit for
    // EVERY party slot, so the cue-wide window/attacker checks alone cannot keep an untargeted slot
    // from resolving. Mirrors the parry's per-slot _parryExpiry discipline. Refreshed on each fresh
    // press; cleared with _dodgeResolvedAtImpactMask at cue end and on runtime reset.
    private uint _dodgeArmedTargetMask = 0;
    // Slots that have resolved as evaded for the current cue. The dodge equivalent of the parry's
    // LastParriedTargetMask: durable, survives the wall-clock window and any cue mutation, and is
    // cleared only at cue end. Gates BOTH MsSetDamageInternal commit passes (p5=0 and p5=1024), so
    // a delayed-finalization attack cannot land HP, death or status after the window closed. Kept
    // separate from _parryResolvedAtImpactMask so no "PARRIED" text is drawn over the "DODGE" text.
    private uint _dodgeResolvedAtImpactMask = 0;
    // Slots whose dodge landed inside the (tighter) parry window — "PERFECT" instead of "DODGE",
    // plus the same overdrive boost a parry grants. Decided at impact, because the durable dodge
    // marker lets the damage commit arrive long after the wall-clock window has closed. Lives with
    // the label (cleared when the DODGE/PERFECT text expires), not with the cue.
    private uint _dodgeTextPerfectMask = 0;
    private long _dodgeEvadeCount = 0;
    // Step-out driven minimally: the avoid move-mode byte (Chr+0x425, what FUN_0078f090 sets)
    // + the evade animation (motion 0xC). Deliberately NOT via MsDamageSetMotion, whose case 1
    // also sets the hit-terminate flag field_0x433 — read by battle-update logic and, poked
    // out-of-band during a multi-phase cast chargeup, it desyncs the enemy action → soft-lock.
    private const int ChrEvadeMoveModeOffset = 0x425;
    // Move PHASE byte (Chr+0x415, from the evade-probe log): 0x09 = stepping out, 0x01 = walking
    // back, 0x00 = idle/home. Used to pace re-presses to the actual move duration.
    private const int ChrEvadeMovePhaseOffset = 0x415;
    // Last-attacker id (Chr+0xdef). MsDamageSetMotion's case 1 refreshes it from chr->attacker_id_
    // right before playing motion 0xC; the motion's battle-ATEL script reads it to orient the
    // step-out away from that attacker. Never read from C code — only written there.
    private const int ChrLastAttackerIdOffset = 0xdef;
    private const int EvadeMotionId = 0xC;
    // The dodge window is armed from ParryDifficultyModel.GetDodgeWindowTicks(_optionDifficulty)
    // and counts down in battle ticks, once per PreUpdate — same as the parry window.
    // "DODGE" success text overlay (mirrors the parry "PARRIED" overlay), per targeted slot.
    private float _dodgeTextRemainingSeconds = 0f;
    private uint _dodgeTextTargetMask = 0;
    // Per-appearance random seed so each DODGE/PARRIED label's entry (skew/rotation/scale) varies a
    // little, so no two labels land identically. (An earlier comment here claimed FFX has no
    // camera shake; it does — see ExternalMemoryOffsetMap.Functions.MsScreenSetShake.)
    private static readonly Random _labelRng = new();
    private float _dodgeTextSeed = 0f;
    private float _parriedTextSeed = 0f;
    private float next_label_seed() => (float)_labelRng.NextDouble() * 1000f;
    // Debug probe: logs Chr+0x415/0x425/0x4AC (move-mode/avoid/motion-type) + world position for
    // the stepped-out slot(s) for a short window after a step-out — to find the move distance/mode.
    private uint _dodgeProbeSlotsMask = 0;
    private int _dodgeProbeFramesLeft = 0;
    // Native parry block (A): flag the parrying char as guarding (ChrRam+0x19A) so the engine's
    // MsDamageSetMotion plays the block reaction 0x43 itself at the real impact — the same field
    // it sets for Sentinel/Defend. The engine hardcodes 0x43 for this flag, so the native path
    // cannot emit a custom impact motion. We now drive a chosen motion (0x2F) via the manual
    // MsSetMotion path instead, so this defaults OFF; flip on only to restore the engine's 0x43.
    // Camera probe (debug): logs EVERY camera hook invocation + the lock-gating state (not just
    // when suppressed) so an un-locked enemy camera pan reveals which path fired and why the lock
    // did not engage (turn/attacker gating). Toggle via the "camera_probe" setting.
    // Guard/defend reaction flag, relative to the ChrRam sub-struct (Chr.ram). Set to 1 →
    // MsDamageSetMotion overrides flinch reactions 9/0x30 to the block motion 0x43.
    private const int ChrRamGuardReactFlagOffset = 0x19A;
    private readonly bool[] _damageEventActive = new bool[PartyActorCapacity];
    private readonly bool[] _parryFeedbackPending = new bool[PartyActorCapacity];
    // Per-hit bitmask of slots intercepted at MsSetDamageInternal. Set at p5=0 and
    // consumed/cleared at that slot's p5=1024 completion boundary.
    private uint _internalInterceptedMask;
    // Per-hit attacker id recorded at p5=0 intercept time for slot-correlated p5=1024 skip.
    private readonly byte[] _internalInterceptedAttackerId = new byte[PartyActorCapacity];
    // Slot-local marker: p5=0 already committed before a valid active parry gate existed.
    // Used to prevent late false-positive "parry success" promotion while still allowing
    // duplicate native commit passes to be safely skipped.
    private uint _latePreOpenP5ZeroCommitMask;
    private readonly byte[] _latePreOpenP5ZeroCommitAttackerId = new byte[PartyActorCapacity];
    // Durable per-turn marker: set at MsDamageSetMotion (visual impact time) when the parry
    // timing gate passed. Consumed at MsSetDamageInternal p5=1024 (authoritative HP/death commit)
    // to skip the commit without re-evaluating the wall-clock window, which may have expired
    // by the time the delayed commit pass fires (Anfunkeln, Blitzra, multi-pass attacks).
    private uint _parryResolvedAtImpactMask;
    private readonly long[] _parryExpiry = new long[PartyActorCapacity];
    private readonly byte[] _parryArmedAttackerId = new byte[PartyActorCapacity];
    private readonly uint[] _preHitHpSnapshot = new uint[PartyActorCapacity];
    // Consecutive fully-parried cues per slot within the current enemy turn block.
    // Increments per fully-parried cue; resets when that slot whiffs / is hit outside
    // the window, at block end, or on full runtime reset (battle end / mod toggled off).
    // Together with _blockMultiHitParriedPerSlot it feeds the block-end sequence rule
    // (block_slot_earned_counter): a counter is queued via MsInsertBtlCommand from
    // fire_block_counters() when count ≥ ParryStreakObserveThreshold OR the slot
    // parried a genuine multi-hit attack. See project_e33_counter_model.
    private readonly byte[] _consecutiveParriesPerSlot = new byte[PartyActorCapacity];
    // Per-slot flag: this slot parried a genuine multi-hit attack (hit_count ≥ 2)
    // during the current enemy turn block. Feeds the block-end sequence rule so a
    // single fully-parried multi-hit attack (e.g. a lone Oblivion) earns a counter
    // on its own, without needing a second consecutive enemy action. Reset together
    // with _consecutiveParriesPerSlot at block end / runtime reset.
    private readonly bool[] _blockMultiHitParriedPerSlot = new bool[PartyActorCapacity];
    private readonly AttackTelemetry[] _attackTelemetry = new AttackTelemetry[PartyActorCapacity];
    private ParryRuntimeState _runtime = ParryRuntimeState.CreateDefault();
    private readonly List<DebugLogEntry> _debugLog = new(DebugLogRingCapacity);
    private readonly List<DebugCueSnapshot> _debugCueSnapshots = new(MaxAttackCueScan);
    private readonly List<DebugCueSnapshot> _debugCueScratch = new(MaxAttackCueScan);
    private readonly List<DebugCueHistoryEntry> _debugCueHistory = new(CueHistoryRingCapacity);
    private readonly TurnTimelineTracker _turnTimeline = new(DebugTurnRowCapacity);
    private readonly TurnTimelineRuntimeEventSource _turnRuntimeEvents = new();
    private readonly FfxDataMappings _dataMappings = new();
    private readonly List<DebugCueSnapshot> _debugHookCueScratch = new(MaxAttackCueScan);
    private readonly List<TurnTimelineCueObservation> _debugTimelineCueScratch = new(MaxAttackCueScan);
    private readonly List<TurnTimelineEvent> _debugTimelineEventScratch = new(64);
    private readonly List<TurnTimelineRuntimeSignal> _debugRuntimeSignalScratch = new(128);
    private readonly Dictionary<string, ulong> _debugMessageLastEmitFrame = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _impactCorrelationRejectCounts = new(StringComparer.Ordinal);
    private int _impactCorrelationMatchedCount;
    private int _impactCorrelationRejectedCount;
    private string _impactCorrelationLastRejectReason = "None";
    private ulong _impactCorrelationLastSummaryFrame;
    // Debug-only: most recently resolved non-zero combat command id, cached at impact
    // resolution time (see on_impact_detected) for the Commands debug panel.
    private ushort _lastCombatCommandId;
    private ulong _msSetDamageLogLastFrame;
    private byte _msSetDamageLogLastP1;
    private int _msSetDamageLogLastP2;
    private int _msSetDamageLogLastP3;
    private int _msSetDamageLogLastResult;
    private bool _msSetDamageLogLastParryWindowActive;
    private byte _msSetDamageLogLastAttackerId;
    private bool _msSetDamageLogLastAwaitingTurnEnd;
    private ulong _msCalcDamageLogLastFrame;
    private int _msCalcDamageLogLastUserId;
    private int _msCalcDamageLogLastTargetId;
    private int _msCalcDamageLogLastCommandId;
    private int _msCalcDamageLogLastHitCount;
    private int _msCalcDamageLogLastResult;
    private double _simulationClockSeconds;
    private ulong _debugFrameIndex;
    private ulong _debugBattleFrameIndex;
    private string _debugLogTextBuf = string.Empty;   // rebuilt each frame for the selectable log box
    private bool _debugBattleActive;
    private bool _debugBattleSessionFirstCueSeen;
    private bool _debugGameSaveLoaded;
    private bool _debugGameplayReady;
    private float _debugStatePanelRatio = 0.50f;

    // Overlay window chrome. No title bar — the tab bar is the header, with a collapse caret
    // pinned to its top-right corner. Collapsed by default, it shows just that square caret at the
    // same corner. Opacity eases toward opaque only while the mouse is near. Window pos+size are
    // captured each frame so the caret can be derived from the window's corner and the window
    // reopens where and how big it was; the prev-rect is what the proximity test reads back.
    private const float OverlayCaretSize = 16f;
    private bool _overlayCollapsed = true;
    private float _overlayBgAlpha = 0.55f;
    private float _overlayContentAlpha = 1.0f;
    private Vector2 _overlayWindowPos = new(20f, 20f);
    private Vector2 _overlayWindowSize = new(420f, 520f);
    private Vector2 _overlayPrevRectMin = new(20f, 20f);
    private Vector2 _overlayPrevRectMax = new(20f, 20f);
    private bool _overlayPositioned;   // set once the full window has been shown; until then the collapsed caret starts in the screen's top-right corner
    private int _debugCueTurnId;
    private string _dataMappingStatus = "No data mappings loaded.";
    private readonly Random _rng = new();
    private string _stateDirectory = string.Empty;

    /// <summary>
    ///     The mod's global-state directory, taken from the FileStream the framework hands us at
    ///     init. Settings no longer live here - Fahrenheit persists those - but the session log and
    ///     the Lab's motion lists still do, and a full deploy mirrors the whole tree, so this only
    ///     survives because "state" is listed in DeployPreservePaths.
    /// </summary>
    private static string resolve_state_directory(FileStream? global_state_file)
    {
        try
        {
            string? dir = Path.GetDirectoryName(global_state_file?.Name);
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir)) return dir;
        }
        catch { /* not path-backed */ }

        return string.Empty;
    }
    private StreamWriter? _sessionDebugLogWriter;
    private StreamWriter? _sessionTimelineLogWriter;
    private string _sessionLogsRoot = string.Empty;
    private string _sessionLogDirectory = string.Empty;
    private string _sessionLogPrefix = string.Empty;
    private bool _sessionLogDisabled;
    private bool _sessionRetentionPruned;
    private string? _audioResourcesDir;
    private string? _fontResourcesDir;
    private string? _overlayFontPath;
    private ImFontPtr _overlayFont;
    private bool _overlayFontsInitialized;
    private bool _overlayFontWarningIssued;
    // Frame a motion was last played per party slot (lab Play / parry block), 0 = none.
    // Read by the observe-only MsEffectEndMotion hook to log the motion's run length.
    private readonly ulong[] _motionPlayFrame = new ulong[PartyActorCapacity];

    // Frame the parry block reaction (0x2F manual / 0x43 native) was last played on a slot, by EITHER the native path
    // (guard flag + orig MsDamageSetMotion) or the manual MsSetMotion poke. Whichever runs first
    // stamps it; the other then stands down. That keeps exactly one driver per hit — the
    // double-drive was the old "parry twitch" — while guaranteeing the block always plays, even
    // for parries resolved at MsSetDamageInternal (which never reach MsDamageSetMotion at all).
    private readonly ulong[] _parryBlockPlayedFrame = new ulong[PartyActorCapacity];
    private const ulong ParryBlockRecentFrames = 3;

    private bool parry_block_recently_played(int slot)
        => (uint)slot < PartyActorCapacity
           && _parryBlockPlayedFrame[slot] != 0
           && _debugFrameIndex - _parryBlockPlayedFrame[slot] <= ParryBlockRecentFrames;

    public ParryModule()
    {
        // Settings live in Fahrenheit's panel and are persisted by it; see
        // ParryModule.NativeSettings.cs for what that costs and what it rules out.
        _ = new Settings(this);

        // Hook delegates are cached (see ParryModule.OrigCalls.cs) so chain_from() does not
        // allocate a fresh delegate on every native call. The same instance is handed to both
        // install_hook(...) and chain_from(...); assigned here, once, before any install runs —
        // the Stage-1 and startup-skip installs happen later in init() but on this same instance.
        _dMsExeInputCue                   = h_ms_exe_input_cue;
        _dMsSetDamage                     = h_ms_set_damage;
        _dMsDamageSetMotion               = h_ms_damage_set_motion;
        _dMsCalcDamage                    = h_ms_calc_damage;
        _dDmgCalcArmored                  = h_dmg_calc_armored;
        _dMsCalcDamageInternal            = h_ms_calc_damage_internal;
        _dMsSetDamageInternal             = h_ms_set_damage_internal;
        _dMsAtelRequestCamera             = h_ms_atel_request_camera;
        _dMsAtelRequestMagicCamera        = h_ms_atel_request_magic_camera;
        _dMsBattleSpecialCameraPause      = h_ms_battle_special_camera_pause;
        _dAtelCameraPolarSet              = h_atel_camera_polar_set;
        _dAtelCameraPosSet                = h_atel_camera_pos_set;
        _dMsDmgCalcCheckHit               = h_ms_dmg_calc_check_hit;
        _dMsEffectEndMotion               = h_ms_effect_end_motion;
        _dStage1MsActionRequest           = h_stage1_ms_action_request;
        _dStage1MsCalcCommand             = h_stage1_ms_calc_command;
        _dStage1MsCheckStatusBeforeAction = h_stage1_ms_check_status_before_action;
        _dStage1MsLimitTypeDamageCheck    = h_stage1_ms_limit_type_damage_check;
        _dStage1OpEtBattleGenkoCounterGet = h_stage1_op_et_battle_genko_counter_get;
        _dStage1MsSetMotion               = h_stage1_ms_set_motion;
    }

    public override bool init(FhModContext mod_context, FileStream global_state_file)
    {
        if (!NativeBuild.TryValidate(out string nativeError))
        {
            _logger.Error(nativeError);
            return false;
        }

        // The mod's own state directory, still needed for the files that are not settings:
        // the session log and the Lab's motion blocklist.
        _stateDirectory = resolve_state_directory(global_state_file);
        initialize_session_logging(mod_context);
        initialize_motion_blocklist();
        _audioResourcesDir = Path.Combine(mod_context.Paths.ResourcesDir.FullName, "audio");
        _fontResourcesDir = Path.Combine(mod_context.Paths.ResourcesDir.FullName, "fonts");
        initialize_overlay_fonts();
        initialize_audio_resources();
        warmup_audio_playback_once();
        initialize_data_mappings(mod_context);

        FhApi.Events.Common.GameLoop.PreUpdate.subscribe(on_pre_update);

        install_hook(loc_ms_exe_input_cue(), _dMsExeInputCue, "MsExeInputCue (continuing without native dispatch signal)");
        install_hook(loc_ms_set_damage(), _dMsSetDamage, "MsSetDamage (experimental spike inactive)");
        install_hook(loc_ms_damage_set_motion(), _dMsDamageSetMotion, "MsDamageSetMotion");
        install_hook(loc_dmg_calc_armored(), _dDmgCalcArmored, "DmgCalcArmored Probe");
        install_hook(loc_ms_calc_damage_internal(), _dMsCalcDamageInternal, "MsCalcDamageInternal Probe");
        install_hook(loc_ms_set_damage_internal(), _dMsSetDamageInternal, "MsSetDamageInternal Probe");
        install_hook(loc_ms_calc_damage(), _dMsCalcDamage, "MsCalcDamage (hit-count probe inactive)");
        install_hook(loc_ms_atel_request_camera(), _dMsAtelRequestCamera, "MsAtelRequestCamera (enemy-turn camera lock unavailable)");
        install_hook(loc_ms_atel_request_magic_camera(), _dMsAtelRequestMagicCamera, "MsAtelRequestMagicCamera (enemy-turn magic camera lock unavailable)");
        install_hook(loc_ms_battle_special_camera_pause(), _dMsBattleSpecialCameraPause, "MsBattleSpecialCameraPause (boss cinematic camera lock unavailable)");
        install_hook(loc_atel_camera_polar_set(), _dAtelCameraPolarSet, "AtelCameraPolarSet (camera-writer probe unavailable)");
        install_hook(loc_atel_camera_pos_set(), _dAtelCameraPosSet, "AtelCameraPosSet (camera-writer probe unavailable)");
        install_hook(loc_ms_effect_end_motion(), _dMsEffectEndMotion, "MsEffectEndMotion (motion-duration observe unavailable)");
        install_hook(loc_ms_dmg_calc_check_hit(), _dMsDmgCalcCheckHit, "MsDmgCalcCheckHit (disable-native-evasion unavailable)");

        install_stage1_probes();

        lock (_vrrSimulationGate) _vrrSimulationReady = true;
        _logger.Info("ParryPrototype ready. Adjust options via Mod Config (F7).");
        return true;
    }

    // alpha11 stripped InputAction down to the level `is_pressed`; `held`, `just_pressed`,
    // `just_released` and consume() are all gone. Edge detection now lives here.
    //
    // Sampled at the top of PreUpdate, before every early return: a button still held while
    // the mod is disabled must not fire a phantom edge the moment it is re-enabled. The tick
    // rate is the same one at which the game refreshes its input word, so this reproduces the
    // old semantics exactly rather than approximating them.
    private bool _prevR1Pressed;
    private bool _prevCancelPressed;

    private void on_pre_update(Fahrenheit.Events.UpdateLoopEventArgs e)
    {
        if (!try_defer_vrr_simulation(e.delta)) on_simulation_update(e.delta);
    }

    private void on_simulation_update(float deltaSeconds)
    {
        _debugFrameIndex++;
        _simulationClockSeconds += deltaSeconds;

        // PreUpdate is raised from a hook on Sg_MainLoop, so this is the rate the mod samples input
        // at - 60/s when the game presents at 60 Hz. Every authored duration is converted against it.
        ParryDifficultyModel.ObserveTickDelta(deltaSeconds);

        bool r1Pressed     = FhApi.Input.r1.is_pressed;
        bool cancelPressed = FhApi.Input.cancel.is_pressed;
        bool r1JustPressed     = r1Pressed     && !_prevR1Pressed;
        bool cancelJustPressed = cancelPressed && !_prevCancelPressed;
        _prevR1Pressed     = r1Pressed;
        _prevCancelPressed = cancelPressed;

        update_debug_save_loaded_state();
        update_debug_battle_session_state();
        tick_evade_probe();
        drive_camera(deltaSeconds);

        if (_optionDebugOverlay || _optionLogging)
        {
            monitor_cue_transitions();
        }

        if (!_optionEnabled)
        {
            reset_runtime_state("disabled", clearFeedbackFlashes: true, clearDamageFlags: true);
            process_turn_runtime_events();
            return;
        }

        bool hasEnemyCue = monitor_attack_cues();
        update_parried_text_timer(deltaSeconds);

        ParryInputContext parryInput = capture_parry_input_context();

        if (r1JustPressed)
        {
            handle_parry_input_press(parryInput);
        }

        if (_optionDodgeEnabled && cancelJustPressed)   // cancel = Circle (○)
        {
            handle_dodge_input_press(parryInput);
        }

        // Poll damage after input so the open window set by the press is visible.
        monitor_damage_resolves();

        // Parry input state machine tick (FINAL_PARRY_SPEC.md).
        //
        //   Open          -> WhiffLockout  on window expiry without a hit
        //   WhiffLockout  -> Ready         when recovery timer elapses
        //
        // Resolved -> Ready is handled when the cue clears or the hit finalizes
        // (see clear_awaiting_turn_end / end_parry_window).
        if (_runtime.InputState == ParryInputState.Open && _runtime.ParryWindowActive)
        {
            _runtime.ParryWindowElapsedTicks++;
            _runtime.ParryWindowRemainingTicks--;
            if (_runtime.ParryWindowRemainingTicks <= 0)
            {
                transition_to_whiff_lockout();
            }
        }
        else if (_runtime.InputState == ParryInputState.WhiffLockout)
        {
            _runtime.WhiffLockoutRemainingTicks--;
            if (_runtime.WhiffLockoutRemainingTicks <= 0)
            {
                transition_whiff_lockout_to_ready();
            }
        }

        if (_dodgeCooldownRemainingTicks > 0)
        {
            _dodgeCooldownRemainingTicks--;
        }

        // Dodge window tick (independent of the parry state machine). Expires by tick count; a
        // successful dodge is not "consumed" so a multi-hit / AoE swing is fully evaded.
        if (_dodgeWindowActive)
        {
            _dodgeWindowRemainingTicks--;
            if (_dodgeWindowRemainingTicks <= 0)
            {
                _dodgeWindowActive = false;

                // Nothing hit us. End the evade animation deterministically rather than letting it run
                // out — together with the press-restart and hit paths this guarantees the dodge motion
                // always terminates, which is what stops a spammed dodge from leaving an actor
                // permanently "animating" and stalling an enemy's charging cast.
                uint stepped = _dodgeProbeSlotsMask;
                while (stepped != 0)
                {
                    int slot = BitOperations.TrailingZeroCount(stepped);
                    stepped &= stepped - 1;
                    try_end_battle_motion(slot, "window_expired");
                }

                if (_optionLogging)
                {
                    log_debug("[Dodge] Window expired without a hit.");
                }
            }
        }

        if (_dodgeTextRemainingSeconds > 0f)
        {
            _dodgeTextRemainingSeconds = MathF.Max(0f, _dodgeTextRemainingSeconds - deltaSeconds);
            if (_dodgeTextRemainingSeconds <= 0f)
            {
                _dodgeTextTargetMask = 0;
                _dodgeTextPerfectMask = 0;
            }
        }

        tick_dodge_field_probe();

        // Cue-cleared cleanup must run regardless of window state. If the cue disappears while
        // the window is still open (e.g., attack animation completed without a damage event),
        // clear_awaiting_turn_end also closes the window to prevent it staying open permanently.
        if (_runtime.AwaitingTurnEnd && !hasEnemyCue)
        {
            clear_awaiting_turn_end("Awaiting turn end cleared after no-cue update.");
        }

        validate_runtime_state();
        process_turn_runtime_events();

        // Drain any deferred native-probe events queued by hooks during this
        // frame. No-op when _optionNativeProbeLogging is false; the ring is
        // empty and the early-return inside drain_probe_ring exits cheaply.
        drain_probe_ring();
    }

    public override void render_imgui()
    {
        render_parry_window_overlay();
        render_dodge_overlay();

        // [Conditional("DEBUG")] - the call disappears in release, and with it the whole window.
        // Nothing is left in it for a release build: settings moved to Fahrenheit's panel and the
        // four remaining tabs were already DEBUG-only.
        render_debug_overlay();
    }

    private void reset_runtime_state(string timingReason, bool clearFeedbackFlashes, bool clearDamageFlags)
    {
        stop_audio_playback();

        _runtime.InputState = ParryInputState.Ready;
        _runtime.ParryWindowActive = false;
        _runtime.CurrentAttackerId = 0;
        _runtime.CurrentCueIndex = 0;
        _runtime.CurrentPartyTargetMask = 0;
        _runtime.CurrentCueSignature = 0;
        _runtime.ParryWindowRemainingTicks = 0;
        _runtime.WhiffLockoutRemainingTicks = 0;
        _runtime.WhiffLockoutTotalTicks = 0;
        _runtime.AwaitingTurnEnd = false;
        _runtime.ParryWindowElapsedTicks = 0;
        _runtime.ParryWindowSucceeded = false;
        _runtime.SuccessIndicatorActive = false;
        _runtime.LastDispatchConsumedFrame = 0;
        _runtime.LastDispatchConsumedAttackerId = 0;
        _runtime.LastDispatchConsumedQueueIndex = 0xFF;
        _runtime.LastCorrelationSkipFrame = 0;
        _runtime.CueFirstSeenFrame = 0;
        _runtime.WindowOpenFrame = 0;
        _runtime.WindowOpenTimestampSeconds = 0f;
        _runtime.WindowDurationSecondsAtOpen = 0f;
        _dodgeWindowActive = false;
        _dodgeWindowRemainingTicks = 0;
        _dodgeCooldownRemainingTicks = 0;
        _dodgeArmedAttackerId = 0;
        _dodgeArmedCueFrame = 0;
        _dodgeArmedTargetMask = 0;
        _dodgeResolvedAtImpactMask = 0;
        _impactCorrelationMatchedCount = 0;
        _impactCorrelationRejectedCount = 0;
        _impactCorrelationLastRejectReason = "None";
        _impactCorrelationLastSummaryFrame = 0;
        _impactCorrelationRejectCounts.Clear();
        // Streak counter is per-slot and persists across turns; only the full
        // runtime reset (battle end / mod toggled off) clears it.
        Array.Clear(_consecutiveParriesPerSlot);
        Array.Clear(_blockMultiHitParriedPerSlot);

        if (clearFeedbackFlashes)
        {
            _runtime.ParriedTextRemainingSeconds = 0f;
            _runtime.ParryMissedTextRemainingSeconds = 0f;
            _runtime.StatusBlockTextRemainingSeconds = 0f;
            _runtime.StatusBlockLabel = string.Empty;
            _runtime.LastParriedTargetMask = 0;
        }

        if (clearDamageFlags)
        {
            Array.Clear(_damageEventActive);
        }
    }

    private bool should_capture_debug_messages()
    {
        return _optionLogging || _optionDebugOverlay;
    }

    private void log_debug(string message)
    {
        if (!should_capture_debug_messages())
        {
            return;
        }

        bool appended = append_debug_event(message);

        if (_optionLogging && appended && !is_low_signal_log_message(message))
        {
            _logger.Info($"[Parry] {message}");
        }
    }

    private static bool is_low_signal_log_message(string message)
    {
        return message switch
        {
            "Parry input ignored (no parryable enemy cue)." => true,
            "Parry release ignored (no active parryable enemy cue)." => true,
            _ when message.StartsWith("Timeline integrity warning:", StringComparison.Ordinal) => true,
            _ => false
        };
    }

    private DateTime current_gameplay_timestamp()
    {
        return DateTime.UnixEpoch + TimeSpan.FromSeconds(_simulationClockSeconds);
    }

    private double current_gameplay_seconds()
    {
        return _simulationClockSeconds;
    }

    private void validate_runtime_state()
    {
        _runtime.ParryWindowRemainingTicks = Math.Max(0, _runtime.ParryWindowRemainingTicks);
        _runtime.ParryWindowElapsedTicks = Math.Max(0, _runtime.ParryWindowElapsedTicks);
        _runtime.WhiffLockoutRemainingTicks = Math.Max(0, _runtime.WhiffLockoutRemainingTicks);
        _runtime.ParriedTextRemainingSeconds = MathF.Max(0f, _runtime.ParriedTextRemainingSeconds);
        _runtime.ParryMissedTextRemainingSeconds = MathF.Max(0f, _runtime.ParryMissedTextRemainingSeconds);
        _runtime.StatusBlockTextRemainingSeconds = MathF.Max(0f, _runtime.StatusBlockTextRemainingSeconds);

        if (!_runtime.ParryWindowActive && (_runtime.ParryWindowRemainingTicks > 0 || _runtime.ParryWindowElapsedTicks > 0))
        {
            _runtime.ParryWindowRemainingTicks = 0;
            _runtime.ParryWindowElapsedTicks = 0;
        }

        if (_runtime.InputState != ParryInputState.WhiffLockout && _runtime.WhiffLockoutRemainingTicks > 0)
        {
            _runtime.WhiffLockoutRemainingTicks = 0;
            _runtime.WhiffLockoutTotalTicks = 0;
        }

        if (!_runtime.AwaitingTurnEnd && _runtime.CurrentPartyTargetMask != 0)
        {
            _runtime.CurrentPartyTargetMask = 0;
        }
        if (!_runtime.AwaitingTurnEnd && _runtime.CurrentCueSignature != 0)
        {
            _runtime.CurrentCueSignature = 0;
        }
    }
    private static uint _cachedEventNameId = uint.MaxValue;
    private static string _cachedEventName = string.Empty;

    // AtelGetEventName opens, seeks, reads and closes eventid.bin on every call. Called per main loop
    // pass it competed with the game's loader for the file system and stretched scene transitions by
    // seconds, so the name is read once per event id.
    private static string get_current_event_name(uint eventId)
    {
        if (eventId == _cachedEventNameId) return _cachedEventName;
        try
        {
            char* ptr = FhFfx.FhCall.AtelGetEventName.fnptr!(eventId);
            _cachedEventName = ptr == null ? string.Empty : Marshal.PtrToStringAnsi((nint)ptr) ?? string.Empty;
        }
        catch
        {
            _cachedEventName = string.Empty;
        }
        _cachedEventNameId = eventId;
        return _cachedEventName;
    }

    private sealed class DebugLogEntry
    {
        public DateTime TimestampLocal { get; set; }
        public double SimulationSeconds { get; set; }
        public ulong FrameIndex { get; set; }
        public string Message { get; set; } = string.Empty;
        public int RepeatCount { get; set; } = 1;
    }
}
