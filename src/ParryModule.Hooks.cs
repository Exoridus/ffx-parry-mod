namespace Fahrenheit.Mods.Parry;

public unsafe sealed partial class ParryModule
{
    private uint h_ms_exe_input_cue(uint actorId, byte mode)
    {
        bool hadBefore = try_get_head_cue_snapshot(_debugHookCueScratch, out DebugCueSnapshot before);
        ulong frame = _debugFrameIndex;
        DateTime now = current_gameplay_timestamp();

        uint result = orig_ms_exe_input_cue(actorId, mode);

        bool hasAfter = try_get_head_cue_snapshot(_debugHookCueScratch, out DebugCueSnapshot after);
        bool changed = !hadBefore || !hasAfter || !before.EqualsSemantic(after);

        if (hadBefore && changed)
        {
            _runtime.LastDispatchConsumedFrame = frame;
            _runtime.LastDispatchConsumedAttackerId = before.AttackerId;
            _runtime.LastDispatchConsumedQueueIndex = before.QueueIndex;

            _turnRuntimeEvents.EmitDispatchConsumed(
                attackerId: before.AttackerId,
                queueIndex: before.QueueIndex,
                timestampLocal: now,
                frameIndex: frame,
                reason: "native dispatch");
        }

        if (hasAfter && changed)
        {
            _turnRuntimeEvents.EmitDispatchStarted(
                attackerId: after.AttackerId,
                queueIndex: after.QueueIndex,
                timestampLocal: now,
                frameIndex: frame,
                parryWindowActive: _runtime.ParryWindowActive);
        }
        return result;
    }

