using System.Collections.Generic;
using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;

/// <summary>
/// 给选中的爆炸模型根物体下所有独立网格批量配置抓取、旋转及双手缩放交互。
/// </summary>
public static class AircraftPartInteractionBatchTool
{
    private const string ConfigureMenu = "工具/翼揽无余/爆炸图/给选中模型批量添加零件交互";
    private const string RemoveMenu = "工具/翼揽无余/爆炸图/移除选中模型的批量零件交互";

    [MenuItem(ConfigureMenu, true)]
    private static bool ValidateConfigure()
    {
        return Selection.activeGameObject != null && !EditorApplication.isPlaying;
    }

    [MenuItem(ConfigureMenu)]
    private static void ConfigureSelectedRoot()
    {
        GameObject root = Selection.activeGameObject;
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);

        if (renderers.Length == 0)
        {
            EditorUtility.DisplayDialog("没有找到零件",
                "选中的物体下面没有 Renderer。请在层级中选择包含147个零件的爆炸模型根物体。", "确定");
            return;
        }

        int configured = 0;
        int skipped = 0;
        HashSet<GameObject> processed = new HashSet<GameObject>();

        Undo.SetCurrentGroupName("批量配置爆炸零件交互");
        int undoGroup = Undo.GetCurrentGroup();

        try
        {
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                GameObject part = renderer.gameObject;

                if (part == root || !processed.Add(part))
                {
                    skipped++;
                    continue;
                }

                EditorUtility.DisplayProgressBar("正在配置爆炸零件交互",
                    $"{i + 1}/{renderers.Length}  {part.name}",
                    (i + 1f) / renderers.Length);

                ConfigureCollider(part, renderer);
                ConfigureRigidbody(part);
                ConfigureGrabInteractable(part);
                configured++;
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            Undo.CollapseUndoOperations(undoGroup);
        }

        EditorSceneManager.MarkSceneDirty(root.scene);
        Selection.activeGameObject = root;

