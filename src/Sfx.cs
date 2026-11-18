using System;
using UnityEngine;

namespace MscTardis
{
    // Sounds: a supplied .wav in assets/sfx wins, otherwise a tone synthesised here stands in for it.
    static class Sfx
    {
        const int Rate = 44100;

        // Interior ambience: hum_11 (chosen by the user from the 11 supplied), else the old stand-in.
        public static AudioClip Hum() { return Pick(new[] { "tardis_hum_11.wav", "tardis_hum.wav" }, () => Synth("hum", 3f, HumAt)); }
        public static AudioClip Door() { return Res.Has("tardis_door.wav") ? Res.Clip("tardis_door.wav") : Synth("door", 0.35f, DoorAt); }
        public static AudioClip Travel() { return Res.Has("tardis_travel.wav") ? Res.Clip("tardis_travel.wav") : Synth("travel", 4.5f, TravelAt); }
        // Dials and buttons. Empty = the synthesised tick. Set to a file name (see `tardis sfx list`) once one sounds right.
        // tardis_remote.wav was tried here and is not a good fit (it sounds like a car remote beep).
        const string ButtonSound = "";
        public static AudioClip Click() { return Pick(new[] { ButtonSound, "tardis_click.wav" }, () => Synth("click", 0.06f, ClickAt)); }

        // Every supplied TARDIS sound, by name without the `tardis_` prefix and `.wav`. Loaded on first use, not at startup:
        // they decode to about 170 MB in all, and the hums alone are most of that.
        public static readonly string[] All =
        {
            "cloister_bell", "door_close", "door_open", "emergency_land", "enter", "flight", "force_field", "force_field_down",
            "force_field_up", "handbrake_engage", "handbrake_release", "hide",
            "hum_1", "hum_2", "hum_3", "hum_4", "hum_5", "hum_6", "hum_7", "hum_8", "hum_9", "hum_10", "hum_11",
            "interior_flight", "junk_arc", "junk_land", "junk_takeoff",
            "land", "land_fast", "land_faster", "land_rapid", "land_warp",
            "malfunction", "malfunction_takeoff", "police_box_close", "police_box_open", "power_down", "power_up",
            "rebuild", "remote", "scanner", "takeoff", "takeoff_fast", "takeoff_faster", "takeoff_rapid", "takeoff_warp",
            "time_rotor", "time_rotor_flying"
        };

        // A supplied sound by catalogue name, or null if it isn't there.
        public static AudioClip Named(string name)
        {
            var f = "tardis_" + name + ".wav";
            return Res.Has(f) ? Res.Clip(f) : null;
        }

        // The three flight sounds, chosen by the user. Takeoff (~9 s) and landing (~12 s) play in full; the flight loop
        // (~1.6 s) repeats through the whole flight phase. Change a name here to a different `tardis_*` file to swap it.
        public static AudioClip Takeoff() { return Named("takeoff_warp"); }
        public static AudioClip Landing() { return Named("land_rapid"); }
        public static AudioClip FlightLoop() { return Named("time_rotor_flying"); }

        // A short sine beep with a quick fade, for the computer's keys and screen. Cached by pitch and length.
        static readonly System.Collections.Generic.Dictionary<string, AudioClip> beeps = new System.Collections.Generic.Dictionary<string, AudioClip>();
        public static AudioClip Beep(float hz, float seconds)
        {
            var key = hz.ToString("0") + "/" + seconds.ToString("0.000");
            AudioClip c;
            if (beeps.TryGetValue(key, out c) && c != null) return c;
            return beeps[key] = Synth("beep" + key, seconds, (t, len) => Sin(hz, t) * 0.5f * Mathf.Min(1f, Mathf.Min(t, len - t) * 400f));
        }



        // Interior door leaves, exterior police box door, and the handbrake lever. A missing file falls back to the old door tone.
        public static AudioClip InteriorDoorOpen() { return Pick(new[] { "tardis_door_open.wav", "tardis_door.wav" }, Door); }
        public static AudioClip InteriorDoorClose() { return Pick(new[] { "tardis_door_close.wav", "tardis_door.wav" }, Door); }
        public static AudioClip BoxDoorOpen() { return Pick(new[] { "tardis_police_box_open.wav", "tardis_door.wav" }, Door); }
        public static AudioClip BoxDoorClose() { return Pick(new[] { "tardis_police_box_close.wav", "tardis_door.wav" }, Door); }
        public static AudioClip HandbrakeEngage() { return Pick(new[] { "tardis_handbrake_engage.wav" }, Click); }
        public static AudioClip HandbrakeRelease() { return Pick(new[] { "tardis_handbrake_release.wav" }, Click); }

        // The first of the named files that exists, otherwise the fallback.
        static AudioClip Pick(string[] files, Func<AudioClip> fallback)
        {
            foreach (var f in files) if (Res.Has(f)) return Res.Clip(f);
            return fallback();
        }

        public static AudioSource Source(GameObject on, AudioClip clip, float volume, float maxDistance, bool loop = false)
        {
            var s = on.AddComponent<AudioSource>();
            s.clip = clip;
            s.volume = volume;
            s.loop = loop;
            s.playOnAwake = false;
            s.spatialBlend = 1f;
            s.rolloffMode = AudioRolloffMode.Linear;
            s.minDistance = 2f;
            s.maxDistance = maxDistance;
            s.dopplerLevel = 0f;   // the player's step through the doorway jumps the listener (and the room's sounds) hundreds of metres in a frame
            return s;
        }

        static AudioClip Synth(string name, float seconds, Func<float, float, float> sample)
        {
            int n = (int)(seconds * Rate);
            var d = new float[n];
            for (int i = 0; i < n; i++) d[i] = Mathf.Clamp(sample(i / (float)Rate, seconds), -1f, 1f);
            var c = AudioClip.Create("msctardis_" + name, n, 1, Rate, false);
            c.SetData(d, 0);
            return c;
        }

        static float Sin(float hz, float t) { return Mathf.Sin(2f * Mathf.PI * hz * t); }

        // Whole cycles in 3 s at every frequency, so it loops without a click.
        static float HumAt(float t, float len)
        {
            return (0.5f * Sin(60f, t) + 0.25f * Sin(90f, t) + 0.12f * Sin(120f, t)) * (0.8f + 0.2f * Sin(1f / 3f, t));
        }

        static readonly System.Random noise = new System.Random(7);
        static float Noise() { return (float)noise.NextDouble() * 2f - 1f; }

        static float DoorAt(float t, float len)
        {
            float decay = Mathf.Exp(-t * 14f);
            return (0.6f * Sin(110f, t) + 0.5f * Noise()) * decay;
        }

        static float ClickAt(float t, float len) { return Noise() * Mathf.Exp(-t * 90f) * 0.7f; }

        // Three swells of a rising engine note over noise: a stand-in, to be replaced by a supplied sound.
        static float TravelAt(float t, float len)
        {
            float phase = t / len * 3f;
            float swell = Mathf.Sin(Mathf.Repeat(phase, 1f) * Mathf.PI);
            float hz = 120f + 160f * Mathf.Repeat(phase, 1f);
            float tone = Sin(hz, t) + 0.4f * Sin(hz * 2.01f, t);
            float fade = Mathf.Min(1f, Mathf.Min(t, len - t) * 8f);
            return (0.45f * tone + 0.12f * Noise()) * swell * fade;
        }
    }
}
