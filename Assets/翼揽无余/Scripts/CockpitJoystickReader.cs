using UnityEngine;

/// <summary>
/// Converts a constrained 3D joystick's local tilt into normalized flight input.
/// </summary>
public sealed class CockpitJoystickReader : MonoBehaviour
{
    [SerializeField] private Transform joystickHandle;
    [SerializeField] private CockpitFlightMotionController flightController;
    [Min(1f)] [SerializeField] private float maximumTiltAngle = 20f;

    private Vector3 centerEuler;

    private void Awake()
    {
        if (joystickHandle == null)
            joystickHandle = transform;

        centerEuler = joystickHandle.localEulerAngles;
    }

    private void Update()
    {
        if (flightController == null || joystickHandle == null)
            return;

        Vector3 euler = joystickHandle.localEulerAngles;
        float forwardBack = Mathf.DeltaAngle(centerEuler.x, euler.x) / maximumTiltAngle;
        float leftRight = -Mathf.DeltaAngle(centerEuler.z, euler.z) / maximumTiltAngle;

        flightController.SetStickInput(new Vector2(
            Mathf.Clamp(leftRight, -1f, 1f),
            Mathf.Clamp(forwardBack, -1f, 1f)));
    }
}
