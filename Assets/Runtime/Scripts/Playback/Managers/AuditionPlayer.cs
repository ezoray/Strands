using System;
using Strands.Audio;
using UnityEngine;

namespace Strands.Playback
{
    /// <summary>
    /// Plays the loaded song with an optional click on every beat of the grid, for checking the
    /// alignment by ear. The audio is produced directly in the output callback, so the click sits on
    /// the grid to the sample and a change to the grid is heard at once.
    /// </summary>
    [RequireComponent(typeof(AudioListener))]
    public class AuditionPlayer : MonoBehaviour
    {
        const double ClickSeconds = 0.012;
        const float MusicGain = 0.7f;
        const float ClickGain = 0.35f;
        // How long one sound takes to give way to another when what is played changes while it plays.
        const double SwitchSeconds = 0.015;

        readonly object gate = new object();
        short[] samples;
        // The audio that was playing before a switch, while it fades out, and how far the fade has to go.
        short[] leaving;
        int switchFrames, switchLeft;
        int frames;
        int sourceRate = 44100;
        int outputRate = 48000;
        double position;        // in source frames
        bool playing;
        bool clickEnabled = true;
        double firstDownbeatSeconds;
        double beatSeconds;
        double loopStart, loopEnd;      // in source frames
        bool looping;
        // The earliest place to play from, in source frames: 0, or before the file starts when the
        // lead-in's pattern starts there with silence. That silence is in the package and so it is
        // played here too, with the click on its beat, or the song would be heard to start a beat
        // later than the screen says it does.
        double earliest;

        public bool IsPlaying { get { lock (gate) return playing; } }

        public bool ClickEnabled
        {
            get { lock (gate) return clickEnabled; }
            set { lock (gate) clickEnabled = value; }
        }

        public double PositionSeconds
        {
            get { lock (gate) return position / sourceRate; }
            set { lock (gate) position = Math.Max(earliest, Math.Min(frames, value * sourceRate)); }
        }

        /// <summary>The earliest moment that can be played, which is the start of the file or a moment of silence before it.</summary>
        public double EarliestSeconds
        {
            get { lock (gate) return earliest / sourceRate; }
            set
            {
                lock (gate)
                {
                    earliest = Math.Min(0, value * sourceRate);
                    position = Math.Max(earliest, position);
                }
            }
        }

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

        public void Load(WavData wav)
        {
            lock (gate)
            {
                samples = wav.Samples;
                leaving = null;
                frames = wav.Frames;
                sourceRate = wav.SampleRate;
                position = 0;
                earliest = 0;
                playing = false;
                looping = false;
            }
        }

        public void Unload()
        {
            lock (gate)
            {
                samples = null;
                leaving = null;
                frames = 0;
                playing = false;
            }
        }

        /// <summary>
        /// Goes on from where it is with other audio of the same length: one stem by itself, or
        /// the whole song again. While playing, the one fades into the other over a moment, so
        /// that the change is not a click.
        /// </summary>
        public void Switch(short[] other)
        {
            lock (gate)
            {
                if (samples == null || other == samples || other.Length != samples.Length)
                    return;
                leaving = playing ? samples : null;
                switchFrames = switchLeft = Math.Max(1, (int)(SwitchSeconds * outputRate));
                samples = other;
            }
        }

        public void SetGrid(BeatGrid grid)
        {
            lock (gate)
            {
                firstDownbeatSeconds = grid.FirstDownbeatSeconds;
                beatSeconds = grid.BeatSeconds;
            }
        }

        public void Play()
        {
            lock (gate)
            {
                if (samples == null)
                    return;
                if (position >= frames - 1)
                    position = looping ? loopStart : earliest;
                playing = true;
            }
        }

        public void Pause()
        {
            lock (gate)
            {
                playing = false;
                leaving = null;
            }
        }

        /// <summary>
        /// Repeats this stretch of the song. With jumpIn the playhead goes into it if it is outside;
        /// without, it stays where it is, and playback reaches the loop from there.
        /// </summary>
        public void SetLoop(double startSeconds, double endSeconds, bool jumpIn = true)
        {
            lock (gate)
            {
                loopStart = Math.Max(earliest, startSeconds * sourceRate);
                loopEnd = Math.Min(frames, endSeconds * sourceRate);
                looping = loopEnd - loopStart >= 64;
                if (jumpIn && looping && (position < loopStart || position >= loopEnd))
                    position = loopStart;
            }
        }

        public void ClearLoop()
        {
            lock (gate)
                looping = false;
        }

        // Runs on the audio thread. On the listener's object this sees the final mix, which is
        // silent in this app, and adds the song to it.
        void OnAudioFilterRead(float[] data, int channels)
        {
            lock (gate)
            {
                if (!playing || samples == null)
                    return;

                double step = (double)sourceRate / outputRate;
                int outputFrames = data.Length / channels;
                for (int n = 0; n < outputFrames; n++)
                {
                    int index = (int)position;
                    if (index >= frames - 1)
                    {
                        // A loop that runs to the very end of the song turns round here, because
                        // the last frame has no frame after it to reach the loop's end from.
                        if (!looping || loopStart >= frames - 1)
                        {
                            playing = false;
                            break;
                        }
                        position = loopStart;
                        index = (int)position;
                    }
                    // Before the file starts there is only the silence a lead-in begins with.
                    bool before = position < 0;
                    // Straight-line interpolation is enough for an audition when the output is not at 44.1 kHz.
                    float fraction = (float)(position - index);
                    float left = before ? 0 : samples[2 * index] + (samples[2 * index + 2] - samples[2 * index]) * fraction;
                    float right = before ? 0 : samples[2 * index + 1] + (samples[2 * index + 3] - samples[2 * index + 1]) * fraction;
                    if (leaving != null && before)
                    {
                        if (--switchLeft <= 0)
                            leaving = null;
                    }
                    else if (leaving != null)
                    {
                        float old = (float)switchLeft / switchFrames;
                        left += (leaving[2 * index] + (leaving[2 * index + 2] - leaving[2 * index]) * fraction - left) * old;
                        right += (leaving[2 * index + 1] + (leaving[2 * index + 3] - leaving[2 * index + 1]) * fraction - right) * old;
                        if (--switchLeft <= 0)
                            leaving = null;
                    }
                    left *= MusicGain / 32768f;
                    right *= MusicGain / 32768f;

                    if (clickEnabled && beatSeconds > 0)
                    {
                        double sinceDownbeat = position / sourceRate - firstDownbeatSeconds;
                        double beat = Math.Floor(sinceDownbeat / beatSeconds);
                        double sinceBeat = sinceDownbeat - beat * beatSeconds;
                        if (sinceBeat < ClickSeconds)
                        {
                            // Downbeats get a higher click than the other beats.
                            bool downbeat = ((long)beat % 4 + 4) % 4 == 0;
                            double envelope = 1.0 - sinceBeat / ClickSeconds;
                            float click = (float)(ClickGain * envelope * Math.Sin(2 * Math.PI * (downbeat ? 2000 : 1000) * sinceBeat));
                            left += click;
                            right += click;
                        }
                    }

                    data[n * channels] += left;
                    if (channels > 1)
                        data[n * channels + 1] += right;

                    position += step;
                    if (looping && position >= loopEnd)
                        position = loopStart;
                }
            }
        }
    }
}
