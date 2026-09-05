using UnityEngine;

/// <summary>
/// 让世界空间 UI 始终面向 XR 主摄像机，同时保留其父级锚点带来的位置跟随。
/// </summary>
public sealed class WorldSpaceBillboard : MonoBehaviour
{
    [SerializeField] private Camera targetCamera;
    [SerializeField] private bool keepWorldUp = true;
    [SerializeField] private bool reverseForward;

    private void LateUpdate()
    {
        if (targetCamera == null)
        {
            targetCamera = Camera.main;
        }

        if (targetCamera == null)
        {
            return;
        }

        Vector3 direction = transform.position - targetCamera.transform.position;
        if (reverseForward)
        {
            direction = -direction;
        }

        if (keepWorldUp)
        {
            direction.y = 0f;
        }

        if (direction.sqrMagnitude < 0.0001f)
        {
            return;
        }

        Vector3 up = keepWorldUp ? Vector3.up : targetCamera.transform.up;
        transform.rotation = Quaternion.LookRotation(direction.normalized, up);
    }
}
