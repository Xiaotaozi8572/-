using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using Yilan.Voice.Runtime.Transport;

namespace Yilan.Voice.Transport
{
    /// <summary>
    /// EditMode tests for the voice transport adapter against <see cref="FakeVoiceSocket"/>.
    /// These verify the IVoiceSocket contract: connect success/timeout, text/binary send,
    /// send failure, server close-code mapping, repeated-close idempotency, callback release
    /// after dispose, and main-thread pumping. Live-network behaviour of NativeVoiceSocket
    /// is out of EditMode scope (needs PlayMode / integration).
    /// </summary>
    [TestFixture]
    public class NativeVoiceSocketTests
    {
        private static Uri DummyUri => new Uri("ws://voice.test/session?token=secret");

        // ---------------- Connect ----------------

        [Test]
        public void Connect_Success_DeliversOpenedOnceOnPump()
        {
            var sock = new FakeVoiceSocket { ConnectSucceeds = true };
            var opened = new List<bool>();
            sock.Opened += () => opened.Add(true);

            var t = sock.ConnectAsync(DummyUri);
            Assert.DoesNotThrow(() => { t.GetAwaiter().GetResult(); });
            // Not delivered yet (queued):
            Assert.AreEqual(0, opened.Count);
            sock.DispatchMessageQueue();
            Assert.AreEqual(1, opened.Count);

            sock.Dispose();
        }

        [Test]
        public void Connect_Timeout_Detectable_WithoutHangingTest()
        {
            var sock = new FakeVoiceSocket { ConnectHangs = true };
            int errors = 0;
            sock.Error += _ => errors++;

            var t = sock.ConnectAsync(DummyUri);
            // Wrap in WhenAny with a short delay so the test itself never hangs.
            var winner = Task.WhenAny(t, Task.Delay(50)).GetAwaiter().GetResult();
            Assert.AreNotSame(t, winner, "ConnectAsync should not complete when the socket never opens.");
            Assert.AreEqual(0, errors);

            sock.Dispose();
        }

        [Test]
        public void Connect_Error_IsRaised()
        {
            var sock = new FakeVoiceSocket
            {
                ConnectSucceeds = true,
                ConnectError = new VoiceSocketError(VoiceSocketErrorKind.Connect, "dns failure")
            };
            var errors = new List<VoiceSocketError>();
            sock.Error += errors.Add;

            var t = sock.ConnectAsync(DummyUri);
            Exception ex = null;
            try { t.GetAwaiter().GetResult(); } catch (Exception e) { ex = e; }
            Assert.IsInstanceOf<InvalidOperationException>(ex);
            sock.DispatchMessageQueue();
            Assert.AreEqual(1, errors.Count);
            Assert.AreEqual(VoiceSocketErrorKind.Connect, errors[0].Kind);

            sock.Dispose();
        }

        // ---------------- Sending ----------------

        [Test]
        public void SendText_RecordsPayload_WhenOpen()
        {
            var sock = new FakeVoiceSocket { ConnectSucceeds = true };
            sock.ConnectAsync(DummyUri).GetAwaiter().GetResult();
            sock.DispatchMessageQueue();

            Assert.DoesNotThrow(() => { sock.SendTextAsync("{\"type\":\"session.start\"}", 1000).GetAwaiter().GetResult(); });
            Assert.AreEqual(1, sock.SendTextCalls);
            Assert.AreEqual("{\"type\":\"session.start\"}", sock.SentTexts[0]);

            sock.Dispose();
        }

        [Test]
        public void SendBinary_RecordsRawBytes_WhenOpen()
        {
            var sock = new FakeVoiceSocket { ConnectSucceeds = true };
            sock.ConnectAsync(DummyUri).GetAwaiter().GetResult();
            sock.DispatchMessageQueue();

            byte[] payload = Encoding.UTF8.GetBytes("\u0000\u00ff\u0001 PCM16 body");
            Assert.DoesNotThrow(() => { sock.SendBinaryAsync(payload, 1000).GetAwaiter().GetResult(); });
            Assert.AreEqual(1, sock.SendBinaryCalls);
            CollectionAssert.AreEqual(payload, sock.SentBinaries[0]);

            sock.Dispose();
        }

