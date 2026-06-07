# Homeland Sow Alignment & Manure Visual

How **Sow all** in `HomelandFarmFeature` must match manual seed-bag planting, why wrong `CropPlantPoint` fields broke fertilizer heap visuals, and what the mod implements today.

Related: [DECOMPILED_SOURCE_MAP.md](./DECOMPILED_SOURCE_MAP.md), [superpowers/plans/2026-06-06-homeland-sow-alignment.md](./superpowers/plans/2026-06-06-homeland-sow-alignment.md), wire parser `tools/parse_grow_packet.py`.

---

## Summary

| Symptom | Root cause | Fix |
|---------|------------|-----|
| Manure heap off-screen / missing on mod-sown crops | Server spawned crop at wrong `entity.position` because `GrowCropNetworkCommand` had wrong `CropPlantPoint.pos` | Align sow wire with UI `BoxArg` |
| Same `pos` on every crop box | Fallback read `TransformComponentData.position` (field-local grid, `y=0`) | Use craft `worldToLocal` + putZone world pose |
| `angle=0` / `270` vs UI `90` | Angle from crop-box `TransformComponentData`, not craft preview rotation | Camera yaw + 180° → field-local (like `CraftMode_Multiple`) |
| `pos.y=0` vs UI `0.06` | Entity/putZone baseline; aura `rectMatrix` path double-offset to `0.12` without normalize | `HomelandFarmNormalizeCropSowFieldLocalPos` → `0.06` |

**Confirmed in-game (2026-06):** after sow wire matched UI (`pos.y=0.06`, `angle=90`, putZone slot 2), **fertilizer visual also fixed** without extra manure-only changes. Manure hook was never the primary bug; it only masked wrong crop placement.

---

## Game call chain (manual sow)

```text
SeedBagCommand.ExecutePlantSeed()
  ← HandHoldSeed.Input_ConfirmPlacing()
  ← CraftMode_Multiple.ConfirmPlacing()
  ← BuildMultipleFocus.GenConfirmCraftResult()
  ← BuildSingle.GenSimpleConfirmOption(placeZone, BoxSide.Bottom)
  → BoxArg.position / BoxArg.rotation / zoneElement.putZoneId
  → PlayerSeedBagParaBase (targetPositions, targetAngleYs, targetLevelObjectIds)
  → PlayerSeedBagAction.OnBehaveFinish()
  → CropProtocolManager.CropSeeding(seedNetId, List<CropPlantPoint>)
  → GrowCropNetworkCommand (TCP)
```

### Decompile references (`ilspy-dumps/`)

| Step | File |
|------|------|
| Confirm / lists | `XDTLevelAndEntity/.../SeedBagCommand.cs` (~182–202) |
| Field-local pos/rot | `XDTDataAndProtocol/.../BuildSingle.cs` `GenSimpleConfirmOption` (~1380–1386) |
| Craft confirm batch | `XDTLevelAndEntity/.../PlantSystem/BuildMultipleFocus.cs` `GenConfirmCraftResult` |
| Crop points → network | `XDTLevelAndEntity/.../PlayerSeedBagAction.cs` (~95–100) |
| Wire command | `EcsClient/.../GrowCropNetworkCommand.cs` |

### `GenSimpleConfirmOption` (authoritative pos/angle)

```csharp
element.root.GetPositionAndRotation(out point, out quaternion2);
Quaternion quaternion3 = Quaternion.Inverse(localToWorld.rotation) * quaternion2;
dst.rotation = CraftMath.ReducePrecision(quaternion3, element.anglePrecision, side);
Vector3 vector2 = worldToLocal.MultiplyPoint(point);
dst.position = CraftMath.ReducePrecision(vector2);
```

`SeedBagCommand` sends **`boxArg.position` / `boxArg.rotation` already in field-local space** (not world). Angle on wire: `Mathf.RoundToInt(rotation.eulerAngles.y)`.

Preview root `point` is the **seed craft template** after `UpdateWorldMatrix` / raycast onto the crop-box put zone — **not** crop-box `TransformComponentData.position`.

---

## Wire: `GrowCropNetworkCommand`

Parser: `python tools/parse_grow_packet.py <hex>`

| Field | Source in UI | Notes |
|-------|--------------|-------|
| `levelObjectNetId` | `boxArg.zoneElement.putZoneId` | Must pass `LevelObjectManager.GetLevelObject(putZoneId) != null` |
| `pos` | `BoxArg.position` | Field-local, `CraftMath.ReducePrecision`, typical `y=0.06` on homeland crop grid |
| `angle` | `RoundToInt(BoxArg.rotation.eulerAngles.y)` | 90° steps |
| `seedNetId` | Handhold seed bag net id | Same as mod |

Put-zone encoding: `LevelObjectId(planterNetId, slot)` → `(slot << 32) | planterNetId`. **Crop-box sow uses slot 2** (craft raycast), not slot 1 (field root).

Example (verified):

```text
UI:  putZone=0x200B1B706 slot=2  pos=(-10.5, 0.06, 7.5)  angle=90  seed=11644504
Mod: (after fix) same fields on wire
```

