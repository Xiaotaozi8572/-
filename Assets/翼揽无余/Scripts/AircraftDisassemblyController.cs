using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 完整飞机与预先拆开的零件模型之间的交互式切换控制器。
/// 支持缩放阈值触发、按钮触发、回弹展开和一键恢复完整飞机。
/// </summary>
public class AircraftDisassemblyController : MonoBehaviour
{
    private enum AircraftState
    {
        Complete,
        Transitioning,
        Exploded
    }

    [Header("必要对象")]
    [SerializeField] private Transform interactionRoot;
    [SerializeField] private GameObject completeAircraft;
    [SerializeField] private GameObject explodedPartsRoot;

    [Header("界面（可不填）")]
    [SerializeField] private GameObject disassembleButton;
    [SerializeField] private GameObject resetButton;

    [Header("闪光（可不填）")]
    [SerializeField] private GameObject transitionFlash;
    [SerializeField] private Vector3 flashMaximumScale = Vector3.one;

    [Header("整体与零件交互（可不填）")]
    [Tooltip("完整飞机或最外层根物体上的 XR Grab Interactable。")]
    [SerializeField] private Behaviour wholeAircraftInteraction;

    [Header("放大触发")]
    [Min(1.01f)]
    [SerializeField] private float scaleTriggerMultiplier = 1.6f;
    [Min(0f)]
    [SerializeField] private float triggerHoldTime = 0.25f;

    [Header("拆解动画")]
    [Range(0f, 1f)]
    [SerializeField] private float compressedPositionRatio = 0.28f;
    [Range(0.05f, 1f)]
    [SerializeField] private float compressedScaleRatio = 0.78f;
    [Min(0.05f)]
    [SerializeField] private float disassemblyDuration = 0.75f;
    [Range(0f, 3f)]
    [SerializeField] private float backOvershoot = 1.35f;

    [Header("复位动画")]
    [Min(0.05f)]
    [SerializeField] private float restorePartsDuration = 0.45f;
    [Min(0.05f)]
    [SerializeField] private float collapseDuration = 0.55f;
    [Min(0.05f)]
    [SerializeField] private float completeAppearDuration = 0.4f;
    [SerializeField] private bool restoreInteractionRootTransform = true;

    private readonly List<PartPose> parts = new List<PartPose>();
    private readonly List<Behaviour> partGrabInteractables = new List<Behaviour>();
    private AircraftState state = AircraftState.Complete;
    private Vector3 initialRootLocalPosition;
    private Quaternion initialRootLocalRotation;
    private Vector3 initialRootLocalScale;
    private Vector3 completeAircraftBaseScale;
    private float scaleHoldTimer;
    private Coroutine activeTransition;

    private sealed class PartPose
    {
        public Transform Transform;
        public Vector3 ExplodedPosition;
        public Quaternion ExplodedRotation;
        public Vector3 ExplodedScale;
        public Vector3 CompressedPosition;
        public Vector3 CompressedScale;
    }

    private void Awake()
    {
        if (interactionRoot == null)
            interactionRoot = transform;

        initialRootLocalPosition = interactionRoot.localPosition;
        initialRootLocalRotation = interactionRoot.localRotation;
        initialRootLocalScale = interactionRoot.localScale;

        if (completeAircraft != null)
            completeAircraftBaseScale = completeAircraft.transform.localScale;

        CacheExplodedParts();
        SetInitialState();
    }

    private void Update()
    {
        if (state != AircraftState.Complete || interactionRoot == null)
            return;

        float scaleRatio = GetScaleRatio();
        if (scaleRatio >= scaleTriggerMultiplier)
        {
            scaleHoldTimer += Time.deltaTime;
            if (scaleHoldTimer >= triggerHoldTime)
                TriggerDisassembly();
        }
        else
        {
            scaleHoldTimer = 0f;
        }
    }

    /// <summary>缩放阈值与“拆解”按钮共同调用的方法。</summary>
    public void TriggerDisassembly()
    {
        if (state != AircraftState.Complete || activeTransition != null)
            return;

        activeTransition = StartCoroutine(DisassemblySequence());
    }

