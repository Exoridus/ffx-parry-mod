using Fahrenheit.Mods.Parry;
using Xunit;

namespace Fahrenheit.Mods.Parry.Tests;

public sealed class ParryDifficultyModelTests
{
    // Windows are tiered again. These reproduce the millisecond values the mod shipped with:
    // the old code closed after ceil(ms / 33.33) ticks, so Easy's 350 ms bought eleven ticks
    // and Expert's 150 ms bought five.
    [Theory]
    [InlineData(ParryDifficulty.Easy,   11)]
    [InlineData(ParryDifficulty.Normal, 6)]
    [InlineData(ParryDifficulty.Expert, 5)]
#if DEBUG
    [InlineData(ParryDifficulty.Debug,  15)]
#endif
    public void GetParryWindowTicks_IsTieredAndTightensWithDifficulty(ParryDifficulty difficulty, int expectedTicks)
    {
        Assert.Equal(expectedTicks, ParryDifficultyModel.GetParryWindowTicks(difficulty));
    }

    [Theory]
    [InlineData(ParryDifficulty.Easy,   11)]
    [InlineData(ParryDifficulty.Normal, 9)]
    [InlineData(ParryDifficulty.Expert, 7)]
#if DEBUG
    [InlineData(ParryDifficulty.Debug,  24)]
#endif
    public void GetDodgeWindowTicks_IsAtLeastAsWideAsTheParryWindow(ParryDifficulty difficulty, int expectedTicks)
    {
        Assert.Equal(expectedTicks, ParryDifficultyModel.GetDodgeWindowTicks(difficulty));
        Assert.True(ParryDifficultyModel.GetDodgeWindowTicks(difficulty)
                 >= ParryDifficultyModel.GetParryWindowTicks(difficulty),
                    "the dodge is the safer option; its window may never be the tighter one");
    }

    [Theory]
    [InlineData(ParryDifficulty.Easy,   14)]
    [InlineData(ParryDifficulty.Normal, 18)]
    [InlineData(ParryDifficulty.Expert, 15)]
#if DEBUG
    [InlineData(ParryDifficulty.Debug,  9)]
#endif
    public void GetWhiffLockoutTicks_IsAuthoredPerTier(ParryDifficulty difficulty, int expectedTicks)
    {
        Assert.Equal(expectedTicks, ParryDifficultyModel.GetWhiffLockoutTicks(difficulty));
        Assert.True(ParryDifficultyModel.GetWhiffLockoutTicks(difficulty) > 0, "a whiff must cost something");
    }

    // Expert used to pay twice: the tightest window AND the longest recovery (767 ms against
    // Normal's 600). Guard against that returning.
    [Theory]
    [InlineData(0, ParryDifficulty.Easy)]
    [InlineData(1, ParryDifficulty.Normal)]
    [InlineData(2, ParryDifficulty.Expert)]
    public void PersistedValuesRetainMeaningAcrossBuildConfigurations(int value, ParryDifficulty expected)
    {
        Assert.Equal(value, (int)expected);
        Assert.Equal(expected, ParryDifficultyModel.NormalizeDifficulty((ParryDifficulty)value));
    }

    [Fact]
    public void DebugValueIsDistinctFromReleasedDifficulties()
    {
#if DEBUG
        Assert.Equal(3, (int)ParryDifficulty.Debug);
#else
        Assert.Equal(ParryDifficulty.Normal, ParryDifficultyModel.NormalizeDifficulty((ParryDifficulty)3));
#endif
    }

    [Fact]
    public void Expert_DoesNotPayTwice()
    {
        Assert.True(ParryDifficultyModel.GetWhiffLockoutTicks(ParryDifficulty.Expert)
                  < ParryDifficultyModel.GetWhiffLockoutTicks(ParryDifficulty.Normal),
                    "Expert already has the tightest window; it must not also carry the longest recovery");
    }

    [Theory]
    [InlineData(ParryDifficulty.Easy,   9)]
    [InlineData(ParryDifficulty.Normal, 12)]
    [InlineData(ParryDifficulty.Expert, 15)]
#if DEBUG
    [InlineData(ParryDifficulty.Debug,  0)]
#endif
    public void GetDodgeCooldownTicks_PacesMultiPress(ParryDifficulty difficulty, int expectedTicks)
    {
        Assert.Equal(expectedTicks, ParryDifficultyModel.GetDodgeCooldownTicks(difficulty));
    }

    [Theory]
    [InlineData(ParryDifficulty.Easy)]
    [InlineData(ParryDifficulty.Normal)]
    [InlineData(ParryDifficulty.Expert)]
    public void TotalCommitment_IsWindowPlusLockout(ParryDifficulty difficulty)
    {
        Assert.Equal(
            ParryDifficultyModel.GetParryWindowTicks(difficulty) + ParryDifficultyModel.GetWhiffLockoutTicks(difficulty),
            ParryDifficultyModel.GetTotalCommitmentTicks(difficulty));
    }

