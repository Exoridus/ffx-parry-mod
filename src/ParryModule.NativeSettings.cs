namespace Fahrenheit.Mods.Parry;

/// <summary>
///     The mod's settings, held by Fahrenheit rather than by our own JSON.
///
///     <para>
///     Every value is exposed as a property with the name the rest of the module already used, so
///     the read sites are unchanged. They are read-only on purpose and not by preference:
///     <c>FhSetting&lt;T&gt;.set</c> is <c>internal</c>, so a mod can read a setting and never write
///     one. That single fact decides three things below - the enums are numbers rather than a
///     radio group, the legacy camera-lock migration can only be interpreted on read, and
///     CheckHitHitValue cannot live here at all.
///     </para>
///
///     <para>
///     Global rather than <c>settings_local</c>: none of this belongs to a save file. Difficulty in
///     particular has to be changeable from the main menu, before any save is loaded.
///     </para>
/// </summary>
public unsafe sealed partial class ParryModule
{
    // ── Gameplay ──────────────────────────────────────────────────────────────

    private readonly FhSettingToggle _setEnabled      = new("fhparry.enabled",      true);
    private readonly FhSettingToggle _setDodgeEnabled = new("fhparry.dodge",        true);

    private readonly FhSettingNumber<int> _setDifficulty =
#if DEBUG
        new("fhparry.difficulty", (int)ParryDifficultyModel.DefaultDifficulty, 0, 3, 1);
#else
        new("fhparry.difficulty", (int)ParryDifficultyModel.DefaultDifficulty, 0, 2, 1);
#endif

    // ── Battle camera ─────────────────────────────────────────────────────────
    //
    // Two independent axes rather than one scope, and phrased as permission rather than as a lock:
    // ON is what the game does on its own, OFF holds the camera. That makes the label readable
    // without a tooltip, which the old 0/1/2 never was.
    //
    // Both default OFF. Cinematic argues for letting the player's own attacks pan, but a player
    // camera can still be settling at the end of a turn while a fast enemy attack - one with no
    // cast to speak of - is already running, and a parry you cannot see is a parry you cannot make.
    // For a mod about seeing the attack, playable beats cinematic.
    //
    // The four combinations are all reachable, because the two suppression sites are independent:
    // the request lock gated on an active enemy turn, and the writer stop that catches the player's
    // own turn where AwaitingTurnEnd is false. Monster ON with Character OFF is a state nobody has
    // observed yet - it is mechanically sound and untested, not tested.

    private readonly FhSettingToggle _setMonsterCamera   = new("fhparry.camera.monster",   false);
    private readonly FhSettingToggle _setCharacterCamera = new("fhparry.camera.character", false);

#if DEBUG
    /// <summary>
    ///     The battle state machine's own framing cameras, identified by a followed actor of 0xFF:
    ///     MsBtlMain issues 0x18 at battle start and 0x75, 0x76 and 0x7c while the encounter loads,
    ///     and state 0x1d picks 0x42 to 0x45 off btl.battle_end_type for the ending. ON, and only a
    ///     diagnostic turns them off - suppressing the game's own flow was never the intent.
    ///
    ///     Distinct from the cutscene gate above: 0xFF catches the flow cameras and nothing else.
    /// </summary>
    private readonly FhSettingToggle _setFlowCamera = new("fhparry.camera.flow", true);
#endif

    // ── Feedback ──────────────────────────────────────────────────────────────

    private readonly FhSettingToggle _setSound         = new("fhparry.audio",          true);
    private readonly FhSettingToggle _setParryEffect   = new("fhparry.parry_effect",   true);
    private readonly FhSettingToggle _setImpactShake   = new("fhparry.impact_shake",   true);
    private readonly FhSettingToggle _setStreakCounter = new("fhparry.streak_counter", false);

    // ── Diagnostics ───────────────────────────────────────────────────────────

    private readonly FhSettingToggle _setLogging =
#if DEBUG
        new("fhparry.logging", true);
#else
        new("fhparry.logging", false);
#endif

#if DEBUG
    private readonly FhSettingToggle _setDebugOverlay = new("fhparry.debug_overlay", true);
    private readonly FhSettingToggle _setCameraProbe = new("fhparry.camera_probe", true);
    private readonly FhSettingToggle _setNativeProbeLogging = new("fhparry.native_probe_logging", false);
    private readonly FhSettingToggle _setParryNativeBlock = new("fhparry.parry_native_block", false);
#endif

    private sealed class Settings(ParryModule module) : FhSettingProvider<ParryModule>
    {
        protected override IEnumerable<FhSetting> get()
            => [
            module._setEnabled,
            module._setDifficulty,
            module._setDodgeEnabled,
            module._setMonsterCamera,
            module._setCharacterCamera,
#if DEBUG
            module._setFlowCamera,
#endif
            module._setSound,
            module._setParryEffect,
            module._setImpactShake,
            module._setStreakCounter,
            module._setLogging,
#if DEBUG
            module._setDebugOverlay,
            module._setCameraProbe,
            module._setNativeProbeLogging,
            module._setParryNativeBlock,
#endif
            ];
    }

    // ── The names the rest of the module reads ────────────────────────────────

    private bool _optionEnabled            => _setEnabled.get();
    private bool _optionDodgeEnabled       => _setDodgeEnabled.get();
    private bool _optionSound              => _setSound.get();
    private bool _optionParryEffect        => _setParryEffect.get();
    private bool _optionImpactShake        => _setImpactShake.get();
    private bool _optionStreakCounter      => _setStreakCounter.get();
    private bool _optionLogging            => _setLogging.get();
#if DEBUG
    private bool _optionDebugOverlay       => _setDebugOverlay.get();
    private bool _optionCameraProbe        => _setCameraProbe.get();
    private bool _optionNativeProbeLogging => _setNativeProbeLogging.get();
    private bool _optionParryNativeBlock   => _setParryNativeBlock.get();
#else
    private bool _optionDebugOverlay       => false;
    private bool _optionCameraProbe        => false;
    private bool _optionNativeProbeLogging => false;
    private bool _optionParryNativeBlock   => false;
#endif

    private ParryDifficulty _optionDifficulty
        => ParryDifficultyModel.NormalizeDifficulty((ParryDifficulty)_setDifficulty.get());

    private bool _optionMonsterCameraAllowed   => _setMonsterCamera.get();
    private bool _optionCharacterCameraAllowed => _setCharacterCamera.get();

#if DEBUG
    private bool _optionFlowCameraAllowed => _setFlowCamera.get();
#else
    private const bool _optionFlowCameraAllowed = true;
#endif
}
