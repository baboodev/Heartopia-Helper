# Draw Color Decode Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** После AES-decrypt файлов `ScreenCapture/Draw/*.png` показывать и редактировать цветное изображение (LUT), а при re-encrypt сохранять обратно игровой index-map формат.

**Architecture:** Draw на диске — это не RGB-фото, а карта индексов палитры (семантика `TextureFormat.R8`). Прозрачность: байт `124` или любой байт без бита `0x80`. Цвет: `(byte)(0x80 | lutIndex)`. Палитра — `DrawingConfig.ColorLut` (`GetPixels()[index]`). Конвертация index↔RGBA реализуется в общем кодеке; mod и Python-скрипт вызывают его на этапах decrypt / encrypt-changed. Для безопасного roundtrip храним index-plain отдельно от colored preview.

**Tech Stack:** C# (Unity mod, `PicturesDecryptFeature`), Python (`tools/screen_capture_crypto.py`, Pillow), AES как сейчас, палитра из `DrawingConfig.ColorLut`.

---

## Контекст (почему «серое» — не баг decrypt)

| Слой | Факт |
|------|------|
| Путь | `{persistentDataPath}/ScreenCapture/Draw/{photoId}_{w}_{h}.png` |
| Шифрование | AES-256-CBC PKCS7 (`EncryptUtil`), как Photo |
| Содержимое plain | PNG с index-байтами (после decrypt — валидный PNG) |
| Загрузка в игре | `LoadImage` → `RGB24`; индекс в канале **R** (`GetR8Texture`, `LocalTextureCacheService`) |
| Прозрачность | `(pixel & 0x80) == 0` → прозрачный; canvas fill = `124` |
| Цветной пиксель | `0x80 \| lutIndex` |
| Палитра | `Managers.Get<IConfigManager>().DrawingConfig.ColorLut` |
| UI shader | `ui/material/m_drawing_lut`, property `_ColorLut` |

Проверка на реальном файле: decrypted Draw PNG — `RGB`, все каналы равны, типичные значения `124`, `128+idx`, `252` (`0x80|124`).

Эталон decode в игре: `PaintingDetailWidget.cs` (ветка `TextureFormat.R8`, ~строки 336–362):
- `alpha = (indexByte != (124 & 0x7F)) ? 255 : 0` → фактически `indexByte != 124`
- `rgb = ColorLut[ indexByte & 0x7F ]`

Эталон encode в игре: `DrawingPanel.GetLutColor`:
- если `ColorLut[idx].a == 0` → `124`
- иначе → `(byte)(0x80 | idx)`

---

## Файлы

| Файл | Назначение |
|------|------------|
| `buddy/DrawColorCodec.cs` | index↔RGBA, загрузка LUT из игры |
| `buddy/PicturesDecryptFeature.cs` | ветка Draw в decrypt / encrypt-changed |
| `buddy/LocalizationManager.cs` | строки UI (опция preview / статус) |
| `tools/draw_color_codec.py` | Python-аналог кодека |
| `tools/assets/drawing_color_lut.png` | fallback-палитра (дамп из игры) |
| `tools/extract_drawing_lut.py` | одноразовый дамп LUT из запущенной игры / assets |
| `docs/plans/2026-06-10-draw-color-decode.md` | этот план |

---

### Task 1: Дамп палитры ColorLut

**Files:**
- Create: `tools/extract_drawing_lut.py`
- Create: `tools/assets/drawing_color_lut.png`
- Create: `tools/assets/drawing_color_lut.json` (опционально: `{ "width", "height", "colors": [[r,g,b,a], ...] }`)

- [ ] **Step 1: Добавить mod-хук для дампа LUT (временный или debug-кнопка)**

В `DrawColorCodec.cs` (или временно в `PicturesDecryptFeature`):

```csharp
private static bool TryDumpColorLut(string outPath)
{
    try
    {
        object cfgMgr = /* reflection: Managers.Get<IConfigManager>() */;
        Texture2D lut = /* DrawingConfig.ColorLut */;
        if (lut == null) return false;
        byte[] png = lut.EncodeToPNG();
        File.WriteAllBytes(outPath, png);
        return true;
    }
    catch { return false; }
}
```