    /// <summary>
    ///     Active hook on MsSetDamage (int __cdecl MsSetDamage(byte param_1, int param_2, int param_3)).
    ///
    ///     Call semantics confirmed from session log analysis (2026-03-22):
    ///       param_1 = attacker battler slot
    ///       param_2 = target party slot (>= 0) for the per-target staging call;
    ///                 -5 for setup (p3=0) and finalization (p3=0x400) calls
    ///       param_3 = 0 for setup/per-target calls; 0x400 for finalization (triggers MsAfterDamageProcess)
    ///
    ///     The p2=target call stages damage_hp/mp/ctb for display but does NOT reduce ram.current_hp.
    ///     The p3=0x400 finalization call is where MsAfterDamageProcess reads from its internal buffer
    ///     and applies the actual HP reduction.
    ///
    ///     Damage negation strategy: h_ms_calc_damage returns 0 (skipping the native call) when
    ///     a party slot has an active parry expiry timestamp. This prevents damage from entering
    ///     the native pipeline for ALL attack paths.
    ///
    ///     Anfunkeln-style attacks (no p2=target call): the hook never fires for a per-target resolve.
    ///     These are detected at finalization (p3=0x400) when the parry window is still open but
    ///     LastParriedTargetMask is zero, and the parry feedback is resolved there.
    /// </summary>
    private int h_ms_set_damage(byte param_1, int param_2, int param_3)
    {
        // p2=target: snapshot HP when a parry expiry is active for this slot so we can restore
        // on impact. The hook is the authoritative impact detection path — it fires at native
        // damage time, before the poll path in on_pre_update. When the per-slot expiry is still
        // live and the attacker matches, resolve the parry directly from the hook to ensure
        // feedback (text + sound) fires at impact time rather than being deferred to the poll path.
        // Use _parryExpiry[param_2] (same gate as h_ms_calc_damage and h_ms_damage_set_motion)
        // rather than _runtime.ParryWindowActive (10s backstop), so all resolution paths agree
        // on parry validity using the same wall-clock window the player actually pressed.
        bool isPartyTargetCall = param_2 >= 0 && param_2 < PartyActorCapacity;
        bool isActiveParry = isPartyTargetCall
            && _optionEnabled
            && DateTime.UtcNow.Ticks < _parryExpiry[param_2]
            && param_1 == _runtime.CurrentAttackerId
            && (_runtime.CurrentPartyTargetMask & (1u << param_2)) != 0;

        Chr* parryTarget = null;

        if (isActiveParry)
        {
            Chr* party = _battleAdapter.GetPlayerCharacters();
            Chr* candidate = party != null ? party + param_2 : null;
            if (candidate != null && candidate->stat_exist_flag)
            {
                parryTarget = candidate;
            }
        }

        if (isPartyTargetCall)
            _attackTelemetry[param_2].SetDamageTargetFired = true;

        long nowTicks = DateTime.UtcNow.Ticks;
        bool anyExpiryActive = false;
        uint currentMask = _runtime.CurrentPartyTargetMask;
        for (int i = 0; i < PartyActorCapacity; i++)
        {
            if ((currentMask & (1u << i)) != 0 && nowTicks < _parryExpiry[i])
            {
                anyExpiryActive = true;
                break;
            }
        }

        // p3=0x400 finalization: if the parry window is still open, close it and handle
        // Anfunkeln-style attacks (no p2=target calls) that need feedback resolved here.
        // h_ms_calc_damage return-0 has already prevented the HP reduction — no
        // snapshot/restore needed.
        bool isFinalizationWithParry = param_3 == 0x400
            && _optionEnabled
            && param_1 == _runtime.CurrentAttackerId
            && (anyExpiryActive || _runtime.LastParriedTargetMask != 0)
            && try_get_enemy_attack_cue(out AttackCue cue, out byte cueIndex, out _)
            && cueIndex == _runtime.CurrentCueIndex;

        // Detect whether the motion hook already handled this exact hit.
        // For 0x40A0-class attacks, the animation system calls MsDamageSetMotion directly
        // BEFORE MsSetDamage p2=target fires (~14 frames later). The motion hook sets
        // LastParriedTargetMask when it handles the hit. By the time MsSetDamage fires, the
        // parry window has expired and isActiveParry is false — parryTarget is null — so
        // skipOrigForParry would not fire. But FUN_0078f0b0 must still be blocked to prevent
        // it from re-applying damage from the native hit-record buffer via MsSubHP.
        bool alreadyParriedThisHit = isPartyTargetCall
            && (_runtime.LastParriedTargetMask & (1u << param_2)) != 0;
        bool latePreOpenCommitted = isPartyTargetCall
            && is_late_preopen_p5_zero_commit(param_2, param_1)
            && has_live_enemy_damage_context_for_slot(param_1, param_2);

        // If the motion hook already handled this hit (window may have since expired),
        // parryTarget is null because isActiveParry failed the expiry check. Reconstruct
        // the target pointer so the skip logic and lethal-restore path below can use it.
        if (alreadyParriedThisHit && parryTarget == null)
        {
            Chr* party = _battleAdapter.GetPlayerCharacters();
            Chr* candidate = party != null ? party + param_2 : null;
            if (candidate != null && candidate->stat_exist_flag)
                parryTarget = candidate;
        }

        // Late-commit fallback: p5=0 already committed before the parry gate opened for this
        // slot/attacker. Rebuild target pointer so we can still block duplicate native apply
        // at p2=target without promoting a false parry success.
        if (latePreOpenCommitted && parryTarget == null)
        {
            Chr* party = _battleAdapter.GetPlayerCharacters();
            Chr* candidate = party != null ? party + param_2 : null;
            if (candidate != null && candidate->stat_exist_flag)
                parryTarget = candidate;
        }

        // Skip orig when the window is still live and damage is pending (standard reactive
        // path), OR when the motion hook already handled this exact hit (durable marker that
        // survives window expiry). In both cases parryTarget must be valid.
        // NOTE: do NOT check damage_hp > 0 for the alreadyParriedThisHit branch — the motion
        // hook calls negate_damage_on_impact which zeroes damage_hp before MsSetDamage fires,
        // but FUN_0078f0b0 reads damage from the native hit-record buffer (separate from
        // damage_hp) and will still call MsSubHP with the original computed damage.
        bool skipOrigForParry = parryTarget != null
            && _optionNegateDamage
            && !is_target_non_parryable(parryTarget)
            && (alreadyParriedThisHit
                || latePreOpenCommitted
                || (DateTime.UtcNow.Ticks < _parryExpiry[param_2] && parryTarget->damage_hp > 0));

        // Telemetry: snapshot HP for all targeted party slots before finalization.
        bool isFinalization = param_3 == 0x400;
        if (isFinalization)
        {
            Chr* party = _battleAdapter.GetPlayerCharacters();
            if (party != null)
            {
                uint mask = _runtime.CurrentPartyTargetMask;
                for (int i = 0; i < PartyActorCapacity; i++)
                {
                    if ((mask & (1u << i)) != 0 && (party + i)->stat_exist_flag)
                        _attackTelemetry[i].HpBeforeFinalization = (uint)(party + i)->ram.hp;
                }
            }
        }

        int result;
        if (skipOrigForParry)
        {
            // Orig skipped — native HP apply blocked. Resolve parry feedback now.
            result = 0;
            log_debug($"Parry blocked p2=target orig for {format_actor_slot((byte)param_2)} (damage_hp={parryTarget->damage_hp}, hp={(uint)parryTarget->ram.hp}).");

            if (!alreadyParriedThisHit && !latePreOpenCommitted)
            {
                string attackerLabel = format_actor_slot(param_1);
                string targetLabel   = format_actor_slot((byte)param_2);
                log_debug($"Hook impact: {attackerLabel} -> {targetLabel}, resolving parry at impact time.");
                resolve_successful_parry(param_2, parryTarget, "physical", closeWindow: false);
                _damageEventActive[param_2]   = true;
                _parryFeedbackPending[param_2] = false;
            }
            else if (latePreOpenCommitted && !alreadyParriedThisHit)
            {
                // p5=0 already committed before the window opened; this is a miss, not a
                // successful parry. We still skip p2=target orig here to avoid duplicate apply.
                _runtime.TurnImpactMissedSeen = true;
                _runtime.TurnImpactMissedAttackerId = (byte)param_1;
                trigger_failure_feedback();
                log_debug($"Parry promotion suppressed for {format_actor_slot((byte)param_2)} (late p5=0 commit occurred before window open).");
            }
            else
            {
                log_debug($"Parry blocked orig (motion-hook already resolved feedback for {format_actor_slot((byte)param_2)}).");
            }

            // Lethal restore: an unknown native reducer (likely MsSetDamageInternal) can clamp
            // ram.hp to 0 before this hook fires for killing blows, even though we skip orig here.
            // If HP is already 0 and we have a pre-hit snapshot, restore it and clear stat_will_die.
            if (parryTarget->ram.hp <= 0)
            {
                uint snap = _preHitHpSnapshot[param_2];
                if (snap > 0)
                {
                    // Diagnostic: read death-state fields before restore to determine
                    // whether the death latch (chr+OffsetDeathLatch et al.) is already set
                    // at this point. See ExternalMemoryOffsetMap.ChrStruct for evidence.
                    byte*   chrB    = (byte*)parryTarget;
                    byte    dcc_pre = chrB[ExternalMemoryOffsetMap.ChrStruct.OffsetDeathLatch];
                    ushort  s606_pre = *(ushort*)(chrB + ExternalMemoryOffsetMap.ChrStruct.OffsetStatusBits);
                    ushort  s700_pre = *(ushort*)(chrB + 0x700);
                    ushort  s702_pre = *(ushort*)(chrB + 0x702);
                    byte    dee_pre  = chrB[0xDEE];
                    byte    dd0_pre  = chrB[0xDD0];
                    byte    dd1_pre  = chrB[0xDD1];
                    byte    f5f_pre  = chrB[0xF5F];
                    log_debug($"[LethalDiag PRE ] slot={param_2} hp={parryTarget->ram.hp} dmg_hp={parryTarget->damage_hp} wdie={parryTarget->stat_will_die} " +
                              $"0xDCC={dcc_pre} 0x606=0x{s606_pre:X4} 0x700=0x{s700_pre:X4} 0x702=0x{s702_pre:X4} " +
                              $"0xDEE={dee_pre} 0xDD0={dd0_pre} 0xDD1={dd1_pre} 0xF5F={f5f_pre}");

                    parryTarget->ram.hp = (int)snap;
                    parryTarget->stat_will_die = 0;

                    // Clear confirmed death-latch fields set by MsDamageCheckDeath.
                    // Evidence: LethalDiag PRE consistently shows OffsetDeathLatch=2 and
                    // OffsetStatusBits bit 0 set. MsGetChrStatDeath returns 1 when
                    // chr+OffsetDeathLatch != 0, gating all downstream death processing;
                    // bit 0 of chr+OffsetStatusBits is the dead-status bit ORed in by the
                    // same function. Clearing both here prevents death from winning despite
                    // the HP restore. See ExternalMemoryOffsetMap.ChrStruct.
                    chrB[ExternalMemoryOffsetMap.ChrStruct.OffsetDeathLatch] = 0;
                    *(ushort*)(chrB + ExternalMemoryOffsetMap.ChrStruct.OffsetStatusBits)
                        &= unchecked((ushort)~ExternalMemoryOffsetMap.ChrStruct.DeadStatusBitMask);

                    byte    dcc_post = chrB[ExternalMemoryOffsetMap.ChrStruct.OffsetDeathLatch];
                    ushort  s606_post = *(ushort*)(chrB + ExternalMemoryOffsetMap.ChrStruct.OffsetStatusBits);
                    ushort  s700_post = *(ushort*)(chrB + 0x700);
                    ushort  s702_post = *(ushort*)(chrB + 0x702);
                    byte    dee_post  = chrB[0xDEE];
                    byte    dd0_post  = chrB[0xDD0];
                    byte    dd1_post  = chrB[0xDD1];
                    byte    f5f_post  = chrB[0xF5F];
                    log_debug($"[LethalDiag POST] slot={param_2} hp={parryTarget->ram.hp} dmg_hp={parryTarget->damage_hp} wdie={parryTarget->stat_will_die} " +
                              $"0xDCC={dcc_post} 0x606=0x{s606_post:X4} 0x700=0x{s700_post:X4} 0x702=0x{s702_post:X4} " +
                              $"0xDEE={dee_post} 0xDD0={dd0_post} 0xDD1={dd1_post} 0xF5F={f5f_post}");

                    log_debug($"Parry lethal restore for {format_actor_slot((byte)param_2)}: HP restored to {snap} (was 0), stat_will_die cleared.");
                }
            }
        }
        else
        {
            result = orig_ms_set_damage(param_1, param_2, param_3);
        }

        // Telemetry: snapshot HP after finalization.
        if (isFinalization)
        {
            Chr* party = _battleAdapter.GetPlayerCharacters();
            if (party != null)
            {
                uint mask = _runtime.CurrentPartyTargetMask;
                for (int i = 0; i < PartyActorCapacity; i++)
                {
                    if ((mask & (1u << i)) != 0 && (party + i)->stat_exist_flag)
                        _attackTelemetry[i].HpAfterFinalization = (uint)(party + i)->ram.hp;
                }
            }
        }

        if (isFinalizationWithParry)
        {
            if (_runtime.LastParriedTargetMask != 0)
            {
                // Safety net: resolve any remaining deferred feedback that MsDamageSetMotion
                // did not consume (e.g. magic.dll attacks where MsDamageSetMotion may not fire).
                Chr* partyFallback = _battleAdapter.GetPlayerCharacters();
                if (partyFallback != null)
                {
                    for (int i = 0; i < PartyActorCapacity; i++)
                    {
                        if (!_parryFeedbackPending[i]) continue;
                        Chr* fb = partyFallback + i;
                        if (fb->stat_exist_flag && !is_target_non_parryable(fb))
                        {
                            // Additive restore — same semantics as motion and p2=target paths.
                            if (_optionNegateDamage && fb->damage_hp > 0)
                            {
                                int restored = (int)fb->ram.hp + fb->damage_hp;
                                uint snap = _preHitHpSnapshot[i];
                                if (snap > 0 && (uint)restored > snap) restored = (int)snap;
                                fb->ram.hp = restored;
                                log_debug($"Parry restored {fb->damage_hp} HP for {format_actor_slot((byte)i)} at fallback (hp now={(uint)fb->ram.hp}).");
                            }

                            resolve_successful_parry(i, fb, "fallback", closeWindow: false);
                        }
                        _parryFeedbackPending[i] = false;
                    }
                }

                // Regular attack: each target was already resolved by the h_ms_calc_damage
                // return-0 path or the p2=target hook path — nothing to restore.
                log_debug($"Turn complete — {BitOperations.PopCount(_runtime.LastParriedTargetMask)} target(s) parried.");
                end_parry_window("finalization_complete");
            }
            else if (_runtime.TurnImpactMissedSeen)
            {
                // A poll-detected impact for this turn was already marked as missed (window was
                // closed when it hit). The player opened the window after the fact — that is not
                // a successful parry. Skip the Anfunkeln fallback to avoid a false PARRIED.
                log_debug($"Magic/special finalization skipped for {format_actor_slot(param_1)}: turn already missed.");
                end_parry_window("missed_turn");
            }
            else
            {
                // Branch on whether any targeted slot received a per-target MsSetDamage p2=slot
                // call before this p3=0x400 finalization. Attacks that go through the per-target
                // call path set SetDamageTargetFired; attacks that commit entirely through
                // MsSetDamageInternal p5=1024 (delayed finalization) do not.
                // When attacks with per-target calls arrive here, it means damage_hp was 0 at
                // p5=0 time (staging latency gap after reactive R1 press), so resolve_successful_parry
                // was skipped even though _internalInterceptedMask was set and HP is protected.
                // This distinction matches all currently observed attack paths. If a hybrid path
                // (per-target calls AND delayed p5=1024 commit) is discovered, revisit this branch.
                bool hasPerTargetSetDamageCalls = false;
                {
                    uint tmask = _runtime.CurrentPartyTargetMask;
                    for (int i = 0; i < PartyActorCapacity; i++)
                    {
                        if ((tmask & (1u << i)) != 0 && _attackTelemetry[i].SetDamageTargetFired)
                        {
                            hasPerTargetSetDamageCalls = true;
                            break;
                        }
                    }
                }

                if (!hasPerTargetSetDamageCalls)
                {
                    // Delayed-finalization path: no per-target MsSetDamage calls observed.
                    // h_ms_calc_damage return-0 blocked the damage. Resolve parry feedback
                    // now for all targeted party members.
                    Chr* party = _battleAdapter.GetPlayerCharacters();
                    if (party != null)
                    {
                        uint mask = _runtime.CurrentPartyTargetMask;
                        while (mask != 0)
                        {
                            int slot = BitOperations.TrailingZeroCount(mask);
                            mask &= mask - 1;
                            Chr* candidate = party + slot;

                            if (candidate->stat_exist_flag && candidate->damage_hp > 0 && !is_target_non_parryable(candidate))
                            {
                                // Additive restore — same semantics as motion and p2=target paths.
                                if (_optionNegateDamage)
                                {
                                    int restored = (int)candidate->ram.hp + candidate->damage_hp;
                                    uint snap = _preHitHpSnapshot[slot];
                                    if (snap > 0 && (uint)restored > snap) restored = (int)snap;
                                    candidate->ram.hp = restored;
                                    log_debug($"Parry restored {candidate->damage_hp} HP for {format_actor_slot((byte)slot)} at magic_finalization (hp now={(uint)candidate->ram.hp}).");
                                }

                                resolve_successful_parry(slot, candidate, "magic_impact", closeWindow: false);
                            }
                        }
                    }
                    end_parry_window("magic_finalization");
                }
                else
                {
                    // Per-target call path: SetDamageTargetFired was observed for at least one
                    // targeted slot. damage_hp was 0 at p5=0 time, so resolve_successful_parry
                    // was not called from h_ms_set_damage_internal. HP is protected via
                    // _internalInterceptedMask. Emit feedback for any slot where the interception
                    // evidence matches the current attacker and the parry has not already been
                    // acknowledged.
                    //
                    // Important: do NOT close the window on this branch unless this exact
                    // finalization pass actually resolves at least one parry.
                    // Some E11 physical follow-up sequences can reach p3=0x400 while the
                    // newly opened window has not yet reached a valid physical resolve path.
                    // Closing without a real resolution causes false early Open=>Waiting.
                    bool resolvedAtPhysicalFinalization = false;
                    Chr* party = _battleAdapter.GetPlayerCharacters();
                    if (party != null)
                    {
                        uint mask = _runtime.CurrentPartyTargetMask;
                        for (int i = 0; i < PartyActorCapacity; i++)
                        {
                            if ((mask & (1u << i)) == 0) continue;
                            bool intercepted = (_internalInterceptedMask & (1u << i)) != 0
                                && _internalInterceptedAttackerId[i] == (byte)param_1;
                            bool resolved = (_runtime.LastParriedTargetMask & (1u << i)) != 0;
                            if (!intercepted || resolved) continue;
                            Chr* candidate = party + i;
                            if (candidate->stat_exist_flag && !is_target_non_parryable(candidate))
                            {
                                resolve_successful_parry(i, candidate, "physical_finalization", closeWindow: false);
                                resolvedAtPhysicalFinalization = true;
                            }
                        }
                    }

                    if (resolvedAtPhysicalFinalization)
                    {
                        end_parry_window("physical_finalization");
                    }
                    else
                    {
                        log_debug($"Physical finalization deferred for {format_actor_slot(param_1)}: no resolved slot in this pass.");
                    }
                }
            }
        }

        log_hook_ms_set_damage(param_1, param_2, param_3, result);
        return result;
    }

