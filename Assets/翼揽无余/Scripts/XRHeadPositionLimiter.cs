using UnityEngine;

/// <summary>
/// Keeps the tracked head inside a cockpit-sized box by moving the XR Origin.
/// Head rotation is never modified, so normal headset look-around remains intact.
/// </summary>
public sealed class XRHeadPositionLimiter : MonoBehaviour
{
    [SerializeField] private Transform xrOriginRoot;
    [SerializeField] private Transform headCamera;
    [Tooltip("A transform placed at the center of the allowed head area. Keep it outside the XR Origin hierarchy.")]
    [SerializeField] private Transform boundaryCenter;
    [SerializeField] private Vector3 halfExtents = new Vector3(0.35f, 0.3f, 0.35f);
    [SerializeField] private bool constrainVerticalPosition;

    private void LateUpdate()
    {
        if (xrOriginRoot == null || headCamera == null || boundaryCenter == null)
            return;

        Vector3 localHead = boundaryCenter.InverseTransformPoint(headCamera.position);
        Vector3 clamped = localHead;

        clamped.x = Mathf.Clamp(localHead.x, -halfExtents.x, halfExtents.x);
        clamped.z = Mathf.Clamp(localHead.z, -halfExtents.z, halfExtents.z);

        if (constrainVerticalPosition)
            clamped.y = Mathf.Clamp(localHead.y, -halfExtents.y, halfExtents.y);

        Vector3 localCorrection = clamped - localHead;
        xrOriginRoot.position += boundaryCenter.TransformVector(localCorrection);
    }

    private void OnDrawGizmosSelected()
    {
        if (boundaryCenter == null)
            return;

        Matrix4x4 oldMatrix = Gizmos.matrix;
        Gizmos.matrix = boundaryCenter.localToWorldMatrix;
        Gizmos.color = new Color(0f, 0.8f, 1f, 0.8f);
        Gizmos.DrawWireCube(Vector3.zero, halfExtents * 2f);
        Gizmos.matrix = oldMatrix;
    }
}
