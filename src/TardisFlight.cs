using System.Collections.Generic;
using UnityEngine;

namespace MscTardis
{
    // Sends the exterior's physics events to Flight.
    class TardisBody : MonoBehaviour
    {
        void FixedUpdate() { Flight.Step(); }
        void OnCollisionEnter(Collision c) { Flight.Hit(c); }
    }

    // Manual flight of the physical box, the float hover, the spin, and the pilot's view. The model and numbers are
    // sh_flight/sh_float's. GMod applies them per 66 Hz tick in Source units, so everything here is scaled by Tick (how much
    // of a 66 Hz tick this physics step is) and Unit (metres per Source unit).
    static class Flight
    {
        const float Unit = 0.0254f;
        const float ForceH = 15f, ForceV = 5f, ForceYaw = 2f, TwistDivisor = 200f, BoostSpeed = 2.5f, TiltPerKey = 5f;
        const float AngularBrake = 0.015f, LinearBrake = 0.01f;
        // The original's stabiliser is a force couple whose strength depends on Source's inertia values. These give the same
        // result as a critically damped spring, reaching level in about a second. LeanPerSpeed is its equilibrium: 7.5
        // degrees of lean at the top speed of 1500 units per second.
        const float Spring = 0.15f, Damper = 0.095f, LeanPerSpeed = 0.0035f;
        const float BrokenSpeed = 200f;
        const float ParkHeight = 0.35f, ParkSpeed = 1f;

        static Rigidbody rb;
        static Transform box;
        static float tilt, nextSkid, nextTurn, lastExplode, airborneSince, stillSince;
        static int keysLast;
        static Vector3 hitNormal;
        static bool wasHit, turnStabilise;
        static readonly List<Collider> ownColliders = new List<Collider>();

        public static bool On { get; private set; }
        public static bool FloatOn { get; private set; }
        public static bool Airborne { get { return rb != null && !rb.isKinematic; } }
        public static bool Moving { get; private set; }
        public static bool Boosting { get; private set; }
        public static bool Broken { get; private set; }
        public static float Speed { get { return rb != null && !rb.isKinematic ? rb.velocity.magnitude : 0f; } }

        // The input: the keys while piloting, or, in the private build only, keys injected for tests.
        static bool Key(KeyCode k, char c)
        {
            if (Pilot.Active && Input.GetKey(k)) return true;
            return false;
        }

        public static void Build(Transform root, List<Collider> colliders)
        {
            box = root;
            ownColliders.Clear();
            ownColliders.AddRange(colliders);
            On = false;
            FloatOn = false;
            Broken = false;
            rb = root.gameObject.AddComponent<Rigidbody>();
            rb.mass = 5000f;   // the original's Mass
            rb.isKinematic = true;
            rb.useGravity = false;
            rb.drag = 0f;
            rb.angularDrag = 0f;
            rb.maxAngularVelocity = 30f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
            SetMass();
            root.gameObject.AddComponent<TardisBody>();
        }

