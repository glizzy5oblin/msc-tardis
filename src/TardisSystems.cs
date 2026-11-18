using UnityEngine;

namespace MscTardis
{
    // Fuel, hull, HADS and repair. The numbers are the GMod TARDIS mod's (sh_artronenergy, sh_health, sh_hads, sh_repair).
    static class Systems
    {
        public const float ArtronMax = 100f, HealthMax = 3000f;
        const float W = 1f / 144f;   // the original writes its fuel values as weights over 144

        public const float CostDemat = 540f * W, CostMat = 360f * W, CostFull = 1200f * W, CostFailedDemat = 80f * W,
            CostHads = 180f * W, CostFastReturn = 360f * W, CostFloat = 270f * W;
        public const float SpendVortex = 32f * W, SpendFlightStatic = 5f * W, SpendFlightMoving = 24f * W, SpendFlightBoost = 45f * W, SpendFlightBroken = 8f * W;
        const float RechargeNormal = 15f * 5f * W, RechargeDamaged = 10f * 5f * W, RechargeBroken = 6f * 5f * W, RechargePowerOff = 24f * 5f * W, RechargeFloat = 8f * 5f * W;
        const float ChargeEvery = 5f, HandbrakeRate = 0.01f, HandbrakeRateFloat = 0.005f;
        const float DamagedPercent = 40f, BrokenPercent = 10f, RepairMaxSeconds = 60f;
        const float HadsRetry = 10f, HadsRange = 300f;

        static float chargeTimer, hadsReturnAt, hadsRetryAt, repairUntil;
        static Vector3 hadsHome;
        static float hadsHomeYaw;
        static bool hadsAway;
        static ParticleSystem smoke;
        static AudioSource alarm;

        public static float Artron { get { return MscTardis.Data.Artron; } }
        public static float Health { get { return MscTardis.Data.Health; } }
        public static float HealthPercent { get { return MscTardis.Data.Health / HealthMax * 100f; } }
        public static bool Damaged { get { return HealthPercent <= DamagedPercent; } }
        public static bool Broken { get { return HealthPercent <= BrokenPercent; } }
        public static bool RepairPrimed { get; private set; }
        public static bool Repairing { get; private set; }

        public static void Build(GameObject on)
        {
            chargeTimer = 0f;
            hadsAway = false;
            RepairPrimed = false;
            Repairing = false;
            MscTardis.Data.Artron = Mathf.Clamp(MscTardis.Data.Artron, 0f, ArtronMax);
            MscTardis.Data.Health = Mathf.Clamp(MscTardis.Data.Health, 0f, HealthMax);
            alarm = Sfx.Source(on, null, 0.8f, 40f);

            var go = new GameObject("Smoke");
            go.transform.SetParent(on.transform, false);
            go.transform.localPosition = new Vector3(0f, 3.1f, 0f);
            go.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            smoke = go.AddComponent<ParticleSystem>();
            smoke.startLifetime = 3f;
            smoke.startSpeed = 1.5f;
            smoke.startSize = 1.2f;
            smoke.startColor = new Color(0.25f, 0.25f, 0.25f, 0.5f);
            smoke.emissionRate = 0f;
            smoke.playOnAwake = true;
            var pr = go.GetComponent<ParticleSystemRenderer>();
            pr.sharedMaterial = Res.RoomMat("halo.png", Res.RoomMode.Glass, Color.white);
        }

        public static void AddArtron(float v)
        {
            var d = MscTardis.Data;
            d.Artron = Mathf.Clamp(d.Artron + v, 0f, ArtronMax);
            if (d.Artron <= 0f && v < 0f) Depleted();
        }

        static void Depleted()
        {
            if (Tardis.State != Tardis.Phase.Idle) { Tardis.Interrupt(); return; }
            if (MscTardis.Data.Power)
            {
                Flight.Stop();
                MscTardis.Data.Power = false;
                G.Flash("ARTRON ENERGY DEPLETED", 3f);
            }
        }

