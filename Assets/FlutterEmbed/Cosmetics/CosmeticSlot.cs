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

    [Tooltip("The Transform on the character rig where cosmetics for this slot are instantiated and parented.")]
    public Transform attachmentPoint;

    [Header("Default Placement")]

    [Tooltip("Local position offset applied to cosmetic prefabs attached to this slot.")]
    public Vector3 defaultOffset = Vector3.zero;

    [Tooltip("Local rotation offset (Euler angles) applied to cosmetic prefabs attached to this slot.")]
    public Vector3 defaultRotation = Vector3.zero;

    [Tooltip("Local scale applied to cosmetic prefabs attached to this slot. Defaults to (1,1,1).")]
    public Vector3 defaultScale = Vector3.one;

    /// <summary>
    /// Attaches a cosmetic prefab instance to this slot's attachment point,
    /// applying the default offset, rotation, and scale.
    /// </summary>
    /// <param name="cosmeticInstance">An already-instantiated cosmetic GameObject.</param>
    /// <returns>True if attached successfully; false if attachmentPoint is null.</returns>
    public bool AttachCosmetic(GameObject cosmeticInstance)
    {
        if (attachmentPoint == null)
        {
            Debug.LogWarning($"[CosmeticSlot] Attachment point is null for slot '{category}'. Cannot attach cosmetic.");
            return false;
        }

        if (cosmeticInstance == null)
        {
            Debug.LogWarning($"[CosmeticSlot] Cosmetic instance is null for slot '{category}'.");
            return false;
        }

        cosmeticInstance.transform.SetParent(attachmentPoint, false);
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
        if (attachmentPoint == null) return;

        for (int i = attachmentPoint.childCount - 1; i >= 0; i--)
        {
            var child = attachmentPoint.GetChild(i).gameObject;
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
