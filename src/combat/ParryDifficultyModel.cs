namespace Fahrenheit.Mods.Parry;

public enum ParryDifficulty
{
    Easy = 0,
    Normal = 1,
    Expert = 2,
#if DEBUG
    Debug = 3,
#endif
}

/// <summary>
///     Difficulty presets for the press-based parry window and its animation-backed
///     whiff recovery lockout.
///
///     The mod no longer uses a tiered anti-spam window; a single window duration is
///     applied on every fresh press, and a whiff that does not connect enters a
///     time-bounded recovery lockout that approximates the visible native
///     "return to guard stance" animation.
///
///     <para>
///         Everything is counted in <b>battle ticks</b>; milliseconds are derived, never stored.
///         Both endpoints of a parry are tick-locked. The press is read from the game's own input
///         word, which the game refreshes once per tick, so a press at 0.6 of a frame becomes
///         visible on the next one — the 0.6 never exists. The impact arrives from
///         MsSetDamageInternal, also on a tick. The only continuous quantity in the old model was
///         the real elapsed time subtracted from the window each frame, and that injected
///         wall-clock drift into a tick-based simulation: one dropped frame burns two ticks of
///         window while the enemy animation and the impact stall with it.
///     </para>
///     <para>
///         A window of W ms admits exactly <c>floor(W / 33.33) + 1</c> impacts, and the fraction
///         beyond that does nothing — except when W lands exactly on a tick, where float residue
///         and frame pacing decide. Normal's old 200 ms was exactly 6.0 ticks. Counting ticks
///         removes the coin flip.
///     </para>
///     <para>
///         <b>Difficulty no longer moves the window.</b> At 30 Hz a tighter window punishes
///         perception and hardware rather than skill. Difficulty stays on the windows — the tiers
///         below reproduce the values the mod shipped with, which played well — but the arithmetic
///         is now honest, and two accidents of the old model are gone. Debug is not a difficulty;
///         it is a testing aid.
///     </para>
///     <para>
///         <b>Accident one:</b> the dodge window had no tiers at all. It was two constants,
///         <c>350 ms</c> for release and <c>800 ms</c> for DEBUG, selected by build configuration
///         rather than by difficulty.
///     </para>
///     <para>
///         <b>Accident two:</b> Expert paid twice — the tightest window *and* the longest recovery
///         (750 ms against Normal's 600). Its lockout is now 500 ms. Total commitment is no longer
///         the authored number; with per-tier windows the lockout is what you tune, and the
///         commitment is what falls out of it.
///     </para>
/// </summary>
public static class ParryDifficultyModel
{
    /// <summary>
    ///     The rate the mod's own update actually runs at, in ticks per second.
    ///
    ///     <para>
    ///     Not a constant, and that is the point. Fahrenheit raises PreUpdate from a hook on
    ///     <c>Sg_MainLoop</c>, so the mod samples input once per iteration of the game's main loop,
    ///     and that loop runs twice as often at 60 Hz. A window authored as six ticks would close
    ///     after 100 ms there instead of 200, and Easy would fall from 367 ms to 183 without anyone
    ///     touching a setting.
    ///     </para>
    ///
    ///     <para>
    ///     So the tiers below are authored in milliseconds and converted against this rate. That
    ///     keeps both properties that matter: the duration is the same at 30, 60 and 120 Hz, and both
    ///     endpoints of a parry still sit on one clock, which is what the tick model was for.
    ///     Measuring the window in real time would have the first and lose the second - one dropped
    ///     frame would burn window while the enemy animation and the impact stalled with it.
    ///     </para>
    /// </summary>
    public static float ObservedTicksPerSecond { get; private set; } = NominalTicksPerSecond;

    /// <summary>The rate the tiers were authored against, and the fallback before anything is observed.</summary>
    public const int NominalTicksPerSecond = 30;

    /// <summary>
    ///     Feeds one update's delta in. Smoothed hard and clamped: a single long frame is not a rate
    ///     change, and a loading stall must not retime a window that is already open. The frameskip
    ///     loop calls Sg_MainLoop more than once per presented frame while catching up, so individual
    ///     deltas are not uniform even at a steady framerate.
    /// </summary>
    public static void ObserveTickDelta(float deltaSeconds)
    {
        if (deltaSeconds <= 0.0005f || deltaSeconds > 0.5f) return;

        float instant = 1f / deltaSeconds;
        if (instant < 15f || instant > 400f) return;

        ObservedTicksPerSecond += (instant - ObservedTicksPerSecond) * 0.02f;
    }

