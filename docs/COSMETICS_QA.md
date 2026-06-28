# 3D Cosmetics — Visual QA Checklist

Use this when tuning or reviewing head cosmetic prefabs under  
`Assets/Resources/FlutterEmbed/Cosmetics/CosmeticAssets/`.

Unity loads at most **three** items, prioritized: **HEADWEAR (hats)** → **facewear (glasses)** → **other head (eyes, etc.)**.

## Per prefab

| Prefab | anchorKind | Shop preview | Workout / form Unity | Squat playback | Overhead playback | Screenshot |
|--------|------------|--------------|----------------------|----------------|-------------------|------------|
| `beanie_get_gains` | HatTop (2) | | | | | |
| `cowboy_hat_get_gains` | HatTop (2) | | | | | |
| `glasses_get_gains` | Facewear (1) | | | | | |
| `2016_gamer_glasses_get_gains` | Facewear (1) | | | | | |
| `eye_cosmetic_get_gains` | Head (0) | | | | | |
| `placeholder_hat` | HatTop (2) | | | | | |
| `placeholder_facewear` | Facewear (1) | | | | | |
| `placeholder_full_head` | Head (0) | | | | | |

## Tuning notes

- `CosmeticManager.TryAttach` applies `CosmeticAttachment.localOffset`, `localRotation`, and `localScale` on the prefab root after parenting to head anchors.
- Fine mesh placement is usually on the **MeshHolder** child transform inside each prefab.
- Facewear anchor: `CosmeticAnchor_Face` (+0.06 Y, +0.08 Z from head). Hat top: `CosmeticAnchor_HatTop` (+0.11 Y).

## Regression

- [ ] Equip hat + glasses + eye cosmetic → all three visible in wardrobe and workout
- [ ] Equip four slots in app → Unity shows three by priority (not arbitrary DB order)
- [ ] Preview single item in shop → `PreviewCosmetic` aligns with equipped view
