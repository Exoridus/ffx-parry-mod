# Difficulty timings, as shipped

The authored values behind `ParryDifficultyModel`, recorded before any settings rework so the
calibration survives a change to how it is stored or exposed. Everything is counted in **battle
ticks**; the millisecond column is derived at 30 ticks per second and never stored.

## Settings values

F7 uses stable numeric IDs: `0 = Easy`, `1 = Normal`, `2 = Expert` in both Debug and Release. Debug additionally offers `3 = Debug`, its default testing preset. Release defaults to Normal and treats the unsupported Debug value as Normal. The existing released values and setting key are unchanged.

Older Debug configurations used `0 = Debug`, `1 = Easy`, `2 = Normal`, `3 = Expert` without recording the originating build. Select the intended difficulty once when reusing such a file. Those ambiguous values are not automatically remapped, because doing so would reinterpret valid Release configurations.

The diagnostic overlay, camera-call probe, native probe logging and experimental native block reaction are available only in Debug. Release ignores saved values for these diagnostic options. Session logging remains available in both builds. Enabling native probe installation requires a restart.

## The four quantities

| Quantity | What it is |
| --- | --- |
| Parry window | How long after a fresh press an impact still counts as a parry. |
| Dodge window | The same for a dodge. Wider on every tier - safer, but grants neither the counter nor the overdrive charge. |
| Whiff recovery | The commitment a press that hits nothing costs. Approximates the visible native return-to-guard animation. |
| Dodge cooldown | Minimum gap between two dodges. Paces multi-press without automating the timing. |

## The tiers

| Tier | Parry | Dodge | Whiff recovery | Dodge cooldown |
| --- | --- | --- | --- | --- |
| Easy | 11 ticks (367 ms) | 11 (367 ms) | 14 (467 ms) | 9 (300 ms) |
| Normal | 6 (200 ms) | 9 (300 ms) | 18 (600 ms) | 12 (400 ms) |
| Expert | 5 (167 ms) | 7 (233 ms) | 15 (500 ms) | 15 (500 ms) |
| Debug | 15 (500 ms) | 24 (800 ms) | 9 (300 ms) | 0 (off) |

Debug is not a difficulty. It is a testing aid, which is what the zero cooldown is for, and it is
`#if DEBUG` only - the release enum is Easy / Normal / Expert with Normal as the default.

## Why these numbers and not round milliseconds

The tick counts reproduce what the mod shipped with in milliseconds, rounded the way the old code
actually behaved rather than the way it read. A window of W ms closed after `ceil(W / 33.33)` ticks,
so Easy's authored 350 ms really bought eleven ticks and Expert's 150 ms bought five - not ten and a
half, and not four and a half. The tick column is therefore the primary value and the millisecond
column is a courtesy.

Two accidents of the pre-tick model are gone and should not come back:

- **The dodge window had no tiers at all.** It was two constants, 350 ms for release and 800 ms for
  DEBUG, selected by build configuration rather than by difficulty.
- **Expert paid twice**, the tightest window *and* the longest recovery, 750 ms against Normal's
  600. Its recovery is now 500 ms. With per-tier windows the recovery is what gets tuned and the
  total commitment is what falls out of it.

## Why the model is tick-based

Both endpoints of a parry are quantised by the engine, not by this mod:

- The **press** is read from the game's own input word, which the game refreshes once per tick. A
  press at 0.6 of a tick becomes visible on the next one; the 0.6 never exists.
- The **impact** arrives from `MsSetDamageInternal`, also on a tick.

So a window measured in real time does not buy finer resolution - it only decides the boundary by
frame pacing. The old model subtracted elapsed wall-clock time from the window each frame, which
injected drift into a tick-based simulation: one dropped frame burned two ticks of window while the
enemy animation and the impact stalled with it. Counting ticks removed a coin flip rather than a
feature.

Difficulty deliberately does **not** move the window much: at 30 Hz a tighter window punishes
perception and hardware rather than skill.

## The open question against the 60 FPS mod

`TicksPerSecond` is a hardcoded 30. That is correct only while the press and the impact both land on
a 30 Hz clock. `docs/forensics/60fps-retiming.md` in the knowledge base establishes that the engine
carries clocks at different observed rates - `sg_count` at 30/s against `sg_vcount` at 60/s, with
`yiGetFCount() = sg_count * 2` - so which one the input word and `MsSetDamageInternal` sit on decides
whether a tick is 33.3 ms or 16.7 ms while that mod runs.

If it turns out to be 60, the fix is to read the live rate instead of hardcoding it, **not** to move
the model back to milliseconds: the endpoints stay quantised either way, and a rate-aware tick count
keeps the determinism that the millisecond model did not have. It is a probe question, not an
analysis one.
