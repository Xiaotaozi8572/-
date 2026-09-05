using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Interaction.Toolkit;

/// <summary>
/// 双手同时捏合后，根据两只手掌连线的角度变化，驱动目标物体绕自身局部 Y 轴旋转。
/// 不依赖飞机本体的 Collider 或 XR Grab Interactable。
/// </summary>
public sealed class TwoHandPinchLocalYawRotate : MonoBehaviour
{
    [Header("旋转对象")]
    [Tooltip("拖入 AircraftRotationRoot。其局部 X、Z 旋转应保持为 0。")]
    [SerializeField] private Transform rotationTarget;

    [Header("捏合识别（米）")]
    [Tooltip("拇指尖与食指尖距离小于该值时开始捏合。")]
    [Min(0.005f)] [SerializeField] private float pinchEnterDistance = 0.025f;

    [Tooltip("距离大于该值时判定松手。应略大于开始捏合距离，以减少抖动。")]
    [Min(0.01f)] [SerializeField] private float pinchExitDistance = 0.04f;

    [Header("旋转手感")]
    [Tooltip("旋转灵敏度。1 表示双手转多少，飞机大致跟随多少。")]
    [Range(0.1f, 3f)] [SerializeField] private float rotationSensitivity = 1f;

    [Tooltip("如果飞机旋转方向与双手动作相反，勾选此项。")]
    [SerializeField] private bool invertRotationDirection;

    [Tooltip("忽略小于该角度的手部抖动。")]
    [Range(0f, 3f)] [SerializeField] private float angleDeadZone = 0.15f;

    [Tooltip("限制单帧最大旋转角度，防止追踪跳变导致飞机突然旋转。")]
    [Range(1f, 20f)] [SerializeField] private float maxDegreesPerFrame = 6f;

    [Tooltip("数值越大越平滑，但跟手性会稍弱。")]
    [Range(0.01f, 0.3f)] [SerializeField] private float smoothTime = 0.06f;

    [Header("旋转期间暂停热点")]
    [Tooltip("可拖入各热点上的 XR Simple Interactable，旋转时会暂时禁止点击。")]
    [SerializeField] private XRBaseInteractable[] hotspotInteractables;

    [Tooltip("松手后延迟恢复热点，避免松手瞬间误点。")]
    [Range(0f, 1f)] [SerializeField] private float hotspotRestoreDelay = 0.25f;

    private readonly List<XRHandSubsystem> handSubsystems = new List<XRHandSubsystem>();
    private XRHandSubsystem handSubsystem;

    private bool leftPinching;
    private bool rightPinching;
    private bool rotating;
    private bool hasPreviousAngle;
    private bool rotationBlocked;

    private float previousHandsAngle;
    private float targetLocalYaw;
    private float smoothVelocity;
    private Coroutine restoreHotspotsCoroutine;

    private void Awake()
    {
        if (rotationTarget != null)
        {
            targetLocalYaw = rotationTarget.localEulerAngles.y;
        }
    }

    private void OnValidate()
    {
        if (pinchExitDistance <= pinchEnterDistance)
        {
            pinchExitDistance = pinchEnterDistance + 0.01f;
        }
    }

    private void Update()
    {
        if (rotationTarget == null)
        {
            return;
        }

        if (rotationBlocked || !TryGetRunningHandSubsystem())
        {
            EndRotation();
            SmoothTargetRotation();
            return;
        }

        bool leftTracked = TryReadHand(
            handSubsystem.leftHand,
            leftPinching,
            out Vector3 leftPalmPosition,
            out leftPinching);

        bool rightTracked = TryReadHand(
            handSubsystem.rightHand,
            rightPinching,
            out Vector3 rightPalmPosition,
            out rightPinching);

        bool bothPinching = leftTracked && rightTracked && leftPinching && rightPinching;

        if (bothPinching)
        {
            RotateFromHands(leftPalmPosition, rightPalmPosition);
        }
        else
        {
            EndRotation();
        }

        SmoothTargetRotation();
    }

    /// <summary>
    /// 标签打开时可通过 Button 事件传入 true，关闭标签时传入 false。
    /// </summary>
    public void SetRotationBlocked(bool blocked)
    {
        rotationBlocked = blocked;
        if (blocked)
        {
            EndRotation();
        }
    }

