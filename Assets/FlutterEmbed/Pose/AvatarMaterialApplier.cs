using UnityEngine;

namespace FlutterEmbed
{
    /// <summary>
    /// Replaces every Renderer material under this GameObject with a shared avatar material.
    /// Attach to the character model root so the workout/cosmetic views always use the same look.
    /// </summary>
    public class AvatarMaterialApplier : MonoBehaviour
    {
        [SerializeField] private Material material;
        [SerializeField] private bool includeInactive = true;

        private void Start() => Apply();

        /// <summary>Assigns <see cref="material"/> to all child renderers.</summary>
        public void Apply()
        {
            if (material == null) return;

            foreach (var renderer in GetComponentsInChildren<Renderer>(includeInactive))
                renderer.sharedMaterial = material;
        }
    }
}