---

## Why manure visual broke (downstream of sow)

```text
Wrong CropPlantPoint.pos
  → server creates crop entity at wrong TransformComponentData / world pose
  → CropComponent.UpdateManureEffect / CreateLevelEntity(..., base.entity.position, ...)
  → decoration spawned at wrong world point (often off-screen)
  → UpdateManureEffect mono hook could run but bind at wrong place
```

Mod still uses direct `CropProtocolManager.CropSeeding` (native `mono_runtime_invoke`), not full `PlayerSeedBagAction` animation — **wire fields must match UI**.

`homelandFarmPlanterSowAnchorByNetId` is filled in `TryHomelandFarmRememberPlanterSowAnchor` during sow resolve for fertilize/VFX fallbacks when crop world pose diverges from planter.

---

## Mod implementation (`HomelandFarmFeature.cs`)

Entry: `TryHomelandFarmAppendEmptyPlanterPoint` → `TryHomelandFarmResolveBoxFieldPlacement` → `TryHomelandFarmTryResolveSowPointFromCraftPutZone` → `TryHomelandFarmSowNative`.

### 1. Put zone (`levelObjectNetId`)

- `TryHomelandFarmResolveCropBoxSowLevelObjectId` — probes slots `{2,1,0,…}`, prefers slot **2** when scores tie (`HomelandFarmIsPreferredSowPutZoneSlot`).
- Validates via Aura `LevelObjectManager.GetLevelObject(uint,int)` (`TryHomelandFarmValidateSowPutZoneLevelObject`).

### 2. World pose for seed root (approximation of `element.root`)

`TryHomelandFarmTryResolveSowSeedRootWorldPose`:

1. `GetLevelObject(putZoneId)` → `DynamicBoxCollider.rectMatrix` origin (`TryHomelandFarmTryGetAuraPutZoneRectMatrix`), else hierarchy position.
2. Fallback: `TryHomelandFarmResolveFarmEntityPosition(planterNetId)`.
3. Rotation: `TryHomelandFarmTryResolveSowPreviewWorldRotation` — `cameraTransform.eulerAngles.y + 180°` (same initial value as `CraftMode_Multiple.OnEnterPlacing`), else putZone rotation.

### 3. Field-local pos/angle

`TryHomelandFarmTryConvertWorldPoseToFieldLocalSow`:

- Matrices: `TryHomelandFarmTryGetFieldCraftMatrices` (Aura `Entities.fieldSystem` → `FieldComponent.buildWorld.worldToLocal` / `localToWorld`).
- `pos = ReducePrecision(worldToLocal * worldPos)`.
- `angle = QuantizeFieldLocalSowAngleY(Inverse(localToWorld.rot) * worldRot)`.

### 4. Y normalize

`HomelandFarmNormalizeCropSowFieldLocalPos`: if `y≈0` or `y≈0.12` (double offset from old half-height hack), set **`y = 0.06`** (`HomelandFarmCropSowFieldLocalY`).

### 5. Send

`CreateHomelandFarmCropPlantPoint` → `TryHomelandFarmSowNative` → `CropProtocolManager.CropSeeding`.

Log line to verify:

```text
[HomelandFarm] Sow point planter=<id> putZone=<encoded> pos=(-10.5, 0.06, 7.5) angle=90.
```

---

## Regression checklist

- [ ] Parse mod TCP grow packet: `pos.y=0.06`, `angle` matches manual sow on same box, `slot=2`.
- [ ] Sow all on 1–3 different crop boxes: **distinct** `pos.x/z` per cell, not one constant for all.
- [ ] Fertilize mod-sown crop: manure heap visible at crop box (no manual re-sow).
- [ ] Manual sow still works; no crash from `icall.c:1622` mono assert (monitor — known with native sow).

---

## Not implemented (future)

Full managed/Aura **`GenSimpleConfirmOption`** path (live `NewTemplateBuildSingle` + raycast + `UpdateWorldMatrix`) — see plan Task 1–2. Current approximation is sufficient for verified wire match on homeland crop grid.

Out of scope: flower `PlantProtocolManager.GrowPlant`, full `PlayerSeedBagAction` animation, global `mono_runtime_invoke` hooks (crashed game in experiments).

---

## History (mod mistakes → fix)

| Mod behavior | Wire symptom |
|--------------|--------------|
| `TransformComponentData.position` as pos | Correct XZ often, **`y=0`**, wrong angle |
| putZone slot 1 fallback | Wrong `levelObjectNetId` |
| `worldToLocal * entityPos` only | Still **`y=0`** |
| rectMatrix + 0.06 m world half-height | **`y=0.12`** |
| rectMatrix + camera rot + Y normalize | **`y=0.06`, angle matches UI** |

---

## Manure hook (secondary)

`EnsureHomelandFarmCropManureVisualPatch` — native hook on `CropComponent.UpdateManureEffect` to bind/sync decoration after game logic runs. Useful when sow pos is already correct; **does not replace** correct sow placement.
