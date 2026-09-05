using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 将由多个独立网格组成的飞机，按照零件相对整机中心的方向展开或还原。
/// </summary>
public class AircraftExplodedViewController : MonoBehaviour
{
    [Header("模型")]
    [Tooltip("包含所有独立零件的根物体。不填写时使用当前物体。")]
    [SerializeField] private Transform partsRoot;

    [Header("爆炸效果")]
    [Min(0f)]
    [SerializeField] private float explosionDistance = 0.45f;

    [Min(0.01f)]
    [SerializeField] private float animationDuration = 2f;

    [Tooltip("控制模型局部 X/Y/Z 三个方向的展开幅度。")]
    [SerializeField] private Vector3 axisMultiplier = new Vector3(1.2f, 0.65f, 1f);

    [Tooltip("让不同零件稍微错开，避免所有零件形成完全相同的外轮廓。")]
    [Min(0f)]
    [SerializeField] private float sizeInfluence = 0.15f;

    [Header("状态（只读）")]
    [SerializeField] private bool isExploded;

    private readonly List<PartState> parts = new List<PartState>();
    private Coroutine animationCoroutine;

    private sealed class PartState
    {
        public Transform Transform;
        public Vector3 AssembledLocalPosition;
        public Vector3 ExplodedLocalPosition;
    }

    private void Awake()
    {
        if (partsRoot == null)
            partsRoot = transform;

        CacheParts();
    }

    /// <summary>播放拆解动画。可直接绑定到 Button 的 OnClick。</summary>
    public void Explode()
    {
        AnimateTo(true);
    }

    /// <summary>播放还原动画。可直接绑定到 Button 的 OnClick。</summary>
    public void Assemble()
    {
        AnimateTo(false);
    }

    /// <summary>在拆解与还原之间切换。可直接绑定到单个 Button。</summary>
    public void ToggleExplosion()
    {
        AnimateTo(!isExploded);
    }

    /// <summary>模型被重新编辑后，可调用此方法重新记录完整位置。</summary>
    public void RefreshParts()
    {
        if (animationCoroutine != null)
        {
            StopCoroutine(animationCoroutine);
            animationCoroutine = null;
        }

        isExploded = false;
        CacheParts();
    }

    private void CacheParts()
    {
        parts.Clear();

        Renderer[] renderers = partsRoot.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
        {
            Debug.LogError("爆炸图控制器没有在 Parts Root 下找到任何 Renderer。", this);
            return;
        }

        Bounds totalBounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            totalBounds.Encapsulate(renderers[i].bounds);

        Vector3 aircraftCenterLocal = partsRoot.InverseTransformPoint(totalBounds.center);
        HashSet<Transform> recordedTransforms = new HashSet<Transform>();

        for (int i = 0; i < renderers.Length; i++)
        {
            Transform part = renderers[i].transform;
            if (part == partsRoot || !recordedTransforms.Add(part))
                continue;

            Vector3 partCenterLocal = partsRoot.InverseTransformPoint(renderers[i].bounds.center);
            Vector3 direction = Vector3.Scale(partCenterLocal - aircraftCenterLocal, axisMultiplier);

            if (direction.sqrMagnitude < 0.000001f)
            {
                // 极少数位于整机中心的零件，给予稳定的向上偏移方向。
                direction = Vector3.up;
            }
            else
            {
                direction.Normalize();
            }

            float largestPartSize = Mathf.Max(
                renderers[i].bounds.size.x,
                renderers[i].bounds.size.y,
                renderers[i].bounds.size.z);
            float distance = explosionDistance + largestPartSize * sizeInfluence;

            // 将 Parts Root 局部方向换算到零件父物体的局部坐标。
            Vector3 worldOffset = partsRoot.TransformVector(direction * distance);
            Vector3 parentLocalOffset = part.parent != null
                ? part.parent.InverseTransformVector(worldOffset)
                : worldOffset;

            parts.Add(new PartState
            {
                Transform = part,
                AssembledLocalPosition = part.localPosition,
                ExplodedLocalPosition = part.localPosition + parentLocalOffset
            });
        }

        Debug.Log($"爆炸图控制器已记录 {parts.Count} 个可移动零件。", this);
    }

    private void AnimateTo(bool exploded)
    {
        if (parts.Count == 0)
        {
            Debug.LogWarning("没有可播放动画的飞机零件。", this);
            return;
        }

        if (animationCoroutine != null)
            StopCoroutine(animationCoroutine);

        animationCoroutine = StartCoroutine(AnimateParts(exploded));
    }

    private IEnumerator AnimateParts(bool exploded)
    {
        Vector3[] startPositions = new Vector3[parts.Count];
        for (int i = 0; i < parts.Count; i++)
            startPositions[i] = parts[i].Transform.localPosition;

        float elapsed = 0f;
        while (elapsed < animationDuration)
        {
            elapsed += Time.deltaTime;
            float normalizedTime = Mathf.Clamp01(elapsed / animationDuration);
            float easedTime = normalizedTime * normalizedTime * (3f - 2f * normalizedTime);

            for (int i = 0; i < parts.Count; i++)
            {
                Vector3 target = exploded
                    ? parts[i].ExplodedLocalPosition
                    : parts[i].AssembledLocalPosition;
                parts[i].Transform.localPosition = Vector3.LerpUnclamped(
                    startPositions[i], target, easedTime);
            }

            yield return null;
        }

        for (int i = 0; i < parts.Count; i++)
        {
            parts[i].Transform.localPosition = exploded
                ? parts[i].ExplodedLocalPosition
                : parts[i].AssembledLocalPosition;
        }

        isExploded = exploded;
        animationCoroutine = null;
    }
}