    /// <summary>
    ///     Writes MsSetDamage hook telemetry to the session log file only (not the overlay).
    ///     High-frequency hook data is useful for post-session analysis but clutters the
    ///     real-time debug overlay with low-signal noise.
    /// </summary>
    private void log_hook_ms_set_damage(byte param_1, int param_2, int param_3, int result)
    {
        if (!_optionLogging)
            return;

        ulong frame = _debugFrameIndex;
        bool parryWindowActive = _runtime.ParryWindowActive;
        byte currentAttackerId = _runtime.CurrentAttackerId;
        bool awaitingTurnEnd = _runtime.AwaitingTurnEnd;
        bool sameAsLast =
            _msSetDamageLogLastFrame != 0
            && _msSetDamageLogLastP1 == param_1
            && _msSetDamageLogLastP2 == param_2
            && _msSetDamageLogLastP3 == param_3
            && _msSetDamageLogLastResult == result
            && _msSetDamageLogLastParryWindowActive == parryWindowActive
            && _msSetDamageLogLastAttackerId == currentAttackerId
            && _msSetDamageLogLastAwaitingTurnEnd == awaitingTurnEnd;

        if (sameAsLast && frame - _msSetDamageLogLastFrame < 30)
            return;

        _msSetDamageLogLastFrame = frame;
        _msSetDamageLogLastP1 = param_1;
        _msSetDamageLogLastP2 = param_2;
        _msSetDamageLogLastP3 = param_3;
        _msSetDamageLogLastResult = result;
        _msSetDamageLogLastParryWindowActive = parryWindowActive;
        _msSetDamageLogLastAttackerId = currentAttackerId;
        _msSetDamageLogLastAwaitingTurnEnd = awaitingTurnEnd;

        // Route to session file log only — not to overlay ring buffer.
        write_session_hook_entry($"[MsSetDamage] f={frame} p1={param_1} p2={param_2} p3={param_3} ret={result} parry={parryWindowActive} atk={currentAttackerId} await={awaitingTurnEnd}");
    }

    /// <summary>
    ///     Active hook on MsCalcDamage (community-confirmed 11-param signature, March 2026).
    ///     Fires with per-target info for ALL attack types — physical and magic alike — making
    ///     it the authoritative damage interception point for parry resolution.
    ///
    ///     When a party slot has an active parry expiry timestamp, the hook skips the native
    ///     MsCalcDamage call entirely and returns 0 — preventing damage from entering the
    ///     native pipeline. This replaces the previous IMMUNITY_HP_DAMAGE flag mechanism.
    ///
    ///     The check MUST happen BEFORE orig is called. If orig runs, damage enters the
    ///     native buffer and cannot be recalled.
    ///
    ///     The MsSetDamage p2=target path remains as a secondary feedback path for physical
    ///     attacks. The Anfunkeln finalization fallback remains as a safety net for attacks
    ///     that bypass MsCalcDamage entirely.
    /// </summary>
    private int h_ms_calc_damage(
        int user_id, nint user_chr, int target_id, nint target_chr,
        nint command, int command_id,
        nint p7, nint p8, nint p9, nint p10, int p11)
    {
        // Check parry expiry BEFORE calling orig — skipping orig is how we prevent damage
        // from entering the native buffer. Once orig runs, damage cannot be recalled.
        bool isPartyTarget = target_id >= 0 && target_id < PartyActorCapacity;
        bool shouldIntercept = isPartyTarget
            && _optionEnabled
            && DateTime.UtcNow.Ticks < _parryExpiry[target_id]
            && (_runtime.LastParriedTargetMask & (1u << target_id)) == 0;

        if (isPartyTarget)
        {
            _attackTelemetry[target_id].CalcDamageFired = true;
            if (_attackTelemetry[target_id].CommandId == 0)
                _attackTelemetry[target_id].CommandId = command_id;
        }

        Chr* targetChrPtr = isPartyTarget && target_chr != 0 ? (Chr*)target_chr : null;

        // Unconditional pre-hit HP snapshot for all party targets — captured before orig runs.
        // Reactive parries (R1 pressed after calc fires) never enter shouldIntercept, so this
        // is the only site that captures snapshot for reactive-parry lethal restore.
        // Guard == 0: preserve first-hit value on multi-hit attacks (don't overwrite with 0).
        if (isPartyTarget && targetChrPtr != null && targetChrPtr->stat_exist_flag
            && _preHitHpSnapshot[target_id] == 0)
        {
            _preHitHpSnapshot[target_id] = (uint)targetChrPtr->ram.hp;
        }

        if (shouldIntercept)
        {
            Chr* party = _battleAdapter.GetPlayerCharacters();
            Chr* candidate = party != null ? party + target_id : null;
            if (candidate != null && candidate->stat_exist_flag && !is_target_non_parryable(candidate))
            {
                _attackTelemetry[target_id].CalcDamageIntercepted = true;
                _runtime.LastParriedTargetMask |= 1u << target_id;
                _parryFeedbackPending[target_id] = true;
                _damageEventActive[target_id] = true;
                log_debug($"MsCalcDamage intercepted: {format_actor_slot((byte)user_id)} -> {format_actor_slot((byte)target_id)} (cmd={command_id}), damage blocked, feedback deferred. HP snapshot={_preHitHpSnapshot[target_id]}");
                log_hook_ms_calc_damage(user_id, target_id, command_id, p11, 0, command);
                return 0;
            }
        }

        int result = orig_ms_calc_damage(
            user_id, user_chr, target_id, target_chr,
            command, command_id,
            p7, p8, p9, p10, p11);

        log_hook_ms_calc_damage(user_id, target_id, command_id, p11, result, command);
        return result;
    }

    private void log_hook_ms_calc_damage(int user_id, int target_id, int command_id, int p11, int result, nint command)
    {
        if (!_optionLogging)
            return;

        ulong frame = _debugFrameIndex;
        bool sameAsLast =
            _msCalcDamageLogLastFrame != 0
            && _msCalcDamageLogLastUserId == user_id
            && _msCalcDamageLogLastTargetId == target_id
            && _msCalcDamageLogLastCommandId == command_id
            && _msCalcDamageLogLastHitCount == p11
            && _msCalcDamageLogLastResult == result;

        if (sameAsLast && frame - _msCalcDamageLogLastFrame < 30)
            return;

        _msCalcDamageLogLastFrame = frame;
        _msCalcDamageLogLastUserId = user_id;
        _msCalcDamageLogLastTargetId = target_id;
        _msCalcDamageLogLastCommandId = command_id;
        _msCalcDamageLogLastHitCount = p11;
        _msCalcDamageLogLastResult = result;

        // Route to session file log only — high-frequency hook data clutters the overlay.
        write_session_hook_entry($"[MsCalcDamage] f={frame} user={user_id} target={target_id} cmd={command_id} hits={p11} ret={result} cmd_ptr=0x{command:X}");
    }

