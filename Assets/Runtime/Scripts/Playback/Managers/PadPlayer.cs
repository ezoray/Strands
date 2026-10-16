using System;
using System.Collections.Generic;
using UnityEngine;

namespace Strands.Playback
{
    /// <summary>
    /// Plays the samples of a package as its pads would on the unit, for trying them on the
    /// Export page: several at once, each from its start, one that loops going round until it is
    /// stopped, and one in a choke group cutting off whatever else of its group is sounding.
    /// What it is given is the sample as the package holds it. The audio is produced in the
    /// output callback, on the listener's object, as the audition's is.
    /// </summary>
    [RequireComponent(typeof(AudioListener))]
    public class PadPlayer : MonoBehaviour
    {
        const float Gain = 0.7f;
        // How long a sample takes to fall silent when it is stopped or cut off, so that it does not click.
        const double StopSeconds = 0.004;

        sealed class Voice
        {
            public int Key;
            public short[] Samples;
            public int Channels, Frames, ChokeGroup;
            public bool Loops;
            public double Position;
            // Frames of the output left before it is silent, once it has been stopped; -1 while it sounds.
            public int Leaving = -1;
        }

        readonly object gate = new object();
        readonly List<Voice> voices = new List<Voice>();
        int sourceRate = 44100;
        int outputRate = 48000;

        void Awake()
        {
            outputRate = AudioSettings.outputSampleRate;
            AudioSettings.OnAudioConfigurationChanged += OnConfigurationChanged;
        }

        void OnDestroy()
        {
            AudioSettings.OnAudioConfigurationChanged -= OnConfigurationChanged;
        }

        void OnConfigurationChanged(bool deviceWasChanged)
        {
            lock (gate)
                outputRate = AudioSettings.outputSampleRate;
        }

        public bool AnyPlaying
        {
            get
            {
                lock (gate)
                    foreach (Voice voice in voices)
                        if (voice.Leaving < 0)
                            return true;
                return false;
            }
        }

        /// <summary>
        /// Starts a sample from its beginning. A sample already sounding under the same key is
        /// cut off first, and so is every sample of the same choke group (0 is no group).
        /// </summary>
        public void Play(int key, short[] samples, int channels, int rate, bool loops, int chokeGroup)
        {
            if (samples == null || samples.Length < 2 * channels)
                return;
            lock (gate)
            {
                sourceRate = rate;
                foreach (Voice voice in voices)
                    if (voice.Leaving < 0 && (voice.Key == key || (chokeGroup > 0 && voice.ChokeGroup == chokeGroup)))
                        voice.Leaving = StopFrames;
                voices.Add(new Voice { Key = key, Samples = samples, Channels = channels, Frames = samples.Length / channels, Loops = loops, ChokeGroup = chokeGroup });
            }
        }

        /// <summary>The keys of the pads sounding now.</summary>
        public List<int> PlayingKeys()
        {
            var keys = new List<int>();
            lock (gate)
                foreach (Voice voice in voices)
                    if (voice.Leaving < 0)
                        keys.Add(voice.Key);
            return keys;
        }

        /// <summary>
        /// Gives a sounding pad what it now holds, after its settings were changed: its sample,
        /// whether it loops and its choke group. It carries on from where it has got to, so that
        /// pressing Loop or Choke on a pad that is playing does not stop or restart it.
        /// </summary>
        public void Change(int key, short[] samples, int channels, bool loops, int chokeGroup)
        {
            if (samples == null || samples.Length < 2 * channels)
            {
                Stop(key);
                return;
            }
            lock (gate)
                foreach (Voice voice in voices)
                    if (voice.Key == key && voice.Leaving < 0)
                    {
                        voice.Samples = samples;
                        voice.Channels = channels;
                        voice.Frames = samples.Length / channels;
                        voice.Loops = loops;
                        voice.ChokeGroup = chokeGroup;
                    }
        }

        public void Stop(int key)
        {
            lock (gate)
                foreach (Voice voice in voices)
                    if (voice.Key == key && voice.Leaving < 0)
                        voice.Leaving = StopFrames;
        }

        public void StopAll()
        {
            lock (gate)
                foreach (Voice voice in voices)
                    if (voice.Leaving < 0)
                        voice.Leaving = StopFrames;
        }

        /// <summary>How far through its sample a key's pad is, in frames of the sample, or -1 when it is not sounding.</summary>
        public double PositionOf(int key)
        {
            lock (gate)
                foreach (Voice voice in voices)
                    if (voice.Key == key && voice.Leaving < 0)
                        return voice.Position;
            return -1;
        }

        int StopFrames { get { return Math.Max(1, (int)(StopSeconds * outputRate)); } }

        // Runs on the audio thread, and adds the pads to whatever the mix holds.
        void OnAudioFilterRead(float[] data, int channels)
        {
            lock (gate)
            {
                if (voices.Count == 0)
                    return;
                double step = (double)sourceRate / outputRate;
                int outputFrames = data.Length / channels, stopFrames = StopFrames;
                for (int v = voices.Count - 1; v >= 0; v--)
                {
                    Voice voice = voices[v];
                    bool done = false;
                    for (int n = 0; n < outputFrames && !done; n++)
                    {
                        int index = (int)voice.Position;
                        if (index >= voice.Frames)
                        {
                            if (!voice.Loops)
                            {
                                done = true;
                                break;
                            }
                            voice.Position -= voice.Frames;
                            index = (int)voice.Position;
                        }
                        // The frame after the last is the first again for a loop, and nothing otherwise.
                        int next = index + 1 < voice.Frames ? index + 1 : voice.Loops ? 0 : index;
                        float fraction = (float)(voice.Position - index);
                        float left, right;
                        if (voice.Channels == 1)
                        {
                            left = right = voice.Samples[index] + (voice.Samples[next] - voice.Samples[index]) * fraction;
                        }
                        else
                        {
                            left = voice.Samples[2 * index] + (voice.Samples[2 * next] - voice.Samples[2 * index]) * fraction;
                            right = voice.Samples[2 * index + 1] + (voice.Samples[2 * next + 1] - voice.Samples[2 * index + 1]) * fraction;
                        }
                        float level = Gain / 32768f;
                        if (voice.Leaving >= 0)
                        {
                            level *= (float)voice.Leaving / stopFrames;
                            if (--voice.Leaving <= 0)
                                done = true;
                        }
                        data[n * channels] += left * level;
                        if (channels > 1)
                            data[n * channels + 1] += right * level;
                        voice.Position += step;
                    }
                    if (done)
                        voices.RemoveAt(v);
                }
            }
        }
    }
}
