using System;

namespace Yilan.Voice.Runtime.Protocol
{
    /// <summary>
    /// Base envelope for every voice-ws-v1 message. <see cref="type"/> selects the
    /// concrete message type; dispatch is owned by <see cref="VoiceProtocolParser"/>.
    /// </summary>
    [Serializable]
    public class VoiceEnvelope
    {
        public string type;
    }
}