        public static void Damage(float amount)
        {
            var d = MscTardis.Data;
            if (amount <= 0f || Repairing) return;
            amount *= Protocols.DamageFactor();
            if (amount <= 0f) return;
            bool wasDamaged = Damaged;
            // Shields: working they change nothing, damaged they let a half more through, off or failed twice as much.
            float sf = Subsystems.Factor(Subsystems.Shields);
            amount *= sf >= 1f ? 1f : sf > 0f ? 1.5f : 2f;
            Subsystems.DamageRandom(amount);
            d.Health = Mathf.Max(0f, d.Health - amount);
            if (!wasDamaged && Damaged) Warn();
            if (d.Health <= 0f) Destroyed();
            else TryHads();
        }

        static void Warn()
        {
            var bell = Sfx.Named("cloister_bell");
            if (bell != null) alarm.PlayOneShot(bell);
            if (MscTardis.Data.Inside) G.Flash("HULL DAMAGED", 3f);
        }

        static void Destroyed()
        {
            Explode();
            MscTardis.Data.Artron = 0f;
            Flight.Stop();
            MscTardis.Data.Power = false;
            G.Flash("TARDIS DESTROYED: PRIME THE REPAIR CONTROL", 4f);
        }

        // Protocol DD0000666: the hull goes to zero with the big explosion; the player isn't hurt.
        public static void SelfDestruct()
        {
            MscTardis.Data.Health = 0f;
            Destroyed();
        }

        // Small explosions in broken flight, a big one at HP 0: a flash, a burst of smoke and the malfunction sound.
        public static void Explode()
        {
            if (smoke != null) smoke.Emit(25);
            var c = Sfx.Named("force_field_down");
            if (c != null) alarm.PlayOneShot(c, 1f);
        }

        // ---------------------------------------------------------------------------------------------------------
        // HADS: armed + hit = forced fast demat to a random spot, back again after 10-25 s.