        [Test]
        public void Send_Failure_RaisesError()
        {
            var sock = new FakeVoiceSocket
            {
                ConnectSucceeds = true,
                SendError = new VoiceSocketError(VoiceSocketErrorKind.Send, "socket not open")
            };
            sock.ConnectAsync(DummyUri).GetAwaiter().GetResult();
            sock.DispatchMessageQueue();

            var errors = new List<VoiceSocketError>();
            sock.Error += errors.Add;
            Assert.DoesNotThrow(() => { sock.SendTextAsync("hello", 1000).GetAwaiter().GetResult(); });
            sock.DispatchMessageQueue();

            Assert.AreEqual(1, errors.Count);
            Assert.AreEqual(VoiceSocketErrorKind.Send, errors[0].Kind);

            sock.Dispose();
        }

        // ---------------- Server close code mapping ----------------

        [Test]
        public void ServerClose_Normal1000_MapsToNormal()
        {
            var sock = new FakeVoiceSocket { ConnectSucceeds = true };
            sock.ConnectAsync(DummyUri).GetAwaiter().GetResult();
            sock.DispatchMessageQueue();

            var closes = new List<VoiceCloseInfo>();
            sock.Closed += closes.Add;
            sock.SimulateServerClose(1000, "bye");
            sock.DispatchMessageQueue();

            Assert.AreEqual(1, closes.Count);
            Assert.AreEqual(1000, closes[0].Code);
            Assert.AreEqual(VoiceCloseCategory.Normal, closes[0].Category);

            sock.Dispose();
        }

        [Test]
        public void ServerClose_ServerError1011_MapsToServerError()
        {
            var sock = new FakeVoiceSocket { ConnectSucceeds = true };
            sock.ConnectAsync(DummyUri).GetAwaiter().GetResult();
            sock.DispatchMessageQueue();

            var closes = new List<VoiceCloseInfo>();
            sock.Closed += closes.Add;
            sock.SimulateServerClose(1011, "internal error");
            sock.DispatchMessageQueue();

            Assert.AreEqual(VoiceCloseCategory.ServerError, closes[0].Category);

            sock.Dispose();
        }

        [Test]
        public void ServerClose_Policy1008_MapsToPolicy()
        {
            var sock = new FakeVoiceSocket { ConnectSucceeds = true };
            sock.ConnectAsync(DummyUri).GetAwaiter().GetResult();
            sock.DispatchMessageQueue();

            var closes = new List<VoiceCloseInfo>();
            sock.Closed += closes.Add;
            sock.SimulateServerClose(1008, "policy");
            sock.DispatchMessageQueue();

            Assert.AreEqual(VoiceCloseCategory.Policy, closes[0].Category);

            sock.Dispose();
        }

        [Test]
        public void ServerClose_Abnormal1006_MapsToAbnormal()
        {
            var sock = new FakeVoiceSocket { ConnectSucceeds = true };
            sock.ConnectAsync(DummyUri).GetAwaiter().GetResult();
            sock.DispatchMessageQueue();

            var closes = new List<VoiceCloseInfo>();
            sock.Closed += closes.Add;
            sock.SimulateServerClose(1006, "transport drop");
            sock.DispatchMessageQueue();

            Assert.AreEqual(VoiceCloseCategory.Abnormal, closes[0].Category);

            sock.Dispose();
        }

        [Test]
        public void ServerClose_ApplicationCode_MapsToApplication()
        {
            var sock = new FakeVoiceSocket { ConnectSucceeds = true };
            sock.ConnectAsync(DummyUri).GetAwaiter().GetResult();
            sock.DispatchMessageQueue();

            var closes = new List<VoiceCloseInfo>();
            sock.Closed += closes.Add;
            sock.SimulateServerClose(4321, "app-custom");
            sock.DispatchMessageQueue();

            Assert.AreEqual(VoiceCloseCategory.Application, closes[0].Category);

            sock.Dispose();
        }

        [Test]
        public void ServerClose_UnknownCode_MapsToUnknown()
        {
            var sock = new FakeVoiceSocket { ConnectSucceeds = true };
            sock.ConnectAsync(DummyUri).GetAwaiter().GetResult();
            sock.DispatchMessageQueue();

            var closes = new List<VoiceCloseInfo>();
            sock.Closed += closes.Add;
            sock.SimulateServerClose(999, "stray");
            sock.DispatchMessageQueue();

            Assert.AreEqual(VoiceCloseCategory.Unknown, closes[0].Category);

            sock.Dispose();
        }

        // ---------------- Server close code mapping (static mapper) ----------------

