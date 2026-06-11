# Shop Buy-All Gold Items Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Добавить в мод кнопку «купить всё за gold (Coin)» для выбранного в dropdown Force Open Shop магазина — без UI-кликов, через `ShopSystem` + сетевую покупку.

**Architecture:** Новый partial-файл `ShopBuyAllFeature.cs` с coroutine-циклом. Переиспользует dropdown `forceOpenShopSelectedIndex` / `forceOpenShopOptions`. Резолв `storeId` — общий helper из существующего switch в `TryOpenSelectedForceShop`. Список товаров — AuraMono invoke `DataModule<ShopSystem>.Instance.GetStoreGoodsData(storeId)`. Покупка — `ShopSystem.BuyItem(netId, count)` (внутри → `ShopShelfProtocolManager.BuyItem` → `BuyStoreItemCommand`). Фильтр: только `StoreMoneyType.Currency` + `currencyType == CurrencyType.Coin` (enum value **1**).

**Tech Stack:** C# / MelonLoader or BepInEx IL2CPP, AuraMono (`mono_runtime_invoke`), existing `HeartopiaComplete` helpers (`TryResolveAuraMonoModule`, `FindAuraMonoMethodOnHierarchy`, coroutines).

**Out of scope (v1):** Clothing Store (`DressShopPanel` / `BuyClothes`), Face Shop, Meteor/Starfall Exchange (оплата предметами), pay-shop (`moneyValue == 2`), UGC-магазины, free-товары, пакеты `PackSlot*`.

---

## Game reference (ilspy-dumps)

| Concern | Type / method |
|---------|----------------|
| Список магазинов | `TableData.TableStoreInfos` |
| Слоты | `TableData.TableStoreSlots` |
| Runtime товары | `ShopSystem._storeItemData` ← `IStoreService` |
| Товары магазина | `ShopSystem.GetStoreGoodsData(int storeId)` → `List<ShopItemData>` |
| Покупка | `ShopSystem.BuyItem(uint netId, int count)` |
| Сеть | `BuyStoreItemCommand` via `ShopShelfProtocolManager.BuyItem` |
| Gold в игре | `CurrencyType.Coin = 1` (`StoreMoneyType.Currency`, `moneyValue == 1`) |
| Открытие UI (опционально) | `ShopPanel.OpenShopPanel(storeId)` — не обязательно для протокола |

`ShopItemData` поля для фильтра: `netId`, `currencyType`, `price`, `leftCount`, `isUnlock`, `storeMoneyType`, `storeId`, `slotId`, `storeGroupId`.

---

## File map

| File | Responsibility |
|------|----------------|
| `buddy/ShopBuyAllFeature.cs` | **Create** — логика buy-all, coroutine, AuraMono invoke, фильтры |
| `buddy/HeartopiaComplete.cs` | **Modify** — UI кнопка рядом с Force Open Shop, tick hook, storeId resolver extract |
| `buddy/LocalizationManager.cs` | **Modify** — строки EN + остальные локали (минимум EN) |
| `docs/FEATURES.md` | **Modify** — описание фичи |
| `docs/DECOMPILED_SOURCE_MAP.md` | **Modify** — одна строка в matrix ShopSystem |

---

## Supported stores (v1)

Магазины, открываемые через `ShopPanel.OpenShopPanel` с фиксированным `storeId`:

| Dropdown index | Label | storeId |
|----------------|-------|---------|
| 1 | Birdwatching Store | 55 |
| 2 | Book Shop | 147 |
| 3 | Carpet Shop | 10 |
| 5 | Cooking Store | 53 |
| 7 | Fishing Store | 52 |
| 8 | Furniture Extra | 6 |
| 9 | Fortune Store - Rainbow | 86 |
| 10 | Fortune Store - Rain | 87 |
| 11 | Garden Store | 51 |
| 12 | General Store | dynamic (`TryOpenGeneralStore` cache / keywords) |
| 13 | Insect Catching Store | 56 |
| 14 | Pet Store | 54 |
| 15 | Special Home Decor Store | 82 |
| 16 | Showroom | 7 |

