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
        // How long a jump of the playhead takes to cross over, and play and pause to fade in and out:
        // a cut from one place in the waveform to another is heard as a click (the user, 2026-10-16).
        const double JumpSeconds = 0.008;

        readonly object gate = new object();
        short[] samples;
        // The audio that was playing before a switch or a jump, while it fades out: its samples, where
        // it has got to, and how far the fade has to go.
        short[] leaving;
        double leavingPosition;
        int switchFrames, switchLeft;
        // A fade in after Play, and a fade out after Pause, counted down in output frames.
        int fadeInFrames, fadeInLeft;
        int fadeOutFrames, fadeOutLeft;
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
            set { lock (gate) JumpTo(Math.Max(earliest, Math.Min(frames, value * sourceRate)), JumpSeconds); }
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
                fadeInLeft = fadeOutLeft = 0;
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
                fadeInLeft = fadeOutLeft = 0;
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
                StartLeaving(SwitchSeconds);
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
                if (!playing || fadeOutLeft > 0)
                {
                    fadeInFrames = fadeInLeft = Frames(JumpSeconds);
                    fadeOutLeft = 0;
                }
                playing = true;
            }
        }

        public void Pause()
        {
            lock (gate)
            {
                // It goes on for a moment while it fades out, and stops in the audio callback.
                if (playing && fadeOutLeft == 0)
                    fadeOutFrames = fadeOutLeft = Frames(JumpSeconds);
            }
        }

        int Frames(double seconds)
        {
            return Math.Max(1, (int)(seconds * outputRate));
        }

        // What is playing now fades out over this long while what replaces it fades in.
        void StartLeaving(double seconds)
        {
            if (!playing)
            {
                leaving = null;
                return;
            }
            leaving = samples;
            leavingPosition = position;
            switchFrames = switchLeft = Frames(seconds);
        }

        // Moves the playhead; while playing, the place it leaves fades out under the place it goes to.
        void JumpTo(double frame, double fadeSeconds)
        {
            if (Math.Abs(frame - position) > 1)
                StartLeaving(fadeSeconds);
            position = frame;
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
                    JumpTo(loopStart, JumpSeconds);
            }
        }

        public void ClearLoop()
        {
            lock (gate)
                looping = false;
        }

        // One side of a stereo frame at a place in source frames, between frames by a straight line,
        // and silence before the file and at its very end.
        float Read(short[] audio, double at, int side)
        {
            if (at < 0)
                return 0;
            int index = (int)at;
            if (index >= frames - 1)
                return 0;
            float fraction = (float)(at - index);
            return audio[2 * index + side] + (audio[2 * index + 2 + side] - audio[2 * index + side]) * fraction;
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
                    if ((int)position >= frames - 1)
                    {
                        // A loop that runs to the very end of the song turns round here, because
                        // the last frame has no frame after it to reach the loop's end from.
                        if (!looping || loopStart >= frames - 1)
                        {
                            playing = false;
                            leaving = null;
                            fadeOutLeft = 0;
                            break;
                        }
                        JumpTo(loopStart, JumpSeconds);
                    }
                    // Before the file starts there is only the silence a lead-in begins with.
                    float left = Read(samples, position, 0);
                    float right = Read(samples, position, 1);
                    if (leaving != null)
                    {
                        float old = (float)switchLeft / switchFrames;
                        left += (Read(leaving, leavingPosition, 0) - left) * old;
                        right += (Read(leaving, leavingPosition, 1) - right) * old;
                        leavingPosition += step;
                        if (--switchLeft <= 0)
                            leaving = null;
                    }
                    float gain = MusicGain / 32768f;
                    if (fadeInLeft > 0)
                        gain *= 1f - (float)fadeInLeft-- / fadeInFrames;
                    if (fadeOutLeft > 0)
                        gain *= (float)fadeOutLeft / fadeOutFrames;
                    left *= gain;
                    right *= gain;

                    if (clickEnabled && beatSeconds > 0 && fadeOutLeft == 0)
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
                        JumpTo(loopStart, JumpSeconds);
                    if (fadeOutLeft > 0 && --fadeOutLeft == 0)
                    {
                        playing = false;
                        leaving = null;
                        break;
                    }
                }
            }
        }
    }
}