    /// <summary>绑定到“一键复位”按钮，恢复完整飞机。</summary>
    public void ResetToComplete()
    {
        if (state != AircraftState.Exploded || activeTransition != null)
            return;

        activeTransition = StartCoroutine(ResetSequence());
    }

    private void SetInitialState()
    {
        state = AircraftState.Complete;
        scaleHoldTimer = 0f;

        if (completeAircraft != null)
        {
            completeAircraft.SetActive(true);
            completeAircraft.transform.localScale = completeAircraftBaseScale;
        }

        if (explodedPartsRoot != null)
            explodedPartsRoot.SetActive(false);

        SetWholeInteractionEnabled(true);
        SetPartInteractionsEnabled(false);
        SetUiForExplodedState(false);
        HideFlash();
    }

    private void CacheExplodedParts()
    {
        parts.Clear();
        partGrabInteractables.Clear();

        if (explodedPartsRoot == null)
        {
            Debug.LogError("没有给 Aircraft Disassembly Controller 指定 Exploded Parts Root。", this);
            return;
        }

        Renderer[] renderers = explodedPartsRoot.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
        {
            Debug.LogError("Exploded Parts Root 下没有找到任何 Renderer。", this);
            return;
        }

        Bounds totalBounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            totalBounds.Encapsulate(renderers[i].bounds);

        HashSet<Transform> recorded = new HashSet<Transform>();
        for (int i = 0; i < renderers.Length; i++)
        {
            Transform part = renderers[i].transform;
            if (part == explodedPartsRoot.transform || !recorded.Add(part))
                continue;

            Vector3 towardCenterWorld = totalBounds.center - renderers[i].bounds.center;
            Vector3 towardCenterParent = part.parent != null
                ? part.parent.InverseTransformVector(towardCenterWorld)
                : towardCenterWorld;

            Vector3 finalScale = part.localScale;
            parts.Add(new PartPose
            {
                Transform = part,
                ExplodedPosition = part.localPosition,
                ExplodedRotation = part.localRotation,
                ExplodedScale = finalScale,
                CompressedPosition = part.localPosition + towardCenterParent * (1f - compressedPositionRatio),
                CompressedScale = finalScale * compressedScaleRatio
            });
        }

        // 不直接引用 XRI 类型，以兼容当前工程包版本。按组件类型名称自动查找。
        MonoBehaviour[] behaviours = explodedPartsRoot.GetComponentsInChildren<MonoBehaviour>(true);
        for (int i = 0; i < behaviours.Length; i++)
        {
            if (behaviours[i] != null && behaviours[i].GetType().Name == "XRGrabInteractable")
                partGrabInteractables.Add(behaviours[i]);
        }

        Debug.Log($"拆解控制器记录了 {parts.Count} 个爆炸零件、{partGrabInteractables.Count} 个零件抓取组件。", this);
    }

    private IEnumerator DisassemblySequence()
    {
        state = AircraftState.Transitioning;
        scaleHoldTimer = 0f;
        SetWholeInteractionEnabled(false);
        SetPartInteractionsEnabled(false);
        SetUiVisible(false, false);

        if (completeAircraft == null || explodedPartsRoot == null)
        {
            Debug.LogError("完整飞机或爆炸模型没有配置。", this);
            FinishTransition(AircraftState.Complete);
            yield break;
        }

        Vector3 completeStartScale = completeAircraft.transform.localScale;
        yield return AnimateScale(completeAircraft.transform, completeStartScale,
            completeStartScale * 0.94f, 0.16f, EaseInCubic);

        yield return ShowFlash(0.12f);

        completeAircraft.SetActive(false);
        ApplyCompressedPose();
        explodedPartsRoot.SetActive(true);

        float elapsed = 0f;
        while (elapsed < disassemblyDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / disassemblyDuration);
            float eased = EaseOutBack(t, backOvershoot);

            for (int i = 0; i < parts.Count; i++)
            {
                PartPose part = parts[i];
                part.Transform.localPosition = Vector3.LerpUnclamped(
                    part.CompressedPosition, part.ExplodedPosition, eased);
                part.Transform.localRotation = part.ExplodedRotation;
                part.Transform.localScale = Vector3.LerpUnclamped(
                    part.CompressedScale, part.ExplodedScale, eased);
            }

            UpdateFlashOut(t);
            yield return null;
        }

