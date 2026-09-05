using System.Collections;
using UnityEngine;

/// <summary>
/// Rotates a target around a fixed pivot by a fixed angle at regular intervals.
/// Designed for inspector-only setup in exhibition scenes.
/// </summary>
public sealed class FixedStepAutoRotate : MonoBehaviour
{
    public enum RotationAxis
    {
        X,
        Y,
        Z
    }

    [Header("旋转对象与中心")]
    [SerializeField] private Transform rotationTarget;
    [Tooltip("留空时绕目标自身中心旋转；指定物体时绕该物体的位置公转。")]
    [SerializeField] private Transform pivot;

    [Header("定点定量旋转")]
    [SerializeField] private RotationAxis rotationAxis = RotationAxis.Y;
    [Tooltip("勾选后使用世界坐标轴；不勾选时使用目标自身坐标轴。")]
    [SerializeField] private bool useWorldAxis;
    [Tooltip("每次旋转的角度，例如 30、45、90。负数表示反方向。")]
    [SerializeField] private float degreesPerStep = 45f;
    [Min(0.01f)]
    [Tooltip("完成一次旋转所用的时间。")]
    [SerializeField] private float stepDuration = 0.8f;
    [Min(0f)]
    [Tooltip("每次旋转完成后的停留时间。")]
    [SerializeField] private float pauseBetweenSteps = 1.5f;
    [SerializeField] private bool playOnStart = true;
    [SerializeField] private AnimationCurve easing = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

    private Coroutine rotationRoutine;

    private void Start()
    {
        if (rotationTarget == null)
        {
            rotationTarget = transform;
        }

        if (playOnStart)
        {
            Play();
        }
    }

    public void Play()
    {
        if (rotationRoutine == null && isActiveAndEnabled)
        {
            rotationRoutine = StartCoroutine(RotateContinuously());
        }
    }

    public void Stop()
    {
        if (rotationRoutine != null)
        {
            StopCoroutine(rotationRoutine);
            rotationRoutine = null;
        }
    }

    public void RotateOneStep()
    {
        if (rotationRoutine == null && isActiveAndEnabled)
        {
            rotationRoutine = StartCoroutine(RotateSingleStep());
        }
    }

    private IEnumerator RotateContinuously()
    {
        while (true)
        {
            yield return RotateStep();

            if (pauseBetweenSteps > 0f)
            {
                yield return new WaitForSeconds(pauseBetweenSteps);
            }
        }
    }

    private IEnumerator RotateSingleStep()
    {
        yield return RotateStep();
        rotationRoutine = null;
    }

    private IEnumerator RotateStep()
    {
        Vector3 axis = GetAxis();
        Quaternion startRotation = rotationTarget.rotation;
        Vector3 startPosition = rotationTarget.position;
        Vector3 pivotPosition = pivot != null ? pivot.position : startPosition;
        Vector3 startOffset = startPosition - pivotPosition;

        float elapsed = 0f;
        while (elapsed < stepDuration)
        {
            elapsed += Time.deltaTime;
            float normalizedTime = Mathf.Clamp01(elapsed / stepDuration);
            float angle = degreesPerStep * easing.Evaluate(normalizedTime);
            Quaternion delta = Quaternion.AngleAxis(angle, axis);

            rotationTarget.rotation = delta * startRotation;
            if (pivot != null)
            {
                rotationTarget.position = pivotPosition + delta * startOffset;
            }

            yield return null;
        }

        Quaternion finalDelta = Quaternion.AngleAxis(degreesPerStep, axis);
        rotationTarget.rotation = finalDelta * startRotation;
        if (pivot != null)
        {
            rotationTarget.position = pivotPosition + finalDelta * startOffset;
        }
    }

    private Vector3 GetAxis()
    {
        Vector3 localAxis;
        switch (rotationAxis)
        {
            case RotationAxis.X:
                localAxis = Vector3.right;
                break;
            case RotationAxis.Z:
                localAxis = Vector3.forward;
                break;
            default:
                localAxis = Vector3.up;
                break;
        }

        return useWorldAxis ? localAxis : rotationTarget.TransformDirection(localAxis).normalized;
    }
}
