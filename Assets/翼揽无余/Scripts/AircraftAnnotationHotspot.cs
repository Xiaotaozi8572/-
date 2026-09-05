using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;

/// <summary>
/// 将三维热点的选择事件转换为“显示标签并暂停飞机旋转”。
/// </summary>
[RequireComponent(typeof(XRBaseInteractable))]
public sealed class AircraftAnnotationHotspot : MonoBehaviour
{
    [SerializeField] private GameObject labelRoot;
    [SerializeField] private TwoHandPinchLocalYawRotate rotationController;
    [SerializeField] private bool hideHotspotWhileLabelIsOpen;

    private XRBaseInteractable interactable;

    private void Awake()
    {
        interactable = GetComponent<XRBaseInteractable>();

        if (labelRoot != null)
        {
            labelRoot.SetActive(false);
        }
    }

    private void OnEnable()
    {
        if (interactable == null)
        {
            interactable = GetComponent<XRBaseInteractable>();
        }

        interactable.selectEntered.AddListener(OnSelected);
    }

    private void OnDisable()
    {
        if (interactable != null)
        {
            interactable.selectEntered.RemoveListener(OnSelected);
        }
    }

    public void OpenLabel()
    {
        if (labelRoot != null)
        {
            labelRoot.SetActive(true);
        }

        if (rotationController != null)
        {
            rotationController.SetRotationBlocked(true);
        }

        if (hideHotspotWhileLabelIsOpen && interactable != null)
        {
            interactable.enabled = false;
        }
    }

    public void CloseLabel()
    {
        if (labelRoot != null)
        {
            labelRoot.SetActive(false);
        }

        if (interactable != null)
        {
            interactable.enabled = true;
        }

        if (rotationController != null)
        {
            rotationController.SetRotationBlocked(false);
        }
    }

    private void OnSelected(SelectEnterEventArgs args)
    {
        OpenLabel();
    }
}
