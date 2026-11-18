using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace MscTardis
{
    // Protocols: an ID typed on the console keypad (keys 1-0 and A-D; * clears, # sends) starts a challenge: a few lit caps and push buttons
    // on the switch board flash, and pressing them all, in any order, before the clock runs out enables the protocol (5 s to start, and each
    // correct press adds 2 s, each with the walk to the nearest flashing control on top: see ChallengeStart and WalkTime). A wrong press or running out
    // of time fails it, with no penalty. Entering an active protocol's ID turns it off at once. PROTOCOL CANCEL turns off the latest one.
    // The IDs are one table (Table, below): changing one is a one-line edit.
    static class Protocols
    {
        public enum Kind { Toggle, Action }

        public class Proto
        {
            public string Id, Name, Desc;
            public Kind Kind;
            public int Needs = -1;        // the subsystem that must be up, or -1
            public Func<string> On;       // starts it: null, or why not
            public Action Off;            // stops it (toggles)
        }

        // Indices into Table.
        public const int Lights = 0, Diagnostics = 1, Scanner = 2, Beacon = 3, Locator = 4, FaultLocator = 5, FastReturn = 6, Hads = 7, Shields = 8, Cloak = 9,
            Shell = 10, Drift = 11, Stasis = 12, Vent = 13, Telepathic = 14, Tow = 15, Siphon = 16, Chameleon = 17, Lockdown = 18, Siege = 19, Emergency = 20, SelfDestruct = 21, Interior = 22;

        public static readonly Proto[] Table = MakeTable();

        static Proto P(string id, string name, string desc, Kind kind, int needs, Func<string> on, Action off)
        {
            return new Proto { Id = id, Name = name, Desc = desc, Kind = kind, Needs = needs, On = on, Off = off };
        }

        static Proto[] MakeTable()
        {
            var t = new Proto[23];
            t[Lights] = P("1047", "LIGHTS / AMBIENCE", "DIM THE ROOM TO A SOFT NIGHT GLOW.", Kind.Toggle, -1, () => null, null);
            t[Diagnostics] = P("2218", "DIAGNOSTICS SCAN", "SCAN EVERY SYSTEM AND LIST THE FAULTS.", Kind.Action, -1, RunDiagnostics, null);
            t[Scanner] = P("3359", "EXTERIOR SCANNER", "A CAMERA VIEW OF OUTSIDE ON THE SCANNER PAGE.", Kind.Toggle, -1, () => null, null);
            t[Beacon] = P("4081", "BEACON", "FLASH THE LAMP ON TOP OF THE EXTERIOR.", Kind.Toggle, -1, () => null, null);
            t[Locator] = P("5126", "LOCATOR", "BEARING AND DISTANCE TO THE SATSUMA.", Kind.Toggle, Subsystems.Telepathic, StartLocator, () => locatorTarget = null);
            t[FaultLocator] = P("6293", "FAULT LOCATOR", "FLASH THE CONTROL THAT NEEDS ATTENTION.", Kind.Action, -1, StartFaultLocator, null);
            t[FastReturn] = P("70418", "FAST RETURN", "FLY BACK TO THE LAST PLACE THE TARDIS STOOD.", Kind.Action, -1, () => Tardis.FastReturn(), null);
            t[Hads] = P("A1507", "HADS", "HOSTILE ACTION DISPLACEMENT SYSTEM.", Kind.Toggle, -1, () => { MscTardis.Data.Hads = true; return null; }, () => MscTardis.Data.Hads = false);
            t[Shields] = P("A2614", "SHIELDS", "A QUARTER OF THE HULL DAMAGE. USES ARTRON.", Kind.Toggle, Subsystems.Shields, () => null, () => PlayNamed("force_field_down"));
            t[Cloak] = P("A3728", "CLOAK", "HIDE THE EXTERIOR FROM VIEW.", Kind.Toggle, Subsystems.Chameleon, () => null, null);
            t[Shell] = P("A4835", "ATMOSPHERIC SHELL", "NO RAIN AROUND THE TARDIS.", Kind.Toggle, -1, () => null, null);
            t[Drift] = P("B1946", "DRIFT COMPENSATOR", "A QUARTER OF THE LANDING DRIFT.", Kind.Toggle, Subsystems.Navigation, () => null, null);
            t[Stasis] = P("B2051", "STASIS FIELD", "YOUR NEEDS STOP CHANGING WHILE INSIDE.", Kind.Toggle, -1, () => null, () => stasisHeld = false);
            t[Vent] = P("B3162", "ARTRON VENT", "SPEND 40 ARTRON TO RESTORE EVERY SYSTEM BY 30.", Kind.Action, -1, RunVent, null);
            t[Telepathic] = P("C1273", "TELEPATHIC JUMP", "MORE NAMED PLACES ON THE SELECTOR.", Kind.Toggle, Subsystems.Telepathic, () => null, ExtrasOff);
            t[Tow] = P("C2384", "TOW BEAM", "BRING THE SATSUMA TO THE TARDIS.", Kind.Action, -1, RunTow, null);
            t[Siphon] = P("C3495", "LIGHTNING SIPHON", "FAST ARTRON RECHARGE IN A THUNDERSTORM.", Kind.Toggle, -1, () => null, null);
            t[Chameleon] = P("C4506", "CHAMELEON CIRCUIT", "CHANGE THE EXTERIOR.", Kind.Action, Subsystems.Chameleon, () => "NO EXTERIOR PATTERNS", null);
            t[Interior] = P("C5617", "INTERIOR CHANGE", "SWITCH TO THE NEXT INTERIOR.", Kind.Action, -1, RunInterior, null);
            t[Lockdown] =P("1963114", "LOCKDOWN", "LOCK AND SEAL THE DOORS.", Kind.Toggle, -1, () => { MscTardis.Data.DoorLocked = true; MscTardis.Data.DoorHeldOpen = false; return null; }, () => MscTardis.Data.DoorLocked = false);
            t[Siege] = P("D4D4217", "SIEGE MODE", "NO DAMAGE, NO TAKE-OFF, DOORS SEALED.", Kind.Toggle, -1, () => { MscTardis.Data.DoorLocked = true; MscTardis.Data.DoorHeldOpen = false; MscTardis.Data.Handbrake = true; return null; }, () => MscTardis.Data.DoorLocked = false);
            t[Emergency] = P("000001999", "EMERGENCY PROGRAM ONE", "FLY TO THE HOME YARD.", Kind.Action, -1, RunEmergency, null);
            t[SelfDestruct] = P("DD0000666", "SELF-DESTRUCT", "SIXTY SECONDS, THEN THE END. CANCELLABLE.", Kind.Toggle, -1, StartDestruct, CancelDestruct);
            return t;
        }

        // ---------------------------------------------------------------------------------------------------------
        // State.

        static readonly List<int> active = new List<int>();   // in the order they were enabled
        static float destructAt = -1f;
        static float nextBell, lockdownTrip;
        static Transform locatorTarget;
        static float scanUntil, ventFlash;
        static List<string> scanLines = new List<string>();
        public static string Entry = "";

        public static bool IsActive(int i)
        {
            if (i == Hads) return MscTardis.Data.Hads;
            if (i == SelfDestruct) return destructAt >= 0f;
            return active.Contains(i);
        }

        public static bool LightsOn { get { return IsActive(Lights); } }
        public static bool ScannerOn { get { return IsActive(Scanner); } }
        public static bool BeaconOn { get { return IsActive(Beacon); } }
        public static bool CloakOn { get { return IsActive(Cloak); } }
        public static bool ShellOn { get { return IsActive(Shell); } }
        public static bool LockdownOn { get { return IsActive(Lockdown) || IsActive(Siege); } }
        public static bool SiegeOn { get { return IsActive(Siege); } }
        public static bool TelepathicOn { get { return IsActive(Telepathic); } }
        public static float DriftFactor { get { return IsActive(Drift) ? 0.25f : 1f; } }
        public static float DestructLeft { get { return destructAt < 0f ? -1f : Mathf.Max(0f, destructAt - Time.time); } }

        // Hull damage after the protocols: siege takes none, shields a quarter.
        public static float DamageFactor()
        {
            if (SiegeOn) return 0f;
            return IsActive(Shields) ? 0.25f : 1f;
        }

        public static void Build()
        {
            active.Clear();
            Entry = "";
            challenge = null;
            destructAt = -1f;
            scanUntil = 0f;
            scanLines.Clear();
            locatorTarget = null;
            stasisHeld = false;
            var s = MscTardis.Data.Protocols ?? "";
            foreach (var id in s.Split(','))
            {
                int i = IndexOf(id);
                if (i >= 0 && i != Hads && i != SelfDestruct && !active.Contains(i)) active.Add(i);
            }
            if (active.Contains(Locator)) StartLocator();
        }

        public static void Save()
        {
            var ids = new List<string>();
            foreach (var i in active) ids.Add(Table[i].Id);
            MscTardis.Data.Protocols = string.Join(",", ids.ToArray());
        }

        public static int IndexOf(string id)
        {
            for (int i = 0; i < Table.Length; i++) if (Table[i].Id == id) return i;
            return -1;
        }

        static void PlayNamed(string name)
        {
            var c = Sfx.Named(name);
            if (c != null) TardisInterior.PlayClip(c);
        }

        // ---------------------------------------------------------------------------------------------------------
        // The keypad.

        public static string KeyFor(string controlKey)
        {
            switch (controlKey)
            {
                case "d_kp1": return "1"; case "z+": return "2"; case "d_kp2": return "3"; case "d_kp3": return "A";
                case "x-": return "4"; case "d_kp4": return "5"; case "x+": return "6"; case "d_kp5": return "B";
                case "d_kp6": return "7"; case "z-": return "8"; case "d_kp7": return "9"; case "d_kp8": return "C";
                case "d_kp9": return "*"; case "d_kp10": return "0"; case "d_kp11": return "#"; case "d_kp12": return "D";
            }
            return null;
        }

        public static readonly string[] KeypadControls =
            { "d_kp1", "z+", "d_kp2", "d_kp3", "x-", "d_kp4", "x+", "d_kp5", "d_kp6", "z-", "d_kp7", "d_kp8", "d_kp9", "d_kp10", "d_kp11", "d_kp12" };

        public static void Key(string k)
        {
            if (challenge != null) return;   // the keys are busy while a challenge runs
            if (k == "*") { Entry = ""; return; }
            if (k == "#") { Send(); return; }
            if (Entry.Length < MaxEntry) Entry += k;
        }

        static void Send()
        {
            var id = Entry;
            Entry = "";
            if (id.Length == 0) return;
            Enter(id);
        }

        // Typed in Returns what happened; the player sees flashes.
        public static string Enter(string id)
        {
            int i = IndexOf(id);
            if (i < 0) { G.Fail("UNKNOWN PROTOCOL " + id); return "unknown " + id; }
            var p = Table[i];
            if (!MscTardis.Data.Power) { G.Fail("NO POWER"); return "no power"; }
            if (IsActive(i)) { Deactivate(i); return p.Name + " OFF"; }
            var why = Unavailable(i);
            if (why != null) { G.Fail(p.Name + ": " + why); return why; }
            StartChallenge(i);
            return "challenge " + p.Name + ": press " + string.Join(" ", new List<string>(challenge.Targets).ToArray());
        }

        // Why a protocol can't start now, or null.
        public static string Unavailable(int i)
        {
            var p = Table[i];
            if (p.Needs >= 0 && !Subsystems.Up(p.Needs)) return Subsystems.Names[p.Needs] + " OFFLINE";
            if (i == Chameleon) return "NO EXTERIOR PATTERNS";
            if (i == Interior && !ModOptions.BetaConsoles) return "BETA CONSOLES ARE OFF (MOD SETTINGS)";
            if (i == FastReturn && Tardis.Busy) return "NOT WHILE IN FLIGHT";
            if (i == Emergency && Tardis.Busy) return "NOT WHILE IN FLIGHT";
            if (i == Tow && Tardis.Busy) return "NOT WHILE IN FLIGHT";
            if (FlightGame.Active) return "THE FLIGHT CONTROLS ARE BUSY";
            if (SiegeOn && i != Siege && (i == Emergency || i == FastReturn)) return "SIEGE MODE";
            return null;
        }

        public static string StateText(int i)
        {
            if (IsActive(i)) return i == SelfDestruct ? "T-" + Mathf.CeilToInt(DestructLeft) : "ACTIVE";
            var why = Unavailable(i);
            return why != null ? "UNAVAILABLE" : "AVAILABLE";
        }

        // ---------------------------------------------------------------------------------------------------------
        // The challenge.

        public class Challenge { public int Index; public HashSet<string> Targets = new HashSet<string>(), Pressed = new HashSet<string>(); public float Until; }
        static Challenge challenge;
        public static Challenge Current { get { return challenge; } }

        // The switch board's caps and levers (RELAY 6 and up), the plain push buttons, and the Toyota's spare switches and tube levers.
        public static readonly string[] Pool = BuildPool();
        static string[] BuildPool()
        {
            var l = new List<string>();
            for (int i = 6; i <= 30; i++) l.Add("d_sbp" + i);   // RELAY 1-5 are the take-off arm row
            l.AddRange(new[] { "d_pb1", "d_pb2", "d_bpb1", "d_bpb2", "d_bpb3", "d_bpb4", "d_lp1", "d_lp2", "d_ct1", "d_cp1", "d_cb1" });
            for (int i = 1; i <= 30; i++) l.Add("d_sw" + i);   // the Toyota's spare switches and tube levers
            for (int i = 1; i <= 6; i++) l.Add("d_ls" + i);
            return l.ToArray();
        }

        // The clock: a starting allowance, and every correct press adds more, so a long challenge is never cut off. On a big console (the
        // 2010's runs right round, the Toyota's spares stand metres out) the flashing controls can be a walk apart and out of sight, so each
        // allowance also gets the walk to the nearest one still to press: round the console, at an unhurried pace that leaves time to look.
        public const float ChallengeStart = 5f, ChallengePerPress = 2f;
        const float WalkPace = 0.7f, Reach = 0.6f;   // m/s, looking about as you go; how far off a control can be worked

        // Seconds to walk from a to b (room space) round the console at the room's middle: along a circle a step outside the outer of the
        // two, plus the change in distance from the middle, less the arm's reach.
        static float WalkTime(Vector3 a, Vector3 b)
        {
            var fa = new Vector2(a.x, a.z);
            var fb = new Vector2(b.x, b.z);
            float ra = fa.magnitude, rb = fb.magnitude;
            float path = Vector2.Angle(fa, fb) * Mathf.Deg2Rad * (Mathf.Max(ra, rb) + 0.4f) + Mathf.Abs(ra - rb);
            return Mathf.Max(0f, path - Reach) / WalkPace;
        }

        // The walk from a point to the nearest flashing control not yet pressed.
        static float ToNext(Vector3 from)
        {
            float best = -1f;
            foreach (var k in challenge.Targets)
            {
                Vector3 at;
                if (challenge.Pressed.Contains(k) || !TardisInterior.ControlPoint(k, out at)) continue;
                float t = WalkTime(from, at);
                if (best < 0f || t < best) best = t;
            }
            return Mathf.Max(0f, best);
        }

        public const int MaxEntry = 9;   // the longest ID

        public static int ChallengeSize(string id) { return id.Length >= 9 ? 6 : id.Length >= 7 ? 5 : id.Length >= 5 ? 4 : 3; }

        static void StartChallenge(int i)
        {
            EndChallenge();
            challenge = new Challenge { Index = i, Until = Time.time + ChallengeStart };
            int n = ChallengeSize(Table[i].Id);
            var avail = new List<string>(Pool);
            avail.RemoveAll(k => !TardisInterior.HasControl(k));
            while (challenge.Targets.Count < n && avail.Count > 0)
            {
                int r = UnityEngine.Random.Range(0, avail.Count);
                challenge.Targets.Add(avail[r]);
                avail.RemoveAt(r);
            }
            foreach (var k in challenge.Targets) TardisInterior.SetHalo(k, true, new Color(0.4f, 1f, 0.6f));
            if (TardisInterior.Root != null && G.Player != null) challenge.Until += ToNext(TardisInterior.Root.InverseTransformPoint(G.Player.position));
            G.Flash(Table[i].Name + ": PRESS THE FLASHING BUTTONS", 3f);
            TardisInterior.PlayBeep(880f);
        }

        static void EndChallenge()
        {
            if (challenge != null) foreach (var k in challenge.Targets) TardisInterior.SetHalo(k, false, Color.white);
            challenge = null;
        }

        // Called for every control used on the console.
        public static void OnControlUsed(string key)
        {
            var kp = TardisInterior.HasKeypad ? KeyFor(key) : null;
            if (kp != null) { Key(kp); return; }
            if (challenge == null || Array.IndexOf(Pool, key) < 0) return;
            if (challenge.Targets.Contains(key))
            {
                if (!challenge.Pressed.Add(key)) return;
                TardisInterior.SetHalo(key, false, Color.white);
                Vector3 at;
                challenge.Until += ChallengePerPress + (TardisInterior.ControlPoint(key, out at) ? ToNext(at) : 0f);
                if (challenge.Pressed.Count >= challenge.Targets.Count)
                {
                    int i = challenge.Index;
                    EndChallenge();
                    Activate(i);
                }
                return;
            }
            G.Fail("WRONG BUTTON: " + Table[challenge.Index].Name + " FAILED");
            EndChallenge();
        }

        // ---------------------------------------------------------------------------------------------------------
        // On and off.

        static void Activate(int i)
        {
            var p = Table[i];
            var why = Unavailable(i) ?? p.On();
            if (why != null) { G.Fail(p.Name + ": " + why); return; }
            if (p.Kind == Kind.Toggle && i != Hads && i != SelfDestruct) { active.Remove(i); active.Add(i); }
            if (i == Shields) PlayNamed("force_field_up");
            else TardisInterior.PlayBeep(1320f);
            G.Flash(p.Name + (p.Kind == Kind.Toggle ? ": ENABLED" : ": DONE"), 2.5f);
            Save();
        }

        static void Deactivate(int i)
        {
            var p = Table[i];
            active.Remove(i);
            if (p.Off != null) p.Off();
            TardisInterior.PlayBeep(440f);
            G.Flash(p.Name + ": OFF", 2.5f);
            Save();
        }

        // PROTOCOL CANCEL: a challenge in progress, else a self-destruct countdown, else the protocol enabled last.
        public static string Cancel()
        {
            if (challenge != null) { EndChallenge(); return "CHALLENGE CANCELLED"; }
            if (destructAt >= 0f) { Deactivate(SelfDestruct); return null; }
            if (Entry.Length > 0) { Entry = ""; return "ENTRY CLEARED"; }
            if (active.Count == 0 && !MscTardis.Data.Hads) return "NO PROTOCOL IS ACTIVE";
            int last = active.Count > 0 ? active[active.Count - 1] : Hads;
            Deactivate(last);
            return null;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Protocols that do something when started.

        static string RunInterior()
        {
            return TardisInterior.SetRoom((MscTardis.Data.Room + 1) % TardisInterior.Rooms.Length);
        }

        static string RunDiagnostics()
        {
            scanLines.Clear();
            scanLines.Add("HULL " + Systems.HealthPercent.ToString("0") + "%   ARTRON " + Systems.Artron.ToString("0"));
            int faults = 0;
            for (int i = 0; i < Subsystems.Count; i++)
            {
                var st = Subsystems.StateName(i);
                if (st != "OK") faults++;
                scanLines.Add(Subsystems.Names[i] + " " + Subsystems.HealthOf(i).ToString("0") + "% " + st);
            }
            scanLines.Add(faults == 0 ? "NO FAULTS FOUND" : faults + " FAULT(S) FOUND");
            scanUntil = Time.time + 90f;
            PlayNamed("scanner");
            return null;
        }

        public static List<string> ScanReport { get { return Time.time < scanUntil ? scanLines : null; } }

        static string RunVent()
        {
            if (Systems.Artron < 40f) return "NEEDS 40 ARTRON";
            Systems.AddArtron(-40f);
            for (int i = 0; i < Subsystems.Count; i++) Subsystems.Set(i, Subsystems.HealthOf(i) + 30f);
            PlayNamed("force_field_down");
            return null;
        }

        static string RunEmergency()
        {
            if (!Subsystems.Up(Subsystems.Dematerialisation)) return "DEMATERIALISATION OFFLINE";
            if (MscTardis.Data.Inside) G.Flash("EMERGENCY PROGRAM ONE: RETURNING TO THE HOME YARD", 4f);
            emergencyAt = Time.time + (MscTardis.Data.Inside ? 3.5f : 0f);
            return null;
        }
        static float emergencyAt = -1f;

        static string RunTow()
        {
            var car = FindCar();
            if (car == null) return "NO SATSUMA FOUND";
            if (G.Seated) return "GET OUT OF THE CAR FIRST";
            var at = Tardis.Position + Tardis.Root.right * 3.2f + Vector3.up * 0.4f;
            float y;
            if (Tardis.Ground(at.x, at.z, at.y + 3f, out y, true)) at.y = y + 0.4f;
            car.position = at;
            car.rotation = Quaternion.Euler(0f, Tardis.Yaw + 90f, 0f);
            var rb = car.GetComponent<Rigidbody>();
            if (rb != null) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
            PlayNamed("force_field");
            return null;
        }

        static Transform FindCar()
        {
            if (carCache != null) return carCache;
            return carCache = G.Find("SATSUMA(557kg, 248)");
        }
        static Transform carCache;

        static string StartLocator()
        {
            locatorTarget = FindCar();
            return locatorTarget == null ? "NO SATSUMA FOUND" : null;
        }

        // Bearing and distance, or null while the locator is off.
        public static string LocatorLine()
        {
            if (!IsActive(Locator) || locatorTarget == null) return null;
            var d = locatorTarget.position - Tardis.Position;
            d.y = 0f;
            float brg = Mathf.Repeat(Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg, 360f);
            return "SATSUMA " + d.magnitude.ToString("0") + " M  BEARING " + brg.ToString("000");
        }

        static void ExtrasOff()
        {
            var d = MscTardis.Data;
            if (d.Selector > Tardis.Places.Length) d.Selector = Tardis.Places.Length;
        }

        static string StartFaultLocator()
        {
            var key = FaultKey();
            if (key == null) return "NO FAULTS FOUND";
            faultKey = key;
            faultUntil = Time.time + 20f;
            TardisInterior.SetHalo(key, true, new Color(1f, 0.35f, 0.2f));
            G.Flash(Subsystems.FaultLine() ?? "FAULT", 4f);
            return null;
        }
        static string faultKey;
        static float faultUntil;

        // The control that deals with the first fault.
        static string FaultKey()
        {
            if (!Subsystems.MasterOn) return "d_sbm1";
            for (int i = 0; i < Subsystems.Count; i++)
            {
                if (!Subsystems.BreakerOn(i)) return "d_sb" + (11 + i);
                if (!Subsystems.CircuitOn(i)) return "d_sb" + (i + 1);
            }
            return null;
        }

        static string StartDestruct()
        {
            destructAt = Time.time + 60f;
            nextBell = 0f;
            G.Flash("SELF-DESTRUCT IN 60 SECONDS", 4f);
            return null;
        }

        static void CancelDestruct() { destructAt = -1f; }

        // ---------------------------------------------------------------------------------------------------------
        // Every frame.

        static readonly string[] needNames = { "Thirst", "Hunger", "Fatigue", "Urine", "Dirtiness", "Stress" };
        static readonly float[] held = new float[6];
        static bool stasisHeld;

        public static void Tick()
        {
            var d = MscTardis.Data;
            float dt = Time.deltaTime;

            if (challenge != null)
            {
                if (!d.Power) EndChallenge();
                else if (Time.time >= challenge.Until)
                {
                    G.Fail(Table[challenge.Index].Name + ": TOO SLOW");
                    EndChallenge();
                }
            }
            if (faultKey != null && Time.time >= faultUntil) { TardisInterior.SetHalo(faultKey, false, Color.white); faultKey = null; }
            if (faultKey != null && FaultKey() != faultKey) { TardisInterior.SetHalo(faultKey, false, Color.white); faultKey = FaultKey(); if (faultKey != null) TardisInterior.SetHalo(faultKey, true, new Color(1f, 0.35f, 0.2f)); }

            // Protocols that need the box's systems drop out when those fail.
            for (int k = active.Count - 1; k >= 0; k--)
            {
                int i = active[k];
                var p = Table[i];
                if (p.Needs >= 0 && !Subsystems.Up(p.Needs)) { Deactivate(i); G.Flash(p.Name + ": " + Subsystems.Names[p.Needs] + " OFFLINE", 3f); }
            }

            // Shields cost a trickle of artron.
            if (IsActive(Shields) && d.Power) Systems.AddArtron(-0.05f * dt);

            // Lockdown and siege keep the doors shut.
            if (LockdownOn && !d.DoorLocked) d.DoorLocked = true;
            if (SiegeOn && !d.Handbrake && Tardis.State == Tardis.Phase.Idle && !Flight.On) d.Handbrake = true;

            // Stasis: the player's needs hold still while they are inside.
            if (IsActive(Stasis) && d.Inside)
            {
                var g = HutongGames.PlayMaker.FsmVariables.GlobalVariables;
                for (int i = 0; i < needNames.Length; i++)
                {
                    var f = g.FindFsmFloat("Player" + needNames[i]);
                    if (f == null) continue;
                    if (!stasisHeld) held[i] = f.Value; else f.Value = held[i];
                }
                stasisHeld = true;
            }
            else stasisHeld = false;

            // Lightning siphon: a thunderstorm tops up the artron fast.
            if (IsActive(Siphon) && Thunder() && Tardis.State == Tardis.Phase.Idle) Systems.AddArtron(2.5f * dt);

            // Self-destruct: a bell every five seconds, then the end.
            if (destructAt >= 0f)
            {
                if (Time.time >= nextBell) { nextBell = Time.time + 5f; if (d.Inside) TardisInterior.PlayBell(); else Tardis.PlayBell(); }
                if (Time.time >= destructAt) { destructAt = -1f; G.Flash("SELF-DESTRUCT", 4f); Systems.SelfDestruct(); }
            }

            // Emergency Program One: off to the yard, past the handbrake.
            if (emergencyAt >= 0f && Time.time >= emergencyAt)
            {
                emergencyAt = -1f;
                d.Handbrake = false;
                d.DoorHeldOpen = false;
                var y = Tardis.Places[0];
                var why = Tardis.Travel(y.Pos, y.Yaw, "EMERGENCY PROGRAM ONE", false, Tardis.Kind.Fast);
                if (why != null) G.Flash("EMERGENCY PROGRAM ONE FAILED: " + why, 4f);
            }
        }

        // WeatherType 2 is a thunderstorm in the game's own weather FSM (MAP/CloudSystem/Clouds).
        static PlayMakerFSM weather;
        public static bool Thunder()
        {
            if (weather == null)
            {
                var t = G.Find("MAP/CloudSystem/Clouds");
                weather = t != null ? t.GetComponent<PlayMakerFSM>() : null;
                if (weather == null) return false;
            }
            var v = weather.FsmVariables.FindFsmInt("WeatherType");
            return v != null && v.Value == 2;
        }

        public static void Reset() { weather = null; carCache = null; }


    }
}
