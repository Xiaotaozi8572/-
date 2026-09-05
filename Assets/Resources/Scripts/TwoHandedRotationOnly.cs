using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;

[RequireComponent(typeof(XRGrabInteractable))]
public class TwoHandedRotationOnly : MonoBehaviour
{
    private XRGrabInteractable grab;
    private Transform leftAttach;
    private Transform rightAttach;

    private float previousAngle;
    private float currentY;
    private bool hasPreviousAngle;

    [SerializeField, Range(1f, 50f)]
    private float smoothSpeed = 12f; // 越大越跟手，越小越顺滑

    private void Awake()
    {
        grab = GetComponent<XRGrabInteractable>();
        grab.movementType = XRBaseInteractable.MovementType.Instantaneous;

        grab.selectEntered.AddListener(OnSelectEntered);
        grab.selectExited.AddListener(OnSelectExited);
    }

    private void OnDestroy()
    {
        grab.selectEntered.RemoveListener(OnSelectEntered);
        grab.selectExited.RemoveListener(OnSelectExited);
    }

    private void OnSelectEntered(SelectEnterEventArgs args)
    {
        var hand = args.interactor.transform;

        if (leftAttach == null)
            leftAttach = hand;
        else if (rightAttach == null)
            rightAttach = hand;
    }

    private void OnSelectExited(SelectExitEventArgs args)
    {
        var hand = args.interactor.transform;
        if (hand == leftAttach) leftAttach = null;
        if (hand == rightAttach) rightAttach = null;

        if (leftAttach == null || rightAttach == null)
            hasPreviousAngle = false;
    }

    private void Update()
    {
        if (leftAttach == null || rightAttach == null) return;

        Vector3 handsVector = rightAttach.position - leftAttach.position;
        handsVector.y = 0f;

        if (handsVector.sqrMagnitude < 0.0001f) return;

        float currentAngle = Mathf.Atan2(handsVector.z, handsVector.x) * Mathf.Rad2Deg;

        if (!hasPreviousAngle)
        {
            previousAngle = currentAngle;
            currentY = transform.rotation.eulerAngles.y;
            hasPreviousAngle = true;
            return;
        }

        // 计算这一帧双手转了多少度
        float deltaAngle = Mathf.DeltaAngle(previousAngle, currentAngle);
        previousAngle = currentAngle;

        // 累加到目标 Y 轴角度
        currentY += deltaAngle;

        Quaternion targetRotation = Quaternion.Euler(0f, currentY, 0f);
        transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, Time.deltaTime * smoothSpeed);
    }
}