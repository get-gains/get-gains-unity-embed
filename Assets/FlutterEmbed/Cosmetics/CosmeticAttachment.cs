using UnityEngine;

/// <summary>
/// Per-prefab metadata: which head anchor to parent under and optional tuning for camera framing.
/// Placement is driven by the anchor transform and MeshHolder pivot inside this prefab (offsets usually zero).
/// </summary>
public enum CosmeticAnchorKind
{
    Head = 0,
    Facewear = 1,
    HatTop = 2,
}

public class CosmeticAttachment : MonoBehaviour
{
    public CosmeticAnchorKind anchorKind = CosmeticAnchorKind.Head;

    public Vector3 localOffset;
    public Quaternion localRotation = Quaternion.identity;
    public Vector3 localScale = Vector3.one;

    [Tooltip("Extra vertical bias (meters) for cosmetic framing / focus height.")]
    public float focusHeightBias = 0.4f;

    public void ApplyDefaults(CosmeticAnchorKind kind)
    {
        anchorKind = kind;
        localOffset = Vector3.zero;
        localRotation = Quaternion.identity;
        localScale = Vector3.one;
    }

    /// <summary>Guess anchor from server asset id when rebuilding or authoring.</summary>
    public static CosmeticAnchorKind InferAnchorKind(string assetRef)
    {
        if (string.IsNullOrEmpty(assetRef)) return CosmeticAnchorKind.Head;
        var s = assetRef.ToLowerInvariant();
        if (s.Contains("hat") || s.Contains("beanie") || s.Contains("cowboy") || s.Contains("headwear"))
            return CosmeticAnchorKind.HatTop;
        if (s.Contains("glass") || s.Contains("goggle") || s.Contains("facewear"))
            return CosmeticAnchorKind.Facewear;
        if (s.Contains("eye"))
            return CosmeticAnchorKind.Head;
        return CosmeticAnchorKind.Head;
    }
}
