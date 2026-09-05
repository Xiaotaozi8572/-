using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using Yilan.Voice.Runtime.UI;

namespace Yilan.Voice.Runtime.Bootstrap
{
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-10000)]
    public sealed class PersistentVoiceAssistant : MonoBehaviour
    {
        private static PersistentVoiceAssistant _instance;

        [SerializeField]
        private Canvas voiceCanvas;

        [SerializeField]
        private Vector3 defaultCameraOffset =
            new Vector3(0f, -0.25f, 1.5f);

        [SerializeField]
        private float defaultWorldScale = 0.0008f;

        [SerializeField]
        private int sortingOrder = 500;

        private VoicePanelAnchor _currentAnchor;
        private Camera _currentCamera;
        private Coroutine _bindingRoutine;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);

            if (voiceCanvas == null)
            {
                voiceCanvas = GetComponentInChildren<Canvas>(true);
            }

            ConfigureCanvas();

            SceneManager.sceneLoaded += OnSceneLoaded;
            StartBinding();
        }

        private void OnDestroy()
        {
            if (_instance != this)
            {
                return;
            }

            SceneManager.sceneLoaded -= OnSceneLoaded;
            _instance = null;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            StartBinding();
        }

        private void StartBinding()
        {
            if (_bindingRoutine != null)
            {
                StopCoroutine(_bindingRoutine);
            }

            _currentAnchor = null;
            _currentCamera = null;
            SetCanvasVisible(false);
            _bindingRoutine = StartCoroutine(BindSceneLayout());
        }

        private IEnumerator BindSceneLayout()
        {
            // VoicePanelAnchor is the explicit per-scene opt-in for displaying the assistant.
            for (int frame = 0; frame < 120; frame++)
            {
                _currentAnchor =
                    FindObjectOfType<VoicePanelAnchor>(true);

                if (_currentAnchor != null)
                {
                    ApplyCurrentLayout();
                    _bindingRoutine = null;
                    yield break;
                }

                yield return null;
            }

            // No anchor is intentional: keep the persistent runtime/network client alive,
            // while hiding only its world-space Canvas for this scene.
            SetCanvasVisible(false);

            _bindingRoutine = null;
        }

        private void LateUpdate()
        {
            // Keep following a moving anchor (for example one parented under an XR camera).
            ApplyCurrentLayout();
        }

        private void ConfigureCanvas()
        {
            if (voiceCanvas == null)
            {
                return;
            }

            voiceCanvas.gameObject.SetActive(true);
            voiceCanvas.enabled = true;
            voiceCanvas.renderMode = RenderMode.WorldSpace;
            voiceCanvas.overrideSorting = true;
            voiceCanvas.sortingOrder = sortingOrder;
        }

        private void ApplyCurrentLayout()
        {
            if (voiceCanvas == null)
            {
                return;
            }

            RectTransform panelTransform =
                voiceCanvas.transform as RectTransform;

            if (panelTransform == null)
            {
                return;
            }

            if (_currentAnchor == null
                || !_currentAnchor.isActiveAndEnabled
                || !_currentAnchor.ShowAssistant)
            {
                SetCanvasVisible(false);
                return;
            }

            panelTransform.SetPositionAndRotation(
                _currentAnchor.transform.position,
                _currentAnchor.transform.rotation);

            SetWorldScale(
                panelTransform,
                _currentAnchor.transform.lossyScale);

            panelTransform.sizeDelta =
                _currentAnchor.PanelSize;

            SetCanvasVisible(true);
        }

        private void SetCanvasVisible(bool visible)
        {
            if (voiceCanvas == null)
            {
                return;
            }

            voiceCanvas.enabled = visible;
            if (voiceCanvas.gameObject.activeSelf != visible)
            {
                voiceCanvas.gameObject.SetActive(visible);
            }
        }

        private Camera FindBestSceneCamera()
        {
            if (IsUsableCamera(Camera.main))
            {
                return Camera.main;
            }

            Camera[] cameras =
                FindObjectsOfType<Camera>(true);

            Camera fallback = null;

            foreach (Camera candidate in cameras)
            {
                if (!IsUsableCamera(candidate))
                {
                    continue;
                }

                if (candidate.stereoTargetEye
                    != StereoTargetEyeMask.None)
                {
                    return candidate;
                }

                if (fallback == null
                    || candidate.depth > fallback.depth)
                {
                    fallback = candidate;
                }
            }

            return fallback;
        }

        private static bool IsUsableCamera(Camera candidate)
        {
            return candidate != null
                && candidate.enabled
                && candidate.gameObject.activeInHierarchy
                && candidate.cameraType == CameraType.Game;
        }

        private static void SetWorldScale(
            Transform target,
            Vector3 desiredWorldScale)
        {
            Transform parent = target.parent;

            if (parent == null)
            {
                target.localScale = desiredWorldScale;
                return;
            }

            Vector3 parentScale = parent.lossyScale;

            target.localScale = new Vector3(
                SafeDivide(desiredWorldScale.x, parentScale.x),
                SafeDivide(desiredWorldScale.y, parentScale.y),
                SafeDivide(desiredWorldScale.z, parentScale.z));
        }

        private static float SafeDivide(float value, float divisor)
        {
            return Mathf.Abs(divisor) < 0.000001f
                ? value
                : value / divisor;
        }
    }
}