- [ ] **Step 2: Сохранить LUT в репозиторий**

Запуск в игре → файл `tools/assets/drawing_color_lut.png`.

Проверка: ширина LUT обычно 256 (или `lut.width * lut.height` записей в `GetPixels()`); каждый индекс `0..255` — один цвет.

- [ ] **Step 3: Зафиксировать SHA256 LUT в коде**

```csharp
public const string DefaultLutSha256 = "<hex после дампа>";
```

Использовать в manifest (`LutSha256`) для предупреждения при несовпадении версии игры.

---

### Task 2: Общий кодек index ↔ RGBA (C#)

**Files:**
- Create: `buddy/DrawColorCodec.cs`
- Modify: `buddy/buddy.csproj` (Compile Include)

- [ ] **Step 1: Реализовать загрузку LUT**

```csharp
internal static class DrawColorCodec
{
    public const byte TransparentIndexByte = 124;
    public const byte OpaqueFlag = 0x80;

    public static Color32[] LoadLut(Texture2D lutTexture) { /* GetPixels → Color32 */ }
    public static Color32[] LoadLutFromPng(string path) { /* Texture2D + LoadImage */ }
    public static Color32[] TryLoadLutFromGame() { /* IConfigManager.DrawingConfig.ColorLut */ }
}
```

Приоритет: LUT из игры → fallback `tools`-копия рядом с mod (если положить в `buddy/assets/`) → hardcoded png resource.

- [ ] **Step 2: Index map → RGBA32 PNG bytes**

```csharp
public static byte[] IndexPngToColoredPng(byte[] indexPngBytes, Color32[] lut)
{
    Texture2D tex = new Texture2D(2, 2);
    tex.LoadImage(indexPngBytes);
    int w = tex.width, h = tex.height;
    Color32[] src = tex.GetPixels32();
    Color32[] dst = new Color32[src.Length];
    for (int i = 0; i < src.Length; i++)
    {
        byte idxByte = src[i].r; // RGB grayscale: R=G=B=index
        if (idxByte == TransparentIndexByte || (idxByte & OpaqueFlag) == 0)
            dst[i] = new Color32(0, 0, 0, 0);
        else
        {
            int lutIndex = idxByte & 0x7F;
            Color32 c = lut[lutIndex];
            dst[i] = new Color32(c.r, c.g, c.b, 255);
        }
    }
    Texture2D outTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
    outTex.SetPixels32(dst);
    outTex.Apply();
    return outTex.EncodeToPNG();
}
```

- [ ] **Step 3: RGBA32 PNG → index map PNG bytes**

```csharp
public static byte[] ColoredPngToIndexPng(byte[] coloredPngBytes, Color32[] lut)
{
    Texture2D tex = new Texture2D(2, 2);
    tex.LoadImage(coloredPngBytes);
    int w = tex.width, h = tex.height;
    Color32[] src = tex.GetPixels32();
    byte[] raw = new byte[w * h];
    for (int i = 0; i < src.Length; i++)
    {
        Color32 p = src[i];
        if (p.a < 128)
        {
            raw[i] = TransparentIndexByte;
            continue;
        }
        int best = FindClosestLutIndex(p, lut); // Euclidean RGB, только записи с a>0
        raw[i] = lut[best].a == 0
            ? TransparentIndexByte
            : (byte)(OpaqueFlag | best);
    }
    Texture2D outTex = new Texture2D(w, h, TextureFormat.R8, false);
    outTex.LoadRawTextureData(raw);
    outTex.Apply();
    return outTex.EncodeToPNG();
}

private static int FindClosestLutIndex(Color32 p, Color32[] lut)
{
    int best = 0;
    int bestDist = int.MaxValue;
    for (int i = 0; i < lut.Length; i++)
    {
        if (lut[i].a == 0) continue;
        int dr = p.r - lut[i].r, dg = p.g - lut[i].g, db = p.b - lut[i].b;
        int d = dr * dr + dg * dg + db * db;
        if (d < bestDist) { bestDist = d; best = i; }
    }
    return best;
}
```

