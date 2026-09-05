using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// World-space UI joystick for XR ray and poke interaction.
/// </summary>
public sealed class CockpitVirtualJoystick : MonoBehaviour,
    IPointerDownHandler, IInitializePotentialDragHandler, IBeginDragHandler,
    IDragHandler, IEndDragHandler, IPointerMoveHandler, IPointerUpHandler,
    IPointerEnterHandler, IPointerExitHandler
{
    [SerializeField] private RectTransform joystickArea;
    [SerializeField] private RectTransform handle;
    [SerializeField] private CockpitFlightMotionController flightController;
    [SerializeField] private Image feedbackGraphic;
    [SerializeField] private float movementRadius = 55f;
    [SerializeField] private Color normalColor = new Color(0.08f, 0.75f, 0.9f, 0.75f);
    [SerializeField] private Color hoverColor = new Color(0.2f, 0.95f, 1f, 1f);
    [SerializeField] private Color activeColor = Color.white;
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private AudioClip pressSound;

    private bool dragging;

    private void Awake()
    {
        if (joystickArea == null)
            joystickArea = transform as RectTransform;
        if (feedbackGraphic == null)
            feedbackGraphic = GetComponent<Image>();

        SetColor(normalColor);
    }

    public void OnPointerEnter(PointerEventData eventData)
    {
        if (!dragging)
            SetColor(hoverColor);
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        if (!dragging)
            SetColor(normalColor);
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        dragging = true;
        SetColor(activeColor);
        if (audioSource != null && pressSound != null)
            audioSource.PlayOneShot(pressSound);
        UpdateInput(eventData);
    }

    public void OnInitializePotentialDrag(PointerEventData eventData)
    {
        // XR tracked-device pointers move only a small number of UI pixels.
        // Waiting for the normal mouse drag threshold can therefore prevent
        // a hand-ray drag from ever starting.
        eventData.useDragThreshold = false;
    }

    public void OnBeginDrag(PointerEventData eventData)
    {
        dragging = true;
        UpdateInput(eventData);
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (dragging)
            UpdateInput(eventData);
    }

    public void OnPointerMove(PointerEventData eventData)
    {
        // Some XR UI input modules keep sending pointer movement while the
        // select gesture is held but do not continuously issue OnDrag.
        if (dragging)
            UpdateInput(eventData);
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        ResetJoystick();
    }

    public void OnEndDrag(PointerEventData eventData)
    {
        ResetJoystick();
    }

    private void OnDisable()
    {
        ResetJoystick();
    }

    private void ResetJoystick()
    {
        dragging = false;
        if (handle != null)
            handle.anchoredPosition = Vector2.zero;
        if (flightController != null)
            flightController.SetStickInput(Vector2.zero);
        SetColor(normalColor);
    }

    private void UpdateInput(PointerEventData eventData)
    {
        if (joystickArea == null)
            return;

        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                joystickArea, eventData.position, eventData.pressEventCamera, out Vector2 localPoint))
            return;

        Vector2 normalized = Vector2.ClampMagnitude(localPoint / movementRadius, 1f);

        if (handle != null)
            handle.anchoredPosition = normalized * movementRadius;
        if (flightController != null)
            flightController.SetStickInput(normalized);
    }

    private void SetColor(Color color)
    {
        if (feedbackGraphic != null)
            feedbackGraphic.color = color;
    }
}
