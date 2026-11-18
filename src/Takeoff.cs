using System.Collections.Generic;
using UnityEngine;

namespace MscTardis
{
    // The take-off procedure: the same fixed steps every time. TAKE OFF refuses while one is missing and says which. POWER has to be on to
    // use the TARDIS at all, but it isn't a step.
    //   1 a destination is set   2 HANDBRAKE off   3 MASTER on   4 CIRCUIT 7-10 on   5 the arm row (RELAY 1-5) lit
    //   6 the STABILISER worked to 80%   7 TAKE OFF
    // FLIGHT stays out of it: it is the free-flight toggle. The private build can skip the checklist, so scripted tests keep flying.
    static class Takeoff
    {
        public static float StabCharge;          // the stabiliser's charge, 0..1: each turn of the wheel adds, it bleeds away slowly
        public const bool Enforce = true;
        public const float Needed = 0.8f;
        public static readonly string[] Names =
            { "DESTINATION SET", "HANDBRAKE OFF", "MASTER ON", "CIRCUITS 7-10 ON", "RELAYS 1-5 LIT", "STABILISER 80%" };
        static readonly string[] Messages =
            { "SET A DESTINATION FIRST", "HANDBRAKE IS ON", "MASTER IS OFF", "SWITCH ON CIRCUITS 7 TO 10", "LIGHT RELAYS 1 TO 5", "WORK THE STABILISER TO 80%" };

        public static string ArmKey(int n) { return "d_sbp" + n; }

        public static bool Done(int step)
        {
            var d = MscTardis.Data;
            switch (step)
            {
                case 0: return TardisInterior.SelectedPlace() != null || d.ManualX != 0f || d.ManualZ != 0f;
                case 1: return !d.Handbrake;
                case 2: return Subsystems.MasterOn;
                case 3:
                    for (int i = 7; i <= 10; i++) if (!TardisInterior.DecoIs("d_sb" + i)) return false;
                    return true;
                case 4:
                    for (int i = 1; i <= 5; i++) if (!TardisInterior.DecoIs(ArmKey(i))) return false;
                    return true;
                default: return StabCharge >= Needed;
            }
        }

        // The first step still to do, as the message for the player, or null when all are done.
        public static string Missing()
        {
            if (!Enforce || !TardisInterior.HasControl("d_sbm1")) return null;   // the other rooms have no switch board to arm
            for (int i = 0; i < Names.Length; i++) if (!Done(i)) return Messages[i];
            return null;
        }

        // After a take-off: the arm row goes out and the stabiliser charge is spent.
        public static void Consume()
        {
            for (int i = 1; i <= 5; i++) TardisInterior.DecoSet(ArmKey(i), false);
            StabCharge = 0f;
        }

        public static void Stabiliser() { StabCharge = Mathf.Min(1f, StabCharge + 0.35f); }

        // ---------------------------------------------------------------------------------------------------------
        // The autopilot (the old HADS switch): on EASY, throwing it does the arming steps for you, one at a time.

        static int autoStep = -1;
        static float autoAt;

        public static string Autopilot(bool turningOn)
        {
            if (!turningOn) { autoStep = -1; return null; }
            if (MscTardis.Data.Difficulty != 0) return "AUTOPILOT: EASY MODE ONLY";
            if (Tardis.State != Tardis.Phase.Idle) return "NOT WHILE IN FLIGHT";
            autoStep = 1;
            autoAt = Time.time;
            return null;
        }

        public static void Tick()
        {
            var d = MscTardis.Data;
            float dt = Time.deltaTime;
            if (Tardis.State == Tardis.Phase.Idle && StabCharge > 0f) StabCharge = Mathf.Max(0f, StabCharge - 0.008f * dt);
            if (autoStep >= 1 && Time.time >= autoAt)
            {
                if (!d.Power || d.Difficulty != 0) { autoStep = -1; return; }
                autoAt = Time.time + 0.6f;
                switch (autoStep)
                {
                    case 1: d.Handbrake = false; break;
                    case 2: Subsystems.SetMaster(true); break;
                    case 3: for (int i = 7; i <= 10; i++) TardisInterior.DecoSet("d_sb" + i, true); break;
                    case 4: for (int i = 1; i <= 5; i++) TardisInterior.DecoSet(ArmKey(i), true); break;
                    case 5: StabCharge = 1f; break;
                }
                TardisInterior.PlayBeep(990f + autoStep * 60f);
                if (++autoStep > 5) { autoStep = -1; G.Flash("AUTOPILOT: READY FOR TAKE-OFF", 2.5f); }
            }
        }


    }