    private void h_ms_damage_set_motion(byte target, int p2, int p3)
    {
        // Skip orig when a parry expiry is active and this is a damage motion (p3=1).
        // MsDamageSetMotion orig reads damage_hp and reduces ram.hp for some attack commands
        // (e.g. 0x4026) before our negate logic runs. Skipping orig prevents both the HP
        // reduction and the flinch animation — correct, since a parried hit should not flinch.
        bool targetIsParty = target < PartyActorCapacity;
        bool parryActive   = targetIsParty
                             && _optionEnabled
                             && DateTime.UtcNow.Ticks < _parryExpiry[target]
                             && _parryArmedAttackerId[target] == _runtime.CurrentAttackerId
                             && p3 == 1;

        // When parry is active but the enemy missed (damage_hp=0), do NOT suppress the flinch —
        // let the native miss/dodge animation play normally. Only suppress when actual damage
        // was pending, meaning we intercepted a real hit.
        bool suppressFlinch = false;
        if (parryActive)
        {
            Chr* flinchParty = _battleAdapter.GetPlayerCharacters();
            Chr* flinchTarget = flinchParty != null ? flinchParty + target : null;
            suppressFlinch = flinchTarget != null && flinchTarget->stat_exist_flag && flinchTarget->damage_hp > 0;
        }

        // Dodge (L1) resolves at impact, symmetric to parry: if the window is still valid for
        // this attacker, this slot was among the armed targets, and real damage is pending, negate
        // it and run the engine's own evade. The target-mask check keeps an untargeted party slot
        // from resolving off the cue-wide window/attacker gate (mirrors DodgeCommitGate at p5=0).
        bool dodgeActive = targetIsParty && p3 == 1
            && DodgeCommitGate.MayResolveAtImpact(
                _optionDodgeEnabled,
                _dodgeWindowActive && _runtime.CueFirstSeenFrame == _dodgeArmedCueFrame,
                _dodgeArmedAttackerId,
                _runtime.CurrentAttackerId,
                _dodgeArmedTargetMask,
                target);
        bool dodgeSuppress = false;
        if (dodgeActive)
        {
            Chr* dodgeParty = _battleAdapter.GetPlayerCharacters();
            Chr* dodgeTarget = dodgeParty != null ? dodgeParty + target : null;
            dodgeSuppress = dodgeTarget != null && dodgeTarget->stat_exist_flag && dodgeTarget->damage_hp > 0;
        }

        bool defended = (dodgeActive && dodgeSuppress) || (parryActive && suppressFlinch);

        if (!defended)
        {
            orig_ms_damage_set_motion(target, p2, p3);
        }
        else
        {
            if (dodgeActive && dodgeSuppress)
            {
                // The step-out movement was triggered on press (handle_dodge_input_press). At
                // impact we only negate the hit and suppress the flinch (skip orig → no hit
                // reaction, no second evade trigger). The resolved-mask makes MsSetDamageInternal
                // skip its authoritative HP/death commit; the shared restore below undoes any
                // HP already applied.
                _dodgeEvadeCount++;
                mark_dodge_resolved(target);
                _dodgeTextRemainingSeconds = ParriedTextSeconds;
                _dodgeTextSeed = next_label_seed();
                _dodgeTextTargetMask |= 1u << target;
                log_debug($"Dodge negated hit for {format_actor_slot(target)} (movement started on press; flinch suppressed, dodge#{_dodgeEvadeCount}).");
            }
            else if (_optionParryNativeBlock && !parry_block_recently_played(target))
            {
                // Native block (A): flag the parrying char as guarding (ChrRam+0x19A) so orig's
                // MsDamageSetMotion plays the block reaction 0x43 itself at the real impact — the
                // same field the engine sets for Sentinel/Defend (FFX.exe.c:830149). Set→orig→
                // restore keeps it confined to this one call so it never persists onto later hits.
                Chr* blockParty = _battleAdapter.GetPlayerCharacters();
                Chr* blockChr = blockParty != null ? blockParty + target : null;
                if (blockChr != null)
                {
                    byte* ramGuard = (byte*)&blockChr->ram + ChrRamGuardReactFlagOffset;
                    byte prevGuard = *ramGuard;
                    *ramGuard = 1;
                    orig_ms_damage_set_motion(target, p2, p3);
                    *ramGuard = prevGuard;
                }
                else
                {
                    orig_ms_damage_set_motion(target, p2, p3);
                }
                if (target < PartyActorCapacity) _parryBlockPlayedFrame[target] = _debugFrameIndex;
                log_debug($"Parry native block for {format_actor_slot(target)} (guard flag → engine plays 0x43).");
            }
            else
            {
                log_debug($"Parry suppressed flinch for {format_actor_slot(target)} (MsDamageSetMotion skipped).");
            }

            // Additive restore: ram.hp += damage_hp undoes whatever the native pipeline
            // already applied. For reactive parries the snapshot is never set (calc fired
            // before R1 press), so we must not gate on snapshot > 0.
            // If the snapshot WAS set (proactive interception), cap to it to prevent
            // over-restore when a lethal hit clamped HP to 0 before our hook fired.
            //
            // Lethal restore: if HP was already ≤ 0 when our hook fires (the unknown native
            // reducer applied damage before MsDamageSetMotion was called), the same native path
            // may have also set the death latch (chr+OffsetDeathLatch) and dead-status bit
            // (chr+OffsetStatusBits & DeadStatusBitMask). See ExternalMemoryOffsetMap.ChrStruct
            // for offsets + evidence. Clear both after restoring HP — same fields cleared by
            // the h_ms_set_damage skipOrigForParry path, moved here because that path is dead
            // code for production windows (MsSetDamage fires ~2.5s after press, well past the
            // 200ms window).
            if (_optionNegateDamage)
            {
                Chr* party = _battleAdapter.GetPlayerCharacters();
                Chr* targetChr = party != null ? party + target : null;
                if (targetChr != null && targetChr->stat_exist_flag && targetChr->damage_hp > 0)
                {
                    bool wasLethal = targetChr->ram.hp <= 0;
                    int restored = (int)targetChr->ram.hp + targetChr->damage_hp;
                    uint snap = _preHitHpSnapshot[target];
                    if (snap > 0 && (uint)restored > snap) restored = (int)snap;
                    targetChr->ram.hp = restored;

                    if (wasLethal && targetChr->ram.hp > 0)
                    {
                        // Clear the same death-latch fields as the h_ms_set_damage path —
                        // see ExternalMemoryOffsetMap.ChrStruct for evidence + offsets.
                        byte* chrB = (byte*)targetChr;
                        chrB[ExternalMemoryOffsetMap.ChrStruct.OffsetDeathLatch] = 0;
                        *(ushort*)(chrB + ExternalMemoryOffsetMap.ChrStruct.OffsetStatusBits)
                            &= unchecked((ushort)~ExternalMemoryOffsetMap.ChrStruct.DeadStatusBitMask);
                        targetChr->stat_will_die = 0;

                        log_debug($"Parry lethal restore (motion) for {format_actor_slot(target)}: HP restored to {(uint)targetChr->ram.hp}, death-latch cleared.");
                    }
                    else
                    {
                        log_debug($"Parry restored {targetChr->damage_hp} HP for {format_actor_slot(target)} (hp now={(uint)targetChr->ram.hp}).");
                    }
                }
            }
        }

        // p3=1 signals a damage-involved motion for party targets (confirmed from session log
        // analysis: p2=5 for standard physical attacks, p2=1 for ramming attacks — both with p3=1).
        // p3=0 calls are cosmetic/non-damage motions and must not trigger parry resolution.
        bool isDamageMotion = p3 == 1 && targetIsParty;

        if (isDamageMotion)
            _attackTelemetry[target].SetMotionFired = true;

        if (isDamageMotion)
        {
            bool pendingDeferred = _parryFeedbackPending[target];
            Chr* reactiveCandidate = null;
            bool reactiveParry   = false;
            if (!pendingDeferred
                && _optionEnabled
                && parryActive
                && (_runtime.CurrentPartyTargetMask & (1u << target)) != 0
                && (_runtime.LastParriedTargetMask  & (1u << target)) == 0)
            {
                Chr* rParty = _battleAdapter.GetPlayerCharacters();
                reactiveCandidate = rParty != null ? rParty + target : null;
                // Only resolve reactively if actual damage is pending — damage_hp=0 means
                // the enemy missed/was evaded, not a successful parry. Also skip if the target
                // was already non-parryable (confused, berserk, etc.) before this hit.
                reactiveParry = reactiveCandidate != null
                    && reactiveCandidate->damage_hp > 0
                    && !is_target_non_parryable(reactiveCandidate);
            }

            if (pendingDeferred || reactiveParry)
            {
                // Set the durable impact marker for this slot so that h_ms_set_damage_internal
                // p5=1024 can skip the authoritative HP/death commit without re-checking the
                // wall-clock window. The marker survives window expiry — critical for attacks
                // (Anfunkeln, Blitzra) where the commit pass is delayed past the timing window.
                // Guard: only set once per slot per turn.
                if ((_parryResolvedAtImpactMask & (1u << target)) == 0)
                {
                    _parryResolvedAtImpactMask |= 1u << target;
                }

                Chr* party = _battleAdapter.GetPlayerCharacters();
                Chr* candidate = party != null ? party + target : null;
                if (candidate != null && candidate->stat_exist_flag)
                {
                    string source = pendingDeferred ? "deferred" : "visual";
                    resolve_successful_parry(target, candidate, source, closeWindow: false);
                }
                _parryFeedbackPending[target] = false;
            }
        }

        if (_optionLogging)
        {
            write_session_hook_entry($"[MsDamageSetMotion] f={_debugFrameIndex} target={target} p2={p2} p3={p3} parry={_runtime.ParryWindowActive}");
        }
    }

    private int h_dmg_calc_armored(Chr* user, Chr* target, Command* command, int p4, int* p5, int damage)
    {
        int result = orig_dmg_calc_armored(user, target, command, p4, p5, damage);

        if (_optionLogging)
        {
            write_session_hook_entry($"[DmgCalcArmored] f={_debugFrameIndex} damage_in={damage} damage_out={result} parry={_runtime.ParryWindowActive}");
        }

        return result;
    }

