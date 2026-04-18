using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Manages cosmetic items on the character rig. Handles loading equipped cosmetics,
/// previewing shop items, and clearing previews.
///
/// Attach to the character rig root (same GameObject as HumanoidPoseDriver or parent).
/// Assign CosmeticSlot ScriptableObjects for each category in the Inspector.
///
/// Prefabs are loaded from Resources at:
///   FlutterEmbed/Cosmetics/CosmeticAssets/{Category}/{assetRef}
///
/// Messages arrive from FlutterUnityBridge → delegates to this manager.
/// Sends confirmation events back to Flutter via SendToFlutter.
/// </summary>
public class CosmeticManager : MonoBehaviour
{
    [Header("Cosmetic Slots")]
    [Tooltip("Assign one CosmeticSlot ScriptableObject per category.")]
    [SerializeField] private CosmeticSlot[] slots;

    // ── Runtime State ──

    /// <summary>Category → CosmeticSlot lookup built from the serialized array.</summary>
    private Dictionary<CosmeticSlot.CosmeticCategory, CosmeticSlot> _slotMap;

    /// <summary>Currently equipped assetRef per category (authoritative state from server).</summary>
    private Dictionary<CosmeticSlot.CosmeticCategory, string> _equippedState
        = new Dictionary<CosmeticSlot.CosmeticCategory, string>();

    /// <summary>Saved state before a preview so we can restore it.</summary>
    private Dictionary<CosmeticSlot.CosmeticCategory, string> _previewBaseline;

    /// <summary>Whether we are in preview mode.</summary>
    private bool _isPreviewing;

    // ── JSON Payload Types ──

    [Serializable]
    private class CosmeticEntry
    {
        public string category;
        public string assetRef;
    }

    [Serializable]
    private class LoadCosmeticsPayload
    {
        public CosmeticEntry[] cosmetics;
    }

    [Serializable]
    private class PreviewPayload
    {
        public string category;
        public string assetRef;
        public bool showOnly;
    }

    // ── Lifecycle ──

    private void Awake()
    {
        BuildSlotMap();
    }

    private void BuildSlotMap()
    {
        _slotMap = new Dictionary<CosmeticSlot.CosmeticCategory, CosmeticSlot>();
        if (slots == null) return;

        foreach (var slot in slots)
        {
            if (slot == null) continue;
            if (_slotMap.ContainsKey(slot.category))
            {
                Debug.LogWarning($"[CosmeticManager] Duplicate slot for category '{slot.category}'. Using first.");
                continue;
            }
            _slotMap[slot.category] = slot;
        }

        Debug.Log($"[CosmeticManager] Initialized with {_slotMap.Count} slots.");
    }

    // ── Public API (called by FlutterUnityBridge) ──

    /// <summary>
    /// Load and apply all equipped cosmetics. Clears any previous cosmetics first.
    /// Called on app startup and after equip/unequip actions.
    /// </summary>
    /// <param name="json">JSON string matching LoadCosmeticsPayload schema.</param>
    public void LoadCosmetics(string json)
    {
        _isPreviewing = false;
        _previewBaseline = null;

        // Clear all slots first
        ClearAllSlots();
        _equippedState.Clear();

        if (string.IsNullOrEmpty(json))
        {
            Debug.Log("[CosmeticManager] LoadCosmetics: empty payload, default appearance.");
            SendToFlutter.Send("cosmetics_loaded");
            return;
        }

        LoadCosmeticsPayload payload;
        try
        {
            payload = JsonUtility.FromJson<LoadCosmeticsPayload>(json);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[CosmeticManager] LoadCosmetics: failed to parse JSON — {ex.Message}");
            SendToFlutter.Send("cosmetics_loaded");
            return;
        }

        if (payload?.cosmetics == null || payload.cosmetics.Length == 0)
        {
            Debug.Log("[CosmeticManager] LoadCosmetics: no cosmetics in payload, default appearance.");
            SendToFlutter.Send("cosmetics_loaded");
            return;
        }

        foreach (var entry in payload.cosmetics)
        {
            ApplyCosmeticEntry(entry);
        }

        Debug.Log($"[CosmeticManager] LoadCosmetics: applied {payload.cosmetics.Length} cosmetic(s).");
        SendToFlutter.Send("cosmetics_loaded");
    }

    /// <summary>
    /// Temporarily preview a cosmetic item. Saves current state as baseline.
    /// If showOnly is true, hides all other slots to focus on the preview item.
    /// </summary>
    /// <param name="json">JSON string matching PreviewPayload schema.</param>
    public void PreviewCosmetic(string json)
    {
        if (string.IsNullOrEmpty(json))
        {
            Debug.LogWarning("[CosmeticManager] PreviewCosmetic: empty payload.");
            return;
        }

        PreviewPayload payload;
        try
        {
            payload = JsonUtility.FromJson<PreviewPayload>(json);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[CosmeticManager] PreviewCosmetic: failed to parse JSON — {ex.Message}");
            return;
        }

        if (!TryParseCategory(payload.category, out var category))
        {
            Debug.LogWarning($"[CosmeticManager] PreviewCosmetic: unknown category '{payload.category}'.");
            return;
        }

        // Save baseline on first preview
        if (!_isPreviewing)
        {
            _previewBaseline = new Dictionary<CosmeticSlot.CosmeticCategory, string>(_equippedState);
            _isPreviewing = true;
        }

        if (payload.showOnly)
        {
            // Hide all slots except the preview category
            foreach (var kvp in _slotMap)
            {
                if (kvp.Key != category)
                    kvp.Value.ClearSlot();
            }
        }

        // Apply the preview cosmetic in its slot
        if (_slotMap.TryGetValue(category, out var slot))
        {
            slot.ClearSlot();
            LoadAndAttachPrefab(category, payload.assetRef, slot);
        }
        else
        {
            Debug.LogWarning($"[CosmeticManager] PreviewCosmetic: no slot configured for '{category}'.");
        }

        Debug.Log($"[CosmeticManager] PreviewCosmetic: previewing '{payload.assetRef}' in slot '{category}' (showOnly={payload.showOnly}).");
        SendToFlutter.Send("cosmetic_preview_ready");
    }

