# Pointers, Offsets, Hooks, and Runtime Events (FFX/Fahrenheit Guide)

This guide explains how pointer/offset based modding works in this project and in Fahrenheit-based FFX/FFX-2 mods.

Audience:
- You are comfortable with software engineering (for example web/backend/frontend),
- but you are new to C#, native memory, pointer arithmetic, and runtime hooks.

Scope:
- Windows x86 process model used by Fahrenheit and this mod.
- How offsets and hooks are represented in this repo.
- How to decide whether to use offsets, hooks, or events.

## 1) Core Terms

## Pointer
A pointer is a memory address that points to data (or code) in the running process.

```csharp
unsafe {
    int value = 42;
    int* p = &value;      // p points to value
    int read = *p;        // dereference pointer
}
```

## Offset
An offset is a relative position from a base address.

Example: `0x00D2A8E0` means "base of module + this many bytes".

## Module Base
The base address where `FFX.exe` is loaded in memory at runtime.

Absolute address formula:

```text
absolute = moduleBase + offset
```

## Hook
A hook intercepts a function call so your code runs before/after/instead of original behavior.

In this repo, hooks are created with `FhMethodHandle<TDelegate>`.

## Runtime Event
An internal mod event emitted from hooks/observers, then consumed by state machines or debug UI.

This is cleaner than letting every system read raw pointers directly.

## 2) How Fahrenheit Represents Native Function Addresses

Fahrenheit exposes many known function addresses in generated call maps (`FhFfx.FhCall.__addr_*`).

Typical usage in this mod:

```csharp
_hMsExeInputCue = new FhMethodHandle<FhFfx.FhCall.MsExeInputCue>(
    this,
    "FFX.exe",
    FhFfx.FhCall.__addr_MsExeInputCue,
    h_ms_exe_input_cue);
```

What this means:
1. `MsExeInputCue` is the native function signature (delegate).
2. `__addr_MsExeInputCue` is the known offset/address entry.
3. `h_ms_exe_input_cue` is your replacement/interceptor callback.

## 3) How This Project Organizes Offsets

Validated offsets are declared in:
- `src/data/ExternalMemoryOffsetMap.cs`
- `src/data/ExternalMemoryOffsetMap.*.cs` (domain partials)

Example domain constants:

```csharp
public static class BattleFlags {
    public const int BattleActive = 0x00D2A8E0;
    public const int BattleState = 0x00D2C9F1;
}
```

For typed runtime usage, this project also has immutable descriptors in:
- `src/data/MemoryRegistry.cs`
- `src/data/MemoryLocation.cs`

Example:

```csharp
public static readonly MemoryLocation BattleActive =
    new(nameof(BattleActive), ExternalMemoryOffsetMap.BattleFlags.BattleActive);
```

`MemoryLocation` is metadata (name + base offset + optional pointer chain), not magic by itself.

## 4) Reading Values: Raw Offsets vs Fahrenheit Struct Pointers

There are two common paths.

## A) Read through Fahrenheit struct pointers (preferred when available)

Example from this mod:

```csharp
Btl* battle = _battleAdapter.GetBattle();
if (battle == null) return;

ushort commandId = (ushort)(battle->last_com & 0xFFFFu);
```

Pros:
- More semantic and stable.
- Less manual pointer arithmetic.

Cons:
- Only possible if Fahrenheit already exposes that structure/field.

## B) Read via manual offset arithmetic (fallback)

Example from this mod (`src/ParryModule.DataMapping.cs`):

```csharp
private static ushort read_attack_command_id_candidate_from_btl_offset(Btl* battle, byte queueIndex) {
    if (battle == null) return 0;

    const int cueBaseOffset = 0x3A8;
    const int cueStride = 0x48;
    byte* ptr = (byte*)battle + cueBaseOffset + (queueIndex * cueStride);
    return *(ushort*)ptr;
}
```

Pros:
- Works even when a field is not modeled in Fahrenheit yet.

Cons:
- More fragile and version-sensitive.
- Must validate carefully.

## 5) Pointer Chains (Deep Reads)

Some values are not at `base + offset` directly. Instead:
1. read pointer at `base + rootOffset`
2. add inner offsets
3. dereference repeatedly

Conceptual helper:

```csharp
unsafe static T ReadAt<T>(byte* moduleBase, int offset) where T : unmanaged {
    return *(T*)(moduleBase + offset);
}

unsafe static T ReadChain<T>(byte* moduleBase, int rootOffset, params int[] chain) where T : unmanaged {
    byte* cursor = *(byte**)(moduleBase + rootOffset); // first dereference
    for (int i = 0; i < chain.Length - 1; i++) {
        cursor = *(byte**)(cursor + chain[i]);
    }

    int leaf = chain.Length == 0 ? 0 : chain[^1];
    return *(T*)(cursor + leaf);
}
```

`MemoryLocation` in this project stores exactly this information (`BaseOffset`, `PointerOffsets`) so you can build safe helpers around it.