    private int h_ms_calc_damage_internal(
        int user_id, nint user_chr, int target_id, nint target_chr,
        nint command, int command_id,
        nint p7, nint p8, nint p9, nint p10, int p11)
    {
        // Inner interception: check parry expiry BEFORE calling orig.
        // This is the safety net for attacks where the outer h_ms_calc_damage interception
        // fails because LastParriedTargetMask was already set by MsDamageSetMotion.
        // Do NOT check LastParriedTargetMask here — that is the entire reason this inner
        // hook exists. The outer already checks it; when outer passes through, inner must
        // still fire based on expiry alone.
        bool isPartyTarget = target_id >= 0 && target_id < PartyActorCapacity;
        bool innerShouldIntercept = isPartyTarget
            && _optionEnabled
            && DateTime.UtcNow.Ticks < _parryExpiry[target_id];

        if (innerShouldIntercept)
        {
            // Snapshot HP if not already captured by the outer h_ms_calc_damage hook.
            if (_preHitHpSnapshot[target_id] == 0 && target_chr != nint.Zero)
            {
                Chr* snapTarget = (Chr*)target_chr;
                if (snapTarget->stat_exist_flag)
                    _preHitHpSnapshot[target_id] = (uint)snapTarget->ram.hp;
            }
            _parryFeedbackPending[target_id] = true;
            _runtime.LastParriedTargetMask |= 1u << target_id;
            _damageEventActive[target_id] = true;
            write_session_hook_entry(
                $"[DmgCalcInternal] INTERCEPTED frame={_debugFrameIndex} user={user_id} target={target_id} cmd=0x{command_id:X4}");
            return 0;
        }

        int damageHpBefore = 0;
        int ramHpBefore = 0;
        bool readOk = false;

        if (_optionLogging && target_chr != nint.Zero)
        {
            try
            {
                Chr* tgt = (Chr*)target_chr;
                damageHpBefore = tgt->damage_hp;
                ramHpBefore = tgt->ram.hp;
                readOk = true;
            }
            catch
            {
                // target_chr may not be a valid Chr* — swallow and log what we can
            }
        }

        int result = orig_ms_calc_damage_internal(
            user_id, user_chr, target_id, target_chr,
            command, command_id,
            p7, p8, p9, p10, p11);

        if (_optionLogging)
        {
            int damageHpAfter = 0;
            int ramHpAfter = 0;

            if (readOk && target_chr != nint.Zero)
            {
                try
                {
                    Chr* tgt = (Chr*)target_chr;
                    damageHpAfter = tgt->damage_hp;
                    ramHpAfter = tgt->ram.hp;
                }
                catch
                {
                    readOk = false;
                }
            }

            if (readOk)
            {
                write_session_hook_entry(
                    $"[DmgCalcInternal] f={_debugFrameIndex} user={user_id} target={target_id} cmd=0x{command_id:X4} result={result}" +
                    $" ramHp_before={ramHpBefore} ramHp_after={ramHpAfter}" +
                    $" damageHp_before={damageHpBefore} damageHp_after={damageHpAfter}" +
                    $" hits={p11}");
            }
            else
            {
                write_session_hook_entry(
                    $"[DmgCalcInternal] f={_debugFrameIndex} user={user_id} target={target_id} cmd=0x{command_id:X4} result={result}" +
                    $" (target_chr read failed) hits={p11}");
            }
        }

        return result;
    }

    /// <summary>
    ///     UI-only suppression for already-parried hits:
    ///     clear staged damage fields and drop matching BtlPos entries so delayed native
    ///     display paths are less likely to emit misleading floating numbers.
    /// </summary>
    private void suppress_parried_damage_display(Chr* slot, int slotIndex, byte attackerId, byte commandId, string source)
    {
        if (slot == null || !slot->stat_exist_flag)
            return;

        int hpBefore = slot->damage_hp;
        int mpBefore = slot->damage_mp;
        int ctbBefore = slot->damage_ctb;

        negate_damage_on_impact(slot);

        byte* slotB = (byte*)slot;
        byte btl0AttBefore = slotB[0x776];
        byte btl0CmdBefore = slotB[0x777];
        byte btl1AttBefore = slotB[0xA4E];
        byte btl1CmdBefore = slotB[0xA4F];

        bool clearedBtl0 = false;
        bool clearedBtl1 = false;
        if (slotB[0x776] == attackerId && slotB[0x777] == commandId)
        {
            slotB[0x776] = 0xFF;
            clearedBtl0 = true;
        }

        if (slotB[0xA4E] == attackerId && slotB[0xA4F] == commandId)
        {
            slotB[0xA4E] = 0xFF;
            clearedBtl1 = true;
        }

        if (_optionLogging && (hpBefore != 0 || mpBefore != 0 || ctbBefore != 0 || clearedBtl0 || clearedBtl1))
        {
            write_session_hook_entry(
                $"[ParryUiSuppress] f={_debugFrameIndex} slot={slotIndex} src={source} atk={attackerId} cmd=0x{commandId:X2} " +
                $"dmg_hp={hpBefore}->{slot->damage_hp} dmg_mp={mpBefore}->{slot->damage_mp} dmg_ctb={ctbBefore}->{slot->damage_ctb} " +
                $"btl0={btl0AttBefore}/{btl0CmdBefore:X2}->{slotB[0x776]}/{slotB[0x777]:X2} " +
                $"btl1={btl1AttBefore}/{btl1CmdBefore:X2}->{slotB[0xA4E]}/{slotB[0xA4F]:X2}");
        }
    }

    private void clear_internal_intercepted_slot(int slotIndex)
    {
        if ((uint)slotIndex >= PartyActorCapacity)
            return;

        _internalInterceptedMask &= ~(1u << slotIndex);
        _internalInterceptedAttackerId[slotIndex] = 0;
    }

    private bool is_late_preopen_p5_zero_commit(int slotIndex, int attackerId)
    {
        if ((uint)slotIndex >= PartyActorCapacity)
            return false;

        return (_latePreOpenP5ZeroCommitMask & (1u << slotIndex)) != 0
            && _latePreOpenP5ZeroCommitAttackerId[slotIndex] == (byte)attackerId;
    }

    private void mark_late_preopen_p5_zero_commit(int slotIndex, int attackerId)
    {
        if ((uint)slotIndex >= PartyActorCapacity)
            return;

        _latePreOpenP5ZeroCommitMask |= 1u << slotIndex;
        _latePreOpenP5ZeroCommitAttackerId[slotIndex] = (byte)attackerId;
    }

    private void clear_late_preopen_p5_zero_commit_slot(int slotIndex)
    {
        if ((uint)slotIndex >= PartyActorCapacity)
            return;

        _latePreOpenP5ZeroCommitMask &= ~(1u << slotIndex);
        _latePreOpenP5ZeroCommitAttackerId[slotIndex] = 0;
    }

    private bool has_live_enemy_damage_context_for_slot(int attackerId, int slotIndex)
    {
        if ((uint)slotIndex >= PartyActorCapacity)
            return false;

        if (!_runtime.AwaitingTurnEnd)
            return false;

        if (attackerId != _runtime.CurrentAttackerId)
            return false;

        if ((_runtime.CurrentPartyTargetMask & (1u << slotIndex)) == 0)
            return false;

        if (!try_get_enemy_attack_cue(out AttackCue cue, out byte cueIndex, out _))
            return false;

        if (cueIndex != _runtime.CurrentCueIndex)
            return false;

        uint liveMask = extract_party_target_mask(cue);
        return (liveMask & (1u << slotIndex)) != 0;
    }

    private bool is_late_preopen_duplicate_skip_safe(int slotIndex)
    {
        if ((uint)slotIndex >= PartyActorCapacity)
            return false;

        Chr* party = _battleAdapter.GetPlayerCharacters();
        Chr* slot = party != null ? party + slotIndex : null;
        if (slot == null || !slot->stat_exist_flag)
            return false;

        // Freeze/crash safety: if p5=0 already pushed the slot into a native death-latch
        // state, let native p5=1024 run so battle/UI finalization can complete normally.
        if (slot->ram.hp <= 0 || slot->stat_will_die != 0)
            return false;

        // See ExternalMemoryOffsetMap.ChrStruct for offsets + evidence.
        byte* slotB = (byte*)slot;
        if (slotB[ExternalMemoryOffsetMap.ChrStruct.OffsetDeathLatch] != 0)
            return false;
        if ((*(ushort*)(slotB + ExternalMemoryOffsetMap.ChrStruct.OffsetStatusBits)
            & ExternalMemoryOffsetMap.ChrStruct.DeadStatusBitMask) != 0)
            return false;

        return true;
    }

