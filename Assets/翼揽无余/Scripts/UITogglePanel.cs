using UnityEngine;

/// <summary>
/// Shows, hides or toggles a UI panel while the trigger button remains active.
/// </summary>
public sealed class UITogglePanel : MonoBehaviour
{
    [SerializeField] private GameObject targetPanel;
    [SerializeField] private bool hideOnAwake = true;

    private void Awake()
    {
        if (targetPanel != null && hideOnAwake)
            targetPanel.SetActive(false);
    }

    public void TogglePanel()
    {
        if (targetPanel != null)
            targetPanel.SetActive(!targetPanel.activeSelf);
    }

    public void ShowPanel()
    {
        if (targetPanel != null)
            targetPanel.SetActive(true);
    }

    public void HidePanel()
    {
        if (targetPanel != null)
            targetPanel.SetActive(false);
    }
}
