# Game Events (EventCenter) — Reference & Mod Integration

How Heartopia's in-game event bus works, how the mod can (and can't) plug into it,
and the full catalogue of event types. The flat list of all ~1450 events lives in
[GAME_EVENTS_LIST.md](GAME_EVENTS_LIST.md).

---

## 1. The bus: `XDTGame.Core.EventCenter`

Source: [`ilspy-dumps/XDTBaseService/XDTGame.Core/EventCenter.cs`](../ilspy-dumps/XDTBaseService/XDTGame.Core/EventCenter.cs).

A static, type-keyed publish/subscribe hub. Every event is a **`struct` that implements
`XDTGame.Core.IEvent`** (a marker interface, no members). Handlers are keyed by the event's
`System.Type`, so dispatch is O(1) on a `Dictionary<Type, ...>`.

```csharp
public static class EventCenter
{
    // Global listeners
    public static Delegate AddListener<T>(in Action<T> action)    where T : struct, IEvent;
    public static void     RemoveListener<T>(in Action<T> action) where T : struct, IEvent;
    public static void     DispatchEvent<T>(in T @event)          where T : struct, IEvent;

    // Per-entity listeners (keyed by netId, e.g. "this specific bird")
    public static void AddListener<T>(uint netId, in Action<T> action)    where T : struct, IEvent;
    public static void RemoveListener<T>(uint netId, in Action<T> action) where T : struct, IEvent;
    public static void DispatchEvent<T>(uint netId, in T @event)          where T : struct, IEvent;

    // Non-generic overloads (subscribe by Type + Delegate)
    public static void AddListener(Type eventType, in Delegate action);
    public static void RemoveListener(Type eventType, in Delegate action);
}
```

Internals worth knowing:
- Global handlers are stored in a `LinkedListExecutor` (a pooled singly-linked list per event
  type, newest-first via `AddHead`). Per-netId handlers live in `subExecutors[netId]`.
- `Dispatch` wraps each handler invocation in `try/catch` and logs via `DebugSystem.LogError`
  — **a throwing listener does not break the dispatch chain**, and the throw is swallowed
  into the game log, not surfaced to other systems.
- `DispatchEvent(netId, …)` is a **no-op if nobody registered for that netId** (no auto-create
  on dispatch — only `AddListener(netId, …)` creates the sub-executor).

### Canonical usage (from game code)

[`InstrumentModule.cs`](../ilspy-dumps/XDTGameUI/XDTGUI.Module.Instrument/InstrumentModule.cs)
is a textbook subscriber:

```csharp
// register
EventCenter.AddListener<InstrumentPanelOpenEvent>(new Action<InstrumentPanelOpenEvent>(OnInstrumentPanelOpen));
EventCenter.AddListener<InstrumentPanelCloseEvent>(new Action<InstrumentPanelCloseEvent>(OnInstrumentPanelClose));
// ... later, on teardown ...
EventCenter.RemoveListener<InstrumentPanelOpenEvent>(new Action<InstrumentPanelOpenEvent>(OnInstrumentPanelOpen));
```

---

## 2. Instrument open/close flow (concrete, end-to-end)

This is the chain behind the InstrumentHotkeyGuard feature, traced through the dumps:

1. The player starts/stops playing — [`PlayerInstrumentMotion.cs`](../ilspy-dumps/XDTLevelAndEntity/XDTLevelAndEntity.Gameplay.Locomotion/PlayerInstrumentMotion.cs)
   dispatches the **root** events:
   - line 250: `EventCenter.DispatchEvent<InstrumentPanelOpenEvent>(new InstrumentPanelOpenEvent { … })`
   - line 207: `EventCenter.DispatchEvent<InstrumentPanelCloseEvent>(default)`
2. [`InstrumentModule`](../ilspy-dumps/XDTGameUI/XDTGUI.Module.Instrument/InstrumentModule.cs)
   listens for those and re-broadcasts higher-level mode events:
   - `InstrumentPanelOpenEvent` → `InstrumentModeStartedEvent`
   - `InstrumentPanelCloseEvent` → `InstrumentModeEndedEvent`
3. [`InstrumentPanel`](../ilspy-dumps/XDTGameUI/XDTGame.UI.Panel/InstrumentPanel.cs) is the UI
   view; `OnStart` builds the keyboard / `OnStop` tears it down. Its fields `_instrumentType`
   (`InstrumentType` enum) and `_nowKeyOption` (`MusicKeyOption` enum) are what the mod reads
   today via AuraMono `GetView`.

### The four instrument events