    // The flight minigame. During a console-flown flight a control on the board is named on the monitor and its cap flashes (a halo over
    // any other control, and the red lamp on a button); work it in time. A hit gives stability back, a miss or a wrong press takes it away.
    // Every third wrong press damages the hull and a subsystem, and three misses in a row force an emergency landing. The stability's passive
    // decay is cut to a quarter, so the prompts do most of the work. EASY gives 6.5 s and the switch board's caps, levers and spare switches;
    // ADVANCED gives 4 s and any control from the wider pool.
    // A console-flown flight is long (Tardis.PilotedMin to PilotedMax), and the game builds through it: the gaps between prompts shorten, and from
    // a fifth of the way a prompt may be a run of two or three controls to work in order. No control comes up again until several others
    // have, and the next one is never worked the same way as the last (SLIDE, FLIP, PRESS, THROW, TURN), where the console has a choice.
    static class FlightGame
    {
        public static bool Active { get; private set; }
        public static string Prompt { get; private set; }   // the control to work now
        public static float Until;
        static float nextAt;
        public static int Hits, Misses, Wrongs, MissStreak, Runs;   // Runs: the multi-control prompts completed
        static string[] chain;                              // the controls of a run, Prompt among them
        static int step;
        static readonly List<string> recent = new List<string>();   // the last few controls asked, most recent last
        static string lastVerb;

        // The caps and levers that flash (RELAY 6 and up: not the arm row), the Toyota's spare switches and tube levers, and on ADVANCED the other
        // push buttons, switches and knobs too.
        static readonly string[] Easy = BuildEasy();
        static string[] BuildEasy()
        {
            var l = new List<string>();
            for (int i = 6; i <= 30; i++) l.Add("d_sbp" + i);
            for (int i = 1; i <= 30; i++) l.Add("d_sw" + i);
            for (int i = 1; i <= 6; i++) l.Add("d_ls" + i);
            return l.ToArray();
        }
        static readonly string[] Extra = BuildExtra();
        static string[] BuildExtra()
        {
            var l = new List<string> { "d_pb1", "d_pb2", "d_bpb1", "d_bpb2", "d_bpb3", "d_bpb4", "d_lp1", "d_lp2", "d_ct1", "d_cp1", "d_cb1", "d_tg1", "d_tg2", "d_bpt1", "d_bpt2", "d_bpt3", "d_bpt4", "d_bpt5", "aux2", "aux3" };
            l.Add("d_bp9"); l.Add("d_bp10");   // the Toyota's spare knobs
            return l.ToArray();
        }

        public const float EasyWindow = 6.5f, AdvancedWindow = 4f;   // seconds to work a prompted control
        public static float Window { get { return MscTardis.Data.Difficulty == 0 ? EasyWindow : AdvancedWindow; } }
        static float StepWindow { get { return Window + (chain != null && chain.Length > 1 ? 0.5f : 0f); } }

        static List<string> PoolNow()
        {
            var l = new List<string>(Easy);
            if (MscTardis.Data.Difficulty != 0) l.AddRange(Extra);
            l.RemoveAll(k => !TardisInterior.HasControl(k));
            return l;
        }

        public static bool InPool(string key) { return PoolNow().Contains(key); }

        static bool ShouldRun()
        {
            return Tardis.State == Tardis.Phase.Flight && Tardis.Piloted && Tardis.FlightKind == Tardis.Kind.Normal && MscTardis.Data.Inside && !Pilot.Active &&
                   MscTardis.Data.Power && TardisInterior.HasControl("d_sbp6");
        }

        // Whether a flight about to start will carry the minigame, so that it is made the long one.
        public static bool Expected()
        {
            return MscTardis.Data.Inside && MscTardis.Data.Power && TardisInterior.HasControl("d_sbp6");
        }

        public static void Tick()
        {
            bool run = ShouldRun();
            if (run && !Active)
            {
                Active = true;
                Hits = Misses = Wrongs = MissStreak = Runs = 0;
                recent.Clear();
                lastVerb = null;
                nextAt = Time.time + 3f;
            }
            else if (!run && Active) Stop();
            if (!Active) return;
            if (Prompt == null)
            {
                if (Time.time >= nextAt) Ask();
            }
            else if (Time.time >= Until) Miss();
        }

        // The pause before the next prompt: long at the start, short near the end.
        static float Gap()
        {
            float hi = Mathf.Lerp(4.5f, 2.2f, Tardis.Progress), lo = hi * 0.55f;
            if (MscTardis.Data.Difficulty != 0) { hi *= 0.8f; lo *= 0.8f; }
            return Random.Range(lo, hi);
        }

        // One control from the candidates, a different kind from the last and not one asked lately.
        static string Pick(List<string> pool, string notVerb)
        {
            int keep = Mathf.Min(10, pool.Count / 2);
            // Not among the last `keep` asked: never asked, or last asked before them. (`recent` holds repeats, so its last place counts; and
            // early in a flight recent.Count - keep is negative, where IndexOf's -1 for a control never asked has to count as fresh. A loop,
            // not List.LastIndexOf: the game's Mono throws from LastIndexOf on an empty list.)
            var fresh = pool.FindAll(k => { int at = recent.Count - 1; while (at >= 0 && recent[at] != k) at--; return at < 0 || at < recent.Count - keep; });
            if (fresh.Count == 0) fresh = pool;
            // choose a kind of control first, so the commonest (the switch board's many caps) does not drown the rest
            var verbs = new List<string>();
            foreach (var k in fresh) { var v = TardisInterior.Verb(k); if (!verbs.Contains(v)) verbs.Add(v); }
            if (verbs.Count > 1 && notVerb != null) verbs.Remove(notVerb);
            // the kind longest unused is the most likely
            var weight = new float[verbs.Count];
            float total = 0f;
            for (int i = 0; i < verbs.Count; i++)
            {
                int ago = 0;
                for (int j = recent.Count - 1; j >= 0 && TardisInterior.Verb(recent[j]) != verbs[i]; j--) ago++;
                weight[i] = 1f + Mathf.Min(ago, 8);
                total += weight[i];
            }
            float r = Random.value * total;
            int pick = 0;
            while (pick < verbs.Count - 1 && r >= weight[pick]) r -= weight[pick++];
            var of = fresh.FindAll(k => TardisInterior.Verb(k) == verbs[pick]);
            return of[Random.Range(0, of.Count)];
        }

