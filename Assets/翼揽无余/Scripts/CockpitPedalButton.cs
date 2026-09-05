using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Press-and-hold UI pedal with visual/audio feedback.
/// </summary>
public sealed class CockpitPedalButton : MonoBehaviour,
    IPointerDownHandler, IPointerUpHandler,
    IPointerEnterHandler, IPointerExitHandler
{
    private enum PedalSide { Left, Right }

    [SerializeField] private PedalSide side;
    [SerializeField] private CockpitFlightMotionController flightController;
    [SerializeField] private RectTransform movingGraphic;
    [SerializeField] private Image feedbackGraphic;
    [SerializeField] private float pressedOffset = -8f;
    [SerializeField] private Color normalColor = new Color(0.05f, 0.75f, 0.9f, 0.8f);
    [SerializeField] private Color hoverColor = new Color(0.15f, 0.95f, 1f, 1f);
    [SerializeField] private Color pressedColor = Color.white;
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private AudioClip pressSound;

    private Vector2 initialPosition;
    private Vector3 initialScale;
    private bool pressed;

    private void Awake()
    {
        if (movingGraphic == null)
            movingGraphic = transform as RectTransform;
        if (feedbackGraphic == null)
            feedbackGraphic = GetComponent<Image>();

        if (movingGraphic != null)
        {
            initialPosition = movingGraphic.anchoredPosition;
            initialScale = movingGraphic.localScale;
        }
        SetColor(normalColor);
    }

    public void OnPointerEnter(PointerEventData eventData)
    {
        if (!pressed)
            SetColor(hoverColor);
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        if (!pressed)
            SetColor(normalColor);
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        pressed = true;
        if (movingGraphic != null)
        {
            movingGraphic.anchoredPosition = initialPosition + new Vector2(0f, pressedOffset);
            movingGraphic.localScale = initialScale * 0.96f;
        }
        SetColor(pressedColor);

        if (side == PedalSide.Left)
            flightController?.PressLeftPedal();
        else
            flightController?.PressRightPedal();

        if (audioSource != null && pressSound != null)
            audioSource.PlayOneShot(pressSound);
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        pressed = false;
        if (movingGraphic != null)
        {
            movingGraphic.anchoredPosition = initialPosition;
            movingGraphic.localScale = initialScale;
        }
        SetColor(normalColor);

        if (side == PedalSide.Left)
            flightController?.ReleaseLeftPedal();
        else
            flightController?.ReleaseRightPedal();
    }

    private void OnDisable()
    {
        if (pressed)
            OnPointerUp(null);
    }

    private void SetColor(Color color)
    {
        if (feedbackGraphic != null)
            feedbackGraphic.color = color;
    }
}
