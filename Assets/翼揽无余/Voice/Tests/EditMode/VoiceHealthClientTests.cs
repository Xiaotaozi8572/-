using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Yilan.Voice.Runtime.Transport;

namespace Yilan.Voice.Transport
{
    /// <summary>
    /// EditMode tests for <see cref="VoiceHealthClient"/> classification and log sanitisation.
    /// The real network transport (<see cref="VoiceHealthClient.HttpClientTransport"/>) lives in
    /// Runtime and is not exercised here; tests inject a fake transport through
    /// <see cref="VoiceHealthClient.Transport"/> so classification (200->ok, non-200->degraded,
    /// timeout/refused->unreachable) and the "no query / no body" log guarantees are verifiable
    /// deterministically without a live server or a BCL network dependency in the test asmdef.
    /// Live end-to-end /health is covered by PlayMode / real-device integration.
    /// </summary>
    [TestFixture]
    public class VoiceHealthClientTests
    {
        private const string SecretQuery = "?token=topsecret&sig=abc123";

        [TearDown]
        public void TearDown()
        {
            // Restore the production transport default so no test override leaks forward.
            VoiceHealthClient.Transport = VoiceHealthClient.CreateDefaultTransport();
        }

        // ---------------- /health 200 -> ok ----------------

        [Test]
        public void Health_200_ReturnsOk()
        {
            VoiceHealthClient.Transport = (u, t, c) =>
                Task.FromResult(new VoiceHealthResult(VoiceHealthStatus.Ok, 200, "http 200"));

            var url = new Uri("http://voice.internal:8080/health" + SecretQuery);
            var res = VoiceHealthClient.CheckAsync(url, 300).GetAwaiter().GetResult();
            Assert.AreEqual(VoiceHealthStatus.Ok, res.Status);
            Assert.AreEqual(200, res.HttpStatus);
        }

        // ---------------- non-200 -> degraded ----------------

        [Test]
        public void Health_503_ReturnsDegraded()
        {
            VoiceHealthClient.Transport = (u, t, c) =>
                Task.FromResult(new VoiceHealthResult(VoiceHealthStatus.Degraded, 503, "http 503"));

            var url = new Uri("http://voice.internal:8080/health" + SecretQuery);
            var res = VoiceHealthClient.CheckAsync(url, 300).GetAwaiter().GetResult();
            Assert.AreEqual(VoiceHealthStatus.Degraded, res.Status);
            Assert.AreEqual(503, res.HttpStatus);
        }

        [Test]
        public void Health_404_ReturnsDegraded()
        {
            VoiceHealthClient.Transport = (u, t, c) =>
                Task.FromResult(new VoiceHealthResult(VoiceHealthStatus.Degraded, 404, "http 404"));

            var url = new Uri("http://voice.internal:8080/health" + SecretQuery);
            var res = VoiceHealthClient.CheckAsync(url, 300).GetAwaiter().GetResult();
            Assert.AreEqual(VoiceHealthStatus.Degraded, res.Status);
            Assert.AreEqual(404, res.HttpStatus);
        }

        // ---------------- unreachable: transport exception (refused/dns) ----------------

        [Test]
        public void Health_TransportError_ReturnsUnreachable()
        {
            VoiceHealthClient.Transport = (u, t, c) => throw new InvalidOperationException("refused");

            var url = new Uri("http://voice.internal:8080/health" + SecretQuery);
            var res = VoiceHealthClient.CheckAsync(url, 300).GetAwaiter().GetResult();
            Assert.AreEqual(VoiceHealthStatus.Unreachable, res.Status);
            Assert.IsNull(res.HttpStatus);
        }

        // ---------------- unreachable: timeout ----------------

        [Test]
        public void Health_Timeout_ReturnsUnreachable()
        {
            VoiceHealthClient.Transport = (u, t, c) => throw new OperationCanceledException("timed out");

            var url = new Uri("http://voice.internal:8080/health" + SecretQuery);
            var res = VoiceHealthClient.CheckAsync(url, 300).GetAwaiter().GetResult();
            Assert.AreEqual(VoiceHealthStatus.Unreachable, res.Status);
            Assert.IsNull(res.HttpStatus);
            Assert.AreEqual("timeout", res.Reason);
        }

        // ---------------- log sanitisation ----------------

        [Test]
        public void SanitizedEndpoint_OmitsQueryAndPathSecrets()
        {
            var url = new Uri("http://voice.internal:8080/health" + SecretQuery);
            string sanitized = VoiceHealthClient.SanitizeForLog(url);
            StringAssert.DoesNotContain(SecretQuery, sanitized);
            StringAssert.DoesNotContain("token", sanitized);
            StringAssert.DoesNotContain("secret", sanitized);
            StringAssert.Contains("voice.internal", sanitized);
            StringAssert.Contains("8080", sanitized);
        }

        [Test]
        public void Health_ResultLogSeed_OmitsQueryAndBody()
        {
            VoiceHealthClient.Transport = (u, t, c) =>
                Task.FromResult(new VoiceHealthResult(VoiceHealthStatus.Degraded, 503, "http 503"));

            var url = new Uri("http://voice.internal:8080/health" + SecretQuery);
            var res = VoiceHealthClient.CheckAsync(url, 300).GetAwaiter().GetResult();
            string seed = res.ToSanitizedLogSeed();
            StringAssert.DoesNotContain("token", seed);
            StringAssert.DoesNotContain("secret", seed);
            StringAssert.DoesNotContain("SECRET_BODY_MARKER", seed);
            StringAssert.Contains("503", seed);
        }

        [Test]
        public void HealthUrlFromBase_BuildsHealthPath()
        {
            var baseUri = new Uri("http://voice.internal:8080");
            var url = VoiceHealthClient.HealthUrlFromBase(baseUri);
            Assert.AreEqual("/health", url.AbsolutePath);
            Assert.AreEqual("voice.internal", url.Host);
            Assert.AreEqual(8080, url.Port);
        }
    }
}
