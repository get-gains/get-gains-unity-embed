#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// Creates <c>CosmeticAnchor_Head</c>, <c>CosmeticAnchor_Face</c>, and <c>CosmeticAnchor_HatTop</c>
/// under the humanoid Head bone (for manual scene setup or rigs without runtime-created anchors).
/// </summary>
public static class CosmeticCompleteSetup
{
    [MenuItem("Flutter Embed/Cosmetics/Create cosmetic anchors on Humanoid Head")]
    [MenuItem("Flutter Embed/Cosmetics/Legacy: run complete cosmetic setup (same as Create anchors)")]
    public static void Run()
    {
        foreach (var driver in Object.FindObjectsByType<HumanoidPoseDriver>(FindObjectsInactive.Include))
        {
            var anim = driver.GetComponent<Animator>();
            if (anim == null) continue;
            var head = anim.GetBoneTransform(HumanBodyBones.Head);
            if (head == null) continue;

            FindOrCreate(head, "CosmeticAnchor_Head", Vector3.zero, Quaternion.identity);
            FindOrCreate(head, "CosmeticAnchor_Face", new Vector3(0f, 0.06f, 0.08f), Quaternion.identity);
            FindOrCreate(head, "CosmeticAnchor_HatTop", new Vector3(0f, 0.11f, 0f), Quaternion.identity);
        }

        Debug.Log("[CosmeticCompleteSetup] Cosmetic anchor transforms ensured under Humanoid Head bones.");
    }

    private static void FindOrCreate(Transform headBone, string name, Vector3 localPos, Quaternion localRot)
    {
        var existing = headBone.Find(name);
        if (existing != null) return;
        var go = new GameObject(name);
        go.transform.SetParent(headBone, false);
        go.transform.localPosition = localPos;
        go.transform.localRotation = localRot;
        go.transform.localScale = Vector3.one;
        Undo.RegisterCreatedObjectUndo(go, "Create cosmetic anchor");
    }
}
#endif