| Event | Namespace | Payload | When |
|---|---|---|---|
| `InstrumentPanelOpenEvent` | `XDTDataAndProtocol.Events` | `InstrumentType InstrumentType; uint instrumentNetId; ulong instrumentLevelObjectNetId; int staticId;` | player begins playing |
| `InstrumentPanelCloseEvent` | `XDTDataAndProtocol.Events` | *(empty, Size=1)* | player stops playing |
| `InstrumentModeStartedEvent` | `XDTGameSystem.UI` | `InstrumentType instrumentType; int staticId; uint instrumentNetId; ulong instrumentLevelObjectNetId;` | re-broadcast of open |
| `InstrumentModeEndedEvent` | `XDTGameSystem.UI` | *(empty, Size=1)* | re-broadcast of close |

Enums (for reading payloads / panel fields):
- `InstrumentType` ([dump](../ilspy-dumps/EcsClient/XDT.Scene.Shared.Modules.Music/InstrumentType.cs)):
  `None=0, Piano=1, Conga=2, KaHongDrum=3, BaYinTong=4, EtherealDrum=5, Lute=11 … Saxophone=21`.
- `MusicKeyOption` ([dump](../ilspy-dumps/EcsClient/XDT.Scene.Shared.Modules.Music/MusicKeyOption.cs)):
  `KeyMode8=0, KeyMode15a=1, KeyMode15b=2, KeyMode22=3`.

---

## 3. How the mod can hook game events — three strategies

The mod runs under BepInEx as a **separate .NET assembly**; the game's managed types
(`XDTGame.*`, the `IEvent` structs) are **not loadable** as compile-time references and are
absent from the mod's runtime (see `memory/homeland-farm-scan-perf.md`). The game executes in
its own embedded **AuraMono** runtime. That rules out simply calling
`EventCenter.AddListener<InstrumentPanelOpenEvent>(…)` in C#. The realistic options:

### A. Native method detour ✅ *recommended*

The mod already has a working native-hook toolkit (used by the Bubble feature):
- `mono_compile_method` → `mono_method_get_unmanaged_thunk` to get a method's native code
  pointer ([`BubbleFeature.TryGetAuraMonoMethodNativePointer`](../buddy/BubbleFeature.cs)),
- `BubbleMonoNativeHook.TryInstall(nativeMethod, hookPtr, out trampoline)` to splice in a
  trampoline ([`BubbleFeature.cs`](../buddy/BubbleFeature.cs) ~line 1090–1126),
- `Marshal.GetFunctionPointerForDelegate` / `GetDelegateForFunctionPointer` to bridge a mod C#
  delegate ↔ native code.

So instead of polling, **detour a method that always runs on open/close** and flip a static
flag. For the instrument case the cleanest targets are `InstrumentPanel.OnStart` (→ open) and
`InstrumentPanel.OnStop` (→ close), or `PlayerInstrumentMotion`'s dispatch methods. Cost: one
hook installed once; zero per-frame work; no native-AV exposure from per-frame raw reads.

This is the right long-term mechanism for **any** "react to a game event" need in the mod, not
just instruments.

### B. Subscribe to `EventCenter` via AuraMono ⚠️ *possible but heavy*

Mechanically: resolve `EventCenter`, `MakeGenericMethod` of `AddListener<T>` with the event's
mono `Type`, build a mono delegate from a reverse-marshaled mod callback
(`GetFunctionPointerForDelegate` + `mono_ftnptr_to_delegate`-style bridge), and pass it in.
Doable, but it's strictly more moving parts than option A (generic instantiation + delegate
marshaling + matching the exact `Action<T>` signature the bus stores) for the same outcome.
Only worth it if we need to listen to many events generically rather than a couple of specific
lifecycle points.

### C. Per-frame `UIManager.GetView` polling — *current approach*

What `InstrumentHotkeyGuardFeature` does now: every refresh tick, resolve `UIManager.Instance`
and call `GetView(typeof(InstrumentPanel))` via AuraMono, then read `_instrumentType` /
`_nowKeyOption`. **Confirmed working** (log: `aura GetView ok: type=1 keyOption=3`) but it is
the most expensive option and carries native-AV risk on the raw field reads, which is why it is
now TTL-throttled + failure-throttled + GC-guarded. Fine as a stopgap; superseded by A.

---

## 4. Event catalogue (by namespace)

~1450 event structs total. The big buckets:

