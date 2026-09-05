// PicoMicrophonePermission.cs
// VR3-T09: Android runtime microphone permission wrapper with a three-path state
// machine (Granted / Denied / PermanentlyDenied). All Unity/Android interaction is
// confined behind IMicrophonePermissionPlatform so PlayMode/EditMode tests can inject
// a deterministic fake and never touch real hardware.
//
// Privacy: no audio data, no payload content and no permission identifiers beyond the
// fixed RECORD_AUDIO constant are logged or persisted anywhere in this file.
using System;

namespace Yilan.Voice.Runtime.Pico
{
    /// <summary>
    /// The three runtime permission outcomes that the caller must distinguish.
    /// The upper layer only builds a session when the state is <see cref="Granted"/>.
    /// </summary>
    public enum MicrophonePermissionStatus
    {
        /// <summary>No decision observed yet (initial / not yet queried).</summary>
        Unknown = 0,

        /// <summary>The RECORD_AUDIO permission is currently granted; capture may start.</summary>
        Granted = 1,

        /// <summary>Permission is denied but the system would still show the request dialog again.</summary>
        Denied = 2,

        /// <summary>
        /// Permission is denied and the system will not show the dialog again ("don't ask
        /// again"). The app must not re-request and must not start capture.
        /// </summary>
        PermanentlyDenied = 3,
    }

    /// <summary>
    /// Narrow platform abstraction for the single Android permission we care about.
    /// The real implementation wraps UnityEngine.Android.Permission; a test may supply a
    /// fake. This keeps the state machine fully deterministic and hardware-free.
    /// </summary>
    public interface IMicrophonePermissionPlatform
    {
        /// <summary>True when android.permission.RECORD_AUDIO is currently granted.</summary>
        bool HasPermission();

        /// <summary>
        /// True when the operating system would still allow a request dialog to be shown.
        /// False means "permanently denied" and the app must not request again.
        /// </summary>
        bool CanRequestAgain();

        /// <summary>Show the runtime permission request dialog (fire-and-forget; result arrives asynchronously).</summary>
        void RequestPermission();
    }

    /// <summary>
    /// Public three-path permission facade. It never decides policy on its own beyond the
    /// platform's HasPermission / CanRequestAgain signals, and it never starts a session;
    /// the caller uses <see cref="Status"/> / <see cref="HasPermission"/> to gate capture.
    /// </summary>
    public sealed class PicoMicrophonePermission
    {
        /// <summary>The only Android permission this class manages (fixed by the harness).</summary>
        public const string PermissionId = "android.permission.RECORD_AUDIO";

        private readonly IMicrophonePermissionPlatform _platform;
        private bool _requestedInSession;

        public PicoMicrophonePermission(IMicrophonePermissionPlatform platform)
        {
            _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        }

        /// <summary>
        /// The resolved three-path state, re-derived from the platform on every read so a
        /// result that arrives asynchronously after RequestPermission is picked up on the
        /// next poll.
        /// </summary>
        public MicrophonePermissionStatus Status
        {
            get
            {
                if (_platform.HasPermission()) return MicrophonePermissionStatus.Granted;
                return _platform.CanRequestAgain()
                    ? MicrophonePermissionStatus.Denied
                    : MicrophonePermissionStatus.PermanentlyDenied;
            }
        }

        /// <summary>True only for <see cref="MicrophonePermissionStatus.Granted"/>; capture is only allowed then.</summary>
        public bool HasPermission => Status == MicrophonePermissionStatus.Granted;

        /// <summary>True when the app may still call <see cref="RequestPermission"/> (not permanent).</summary>
        public bool CanRequest => !HasPermission && _platform.CanRequestAgain();

        /// <summary>
        /// Idempotently advances the permission state machine:
        ///   Granted          -> nothing, returns Granted;
        ///   Denied (re-ask)  -> issues at most one request per session, returns Denied
        ///                       (result is observed on a later Status poll);
        ///   PermanentlyDenied-> issues nothing and returns PermanentlyDenied.
        /// </summary>
        public MicrophonePermissionStatus EnsurePermission()
        {
            if (_platform.HasPermission())
            {
                _requestedInSession = false;
                return MicrophonePermissionStatus.Granted;
            }
            if (_platform.CanRequestAgain())
            {
                if (!_requestedInSession)
                {
                    _platform.RequestPermission();
                    _requestedInSession = true;
                }
                return MicrophonePermissionStatus.Denied;
            }
            return MicrophonePermissionStatus.PermanentlyDenied;
        }

        /// <summary>Clear the per-session single-request guard (e.g. on a fresh app session / resume).</summary>
        public void ResetSession()
        {
            _requestedInSession = false;
        }
    }

    /// <summary>
    /// Real Android implementation backed by UnityEngine.Android.Permission.
    /// On non-Android platforms it degrades to "permanently denied" (nothing can be
    /// requested), which keeps the three-path contract testable and safe.
    ///
    /// Permanently-denied is inferred from the OS "don't ask again" callback; a plain deny
    /// keeps CanRequestAgain true so the user can be asked again.
    /// </summary>
    public sealed class AndroidMicrophonePermissionPlatform : IMicrophonePermissionPlatform
    {
        private bool _permanentlyDenied;

        public AndroidMicrophonePermissionPlatform()
        {
            // Note: no Linux / desktop path is actionable here; #if blocks keep the
            // Android API reference out of builds for other platforms.
        }

        public bool HasPermission()
        {
#if UNITY_ANDROID
            return UnityEngine.Android.Permission.HasUserAuthorizedPermission(PicoMicrophonePermission.PermissionId);
#else
            return false;
#endif
        }

        public bool CanRequestAgain()
        {
#if UNITY_ANDROID
            return !_permanentlyDenied;
#else
            return false;
#endif
        }

        public void RequestPermission()
        {
#if UNITY_ANDROID
            var callbacks = new UnityEngine.Android.PermissionCallbacks();
            // PermissionCallbacks fields are events: they can only be subscribed with += / -=,
            // never assigned in an object initializer (CS0079).
            callbacks.PermissionGranted += id =>
            {
                if (id == PicoMicrophonePermission.PermissionId) _permanentlyDenied = false;
            };
            callbacks.PermissionDenied += id =>
            {
                // A plain deny: the dialog may still be shown again on a later request.
                if (id == PicoMicrophonePermission.PermissionId) _permanentlyDenied = false;
            };
            callbacks.PermissionDeniedAndDontAskAgain += id =>
            {
                if (id == PicoMicrophonePermission.PermissionId) _permanentlyDenied = true;
            };
            UnityEngine.Android.Permission.RequestUserPermission(PicoMicrophonePermission.PermissionId, callbacks);
#else
            _permanentlyDenied = true; // no platform support: cannot ask, so degrade to permanent.
#endif
        }
    }
}
