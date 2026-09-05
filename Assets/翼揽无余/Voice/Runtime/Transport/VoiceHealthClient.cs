using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Yilan.Voice.Runtime.Transport
{
    /// <summary>Stable health classification.</summary>
    public enum VoiceHealthStatus
    {
        Ok,          // HTTP 2xx
        Degraded,    // any other HTTP response (3xx, 4xx, 5xx)
        Unreachable  // refused, timeout, TLS failure, transport error
    }

    /// <summary>Immutable outcome of a /health probe. Never carries the response body.</summary>
    public sealed class VoiceHealthResult
    {
        public VoiceHealthStatus Status { get; }
        public int? HttpStatus { get; }
        public string Reason { get; }

        public VoiceHealthResult(VoiceHealthStatus status, int? httpStatus, string reason)
        {
            Status = status;
            HttpStatus = httpStatus;
            Reason = reason ?? string.Empty;
        }

        /// <summary>
        /// A log-safe line with only the classification and the HTTP status — never the URL
        /// query and never the response body.
        /// </summary>
        public string ToSanitizedLogSeed()
        {
            string s = $"VoiceHealth[{Status}]";
            s += HttpStatus.HasValue ? $" http={HttpStatus.Value}" : " http=<none>";
            return s;
        }
    }

    /// <summary>
    /// Health probe adapter: GET <c>http(s)://&lt;host&gt;/health</c> against a caller-supplied
    /// base endpoint (host address comes from config/usage, never hard-coded here) with a fixed
    /// overall timeout. Status is classified ok / degraded / unreachable. Logging strips the URL
    /// query and never includes the response body.
    /// </summary>
    public static class VoiceHealthClient
    {
        public const int DefaultTimeoutMs = 5000;

        /// <summary>
        /// Injectable HTTP transport seam. Defaults to <see cref="CreateDefaultTransport"/>.
        /// Tests replace this to exercise classification/sanitization without network.
        /// Signature: given the health URL and timeout, returns a result (or throws on unreachable).
        /// </summary>
        public static Func<Uri, int, CancellationToken, Task<VoiceHealthResult>> Transport = CreateDefaultTransport();

        /// <summary>Returns the production transport (real HTTP GET over the network).</summary>
        public static Func<Uri, int, CancellationToken, Task<VoiceHealthResult>> CreateDefaultTransport()
            => HttpClientTransport;

        /// <summary>Build a /health URL from a caller-provided base URI (host + optional port).</summary>
        public static Uri HealthUrlFromBase(Uri baseUri, string extraQuery = null)
        {
            if (baseUri == null) throw new ArgumentNullException(nameof(baseUri));
            string scheme = (baseUri.Scheme ?? string.Empty).ToLowerInvariant();
            if (scheme != "http" && scheme != "https")
                throw new ArgumentException("Unsupported health scheme: " + scheme, nameof(baseUri));

            var ub = new UriBuilder(baseUri) { Path = "/health", Query = extraQuery ?? string.Empty };
            return ub.Uri;
        }

        public static Task<VoiceHealthResult> CheckAsync(Uri healthUrl) => CheckAsync(healthUrl, DefaultTimeoutMs);

        public static async Task<VoiceHealthResult> CheckAsync(Uri healthUrl, int timeoutMs)
        {
            int timeout = timeoutMs > 0 ? timeoutMs : DefaultTimeoutMs;
            if (healthUrl == null)
                return new VoiceHealthResult(VoiceHealthStatus.Unreachable, null, "null health URL");

            string endpoint = SanitizeForLog(healthUrl); // sanitize before any work

            try
            {
                var result = await Transport(healthUrl, timeout, CancellationToken.None).ConfigureAwait(false);
                Debug.Log($"VoiceHealth probe endpoint={endpoint} {result.ToSanitizedLogSeed()}");
                return result;
            }
            catch (OperationCanceledException)
            {
                var r = new VoiceHealthResult(VoiceHealthStatus.Unreachable, null, "timeout");
                Debug.Log($"VoiceHealth probe endpoint={endpoint} {r.ToSanitizedLogSeed()}");
                return r;
            }
            catch (Exception ex)
            {
                // Whatever the transport raised (refused, DNS, TLS, protocol, timeout), the
                // endpoint is unreachable. The type name is diagnostic-safe; body/query never logged.
                var r = new VoiceHealthResult(VoiceHealthStatus.Unreachable, null, ex.GetType().Name);
                Debug.Log($"VoiceHealth probe endpoint={endpoint} {r.ToSanitizedLogSeed()}");
                return r;
            }
        }

        /// <summary>Host-only representation for logging: scheme://host:port. Query/path dropped.</summary>
        public static string SanitizeForLog(Uri url)
        {
            if (url == null) return "<null>";
            try { return $"{url.Scheme}://{url.Host}:{url.Port}"; }
            catch { return "<unparseable>"; }
        }

        private static async Task<VoiceHealthResult> HttpClientTransport(Uri url, int timeoutMs, CancellationToken token)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                cts.CancelAfter(timeoutMs);
                using (var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan })
                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                using (var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                                              .ConfigureAwait(false))
                {
                    int code = (int)resp.StatusCode;
                    var status = code >= 200 && code < 300 ? VoiceHealthStatus.Ok : VoiceHealthStatus.Degraded;
                    return new VoiceHealthResult(status, code, $"http {code}");
                }
            }
        }
    }
}
