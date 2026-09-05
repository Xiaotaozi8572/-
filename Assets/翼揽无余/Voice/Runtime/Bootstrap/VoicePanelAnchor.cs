using UnityEngine;

namespace Yilan.Voice.Runtime.UI
{
    [DisallowMultipleComponent]
    public sealed class VoicePanelAnchor : MonoBehaviour
    {
        [Header("Scene visibility")]
        [Tooltip("Show the persistent voice assistant UI in this scene.")]
        [SerializeField]
        private bool showAssistant = true;

        [SerializeField]
        private Vector2 panelSize = new Vector2(1486.5f, 592f);

        public bool ShowAssistant => showAssistant;
        public Vector2 PanelSize => panelSize;

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.cyan;
            Gizmos.matrix = transform.localToWorldMatrix;

            Gizmos.DrawWireCube(
                Vector3.zero,
                new Vector3(panelSize.x, panelSize.y, 1f));
        }
    }
}
