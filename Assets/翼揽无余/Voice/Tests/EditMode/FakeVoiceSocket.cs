using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Yilan.Voice.Runtime.Transport;

namespace Yilan.Voice.Transport
{
    /// <summary>
    /// Test double for <see cref="IVoiceSocket"/> used by the EditMode tests. It mirrors the
    /// real adapter's delivery contract: raised events are queued (thread-safe) and only
    /// delivered to subscribers when <see cref="DispatchMessageQueue"/> is called on the
    /// "main thread" of the test. This lets tests verify connect timeout, send failure,
    /// server close-code mapping, repeated-close idempotency, callback release after
    /// dispose, and main-thread pumping deterministically without a live WebSocket server.
    /// </summary>
    public sealed class FakeVoiceSocket : IVoiceSocket
    {
        private readonly ConcurrentQueue<Action> _pending = new ConcurrentQueue<Action>();
        private readonly object _gate = new object();
        private bool _disposed;
        private int _connectCalls;
        private int _sendTextCalls;
        private int _sendBinaryCalls;
        private int _closeCalls;

        /// <summary>When true <see cref="ConnectAsync"/> completes immediately (success).</summary>
        public bool ConnectSucceeds { get; set; } = true;

        /// <summary>When true <see cref="ConnectAsync"/> returns a never-completing task (timeout).</summary>
        public bool ConnectHangs { get; set; }

        /// <summary>When set, <see cref="ConnectAsync"/> delivers this Error before returning.</summary>
        public VoiceSocketError ConnectError { get; set; }

        /// <summary>When set, <see cref="SendTextAsync"/> and <see cref="SendBinaryAsync"/> deliver this Error.</summary>
        public VoiceSocketError SendError { get; set; }

        /// <summary>When true <see cref="CloseAsync"/> delivers a server-style Closed for the idempotency test.</summary>
        public bool RaiseCloseOnFirstClose { get; set; }

        public event VoiceSocketOpenedHandler Opened;
        public event VoiceSocketPayloadHandler Payload;
        public event VoiceSocketErrorHandler Error;
        public event VoiceSocketClosedHandler Closed;

        public bool IsOpen { get; private set; }

        public int ConnectCalls => _connectCalls;
        public int SendTextCalls => _sendTextCalls;
        public int SendBinaryCalls => _sendBinaryCalls;
        public int CloseCalls => _closeCalls;

        /// <summary>Raw text payloads passed to <see cref="SendTextAsync"/>.</summary>
        public List<string> SentTexts { get; } = new List<string>();

        /// <summary>Raw binary payloads passed to <see cref="SendBinaryAsync"/>.</summary>
        public List<byte[]> SentBinaries { get; } = new List<byte[]>();

        /// <summary>(code, reason) recorded from successful <see cref="CloseAsync"/> calls.</summary>
        public List<(int Code, string Reason)> CloseRequests { get; } = new List<(int, string)>();

        public Task ConnectAsync(Uri url, int timeoutMs = 0)
        {
            lock (_gate)
            {
                _connectCalls++;
                if (_disposed)
                    return Task.FromException(new ObjectDisposedException(nameof(FakeVoiceSocket)));
            }

            if (ConnectHangs)
            {
                // Never complete. Tests wrap this in Task.WhenAny with a delay to observe timeout.
                var never = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                return never.Task;
            }

            if (ConnectError != null)
            {
                Enqueue(() => Error?.Invoke(ConnectError));
                return Task.FromException(new InvalidOperationException(ConnectError.Message));
            }

            if (ConnectSucceeds)
            {
                IsOpen = true;
                Enqueue(() => Opened?.Invoke());
            }
            return Task.CompletedTask;
        }

        public async Task SendTextAsync(string text, int timeoutMs = 0)
        {
            lock (_gate)
            {
                _sendTextCalls++;
                if (_disposed) throw new ObjectDisposedException(nameof(FakeVoiceSocket));
            }
            if (SendError != null)
            {
                Enqueue(() => Error?.Invoke(SendError));
                return;
            }
            await Task.CompletedTask;
            lock (_gate) { if (IsOpen) SentTexts.Add(text); }
        }

        public async Task SendBinaryAsync(byte[] data, int timeoutMs = 0)
        {
            lock (_gate)
            {
                _sendBinaryCalls++;
                if (_disposed) throw new ObjectDisposedException(nameof(FakeVoiceSocket));
            }
            if (SendError != null)
            {
                Enqueue(() => Error?.Invoke(SendError));
                return;
            }
            await Task.CompletedTask;
            lock (_gate) { if (IsOpen) SentBinaries.Add((byte[])data.Clone()); }
        }

        public async Task CloseAsync(int code, string reason, int timeoutMs = 0)
        {
            lock (_gate)
            {
                _closeCalls++;
                if (_disposed) return; // idempotent no-op after dispose
                if (!IsOpen) return;   // idempotent: already closed
                IsOpen = false;
                CloseRequests.Add((code, reason));
                if (RaiseCloseOnFirstClose)
                {
                    // Server-style closed event, delivered on the next pump.
                    Enqueue(() => Closed?.Invoke(new VoiceCloseInfo(code, VoiceCloseMapper.Classify(code), reason)));
                    RaiseCloseOnFirstClose = false;
                }
            }
            await Task.CompletedTask;
        }

        /// <summary>Raise a server-originated open.</summary>
        public void SimulateOpen()
        {
            if (_disposed) return;
            IsOpen = true;
            Enqueue(() => Opened?.Invoke());
        }

        /// <summary>Raise a raw payload (bytes) as if received from the server.</summary>
        public void SimulatePayload(byte[] data)
        {
            if (_disposed) return;
            Enqueue(() => Payload?.Invoke((byte[])data.Clone()));
        }

        /// <summary>Raise a transport error.</summary>
        public void SimulateError(VoiceSocketError error)
        {
            if (_disposed) return;
            Enqueue(() => Error?.Invoke(error));
        }

        /// <summary>Raise a server close with the given code/reason -> classified on delivery.</summary>
        public void SimulateServerClose(int code, string reason)
        {
            if (_disposed) return;
            Enqueue(() => Closed?.Invoke(new VoiceCloseInfo(code, VoiceCloseMapper.Classify(code), reason)));
        }

        /// <summary>Mirror of the real adapter: drain the thread-safe queue on the calling thread.</summary>
        public void DispatchMessageQueue()
        {
            while (_pending.TryDequeue(out var action))
                action();
        }

        public void Dispose() => Dispose(true);

        private void Dispose(bool disposing)
        {
            if (!disposing) return;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                IsOpen = false;
            }
            // Unsubscribe all callbacks so no further events reach a disposed consumer.
            Opened = null;
            Payload = null;
            Error = null;
            Closed = null;
            while (_pending.TryDequeue(out _)) { }
        }

        private void Enqueue(Action action)
        {
            if (_disposed) return;
            _pending.Enqueue(action);
        }
    }
}