        static void TryHads()
        {
            var d = MscTardis.Data;
            if (!d.Hads || hadsAway || Tardis.State != Tardis.Phase.Idle || Flight.Airborne || Time.time < hadsRetryAt) return;
            if (!d.Power || d.Artron < CostHads)
            {
                hadsRetryAt = Time.time + HadsRetry;
                G.Flash("TARDIS UNDER ATTACK", 3f);
                return;
            }
            hadsHome = Tardis.Position;
            hadsHomeYaw = Tardis.Yaw;
            var off = Random.insideUnitCircle.normalized * Random.Range(HadsRange * 0.3f, HadsRange);
            var at = new Vector3(hadsHome.x + off.x, 300f, hadsHome.z + off.y);
            if (Tardis.Travel(at, hadsHomeYaw, "HADS", false, Tardis.Kind.Hads) != null) return;
            hadsAway = true;
            hadsReturnAt = -1f;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Repair: prime it from the console (power goes off), leave, and it runs while nobody is inside.

        public static string TogglePrimeRepair()
        {
            if (Tardis.Busy) return "NOT WHILE IN FLIGHT";
            if (Repairing) return "ALREADY REPAIRING";
            if (RepairPrimed) { RepairPrimed = false; MscTardis.Data.Power = MscTardis.Data.Health > 0f; return null; }
            if (MscTardis.Data.Health >= HealthMax) return "THE TARDIS DOESN'T NEED REPAIRS";
            RepairPrimed = true;
            MscTardis.Data.Power = false;
            G.Flash("REPAIR PRIMED: LEAVE THE TARDIS", 3f);
            return null;
        }

        public static void OnPlayerLeft()
        {
            if (!RepairPrimed) return;
            RepairPrimed = false;
            Repairing = true;
            repairUntil = Time.time + Mathf.Clamp(RepairMaxSeconds * (HealthMax - MscTardis.Data.Health) / HealthMax, 1f, RepairMaxSeconds);
            G.Flash("REPAIRING: THE DOORS ARE LOCKED", 3f);
        }

        // ---------------------------------------------------------------------------------------------------------

        public static void Tick()
        {
            var d = MscTardis.Data;
            float dt = Time.deltaTime;

            if (Repairing && Time.time >= repairUntil)
            {
                Repairing = false;
                d.Health = HealthMax;
                Subsystems.RepairAll();
                d.Power = true;
                G.Flash("TARDIS REPAIRED", 3f);
            }

            bool travelling = Tardis.State != Tardis.Phase.Idle;
            if (Tardis.State == Tardis.Phase.Flight) AddArtron(-SpendVortex * dt);
            if (Flight.On && !travelling)
            {
                if (Flight.Broken) AddArtron(-SpendFlightBroken * dt);
                else AddArtron(-(Flight.Boosting ? SpendFlightBoost : Flight.Moving ? SpendFlightMoving : SpendFlightStatic) * dt);
            }

            // Recharge, while parked or hovering.
            if (!travelling && !Flight.On)
            {
                chargeTimer += dt;
                if (chargeTimer >= ChargeEvery)
                {
                    chargeTimer -= ChargeEvery;
                    AddArtron(!d.Power ? RechargePowerOff : Flight.FloatOn ? RechargeFloat : Broken ? RechargeBroken : Damaged ? RechargeDamaged : RechargeNormal);
                }
                if (d.Handbrake && d.Power) AddArtron(ArtronMax * (Flight.FloatOn ? HandbrakeRateFloat : HandbrakeRate) * dt);
            }

            // HADS comes back once it has landed.
            if (hadsAway && !travelling)
            {
                if (hadsReturnAt < 0f) hadsReturnAt = Time.time + Random.Range(10f, 25f);
                else if (Time.time >= hadsReturnAt)
                {
                    hadsAway = false;
                    Tardis.Travel(hadsHome, hadsHomeYaw, "HADS RETURN", false, Tardis.Kind.Fast);
                }
            }

            // Low health smokes (only when there is something there to smoke).
            bool visible = Tardis.State == Tardis.Phase.Idle;
            smoke.emissionRate = visible && Damaged ? (Broken ? 12f : 4f) : 0f;
        }

    }

    // The part animation driver: a pose position that eases to a target by type. In the original, a travel or idle part
    // wraps back to its start on reaching the end, so its animation loops.
    class PartAnim
    {
        public enum Type { Toggle, PerpetualUse, Travel, Idle, Cycle }

        public Type Kind;
        public float Speed = 1.5f, Pos;
        public bool Run;   // cycle: one full turn under way
        public System.Func<bool> On;   // toggle: which end to head for

        public PartAnim(Type kind, float speed, System.Func<bool> on = null) { Kind = kind; Speed = speed; On = on; if (on != null && on()) Pos = 1f; }

        public void Tick(float dt, bool power, bool travelling, bool used)
        {
            switch (Kind)
            {
                case Type.Toggle:
                    Pos = Mathf.MoveTowards(Pos, On != null && On() ? 1f : 0f, dt * Speed);
                    break;
                case Type.Cycle:
                    if (Run) { Pos += dt * Speed; if (Pos >= 1f) { Pos = 0f; Run = false; } }
                    break;
                case Type.PerpetualUse:
                    if (used) Pos = Mathf.Repeat(Pos + dt * Speed, 1f);
                    break;
                case Type.Travel:
                    if (power)
                    {
                        if (travelling) Pos = Mathf.Repeat(Pos + dt * Speed, 1f);
                        else if (Pos != 0f) { Pos += dt * Speed; if (Pos >= 1f) Pos = 0f; }   // finish the cycle and rest
                    }
                    break;
                case Type.Idle:
                    if (power) Pos = Mathf.Repeat(Pos + dt * Speed, 1f);
                    break;
            }
        }
    }
}
