# Draw Upload-to-Server Implementation Plan

**Goal:** Push an edited Draw image back to the **server** (not just the local cache) so the drawing actually changes in-game and for other players.

**Why the current feature can't do it:** `ScreenCapture/Draw/*.png` is only a local download cache written by `LocalTextureCacheService.SaveTexture(...)`. The server stores a painting as a stream of **pixel operations** over the DrawBoard protocol, plus a rendered preview image in OBS media storage. Editing the local cache never touches either. Confirmed in `ilspy-dumps/` (2026-06-11).

> Scope v1: **edit an existing, self-owned, single-part ("normal", `CustomID <= 0`) drawing while standing at your own easel.** New/template/multi-part artworks are out of scope for v1 (see Risks).

---

## How the game actually saves a drawing (authoritative references)

| Step | Source | What happens |
|------|--------|--------------|
| Encode pixels | `XDTLevelAndEntity.GameplaySystem.Drawing.CanvasPainter.Save(int step)` | Builds `Dictionary<byte,List<DrawingBatchOperationSequentialInfo>>` from changed pixels and calls `DrawBoardProtoManager.DrawingOperation(dict, step, currentPart)` |
| Start session | `DrawBoardProtoManager.SendStartDrawing(drawNetId, canvasId, netid, name, templateId)` | `StartDrawingNetworkCommand` (3 branches by `StarDrawingType`) |
| Send pixels | `DrawBoardProtoManager.DrawingOperation(...)` | `DrawingBatchOperationNetworkCommand` via `WebRequestUtility.SendCommand` |
| End session | `DrawBoardProtoManager.SendExitDrawing()` | empty `ExitDrawingNetworkCommand`; server sets `isPainting=false` |
| Preview + antispam | `DrawSystem.UploadRGBATexture(tex, auditPhotoId, needUploadMedia, photoType)` | `UploadSystem.UploadTextureWithFileName(..., UploadTextureType.Drawing, ...)` → OBS, then `UploadMediaToAntispamNetworkCommand` via `CensorProtocolManager.Send` |
| Local cache | `LocalTextureCacheService.SaveTexture(...)` | what the mod currently edits — **local only** |

### Command structs (image `EcsClient`, ns `XDT.Scene.Shared.Modules.DrawBoard`)

```csharp
enum StarDrawingType { None, Canvas, Template, Artwork }   // note spelling "Star"

struct StartDrawingNetworkCommand {              // [NetworkCommand]
    StarDrawingType Type;
    uint DrawBoardNetId;        // [VerifyEntity] – must be a valid drawboard entity near you
    int  CanvasId;              // for Type.Canvas (new blank)
    uint DrawManualArtworkNetId;// for Type.Artwork (edit existing)
    int  TemplateId;            // for Type.Template
    string DrawManualArtworkName;
}

struct DrawingBatchOperationNetworkCommand {     // [NetworkCommand]
    int StepNum;
    int DrawingPart;
    Dictionary<byte, List<DrawingBatchOperationSequentialInfo>> PixelDataToSequentialInfos;
}

struct DrawingBatchOperationSequentialInfo { ushort Start; ushort Length; }  // RLE run

struct ExitDrawingNetworkCommand { }             // default/empty
```

### Exact RLE encoding (mirror `CanvasPainter.Save`)

For the changed pixels grouped **by color byte**:
- flat index = `y * Width + x`
- per byte: collect indices → `Sort()` → emit runs where `Start` = first index of a consecutive run, `Length` = **(runLength − 1)** (a single pixel → `Length = 0`).
- color byte is the **stored R8 byte**: opaque = `0x80 | paletteIndex`, transparent/erase = `124` (same bytes as the index map we already produce in `DrawColorCodec`).

To replace a whole drawing: treat **every** pixel as "changed" (full overwrite) — build the dict from all pixels of the target index map. (Diff-vs-current is a later optimization.)

> ⚠️ **Pixel orientation must be verified.** `CanvasPainter` indexes `pixelData = Texture.GetRawTextureData()` (R8, bottom-up rows) as `y*Width+x`, while our index PNG is read via `GetPixels32`/`LoadImage`. Send a known **asymmetric** test pattern first and confirm it appears upright (not vertically flipped/mirrored) in-game before trusting the mapping. Fix with a row-flip if needed.