| Namespace | Count | What's in it |
|---|---|---|
| `XDTDataAndProtocol.Events` | 810 | server/protocol-driven gameplay events (the bulk) |
| `XDTGameSystem.UI` | 316 | UI mode/panel focus + system UI events |
| `ScriptsRefactory.DataAndProtocol.Events` | 170 | older protocol-layer events (birds, pets, animation, …) |
| `XDTGame.UI` | 31 | UI bridge events (game-mode focus, blueprint, etc.) |
| `XDTDataAndProtocol.Events.GameSetting` | 15 | settings-changed notifications |
| `XDTDataAndProtocol.Events.Player` | 14 | player-state events |
| *(others)* | ~96 | build/competition, party, energy, homeland, navigation, … |

Full enumerated list grouped by namespace: **[GAME_EVENTS_LIST.md](GAME_EVENTS_LIST.md)**.

### Regenerating the list

Run from the repo root (Git Bash / WSL):

```bash
grep -rl ": IEvent" ilspy-dumps/ | while read f; do
  ns=$(grep -m1 "^namespace " "$f" | sed 's/^namespace //; s/;.*//')
  grep -E "struct [A-Za-z0-9_]+ : .*\bIEvent\b" "$f" \
    | sed -E 's/.*struct ([A-Za-z0-9_]+).*/\1/' \
    | while read s; do echo "${ns:-(global)}.$s"; done
done | sort -u > events.txt
```

---

## 5. Status & next steps for the InstrumentHotkeyGuard bug

**Diagnosis (from the live log).** Detection is *not* the problem — `aura GetView ok:
type=1 keyOption=3` shows the open Piano panel in KeyMode22 was resolved. The mod hotkeys still
fired (`SetHandhold toolId=1/3/5` = Axe/Rod/Net equips) because the **per-key blocking set did
not contain the pressed keys**. Two contributing causes:

1. **`pianoSemitone` can't be read.** `TryGetGameSettingPianoSemitone` uses *managed* reflection
   on `GameSettingSystem`, which is absent on this build, so it always returns `false`. With
   Piano + KeyMode22 the guard therefore always picks the plain `inputevent22` layout and never
   the semitone+black-key layout — so half the real piano keys (`O P [ ] 0 - =`, number row,
   etc.) are never blocked.
2. **Design mismatch.** The guard only blocks keys that match the instrument's note layout. Any
   mod hotkey bound to a key *outside* that layout still fires while you're playing — which is
   exactly what happened with the equip hotkeys.

**Recommended fix (simple, matches the commit's intent "Block hotkeys if instrument is open"):**
when an instrument panel is detected open, **block *all* mod hotkeys**, not just layout-matching
keys. Detection already works reliably; drop the fragile per-key/`pianoSemitone` matching
entirely. `IsModHotkeyBlockedByInstrument` / `TryGetModHotkeyDown` would just consult a single
"instrument open" flag.

**Recommended mechanism (robust):** replace the per-frame `GetView` poll (strategy C) with a
one-time native detour (strategy A) on `InstrumentPanel.OnStart`/`OnStop` to maintain that flag.
This removes the per-frame AuraMono work and the native-AV exposure that caused the original
post-world-load crash.

### Implemented (final)

1. **Layout fix.** `pianoSemitone` is read from `PlayerPrefs.GetInt("PianoSemitone", 0)` (it's a
   computed property, not a field — managed/AuraMono field reads could never see it). KeyMode22
   piano now blocks the correct semitone + black-key layout.
2. **Detection = throttled GetView polling (strategy C).** `InstrumentHotkeyGuardFeature` resolves
   the open panel via `UIManager.GetView` at most every `InstrumentHotkeyGuardRefreshInterval`
   (0.2s), with an AuraMono miss-cooldown and a GC guard around the raw reads. Reliable and stable.

### Native detour (strategy A) — attempted, abandoned

Detouring `InstrumentPanel.OnStart`/`OnStop` via `BubbleMonoNativeHook` **crashed** on the first
instrument open. Root cause: `BubbleMonoNativeHook` steals a **fixed 14 bytes** of prologue and
cannot relocate RIP-relative instructions; `CreateBubble`'s prologue happened to be relocatable,
`OnStart`/`OnStop`'s was not. (Also note `mono_method_get_unmanaged_thunk`, which the Bubble path
resolves, is a native-call wrapper that normal managed/vtable calls never traverse — to intercept
real calls you must detour the `mono_compile_method` return address instead.) Making detours safe
here needs a real length-disassembling hook library (MinHook/Detours-style) — deferred.

### Other event routes (not pursued)

- **EventCenter subscription via AuraMono** (true events): requires generic-method inflation of
  `AddListener<T>` + constructing a managed `Action<T>` delegate around a native callback. No such
  machinery exists in the mod today; crash risk comparable to the detour. Not worth it for this
  feature.
- **mono vtable slot swap**: safer than byte-patching but needs vtable internals not currently
  exported.