    /// <summary>
    ///     Phase 2 reactive interception at MsSetDamageInternal (FUN_0078f0b0 at FFX.exe+0x38F0B0).
    ///
    ///     Architecture:
    ///     - p5=0: intercepted when parry window is still live (isActiveParry) or slot already resolved.
    ///       Calls resolve_successful_parry if not yet resolved (group-attack fallback path).
    ///       Sets _internalInterceptedMask for correlated p5=1024/finalization handling.
    ///     - p5=1024: the confirmed HP/death commit pass for delayed-finalization attacks (Anfunkeln,
    ///       Blitzra, Hauch). Skip when alreadyResolved, markerSet, or internalBlocked.
    ///       Also skips when _parryResolvedAtImpactMask is set (MsDamageSetMotion marker path, if available).
    ///     - Feedback (text/sound) is emitted at resolve_successful_parry; no second signal here.
    /// </summary>
    private int h_ms_set_damage_internal(int param_1, byte param_2, int param_3, int param_4, int param_5)
    {
        bool isPartySlot = param_3 >= 0 && param_3 < PartyActorCapacity;

        // Phase 2: authoritative commit skip via durable impact marker or prior resolution.
        // - p5=0: skip if already resolved, OR if the parry window is currently active for this slot.
        // - p5=1024: skip if the durable impact marker is set (handles delayed-finalization).
        if (isPartySlot && _optionEnabled && _optionNegateDamage)
        {
            bool hasLiveEnemyContext = has_live_enemy_damage_context_for_slot(param_1, param_3);
            bool markerSet = (_parryResolvedAtImpactMask & (1u << param_3)) != 0;
            bool alreadyResolved = (_runtime.LastParriedTargetMask & (1u << param_3)) != 0;
            bool latePreOpenCommitted = is_late_preopen_p5_zero_commit(param_3, param_1)
                && hasLiveEnemyContext;
            bool latePreOpenSkipSafe = is_late_preopen_duplicate_skip_safe(param_3);

            bool isActiveParry = !alreadyResolved
                && DateTime.UtcNow.Ticks < _parryExpiry[param_3]
                && hasLiveEnemyContext;

            // Dodge: a valid dodge window from this attacker skips the ENTIRE p5=0 commit
            // (HP + status + death + the inner flinch/MsDamageSetMotion). This is what blocks a
            // dodged STATUS attack (e.g. Anfunkeln → Confuse) — status is applied earlier in this
            // same call, before MsDamageSetMotion, so the reactive motion-hook negation was too
            // late. The evade animation was already triggered on press, so skipping is clean.
            // Two ways in: the wall-clock window is still live, or this slot already resolved as
            // evaded earlier in this cue (durable marker). The marker is what carries a dodge
            // across a chargeup longer than the window, and across cue mutation mid-cast. Either
            // way the slot must also be in _dodgeArmedTargetMask (checked by MayResolveAtImpact):
            // the engine drives this p5=0 commit for every party slot, so the window/attacker gate
            // is cue-wide and would otherwise resolve an evade for slots the attack never targeted.
            bool dodgeWindowLive = _dodgeWindowActive
                && _runtime.CueFirstSeenFrame == _dodgeArmedCueFrame;
            bool dodgeMarkerSet = (_dodgeResolvedAtImpactMask & (1u << param_3)) != 0;

            if (param_5 == 0
                && DodgeCommitGate.MayResolveAtImpact(
                    _optionDodgeEnabled,
                    dodgeWindowLive || dodgeMarkerSet,
                    _dodgeArmedAttackerId,
                    (byte)param_1,
                    _dodgeArmedTargetMask,
                    param_3))
            {
                _dodgeEvadeCount++;
                mark_dodge_resolved(param_3);   // durable marker + perfect grade (idempotent per cue)
                _dodgeTextRemainingSeconds = ParriedTextSeconds;
                _dodgeTextSeed = next_label_seed();
                _dodgeTextTargetMask |= 1u << param_3;
                Chr* dodgeParty = _battleAdapter.GetPlayerCharacters();
                Chr* dodgeSlot = dodgeParty != null ? dodgeParty + param_3 : null;
                suppress_parried_damage_display(dodgeSlot, param_3, (byte)param_1, param_2, "dodge");
                log_debug($"MsSetDamageInternal p5=0 skipped for {format_actor_slot((byte)param_3)} (dodge — HP+status+death blocked).");
                return 0;
            }

            if (param_5 == 0
                && !alreadyResolved
                && !isActiveParry
                && !latePreOpenCommitted
                && hasLiveEnemyContext)
            {
                Chr* partyForLate = _battleAdapter.GetPlayerCharacters();
                Chr* slotForLate = partyForLate != null ? partyForLate + param_3 : null;
                if (slotForLate != null && slotForLate->stat_exist_flag && slotForLate->damage_hp > 0)
                {
                    mark_late_preopen_p5_zero_commit(param_3, param_1);
                    write_session_hook_entry(
                        $"[MsSetDamageInternal] f={_debugFrameIndex} slot={param_3} p5=0 late_preopen=1 observed " +
                        $"attacker={param_1} cmd={param_2}");
                }
            }

            if (param_5 == 0 && (alreadyResolved || (isActiveParry && !latePreOpenCommitted)))
            {
                if (isActiveParry)
                {
                    // If h_ms_set_damage p2=target didn't resolve it (e.g. group attack p2=-5),
                    // resolve it here before skipping the native commit.
                    Chr* party = _battleAdapter.GetPlayerCharacters();
                    Chr* candidate = party != null ? party + param_3 : null;
                    if (candidate != null && candidate->stat_exist_flag && candidate->damage_hp > 0)
                    {
                        resolve_successful_parry(param_3, candidate, "internal_impact", closeWindow: false);
                    }
                }

                // p5=0 commit skip. Record attacker id so that the later p5=1024 skip can
                // require exact attacker match — preventing a different next attacker's p5=1024
                // from inheriting this slot's interception evidence if end_parry_window fires
                // between the two passes (e.g. cue mutation / "attacker changed").
                Chr* partyForUi = _battleAdapter.GetPlayerCharacters();
                Chr* slotForUi = partyForUi != null ? partyForUi + param_3 : null;
                suppress_parried_damage_display(slotForUi, param_3, (byte)param_1, param_2, "p5=0");

                _internalInterceptedMask |= 1u << param_3;
                _internalInterceptedAttackerId[param_3] = (byte)param_1;
                write_session_hook_entry(
                    $"[MsSetDamageInternal] f={_debugFrameIndex} slot={param_3} p5=0 resolved=1 -> SKIP " +
                    $"attacker={param_1} cmd={param_2}");
                log_debug($"MsSetDamageInternal p5=0 commit skipped for {format_actor_slot((byte)param_3)} (resolved).");

                return 0;
            }

            if (param_5 == 1024 && latePreOpenCommitted && !alreadyResolved && !markerSet)
            {
                if (!latePreOpenSkipSafe)
                {
                    write_session_hook_entry(
                        $"[MsSetDamageInternal] f={_debugFrameIndex} slot={param_3} p5=1024 late_preopen=1 -> PASS_ORIG death_latch=1 " +
                        $"attacker={param_1} cmd={param_2}");
                    log_debug($"MsSetDamageInternal p5=1024 late pre-open pass-through for {format_actor_slot((byte)param_3)} (death-latched slot).");
                    clear_late_preopen_p5_zero_commit_slot(param_3);
                }
                else
                {
                    // Late-preopen duplicate path: clear staged values before skipping so stale
                    // damage records cannot bleed into later item/system turns.
                    Chr* partyForUi = _battleAdapter.GetPlayerCharacters();
                    Chr* slotForUi = partyForUi != null ? partyForUi + param_3 : null;
                    suppress_parried_damage_display(slotForUi, param_3, (byte)param_1, param_2, "p5=1024_late_preopen");

                    write_session_hook_entry(
                        $"[MsSetDamageInternal] f={_debugFrameIndex} slot={param_3} p5=1024 late_preopen=1 -> SKIP_DUP " +
                        $"attacker={param_1} cmd={param_2}");
                    log_debug($"MsSetDamageInternal p5=1024 duplicate commit skipped for {format_actor_slot((byte)param_3)} (late pre-open commit).");
                    clear_internal_intercepted_slot(param_3);
                    clear_late_preopen_p5_zero_commit_slot(param_3);
                    return 0;
                }
            }

            // p5=1024 is the authoritative HP/death commit for delayed-finalization attacks
            // (Anfunkeln, Blitzra, Hauch). Three skip paths:
            //   markerSet     — durable marker set at MsDamageSetMotion visual-impact time
            //   alreadyResolved — LastParriedTargetMask set by a prior resolution call
            //   internalBlocked — p5=0 was intercepted for this slot, same attacker:
            //                     the window may have been closed by "attacker changed" between
            //                     p5=0 and p5=1024, but the attacker id must still match to
            //                     prevent a different next-attacker's commit from being skipped.
            bool internalBlocked =
                (_internalInterceptedMask & (1u << param_3)) != 0
                && _internalInterceptedAttackerId[param_3] == (byte)param_1;

            // Dodge finalization: skip the p5=1024 commit for a dodge-resolved slot WITHOUT
            // setting LastParriedTargetMask (that would draw a second "PARRIED" text over "DODGE").
            // Marker is NOT consumed here: a multi-hit / AoE swing from the armed attacker must stay
            // fully evaded, and its later hits commit through this same pass. It is cleared in
            // clear_awaiting_turn_end, next to its parry twin. The attacker check is what stops a
            // surviving marker from swallowing an unrelated attacker's commit.
            if (param_5 == 1024
                && DodgeCommitGate.ShouldSkipCommit(
                    _optionDodgeEnabled, dodgeMarkerSet, _dodgeArmedAttackerId, (byte)param_1))
            {
                Chr* dodgeFinParty = _battleAdapter.GetPlayerCharacters();
                Chr* dodgeFinSlot = dodgeFinParty != null ? dodgeFinParty + param_3 : null;
                suppress_parried_damage_display(dodgeFinSlot, param_3, (byte)param_1, param_2, "dodge_p5=1024");
                log_debug($"MsSetDamageInternal p5=1024 skipped for {format_actor_slot((byte)param_3)} (dodge).");
                return 0;
            }

            if (param_5 == 1024 && (markerSet || alreadyResolved || internalBlocked))
            {
                if (markerSet)
                    _parryResolvedAtImpactMask &= ~(1u << param_3);
                _internalInterceptedMask |= 1u << param_3;
                _runtime.LastParriedTargetMask |= 1u << param_3;
                clear_late_preopen_p5_zero_commit_slot(param_3);

                string skipReason = markerSet ? "marker=1"
                    : alreadyResolved ? "resolved=1"
                    : $"internal=1 attacker={param_1}";
                write_session_hook_entry(
                    $"[MsSetDamageInternal] f={_debugFrameIndex} slot={param_3} p5=1024 {skipReason} -> SKIP " +
                    $"attacker={param_1} cmd={param_2}");
                log_debug($"MsSetDamageInternal p5=1024 commit skipped for {format_actor_slot((byte)param_3)} ({(markerSet ? "impact marker" : alreadyResolved ? "resolved" : "internal intercepted")}).");

                Chr* party = _battleAdapter.GetPlayerCharacters();
                Chr* candidate = party != null ? party + param_3 : null;
                suppress_parried_damage_display(candidate, param_3, (byte)param_1, param_2, "p5=1024");

                // Hit lifecycle boundary: this slot's p5=1024 completion was handled,
                // so stale p5=0 evidence must not leak into the next hit in the same turn.
                clear_internal_intercepted_slot(param_3);

                return 0;
            }
        }

        // Status-leak trace: every party-slot pass that reaches orig. _MsAfterDamageProcess writes
        // status_suffer in the same block as the HP subtract, so any pass logged here with a dodge
        // that looked valid to the player is a slot where Petrify/Confuse gets through. The dodge
        // gates are wall-clock (_dodgeWindowActive); the parry ones are durable markers — this line
        // shows which of them was still standing when the commit landed.
        if (_optionLogging && isPartySlot)
        {
            Chr* traceParty = _battleAdapter.GetPlayerCharacters();
            Chr* traceSlot = traceParty != null ? traceParty + param_3 : null;
            if (traceSlot != null && traceSlot->stat_exist_flag)
            {
                write_session_hook_entry(
                    $"[StatusTrace] f={_debugFrameIndex} slot={param_3} p5={param_5} attacker={param_1} cmd={param_2} -> PASS_ORIG "
                    + $"dodge_win={(_dodgeWindowActive ? 1 : 0)} dodge_valid={(is_dodge_window_valid() ? 1 : 0)} "
                    + $"dodge_marker={((_dodgeResolvedAtImpactMask & (1u << param_3)) != 0 ? 1 : 0)} "
                    + $"parry_marker={((_parryResolvedAtImpactMask & (1u << param_3)) != 0 ? 1 : 0)} "
                    + $"resolved={((_runtime.LastParriedTargetMask & (1u << param_3)) != 0 ? 1 : 0)} "
                    + $"stone={traceSlot->stat_stone} suffer={traceSlot->ram.status_suffer}");
            }
        }

        if (_optionLogging && _runtime.ParryWindowActive)
        {
            // Slot-level pre-commit state: log BEFORE calling orig so we see pre-commit snapshot.
            if (isPartySlot)
            {
                Chr* party = _battleAdapter.GetPlayerCharacters();
                if (party != null)
                {
                    Chr* slot = party + param_3;
                    if (slot->stat_exist_flag)
                    {
                        byte* slotB = (byte*)slot;
                        write_session_hook_entry(
                            $"[MsSetDamageInternal/slot] f={_debugFrameIndex} slot={param_3} " +
                            $"hp={(uint)slot->ram.hp} dmg_hp={slot->damage_hp} " +
                            $"0x606=0x{*(ushort*)(slotB + ExternalMemoryOffsetMap.ChrStruct.OffsetStatusBits):X4} " +
                            $"0xDCC={slotB[ExternalMemoryOffsetMap.ChrStruct.OffsetDeathLatch]} " +
                            $"btl0_att={slotB[0x776]} btl0_cmd=0x{slotB[0x777]:X2} " +
                            $"btl1_att={slotB[0xA4E]} btl1_cmd=0x{slotB[0xA4F]:X2}");
                    }
                }
            }

            // Raw params — only logged during an active parry window.
            write_session_hook_entry(
                $"[MsSetDamageInternal] f={_debugFrameIndex} p1={param_1} p2={param_2} p3={param_3} p4={param_4} p5={param_5} " +
                $"attacker={_runtime.CurrentAttackerId} targetMask={_runtime.CurrentPartyTargetMask} windowActive={_runtime.ParryWindowActive}");
        }

        int result = orig_ms_set_damage_internal(param_1, param_2, param_3, param_4, param_5);

        return result;
    }

