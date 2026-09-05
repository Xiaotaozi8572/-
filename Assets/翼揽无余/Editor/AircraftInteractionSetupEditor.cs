#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.UI;

public static class AircraftInteractionSetupEditor
{
    private const string RotationRootName = "AircraftRotationRoot";
    private const string AircraftSystemName = "AircraftSystem";
    private const string AnchorsRootName = "AnnotationAnchors";
    private const string ControllerName = "AircraftGestureController";

    [MenuItem("工具/翼揽无余/配置当前场景飞机交互")]
    private static void ConfigureCurrentScene()
    {
        GameObject rotationRoot = GameObject.Find(RotationRootName);
        GameObject aircraftSystem = GameObject.Find(AircraftSystemName);
        GameObject anchorsRoot = GameObject.Find(AnchorsRootName);

        if (rotationRoot == null || aircraftSystem == null)
        {
            EditorUtility.DisplayDialog(
                "配置失败",
                "当前场景中没有找到 AircraftSystem 或 AircraftRotationRoot。请先打开飞机展示场景。",
                "确定");
            return;
        }

        GameObject controllerObject = GameObject.Find(ControllerName);
        if (controllerObject == null)
        {
            controllerObject = new GameObject(ControllerName);
            Undo.RegisterCreatedObjectUndo(controllerObject, "创建飞机手势控制器");
            controllerObject.transform.SetParent(aircraftSystem.transform, false);
        }

        TwoHandPinchLocalYawRotate controller =
            controllerObject.GetComponent<TwoHandPinchLocalYawRotate>();

        if (controller == null)
        {
            controller = Undo.AddComponent<TwoHandPinchLocalYawRotate>(controllerObject);
        }

        XRBaseInteractable[] hotspots = anchorsRoot != null
            ? anchorsRoot.GetComponentsInChildren<XRBaseInteractable>(true)
            : new XRBaseInteractable[0];

        SerializedObject serializedController = new SerializedObject(controller);
        serializedController.FindProperty("rotationTarget").objectReferenceValue = rotationRoot.transform;

        SerializedProperty hotspotProperty =
            serializedController.FindProperty("hotspotInteractables");
        hotspotProperty.arraySize = hotspots.Length;
        for (int i = 0; i < hotspots.Length; i++)
        {
            hotspotProperty.GetArrayElementAtIndex(i).objectReferenceValue = hotspots[i];
        }

        serializedController.ApplyModifiedProperties();

        AircraftAnnotationHotspot[] hotspotControllers = anchorsRoot != null
            ? anchorsRoot.GetComponentsInChildren<AircraftAnnotationHotspot>(true)
            : new AircraftAnnotationHotspot[0];

        foreach (AircraftAnnotationHotspot hotspotController in hotspotControllers)
        {
            SerializedObject serializedHotspot = new SerializedObject(hotspotController);
            serializedHotspot.FindProperty("rotationController").objectReferenceValue = controller;
            serializedHotspot.ApplyModifiedProperties();
        }

        EditorUtility.SetDirty(controller);
        EditorSceneManager.MarkSceneDirty(controller.gameObject.scene);
        Selection.activeGameObject = controllerObject;

        EditorUtility.DisplayDialog(
            "飞机交互配置完成",
            $"已连接 AircraftRotationRoot，并收集 {hotspots.Length} 个热点。以后新增热点后可以再次执行此菜单。",
            "确定");
    }