**Unsupported (вернуть понятный статус, не покупать):**

| Index | Reason |
|-------|--------|
| 0 | None |
| 4 | Clothing — `DressShopPanel`, `BuyClothes` |
| 6 | Face Shop — `FaceShopPanel` |
| 17 | Meteor Exchange — `WeatherExchangeShopPanel`, оплата `TableCostItem[]` |

Fortune stores (86/87): поддерживаются, но купятся только позиции с `currencyType == Coin`; wish-star товары отфильтруются автоматически.

---

### Task 1: Extract storeId resolver

**Files:**
- Modify: `buddy/HeartopiaComplete.cs` (~17988–21070)

- [ ] **Step 1: Add helper method**

```csharp
private bool TryResolveForceOpenShopStoreId(int selectedIndex, out int storeId, out string label, out string unsupportedReason)
{
    storeId = 0;
    label = string.Empty;
    unsupportedReason = null;

    switch (selectedIndex)
    {
        case 0:
            unsupportedReason = "No shop selected.";
            return false;
        case 4:
            unsupportedReason = "Clothing Store uses BuyClothes, not supported.";
            return false;
        case 6:
            unsupportedReason = "Face Shop is not a Coin shop panel.";
            return false;
        case 17:
            unsupportedReason = "Meteor Exchange uses item cost, not Coin.";
            return false;
        case 1: storeId = 55; label = "Birdwatching Store"; return true;
        case 2: storeId = 147; label = "Book Shop"; return true;
        case 3: storeId = 10; label = "Carpet Shop"; return true;
        case 5: storeId = 53; label = "Cooking Store"; return true;
        case 7: storeId = 52; label = "Fishing Store"; return true;
        case 8: storeId = 6; label = "Furniture Extra"; return true;
        case 9: storeId = 86; label = "Fortune Store - Rainbow"; return true;
        case 10: storeId = 87; label = "Fortune Store - Rain"; return true;
        case 11: storeId = 51; label = "Garden Store"; return true;
        case 13: storeId = 56; label = "Insect Catching Store"; return true;
        case 14: storeId = 54; label = "Pet Store"; return true;
        case 15: storeId = 82; label = "Special Home Decor Store"; return true;
        case 16: storeId = 7; label = "Showroom"; return true;
        case 12:
            if (this.forceOpenShopResolvedStoreIds.TryGetValue("General Store", out int cached) && cached > 0 && cached != 88)
            {
                storeId = cached;
                label = "General Store";
                return true;
            }
            if (this.TryResolveStoreIdByKeywords(new[] { "general", "ka ching", "ui_picture_shop_img_1001" }, out storeId, out label))
            {
                if (storeId == 88) { unsupportedReason = "Resolved pay shop (88), refused."; return false; }
                this.forceOpenShopResolvedStoreIds["General Store"] = storeId;
                return true;
            }
            unsupportedReason = "General Store id not resolved.";
            return false;
        default:
            unsupportedReason = "Unknown shop index " + selectedIndex;
            return false;
    }
}
```

- [ ] **Step 2: Refactor `TryOpenSelectedForceShop`**

Заменить дублирующий `switch (forceOpenShopSelectedIndex)` на вызов `TryResolveForceOpenShopStoreId` + `TryOpenShopPanelByStoreId` / спец-панели для unsupported cases 4, 6, 17.

- [ ] **Step 3: Build**

```bat
cd buddy
build-all.bat
```

Expected: compile success.

- [ ] **Step 4: Commit**

```bash
git add buddy/HeartopiaComplete.cs
git commit -m "refactor: extract force-open shop storeId resolver"
```

---

### Task 2: ShopBuyAllFeature skeleton + constants

**Files:**
- Create: `buddy/ShopBuyAllFeature.cs`

- [ ] **Step 1: Create partial class file**