    private bool try_get_head_cue_snapshot(List<DebugCueSnapshot> scratch, out DebugCueSnapshot head)
    {
        scratch.Clear();
        collect_live_cues(scratch, out _);
        if (scratch.Count == 0)
        {
            head = default;
            return false;
        }

        head = scratch[0];
        return true;
    }

    /// <summary>
    ///     Active hook on MsAtelRequestCamera (FFX.exe+0x397BD0).
    ///
    ///     The function is the central gate for in-game camera change requests
    ///     (called from 12 sites — battle camera setup, scene transitions, etc.).
    ///     We intercept and short-circuit the call when both:
    ///       - the user has the enemy-turn camera lock enabled (default-on);
    ///       - and an enemy turn is currently in progress (cue active, attacker id
    ///         outside party slot range 0..PartyActorCapacity-1).
    ///
    ///     Result: during enemy turns the camera stays at whatever position the
    ///     game left it in for the player, keeping incoming attack animations
    ///     readable for parry timing. Player turns and out-of-battle camera
    ///     changes pass through unchanged.
    ///
    ///     Return value at all 12 observed call sites is unused, so returning 0
    ///     on suppression is safe.
    /// </summary>
    // Debug camera probe: logs a camera hook invocation with the full lock-gating state, whether
    // or not it was suppressed. Reveals un-locked enemy camera pans (which path fired + why the
    // lock did not engage). Gated on the "camera_probe" setting.
    private void probe_camera_call(string fn, string args, bool anyTurn, bool enemyTurn, bool suppress)
    {
        if (!_optionCameraProbe) return;
        log_debug($"[CameraProbe] {fn}({args}) turn_active={anyTurn} enemy_turn={enemyTurn} attacker={_runtime.CurrentAttackerId} suppress={suppress}");
    }

    // Debug writer probe: logs the ATEL opcodes that actually move the battle camera.
    // Distinct from probe_camera_call, which covers the *request* path (MsAtelRequestCamera
    // and siblings) — those only enqueue camera scripts and never touch camera state, which
    // is why an enemy pan runs straight past all three of them. Gated on "camera_probe".
    private void probe_camera_writer(string fn, string args)
    {
        if (!_optionCameraProbe) return;

        bool anyTurn   = _runtime.AwaitingTurnEnd;
        bool enemyTurn = anyTurn && _runtime.CurrentAttackerId >= PartyActorCapacity;
        log_debug($"[CameraWriter] {fn}({args}) turn_active={anyTurn} enemy_turn={enemyTurn} attacker={_runtime.CurrentAttackerId}");
    }

    // The two wrapper constants FUN_007bad30 receives identify the calling opcode exactly.
    private static string polar_opcode_name(int isCam, int variant) => (isCam, variant) switch
    {
        (1, 1) => "camSetBtlPolar",
        (0, 1) => "refSetBtlPolar",
        (1, 2) => "camSetBtlPolar2",
        (0, 2) => "refSetBtlPolar2",
        (1, 3) => "camSetChrPolar",
        (1, 4) => "camSetChrPolar2",
        _      => $"unknownPolar(isCam={isCam},variant={variant})",
    };

    /// <summary>
    ///     True while the battle camera lock should hold the camera hard — the AllTurns mode. In
    ///     that mode, suppressing camera *requests* is not enough: the request lock only fires while
    ///     a turn is active (AwaitingTurnEnd), and the ATEL position writers that pan the camera on
    ///     the player's own turn run with AwaitingTurnEnd false, so they slip past it. So AllTurns
    ///     also stops those writers directly. EnemyTurnsOnly leaves them alone — the writer probe
    ///     shows they never fire during an enemy action, so the request lock covers that mode.
    /// </summary>
    private bool camera_hard_lock_engaged() => should_hold_camera_pose();

    // Whether to suppress a camera writer this call. Always tries to cache the battle camera id (the
    // freecam needs it). Suppresses when the hard lock is engaged — except while the freecam is on
    // but the id is still unresolved, when the write is let through so the game resolves the id.
    // True while we should hold the battle camera still (AllTurns). Immediately when we have a pose to
    // hold (freecam or a saved start camera); otherwise only after the settle grace, so the game frames
    // the battle at its correct default first. This drives BOTH the writer suppression and the request
    // suppression: with requests held too, an ability's camera script — which also drives the
    // zoom/depth writers we do not hook — never queues, so the camera cannot zoom out from under us.
    private bool should_hold_camera() => should_hold_camera_pose();

    private bool should_suppress_camera_writer(int worker, string source, string detail)
    {
        capture_battle_camera_id(worker);
        if (_battleCameraId == 0) return false;   // let the game's write through so the camera id resolves

        // A writer sees the issuing worker but not the request's followed actor, so it cannot be a
        // flow camera as far as this call can tell: -1 says "no actor carried".
        return !camera_move_allowed(source, worker, followedActor: -1, detail: detail)
            && should_hold_camera_pose();
    }

    /// <summary>
    ///     Replicate a suppressed camera writer's ATEL stack pops without applying the write, so the
    ///     VM stack stays balanced — an unbalanced pop desyncs it. Each value must be popped with the
    ///     same primitive the callee used (the pop functions check a per-value type byte), in the
    ///     same order: floats first, then ints. `size` is at offset 0 of AtelStack, so the one
    ///     `stack` pointer serves both pops. Verified against FUN_007bad30 (4 float + 2 int) and
    ///     FUN_007bb620 (3 float).
    /// </summary>
    private void drain_camera_writer_stack(int worker, int stack, int floats, int ints)
    {
        _popStackFloat   ??= get_fptr<AtelPopStackFloatFn>(ExternalMemoryOffsetMap.Functions.AtelPopStackFloat);
        _popStackInteger ??= get_fptr<AtelPopStackIntegerFn>(ExternalMemoryOffsetMap.Functions.AtelPopStackInteger);
        for (int i = 0; i < floats; i++) _popStackFloat(worker, stack);
        for (int i = 0; i < ints; i++)   _popStackInteger(worker, stack);
    }

    /// <summary>
    ///     Hook on FUN_007bad30 (FFX.exe+0x3BAD30) — the actor-relative polar camera writer shared by
    ///     all six camSetBtlPolar/refSetBtlPolar/camSetChrPolar variants. Observe-only unless the
    ///     hard lock is engaged, in which case the write is suppressed: the callee's stack pops
    ///     (4x float, then 2x int) are replicated so the ATEL stack stays balanced, and the original
    ///     — which is what actually moves the camera — is skipped.
    /// </summary>
    private int h_atel_camera_polar_set(int worker, int p2, int stack, int isCam, int variant)
    {
        string name = polar_opcode_name(isCam, variant);
        probe_camera_writer(name, $"isCam={isCam},variant={variant}");
        if (should_suppress_camera_writer(worker, name, $"isCam={isCam},variant={variant}"))
        {
            drain_camera_writer_stack(worker, stack, floats: 4, ints: 2);
            _cameraWriterSuppressCount++;
            return 0;
        }
        return orig_atel_camera_polar_set(worker, p2, stack, isCam, variant);
    }

    /// <summary>
    ///     Hook on FUN_007bb620 (FFX.exe+0x3BB620) — the absolute-position camera writer behind
    ///     camSetPos, the dominant writer outside enemy turns. Observe-only unless the hard lock is
    ///     engaged, in which case the write is suppressed: the callee pops 3 floats off the ATEL
    ///     stack (no ints), replicated here, and the original is skipped.
    /// </summary>
    private void h_atel_camera_pos_set(int worker, int p2, int stack, int p4)
    {
        probe_camera_writer("camSetPos", $"p4={p4}");
        if (should_suppress_camera_writer(worker, "camSetPos", $"p4={p4}"))
        {
            drain_camera_writer_stack(worker, stack, floats: 3, ints: 0);
            _cameraWriterSuppressCount++;
            return;
        }
        orig_atel_camera_pos_set(worker, p2, stack, p4);
    }

