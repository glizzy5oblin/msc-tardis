using System.Text;
using UnityEngine;

namespace MscTardis
{
    // The six subsystems: Navigation, Dematerialisation, Shields, Chameleon, Telepathic and Life support. Each has health (0..100) and
    // a breaker and a circuit on the console's switch board; MASTER is the main bus. A subsystem is UP when MASTER, its circuit and its
    // breaker are on and it hasn't failed. A fault trips its breaker (it flips off); the player resets it by hand. A failed subsystem
    // trips its breaker again until it is repaired. DAMAGED (below 40) gives half the effect, FAILED (below 10) none.
    static class Subsystems
    {
        public const int Count = 6;
        public const int Navigation = 0, Dematerialisation = 1, Shields = 2, Chameleon = 3, Telepathic = 4, LifeSupport = 5;
        public static readonly string[] Names = { "NAVIGATION", "DEMATERIALISATION", "SHIELDS", "CHAMELEON", "TELEPATHIC", "LIFE SUPPORT" };
        public static readonly string[] Keys = { "nav", "demat", "shields", "chameleon", "telepathic", "life" };
        public enum State { Ok, Damaged, Failed, Off }

        const float DamagedBelow = 40f, FailedBelow = 10f;
        const float LifeDangerSeconds = 180f;   // inside with life support down: stress and fatigue reach their limit in about this long

        static float bellAt, lifeSeconds;
        public static float Emergency { get; private set; }   // 0 normal .. 1 emergency lighting, eased

        static float[] Health { get { Fix(); return MscTardis.Data.SubHealth; } }

        static void Fix()
        {
            var d = MscTardis.Data;
            if (d.SubHealth != null && d.SubHealth.Length == Count) return;
            var h = new float[Count];
            for (int i = 0; i < Count; i++) h[i] = d.SubHealth != null && i < d.SubHealth.Length ? d.SubHealth[i] : 100f;
            d.SubHealth = h;
        }

        public static float HealthOf(int i) { return Mathf.Clamp(Health[i], 0f, 100f); }
        // The switch board is the coral console's. A room without one (Toyota, 2010) has the bus, circuits and breakers always on, so only
        // health can take a subsystem down there.
        public static bool Board = true;
        public static bool MasterOn { get { return !Board || MscTardis.Data.MasterOn; } }
        public static bool CircuitOn(int i) { return !Board || (MscTardis.Data.Circuits & (1 << i)) != 0; }
        public static bool BreakerOn(int i) { return !Board || (MscTardis.Data.Breakers & (1 << i)) != 0; }

        public static void SetMaster(bool on) { MscTardis.Data.MasterOn = on; }
        public static void SetCircuit(int i, bool on) { MscTardis.Data.Circuits = on ? MscTardis.Data.Circuits | (1 << i) : MscTardis.Data.Circuits & ~(1 << i); }
        public static void SetBreaker(int i, bool on) { MscTardis.Data.Breakers = on ? MscTardis.Data.Breakers | (1 << i) : MscTardis.Data.Breakers & ~(1 << i); }

        public static bool Enabled(int i) { return MasterOn && CircuitOn(i) && BreakerOn(i); }
        public static bool Failed(int i) { return HealthOf(i) < FailedBelow; }
        public static bool Up(int i) { return Enabled(i) && !Failed(i); }

        public static State StateOf(int i)
        {
            if (!Enabled(i)) return State.Off;
            return Failed(i) ? State.Failed : HealthOf(i) < DamagedBelow ? State.Damaged : State.Ok;
        }

        // 1 for working, 0.5 for damaged, 0 for off or failed.
        public static float Factor(int i)
        {
            if (!Up(i)) return 0f;
            return HealthOf(i) < DamagedBelow ? 0.5f : 1f;
        }

        public static string StateName(int i)
        {
            switch (StateOf(i))
            {
                case State.Ok: return "OK";
                case State.Damaged: return "DAMAGED";
                case State.Failed: return "FAILED";
                default: return Failed(i) && MasterOn ? "FAILED" : MasterOn ? (BreakerOn(i) ? "OFF" : "TRIPPED") : "NO BUS";
            }
        }