    /// <summary>
    ///     Milliseconds to ticks at the observed rate. Any positive duration is at least one tick: a
    ///     window that rounded to zero would reject an impact arriving on the very tick of the
    ///     press, which is the most common way a parry lands. Zero stays zero, which is how a
    ///     cooldown is switched off.
    /// </summary>
    public static int MsToTicks(int milliseconds)
        => milliseconds <= 0 ? 0 : Math.Max(1, (int)MathF.Round(milliseconds / 1000f * ObservedTicksPerSecond));

    // Parry window. The old millisecond values closed after ceil(ms / 33.33) ticks, so Easy's
    // 350 ms really bought eleven ticks and Expert's 150 ms bought five — not ten and a half,
    // and not four and a half. These reproduce that exactly.
    private const int DebugParryMs  = 500;
    private const int EasyParryMs   = 367;   // 11 ticks at 30 Hz; authored 350
    private const int NormalParryMs = 200;
    private const int ExpertParryMs = 167;   // 5 ticks at 30 Hz; authored 150

    // Dodge window: wider than the parry window on every tier — safer, but it grants neither the
    // counter nor the overdrive charge. Easy keeps the old flat 350 ms; the tiers below it are new.
    private const int DebugDodgeMs  = 800;
    private const int EasyDodgeMs   = 367;
    private const int NormalDodgeMs = 300;
    private const int ExpertDodgeMs = 233;

    // Whiff recovery: the commitment a press that hits nothing costs. Authored per tier.
    private const int DebugLockoutMs  = 300;
    private const int EasyLockoutMs   = 467;  // 14 ticks at 30 Hz; authored 450
    private const int NormalLockoutMs = 600;
    private const int ExpertLockoutMs = 500;  // was 767, the double punishment

    // Cooldown between two dodges. Paces multi-press without automating the timing. This existed
    // as `dodge_whiffout`, defaulted to 0, and was therefore inert from the day it was written.
    private const int DebugDodgeCooldownMs  = 0;    // off - testing aid
    private const int EasyDodgeCooldownMs   = 300;
    private const int NormalDodgeCooldownMs = 400;
    private const int ExpertDodgeCooldownMs = 500;


#if DEBUG
    private static readonly ParryDifficulty[] SelectableDifficulties =
    [
        ParryDifficulty.Easy,
        ParryDifficulty.Normal,
        ParryDifficulty.Expert,
        ParryDifficulty.Debug
    ];
    private const string DifficultyComboItems = "Easy\0Normal\0Expert\0Debug\0";
#else
    private static readonly ParryDifficulty[] SelectableDifficulties =
    [
        ParryDifficulty.Easy,
        ParryDifficulty.Normal,
        ParryDifficulty.Expert
    ];
    private const string DifficultyComboItems = "Easy\0Normal\0Expert\0";
#endif

    public static ParryDifficulty DefaultDifficulty =>
#if DEBUG
        ParryDifficulty.Debug;
#else
        ParryDifficulty.Normal;
#endif

    public static string FormatName(ParryDifficulty difficulty) => difficulty switch
    {
#if DEBUG
        ParryDifficulty.Debug => "Debug",
#endif
        ParryDifficulty.Easy => "Easy",
        ParryDifficulty.Normal => "Normal",
        ParryDifficulty.Expert => "Expert",
        _ => FormatName(DefaultDifficulty)
    };

    /// <summary>Parry window, in battle ticks. The window admits impacts on ticks 0..N-1 after the press.</summary>
    public static int GetParryWindowTicks(ParryDifficulty difficulty) => difficulty switch
    {
#if DEBUG
        ParryDifficulty.Debug => MsToTicks(DebugParryMs),
#endif
        ParryDifficulty.Easy => MsToTicks(EasyParryMs),
        ParryDifficulty.Normal => MsToTicks(NormalParryMs),
        ParryDifficulty.Expert => MsToTicks(ExpertParryMs),
        _ => GetParryWindowTicks(DefaultDifficulty)
    };

    /// <summary>Dodge window, in battle ticks. Wider than the parry window: safer, but it grants neither counter nor overdrive.</summary>
    public static int GetDodgeWindowTicks(ParryDifficulty difficulty) => difficulty switch
    {
#if DEBUG
        ParryDifficulty.Debug => MsToTicks(DebugDodgeMs),
#endif
        ParryDifficulty.Easy => MsToTicks(EasyDodgeMs),
        ParryDifficulty.Normal => MsToTicks(NormalDodgeMs),
        ParryDifficulty.Expert => MsToTicks(ExpertDodgeMs),
        _ => GetDodgeWindowTicks(DefaultDifficulty)
    };