- [ ] **Step 4: Unit-проверка вручную**

1. Взять decrypted `Draw/*.png` (index).
2. Прогнать `IndexPngToColoredPng` → открыть в просмотрщике: должны быть цвета как в игре.
3. Прогнать обратно `ColoredPngToIndexPng` без правок → SHA256 index PNG должен совпасть с исходным (lossless roundtrip).

---

### Task 3: Расширить manifest

**Files:**
- Modify: `buddy/PicturesDecryptFeature.cs`

- [ ] **Step 1: Добавить поля в `PicturesManifestEntry`**

```csharp
public string ContentKind { get; set; } // "photo" | "draw-index" | "draw-colored"
public string IndexPlainSha256 { get; set; } // для draw-colored: hash index sibling
public string LutSha256 { get; set; }
```

- [ ] **Step 2: Схема файлов для Draw**

```
ScreenCaptureDecrypted/Draw/{id}_{w}_{h}.png          ← colored preview (редактируемый)
ScreenCaptureDecrypted/Draw/.index/{id}_{w}_{h}.png   ← index plain (для lossless roundtrip)
```

Manifest:
- `Draw/foo.png` → `ContentKind=draw-colored`, `PlainSha256` = hash colored, `IndexPlainSha256` = hash index
- `Draw/.index/foo.png` → `ContentKind=draw-index`, не показывать в UI changed-list по умолчанию

Альтернатива (проще, без `.index/`): один файл colored, index hash только в manifest не хранится — roundtrip lossy. **Не рекомендуется.**

- [ ] **Step 3: Миграция manifest v1 → v2**

Если `ContentKind` отсутствует — считать `photo` (JPG/PNG Photo/Head/…).

---

### Task 4: Интеграция в decrypt (mod)

**Files:**
- Modify: `buddy/PicturesDecryptFeature.cs`

- [ ] **Step 1: Определять Draw-файлы**

```csharp
private static bool IsDrawRelativePath(string relativePath)
{
    return relativePath.StartsWith("Draw/", StringComparison.OrdinalIgnoreCase)
        && relativePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 2: В `TryDecryptScreenCaptureFile` / post-process**

После AES-decrypt PNG:
1. Записать index-plain в `Draw/.index/...` (как сейчас в `Draw/...`).
2. Сконвертировать → colored PNG в `Draw/...` (перезапись пути для пользователя).
3. Обновить manifest (оба hash).

Псевдокод:

```csharp
if (IsDrawRelativePath(relativePath))
{
    string indexPath = destDrawIndexPath(relativePath);
    Directory.CreateDirectory(Path.GetDirectoryName(indexPath));
    File.WriteAllBytes(indexPath, plainPng);

    Color32[] lut = DrawColorCodec.TryLoadLutFromGame() ?? DrawColorCodec.LoadLutFromEmbedded();
    byte[] colored = DrawColorCodec.IndexPngToColoredPng(plainPng, lut);
    File.WriteAllBytes(destFile, colored);
    // manifest entries for both
}
```

- [ ] **Step 3: UI статус**

Добавить в `picturesLastStatus` счётчик: `N draw previews generated`.

- [ ] **Step 4: Сборка**

```bash
dotnet build buddy/buddy.csproj
```

Expected: 0 errors.

---

### Task 5: Интеграция в encrypt-changed (mod)

**Files:**
- Modify: `buddy/PicturesDecryptFeature.cs`

- [ ] **Step 1: Detect changed draw-colored file**

Если `ContentKind == draw-colored` и hash colored изменился:
1. `coloredBytes = File.ReadAllBytes(coloredPath)`
2. `indexBytes = DrawColorCodec.ColoredPngToIndexPng(coloredBytes, lut)`
3. Записать index в `Draw/.index/...`, обновить `IndexPlainSha256`
4. AES-encrypt `indexBytes` → `ScreenCapture/Draw/...`

- [ ] **Step 2: Если изменён только `.index/` (продвинутый пользователь)**

Пропустить re-quantize; encrypt index напрямую; синхронизировать colored preview из index.

- [ ] **Step 3: Проверка в игре**

1. Decrypt all.
2. Отредактировать colored PNG в `Draw/`.
3. Encrypt changed.
4. Открыть рисунок в игре — цвета должны совпасть.

---

### Task 6: Python-паритет (`tools/`)

**Files:**
- Create: `tools/draw_color_codec.py`
- Modify: `tools/screen_capture_crypto.py`

- [ ] **Step 1: Порт кодека**

```python
TRANSPARENT = 124
OPAQUE_FLAG = 0x80

