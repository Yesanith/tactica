using System.Collections.Generic;
using UnityEngine;

namespace Tactica.UI
{
    // Tiny synthesised blips, so feedback has a sound without shipping audio files. Stand-ins
    // for the real sound pass; each cue is a few lines of maths.
    public static class FeedbackAudio
    {
        public enum Cue { Hit, Heal, KnockOut, Miss, Status, Hack }

        private const int SampleRate = 44100;
        private const float Volume = 0.35f;

        private static AudioSource source;
        private static readonly Dictionary<Cue, AudioClip> clips = new Dictionary<Cue, AudioClip>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            source = null;
            clips.Clear();
        }

        public static void Play(Cue cue)
        {
            if (source == null)
            {
                GameObject sourceObject = new GameObject("FeedbackAudio");
                Object.DontDestroyOnLoad(sourceObject);
                source = sourceObject.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.spatialBlend = 0f;
            }

            if (!clips.TryGetValue(cue, out AudioClip clip))
            {
                clip = Build(cue);
                clips[cue] = clip;
            }

            source.PlayOneShot(clip, Volume);
        }

        private static AudioClip Build(Cue cue)
        {
            float length = cue switch
            {
                Cue.KnockOut => 0.45f,
                Cue.Hack => 0.4f,
                Cue.Heal => 0.3f,
                _ => 0.15f,
            };

            int count = Mathf.CeilToInt(length * SampleRate);
            float[] data = new float[count];
            System.Random noise = new System.Random(17);
            float phase = 0f;

            for (int i = 0; i < count; i++)
            {
                float t = (float)i / SampleRate;
                float u = t / length;
                float envelope = (1f - u) * Mathf.Clamp01(t * 200f);

                float frequency = cue switch
                {
                    Cue.Hit => 180f - 100f * u,
                    Cue.Heal => 520f + 500f * u,
                    Cue.KnockOut => 220f - 170f * u,
                    Cue.Miss => 900f + 300f * u,
                    Cue.Status => 660f,
                    _ => 300f + 250f * Mathf.Sin(u * 40f),
                };

                phase += frequency / SampleRate;
                float tone = Mathf.Sin(phase * Mathf.PI * 2f);

                // Hits are mostly noise (impact); a square-ish edge gives hacks their digital buzz.
                float sample = cue switch
                {
                    Cue.Hit => 0.4f * tone + 0.6f * ((float)noise.NextDouble() * 2f - 1f) * (1f - u),
                    Cue.Hack => Mathf.Sign(tone) * 0.5f,
                    Cue.KnockOut => tone + 0.3f * ((float)noise.NextDouble() * 2f - 1f),
                    _ => tone,
                };

                data[i] = sample * envelope * 0.6f;
            }

            AudioClip clip = AudioClip.Create($"Feedback_{cue}", count, 1, SampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