    private int h_ms_atel_request_camera(int p1, int p2, int p3, int p4, int p5, int p6, int p7, int p8)
    {
        // p2 is the actor the camera script follows; 0xFF is the engine's "no actor" sentinel and
        // marks the battle state machine's own framing. One decision, one log line - see
        // camera_move_allowed.
        bool shouldSuppress = !camera_move_allowed(
            "MsAtelRequestCamera", worker: 0, followedActor: p2,
            detail: $"p1={p1:X},p3={p3:X},p4={p4:X}");

        if (shouldSuppress)
        {
            _enemyCameraLockSuppressCount++;
            if (_optionLogging)
            {
                log_debug(
                    $"[CameraLock] Suppressed MsAtelRequestCamera(p1={p1:X}, p2={p2:X}, p3={p3:X}) "
                    + $"(attacker={_runtime.CurrentAttackerId}, count={_enemyCameraLockSuppressCount}).");
            }

            // -1, not 0. MsAtelRequestCamera returns the id of the request it queues via
            // MsAtelRequestExe, and the engine's OWN suppression path — the guard on btl.debug._6_1_
            // (FFX.exe.c:841575) — falls straight through to `return 0xffffffff` (:841607). So -1 is
            // the sanctioned "I queued nothing" value that every caller already handles.
            //
            // 0 is a VALID request id. Returning it told callers a request existed, so a script that
            // waits on its scripted camera waited on a request that was never queued: a hang, not a
            // crash — exactly the freeze seen when an enemy starts a special move, broken only by Esc.
            //
            // To abort a request that HAS been queued, the engine provides MsAtelRequestCancel
            // (FFX.exe.c:841612): AtelSkipReqLevel2 + AtelExecReturn2, then
            // AtelDecodeSignal(req, 0xffffffff), which releases whoever waits on the signal.
            return -1;
        }

        return orig_ms_atel_request_camera(p1, p2, p3, p4, p5, p6, p7, p8);
    }

    /// <summary>
    ///     Active hook on MsAtelRequestMagicCamera (FFX.exe+0x398010). Sibling of
    ///     the MsAtelRequestCamera lock — same gating logic, separate camera path.
    ///     Magic spell camera changes (enemy spells like Fire/Thunder/Demi) route
    ///     through this function instead of MsAtelRequestCamera, so a parallel
    ///     hook is needed for the enemy-turn camera lock to cover both paths.
    ///
    ///     On suppression returns 0xFF — the engine's "no camera assigned"
    ///     sentinel (engine line 841782 default), which is the safe no-op value
    ///     for callers that store the returned camera-id.
    /// </summary>
    private byte h_ms_atel_request_magic_camera(int p1, int p2, uint p3, int p4, int p5, int p6, uint p7, int p8, int p9)
    {
        bool shouldSuppress = !camera_move_allowed(
            "MsAtelRequestMagicCamera", worker: 0, followedActor: p2,
            detail: $"p1={p1:X},p3={p3:X}");

        if (shouldSuppress)
        {
            _enemyMagicCameraLockSuppressCount++;
            if (_optionLogging)
            {
                log_debug(
                    $"[CameraLock] Suppressed MsAtelRequestMagicCamera(p1={p1:X}, p2={p2:X}, p3={p3:X}) "
                    + $"(attacker={_runtime.CurrentAttackerId}, count={_enemyMagicCameraLockSuppressCount}).");
            }
            return 0xFF;
        }

        return orig_ms_atel_request_magic_camera(p1, p2, p3, p4, p5, p6, p7, p8, p9);
    }

    /// <summary>
    ///     Active hook on MsBattleSpecialCameraPause (FFX.exe+0x39DDD0). The third
    ///     sibling of the Battle Camera Lock — covers the cinematic camera path
    ///     used by boss / overdrive-class enemy attacks (high-cue 0x40XX commands)
    ///     that bypass MsAtelRequestCamera and MsAtelRequestMagicCamera entirely.
    ///
    ///     Suppression is a bare <c>return</c> (no call to original). Engine-level
    ///     safety: btl._24_1_ stays 0, so the partner MsBattleSpecialCameraFree
    ///     becomes a no-op via its own early guard. No soft-lock risk.
    /// </summary>
    private void h_ms_battle_special_camera_pause(byte mode)
    {
        // No followed actor in the signature, so this cannot be classified as a flow camera here.
        bool shouldSuppress = !camera_move_allowed(
            "MsBattleSpecialCameraPause", worker: 0, followedActor: -1,
            detail: $"mode=0x{mode:X2}");

        if (shouldSuppress)
        {
            _battleSpecialCameraLockSuppressCount++;
            if (_optionLogging)
            {
                log_debug(
                    $"[CameraLock] Suppressed MsBattleSpecialCameraPause(mode=0x{mode:X2}) "
                    + $"(attacker={_runtime.CurrentAttackerId}, count={_battleSpecialCameraLockSuppressCount}).");
            }
            return;
        }

        orig_ms_battle_special_camera_pause(mode);
    }

    /// <summary>
    ///     Observe-only hook on MsEffectEndMotion (FFX.exe+0x387A10) — the engine's
    ///     "a battler's motion just finished" handler. Always calls the original, then
    ///     (when logging is on) logs how long ago we played a motion on that slot via
    ///     the FX/Motion lab or the parry block reaction. This measures the real motion
    ///     run length so we can decide whether to drive the parry window / whiff recovery
    ///     from the animation instead of the static FINAL_PARRY_SPEC durations.
    /// </summary>
    private void h_ms_effect_end_motion(uint chr_id, int mode)
    {
        orig_ms_effect_end_motion(chr_id, mode);

        if (!_optionLogging) return;
        uint slot = chr_id & 0xff;
        if (slot >= PartyActorCapacity) return;
        ulong startFrame = _motionPlayFrame[slot];
        if (startFrame == 0) return; // only report motions we played, so the log stays correlated
        _motionPlayFrame[slot] = 0;
        ulong frames = _debugFrameIndex - startFrame;
        double ms = frames * (1000.0 / BattleFrameRate);
        log_debug($"[MotionEnd] {format_actor_slot((byte)slot)} motion ended after {frames} frames (~{ms:F0} ms @ {BattleFrameRate:F0}fps, mode={mode}).");
    }

    /// <summary>
    ///     Active hook on MsDmgCalc_CheckHit (FFX.exe+0x38A950). The engine's
    ///     accuracy/evasion roll. Always invokes the original to preserve battle
    ///     RNG state, then conditionally overrides MISS → HIT when:
    ///       - the option is enabled,
    ///       - the target is a real PC (not monster, not aeon),
    ///       - and we've auto-cached the HIT enum integer value.
    ///
    ///     The hook also runs auto-discovery for the CheckHitResult enum integers
    ///     by observing returns. Logs every PC-target invocation when logging is on
    ///     so the user can manually verify HIT/MISS values from the session log.
    /// </summary>
    /// <summary>
    ///     MISS_ALIVE is returned by exactly one early-return in the engine
    ///     (`_DmgCalc_CheckHit`, decomp :830187): the command is revive-class
    ///     (`flags_misc` bit 23) and the target is neither zombie nor dead — a Phoenix
    ///     Down or Life aimed at a living character. Its caller (:833532) then `goto`s
    ///     past the entire damage and status chain.
    ///
    ///     It is therefore **not** an evade, and must never be flipped to HIT: doing so
    ///     would land a revive on a healthy party member. It has nothing to do with
    ///     status resistance either — petrify and friends are decided later, in
    ///     `_DmgCalc_InflictStatus` against `status_resists`.
    /// </summary>
    private const uint CommandFlagsMiscReviveClass = 0x800000;

    private int h_ms_dmg_calc_check_hit(Chr* user, Chr* target, Command* command, void* info, int counter)
    {
        int result = orig_ms_dmg_calc_check_hit(user, target, command, info, counter);

        if (target == null) return result;  // defensive — never observed but cheap

        // Chr has TWO id fields:
        //   - id     at 0xC: engine slot index (0..0x1B), what MsGetRamChrMonster operates on.
        //   - chr_id at 0xE: character/monster *template* id (e.g. 0x10CF = Cactuar).
        // Slot is what gates monster/PC discrimination; template is informational only.
        ushort targetSlot = target->id;
        ushort targetTemplate = target->chr_id;
        bool isMonster = (uint)(targetSlot - 0x14u) < 8u;   // matches MsGetRamChrMonster
        bool isAeon = target->ram.is_aeon;
        bool isRealPC = !isMonster && !isAeon;

        if (!isRealPC) return result;  // monsters & aeons keep vanilla evade

        _checkHitObservationCount++;
        if (_optionLogging)
        {
            ushort userSlot = user != null ? user->id : (ushort)0;
            ushort userTemplate = user != null ? user->chr_id : (ushort)0;
            log_debug($"[CheckHit] user_slot={userSlot:X2} user_tpl={userTemplate:X4} target_slot={targetSlot:X2} target_tpl={targetTemplate:X4} is_aeon={isAeon} result={result} (obs#{_checkHitObservationCount}, hit={CheckHitHit})");
        }

        // Native PC evasion stays disabled: a PC that evades natively never reaches our impact
        // path, so the player's parry/dodge is the only way to avoid a hit.
        if (result == CheckHitHit) return result;

        // Do NOT treat every non-HIT value as an evasion roll. The enum has three members, and
        // MISS_ALIVE is identifiable structurally rather than by value: it is returned iff the
        // command is revive-class. Everything else that is not HIT is a genuine evasion roll.
        //
        // This used to be gated on a value discovered by observation, which cached whichever
        // non-HIT value it saw first - on one profile MISS_ALIVE (2), after which the real MISS (1)
        // was never overridden and PCs evaded on their own.
        if (command != null && (command->flags_misc & CommandFlagsMiscReviveClass) != 0) return result;

        _checkHitOverrideCount++;
        if (_optionLogging)
        {
            log_debug($"[CheckHit] Overrode MISS ({result}) → HIT for PC target_slot={targetSlot:X2} target_tpl={targetTemplate:X4} (override#{_checkHitOverrideCount}).");
        }
        return CheckHitHit;
    }

}