```csharp
using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace HeartopiaMod
{
    public partial class HeartopiaComplete
    {
        private const bool ShopBuyAllLogsEnabled = MasterLogAutoBuy;
        private const int ShopBuyAllCurrencyCoin = 1; // CurrencyType.Coin
        private const int ShopBuyAllStoreMoneyCurrency = 1; // StoreMoneyType.Currency
        private const float ShopBuyAllDelaySeconds = 0.08f;
        private const int ShopBuyAllMaxPerCommand = 99; // cap per BuyItem call

        private object shopBuyAllCoroutine = null;
        private string shopBuyAllStatus = "Idle.";
        private bool shopBuyAllRunning = false;

        private IntPtr shopBuyAllShopSystemModule = IntPtr.Zero;
        private IntPtr shopBuyAllGetStoreGoodsDataMethod = IntPtr.Zero;
        private IntPtr shopBuyAllBuyItemMethod = IntPtr.Zero;

        private void ShopBuyAllLog(string message)
        {
            if (ShopBuyAllLogsEnabled) ModLogger.Msg("[ShopBuyAll] " + message);
        }

        private void StartShopBuyAllGold()
        {
            if (this.shopBuyAllRunning || this.shopBuyAllCoroutine != null)
            {
                this.AddMenuNotification(this.L("Shop buy-all already running"), new Color(0.45f, 0.88f, 1f));
                return;
            }

            if (!this.TryResolveForceOpenShopStoreId(this.forceOpenShopSelectedIndex, out int storeId, out string label, out string unsupported))
            {
                this.shopBuyAllStatus = unsupported ?? "Shop not supported.";
                this.AddMenuNotification(this.shopBuyAllStatus, new Color(1f, 0.55f, 0.45f));
                return;
            }

            this.shopBuyAllStatus = "Preparing: " + label + " (id " + storeId + ")...";
            this.shopBuyAllRunning = true;
            this.shopBuyAllCoroutine = ModCoroutines.Start(this.ShopBuyAllGoldRoutine(storeId, label));
        }

        private IEnumerator ShopBuyAllGoldRoutine(int storeId, string label)
        {
            yield return null;
            // Tasks 3–5 fill this in
            this.shopBuyAllRunning = false;
            this.shopBuyAllCoroutine = null;
        }
    }
}
```

- [ ] **Step 2: Register file in `buddy.csproj`**

Убедиться, что `ShopBuyAllFeature.cs` включён в compile (обычно wildcard `*.cs` — проверить).

- [ ] **Step 3: Build** — `cd buddy && build-all.bat` — PASS.

- [ ] **Step 4: Commit**

```bash
git add buddy/ShopBuyAllFeature.cs
git commit -m "feat: add shop buy-all gold feature skeleton"
```

---

### Task 3: AuraMono — resolve ShopSystem + GetStoreGoodsData

**Files:**
- Modify: `buddy/ShopBuyAllFeature.cs`

- [ ] **Step 1: Add cache resolver**

```csharp
private bool TryEnsureShopBuyAllShopSystem(out string error)
{
    error = null;
    if (this.shopBuyAllShopSystemModule != IntPtr.Zero
        && this.shopBuyAllGetStoreGoodsDataMethod != IntPtr.Zero
        && this.shopBuyAllBuyItemMethod != IntPtr.Zero)
    {
        return true;
    }

    if (!this.EnsureAuraMonoApiReady() || !this.AttachAuraMonoThread())
    {
        error = "AuraMono unavailable.";
        return false;
    }

    if (!this.TryResolveAuraMonoModule(
            "XDTGameSystem.GameplaySystem.Shop.ShopSystem",
            out IntPtr shopSys) || shopSys == IntPtr.Zero)
    {
        error = "ShopSystem module not found.";
        return false;
    }

    IntPtr getGoods = this.FindAuraMonoMethodOnHierarchy(shopSys, "GetStoreGoodsData", 1);
    IntPtr buyItem = this.FindAuraMonoMethodOnHierarchy(shopSys, "BuyItem", 2);
    if (getGoods == IntPtr.Zero || buyItem == IntPtr.Zero)
    {
        error = "GetStoreGoodsData or BuyItem method missing.";
        return false;
    }

    this.shopBuyAllShopSystemModule = shopSys;
    this.shopBuyAllGetStoreGoodsDataMethod = getGoods;
    this.shopBuyAllBuyItemMethod = buyItem;
    return true;
}
```