        static void Ask()
        {
            var pool = PoolNow();
            if (pool.Count == 0) return;
            // A run of two or three controls, more likely and longer as the flight goes on.
            int n = 1;
            float p = Mathf.Clamp01((Tardis.Progress - 0.2f) * 0.9f);
            if (pool.Count >= 8 && Random.value < p) n = Tardis.Progress > 0.6f && Random.value < 0.5f ? 3 : 2;
            var list = new List<string>();
            string verb = lastVerb;
            for (int i = 0; i < n; i++)
            {
                var k = Pick(pool.FindAll(c => !list.Contains(c)), verb);
                list.Add(k);
                recent.Add(k);
                verb = TardisInterior.Verb(k);
            }
            while (recent.Count > 24) recent.RemoveAt(0);
            lastVerb = verb;
            chain = list.ToArray();
            step = 0;
            Target();
            TardisInterior.PlayBeep(1500f);
        }

        // The control now asked for: its halo and its clock.
        static void Target()
        {
            Prompt = chain[step];
            Until = Time.time + StepWindow;
            TardisInterior.SetHalo(Prompt, true, new Color(1f, 0.85f, 0.2f));
        }

        static void Clear()
        {
            if (Prompt != null) TardisInterior.SetHalo(Prompt, false, Color.white);
            Prompt = null;
            chain = null;
            nextAt = Time.time + Gap();
        }

        static void Stop()
        {
            Clear();
            Active = false;
        }

        // What the monitor says to do: "FLIP ARTRON SHUNT", and for a run the controls still to come ("FLIP ARTRON SHUNT, THEN 2 MORE").
        public static string PromptText
        {
            get
            {
                if (Prompt == null) return null;
                var s = TardisInterior.Verb(Prompt) + " " + TardisInterior.ControlName(Prompt);
                int more = chain == null ? 0 : chain.Length - step - 1;
                if (more > 0) s += ", THEN " + more + " MORE";
                return s;
            }
        }

        public static void OnControlUsed(string key)
        {
            if (!Active || Prompt == null || !InPool(key)) return;
            if (key == Prompt) Next();
            else if (chain != null && System.Array.IndexOf(chain, key, 0, step) >= 0) return;   // one just worked in this run: not a wrong control
            else Wrong();
        }

        // The right control was worked: on to the next of the run, or the prompt is done.
        static void Next()
        {
            if (chain != null && step + 1 < chain.Length)
            {
                TardisInterior.SetHalo(Prompt, false, Color.white);
                Tardis.Stability = Mathf.Min(1f, Tardis.Stability + 0.05f);
                step++;
                Target();
                TardisInterior.PlayBeep(1650f);
                return;
            }
            Hit();
        }

        public static void Hit()
        {
            if (Prompt == null) return;
            bool run = chain != null && chain.Length > 1;
            Hits++;
            if (run) Runs++;
            MissStreak = 0;
            Tardis.Stability = Mathf.Min(1f, Tardis.Stability + (run ? 0.2f : 0.15f));
            Clear();
            TardisInterior.PlayBeep(1760f);
        }

        public static void Miss()
        {
            if (Prompt == null) return;
            Misses++;
            MissStreak++;
            Tardis.Stability = Mathf.Max(0f, Tardis.Stability - 0.15f);
            Clear();
            G.Flash("MISSED", 1.5f);
            TardisInterior.PlayBeep(180f);
            if (MissStreak >= 3) EmergencyLanding();
        }

        static void Wrong()
        {
            Wrongs++;
            Tardis.Stability = Mathf.Max(0f, Tardis.Stability - 0.15f);
            G.Flash("WRONG CONTROL", 1.5f);
            TardisInterior.PlayBeep(180f);
            if (Wrongs % 3 == 0)
            {
                Systems.Damage(150f);
                Subsystems.Damage(Random.Range(0, Subsystems.Count), 30f);
            }
        }

        static void EmergencyLanding()
        {
            Systems.Damage(300f);
            Subsystems.Damage(Random.Range(0, Subsystems.Count), 35f);
            G.Flash("EMERGENCY LANDING", 3f);
            Stop();
            Tardis.Interrupt();
        }


    }
}