    [MenuItem("工具/翼揽无余/为选中的锚点创建热点模板")]
    private static void CreateHotspotForSelectedAnchor()
    {
        Transform anchor = Selection.activeTransform;
        if (anchor == null)
        {
            EditorUtility.DisplayDialog("未选择锚点", "请先在层级窗口选择一个 Anchor。", "确定");
            return;
        }

        if (anchor.Find("Hotspot") != null)
        {
            EditorUtility.DisplayDialog("热点已存在", "所选锚点下面已经存在名为 Hotspot 的对象。", "确定");
            return;
        }

        GameObject hotspot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        hotspot.name = "Hotspot";
        Undo.RegisterCreatedObjectUndo(hotspot, "创建标注热点");
        hotspot.transform.SetParent(anchor, false);
        hotspot.transform.localScale = Vector3.one * 0.04f;

        SphereCollider collider = hotspot.GetComponent<SphereCollider>();
        collider.isTrigger = true;
        // 可见球直径约 4 厘米，但射线命中范围放大到约 20 厘米。
        collider.radius = 2.5f;

        XRSimpleInteractable interactable = Undo.AddComponent<XRSimpleInteractable>(hotspot);
        AircraftAnnotationHotspot hotspotController =
            Undo.AddComponent<AircraftAnnotationHotspot>(hotspot);

        GameObject labelCanvasObject = new GameObject(
            "LabelCanvas",
            typeof(RectTransform),
            typeof(Canvas),
            typeof(CanvasScaler),
            typeof(TrackedDeviceGraphicRaycaster),
            typeof(WorldSpaceBillboard));
        Undo.RegisterCreatedObjectUndo(labelCanvasObject, "创建标注标签");
        labelCanvasObject.transform.SetParent(anchor, false);
        labelCanvasObject.transform.localPosition = new Vector3(0.15f, 0.08f, 0f);
        labelCanvasObject.transform.localScale = Vector3.one * 0.001f;

        Canvas canvas = labelCanvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = Camera.main;

        RectTransform canvasRect = labelCanvasObject.GetComponent<RectTransform>();
        canvasRect.sizeDelta = new Vector2(260f, 80f);

        GameObject labelButtonObject = new GameObject(
            "LabelButton",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image),
            typeof(Button));
        Undo.RegisterCreatedObjectUndo(labelButtonObject, "创建标签按钮");
        labelButtonObject.transform.SetParent(labelCanvasObject.transform, false);

        RectTransform buttonRect = labelButtonObject.GetComponent<RectTransform>();
        buttonRect.anchorMin = Vector2.zero;
        buttonRect.anchorMax = Vector2.one;
        buttonRect.offsetMin = Vector2.zero;
        buttonRect.offsetMax = Vector2.zero;

        Image buttonImage = labelButtonObject.GetComponent<Image>();
        buttonImage.color = new Color(0.08f, 0.12f, 0.16f, 0.9f);

        SerializedObject serializedHotspot = new SerializedObject(hotspotController);
        serializedHotspot.FindProperty("labelRoot").objectReferenceValue = labelCanvasObject;
        serializedHotspot.ApplyModifiedProperties();

        labelCanvasObject.SetActive(false);
        Selection.activeGameObject = hotspot;
        EditorSceneManager.MarkSceneDirty(anchor.gameObject.scene);

        EditorUtility.DisplayDialog(
            "热点模板创建完成",
            "已创建 Hotspot、Sphere Collider、XR Simple Interactable 和世界空间 LabelCanvas。请调整锚点位置、碰撞范围和标签样式，然后再次执行“配置当前场景飞机交互”。",
            "确定");
    }

    [MenuItem("工具/翼揽无余/检查当前场景飞机交互")]
    private static void ValidateCurrentScene()
    {
        List<string> problems = new List<string>();

        GameObject rotationRoot = GameObject.Find(RotationRootName);
        if (rotationRoot == null)
        {
            problems.Add("缺少 AircraftRotationRoot");
        }
        else
        {
            Vector3 euler = rotationRoot.transform.localEulerAngles;
            if (Mathf.Abs(Mathf.DeltaAngle(euler.x, 0f)) > 0.1f ||
                Mathf.Abs(Mathf.DeltaAngle(euler.z, 0f)) > 0.1f)
            {
                problems.Add("AircraftRotationRoot 的局部 X/Z 旋转不是 0");
            }
        }

        TwoHandPinchLocalYawRotate controller =
            Object.FindObjectOfType<TwoHandPinchLocalYawRotate>(true);
        if (controller == null)
        {
            problems.Add("缺少 TwoHandPinchLocalYawRotate");
        }

        GameObject anchorsRoot = GameObject.Find(AnchorsRootName);
        if (anchorsRoot == null)
        {
            problems.Add("缺少 AnnotationAnchors");
        }
        else if (anchorsRoot.GetComponentsInChildren<XRBaseInteractable>(true).Length == 0)
        {
            problems.Add("AnnotationAnchors 下还没有 XR 交互热点");
        }

        string message = problems.Count == 0
            ? "当前自动配置项检查通过。锚点位置、Collider 大小和真机手势仍需人工验证。"
            : "发现以下问题：\n\n- " + string.Join("\n- ", problems);

        EditorUtility.DisplayDialog("飞机交互检查", message, "确定");
    }
}
#endif