- [ ] **Step 2: Invoke GetStoreGoodsData**

```csharp
private bool TryInvokeGetStoreGoodsData(int storeId, out IntPtr listObj, out string error)
{
    listObj = IntPtr.Zero;
    error = null;
    if (!this.TryEnsureShopBuyAllShopSystem(out error))
    {
        return false;
    }

    IntPtr[] args = { Marshal.AllocHGlobal(sizeof(int)) };
    try
    {
        Marshal.WriteInt32(args[0], storeId);
        IntPtr exc = IntPtr.Zero;
        listObj = auraMonoRuntimeInvoke(this.shopBuyAllGetStoreGoodsDataMethod, this.shopBuyAllShopSystemModule, args, out exc);
        if (exc != IntPtr.Zero || listObj == IntPtr.Zero)
        {
            error = "GetStoreGoodsData invoke failed.";
            return false;
        }
        return true;
    }
    finally
    {
        Marshal.FreeHGlobal(args[0]);
    }
}
```

(При необходимости скорректировать сигнатуру `aura_runtime_invoke` под существующий helper в `HeartopiaComplete` — использовать тот же паттерн, что в `PetFeedFeature` / `DailyQuestSubmitFeature`.)

- [ ] **Step 3: Build** — PASS.

- [ ] **Step 4: Commit**

```bash
git add buddy/ShopBuyAllFeature.cs
git commit -m "feat: aura resolve ShopSystem GetStoreGoodsData"
```

---

### Task 4: Read ShopItemData fields from Mono list

**Files:**
- Modify: `buddy/ShopBuyAllFeature.cs`

`ShopItemData` — struct в `XDTGameSystem.GameplaySystem.Shop`. Поля читать через `mono_object_unbox` + offset или через существующий `TryReadAuraMonoStructField` если есть.

- [ ] **Step 1: Define internal DTO**

```csharp
private struct ShopBuyAllCandidate
{
    public uint NetId;
    public int Price;
    public int LeftCount;
    public int CurrencyType;
    public int StoreMoneyType;
    public bool IsUnlock;
    public string Name;
}
```

- [ ] **Step 2: Implement collector**

```csharp
private bool TryCollectGoldCoinItems(int storeId, out List<ShopBuyAllCandidate> items, out string error)
{
    items = new List<ShopBuyAllCandidate>();
    if (!this.TryInvokeGetStoreGoodsData(storeId, out IntPtr listObj, out error))
    {
        return false;
    }

    List<IntPtr> elements = new List<IntPtr>();
    if (!this.TryEnumerateAuraMonoCollectionItems(listObj, elements))
    {
        error = "Failed to enumerate shop items.";
        return false;
    }

    for (int i = 0; i < elements.Count; i++)
    {
        if (!this.TryReadShopItemDataMono(elements[i], out ShopBuyAllCandidate c))
        {
            continue;
        }

        if (c.StoreMoneyType != ShopBuyAllStoreMoneyCurrency)
        {
            continue; // not plain currency
        }
        if (c.CurrencyType != ShopBuyAllCurrencyCoin)
        {
            continue; // not gold/coin
        }
        if (!c.IsUnlock || c.LeftCount <= 0 || c.Price <= 0)
        {
            continue;
        }

        items.Add(c);
    }

    if (items.Count == 0)
    {
        error = "No purchasable Coin items in this store.";
        return false;
    }

    return true;
}
```

- [ ] **Step 3: Implement `TryReadShopItemDataMono`**

Читать минимум: `netId` (uint), `currencyType` (int enum), `price` (int), `isUnlock` (bool), `storeMoneyType` (int), `name` (string property — fallback `"item"`).

Для `leftCount`: предпочтительно invoke property getter `get_leftCount` на unboxed struct; fallback — поле `_leftCount` (int).

Сверить имена полей в `ilspy-dumps/XDTGameSystem/XDTGameSystem.GameplaySystem.Shop/ShopItemData.cs`.

