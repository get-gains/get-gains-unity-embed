using System;
using UnityEngine;

/// <summary>
/// ScriptableObject that defines the attachment Transform reference for a single cosmetic
/// category slot on the character rig.
/// 
/// Create one asset per category (HEADWEAR, TOP, BOTTOM, ACCESSORY) via
/// Assets → Create → GetGains → Cosmetic Slot.
/// 
/// Each slot stores:
/// - The cosmetic category it represents
/// - A reference to the Transform on the character rig where cosmetics attach
/// - An optional default offset and scale for fine-tuning placement
/// </summary>
[CreateAssetMenu(fileName = "New CosmeticSlot", menuName = "GetGains/Cosmetic Slot")]
public class CosmeticSlot : ScriptableObject
{
    /// <summary>
    /// The four cosmetic categories matching the server-side CosmeticCategory enum.
    /// Values map 1:1 with database enum and Flutter model.
    /// </summary>
    public enum CosmeticCategory
    {
        HEADWEAR,
        TOP,
        BOTTOM,
        ACCESSORY
    }

    [Header("Slot Configuration")]

    [Tooltip("Which cosmetic category this slot handles (HEADWEAR, TOP, BOTTOM, ACCESSORY).")]
    public CosmeticCategory category;

    [Tooltip("The Transform on the character rig where cosmetics for this slot are instantiated and parented. " +
             "Set in the Unity Editor via Tools → GetGains → Complete Cosmetic Scene Setup. " +
             "If null at runtime (scene-to-asset references don't survive serialization), " +
             "the slot falls back to finding a scene object named 'CosmeticSlot_<Category>'.")]
    public Transform attachmentPoint;

    // Cached runtime resolution — avoids repeated GameObject.Find calls.
    [NonSerialized] private Transform _resolvedPoint;

    [Header("Default Placement")]

    [Tooltip("Local position offset applied to cosmetic prefabs attached to this slot.")]
    public Vector3 defaultOffset = Vector3.zero;

    [Tooltip("Local rotation offset (Euler angles) applied to cosmetic prefabs attached to this slot.")]
    public Vector3 defaultRotation = Vector3.zero;

    [Tooltip("Local scale applied to cosmetic prefabs attached to this slot. Defaults to (1,1,1).")]
    public Vector3 defaultScale = Vector3.one;

    /// <summary>
    /// Returns the attachment point Transform to use at runtime.
    /// Prefers the serialized <see cref="attachmentPoint"/> (set in the Editor);
    /// falls back to a scene search for "CosmeticSlot_{category}" because
    /// Unity strips scene-object references from ScriptableObject assets on save.
    /// The result is cached so <c>GameObject.Find</c> is called at most once per slot.
    /// </summary>
    private Transform GetOrFindAttachmentPoint()
    {
        if (_resolvedPoint != null) return _resolvedPoint;
        if (attachmentPoint != null) { _resolvedPoint = attachmentPoint; return _resolvedPoint; }

        // Scene anchors use title-case names set by CosmeticCompleteSetup:
        // "CosmeticSlot_Headwear", "CosmeticSlot_Top", etc.
        // category.ToString() produces ALL_CAPS enum names, so convert to title case.
        string enumStr   = category.ToString(); // e.g. "HEADWEAR"
        string titleCase = char.ToUpperInvariant(enumStr[0]) + enumStr.Substring(1).ToLowerInvariant();
        string anchorName = $"CosmeticSlot_{titleCase}"; // → "CosmeticSlot_Headwear"
        var go = GameObject.Find(anchorName);
        if (go != null)
        {
            _resolvedPoint = go.transform;
            Debug.Log($"[CosmeticSlot] Resolved attachment point '{anchorName}' via scene search for slot '{category}'.");
        }
        else
        {
            Debug.LogWarning($"[CosmeticSlot] '{anchorName}' not found in scene for slot '{category}'. " +
                             "Expected a child GameObject named exactly '{anchorName}' on the rig. " +
                             "Re-run Tools → GetGains → Complete Cosmetic Scene Setup to recreate anchors.");
        }
        return _resolvedPoint;
    }

    /// <summary>
    /// Attaches a cosmetic prefab instance to this slot's attachment point,
    /// applying the default offset, rotation, and scale.
    /// </summary>
    /// <param name="cosmeticInstance">An already-instantiated cosmetic GameObject.</param>
    /// <returns>True if attached successfully; false if no attachment point could be resolved.</returns>
    public bool AttachCosmetic(GameObject cosmeticInstance)
    {
        var point = GetOrFindAttachmentPoint();
        if (point == null)
        {
            Debug.LogWarning($"[CosmeticSlot] No attachment point for slot '{category}'. Cannot attach cosmetic.");
            return false;
        }

        if (cosmeticInstance == null)
        {
            Debug.LogWarning($"[CosmeticSlot] Cosmetic instance is null for slot '{category}'.");
            return false;
        }

        cosmeticInstance.transform.SetParent(point, false);
        cosmeticInstance.transform.localPosition = defaultOffset;
        cosmeticInstance.transform.localRotation = Quaternion.Euler(defaultRotation);
        cosmeticInstance.transform.localScale = defaultScale;

        return true;
    }

    /// <summary>
    /// Removes all child GameObjects from this slot's attachment point.
    /// Used when clearing cosmetics or swapping items.
    /// </summary>
    public void ClearSlot()
    {
        var point = GetOrFindAttachmentPoint();
        if (point == null) return;

        for (int i = point.childCount - 1; i >= 0; i--)
        {
            var child = point.GetChild(i).gameObject;
#if UNITY_EDITOR
            if (!Application.isPlaying)
                DestroyImmediate(child);
            else
#endif
                Destroy(child);
        }
    }

    /// <summary>
    /// Returns the category string matching the server's enum value (e.g., "HEADWEAR").
    /// Used for JSON message parsing from Flutter.
    /// </summary>
    public string CategoryName => category.ToString();
}