    /// <summary>
    ///     Whiff recovery lockout, in battle ticks — the time a press that hits nothing commits
    ///     the player to before another is accepted. Authored per tier.
    /// </summary>
    public static int GetWhiffLockoutTicks(ParryDifficulty difficulty) => difficulty switch
    {
#if DEBUG
        ParryDifficulty.Debug => MsToTicks(DebugLockoutMs),
#endif
        ParryDifficulty.Easy => MsToTicks(EasyLockoutMs),
        ParryDifficulty.Normal => MsToTicks(NormalLockoutMs),
        ParryDifficulty.Expert => MsToTicks(ExpertLockoutMs),
        _ => GetWhiffLockoutTicks(DefaultDifficulty)
    };

    /// <summary>
    ///     Cooldown between two dodges, in battle ticks. Paces multi-press. Zero on Debug.
    /// </summary>
    public static int GetDodgeCooldownTicks(ParryDifficulty difficulty) => difficulty switch
    {
#if DEBUG
        ParryDifficulty.Debug => MsToTicks(DebugDodgeCooldownMs),
#endif
        ParryDifficulty.Easy => MsToTicks(EasyDodgeCooldownMs),
        ParryDifficulty.Normal => MsToTicks(NormalDodgeCooldownMs),
        ParryDifficulty.Expert => MsToTicks(ExpertDodgeCooldownMs),
        _ => GetDodgeCooldownTicks(DefaultDifficulty)
    };

    /// <summary>
    ///     Total commitment from the press: the parry window plus the recovery that follows a
    ///     whiff. Derived, for display — the lockout is what you tune.
    /// </summary>
    public static int GetTotalCommitmentTicks(ParryDifficulty difficulty)
        => GetParryWindowTicks(difficulty) + GetWhiffLockoutTicks(difficulty);

    /// <summary>Nominal wall-clock duration of a tick count. For display only — never for a gameplay decision.</summary>
    public static float TicksToSeconds(int ticks) => ticks / ObservedTicksPerSecond;

    /// <summary>Nominal wall-clock duration of a tick count, in milliseconds. Display only.</summary>
    public static float TicksToMs(int ticks) => ticks * (1000f / ObservedTicksPerSecond);

    public static float GetWindowSeconds(ParryDifficulty difficulty)
        => TicksToSeconds(GetParryWindowTicks(difficulty));

    public static float GetDodgeWindowSeconds(ParryDifficulty difficulty)
        => TicksToSeconds(GetDodgeWindowTicks(difficulty));

    public static float GetWhiffLockoutSeconds(ParryDifficulty difficulty)
        => TicksToSeconds(GetWhiffLockoutTicks(difficulty));

    public static ReadOnlySpan<ParryDifficulty> GetSelectableDifficulties()
    {
        return SelectableDifficulties;
    }

    public static string GetComboItems()
    {
        return DifficultyComboItems;
    }

    public static int GetComboIndex(ParryDifficulty difficulty)
    {
        ParryDifficulty normalized = NormalizeDifficulty(difficulty);
        for (int i = 0; i < SelectableDifficulties.Length; i++)
        {
            if (SelectableDifficulties[i] == normalized)
            {
                return i;
            }
        }

        return 0;
    }

    public static ParryDifficulty DifficultyFromComboIndex(int index)
    {
        if (index < 0 || index >= SelectableDifficulties.Length)
        {
            return DefaultDifficulty;
        }

        return SelectableDifficulties[index];
    }

    public static ParryDifficulty NormalizeDifficulty(ParryDifficulty difficulty)
    {
        return difficulty switch
        {
#if DEBUG
            ParryDifficulty.Debug => ParryDifficulty.Debug,
#endif
            ParryDifficulty.Easy => ParryDifficulty.Easy,
            ParryDifficulty.Normal => ParryDifficulty.Normal,
            ParryDifficulty.Expert => ParryDifficulty.Expert,
            _ => DefaultDifficulty
        };
    }

    public static bool TryParsePersistedDifficulty(string? persistedValue, out ParryDifficulty difficulty)
    {
        difficulty = DefaultDifficulty;
        if (string.IsNullOrWhiteSpace(persistedValue))
        {
            return false;
        }

#if !DEBUG
        if (persistedValue.Equals("Debug", StringComparison.OrdinalIgnoreCase))
        {
            difficulty = ParryDifficulty.Normal;
            return true;
        }
#endif

        if (!Enum.TryParse(persistedValue, ignoreCase: true, out ParryDifficulty parsed))
        {
            return false;
        }

        difficulty = NormalizeDifficulty(parsed);
        return true;
    }

}
