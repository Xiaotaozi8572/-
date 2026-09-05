using UnityEngine;
using UnityEngine.EventSystems;

public class CardHoverEffect :
    MonoBehaviour,
    IPointerEnterHandler,
    IPointerExitHandler
{
    Vector3 originalScale;

    private void Start()
    {
        originalScale = transform.localScale;
    }

    public void OnPointerEnter(PointerEventData eventData)
    {
        transform.localScale =
            originalScale * 1.08f;
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        transform.localScale =
            originalScale;
    }
}