### Session identifiers

- `DrawBoardNetId` = the easel entity you interact with. Mirror `DrawingPanel.GetInteractionNetId()`:
  `EntityUtil.GetSelfPlayer().Status.FocusUIStatus.FocusLevelObject` → `LevelObjectManager.GetLevelObject(...)` → `levelObject.ownerNetId`. The easel carries `DrawingBoardComponent` (`IsSelfPainting`, `DrawPhotoId`, `AuditPhotoId`, `CustomID`, `ownerNetId`).
- `DrawManualArtworkNetId` = `DataModule<DrawSystem>.Instance.GetNetId(photoId)` (→ `DrawDataModule.GetNetId`). `photoId` comes from the Draw filename.
- `auditPhotoId` = `DrawingBoardComponent.AuditPhotoId` (needed for the preview upload).
- Server validates ownership: `HomelandSystem.CheckDrawBoardPermission(ownerNetId)` + `IsSelfPainting`. ⇒ **only your own drawing on your own board.**

---

## Files

| File | Change |
|------|--------|
| `buddy/DrawUploadFeature.cs` (new, partial `HeartopiaComplete`) | Resolution + send pipeline + RLE builder + small GUI |
| `buddy/DrawColorCodec.cs` | Add `BuildPixelRuns(byte[] indexBytes, w, h)` → `Dictionary<byte,(ushort Start,ushort Length)[]>` (pure C#, reused by tests) |
| `buddy/buddy.csproj` | `Compile Include` for the new file |
| `buddy/LocalizationManager.cs` | UI strings/status |
| `tools/draw_upload_sim.py` (optional) | Offline RLE builder to diff against `CanvasPainter.Save` output for tests |
| `docs/FEATURES.md` | Document the upload action + its constraints |

---

### Task 1 — Resolve DrawBoard types & SendCommand (IL2CPP/interop)

These structs live in **EcsClient** (`XDT.Scene.Shared.Modules.DrawBoard`). Resolve like the farm/bird network commands (see `docs/TYPE_RESOLUTION.md` § SendCommand):

- [ ] `FindLoadedType("XDT.Scene.Shared.Modules.DrawBoard.StartDrawingNetworkCommand", …)` (+ `EcsClient.`-prefixed and `Il2Cpp`-prefixed variants); same for `DrawingBatchOperationNetworkCommand`, `ExitDrawingNetworkCommand`, and the value type `DrawingBatchOperationSequentialInfo`, and enum `StarDrawingType`. Fall back to `IL2CPP.GetIl2CppClass("EcsClient.dll", "XDT.Scene.Shared.Modules.DrawBoard", "…")` (the daily-quest v10 pattern).
- [ ] Resolve `WebRequestUtility` + `ChannelType` and the static generic `SendCommand` (3-param) as the existing features do; cache `MakeGenericMethod` per command type.
- [ ] Smoke test: send `ExitDrawingNetworkCommand` (harmless no-op when not painting) and confirm no exception → proves the SendCommand plumbing resolves for these types.

### Task 2 — Build the pixel-operation payload

- [ ] In `DrawColorCodec`, add `BuildPixelRuns(byte[] indexBytes, int w, int h)` returning per-byte sorted RLE runs exactly per `CanvasPainter.Save` (`Length = run-1`, index `y*w+x`). Pure, unit-testable.
- [ ] Construct the IL2CPP `DrawingBatchOperationNetworkCommand`:
  - build `Il2CppSystem.Collections.Generic.Dictionary<byte, List<DrawingBatchOperationSequentialInfo>>`,
  - for each byte, an `Il2CppSystem...List<DrawingBatchOperationSequentialInfo>`, adding boxed structs (`Start`,`Length`).
  - **Highest-risk interop step.** If nested-generic construction is painful, fall back to invoking Mono `DrawBoardProtoManager.DrawingOperation(dict, step, part)` via AuraMono, or driving a real `CanvasPainter` (`CreatePainter` → `EnableSave` → set pixels → `Save`). Pick whichever resolves cleanly on this build.
- [ ] Decide payload size strategy: one full-canvas batch for small canvases; for larger, split into multiple `DrawingOperation` calls (also reduces anti-cheat suspicion — see Task 5).

### Task 3 — Session driver

- [ ] Resolve `DrawBoardNetId` (mirror `GetInteractionNetId`) and require a `DrawingBoardComponent` with `IsSelfPainting == true`, `CustomID <= 0` (v1), capture `DrawPhotoId` + `AuditPhotoId`.
- [ ] Resolve `DrawManualArtworkNetId = DrawSystem.GetNetId(photoId)` (AuraMono / DataModule).
- [ ] Sequence: `StartDrawingNetworkCommand{Type=Artwork, DrawBoardNetId, DrawManualArtworkNetId, DrawManualArtworkName=name}` → wait for `StartDrawingNetworkEvent` (server `isPainting=true`) → one/more `DrawingBatchOperationNetworkCommand` → `ExitDrawingNetworkCommand`.
- [ ] Gate everything on: at own easel, own drawing, board entity valid. Log one status line per stage.

### Task 4 — Preview image + antispam

- [ ] After Exit, call `DrawSystem.UploadRGBATexture(rgbaPreview, auditPhotoId, needUploadMedia:true, photoType)` so the thumbnail updates and censorship passes. Build `rgbaPreview` from the index map via the LUT (reuse `IndexPngToColoredPng` → Texture2D), or pass the R8 texture (the method converts via `R8ToRGBATexture`). Without this the artwork may show stale/preview-less or get flagged.

### Task 5 — Anti-cheat & safety

- [ ] Review `docs/BEHAVIORAL_ANTI_CHEAT.md`. A single full-canvas op is unlike human stroke input → **split into several `DrawingOperation` steps with small delays**, plausible `StepNum`, and stay within draw-burden limits (`DrawSystem.GetDrawBurden`, `IsPaintingLimit`).
- [ ] Hard guard: refuse if not owner / not self-painting / board missing. Never target other players' boards.

### Task 6 — UI & flow

- [ ] In the Pictures/Draw area add **"Upload edited Draw to server"** acting on the currently-focused easel's `DrawPhotoId`: load edited `Draw/<id>.png` → index bytes → runs → run Task 3+4 coroutine. Status + result notification.

### Task 7 — Verification (in-world)

- [ ] **Orientation test:** upload an asymmetric marker; confirm upright in-game (fix flip if needed).
- [ ] **Round-trip:** edit colored PNG → upload → reopen drawing in album → matches edit.
- [ ] **Persistence:** relaunch / have another account view it → server-side change confirmed.
- [ ] `dotnet build buddy/buddy.csproj -p:Loader=BepInEx` → 0 errors.

---

## Risks & open questions

| Risk | Mitigation |
|------|------------|
| Pixel orientation (R8 raw bottom-up vs PNG) | Asymmetric test pattern first (Task 7) |
| Nested IL2CPP generic dict construction | Fallbacks: AuraMono `DrawBoardProtoManager.DrawingOperation`, or drive `CanvasPainter` |
| `StepNum`/`DrawingPart` semantics | Confirm server accepts a bulk op; start single-part `CustomID<=0` only |
| Anti-cheat flags bulk write | Split steps + delays + burden limits (Task 5) |
| Server rejects ops unless `isPainting` set | Wait for `StartDrawingNetworkEvent` before sending ops |
| Multi-part / template / new artwork | Out of v1 scope; needs per-part region data + create flow |
| Editing others' / official covers | Hard-blocked by permission; do not attempt |

## Recommended order
1. Task 1 (resolve + SendCommand smoke test)
2. Task 2 (payload, with orientation test harness)
3. Task 3 (session driver) → first end-to-end on a tiny canvas
4. Task 7 orientation/round-trip checks
5. Task 4 (preview/antispam)
6. Task 5 (anti-cheat hardening)
7. Task 6 (UI) + Task 8 docs

## Done criteria
- [ ] Editing a self-owned normal drawing and clicking upload changes it **on the server** (survives relaunch, visible to others).
- [ ] Colors/orientation match the edited PNG.
- [ ] Guarded to own boards/drawings; respects burden limits; no anti-cheat trip in normal use.
- [ ] `dotnet build` clean.