        ApplyExplodedPose();
        HideFlash();
        SetPartInteractionsEnabled(true);
        SetUiForExplodedState(true);
        FinishTransition(AircraftState.Exploded);
    }

    private IEnumerator ResetSequence()
    {
        state = AircraftState.Transitioning;
        SetWholeInteractionEnabled(false);
        SetPartInteractionsEnabled(false);
        SetUiVisible(false, false);

        // 第一步：无论用户把零件放到哪里，都先平滑回到标准爆炸图位置。
        Vector3[] startPositions = new Vector3[parts.Count];
        Quaternion[] startRotations = new Quaternion[parts.Count];
        Vector3[] startScales = new Vector3[parts.Count];
        for (int i = 0; i < parts.Count; i++)
        {
            startPositions[i] = parts[i].Transform.localPosition;
            startRotations[i] = parts[i].Transform.localRotation;
            startScales[i] = parts[i].Transform.localScale;
        }

        float elapsed = 0f;
        while (elapsed < restorePartsDuration)
        {
            elapsed += Time.deltaTime;
            float t = EaseInOutCubic(Mathf.Clamp01(elapsed / restorePartsDuration));
            for (int i = 0; i < parts.Count; i++)
            {
                parts[i].Transform.localPosition = Vector3.Lerp(startPositions[i], parts[i].ExplodedPosition, t);
                parts[i].Transform.localRotation = Quaternion.Slerp(startRotations[i], parts[i].ExplodedRotation, t);
                parts[i].Transform.localScale = Vector3.Lerp(startScales[i], parts[i].ExplodedScale, t);
            }
            yield return null;
        }

        ApplyExplodedPose();

        // 第二步：零件向中心收拢，给模型切换提供视觉遮挡。
        elapsed = 0f;
        while (elapsed < collapseDuration)
        {
            elapsed += Time.deltaTime;
            float t = EaseInCubic(Mathf.Clamp01(elapsed / collapseDuration));
            for (int i = 0; i < parts.Count; i++)
            {
                parts[i].Transform.localPosition = Vector3.Lerp(parts[i].ExplodedPosition, parts[i].CompressedPosition, t);
                parts[i].Transform.localScale = Vector3.Lerp(parts[i].ExplodedScale, parts[i].CompressedScale, t);
            }
            yield return null;
        }

        yield return ShowFlash(0.12f);

        explodedPartsRoot.SetActive(false);
        ApplyExplodedPose();

        if (restoreInteractionRootTransform && interactionRoot != null)
        {
            interactionRoot.localPosition = initialRootLocalPosition;
            interactionRoot.localRotation = initialRootLocalRotation;
            interactionRoot.localScale = initialRootLocalScale;
        }

        completeAircraft.SetActive(true);
        Vector3 appearStartScale = completeAircraftBaseScale * 0.88f;
        completeAircraft.transform.localScale = appearStartScale;
        yield return AnimateScale(completeAircraft.transform, appearStartScale,
            completeAircraftBaseScale, completeAppearDuration, t => EaseOutBack(t, 0.8f));

        HideFlash();
        SetWholeInteractionEnabled(true);
        SetUiForExplodedState(false);
        FinishTransition(AircraftState.Complete);
    }

    private void ApplyCompressedPose()
    {
        for (int i = 0; i < parts.Count; i++)
        {
            parts[i].Transform.localPosition = parts[i].CompressedPosition;
            parts[i].Transform.localRotation = parts[i].ExplodedRotation;
            parts[i].Transform.localScale = parts[i].CompressedScale;
        }
    }

    private void ApplyExplodedPose()
    {
        for (int i = 0; i < parts.Count; i++)
        {
            parts[i].Transform.localPosition = parts[i].ExplodedPosition;
            parts[i].Transform.localRotation = parts[i].ExplodedRotation;
            parts[i].Transform.localScale = parts[i].ExplodedScale;
        }
    }

    private void SetWholeInteractionEnabled(bool enabled)
    {
        if (wholeAircraftInteraction != null)
            wholeAircraftInteraction.enabled = enabled;
    }

    private void SetPartInteractionsEnabled(bool enabled)
    {
        for (int i = 0; i < partGrabInteractables.Count; i++)
        {
            if (partGrabInteractables[i] != null)
                partGrabInteractables[i].enabled = enabled;
        }
    }

    private void SetUiForExplodedState(bool exploded)
    {
        SetUiVisible(!exploded, exploded);
    }

    private void SetUiVisible(bool enableDisassemble, bool enableReset)
    {
        SetButtonState(disassembleButton, enableDisassemble);
        SetButtonState(resetButton, enableReset);
    }

    private void SetButtonState(GameObject buttonObject, bool interactable)
    {
        if (buttonObject == null)
            return;

        // 两个按钮始终显示，只通过 Button.interactable 控制是否可点击。
        if (!buttonObject.activeSelf)
            buttonObject.SetActive(true);

        Button button = buttonObject.GetComponent<Button>();
        if (button == null)
            button = buttonObject.GetComponentInChildren<Button>(true);

        if (button != null)
        {
            button.interactable = interactable;
        }
        else
        {
            Debug.LogWarning($"{buttonObject.name} 没有找到 Unity UI Button 组件。", buttonObject);
        }
    }

    private float GetScaleRatio()
    {
        Vector3 current = interactionRoot.localScale;
        float x = Mathf.Abs(initialRootLocalScale.x) > 0.0001f ? Mathf.Abs(current.x / initialRootLocalScale.x) : 1f;
        float y = Mathf.Abs(initialRootLocalScale.y) > 0.0001f ? Mathf.Abs(current.y / initialRootLocalScale.y) : 1f;
        float z = Mathf.Abs(initialRootLocalScale.z) > 0.0001f ? Mathf.Abs(current.z / initialRootLocalScale.z) : 1f;
        return Mathf.Max(x, y, z);
    }

    private IEnumerator ShowFlash(float duration)
    {
        if (transitionFlash == null)
            yield break;

        transitionFlash.SetActive(true);
        Transform flashTransform = transitionFlash.transform;
        Vector3 start = Vector3.zero;
        flashTransform.localScale = start;
        yield return AnimateScale(flashTransform, start, flashMaximumScale, duration, EaseOutCubic);
    }

    private void UpdateFlashOut(float animationTime)
    {
        if (transitionFlash == null || !transitionFlash.activeSelf)
            return;

        float t = Mathf.Clamp01(animationTime * 2f);
        transitionFlash.transform.localScale = Vector3.Lerp(
            flashMaximumScale, Vector3.zero, EaseOutCubic(t));
        if (t >= 1f)
            transitionFlash.SetActive(false);
    }

    private void HideFlash()
    {
        if (transitionFlash == null)
            return;
        transitionFlash.transform.localScale = Vector3.zero;
        transitionFlash.SetActive(false);
    }

    private IEnumerator AnimateScale(Transform target, Vector3 from, Vector3 to,
        float duration, System.Func<float, float> easing)
    {
        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = easing(Mathf.Clamp01(elapsed / duration));
            target.localScale = Vector3.LerpUnclamped(from, to, t);
            yield return null;
        }
        target.localScale = to;
    }

    private void FinishTransition(AircraftState newState)
    {
        state = newState;
        activeTransition = null;
    }

    private static float EaseInCubic(float t) => t * t * t;
    private static float EaseOutCubic(float t)
    {
        float oneMinusT = 1f - t;
        return 1f - oneMinusT * oneMinusT * oneMinusT;
    }

    private static float EaseInOutCubic(float t)
    {
        return t < 0.5f
            ? 4f * t * t * t
            : 1f - Mathf.Pow(-2f * t + 2f, 3f) / 2f;
    }

    private static float EaseOutBack(float t, float overshoot)
    {
        float c1 = overshoot;
        float c3 = c1 + 1f;
        float x = t - 1f;
        return 1f + c3 * x * x * x + c1 * x * x;
    }
}