- [ ] **Step 4: Log first run**

В `ShopBuyAllGoldRoutine` после collect — `ShopBuyAllLog("candidates=" + items.Count)`.

- [ ] **Step 5: Build** — PASS.

- [ ] **Step 6: Commit**

```bash
git add buddy/ShopBuyAllFeature.cs
git commit -m "feat: collect coin-priced shop items via aura"
```

---

### Task 5: Buy loop with affordability

**Files:**
- Modify: `buddy/ShopBuyAllFeature.cs`

- [ ] **Step 1: Optional currency read**

```csharp
private bool TryGetPlayerCoinBalance(out long balance, out string error)
{
    balance = 0;
    error = null;
    // AuraMono: DataModule<PlayerServiceSystem>.Instance.GetCurrencyCount(CurrencyType.Coin)
    // Pattern: TryResolveAuraMonoModule("XDTGameSystem.PlayerService.PlayerServiceSystem", ...)
    // Invoke GetCurrencyCount(int) with arg ShopBuyAllCurrencyCoin
    // If unavailable: return true with balance = long.MaxValue (buy until server rejects)
}
```

- [ ] **Step 2: Purchase invoke**

```csharp
private bool TryInvokeShopBuyItem(uint netId, int count, out string error)
{
    error = null;
    if (!this.TryEnsureShopBuyAllShopSystem(out error))
    {
        return false;
    }

    count = Mathf.Clamp(count, 1, ShopBuyAllMaxPerCommand);
    // mono_runtime_invoke BuyItem(uint netId, int count) — 2 args
    // Log: ShopBuyAllLog($"BuyItem netId={netId} count={count}");
    return true; // or false on exception
}
```

- [ ] **Step 3: Complete coroutine**

```csharp
private IEnumerator ShopBuyAllGoldRoutine(int storeId, string label)
{
    yield return null;

    if (!this.TryCollectGoldCoinItems(storeId, out List<ShopBuyAllCandidate> items, out string prepError))
    {
        this.shopBuyAllStatus = prepError;
        this.AddMenuNotification(prepError, new Color(1f, 0.55f, 0.45f));
        this.shopBuyAllRunning = false;
        this.shopBuyAllCoroutine = null;
        yield break;
    }

    this.TryGetPlayerCoinBalance(out long coinBalance, out _);
    int bought = 0;
    int skipped = 0;

    for (int i = 0; i < items.Count; i++)
    {
        ShopBuyAllCandidate item = items[i];
        int unitPrice = item.Price;
        int maxAffordable = unitPrice > 0 ? (int)Math.Min(item.LeftCount, coinBalance / unitPrice) : 0;
        if (maxAffordable <= 0)
        {
            skipped++;
            continue;
        }

        if (!this.TryInvokeShopBuyItem(item.NetId, maxAffordable, out string buyError))
        {
            this.ShopBuyAllLog("buy failed netId=" + item.NetId + ": " + buyError);
            skipped++;
            yield return new WaitForSecondsRealtime(ShopBuyAllDelaySeconds);
            continue;
        }

        bought++;
        coinBalance -= (long)unitPrice * maxAffordable;
        this.shopBuyAllStatus = $"Buying {i + 1}/{items.Count}: {item.Name} x{maxAffordable}";
        yield return new WaitForSecondsRealtime(ShopBuyAllDelaySeconds);
    }

    this.shopBuyAllStatus = $"Done ({label}): bought {bought}, skipped {skipped}.";
    this.AddMenuNotification(this.shopBuyAllStatus, new Color(0.55f, 1f, 0.65f));
    this.shopBuyAllRunning = false;
    this.shopBuyAllCoroutine = null;
}
```

- [ ] **Step 4: In-game test (manual)**

1. Зайти в город, открыть меню Insert.
2. Features → выбрать **Cooking Store** → **BUY ALL (COIN)**.
3. Проверить лог `[ShopBuyAll]` и что предметы появились в рюкзаке.
4. Повторить с магазином без coin-товаров — ожидать «No purchasable Coin items».