## 6) Hook Lifecycle in This Mod

Hook setup:

```csharp
_hMainLoop = new FhMethodHandle<SgMainLoop>(this, "FFX.exe", 0x420C00, h_main_loop_timing);
_hMsExeInputCue = new FhMethodHandle<FhFfx.FhCall.MsExeInputCue>(
    this, "FFX.exe", FhFfx.FhCall.__addr_MsExeInputCue, h_ms_exe_input_cue);
```

Activate:

```csharp
_hMainLoop.hook();
_hMsExeInputCue.hook();
```

Call original function from inside hook:

```csharp
private void h_ms_exe_input_cue() {
    // pre logic
    _hMsExeInputCue.orig_fptr.Invoke();
    // post logic
}
```

Important:
- If you forget `orig_fptr.Invoke()`, you replaced behavior completely.
- If you call it incorrectly (for example recursion), you can crash or freeze.

## 7) Why This Mod Uses Runtime Events on Top of Hooks

Hooks are low-level interception points. They are noisy and timing-sensitive.

This mod emits higher-level runtime events (for example dispatch started/consumed), then a turn/parry state machine consumes them.

Pattern:

```csharp
// in hook callback
_turnRuntimeEvents.EmitDispatchStarted(...);
_hMsExeInputCue.orig_fptr.Invoke();
_turnRuntimeEvents.EmitDispatchConsumed(...);
```

Then elsewhere:

```csharp
process_turn_runtime_events();
```

Benefits:
1. Cleaner architecture (combat logic does not depend on hook internals everywhere).
2. Better debuggability (timeline/log view can consume normalized events).
3. Easier testing (state machine tests without native runtime).

## 8) How Offsets, Hooks, and Mappings Work Together in This Repo

1. Hook gives timing and transition boundaries (`MsExeInputCue`, main loop).
2. Struct reads give current combat state (`Btl*`, cues, attacker/target).
3. Offset fallback fills gaps where struct modeling is incomplete.
4. JSON data mappings (`resources/mappings/*.json`) map IDs to user-facing labels.
5. Debug timeline/log displays interpreted state transitions.

Example output formatting path:

```csharp
ushort commandId = (ushort)(battle->last_com & 0xFFFFu);
if (_dataMappings.TryResolveCommandDisplay(commandId, out string label, out string kind)) {
    return $"0x{commandId:X4} {kind}: {label}";
}
```

## 9) Decision Matrix: What to Use When

Use Fahrenheit-exposed struct/field if:
- it already exists and is semantically clear.

Use `FhCall.__addr_*` hook if:
- you need exact transition timing or call boundaries.

Use raw offset fallback if:
- Fahrenheit does not expose the needed field yet,
- and you can validate behavior with at least two independent runtime signals.

Use runtime events if:
- multiple systems (state machine, overlay, logs) need the same transition stream.

## 10) Safety Rules and Common Failure Modes

## Crash Causes
1. Wrong calling convention in delegate signature.
2. Wrong struct size/packing assumptions.
3. Reading from stale/invalid pointers outside expected game state.
4. Hook recursion or not invoking original where required.

## Defensive Practices
1. Guard null pointers and state preconditions.
2. Keep offset reads behind dedicated methods (single change point).
3. Add confidence levels for inferred values.
4. Log transitions and raw IDs in debug builds.
5. Add tests for state machine logic (already done in `tests/Parry.Tests`).

## 11) Minimal End-to-End Example

Goal: detect possible command id for head cue and produce user-friendly text.

```csharp
Btl* battle = _battleAdapter.GetBattle();
if (battle == null) return "None";

byte queueIndex = 0;
AttackCue cue = battle->attack_cues[queueIndex];

ushort fromCue = cue.command_count > 0 ? read_attack_command_id_raw(cue.command_list[0]) : (ushort)0;
ushort fromOffset = read_attack_command_id_candidate_from_btl_offset(battle, queueIndex);

ushort selected = is_plausible_command_id(fromCue) ? fromCue : fromOffset;
if (selected == 0) return "Unknown";

if (_dataMappings.TryResolveCommandDisplay(selected, out string label, out string kind)) {
    return $"0x{selected:X4} {kind}: {label}";
}

return $"0x{selected:X4}";
```

This exact layering is the practical pattern used throughout this mod:
- gather low-level runtime signal,
- resolve confidence/fallback,
- map to human-readable output.

## 12) Suggested Learning Path for a Web Developer

1. Start with `src/ParryModule.cs` and hook setup only.
2. Read `src/ParryModule.Hooks.cs` to understand pre/orig/post hook flow.
3. Read `src/ParryModule.DataMapping.cs` for pointer/offset fallback logic.
4. Read `src/combat/*.cs` for event-driven state machine design.
5. Run `dotnet test tests/Parry.Tests/Parry.Tests.csproj -c Debug` and map test behavior back to runtime logic.

Once you can explain one hook and one offset read end-to-end, the rest of the codebase becomes much easier.