    // Every window must end BETWEEN two ticks, never on one: a boundary value is decided by
    // float residue and frame pacing. The tiers are authored in milliseconds, but a window is
    // the whole number of ticks that comes out of MsToTicks, and every derived duration must be
    // computed from that tick count at the observed rate, never from the millisecond constant.
    [Theory]
    [InlineData(ParryDifficulty.Easy)]
    [InlineData(ParryDifficulty.Normal)]
    [InlineData(ParryDifficulty.Expert)]
    public void DerivedSeconds_AreExactTickMultiples(ParryDifficulty difficulty)
    {
        float tick = 1f / ParryDifficultyModel.ObservedTicksPerSecond;

        Assert.Equal(ParryDifficultyModel.GetParryWindowTicks(difficulty) * tick,
                     ParryDifficultyModel.GetWindowSeconds(difficulty), precision: 5);
        Assert.Equal(ParryDifficultyModel.GetDodgeWindowTicks(difficulty) * tick,
                     ParryDifficultyModel.GetDodgeWindowSeconds(difficulty), precision: 5);
        Assert.Equal(ParryDifficultyModel.GetWhiffLockoutTicks(difficulty) * tick,
                     ParryDifficultyModel.GetWhiffLockoutSeconds(difficulty), precision: 5);
    }

    // The mod samples input once per Sg_MainLoop iteration, so the tick rate follows the
    // framerate. A duration authored in milliseconds has to buy twice the ticks at 60 Hz and
    // four times at 120, rounded to the nearest tick and never below one, or Easy's 367 ms
    // would silently shrink to 183 on a 60 Hz display.
    [Theory]
    [InlineData(200, 30f,  6)]
    [InlineData(200, 60f,  12)]
    [InlineData(200, 120f, 24)]
    [InlineData(367, 30f,  11)]
    [InlineData(367, 60f,  22)]
    [InlineData(367, 120f, 44)]
    [InlineData(167, 30f,  5)]
    [InlineData(167, 60f,  10)]
    [InlineData(167, 120f, 20)]
    [InlineData(1,   30f,  1)]
    [InlineData(1,   120f, 1)]
    [InlineData(0,   30f,  0)]
    [InlineData(0,   120f, 0)]
    public void MsToTicks_ScalesWithTheObservedRate(int milliseconds, float ticksPerSecond, int expectedTicks)
    {
        try
        {
            ConvergeObservedRate(ticksPerSecond);
            Assert.Equal(expectedTicks, ParryDifficultyModel.MsToTicks(milliseconds));
        }
        finally
        {
            ConvergeObservedRate(ParryDifficultyModel.NominalTicksPerSecond);
        }
    }

    // The observed rate only moves through the smoothed delta feed, which is the point of it: a
    // single long frame is not a rate change. Feeding a steady delta long enough converges it to
    // within float noise of the target, which the assertion below pins down so a failing case
    // reads as a conversion error and not as an unconverged rate.
    private static void ConvergeObservedRate(float ticksPerSecond)
    {
        float delta = 1f / ticksPerSecond;
        for (int i = 0; i < 4000; i++)
        {
            ParryDifficultyModel.ObserveTickDelta(delta);
        }

        Assert.Equal(ticksPerSecond, ParryDifficultyModel.ObservedTicksPerSecond, precision: 2);
    }


    [Fact]
    public void DefaultDifficulty_ShouldMatchBuildConfiguration()
    {
#if DEBUG
        Assert.Equal(ParryDifficulty.Debug, ParryDifficultyModel.DefaultDifficulty);
#else
        Assert.Equal(ParryDifficulty.Normal, ParryDifficultyModel.DefaultDifficulty);
#endif
    }

    [Fact]
    public void SelectableDifficulties_ShouldMatchBuildConfiguration()
    {
        ParryDifficulty[] selectable = ParryDifficultyModel.GetSelectableDifficulties().ToArray();

#if DEBUG
        Assert.Equal(
            new[] { ParryDifficulty.Easy, ParryDifficulty.Normal, ParryDifficulty.Expert, ParryDifficulty.Debug },
            selectable);
#else
        Assert.Equal(
            new[] { ParryDifficulty.Easy, ParryDifficulty.Normal, ParryDifficulty.Expert },
            selectable);
#endif
    }

    [Fact]
    public void TryParsePersistedDifficulty_ShouldHandleLegacyDebugSelection()
    {
        bool parsed = ParryDifficultyModel.TryParsePersistedDifficulty("Debug", out ParryDifficulty difficulty);
        Assert.True(parsed);
#if DEBUG
        Assert.Equal(ParryDifficulty.Debug, difficulty);
#else
        Assert.Equal(ParryDifficulty.Normal, difficulty);
#endif
    }
}