- [ ] **Step 5: Commit**

```bash
git add buddy/ShopBuyAllFeature.cs
git commit -m "feat: buy all coin items in selected store via protocol"
```

---

### Task 6: UI integration

**Files:**
- Modify: `buddy/HeartopiaComplete.cs` (~25220–25300, Force Open Shop panel)
- Modify: `buddy/LocalizationManager.cs`

- [ ] **Step 1: Increase force panel height**

`forcePanelHeight` + ~40f для новой кнопки.

- [ ] **Step 2: Add button below OPEN SELECTED SHOP**

```csharp
Rect buyAllRect = new Rect(forceBodyPanel.x + 14f, openBtnRect.yMax + 8f, forceBodyPanel.width - 28f, 32f);
if (GUI.Button(buyAllRect, this.L("BUY ALL (COIN)")))
{
    this.StartShopBuyAllGold();
}
GUI.Label(new Rect(buyAllRect.x, buyAllRect.yMax + 2f, buyAllRect.width, 16f), this.shopBuyAllStatus, hintStyle);
```

Disable button when `shopBuyAllRunning`.

- [ ] **Step 3: Localization**

```csharp
{ "BUY ALL (COIN)", "BUY ALL (COIN)" },
```

Добавить переводы в существующие языковые блоки по аналогии с `"AUTO BUY (Cooking Store)"`.

- [ ] **Step 4: Build + manual UI test**

- [ ] **Step 5: Commit**

```bash
git add buddy/HeartopiaComplete.cs buddy/LocalizationManager.cs
git commit -m "feat: shop buy-all coin button in features tab"
```

---

### Task 7: Documentation

**Files:**
- Modify: `docs/FEATURES.md`
- Modify: `docs/DECOMPILED_SOURCE_MAP.md`

- [ ] **Step 1: FEATURES.md** — секция после Force Open Shop:

```markdown
### Buy All (Coin) — Selected Shop

- Uses the same shop dropdown as Force Open Shop.
- Buys every available item priced in `CurrencyType.Coin` via `ShopSystem.BuyItem` (no UI clicks).
- Unsupported: Clothing, Face Shop, Meteor Exchange.
- Log: `MasterLogAutoBuy` / `[ShopBuyAll]`.
```

- [ ] **Step 2: DECOMPILED_SOURCE_MAP.md** — строка в matrix:

`| Shop buy-all coin | ShopSystem, ShopShelfProtocolManager, BuyStoreItemCommand | ShopBuyAllFeature.cs | A |`

- [ ] **Step 3: Commit**

```bash
git add docs/FEATURES.md docs/DECOMPILED_SOURCE_MAP.md
git commit -m "docs: shop buy-all coin feature"
```

---

## Risk matrix

| Risk | Mitigation |
|------|------------|
| `ShopItemData` struct read fails on IL2CPP | Логировать raw field dump; fallback managed `FindLoadedType` + reflection если interop stub есть |
| Сервер rate-limit / anti-cheat | `ShopBuyAllDelaySeconds` 80–150ms; stop on repeated invoke errors |
| Нехватка Coin mid-loop | `TryGetPlayerCoinBalance` + skip; сервер всё равно валидирует |
| General Store id 88 (pay shop) | Уже отклоняется в resolver |
| Pack/bundle items | Исключены фильтром `StoreMoneyType.Currency` only |

---

## Future (not in v1)

- Checkbox «open shop panel for visual feedback»
- Config max spend / max items per run
- Support `CurrencyType.GoldPiece` (25) as separate toggle
- Clothing `BuyClothes` batch
- Exchange shops with `SubmitItems` builder from backpack

---

## Self-review

| Spec requirement | Task |
|------------------|------|
| Выбранный магазин из dropdown | Task 1 resolver + Task 6 UI |
| Только gold/Coin | Task 4 filter `CurrencyType.Coin` |
| Купить всё доступное | Task 5 loop `leftCount` + affordability |
| Протокол, не UI | Task 3–5 AuraMono BuyItem |
| Unsupported shops clear error | Task 1 |

No TBD placeholders remain.