        // Harm to one subsystem. A hard hit throws its breaker.
        public static void Damage(int i, float amount)
        {
            if (amount <= 0f) return;
            Health[i] = Mathf.Max(0f, Health[i] - amount);
            if (amount >= 20f || Failed(i)) Trip(i);
        }

        public static void Trip(int i)
        {
            if (!Board || !BreakerOn(i)) return;
            SetBreaker(i, false);
            if (MscTardis.Data.Inside) G.Flash(Names[i] + ": BREAKER " + (i + 1) + " TRIPPED", 3f);
        }

        // Hull damage spreads into one subsystem at random (a share of the hull's 3000 points).
        public static void DamageRandom(float hullDamage)
        {
            Damage(Random.Range(0, Count), hullDamage / 15f);
        }

        public static void Set(int i, float health) { Health[i] = Mathf.Clamp(health, 0f, 100f); }

        public static void RepairAll()
        {
            for (int i = 0; i < Count; i++) Health[i] = 100f;
            MscTardis.Data.Breakers = (1 << Count) - 1;
        }

        public static bool AnyFault
        {
            get
            {
                for (int i = 0; i < Count; i++) if (StateOf(i) == State.Damaged || StateOf(i) == State.Failed || (MasterOn && !BreakerOn(i))) return true;
                return false;
            }
        }

        // What the player should do next, or null: the computer tells you, you do it on the console.
        public static string Instruction()
        {
            if (!MasterOn) return "MASTER IS OFF: SWITCH ON MASTER";
            for (int i = 0; i < Count; i++)
            {
                if (!BreakerOn(i)) return Failed(i) ? "BREAKER " + (i + 1) + " TRIPPED: " + Names[i] + " FAILED, REPAIR NEEDED" : "BREAKER " + (i + 1) + " TRIPPED: RESET BREAKER " + (i + 1);
                if (!CircuitOn(i)) return "CIRCUIT " + (i + 1) + " IS OFF: SWITCH ON CIRCUIT " + (i + 1);
            }
            for (int i = 0; i < Count; i++)
                if (Failed(i)) return Names[i] + " FAILED: REPAIR NEEDED";
            return null;
        }

        public static string FaultLine()
        {
            var s = Instruction();
            if (s != null) return s;
            for (int i = 0; i < Count; i++) if (StateOf(i) == State.Damaged) return Names[i] + " DAMAGED";
            return null;
        }

        // ---------------------------------------------------------------------------------------------------------

        public static void Tick()
        {
            float dt = Time.deltaTime;
            // A failed subsystem throws its breaker again as soon as it is reset.
            if (Board) for (int i = 0; i < Count; i++) if (Failed(i) && BreakerOn(i)) Trip(i);

            // Life support: down (off or failed) the room goes to emergency red, the cloister bell rings every 30 s until it is back, the hum stops, and the
            // air hurts: stress and fatigue rise while the player is inside, to their limit in about three minutes. Damaged gives half.
            float f = Factor(LifeSupport);
            float want = f >= 1f ? 0f : f > 0f ? 0.5f : 1f;
            Emergency = Mathf.MoveTowards(Emergency, want, dt / 1.5f);
            var d = MscTardis.Data;
            if (want > 0f && d.Power)
            {
                if (Time.time >= bellAt)
                {
                    bellAt = Time.time + 30f;
                    if (d.Inside) TardisInterior.RingLifeBell();
                }
            }
            else
            {
                // Life support is back (or the power has gone): the bell stops at once, whatever is left of its clip, and the next failure
                // rings straight away rather than waiting out the 30 s of the last.
                bellAt = 0f;
                TardisInterior.StopLifeBell();
            }
            if (f <= 0f && d.Inside && d.Power)
            {
                lifeSeconds += dt;
                float rise = 100f / LifeDangerSeconds * dt;
                G.Stress = Mathf.Min(100f, G.Stress + rise);
                G.Fatigue = Mathf.Min(100f, G.Fatigue + rise);
            }
            else if (f > 0f) lifeSeconds = 0f;
        }


    }
}
