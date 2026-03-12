using UnityEngine;
using UnityEditor;

/// <summary>
/// Editor utility to set up cosmetic attachment point Transforms on a character rig.
///
/// Usage:
///   1. Select the character rig root GameObject (e.g., HumanBasemesh) in the Hierarchy.
///   2. Click Tools → GetGains → Setup Cosmetic Attachment Points.
///   3. The script creates child GameObjects for each slot and wires them to
///      the correct bones via the Animator's avatar humanoid mapping.
///
/// Attachment points created:
///   - HEADWEAR → Head bone child ("CosmeticSlot_Headwear")
///   - TOP → Chest/UpperChest bone child ("CosmeticSlot_Top")
///   - BOTTOM → Hips bone child ("CosmeticSlot_Bottom")
///   - ACCESSORY → RightHand bone child ("CosmeticSlot_Accessory")
///
/// If points already exist (by name), they are reused rather than duplicated.
/// </summary>
public class CosmeticSlotSetup : EditorWindow
{
    private struct SlotDefinition
    {
        public string Name;
        public CosmeticSlot.CosmeticCategory Category;
        public HumanBodyBones Bone;
        public Vector3 LocalOffset;
    }

    private static readonly SlotDefinition[] SlotDefinitions = new[]
    {
        new SlotDefinition
        {
            Name = "CosmeticSlot_Headwear",
            Category = CosmeticSlot.CosmeticCategory.HEADWEAR,
            Bone = HumanBodyBones.Head,
            LocalOffset = new Vector3(0f, 0.15f, 0f) // Slightly above head
        },
        new SlotDefinition
        {
            Name = "CosmeticSlot_Top",
            Category = CosmeticSlot.CosmeticCategory.TOP,
            Bone = HumanBodyBones.UpperChest,
            LocalOffset = Vector3.zero
        },
        new SlotDefinition
        {
            Name = "CosmeticSlot_Bottom",
            Category = CosmeticSlot.CosmeticCategory.BOTTOM,
            Bone = HumanBodyBones.Hips,
            LocalOffset = Vector3.zero
        },
        new SlotDefinition
        {
            Name = "CosmeticSlot_Accessory",
            Category = CosmeticSlot.CosmeticCategory.ACCESSORY,
            Bone = HumanBodyBones.RightHand,
            LocalOffset = Vector3.zero
        }
    };

    [MenuItem("Tools/GetGains/Setup Cosmetic Attachment Points")]
    public static void SetupAttachmentPoints()
    {
        var selected = Selection.activeGameObject;
        if (selected == null)
        {
            EditorUtility.DisplayDialog(
                "Setup Cosmetic Slots",
                "Please select the character rig root GameObject (with Animator) in the Hierarchy.",
                "OK");
            return;
        }

        var animator = selected.GetComponent<Animator>();
        if (animator == null || animator.avatar == null || !animator.avatar.isHuman)
        {
            EditorUtility.DisplayDialog(
                "Setup Cosmetic Slots",
                "The selected GameObject must have an Animator with a valid Humanoid Avatar assigned.",
                "OK");
            return;
        }

        Undo.SetCurrentGroupName("Setup Cosmetic Attachment Points");
        int undoGroup = Undo.GetCurrentGroup();

        int created = 0;
        int reused = 0;

        foreach (var def in SlotDefinitions)
        {
            Transform boneTransform = animator.GetBoneTransform(def.Bone);
            if (boneTransform == null)
            {
                // Fallback: try UpperChest → Chest → Spine for TOP
                if (def.Bone == HumanBodyBones.UpperChest)
                {
                    boneTransform = animator.GetBoneTransform(HumanBodyBones.Chest);
                    if (boneTransform == null)
                        boneTransform = animator.GetBoneTransform(HumanBodyBones.Spine);
                }

                if (boneTransform == null)
                {
                    Debug.LogWarning($"[CosmeticSlotSetup] Bone '{def.Bone}' not found on '{selected.name}'. Skipping slot '{def.Name}'.");
                    continue;
                }
            }

            // Check if attachment point already exists
            Transform existing = boneTransform.Find(def.Name);
            if (existing != null)
            {
                reused++;
                Debug.Log($"[CosmeticSlotSetup] Reusing existing attachment point '{def.Name}' on '{boneTransform.name}'.");
                continue;
            }

            // Create a new empty GameObject as the attachment point
            var slotGo = new GameObject(def.Name);
            Undo.RegisterCreatedObjectUndo(slotGo, $"Create {def.Name}");
            slotGo.transform.SetParent(boneTransform, false);
            slotGo.transform.localPosition = def.LocalOffset;
            slotGo.transform.localRotation = Quaternion.identity;
            slotGo.transform.localScale = Vector3.one;

            created++;
            Debug.Log($"[CosmeticSlotSetup] Created attachment point '{def.Name}' as child of '{boneTransform.name}' (offset: {def.LocalOffset}).");
        }

        Undo.CollapseUndoOperations(undoGroup);

        EditorUtility.DisplayDialog(
            "Setup Cosmetic Slots",
            $"Done! Created {created} new attachment point(s), reused {reused} existing.\n\n" +
            "Next steps:\n" +
            "1. Create CosmeticSlot ScriptableObjects (Assets → Create → GetGains → Cosmetic Slot)\n" +
            "2. Assign the attachment point Transforms to each slot asset\n" +
            "3. Assign the slot assets to the CosmeticManager component",
            "OK");
    }

    [MenuItem("Tools/GetGains/Setup Cosmetic Attachment Points", true)]
    private static bool ValidateSetupAttachmentPoints()
    {
        return Selection.activeGameObject != null;
    }
}