        // The box's shell is several colliders now (it is hollow at the doorway, with a plug that comes and goes), but it flies as the
        // one solid block it used to be: 1.44 x 3.1 x 1.44 m about its middle, whatever the pieces would work out to.
        static void SetMass()
        {
            rb.centerOfMass = new Vector3(0f, 1.57f, 0f);
            rb.inertiaTensor = new Vector3(4868f, 1728f, 4868f);   // mass / 12 * (the squares of the two other sides)
            rb.inertiaTensorRotation = Quaternion.identity;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Turning things on and off. Each returns null, or why it can't.

        public static string ToggleFlight()
        {
            if (On) { SetFlight(false); return null; }
            var d = MscTardis.Data;
            if (!d.Power) return "NO POWER";
            if (d.Handbrake) return "HANDBRAKE IS ON";
            if (Tardis.State != Tardis.Phase.Idle) return "NOT WHILE IN THE VORTEX";
            if (d.Artron <= 0f) return "NO ARTRON ENERGY";
            SetFlight(true);
            return null;
        }

        static void SetFlight(bool on)
        {
            On = on;
            if (on) { Lift(); airborneSince = Time.time; }
            if (!on) Broken = false;
            Sync();
        }

        public static string ToggleFloat()
        {
            var d = MscTardis.Data;
            if (FloatOn)
            {
                if (On) return "NOT WHILE FLYING";
                FloatOn = false;
                Sync();
                return null;
            }
            if (!d.Power) return "NO POWER";
            if (Tardis.State != Tardis.Phase.Idle) return "NOT WHILE IN THE VORTEX";
            if (d.Artron < Systems.CostFloat) return "NOT ENOUGH ARTRON ENERGY";
            Systems.AddArtron(-Systems.CostFloat);
            FloatOn = true;
            Lift();
            airborneSince = Time.time;
            Sync();
            return null;
        }

        // None -> direction -> none -> the other direction.
        public static string CycleSpin()
        {
            var d = MscTardis.Data;
            if (d.SpinDir != 0) { d.SpinLast = d.SpinDir; d.SpinDir = 0; }
            else d.SpinDir = d.SpinLast == 0 ? -1 : -d.SpinLast;
            return SpinName();
        }

        public static string SpinName()
        {
            int s = MscTardis.Data.SpinDir;
            return s == 0 ? "NONE" : s > 0 ? "CLOCKWISE" : "ANTI-CLOCKWISE";
        }

        // Flight and float both fall away: no power, out of fuel, or entering the vortex.
        public static void Stop()
        {
            On = false;
            FloatOn = false;
            Broken = false;
            Sync();
        }

        // On for the vortex: the box stops being a physical thing altogether.
        public static void Freeze()
        {
            On = false;
            FloatOn = false;
            Broken = false;
            if (rb.isKinematic) return;
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.isKinematic = true;
        }

        static void Lift()
        {
            if (rb.isKinematic)
            {
                rb.isKinematic = false;
                SetMass();
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
        }

        static void Sync()
        {
            if (rb.isKinematic) return;
            rb.useGravity = !On && !FloatOn;
        }

        public static void Park()
        {
            if (rb.isKinematic) return;
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.isKinematic = true;
            Tardis.SettleAfterFlight();
        }

        // ---------------------------------------------------------------------------------------------------------
        // The physics step.

        public static void Step()
        {
            if (rb == null || rb.isKinematic) return;
            float tick = Time.fixedDeltaTime * 66f;

            if (!On && !FloatOn)
            {
                TryPark();
                return;
            }
            stillSince = 0f;

            if (On) StepFlight(tick);
            else StepFloat(tick);
        }

        static void TryPark()
        {
            if (Time.time - airborneSince < 0.5f) return;
            float y;
            var at = box.position;
            if (Tardis.Ground(at.x, at.z, at.y + 1f, out y, true) && at.y - y < ParkHeight && rb.velocity.magnitude < ParkSpeed || at.y <= Tardis.WaterLevel) { Park(); return; }

            // Come to rest on something the ground check doesn't count, a tree say: it isn't going anywhere, so that's where it lands.
            if (rb.velocity.magnitude >= ParkSpeed) { stillSince = 0f; return; }
            if (stillSince == 0f) stillSince = Time.time;
            else if (Time.time - stillSince > 1.5f) Park();
        }

        static void StepFlight(float tick)
        {
            var d = MscTardis.Data;
            var vel = rb.velocity;
            float speed = vel.magnitude / Unit;   // Source units per second
            Vector3 fwd = Pilot.ViewForward, right = Pilot.ViewRight;

            float force = ForceH, vforce = ForceV, rforce = ForceYaw, tforce = TwistDivisor;
            bool brakes = false;
            bool boost = Key(KeyCode.LeftShift, 'b');
            Boosting = boost;
            if (boost)
            {
                float mult;
                if (Tardis.DoorOpen) { mult = 0.25f; brakes = true; }
                else
                {
                    mult = BoostSpeed;
                    if (d.SpinDir == 0) mult = Mathf.Max(1f, mult * 0.6f);
                }
                force *= mult;
                vforce *= mult;
                rforce *= mult;
            }

            bool w = Key(KeyCode.W, 'w'), s = Key(KeyCode.S, 's'), a = Key(KeyCode.A, 'a'), dk = Key(KeyCode.D, 'd');
            bool up = Key(KeyCode.Space, 'u'), down = Key(KeyCode.LeftControl, 'j'), rotate = Key(KeyCode.LeftAlt, 'r');
            Moving = w || s || a || dk || up || down;
            tilt = 0f;
            int keys = 0;
            if (w) { vel += fwd * force * tick * Unit; tilt += TiltPerKey; keys++; }
            if (s) { vel -= fwd * force * tick * Unit; tilt += TiltPerKey; keys++; }
            var ang = rb.angularVelocity;
            if (dk)
            {
                keys++;
                if (rotate) ang += box.up * (rforce * tick * Mathf.Deg2Rad);
                else { vel += right * force * tick * Unit; tilt += TiltPerKey; }
            }
            if (a)
            {
                keys++;
                if (rotate) ang -= box.up * (rforce * tick * Mathf.Deg2Rad);
                else { vel -= right * force * tick * Unit; tilt += TiltPerKey; }
            }
            if (down) { vel -= Vector3.up * vforce * tick * Unit; keys++; }
            else if (up) { vel += Vector3.up * vforce * tick * Unit; keys++; }

            Broken = Systems.Broken && speed > BrokenSpeed;
            if (Broken)
            {
                StepBroken(ref vel, ref ang, speed, boost, tick);
                keysLast = keys;
                rb.velocity = vel;
                rb.angularVelocity = ang;
                return;
            }

            if (Tardis.DoorOpen) brakes = true;
            if (d.SpinDir == 0 || brakes) tilt = 0f;

            if (Systems.Damaged)
            {
                bool randomSkid = false;
                if (Time.time > nextSkid)
                {
                    nextSkid = Time.time + Random.Range(3, 6);
                    randomSkid = speed > 400f;
                }
                if (keysLast < keys || randomSkid)
                {
                    float mult = (5f + 40f - Systems.HealthPercent) / 10f;
                    vel += Random.onUnitSphere * speed * Unit * (0.2f + Random.value) * mult;
                    ang += Random.onUnitSphere * (speed * mult * 0.01f * Mathf.Deg2Rad);
                    tforce *= 2f;
                }
            }
            keysLast = keys;

            if (d.SpinDir != 0 && !brakes)
                ang += box.up * (d.SpinDir * Mathf.Sqrt(speed / tforce) * tick * Mathf.Deg2Rad);

            Stabilise(ref vel, ref ang, tick);
            rb.velocity = vel;
            rb.angularVelocity = ang;
        }

        static void Stabilise(ref Vector3 vel, ref Vector3 ang, float tick)
        {
            // Lean into the flight and bank by tilt: the up it wants, against the up it has.
            var horizontal = vel; horizontal.y = 0f;
            var want = (Vector3.up + horizontal * LeanPerSpeed + box.right * Mathf.Sin(tilt * Mathf.Deg2Rad)).normalized;
            ang += Vector3.Cross(box.up, want) * (Spring * tick);

            // Pitch and roll are damped hard, the yaw only by the original's angular brake, so it keeps spinning.
            var yaw = Vector3.Project(ang, box.up);
            var rest = ang - yaw;
            ang = yaw * (1f - AngularBrake * tick) + rest * (1f - (Damper + AngularBrake) * tick);
            vel *= 1f - LinearBrake * tick;
        }

        // Below 10% hull at speed the box goes out of control: lurching off in new directions, bouncing off what it hits, exploding.
        static void StepBroken(ref Vector3 vel, ref Vector3 ang, float speed, bool boost, float tick)
        {
            if (Time.time > nextTurn || wasHit)
            {
                var dir = Random.onUnitSphere * speed * Unit;
                if (wasHit) dir -= 2f * hitNormal * speed * Unit;
                if (boost) dir *= 3f;
                float keep = wasHit ? -2f : 0.7f;
                wasHit = false;
                if (Random.Range(0, 5) == 0 && Time.time - lastExplode > 2f)
                {
                    lastExplode = Time.time;
                    Systems.Explode();
                    ang = Random.onUnitSphere * (speed * Mathf.Deg2Rad);
                    vel += -0.75f * vel + dir * Random.Range(2f, 5f) * 0.1f;
                }
                else vel += 0.5f * (keep * vel + dir * Random.Range(2f, 3f) * 0.1f);
                turnStabilise = Random.Range(0, 4) == 0;
                nextTurn = Time.time + Random.Range(0, 3) + 0.5f;
            }
            if (speed < 2000f) vel *= 1f + 0.02f * tick;
            if (turnStabilise && Random.Range(0, 6) != 0) Stabilise(ref vel, ref ang, tick);
            else ang += Random.onUnitSphere * (40f * tick * Mathf.Deg2Rad);
        }

        // Hover without flight: the keys only turn the box. Gravity is off, so it stays where it is.
        static void StepFloat(float tick)
        {
            Moving = false;
            Boosting = false;
            float force = Key(KeyCode.LeftShift, 'b') ? 2.5f : 1f;
            var ang = rb.angularVelocity;
            Vector3 fwd = Pilot.ViewForward, right = Pilot.ViewRight;
            float k = force * tick * Mathf.Deg2Rad;
            if (Key(KeyCode.W, 'w')) ang += right * k;
            if (Key(KeyCode.S, 's')) ang -= right * k;
            bool rotate = Key(KeyCode.LeftAlt, 'r');
            if (Key(KeyCode.D, 'd')) ang += rotate ? box.up * (k * 2f) : -fwd * k;
            if (Key(KeyCode.A, 'a')) ang -= rotate ? box.up * (k * 2f) : -fwd * k;
            if (Key(KeyCode.Space, 'u')) ang *= 1f - 0.05f * tick;   // the brake
            rb.angularVelocity = ang;
            rb.velocity *= 1f - LinearBrake * tick;
        }

        public static void Hit(Collision c)
        {
            if (rb == null || rb.isKinematic) return;
            var n = c.contacts.Length > 0 ? c.contacts[0].normal : Vector3.up;
            float speed = c.relativeVelocity.magnitude / Unit;
            bool vertical = Mathf.Abs(n.y) > 0.7f;
            hitNormal = n;
            wasHit = Broken;
            if (speed >= (vertical ? 1500f : 300f)) Systems.Damage((vertical ? 0.2f : 1f) * speed / 25f);
        }
    }

    // Sitting at the console flying from outside: the game's camera moves to a chase rig behind the box, and the player's own
    // controls are switched off. The player's body stays in the interior the whole time.
    static class Pilot
    {
        public static bool Active { get; private set; }

        static Transform rig, oldParent;
        static Vector3 oldLocalPos;
        static Quaternion oldLocalRot;
        static float yaw, pitch = 12f, dist = 9f;

        // Destination mode: a ghost box moved with the flight keys; it snaps to the ground under it.
        static Transform ghost;
        static bool destMode, aiming;
        static Vector3 ghostPos;
        static float ghostYaw;

        public static Vector3 ViewForward { get { return Quaternion.Euler(pitch, yaw, 0f) * Vector3.forward; } }
        public static Vector3 ViewRight { get { return Quaternion.Euler(0f, yaw, 0f) * Vector3.right; } }

        public static void Build()
        {
            Active = false;
            destMode = false;
            aiming = false;
            rig = new GameObject("MscTardisPilotRig").transform;
            yaw = Tardis.Yaw + 180f;
            ghost = Tardis.BuildGhost();
            ghost.gameObject.SetActive(false);
        }

        public static string Enter()
        {
            if (Active) return "already piloting";
            if (!MscTardis.Data.Inside) return "not inside";
            if (!MscTardis.Data.Power) return "NO POWER";
            if (Computer.Open) return "CLOSE THE COMPUTER FIRST";
            Active = true;
            var cam = G.Cam.transform;
            oldParent = cam.parent;
            oldLocalPos = cam.localPosition;
            oldLocalRot = cam.localRotation;
            cam.SetParent(rig, false);
            cam.localPosition = Vector3.zero;
            cam.localRotation = Quaternion.identity;

            PlayerHold.Add("pilot");
            G.Flash("PILOTING: PRESS USE TO LEAVE THE CONTROLS", 3f);
            return null;
        }

        public static void Exit()
        {
            if (!Active) return;
            Active = false;
            destMode = false;
            aiming = false;
            ghost.gameObject.SetActive(false);
            var cam = G.Cam.transform;
            cam.SetParent(oldParent, false);
            cam.localPosition = oldLocalPos;
            cam.localRotation = oldLocalRot;
            PlayerHold.Remove("pilot");
            Tardis.ClearPrompt();
        }

        static void Say(string why) { if (why != null) G.Fail(why); }

        public static void Tick()
        {
            if (!Active) return;
            if (G.UsePressed) { Exit(); return; }

            yaw += Input.GetAxis("Mouse X") * 3f;
            pitch = Mathf.Clamp(pitch - Input.GetAxis("Mouse Y") * 3f, -80f, 80f);
            dist = Mathf.Clamp(dist - Input.GetAxis("Mouse ScrollWheel") * 8f, 4f, 40f);
            var focus = Tardis.Position + Vector3.up * 1.6f;
            var rot = Quaternion.Euler(pitch, yaw, 0f);
            var back = rot * Vector3.back;
            float d = dist;
            foreach (var h in Physics.RaycastAll(focus, back, dist, ~0))
            {
                if (h.collider.isTrigger || h.collider.transform.IsChildOf(Tardis.Root)) continue;
                d = Mathf.Min(d, Mathf.Max(0.5f, h.distance - 0.3f));
            }
            rig.position = focus + back * d;
            rig.rotation = rot;

            if (Input.GetKeyDown(KeyCode.R)) Say(Flight.ToggleFlight());
            if (Input.GetKeyDown(KeyCode.T)) Say(Flight.ToggleFloat());
            if (Input.GetKeyDown(KeyCode.J)) TardisInterior.UseControl("handbrake");
            if (Input.GetKeyDown(KeyCode.P)) TardisInterior.UseControl("power");
            if (Input.GetMouseButtonDown(1)) G.Flash("SPIN: " + Flight.CycleSpin());
            if (Input.GetKeyDown(KeyCode.H)) ToggleDestination();
            TickTargeting();
            G.Interaction(Hud());
        }

        static void ToggleDestination()
        {
            destMode = !destMode;
            ghost.gameObject.SetActive(destMode);
            if (destMode)
            {
                ghostPos = Tardis.HasDestination ? Tardis.Destination : Tardis.Position;
                ghostYaw = Tardis.Yaw;
                return;
            }
            Tardis.SetDestination(ghostPos, ghostYaw);
            G.Flash("DESTINATION SET: " + ghostPos.x.ToString("0") + " / " + ghostPos.z.ToString("0"));
        }

        // Destination mode's ghost, or LMB: hold to aim at where the camera looks, release to demat there. In the vortex, LMB lands.
        static void TickTargeting()
        {
            if (destMode)
            {
                float speed = (Input.GetKey(KeyCode.LeftShift) ? 60f : 15f) * Time.deltaTime;
                if (Input.GetKey(KeyCode.W)) ghostPos += ViewForward * speed;
                if (Input.GetKey(KeyCode.S)) ghostPos -= ViewForward * speed;
                if (Input.GetKey(KeyCode.LeftAlt))
                {
                    if (Input.GetKey(KeyCode.D)) ghostYaw += 60f * Time.deltaTime;
                    if (Input.GetKey(KeyCode.A)) ghostYaw -= 60f * Time.deltaTime;
                }
                else
                {
                    if (Input.GetKey(KeyCode.D)) ghostPos += ViewRight * speed;
                    if (Input.GetKey(KeyCode.A)) ghostPos -= ViewRight * speed;
                }
                if (Input.GetKey(KeyCode.Space)) ghostPos += Vector3.up * speed;
                if (Input.GetKey(KeyCode.LeftControl)) ghostPos -= Vector3.up * speed;
                float y;
                if (Tardis.Ground(ghostPos.x, ghostPos.z, ghostPos.y + 1f, out y, true)) ghostPos.y = y;
                ghost.position = ghostPos;
                ghost.rotation = Quaternion.Euler(0f, ghostYaw, 0f);
                if (Input.GetMouseButtonDown(0)) ToggleDestination();
                return;
            }

            if (Tardis.State == Tardis.Phase.Flight)
            {
                if (Input.GetMouseButtonDown(0)) Tardis.RequestLanding();
                return;
            }
            if (Tardis.State != Tardis.Phase.Idle) return;

            if (Input.GetMouseButtonDown(0)) aiming = true;
            if (aiming && Input.GetMouseButton(0))
            {
                RaycastHit hit;
                var cam = G.Cam.transform;
                bool found = false;
                foreach (var h in SortedHits(cam.position, cam.forward))
                {
                    if (h.collider.isTrigger || h.collider.transform.IsChildOf(Tardis.Root)) continue;
                    hit = h;
                    ghostPos = hit.point;
                    found = true;
                    break;
                }
                ghost.gameObject.SetActive(found);
                if (found)
                {
                    ghost.position = ghostPos;
                    ghost.rotation = Quaternion.Euler(0f, Tardis.Yaw, 0f);
                }
                aimFound = found;
            }
            else if (aiming)
            {
                aiming = false;
                ghost.gameObject.SetActive(false);
                if (aimFound)
                {
                    Say(Tardis.Travel(new Vector3(ghostPos.x, ghostPos.y + 3f, ghostPos.z), Tardis.Yaw, "PILOT TARGET", true));
                    return;
                }
                if (Tardis.HasDestination) Say(Tardis.Travel(Tardis.Destination + Vector3.up * 3f, Tardis.Yaw, "DESTINATION", true));
                else Say(TardisInterior.TakeOffSelected());
            }
        }

        static bool aimFound;

        static RaycastHit[] SortedHits(Vector3 from, Vector3 dir)
        {
            var hits = Physics.RaycastAll(from, dir, 3000f, ~0);
            System.Array.Sort(hits, (p, q) => p.distance.CompareTo(q.distance));
            return hits;
        }

        static string Hud()
        {
            var d = MscTardis.Data;
            string mode = destMode ? "DESTINATION" : Tardis.State != Tardis.Phase.Idle ? Tardis.State.ToString().ToUpperInvariant() : Flight.On ? "FLIGHT" : Flight.FloatOn ? "FLOAT" : "PARKED";
            return mode + "  FUEL " + d.Artron.ToString("0") + "  HULL " + Systems.HealthPercent.ToString("0") + "%  " + (Flight.Speed * 3.6f).ToString("0") + " KM/H  SPIN " + Flight.SpinName();
        }
    }
}