    private bool TryGetRunningHandSubsystem()
    {
        if (handSubsystem != null && handSubsystem.running)
        {
            return true;
        }

        handSubsystems.Clear();
        SubsystemManager.GetSubsystems(handSubsystems);

        for (int i = 0; i < handSubsystems.Count; i++)
        {
            XRHandSubsystem candidate = handSubsystems[i];
            if (candidate != null && candidate.running)
            {
                handSubsystem = candidate;
                return true;
            }
        }

        handSubsystem = null;
        return false;
    }

    private bool TryReadHand(
        XRHand hand,
        bool wasPinching,
        out Vector3 palmPosition,
        out bool isPinching)
    {
        palmPosition = default;
        isPinching = false;

        if (!hand.isTracked)
        {
            return false;
        }

        XRHandJoint palm = hand.GetJoint(XRHandJointID.Palm);
        XRHandJoint thumbTip = hand.GetJoint(XRHandJointID.ThumbTip);
        XRHandJoint indexTip = hand.GetJoint(XRHandJointID.IndexTip);

        if (!palm.TryGetPose(out Pose palmPose) ||
            !thumbTip.TryGetPose(out Pose thumbPose) ||
            !indexTip.TryGetPose(out Pose indexPose))
        {
            return false;
        }

        float pinchDistance = Vector3.Distance(thumbPose.position, indexPose.position);
        isPinching = wasPinching
            ? pinchDistance < pinchExitDistance
            : pinchDistance < pinchEnterDistance;

        palmPosition = palmPose.position;
        return true;
    }

    private void RotateFromHands(Vector3 leftPosition, Vector3 rightPosition)
    {
        Vector3 handsDirection = rightPosition - leftPosition;
        handsDirection.y = 0f;

        // 双手距离太近时角度不稳定，不进行旋转。
        if (handsDirection.sqrMagnitude < 0.0025f)
        {
            return;
        }

        float currentAngle = Mathf.Atan2(handsDirection.z, handsDirection.x) * Mathf.Rad2Deg;

        if (!rotating)
        {
            BeginRotation();
            previousHandsAngle = currentAngle;
            hasPreviousAngle = true;
            return;
        }

        if (!hasPreviousAngle)
        {
            previousHandsAngle = currentAngle;
            hasPreviousAngle = true;
            return;
        }

        float deltaAngle = Mathf.DeltaAngle(previousHandsAngle, currentAngle);
        previousHandsAngle = currentAngle;

        if (Mathf.Abs(deltaAngle) < angleDeadZone)
        {
            return;
        }

        float directionMultiplier = invertRotationDirection ? -1f : 1f;
        deltaAngle = Mathf.Clamp(
            deltaAngle * rotationSensitivity * directionMultiplier,
            -maxDegreesPerFrame,
            maxDegreesPerFrame);

        targetLocalYaw += deltaAngle;
    }

    private void BeginRotation()
    {
        rotating = true;
        hasPreviousAngle = false;
        targetLocalYaw = rotationTarget.localEulerAngles.y;

        if (restoreHotspotsCoroutine != null)
        {
            StopCoroutine(restoreHotspotsCoroutine);
            restoreHotspotsCoroutine = null;
        }

        SetHotspotsEnabled(false);
    }

    private void EndRotation()
    {
        if (!rotating)
        {
            return;
        }

        rotating = false;
        hasPreviousAngle = false;
        targetLocalYaw = rotationTarget.localEulerAngles.y;

        if (restoreHotspotsCoroutine != null)
        {
            StopCoroutine(restoreHotspotsCoroutine);
        }

        restoreHotspotsCoroutine = StartCoroutine(RestoreHotspotsAfterDelay());
    }

    private IEnumerator RestoreHotspotsAfterDelay()
    {
        if (hotspotRestoreDelay > 0f)
        {
            yield return new WaitForSeconds(hotspotRestoreDelay);
        }

        SetHotspotsEnabled(true);
        restoreHotspotsCoroutine = null;
    }

    private void SetHotspotsEnabled(bool enabled)
    {
        if (hotspotInteractables == null)
        {
            return;
        }

        for (int i = 0; i < hotspotInteractables.Length; i++)
        {
            if (hotspotInteractables[i] != null)
            {
                hotspotInteractables[i].enabled = enabled;
            }
        }
    }

    private void SmoothTargetRotation()
    {
        float smoothedYaw = Mathf.SmoothDampAngle(
            rotationTarget.localEulerAngles.y,
            targetLocalYaw,
            ref smoothVelocity,
            smoothTime);

        rotationTarget.localRotation = Quaternion.Euler(0f, smoothedYaw, 0f);
    }
}
