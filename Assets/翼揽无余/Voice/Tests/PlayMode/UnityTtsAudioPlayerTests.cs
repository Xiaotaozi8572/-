// UnityTtsAudioPlayerTests.cs
// P0-5 regression (PlayMode): the TTS audio player must actually sound decoded clips.
// Before the fix, Mp3PlaybackQueue.FramePlayed had no runtime subscriber and the decoded
// AudioClips were silently discarded; these tests pin the player-side contract the
// bootstrap wiring relies on: idle channel plays immediately, busy channel queues strictly
// in arrival order, Tick advances when the channel frees, Cancel/Dispose silence and clear.
using System;
using NUnit.Framework;
using UnityEngine;
using Yilan.Voice.Runtime.Playback;

namespace Yilan.Voice.Playback
{
    public class UnityTtsAudioPlayerTests
    {
        private GameObject _owner;
        private AudioSource _source;
        private UnityTtsAudioPlayer _player;

        [SetUp]
        public void SetUp()
        {
            _owner = new GameObject("tts-player-test");
            _source = _owner.AddComponent<AudioSource>();
            _player = new UnityTtsAudioPlayer(_source);
        }

        [TearDown]
        public void TearDown()
        {
            _player?.Dispose();
            if (_owner != null)
            {
                UnityEngine.Object.Destroy(_owner);
            }
        }

        private static AudioClip Clip(string name)
        {
            // 100 ms of mono 16 kHz — a plausible one-frame TTS slice.
            return AudioClip.Create(name, 1600, 1, 16000, false);
        }

        [Test]
        public void Constructor_NullSource_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new UnityTtsAudioPlayer(null));
        }

        [Test]
        public void Enqueue_IdleChannel_PlaysClipImmediately()
        {
            var clip = Clip("tts-a");

            _player.Enqueue(clip);

            Assert.IsTrue(_source.isPlaying, "decoded clip must start sounding on an idle channel");
            Assert.AreSame(clip, _source.clip);
            Assert.AreEqual(0, _player.PendingCount);
        }

        [Test]
        public void Enqueue_BusyChannel_QueuesClipInArrivalOrder()
        {
            var first = Clip("tts-a");
            var second = Clip("tts-b");
            _player.Enqueue(first);
            Assume.That(_source.isPlaying, "first clip must own the channel for the queueing path");

            _player.Enqueue(second);

            Assert.AreEqual(1, _player.PendingCount, "second clip must wait, never overlap");
            Assert.AreSame(first, _source.clip);
        }

        [Test]
        public void Tick_FreedChannel_AdvancesToNextQueuedClip()
        {
            var first = Clip("tts-a");
            var second = Clip("tts-b");
            _player.Enqueue(first);
            _player.Enqueue(second);
            Assume.That(_source.isPlaying, "first clip must own the channel");
            _source.Stop(); // deterministic stand-in for "the current clip finished"

            _player.Tick();

            Assert.AreSame(second, _source.clip, "Tick must advance to the next queued clip");
            Assert.AreEqual(0, _player.PendingCount);
        }

        [Test]
        public void Tick_PlayingChannel_ChangesNothing()
        {
            var first = Clip("tts-a");
            _player.Enqueue(first);
            Assume.That(_source.isPlaying, "first clip must own the channel");

            _player.Tick();

            Assert.AreSame(first, _source.clip);
        }

        [Test]
        public void Cancel_StopsSoundingClip_AndClearsPending()
        {
            var first = Clip("tts-a");
            var second = Clip("tts-b");
            _player.Enqueue(first);
            _player.Enqueue(second);
            Assume.That(_source.isPlaying, "first clip must own the channel");

            _player.Cancel();

            Assert.IsFalse(_source.isPlaying, "barge-in must silence the sounding clip");
            Assert.IsNull(_source.clip);
            Assert.AreEqual(0, _player.PendingCount);
        }

        [Test]
        public void Enqueue_NullClip_IsNoOp()
        {
            // NOTE: a fresh AudioSource reports isPlaying == true under Unity's dummy
            // audio driver (batchmode), so only the player-owned state is asserted here.
            _player.Enqueue(null);

            Assert.AreEqual(0, _player.PendingCount);
            Assert.IsNull(_source.clip, "null clip must never bind to the channel");
        }

        [Test]
        public void Dispose_SilencesAndIgnoresLaterEnqueue()
        {
            var first = Clip("tts-a");
            _player.Enqueue(first);
            Assume.That(_source.isPlaying, "first clip must own the channel");

            _player.Dispose();
            _player.Enqueue(Clip("tts-after-dispose"));

            Assert.IsFalse(_source.isPlaying);
            Assert.IsNull(_source.clip);
            Assert.AreEqual(0, _player.PendingCount);
        }
    }
}