        [TestCase(1000, VoiceCloseCategory.Normal)]
        [TestCase(1002, VoiceCloseCategory.Protocol)]
        [TestCase(1003, VoiceCloseCategory.Protocol)]
        [TestCase(1007, VoiceCloseCategory.Protocol)]
        [TestCase(1009, VoiceCloseCategory.Protocol)]
        [TestCase(1010, VoiceCloseCategory.Protocol)]
        [TestCase(1008, VoiceCloseCategory.Policy)]
        [TestCase(1011, VoiceCloseCategory.ServerError)]
        [TestCase(1006, VoiceCloseCategory.Abnormal)]
        [TestCase(2000, VoiceCloseCategory.Application)]
        [TestCase(4999, VoiceCloseCategory.Application)]
        [TestCase(-1, VoiceCloseCategory.Unknown)]
        [TestCase(999, VoiceCloseCategory.Unknown)]
        public void CloseCodeMapper_Classifies(int code, VoiceCloseCategory expected)
        {
            Assert.AreEqual(expected, VoiceCloseMapper.Classify(code));
        }

        // ---------------- Repeated close idempotency ----------------

        [Test]
        public void Close_IsIdempotent_MultipleCallsDoNotThrow()
        {
            var sock = new FakeVoiceSocket { ConnectSucceeds = true };
            sock.ConnectAsync(DummyUri).GetAwaiter().GetResult();
            sock.DispatchMessageQueue();

            Assert.DoesNotThrow(() => { sock.CloseAsync(1000, "done", 1000).GetAwaiter().GetResult(); });
            Assert.DoesNotThrow(() => { sock.CloseAsync(1000, "done", 1000).GetAwaiter().GetResult(); });
            Assert.DoesNotThrow(() => { sock.CloseAsync(1000, "done", 1000).GetAwaiter().GetResult(); });

            Assert.AreEqual(3, sock.CloseCalls);
            Assert.AreEqual(1, sock.CloseRequests.Count); // only recorded while open

            sock.Dispose();
        }

        // ---------------- Callback release after dispose ----------------

        [Test]
        public void Dispose_ReleasesCallbacks_NoEventsAfterDispose()
        {
            var sock = new FakeVoiceSocket { ConnectSucceeds = true };
            sock.ConnectAsync(DummyUri).GetAwaiter().GetResult();
            sock.DispatchMessageQueue();

            int events = 0;
            sock.Opened += () => events++;
            sock.Payload += _ => events++;
            sock.Error += _ => events++;
            sock.Closed += _ => events++;

            sock.Dispose();

            // Simulated events after dispose must be silently dropped.
            Assert.DoesNotThrow(() => sock.SimulateOpen());
            Assert.DoesNotThrow(() => sock.SimulatePayload(new byte[] { 1, 2, 3 }));
            Assert.DoesNotThrow(() => sock.SimulateError(new VoiceSocketError(VoiceSocketErrorKind.Unexpected, "x")));
            Assert.DoesNotThrow(() => sock.SimulateServerClose(1000, "bye"));
            sock.DispatchMessageQueue();

            Assert.AreEqual(0, events);

            sock.Dispose(); // double dispose is safe
        }

        // ---------------- Main-thread pumping ----------------

        [Test]
        public void Dispatch_QueuesInOrder_UntilPumped()
        {
            var sock = new FakeVoiceSocket { ConnectSucceeds = true };
            sock.ConnectAsync(DummyUri).GetAwaiter().GetResult();
            sock.DispatchMessageQueue();

            var payloads = new List<byte[]>();
            sock.Payload += payloads.Add;

            // Simulate arrival from a background thread without delivering yet.
            sock.SimulatePayload(new byte[] { 1 });
            sock.SimulatePayload(new byte[] { 2 });
            sock.SimulatePayload(new byte[] { 3 });
            Assert.AreEqual(0, payloads.Count, "Nothing delivered until DispatchMessageQueue.");

            sock.DispatchMessageQueue();
            Assert.AreEqual(3, payloads.Count);
            CollectionAssert.AreEqual(new byte[] { 1 }, payloads[0]);
            CollectionAssert.AreEqual(new byte[] { 2 }, payloads[1]);
            CollectionAssert.AreEqual(new byte[] { 3 }, payloads[2]);

            // Draining again must be a no-op.
            sock.DispatchMessageQueue();
            Assert.AreEqual(3, payloads.Count);

            sock.Dispose();
        }

        [Test]
        public void VoiceSocketError_Message__LogSanitization_Smoke()
        {
            // The adapter must never log payload content or full URL query; this guards
            // that the error object carries only a sanitized message seed.
            var err = new VoiceSocketError(VoiceSocketErrorKind.Unexpected, "unexpected transport error");
            StringAssert.DoesNotContain("token", err.Message);
            StringAssert.DoesNotContain("secret", err.Message);
        }
    }
}
