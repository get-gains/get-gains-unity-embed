# Cosmetics System

This directory contains the cosmetic system for character customization.

## Structure

- `CosmeticManager.cs` - MonoBehaviour managing loading/applying/previewing cosmetics on the character rig
- `CosmeticSlot.cs` - ScriptableObject defining attachment Transform per category
- `CosmeticAssets/` - Prefab directories organized by category
  - `Headwear/` - Hats, helmets, headbands
  - `Top/` - Shirts, tanks, hoodies
  - `Bottom/` - Shorts, pants, skirts
  - `Accessory/` - Wristbands, gloves, watches

## Setup Instructions

### 1. Create Attachment Points on Character Rig

Select the character rig root (e.g., HumanBasemesh) in the Hierarchy, then run:
**Tools → GetGains → Setup Cosmetic Attachment Points**

This creates empty child GameObjects on the correct bones:

- `CosmeticSlot_Headwear` → Head bone
- `CosmeticSlot_Top` → UpperChest bone
- `CosmeticSlot_Bottom` → Hips bone
- `CosmeticSlot_Accessory` → RightHand bone

### 2. Create CosmeticSlot ScriptableObjects

For each category, create an asset via **Assets → Create → GetGains → Cosmetic Slot**.
Assign the matching attachment point Transform from step 1.

### 3. Configure CosmeticManager

Add the `CosmeticManager` component to the character rig root and assign all four CosmeticSlot assets.

### 4. Generate Placeholder Prefabs (Testing)

Run **Tools → GetGains → Generate Placeholder Cosmetic Prefabs** to create simple colored primitives for testing the full pipeline. Replace with real models when available.

## Prefab Naming Convention

Prefabs must be named `{category}_{item_name}` matching `Cosmetic.unityAssetRef` in the database.
Examples: `headwear_flame_headband`, `top_iron_tank`, `bottom_cargo_shorts`, `accessory_wrist_band`

Prefabs are loaded at runtime via `Resources.Load` from:
`Resources/FlutterEmbed/Cosmetics/CosmeticAssets/{Category}/{assetRef}`

## Message Flow (Flutter ↔ Unity)

1. **LoadEquippedCosmetics** — Clears all slots, applies equipped items, sends `cosmetics_loaded`
2. **PreviewCosmetic** — Saves baseline, shows preview item, sends `cosmetic_preview_ready`
3. **ClearPreview** — Restores baseline equipped state, sends `cosmetics_loaded`