def index_png_to_rgba_png(index_png: bytes, lut: list[tuple[int,int,int,int]]) -> bytes: ...
def rgba_png_to_index_png(rgba_png: bytes, lut: list[tuple[int,int,int,int]]) -> bytes: ...
def load_lut(path: Path) -> list[tuple[int,int,int,int]]: ...
```

- [ ] **Step 2: Команды CLI**

```bash
python tools/screen_capture_crypto.py decode-draw --source ... --dest ...
python tools/screen_capture_crypto.py encode-draw --colored path --target Draw/...
```

- [ ] **Step 3: Встроить в `decrypt`**

Для `Draw/*.png`: после AES → index в `.index/`, colored в `Draw/`.

- [ ] **Step 4: Тест roundtrip**

```bash
python -m pytest tools/test_draw_color_codec.py -v
```

Минимальный тест:

```python
def test_roundtrip_sample(tmp_path):
    index_bytes = Path("tools/testdata/sample_draw_index.png").read_bytes()
    lut = load_lut(Path("tools/assets/drawing_color_lut.png"))
    colored = index_png_to_rgba_png(index_bytes, lut)
    roundtrip = rgba_png_to_index_png(colored, lut)
    assert sha256(roundtrip) == sha256(index_bytes)
```

---

### Task 7: UI / UX

**Files:**
- Modify: `buddy/PicturesDecryptFeature.cs`
- Modify: `buddy/LocalizationManager.cs`

- [ ] **Step 1: Подсказка в Pictures tab**

Текст: «Draw: edit `Draw/*.png`; index copies in `Draw/.index/`».

- [ ] **Step 2: Changed files list**

Показывать только `draw-colored` и обычные photo — не `.index/`.

- [ ] **Step 3: Опциональный toggle «Keep grayscale index only»**

Для отладки; по умолчанию off.

---

### Task 8: Документация

**Files:**
- Modify: `docs/FEATURES.md` (краткий абзац про Draw LUT)
- Modify: `tools/screen_capture_crypto.py` docstring

- [ ] Описать формат, пути, ограничение: цвета только из палитры игры (post-edit quantization).

---

## Риски и ограничения

| Риск | Митигация |
|------|-----------|
| LUT изменится в патче игры | `LutSha256` в manifest; fallback png в repo; предупреждение в UI |
| Правка colored PNG цветом вне палитры | `FindClosestLutIndex` — lossy; предупредить пользователя |
| Большие Draw файлы | Конвертация O(w×h), приемлемо для canvas ≤ несколько сотен px |
| `GetIndex(124)` в `DrawingPanel` vs `& 0x7F` в `PaintingDetailWidget` | Для decode использовать логику `PaintingDetailWidget` + `IsPixelTransparent` (`& 0x80`) |
| Индекс в G-канале для ARGB32 | На диске после decrypt — RGB с R=index; использовать **R** |

## Порядок реализации (рекомендуемый)

1. Task 1 (LUT dump)
2. Task 2 (кодек C#)
3. Task 3 (manifest v2)
4. Task 4 (decrypt)
5. Task 5 (encrypt-changed)
6. Task 6 (Python)
7. Task 7–8 (UI, docs)

## Критерии готовности

- [ ] Decrypted `Draw/*.png` открываются цветными в стандартном просмотрщике
- [ ] Roundtrip без редактирования: index SHA256 не меняется
- [ ] После редактирования colored + encrypt changed игра показывает обновлённый рисунок
- [ ] Python `decrypt` даёт тот же результат, что mod
- [ ] `dotnet build` без ошибок