    /// <summary>
    /// Clear the preview and restore the actual equipped state.
    /// </summary>
    public void ClearPreview()
    {
        if (!_isPreviewing || _previewBaseline == null)
        {
            Debug.Log("[CosmeticManager] ClearPreview: not in preview mode, nothing to restore.");
            SendToFlutter.Send("cosmetics_loaded");
            return;
        }

        _isPreviewing = false;

        // Clear all slots and re-apply baseline
        ClearAllSlots();
        _equippedState.Clear();

        foreach (var kvp in _previewBaseline)
        {
            var entry = new CosmeticEntry { category = kvp.Key.ToString(), assetRef = kvp.Value };
            ApplyCosmeticEntry(entry);
        }

        _previewBaseline = null;

        Debug.Log("[CosmeticManager] ClearPreview: restored equipped state.");
        SendToFlutter.Send("cosmetics_loaded");
    }

    // ── Internals ──

    private void ApplyCosmeticEntry(CosmeticEntry entry)
    {
        if (entry == null || string.IsNullOrEmpty(entry.category) || string.IsNullOrEmpty(entry.assetRef))
        {
            Debug.LogWarning("[CosmeticManager] Skipping null/empty cosmetic entry.");
            return;
        }

        if (!TryParseCategory(entry.category, out var category))
        {
            Debug.LogWarning($"[CosmeticManager] Unknown category '{entry.category}', skipping.");
            return;
        }

        if (!_slotMap.TryGetValue(category, out var slot))
        {
            Debug.LogWarning($"[CosmeticManager] No slot configured for category '{category}', skipping.");
            return;
        }

        // Clear existing cosmetic in this slot before applying new one
        slot.ClearSlot();

        if (LoadAndAttachPrefab(category, entry.assetRef, slot))
        {
            _equippedState[category] = entry.assetRef;
        }
    }

    /// <summary>
    /// Loads a prefab from Resources and attaches it to the slot.
    /// Prefab path: FlutterEmbed/Cosmetics/CosmeticAssets/{Category}/{assetRef}
    /// </summary>
    /// <returns>True if successfully loaded and attached.</returns>
    private bool LoadAndAttachPrefab(CosmeticSlot.CosmeticCategory category, string assetRef, CosmeticSlot slot)
    {
        string categoryFolder = GetCategoryFolder(category);
        string resourcePath = $"FlutterEmbed/Cosmetics/CosmeticAssets/{categoryFolder}/{assetRef}";

        Debug.Log($"[CosmeticManager] Loading prefab at '{resourcePath}'");
        var prefab = Resources.Load<GameObject>(resourcePath);
        if (prefab == null)
        {
            Debug.LogWarning($"[CosmeticManager] Prefab NOT FOUND at '{resourcePath}'. " +
                             $"Check: (1) file exists under Resources/{resourcePath}, " +
                             $"(2) assetRef casing matches folder name, " +
                             $"(3) category folder maps correctly (categoryFolder='{categoryFolder}').");
            return false;
        }

        Debug.Log($"[CosmeticManager] Prefab found '{assetRef}' — instantiating and attaching to slot '{category}'.");
        var instance = Instantiate(prefab);
        instance.name = assetRef;

        if (!slot.AttachCosmetic(instance))
        {
            Debug.LogWarning($"[CosmeticManager] AttachCosmetic FAILED for '{assetRef}' in slot '{category}'. " +
                             $"Check: (1) slot has a valid anchor bone assigned, " +
                             $"(2) anchor bone exists on the active rig.");
            Destroy(instance);
            return false;
        }

        Debug.Log($"[CosmeticManager] '{assetRef}' attached successfully to slot '{category}'.");
        return true;
    }

    private void ClearAllSlots()
    {
        if (_slotMap == null) return;
        foreach (var kvp in _slotMap)
        {
            kvp.Value.ClearSlot();
        }
    }

    private static bool TryParseCategory(string value, out CosmeticSlot.CosmeticCategory category)
    {
        if (Enum.TryParse(value, true, out category))
            return true;

        category = default;
        return false;
    }

    /// <summary>
    /// Maps category enum to the subfolder name under CosmeticAssets/.
    /// </summary>
    private static string GetCategoryFolder(CosmeticSlot.CosmeticCategory category)
    {
        switch (category)
        {
            case CosmeticSlot.CosmeticCategory.HEADWEAR:  return "Headwear";
            case CosmeticSlot.CosmeticCategory.TOP:       return "Top";
            case CosmeticSlot.CosmeticCategory.BOTTOM:    return "Bottom";
            case CosmeticSlot.CosmeticCategory.ACCESSORY: return "Accessory";
            default: return category.ToString();
        }
    }
}
