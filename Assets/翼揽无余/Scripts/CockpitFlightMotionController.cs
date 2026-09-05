using UnityEngine;

/// <summary>
/// Drives the parent of the XR rig instead of the tracked Main Camera.
/// The stick controls pitch/roll and the pedals control yaw.
/// </summary>
public sealed class CockpitFlightMotionController : MonoBehaviour
{
    [Header("Flight root")]
    [SerializeField] private Transform flightMotionRoot;

    [Header("Motion range")]
    [Min(0f)] [SerializeField] private float forwardSpeed = 0f;
    [Range(0f, 20f)] [SerializeField] private float maximumPitch = 8f;
    [Range(0f, 30f)] [SerializeField] private float maximumRoll = 12f;
    [Range(0f, 45f)] [SerializeField] private float pedalYawSpeed = 18f;
    [Min(0.1f)] [SerializeField] private float responseSpeed = 3f;

    [Header("Operation feedback")]
    [SerializeField] private CockpitOperationFeedback operationFeedback;
    [Range(0.05f, 0.95f)] [SerializeField] private float feedbackThreshold = 0.45f;

    private Vector2 stickInput;
    private float pedalInput;
    private float currentPitch;
    private float currentRoll;
    private float currentHeading;
    private Quaternion initialLocalRotation;
    private CockpitOperationFeedback.StickDirection lastStickDirection;

    private void Awake()
    {
        if (flightMotionRoot == null)
            flightMotionRoot = transform;

        initialLocalRotation = flightMotionRoot.localRotation;
    }

    private void Update()
    {
        // UI down / pull back => nose up; UI up / push forward => nose down.
        float targetPitch = stickInput.y * maximumPitch;
        float targetRoll = -stickInput.x * maximumRoll;

        currentPitch = Mathf.Lerp(currentPitch, targetPitch, responseSpeed * Time.deltaTime);
        currentRoll = Mathf.Lerp(currentRoll, targetRoll, responseSpeed * Time.deltaTime);
        currentHeading += pedalInput * pedalYawSpeed * Time.deltaTime;

        Quaternion flightRotation = Quaternion.Euler(currentPitch, currentHeading, currentRoll);
        flightMotionRoot.localRotation = initialLocalRotation * flightRotation;

        if (forwardSpeed > 0f)
            flightMotionRoot.position += flightMotionRoot.forward * (forwardSpeed * Time.deltaTime);
    }

    public void SetStickInput(Vector2 value)
    {
        stickInput = Vector2.ClampMagnitude(value, 1f);
        UpdateStickFeedback(stickInput);
    }

    public void PressLeftPedal()
    {
        pedalInput = -1f;
        operationFeedback?.PlayLeftPedal();
    }

    public void ReleaseLeftPedal()
    {
        if (pedalInput < 0f)
            pedalInput = 0f;
    }

    public void PressRightPedal()
    {
        pedalInput = 1f;
        operationFeedback?.PlayRightPedal();
    }

    public void ReleaseRightPedal()
    {
        if (pedalInput > 0f)
            pedalInput = 0f;
    }

    public void ResetFlightAttitude()
    {
        stickInput = Vector2.zero;
        pedalInput = 0f;
        currentHeading = 0f;
        lastStickDirection = CockpitOperationFeedback.StickDirection.Neutral;
    }

    private void UpdateStickFeedback(Vector2 value)
    {
        CockpitOperationFeedback.StickDirection direction = GetStickDirection(value);
        if (direction == lastStickDirection)
            return;

        lastStickDirection = direction;
        if (direction != CockpitOperationFeedback.StickDirection.Neutral)
            operationFeedback?.PlayStickDirection(direction);
    }

    private CockpitOperationFeedback.StickDirection GetStickDirection(Vector2 value)
    {
        if (value.magnitude < feedbackThreshold)
            return CockpitOperationFeedback.StickDirection.Neutral;

        if (Mathf.Abs(value.x) > Mathf.Abs(value.y))
        {
            return value.x < 0f
                ? CockpitOperationFeedback.StickDirection.Left
                : CockpitOperationFeedback.StickDirection.Right;
        }

        return value.y > 0f
            ? CockpitOperationFeedback.StickDirection.Forward
            : CockpitOperationFeedback.StickDirection.Backward;
    }
}