        EditorUtility.DisplayDialog("批量配置完成",
            $"已配置 {configured} 个零件。\n跳过 {skipped} 个根物体或重复 Renderer。\n\n请按 Ctrl+S 保存场景。", "确定");
    }

    private static void ConfigureCollider(GameObject part, Renderer renderer)
    {
        Collider existingCollider = part.GetComponent<Collider>();
        if (existingCollider != null)
            return;

        // Hundreds of per-component Undo records can overflow Unity's Undo stack.
        // The batch tool writes components directly and relies on a scene backup.
        BoxCollider collider = part.AddComponent<BoxCollider>();
        Bounds localBounds = renderer.localBounds;
        Vector3 size = localBounds.size;

        // 太薄的零件稍微扩充点击范围，方便PICO手势射线选中。
        const float minimumSize = 0.015f;
        size.x = Mathf.Max(size.x, minimumSize);
        size.y = Mathf.Max(size.y, minimumSize);
        size.z = Mathf.Max(size.z, minimumSize);

        collider.center = localBounds.center;
        collider.size = size;
        EditorUtility.SetDirty(collider);
    }

    private static void ConfigureRigidbody(GameObject part)
    {
        Rigidbody body = part.GetComponent<Rigidbody>();
        if (body == null)
            body = part.AddComponent<Rigidbody>();

        body.useGravity = false;
        body.isKinematic = true;
        body.interpolation = RigidbodyInterpolation.Interpolate;
        body.collisionDetectionMode = CollisionDetectionMode.Discrete;
        EditorUtility.SetDirty(body);
    }

    private static void ConfigureGrabInteractable(GameObject part)
    {
        Component transformer = FindOrAddGeneralGrabTransformer(part);

        XRGrabInteractable grab = part.GetComponent<XRGrabInteractable>();
        if (grab == null)
            grab = part.AddComponent<XRGrabInteractable>();

        grab.selectMode = InteractableSelectMode.Multiple;
        grab.movementType = XRBaseInteractable.MovementType.Kinematic;
        grab.throwOnDetach = false;
        grab.trackPosition = true;
        grab.trackRotation = true;
        grab.trackScale = true;
        grab.useDynamicAttach = true;
        // XRI 会自动使用同物体上的 XR General Grab Transformer。
        grab.addDefaultGrabTransformers = true;
        EditorUtility.SetDirty(grab);
    }

    private static Component FindOrAddGeneralGrabTransformer(GameObject part)
    {
        Type transformerType = Type.GetType(
            "UnityEngine.XR.Interaction.Toolkit.Transformers.XRGeneralGrabTransformer, Unity.XR.Interaction.Toolkit");

        if (transformerType == null)
        {
            Debug.LogWarning("没有找到 XR General Grab Transformer 类型，将仅配置基础抓取交互。", part);
            return null;
        }

        Component transformer = part.GetComponent(transformerType);
        if (transformer == null)
            transformer = part.AddComponent(transformerType);

        // 通过序列化字段配置，以兼容 XRI 2.x 不同小版本的命名空间差异。
        SerializedObject serializedTransformer = new SerializedObject(transformer);
        SetBool(serializedTransformer, "m_AllowOneHandedScaling", false);
        SetBool(serializedTransformer, "m_AllowTwoHandedScaling", true);
        SetBool(serializedTransformer, "m_ClampScaling", true);
        SetFloat(serializedTransformer, "m_MinimumScaleRatio", 0.35f);
        SetFloat(serializedTransformer, "m_MaximumScaleRatio", 3f);
        SerializedProperty rotationMode = serializedTransformer.FindProperty("m_TwoHandedRotationMode");
        if (rotationMode != null)
            rotationMode.enumValueIndex = 0;
        serializedTransformer.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(transformer);
        return transformer;
    }

    private static void SetBool(SerializedObject target, string propertyName, bool value)
    {
        SerializedProperty property = target.FindProperty(propertyName);
        if (property != null)
            property.boolValue = value;
    }

    private static void SetFloat(SerializedObject target, string propertyName, float value)
    {
        SerializedProperty property = target.FindProperty(propertyName);
        if (property != null)
            property.floatValue = value;
    }

    [MenuItem(RemoveMenu, true)]
    private static bool ValidateRemove()
    {
        return Selection.activeGameObject != null && !EditorApplication.isPlaying;
    }

    [MenuItem(RemoveMenu)]
    private static void RemoveFromSelectedRoot()
    {
        GameObject root = Selection.activeGameObject;
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        HashSet<GameObject> processed = new HashSet<GameObject>();
        int removed = 0;

        Undo.SetCurrentGroupName("移除批量爆炸零件交互");
        int undoGroup = Undo.GetCurrentGroup();

        for (int i = 0; i < renderers.Length; i++)
        {
            GameObject part = renderers[i].gameObject;
            if (part == root || !processed.Add(part))
                continue;

            XRGrabInteractable grab = part.GetComponent<XRGrabInteractable>();
            Component transformer = FindGeneralGrabTransformer(part);
            Rigidbody body = part.GetComponent<Rigidbody>();
            BoxCollider box = part.GetComponent<BoxCollider>();

            if (grab != null) Undo.DestroyObjectImmediate(grab);
            if (transformer != null) Undo.DestroyObjectImmediate(transformer);
            if (body != null) Undo.DestroyObjectImmediate(body);
            if (box != null) Undo.DestroyObjectImmediate(box);
            removed++;
        }

        Undo.CollapseUndoOperations(undoGroup);
        EditorSceneManager.MarkSceneDirty(root.scene);
        EditorUtility.DisplayDialog("移除完成", $"已处理 {removed} 个零件。请按 Ctrl+S 保存场景。", "确定");
    }

    private static Component FindGeneralGrabTransformer(GameObject part)
    {
        MonoBehaviour[] behaviours = part.GetComponents<MonoBehaviour>();
        for (int i = 0; i < behaviours.Length; i++)
        {
            if (behaviours[i] != null && behaviours[i].GetType().Name == "XRGeneralGrabTransformer")
                return behaviours[i];
        }
        return null;
    }
}
