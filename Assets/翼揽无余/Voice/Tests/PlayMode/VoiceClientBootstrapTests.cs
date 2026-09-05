using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Yilan.Voice.Runtime.Bootstrap;
using Yilan.Voice.Runtime.UI;

namespace Yilan.Voice.Remediation
{
    public class VoiceClientBootstrapTests
    {
        [UnityTest]
        public IEnumerator Bootstrap_MissingAudioSource_RefusesStartup()
        {
            var go = new GameObject("voice-bootstrap-test");
            go.AddComponent<VoicePanel>();
            var bootstrap = go.AddComponent<VoiceClientBootstrap>();

            Assert.IsFalse(bootstrap.ValidateReferences(out var code));
            Assert.IsFalse(bootstrap.IsValid);
            Assert.AreEqual("bootstrap_invalid", code);

            Object.Destroy(go);
            yield return null;
        }

        [UnityTest]
        public IEnumerator Bootstrap_MissingPanel_RefusesStartup()
        {
            var go = new GameObject("voice-bootstrap-test");
            go.AddComponent<AudioSource>();
            var bootstrap = go.AddComponent<VoiceClientBootstrap>();

            Assert.IsFalse(bootstrap.ValidateReferences(out var code));
            Assert.IsFalse(bootstrap.IsValid);
            Assert.AreEqual("bootstrap_invalid", code);

            Object.Destroy(go);
            yield return null;
        }

        [UnityTest]
        public IEnumerator Bootstrap_MissingConfig_RefusesStartup()
        {
            var go = BuildValidBootstrapRoot();
            var bootstrap = go.GetComponent<VoiceClientBootstrap>();
            SetPrivateField(bootstrap, "wsHost", string.Empty);

            Assert.IsFalse(bootstrap.ValidateReferences(out var code));
            Assert.IsFalse(bootstrap.IsValid);
            Assert.AreEqual("bootstrap_invalid", code);

            Object.Destroy(go);
            yield return null;
        }

        [UnityTest]
        public IEnumerator Bootstrap_ValidReferences_PassesValidation()
        {
            var go = BuildValidBootstrapRoot();
            var bootstrap = go.GetComponent<VoiceClientBootstrap>();

            Assert.IsTrue(bootstrap.ValidateReferences(out var code));
            Assert.IsTrue(bootstrap.IsValid);
            Assert.AreEqual("ok", code);

            Object.Destroy(go);
            yield return null;
        }

        private static GameObject BuildValidBootstrapRoot()
        {
            var root = new GameObject("voice-bootstrap-test");
            root.AddComponent<AudioSource>();
            var panel = root.AddComponent<VoicePanel>();
            var bootstrap = root.AddComponent<VoiceClientBootstrap>();

            var canvas = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
            canvas.transform.SetParent(root.transform, false);
            canvas.layer = 5;

            SetPrivateField(panel, "stateText", CreateText(canvas.transform, "StateText"));
            SetPrivateField(panel, "answerText", CreateText(canvas.transform, "AnswerText"));
            SetPrivateField(panel, "clarificationText", CreateText(canvas.transform, "ClarificationText"));
            SetPrivateField(bootstrap, "panel", panel);
            SetPrivateField(bootstrap, "audioSource", root.GetComponent<AudioSource>());

            return root;
        }

        private static Text CreateText(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            go.transform.SetParent(parent, false);
            go.layer = 5;
            return go.GetComponent<Text>();
        }

        private static void SetPrivateField(object target, string fieldName, object value)
        {
            var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "missing private field: " + fieldName);
            field.SetValue(target, value);
        }
    }
}
