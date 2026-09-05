using UnityEngine;

/// <summary>
/// 让物体持续、平滑地绕 Y 轴旋转，用于模型自动展示。
/// </summary>
public class SimpleAutoRotate : MonoBehaviour
{
    [Tooltip("每秒旋转的角度。正数和负数代表相反方向。")]
    [SerializeField] private float degreesPerSecond = 20f;

    [Tooltip("勾选后绕世界 Y 轴旋转；不勾选则绕物体自身 Y 轴旋转。")]
    [SerializeField] private bool useWorldYAxis = false;

    private void Update()
    {
        Space rotationSpace = useWorldYAxis ? Space.World : Space.Self;
        transform.Rotate(0f, degreesPerSecond * Time.deltaTime, 0f, rotationSpace);
    }
}
