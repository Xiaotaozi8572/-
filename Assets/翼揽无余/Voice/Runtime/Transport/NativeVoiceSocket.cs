using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Yilan.Voice.Runtime.Transport
{
    /// <summary>
    /// Adapter over the approved NativeWebSocket baseline package
    /// (<c>com.endel.nativewebsocket</c>, assembly <c>colyseus.nativewebsocket</c>).
    ///
    /// Design rules enforced here:
    ///   * Raw binary frames are surfaced verbatim on <see cref="Payload"/> — no UTF-8 or
    ///     content sniffing. Whether a byte stream is a TTS PCM16 body is decided by the
    ///     protocol layer's pending state (<see cref="Yilan.Voice.Runtime.Protocol.TtsBinaryPairer"/>),
    ///     never by guessing here.
    ///   * ALL outgoing messages go through a strict-FIFO outbox. The package keeps separate
    ///     internal queues for text and binary sends, so once any send is in flight, later
    ///     text frames drain ahead of queued binary frames (and vice versa) — reordering
    ///     protocol header+body pairs on the wire. Serializing every send through one queue
    ///     and awaiting each library send to completion keeps the package's internal queues
    ///     out of the picture and preserves global send order.
    ///   * Events are delivered on the main thread: the package queues events and the caller
    ///     pumps them with <see cref="DispatchMessageQueue"/> (typically from Unity Update).
    ///   * Connect/send/close honour a timeout and classify failures via
    ///     <see cref="VoiceSocketErrorKind"/>. Server close codes map through
    ///     <see cref="VoiceCloseMapper"/>.
    ///   * <see cref="CloseAsync"/> is idempotent; <see cref="Dispose"/> (OnDestroy) unsubscribes
    ///     every callback so no event reaches a destroyed consumer, and fails any queued
    ///     outbox sends.
    ///   * Logging never includes payload content or the full URL query — only length/type/
    ///     category/state and a host-only endpoint.
    /// </summary>
    public sealed class NativeVoiceSocket : IVoiceSocket
    {
        public const int DefaultTimeoutMs = 8000;

        private readonly object _gate = new object();
        private NativeWebSocket.WebSocket _socket;
        private TaskCompletionSource<bool> _openTcs;
        private bool _disposed;
        private bool _closedFired;      // dedupe server close event
        private bool _userClosePending; // suppress our own close echo

        // Strict-FIFO outgoing pump (see class doc: the library's split text/binary queues
        // reorder header+body pairs; this outbox restores global send order).
        private readonly object _outboxLock = new object();
        private readonly Queue<_OutboxItem> _outbox = new Queue<_OutboxItem>();
        private bool _pumpRunning;

        private sealed class _OutboxItem
        {
            public bool IsText;
            public string Text;      // set when IsText
            public byte[] Binary;    // set when !IsText
            public int TimeoutMs;
            public volatile bool Succeeded;
            public volatile string FailReason;
            public readonly TaskCompletionSource<bool> Done =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public event VoiceSocketOpenedHandler Opened;
        public event VoiceSocketPayloadHandler Payload;
        public event VoiceSocketErrorHandler Error;
        public event VoiceSocketClosedHandler Closed;

        /// <summary>Endpoint this socket was constructed against (host-only for safe logging).</summary>
        public Uri Target { get; }

        public bool IsOpen
        {
            get
            {
                lock (_gate)
                {
                    return _socket != null && _socket.State == NativeWebSocket.WebSocketState.Open;
                }
            }
        }

        public NativeVoiceSocket(Uri url)
        {
            if (url == null) throw new ArgumentNullException(nameof(url));
            string scheme = (url.Scheme ?? string.Empty).ToLowerInvariant();
            if (scheme != "ws" && scheme != "wss")
                throw new ArgumentException("Unsupported WebSocket scheme: " + scheme, nameof(url));
            Target = url;
        }

        public void DispatchMessageQueue()
        {
            NativeWebSocket.WebSocket s;
            lock (_gate) { s = _socket; }
            s?.DispatchMessageQueue();
        }

        public async Task ConnectAsync(Uri url, int timeoutMs = 0)
        {
            if (url == null) throw new ArgumentNullException(nameof(url));
            int timeout = timeoutMs > 0 ? timeoutMs : DefaultTimeoutMs;

            TaskCompletionSource<bool> tcs;
            lock (_gate)
            {
                ThrowIfDisposedLocked();
                _openTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                tcs = _openTcs;
                CreateAndSubscribeLocked(url);
            }

            // Fire-and-forget connect; open is signalled by the OnOpen handler through tcs.
            _ = _socket.Connect();

            var deadline = DateTime.UtcNow.AddMilliseconds(timeout);
            while (!tcs.Task.IsCompleted)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    RaiseError(VoiceSocketErrorKind.Timeout, "connect timeout");
                    await QuietAbortAsync();
                    return;
                }
                _socket.DispatchMessageQueue();
                await Task.Delay(5).ConfigureAwait(false);
            }

            await tcs.Task.ConfigureAwait(false);
            LogState("connected", $"state={_socket.State}");
        }

        public async Task SendTextAsync(string text, int timeoutMs = 0)
        {
            int timeout = timeoutMs > 0 ? timeoutMs : DefaultTimeoutMs;
            NativeWebSocket.WebSocket s;
            lock (_gate) { s = _socket; }

            if (s == null || !IsOpen)
            {
                RaiseError(VoiceSocketErrorKind.InvalidState, "send text: socket not open");
                return;
            }

            bool ok = await EnqueueOutbox(true, text ?? string.Empty, null, timeout).ConfigureAwait(false);
            if (ok) LogState("send-text", $"bytes={(text ?? string.Empty).Length}");
        }

        public async Task SendBinaryAsync(byte[] data, int timeoutMs = 0)
        {
            int timeout = timeoutMs > 0 ? timeoutMs : DefaultTimeoutMs;
            NativeWebSocket.WebSocket s;
            lock (_gate) { s = _socket; }

            if (s == null || !IsOpen)
            {
                RaiseError(VoiceSocketErrorKind.InvalidState, "send binary: socket not open");
                return;
            }

            bool ok = await EnqueueOutbox(false, null, data ?? Array.Empty<byte>(), timeout).ConfigureAwait(false);
            if (ok) LogState("send-binary", $"bytes={(data?.Length ?? 0)}");
        }

        public async Task CloseAsync(int code, string reason, int timeoutMs = 0)
        {
            int timeout = timeoutMs > 0 ? timeoutMs : DefaultTimeoutMs;
            NativeWebSocket.WebSocket s;
            lock (_gate)
            {
                if (_disposed) return; // idempotent no-op after dispose
                _userClosePending = true;
                s = _socket;
            }

            if (s == null || !IsOpen)
            {
                LogState("close", "already closed (no-op)");
                return;
            }

            try
            {
                var close = s.Close((NativeWebSocket.WebSocketCloseCode)code, reason ?? string.Empty);
                await WaitOrTimeoutAsync(close, timeout, VoiceSocketErrorKind.Unexpected, "close");
                LogState("close", $"code={code}");
            }
            catch (Exception ex)
            {
                RaiseError(VoiceSocketErrorKind.Unexpected, "close failed: " + ex.GetType().Name);
            }
            finally
            {
                lock (_gate) { _userClosePending = false; }
            }
        }

        public void Dispose()
        {
            NativeWebSocket.WebSocket s;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _userClosePending = true;
                s = _socket;
                _socket = null;
            }

            if (s != null)
            {
                try { s.CancelConnection(); } catch { }
                // NativeWebSocket.WebSocket disposes its inner ClientWebSocket internally; there is
                // no public Dispose on the adapter, so cancelling the connection is sufficient.
            }

            // Fail any queued outbox sends so their awaiters do not hang.
            lock (_outboxLock)
            {
                while (_outbox.Count > 0)
                {
                    var item = _outbox.Dequeue();
                    item.FailReason = "disposed";
                    item.Done.TrySetResult(false);
                }
            }

            // Unsubscribe every callback so nothing reaches a destroyed consumer.
            _openTcs = null;
            Opened = null;
            Payload = null;
            Error = null;
            Closed = null;
            LogState("dispose", "transport released");
        }

        // ---------------- internals ----------------

        private void CreateAndSubscribeLocked(Uri url)
        {
            var s = new NativeWebSocket.WebSocket(url.ToString());
            s.OnOpen += () =>
            {
                lock (_gate)
                {
                    _openTcs?.TrySetResult(true);
                }
                LogState("on-open", null);
                Opened?.Invoke();
            };
            s.OnMessage += bytes =>
            {
                // Raw bytes, verbatim. Text/binary judgement is left to the protocol's
                // pending state; never decode or sniff here.
                LogState("on-payload", $"bytes={bytes?.Length ?? 0}");
                Payload?.Invoke(bytes ?? Array.Empty<byte>());
            };
            s.OnError += msg =>
            {
                // The underlying message may embed host details; log only the classification.
                LogState("on-error", null);
                RaiseError(VoiceSocketErrorKind.Unexpected, "native socket error");
            };
            s.OnClose += code =>
            {
                int codeValue = (int)code;
                bool userClose;
                lock (_gate)
                {
                    if (_closedFired) return;
                    _closedFired = true;
                    userClose = _userClosePending;
                }
                if (!userClose)
                    LogState("on-close", $"code={codeValue} category={VoiceCloseMapper.Classify(codeValue)}");
                Closed?.Invoke(new VoiceCloseInfo(codeValue, VoiceCloseMapper.Classify(codeValue), null));
            };
            _socket = s;
        }

        // ---------------- strict-FIFO outbox ----------------

        /// <summary>
        /// Enqueue one outgoing message and start the pump if idle. Returns a task that
        /// completes with true when the message was handed to the library and its send
        /// completed, false on failure/timeout/dispose.
        /// </summary>
        private Task<bool> EnqueueOutbox(bool isText, string text, byte[] binary, int timeoutMs)
        {
            var item = new _OutboxItem
            {
                IsText = isText,
                Text = text,
                Binary = binary,
                TimeoutMs = timeoutMs,
            };

            lock (_outboxLock)
            {
                if (_disposed)
                {
                    item.FailReason = "disposed";
                    item.Done.TrySetResult(false);
                    return item.Done.Task;
                }
                _outbox.Enqueue(item);
                if (!_pumpRunning)
                {
                    // Reserve the slot BEFORE starting the pump: when the library
                    // send completes synchronously (loopback + small frame), the
                    // whole pump drains inline inside this very call and clears
                    // the flag itself. Storing the pump task AFTER the call (the
                    // previous design) resurrected that already-completed task as
                    // a zombie "running" flag, so every message queued behind the
                    // first one was stranded in the outbox forever.
                    _pumpRunning = true;
                    _ = PumpOutboxAsync();
                }
            }
            return item.Done.Task;
        }

        /// <summary>
        /// Single-consumer pump: takes one item at a time and fully awaits its library send
        /// before starting the next, so the library never has two sends in flight and its
        /// internal split text/binary queues never reorder our messages. Fails fast: once an
        /// item fails, every queued item is failed too (ordering can no longer be guaranteed).
        /// </summary>
        private async Task PumpOutboxAsync()
        {
            while (true)
            {
                _OutboxItem item;
                lock (_outboxLock)
                {
                    if (_outbox.Count == 0)
                    {
                        _pumpRunning = false;
                        return;
                    }
                    item = _outbox.Dequeue();
                }

                LogState("pump-send-start", item.IsText ? "text" : "binary");
                bool ok = await SendOutboxItemAsync(item).ConfigureAwait(false);
                LogState("pump-send-end", $"ok={ok}");
                if (!ok)
                {
                    lock (_outboxLock)
                    {
                        _pumpRunning = false;
                        while (_outbox.Count > 0)
                        {
                            var dropped = _outbox.Dequeue();
                            dropped.FailReason = "send aborted";
                            dropped.Done.TrySetResult(false);
                        }
                    }
                    return;
                }
            }
        }

        private async Task<bool> SendOutboxItemAsync(_OutboxItem item)
        {
            NativeWebSocket.WebSocket s;
            lock (_gate) { s = _socket; }

            if (s == null || s.State != NativeWebSocket.WebSocketState.Open)
            {
                item.FailReason = "socket not open";
                RaiseError(VoiceSocketErrorKind.InvalidState, "send: socket not open");
                item.Done.TrySetResult(false);
                return false;
            }

            try
            {
                Task send = item.IsText
                    ? s.SendText(item.Text ?? string.Empty)
                    : s.Send(item.Binary ?? Array.Empty<byte>());
                await WaitOrTimeoutAsync(send, item.TimeoutMs, VoiceSocketErrorKind.Send,
                    item.IsText ? "send text" : "send binary").ConfigureAwait(false);
                item.Succeeded = true;
                item.Done.TrySetResult(true);
                return true;
            }
            catch (TimeoutException)
            {
                // WaitOrTimeoutAsync already raised the Timeout error; the library send may
                // still be in flight, so ordering is no longer guaranteed — fail fast.
                item.FailReason = "timeout";
                item.Done.TrySetResult(false);
                return false;
            }
            catch (Exception ex)
            {
                item.FailReason = "send failed: " + ex.GetType().Name;
                RaiseError(VoiceSocketErrorKind.Send,
                    (item.IsText ? "send text" : "send binary") + " failed: " + ex.GetType().Name);
                item.Done.TrySetResult(false);
                return false;
            }
        }

        private async Task WaitOrTimeoutAsync(Task op, int timeoutMs, VoiceSocketErrorKind kind, string what)
        {
            if (op.IsCompleted)
            {
                await op.ConfigureAwait(false);
                return;
            }
            var delay = Task.Delay(timeoutMs);
            var done = await Task.WhenAny(op, delay).ConfigureAwait(false);
            if (done != op)
            {
                RaiseError(VoiceSocketErrorKind.Timeout, what + " timeout");
                throw new TimeoutException(what + " timed out");
            }
            await op.ConfigureAwait(false);
        }

        private async Task QuietAbortAsync()
        {
            NativeWebSocket.WebSocket s;
            lock (_gate) { s = _socket; }
            if (s == null) return;
            try { s.CancelConnection(); } catch { }
            await Task.CompletedTask;
        }

        private void RaiseError(VoiceSocketErrorKind kind, string message)
        {
            LogState("error", $"kind={kind}");
            Error?.Invoke(new VoiceSocketError(kind, message));
        }

        private void ThrowIfDisposedLocked()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(NativeVoiceSocket));
        }

        // Logging must never expose payload content or the full URL query.
        private void LogState(string action, string detail)
        {
            string ep = SanitizeHost(Target);
            Debug.Log($"[NativeVoiceSocket] {action} endpoint={ep} {detail}".Trim());
        }

        internal static string SanitizeHost(Uri url)
        {
            if (url == null) return "<null>";
            try { return $"{url.Scheme}://{url.Host}:{url.Port}"; }
            catch { return "<unparseable>"; }
        }
    }
}
