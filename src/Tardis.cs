using System.Collections.Generic;
using UnityEngine;

namespace MscTardis
{
    // The TARDIS: the exterior in the world, its door, and the travel core. Everything that moves it goes through
    // Travel (the `goto` command and the piloting console both end up there). The interior is TardisInterior.
    static class Tardis
    {
        public class Place
        {
            public string Key, Name;
            public Vector3 Pos;
            public float Yaw;
            public Place(string key, string name, float x, float y, float z, float yaw) { Key = key; Name = name; Pos = new Vector3(x, y, z); Yaw = yaw; }
        }

        // Read in game with `tardis where`. y is only a hint: landing snaps to the ground under it.
        public static readonly Place[] Places =
        {
            new Place("yard", "HOME YARD", -14f, 0f, 16f, 180f),
            new Place("village", "PERAJARVI", -1422f, 9f, 1145f, 140f),
            new Place("shop", "TEIMO'S SHOP", -1536f, 3.5f, 1165f, 300f),
        };

        // The extra named places the telepathic jump adds to the selector (the scene objects' positions; the landing finds ground nearby).
        public static readonly Place[] Extras =
        {
            new Place("fleetari", "FLEETARI'S GARAGE", 1557f, 3.9f, 716f, 0f),
            new Place("dancehall", "DANCEHALL", 455f, 13f, 1308f, 0f),
            new Place("farm", "THE FARM", -853f, 1.4f, 1334f, 0f),
            new Place("landfill", "LANDFILL", -768f, -2.9f, -640f, 0f),
            new Place("cabin", "LAKE CABIN", -170f, -3.8f, 1010f, 0f),
        };

        public enum Phase { Idle, Dematerialising, Flight, Premat, Materialising }
        public enum Kind { Normal, Fast, Hads }

        // Exterior.Teleport from the original's metadata. A fade runs through its sequence at 66 * speed alpha units a
        // second: not smooth, the flicker is the recognisable look. 8.9 s each way at the normal speed.
        const float SequenceSpeed = 0.77f, SequenceSpeedWarning = 0.6f, SequenceSpeedFast = 0.935f, SequenceSpeedWarnFast = 0.97f, SequenceSpeedHads = 1.8f, DematInterruptSpeed = 5f;
        const float PrematDelay = 8.5f, PrematDelayFast = 1.9f, DematFailLock = 4f;
        static readonly float[] DematSequence = { 200f, 100f, 150f, 50f, 100f, 0f };
        static readonly float[] MatSequence = { 50f, 150f, 100f, 200f, 150f, 255f };
        static readonly float[] HadsDematSequence = { 100f, 200f, 0f };
        static readonly float[] Solidify = { 255f };

        // The vortex lasts this long unless the pilot lands sooner. Shorter for a fast return or HADS.
        const float MinFlight = 25f, MaxFlight = 40f, FlightSpeed = 60f, FastFlight = 3f;
        // Flown from a console that has the flight minigame, the vortex is long, so there is time to play it.
        public const float PilotedMin = 70f, PilotedMax = 120f, PilotedSpeed = 20f;
        const float DoorRange = 2.5f, LandingCeiling = 300f;
        public const float WaterLevel = -4.04f;   // MAP/LakeWaterColor
        const float MaxWander = 30f;   // metres off target when piloted with no stabilising at all

        public static Phase State { get; private set; }
        public static float Progress { get; private set; }   // 0..1 through the vortex and premat phases
        public static float Stability { get; set; } = 1f;
        public static string Heading { get; private set; }

        public static Transform Root { get { return root; } }
        static Transform root, doorHingeL, doorHingeR;
        // One pair of doors, the police box's, opening inwards. Behind the box's doorway is the room (through the doorway), so the
        // leaves swing into the room: copies of them hang in the room's doorway (BuildRoomLeaves) and swing with these. The box's own
        // leaves are drawn only while shut; ajar, what shows of them from outside is the copies, through the doorway.
        public const float DoorSwing = 90f;
        static Transform roomHingeL, roomHingeR;
        static readonly List<Renderer> leafRenderers = new List<Renderer>();
        static Light lamp;
        static Collider doorCollider, doorPlug;
        static Renderer blackInner;   // the black box behind the shut doors: out of the way while the doorway shows the room
        static bool solid = true, plugOn = true;
        static readonly List<Collider> colliders = new List<Collider>();
        static readonly List<Renderer> renderers = new List<Renderer>();
        class FadeMat { public Material M; public Shader Solid; public Color Colour; }
        static readonly List<FadeMat> fadeMats = new List<FadeMat>();
        static FadeMat topLight;   // the lamp on the roof: the beacon pulses it, as its light can't be seen by day
        static readonly List<MeshFilter> bodyMeshes = new List<MeshFilter>();
        static Shader fading;
        static AudioSource doorSound, travelSound;

        static float phaseLength, doorOpenUntil, doorAngle, alpha, sequenceSpeed, soundAt, failLockUntil;
        static float[] sequence;
        static int step;
        static AudioClip takeoffClip, landClip;
        static AudioSource flightSound;
        public const float RoughLanding = 0.3f;   // below this stability a piloted flight sounds the alarm and lands hard
        public static bool Piloted { get { return piloted; } }
        public static Kind FlightKind { get { return kind; } }
        static AudioClip doorOpenClip, doorCloseClip;
        static bool doorIsOpen, interrupted;
        static Kind kind;
        static Vector3 targetPos, lastPos, destination;
        static float targetYaw, lastYaw;
        static bool piloted, promptShown, moving, hasLast, beaconWasOff = true;

        public static Vector3 Position { get { return root.position; } }
        public static float Yaw { get { return root.eulerAngles.y; } }
        public static bool Busy { get { return moving || State != Phase.Idle || Flight.Airborne; } }

        // The doors are open or shut (SaveData.DoorHeldOpen: Use on either door, or the console's switch), and they also swing open for
        // a moment round a fade. Open fails a demat, slows a boost and stops the spin.
        public static bool DoorOpen { get { return MscTardis.Data.DoorHeldOpen || Time.time < doorOpenUntil; } }
        public const bool CopiesShut = false;
        // Open wide enough to walk through, with the box standing still.
        public static bool Passable { get { return MscTardis.Data.DoorHeldOpen && !Busy && doorAngle >= 0.7f * DoorSwing; } }
        // How far open the doors are, 0 to 1.
        public static float DoorFraction { get { return doorAngle / DoorSwing; } }
        // The doors swing open for a moment, as round a fade (the room's side asks for this when the player leaves by it).
        public static void SwingDoor() { doorOpenUntil = Mathf.Max(doorOpenUntil, Time.time + 1.3f); }

        public static bool HasDestination { get; private set; }
        public static Vector3 Destination { get { return destination; } }
        public static void SetDestination(Vector3 at, float yaw)
        {
            destination = at;
            HasDestination = true;
            if (State == Phase.Flight)
            {
                Vector3 spot;
                if (FindLanding(at + Vector3.up * 3f, out spot)) { targetPos = spot; targetYaw = yaw; Heading = "DESTINATION"; }
            }
        }

        public static void Build()
        {
            State = Phase.Idle;
            moving = false;
            promptShown = false;
            hasLast = false;
            HasDestination = false;
            colliders.Clear();
            renderers.Clear();
            fadeMats.Clear();
            topLight = null;
            bodyMeshes.Clear();

            BuildExterior();
            Flight.Build(root, colliders);
            Systems.Build(root.gameObject);
            TardisInterior.Build();
            Protocols.Build();

            if (!MscTardis.Data.Placed)
            {
                var yard = Places[0];
                if (!Stand(yard.Pos, yard.Yaw, yard.Pos.y + 6f)) MoveTo(yard.Pos, yard.Yaw);
                MscTardis.Data.Placed = true;
            }
            else
            {
                MoveTo(new Vector3(MscTardis.Data.X, MscTardis.Data.Y, MscTardis.Data.Z), MscTardis.Data.Yaw);
                SettleAfterFlight();   // saved in mid-air, or on something that has since moved
            }
            Pilot.Build();

            if (MscTardis.Data.Inside) TardisInterior.PlacePlayer();
        }

        // ---------------------------------------------------------------------------------------------------------
        // The exterior, from tools/assets/convert_source.py. Origin is the ground under the middle of the box, door along +Z.
        const float BoxHalf = 0.72f, BoxHeight = 3.1f;
        // The door opening, measured from the model: between the posts' inner faces at x +-0.517, from the base's top at y 0.22 to the
        // lintel at 2.42. The doorway's plane is just behind the shut leaves (their inner faces are at z 0.574; they swing in, which is
        // into the room: BuildRoomLeaves).
        const float SillHeight = 0.22f, DoorPlaneZ = 0.568f;

        static void BuildExterior()
        {
            root = new GameObject("MscTardisTardis").transform;
#pragma warning disable 618   // compiling a shader from source is the only way to add one without a Unity editor
            fading = new Material(FadeShader).shader;
#pragma warning restore 618
            var lit = Res.FindShader("Legacy Shaders/Diffuse");

            doorHingeL = doorHingeR = null;
            blackInner = null;
            leafRenderers.Clear();
            var leafL = new LeafExtent();
            var leafR = new LeafExtent();
            foreach (var line in Res.Lines("ext.txt"))
            {
                var f = line.Split(' ');
                if (f.Length < 7) continue;
                string part = f[1], mode = f[3];
                var pivot = new Vector3(G.F(f[4]), G.F(f[5]), G.F(f[6]));
                Transform parent = root;
                if (part == "door_l") { if (doorHingeL == null) doorHingeL = Hinge("DoorHingeL", pivot, root); parent = doorHingeL; }
                else if (part == "door_r") { if (doorHingeR == null) doorHingeR = Hinge("DoorHingeR", pivot, root); parent = doorHingeR; }

                Color colour;
                var mat = ExteriorMat(f[2], mode, lit, out colour);
                var partMesh = LeafMesh(f[0], part, mode);
                var go = Res.MeshObject(part, partMesh, mat, parent);
                if (part == "door_l") leafL.Add(partMesh.bounds);
                else if (part == "door_r") leafR.Add(partMesh.bounds);
                renderers.Add(go.GetComponent<Renderer>());
                if (parent != root) leafRenderers.Add(go.GetComponent<Renderer>());
                if (f[2] == "ext_black.png") blackInner = go.GetComponent<Renderer>();
                fadeMats.Add(new FadeMat { M = mat, Solid = mat.shader, Colour = colour });
                if (f[2] == "ext_tardis_df_toplight.png") topLight = fadeMats[fadeMats.Count - 1];
                if (part == "body") bodyMeshes.Add(go.GetComponent<MeshFilter>());
            }

            // The shell, hollow at the doorway: a floor at the sill, the roof, two sides and the back, and a plug that fills the doorway
            // while the doors are shut (TickDoorway). The way in is as wide and as deep as the walking room on the room's side.
            float top = BoxHeight + 0.02f, lintel = SillHeight + WorldPortal.Height, w = Doorway.WalkHalf, back = DoorPlaneZ - Doorway.WalkDepth;
            colliders.Add(Solid("FloorSolid", new Vector3(-BoxHalf, 0.02f, -BoxHalf), new Vector3(BoxHalf, SillHeight, BoxHalf)));
            colliders.Add(SillRamp());
            colliders.Add(Solid("RoofSolid", new Vector3(-BoxHalf, lintel, -BoxHalf), new Vector3(BoxHalf, top, BoxHalf)));
            colliders.Add(Solid("SideSolidL", new Vector3(-BoxHalf, SillHeight, -BoxHalf), new Vector3(-w, lintel, BoxHalf)));
            colliders.Add(Solid("SideSolidR", new Vector3(w, SillHeight, -BoxHalf), new Vector3(BoxHalf, lintel, BoxHalf)));
            colliders.Add(Solid("BackSolid", new Vector3(-w, SillHeight, -BoxHalf), new Vector3(w, lintel, back)));
            doorPlug = Solid("DoorSolid", new Vector3(-w, SillHeight, back), new Vector3(w, lintel, BoxHalf));
            // The box's own leaves are as solid as the room's copies (BuildRoomLeaves), open in the box as those are in the room, so the way
            // through is as wide on both sides: narrower outside, the player walking in by a post came through into a leaf and was pushed
            // off it, 8 cm sideways in a frame.
            var sill = root.TransformPoint(new Vector3(0f, SillHeight, DoorPlaneZ));
            if (leafL.Any) colliders.Add(LeafSolid(doorHingeL, leafL, sill));
            if (leafR.Any) colliders.Add(LeafSolid(doorHingeR, leafR, sill));
            solid = plugOn = true;
            Doorway.Build(root, new Vector3(0f, SillHeight, DoorPlaneZ));

            var lightGo = new GameObject("LampLight");
            lightGo.transform.SetParent(root, false);
            lightGo.transform.localPosition = new Vector3(0f, 3.1f, 0f);
            lamp = lightGo.AddComponent<Light>();
            lamp.type = LightType.Point;
            lamp.range = 6f;
            lamp.intensity = 0f;
            lamp.color = new Color(1f, 0.95f, 0.8f);

            var trigger = new GameObject("DoorTrigger");
            trigger.transform.SetParent(root, false);
            trigger.transform.localPosition = new Vector3(0f, 1.3f, BoxHalf + 0.04f);
            var bc = trigger.AddComponent<BoxCollider>();
            bc.size = new Vector3(1.2f, 2.3f, 0.1f);
            bc.isTrigger = true;
            doorCollider = bc;
            colliders.Add(bc);

            doorSound = Sfx.Source(root.gameObject, null, 0.8f, 25f);
            doorSound.spatialBlend = 0f;   // heard by the player on both sides of the fade, whichever room the source is in
            doorOpenClip = Sfx.BoxDoorOpen();
            doorCloseClip = Sfx.BoxDoorClose();
            doorIsOpen = false;
            travelSound = Sfx.Source(root.gameObject, Sfx.Travel(), 1f, 120f);
            flightSound = Sfx.Source(root.gameObject, Sfx.FlightLoop(), 0.7f, 60f, true);
        }

        // Lit by vertex, unlike the game's own transparent shaders, which aren't all in its build.
        const string FadeShader = @"Shader ""MscTardis/Fade"" {
            Properties { _MainTex (""Tex"", 2D) = ""white"" {} _Color (""Color"", Color) = (1,1,1,1) }
            SubShader {
                Tags { ""Queue"" = ""Transparent"" ""IgnoreProjector"" = ""True"" ""RenderType"" = ""Transparent"" }
                Blend SrcAlpha OneMinusSrcAlpha
                Pass {
                    Material { Diffuse (1,1,1,1) Ambient (1,1,1,1) }
                    Lighting On
                    SetTexture [_MainTex] { constantColor [_Color] combine texture * primary DOUBLE, constant }
                }
            }
        }";

        // An unseen ramp up to the sill, in front of the box. Up the 22 cm step the game's walk popped the view up 20 cm in two frames,
        // then lost half its pace over the last 30 cm before the doorway (the step through came at 1.2 m/s, picking up again in the
        // room). On the layer that only the player collides with, so the box's own physics (landing, cars) never meets it.
        const float RampRun = 0.7f;
        const int PlayerOnlyLayer = 23;   // "PlayerOnlyColl"
        static Collider SillRamp()
        {
            var go = new GameObject("SillRamp");
            go.transform.SetParent(root, false);
            go.layer = PlayerOnlyLayer;
            float a = Mathf.Atan2(SillHeight, RampRun), thick = 0.1f, length = RampRun / Mathf.Cos(a) + 0.4f;
            var up = new Vector3(0f, Mathf.Cos(a), Mathf.Sin(a));      // the ramp's face, tilted towards +z (out of the door)
            var down = new Vector3(0f, -Mathf.Sin(a), Mathf.Cos(a));   // along it, away from the box and down
            var top = new Vector3(0f, SillHeight, BoxHalf);              // its top edge, level with the sill at the box's front
            go.transform.localRotation = Quaternion.Euler(a * Mathf.Rad2Deg, 0f, 0f);
            go.transform.localPosition = top + down * (length / 2f) - up * (thick / 2f);   // runs on under the ground past the foot
            var bc = go.AddComponent<BoxCollider>();
            bc.size = new Vector3(BoxHalf * 2f, thick, length);
            return bc;
        }

        // A box collider on the exterior, by its two corners in the box's own space.
        static Collider Solid(string name, Vector3 min, Vector3 max)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root, false);
            var bc = go.AddComponent<BoxCollider>();
            bc.center = (min + max) / 2f;
            bc.size = max - min;
            return bc;
        }

        static Transform Hinge(string name, Vector3 at, Transform parent)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            t.localPosition = at;
            return t;
        }

        // An exterior part's material: lit by the scene, or (glow) the texture as it is.
        static Material ExteriorMat(string texture, string mode, Shader lit, out Color colour)
        {
            if (mode == "glow")
            {
                colour = new Color(0.5f, 0.5f, 0.5f, 1f);   // the room shader doubles its texture
                return Res.RoomMat(texture, Res.RoomMode.Baked, colour);
            }
            colour = Color.white;
            return new Material(lit) { mainTexture = Res.Tex(texture, false, TextureWrapMode.Repeat) };
        }

        // The police box's leaves again, in the room's doorway. frame is the room's doorway (x across, y up from its floor, z into the
        // room); the box's own doorway is the same with the box's z out of it. Across the doorway one maps to the other turned half
        // round, so the leaves sit where the box's would be if the room were behind its door, and swinging in, they swing into the room.
        // Their own materials: the box's fade as it dematerialises, and these stay.
        public static void BuildRoomLeaves(Transform frame)
        {
            var t = new GameObject("PoliceBoxLeaves").transform;
            t.SetParent(frame, false);
            t.localRotation = Quaternion.Euler(0f, 180f, 0f);
            t.localPosition = new Vector3(0f, -SillHeight, DoorPlaneZ);
            roomHingeL = roomHingeR = null;
            foreach (var old in litLeaves) if (old.Mesh != null) UnityEngine.Object.Destroy(old.Mesh);
            litLeaves.Clear();
            litAngle = -1f;
#pragma warning disable 618
            if (leafGlow == null) leafGlow = new Material(LeafGlowShader).shader;
            if (leafLit == null) leafLit = new Material(LeafLitShader).shader;
#pragma warning restore 618
            var leafL = new LeafExtent();
            var leafR = new LeafExtent();
            foreach (var line in Res.Lines("ext.txt"))
            {
                var f = line.Split(' ');
                if (f.Length < 7 || (f[1] != "door_l" && f[1] != "door_r")) continue;
                var pivot = new Vector3(G.F(f[4]), G.F(f[5]), G.F(f[6]));
                bool left = f[1] == "door_l";
                Transform hinge;
                if (left) { if (roomHingeL == null) roomHingeL = Hinge("DoorHingeL", pivot, t); hinge = roomHingeL; }
                else { if (roomHingeR == null) roomHingeR = Hinge("DoorHingeR", pivot, t); hinge = roomHingeR; }
                var mesh = LeafMesh(f[0], f[1], f[3]);   // the leaves' meshes are in their hinge's space
                (left ? leafL : leafR).Add(mesh.bounds);
                if (f[3] == "glow")
                {
                    Color colour;
                    var glow = ExteriorMat(f[2], f[3], null, out colour);
                    glow.shader = leafGlow;
                    Res.MeshObject(f[1], mesh, glow, hinge);
                    continue;
                }
                var mat = new Material(leafLit) { mainTexture = Res.Tex(f[2], false, TextureWrapMode.Repeat) };
                Res.MeshObject(f[1], LitCopy(mesh, pivot, left), mat, hinge);
                if (left) Res.MeshObject("NoticeBack", LitCopy(NoticeBack(), pivot, true), mat, hinge);
            }
            // Open, the leaves stand in the room beside the doorway, and the player walks into them, not through them. (Shut, the
            // doorway's plug stops the player; and a door won't shut on someone standing where they swing: Doorway.PlayerInDoorway.)
            if (leafL.Any) LeafSolid(roomHingeL, leafL, frame.position);
            if (leafR.Any) LeafSolid(roomHingeR, leafR, frame.position);
            SwingLeaves();
            LightLeaves();
        }

        // Down to the floor: the leaf itself stops a hand above it, and the player's body (a capsule half a metre tall, stepping up 0.4)
        // took that edge for a step and climbed the leaf.
        // As thick as the leaf's panel only: the notice and the handle stand off it, and counted in they made an invisible edge that the
        // player walking in by the post caught on.
        // A leaf's parts, in its hinge's space: all of them, and the biggest (its panel).
        sealed class LeafExtent
        {
            public Bounds All, Panel;
            public bool Any;
            public void Add(Bounds b)
            {
                if (!Any || b.size.x * b.size.y > Panel.size.x * Panel.size.y) Panel = b;
                if (Any) All.Encapsulate(b); else All = b;
                Any = true;
            }
        }

        static Collider LeafSolid(Transform hinge, LeafExtent e, Vector3 floorAt)
        {
            if (hinge == null) return null;
            var bc = new GameObject("LeafSolid").AddComponent<BoxCollider>();
            bc.transform.SetParent(hinge, false);
            float floor = hinge.InverseTransformPoint(floorAt).y - 0.05f, top = e.All.max.y;   // the hinge turns about the vertical only
            bc.center = new Vector3(e.All.center.x, (floor + top) / 2f, e.Panel.center.z);
            bc.size = new Vector3(Mathf.Max(e.All.size.x, 0.04f), top - floor, Mathf.Max(e.Panel.size.z, 0.04f));
            return bc;
        }

        // ---------------------------------------------------------------------------------------------------------
        // The room's leaves light themselves, as the box's own would be lit where it stands at that angle. With the game's lights they
        // came out otherwise: the room's (the rotor's) reached them and not the box's, the portal's camera (forward) and the game's
        // (deferred) take the sun and the ambient differently, and the box's sign overhanging the doors shades them outside, which nothing
        // in the room does. So shut, they would change as they closed and the box's own took their place. Per vertex: the game's flat
        // ambient and its sun by the angle (Lambert, as Legacy Diffuse), the sun only where the box itself or what stands round it isn't
        // in its way, into an unlit shader that doubles them.
        sealed class LitLeaf { public Mesh Mesh; public Vector3[] V, N; public Color32[] C; public Vector3 Pivot; public bool Left; }
        static readonly List<LitLeaf> litLeaves = new List<LitLeaf>();
        static Light sun;
        static float litAngle = -1f;
        static Vector3 litSun;
        static Color litLight, litAmbient;
        // The shadow the game casts on the doors (a building's, its eaves', a tree's: anything it draws, with a collider or not), as a grid
        // over the shut leaves: what of the sun reaches each point, 1 in full sun, 1 - the sun's shadow strength in full shadow. A shadow's
        // edge then falls across the copies where it falls on the box's own leaves. (One ray from the doors' middle shaded them all or none,
        // and rays miss the eaves and leaves that have no collider: either way, opening the doors changed how they looked.) Read by a
        // camera of the mod's own (Probe), every half second while the box is near or the player is inside.
        const int EnvCols = 18, EnvRows = 34;   // cells about 6 cm square
        static readonly float[,] envLit = NewEnv();
        static int envVersion, litEnvVersion = -1;
        static bool envWasOpen;
        static float envSeenAt = -10f;
        static float[,] NewEnv() { var a = new float[EnvCols, EnvRows]; for (int i = 0; i < EnvCols; i++) for (int j = 0; j < EnvRows; j++) a[i, j] = 1f; return a; }
        // Over the doors, the sign box stands out to here (the roof's tiers above it are further back); the posts beside the doors,
        // behind them. Measured from the model.
        const float SignFront = 0.73f, PostFront = 0.52f, BoxOuter = 0.76f, BoxTop = 3.26f;

        static Mesh LitCopy(Mesh src, Vector3 pivot, bool left)
        {
            var m = (Mesh)UnityEngine.Object.Instantiate(src);
            m.name = src.name + "_lit";
            if (m.normals == null || m.normals.Length != m.vertexCount) m.RecalculateNormals();
            litLeaves.Add(new LitLeaf { Mesh = m, V = m.vertices, N = m.normals, C = new Color32[m.vertexCount], Pivot = pivot, Left = left });
            return m;
        }

        static void LightLeaves()
        {
            if (litLeaves.Count == 0 || root == null) return;
            if (sun == null) sun = FindSun();
            bool up = sun != null && sun.enabled && sun.gameObject.activeInHierarchy;
            var l = up ? root.InverseTransformDirection(-sun.transform.forward) : Vector3.up;   // towards the sun, in the box's space
            var light = up ? sun.color * sun.intensity : Color.black;
            var ambient = RenderSettings.ambientLight * RenderSettings.ambientIntensity;
            float shade = up && sun.shadows != LightShadows.None ? 1f - sun.shadowStrength : 1f;   // what of the sun is left in a shadow
            bool envUsed = up && shade < 1f, open = doorAngle > 0f || CopiesShut;   // shut, the copies aren't seen
            if (envUsed && open) RefreshEnv(l, !envWasOpen);   // fresh the frame they start to open, the frame the copies are first shown
            envWasOpen = open;

            if (Mathf.Abs(doorAngle - litAngle) < 0.01f && (l - litSun).sqrMagnitude < 1e-6f && envVersion == litEnvVersion && Same(light, litLight) && Same(ambient, litAmbient)) return;
            litAngle = doorAngle; litSun = l; litLight = light; litAmbient = ambient; litEnvVersion = envVersion;

            bool dark = light.r + light.g + light.b <= 0f;
            foreach (var leaf in litLeaves)
            {
                var rot = Quaternion.Euler(0f, leaf.Left ? -doorAngle : doorAngle, 0f);   // as SwingLeaves turns the box's own
                for (int i = 0; i < leaf.V.Length; i++)
                {
                    float ndl = dark ? 0f : Vector3.Dot(rot * leaf.N[i], l);
                    if (ndl > 0f)
                    {
                        var p = leaf.Pivot + rot * leaf.V[i];
                        if (BoxShades(p, l)) ndl *= shade;
                        else if (envUsed) ndl *= EnvLitAt(p, l);
                    }
                    var c = (ambient + light * Mathf.Max(0f, ndl)) * 0.5f;
                    leaf.C[i] = new Color32((byte)(Mathf.Clamp01(c.r) * 255f + 0.5f), (byte)(Mathf.Clamp01(c.g) * 255f + 0.5f), (byte)(Mathf.Clamp01(c.b) * 255f + 0.5f), 255);
                }
                leaf.Mesh.colors32 = leaf.C;
            }
        }

        static bool Same(Color a, Color b) { return Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) < 0.002f; }

        static Light FindSun()
        {
            Light best = null;
            foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>())
                if (l.type == LightType.Directional && (best == null || l.intensity > best.intensity)) best = l;
            return best;
        }

        // Whether the box stands between a point of a leaf (the box's space) and the sun. Behind the door plane, in the box, light comes
        // in by the opening only; in front of it, the sign box over the doors overhangs them, and the posts stand beside them.
        static bool BoxShades(Vector3 p, Vector3 l)
        {
            const float half = WorldPortal.Width / 2f;
            float lintel = SillHeight + WorldPortal.Height;
            if (p.z < DoorPlaneZ)
            {
                if (l.z <= 1e-4f) return true;
                p += l * ((DoorPlaneZ - p.z) / l.z);
                if (Mathf.Abs(p.x) > half || p.y > lintel || p.y < SillHeight) return true;
            }
            p += l * 0.002f;
            return RayHits(p, l, new Vector3(-BoxOuter, lintel, -BoxOuter), new Vector3(BoxOuter, BoxTop, SignFront))
                || RayHits(p, l, new Vector3(-BoxOuter, 0f, -BoxOuter), new Vector3(-half, lintel, PostFront))
                || RayHits(p, l, new Vector3(half, 0f, -BoxOuter), new Vector3(BoxOuter, lintel, PostFront));
        }

        // Inside the police box, clear of its walls, roof and floor (Doorway.NearInBox).
        public static bool InBox(Vector3 world)
        {
            if (root == null) return false;
            var p = root.InverseTransformPoint(world);
            const float wall = 0.68f;
            return Mathf.Abs(p.x) < wall && p.z > -wall && p.z < DoorPlaneZ + 0.01f && p.y > SillHeight && p.y < 2.9f;
        }

        // A ray from p along d meets the box min..max (or starts in it).
        static bool RayHits(Vector3 p, Vector3 d, Vector3 min, Vector3 max)
        {
            float t0 = 0f, t1 = float.MaxValue;
            for (int a = 0; a < 3; a++)
            {
                if (Mathf.Abs(d[a]) < 1e-6f) { if (p[a] < min[a] || p[a] > max[a]) return false; continue; }
                float u = (min[a] - p[a]) / d[a], v = (max[a] - p[a]) / d[a];
                if (u > v) { var s = u; u = v; v = s; }
                t0 = Mathf.Max(t0, u);
                t1 = Mathf.Min(t1, v);
                if (t0 > t1) return false;
            }
            return true;
        }

        // The probe: a grey board just in front of the shut leaves, as wide as the leaves and as tall as the doorway, a cell to each grid
        // point, and over it two patches: one that takes no shadow (the sun at full) and one on a layer the sun doesn't light (the ambient
        // alone). Every part faces the sun, so the ambient is the same on each. A camera of its own draws them square on, with the game's
        // lights and shadows, into a texture of a pixel a cell: where a cell's light falls between the two patches' is the game's own
        // shadow there, 1 in sun and 1 - the sun's shadow strength in full shadow. The probe is switched on, and the box's own renderers
        // off, only for that picture: the box would be drawn over the board, and its shadows are BoxShades'. So the grid holds only what
        // stands round the box: a building, its eaves, a tree, collider or not.
        const float EnvHalf = 0.56f;   // the grid's half width: the leaves from hinge to hinge
        const float BoardZ = 0.66f;    // in front of the shut leaves' faces
        const int ProbeRows = EnvRows + 2;
        static Camera probeCam;
        static RenderTexture probeRT;
        static Texture2D probeTex;
        static Mesh[] probeMeshes;
        static readonly Vector3[] probeNormals = new Vector3[4];
        static Renderer[] probeParts;
        static readonly List<Renderer> boxHidden = new List<Renderer>();
        static int ambientLayer = -1;
        static bool probeFailed;
        static float CellW { get { return 2f * EnvHalf / EnvCols; } }
        static float CellH { get { return WorldPortal.Height / EnvRows; } }
        static float ProbeHeight { get { return WorldPortal.Height + 2f * CellH; } }
        static float ProbeAspect { get { return 2f * EnvHalf / ProbeHeight; } }   // the board and the patches, filling the picture

        static void BuildProbe()
        {
            var shader = Shader.Find("Legacy Shaders/Diffuse") ?? Shader.Find("Diffuse");
            if (sun != null)
                for (int k = 8; k < 32 && ambientLayer < 0; k++)
                    if ((sun.cullingMask & (1 << k)) == 0) ambientLayer = k;
            if (shader == null || ambientLayer < 0)
            {
                probeFailed = true;
                Debug.Log("MSC-TARDIS: door shadow probe off (" + (shader == null ? "no Diffuse shader" : "the sun lights every layer") + "): the leaves' copies take no shadow from round the box");
                return;
            }

            var go = new GameObject("DoorShadowProbe");
            go.transform.SetParent(root, false);
            var mat = new Material(shader) { color = new Color(0.2f, 0.2f, 0.2f) };   // grey, so the sun at noon can't clip it to white
            float lintel = SillHeight + WorldPortal.Height, top = lintel + 2f * CellH;
            var board = ProbePart(go.transform, "Board", -EnvHalf, EnvHalf, SillHeight, lintel, true, mat);
            var lit = ProbePart(go.transform, "Sunlit", 0.02f, EnvHalf, lintel, top, false, mat);
            var amb = ProbePart(go.transform, "Ambient", -EnvHalf, -0.02f, lintel, top, false, mat);
            amb.gameObject.layer = ambientLayer;
            probeParts = new[] { board, lit, amb };
            probeMeshes = new[] { board.GetComponent<MeshFilter>().sharedMesh, lit.GetComponent<MeshFilter>().sharedMesh, amb.GetComponent<MeshFilter>().sharedMesh };

            const float D = 1f;   // the camera's distance from the board
            var cg = new GameObject("Camera");
            cg.transform.SetParent(go.transform, false);
            cg.transform.localPosition = new Vector3(0f, SillHeight + ProbeHeight / 2f, BoardZ + D);
            cg.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);   // looking at the doors
            probeCam = cg.AddComponent<Camera>();
            probeCam.enabled = false;
            probeCam.fieldOfView = 2f * Mathf.Atan(ProbeHeight / 2f / D) * Mathf.Rad2Deg;
            probeCam.nearClipPlane = D - 0.05f;
            probeCam.farClipPlane = D + 2f;   // the sun's shadows fade out towards the shadow distance, which is the far plane at most
                                              // (the board, filling the picture, hides what is behind it)
            probeCam.clearFlags = CameraClearFlags.SolidColor;
            probeCam.backgroundColor = Color.black;
            probeCam.renderingPath = RenderingPath.Forward;
            probeCam.hdr = false;
            probeCam.useOcclusionCulling = false;
            probeRT = new RenderTexture(EnvCols, ProbeRows, 24, RenderTextureFormat.ARGB32) { filterMode = FilterMode.Point };
            probeCam.targetTexture = probeRT;
            probeCam.aspect = ProbeAspect;
            probeTex = new Texture2D(EnvCols, ProbeRows, TextureFormat.RGB24, false);
        }

        // A quad in the box's space at the board's depth, facing +z (the camera).
        static Renderer ProbePart(Transform parent, string name, float x0, float x1, float y0, float y1, bool shadows, Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var m = new Mesh { name = "Probe" + name };
            m.MarkDynamic();
            m.vertices = new[] { new Vector3(x0, y0, BoardZ), new Vector3(x1, y0, BoardZ), new Vector3(x1, y1, BoardZ), new Vector3(x0, y1, BoardZ) };
            m.normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward };
            m.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
            m.triangles = new[] { 0, 1, 3, 1, 2, 3 };
            m.RecalculateBounds();
            go.AddComponent<MeshFilter>().sharedMesh = m;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.receiveShadows = shadows;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.enabled = false;
            return r;
        }

        // While the doors are open, every half second (and at once when they start to open) if the box is near or the player inside:
        // the probe's picture, read back into the grid. l is towards the sun, in the box's space. About 2-3 ms each (`portal light`).
        static void RefreshEnv(Vector3 l, bool now)
        {
            if (probeFailed || root == null) return;
            bool near = MscTardis.Data.Inside || (G.Cam != null && (G.Cam.transform.position - root.position).sqrMagnitude < 90f * 90f);
            if (!near || (!now && Time.time - envSeenAt < 0.5f)) return;
            envSeenAt = Time.time;
            if (probeCam == null) BuildProbe();
            if (probeCam == null) return;

            for (int k = 0; k < 4; k++) probeNormals[k] = l;
            foreach (var m in probeMeshes) m.normals = probeNormals;
            probeCam.cullingMask = (G.Cam != null ? G.Cam.cullingMask : -1) | (1 << ambientLayer);   // the game's own casters, and the ambient patch
            RenderProbe();
            var prev = RenderTexture.active;
            RenderTexture.active = probeRT;
            probeTex.ReadPixels(new Rect(0, 0, EnvCols, ProbeRows), 0, 0);
            RenderTexture.active = prev;

            // Seen from in front, the picture's left is the box's +x: the sunlit patch is the left half of the top two rows, the ambient one
            // the right half, less the two columns at the middle, where they meet.
            float lit = 0f, amb = 0f;
            int n = 0;
            for (int c = 0; c < EnvCols / 2 - 1; c++)
                for (int r = EnvRows; r < ProbeRows; r++, n++) { lit += Lum(probeTex.GetPixel(c, r)); amb += Lum(probeTex.GetPixel(EnvCols - 1 - c, r)); }
            lit /= n; amb /= n;
            float span = lit - amb;
            bool changed = false;
            for (int j = 0; j < EnvRows; j++)
                for (int c = 0; c < EnvCols; c++)
                {
                    int i = EnvCols - 1 - c;
                    float v = span > 0.02f ? Mathf.Clamp01((Lum(probeTex.GetPixel(c, j)) - amb) / span) : 1f;
                    if (Mathf.Abs(v - envLit[i, j]) > 0.03f) { envLit[i, j] = v; changed = true; }
                }
            if (changed) envVersion++;
        }

        // The probe's picture: the probe on and the box's own renderers off (they would be drawn over the board, and their shadows are
        // BoxShades'), for this one render; the game's camera draws later in the frame and sees them as they were. Gathered each time,
        // so nothing added to the box since is missed (the probe's own parts are off as they're gathered, so they aren't among them).
        static void RenderProbe()
        {
            boxHidden.Clear();
            foreach (var r in root.GetComponentsInChildren<Renderer>())
                if (r.enabled) { r.enabled = false; boxHidden.Add(r); }
            foreach (var r in probeParts) r.enabled = true;
            try { probeCam.Render(); }
            finally
            {
                foreach (var r in probeParts) r.enabled = false;
                foreach (var r in boxHidden) if (r != null) r.enabled = true;
                boxHidden.Clear();
            }
        }

        static float Lum(Color c) { return 0.299f * c.r + 0.587f * c.g + 0.114f * c.b; }


        // What of the sun reaches a leaf's point p (box space, l towards the sun) past what stands round the box: the grid where the sun's
        // ray to p crosses the board's plane, blended between its points so a shadow's edge is soft, as the game's are.
        static float EnvLitAt(Vector3 p, Vector3 l)
        {
            if (Mathf.Abs(l.z) > 0.05f) p += l * ((BoardZ - p.z) / l.z);
            float u = Mathf.Clamp((p.x + EnvHalf) / CellW - 0.5f, 0f, EnvCols - 1);
            float v = Mathf.Clamp((p.y - SillHeight) / CellH - 0.5f, 0f, EnvRows - 1);
            int i = Mathf.Min((int)u, EnvCols - 2), j = Mathf.Min((int)v, EnvRows - 2);
            float fu = u - i, fv = v - j;
            return Mathf.Lerp(Mathf.Lerp(envLit[i, j], envLit[i + 1, j], fu), Mathf.Lerp(envLit[i, j + 1], envLit[i + 1, j + 1], fu), fv);
        }




        // The left leaf is open behind its notice (the notice's box filled it front to back). From inside, a plate of the leaf's own
        // inner blue covers that, level with its inner panels, just behind the notice's face. Hinge space; facing into the box.
        static Mesh NoticeBack()
        {
            const float x0 = -0.412f, x1 = -0.127f, y0 = 0.636f, y1 = 0.955f, z = 0.015f;
            const float u0 = 0.045f, u1 = 0.085f, v0 = 0.64f, v1 = 0.70f;   // a plain stretch of the inner panels' blue in ext_tardis_df.png
            var m = new Mesh { name = "NoticeBack" };
            m.vertices = new[] { new Vector3(x0, y0, z), new Vector3(x0, y1, z), new Vector3(x1, y1, z), new Vector3(x1, y0, z) };
            m.uv = new[] { new Vector2(u0, v0), new Vector2(u0, v1), new Vector2(u1, v1), new Vector2(u1, v0) };
            m.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
            m.triangles = new[] { 0, 1, 2, 0, 2, 3 };   // clockwise seen from -z (x right, y up), Unity's front
            m.RecalculateBounds();
            return m;
        }

        // The room's own unlit shader (Res, Baked) seen from the front only. The leaves' windows are two panes, one facing each way, but
        // the notice is one face, and from inside, through the leaf, it would read backwards. The lit leaf has a face of its own level with
        // the notice: in the game's camera (deferred) this, drawn after it, wins; in the portal's (forward) the order is by distance, and
        // the notice came out dark. Drawn after the leaf and pulled a hair nearer, it wins in both.
        static Shader leafGlow;
        const string LeafGlowShader = @"Shader ""MscTardis/LeafGlow"" {
            Properties { _MainTex (""Tex"", 2D) = ""white"" {} _Color (""Color"", Color) = (1,1,1,1) }
            SubShader {
                Tags { ""Queue"" = ""Geometry+1"" ""RenderType"" = ""Opaque"" }
                Lighting Off Cull Back Fog { Mode Off } Offset -1, -1
                Pass {
                    BindChannels { Bind ""Vertex"", vertex Bind ""texcoord"", texcoord Bind ""Color"", color }
                    SetTexture [_MainTex] { combine texture * primary DOUBLE }
                    SetTexture [_MainTex] { constantColor [_Color] combine previous * constant, previous }
                }
            }
        }";

        // The lit leaves' copies: the texture by the light LightLeaves works out per vertex (halved, to reach twice white).
        static Shader leafLit;
        const string LeafLitShader = @"Shader ""MscTardis/LeafLit"" {
            Properties { _MainTex (""Tex"", 2D) = ""white"" {} }
            SubShader {
                Tags { ""Queue"" = ""Geometry"" ""RenderType"" = ""Opaque"" }
                Lighting Off Cull Back Fog { Mode Off }
                Pass {
                    BindChannels { Bind ""Vertex"", vertex Bind ""texcoord"", texcoord Bind ""Color"", color }
                    SetTexture [_MainTex] { combine texture * primary DOUBLE }
                }
            }
        }";

        // The notice on the left leaf is a box 13 cm deep with the notice on every face, which pushes 10 cm out through the inside of
        // the leaf (the black box behind the doors hid that). Only its front stays; the windows are nearer the leaf's middle.
        // Hinge space, z -0.97 to -0.01: a triangle goes if its middle is in it. The notice's sides have middles at -0.027 or -0.071 and its
        // back -0.116; its front is at 0.018, the left leaf's windows at 0.003 and the right's at -0.008.
        static readonly Bounds BehindLeaf = new Bounds(new Vector3(0f, 0f, -0.49f), new Vector3(4f, 8f, 0.96f));
        static Mesh LeafMesh(string file, string part, string mode)
        {
            var mesh = Res.Dwm(file);
            return (part == "door_l" || part == "door_r") && mode == "glow" ? Res.Cut(mesh, BehindLeaf, false) : mesh;
        }

        // Both pairs to the doors' angle. Inwards: the left leaf (hinged at +x) turns its free edge towards -z, into the box.
        static void SwingLeaves()
        {
            var l = Quaternion.Euler(0f, -doorAngle, 0f);
            var r = Quaternion.Euler(0f, doorAngle, 0f);
            if (doorHingeL != null) doorHingeL.localRotation = l;
            if (doorHingeR != null) doorHingeR.localRotation = r;
            if (roomHingeL != null) roomHingeL.localRotation = l;
            if (roomHingeR != null) roomHingeR.localRotation = r;
        }

        // A see-through blue copy of the shell, for destination mode.
        public static Transform BuildGhost()
        {
            var t = new GameObject("MscTardisGhost").transform;
            var mat = new Material(fading) { color = new Color(0.4f, 0.75f, 1f, 0.35f) };
            mat.mainTexture = Res.Tex("white.png", false, TextureWrapMode.Repeat);
            foreach (var mf in bodyMeshes) Res.MeshObject("Ghost", mf.sharedMesh, mat, t);
            return t;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Placing it.

        static void MoveTo(Vector3 pos, float yaw)
        {
            root.position = pos;
            root.rotation = Quaternion.Euler(0f, yaw, 0f);
            MscTardis.Data.X = pos.x;
            MscTardis.Data.Y = pos.y;
            MscTardis.Data.Z = pos.z;
            MscTardis.Data.Yaw = yaw;
        }

        // Stand on the ground under pos. False (and no move) if there is no ground there.
        static bool Stand(Vector3 pos, float yaw, float fromHeight)
        {
            float y;
            if (!Ground(pos.x, pos.z, fromHeight, out y)) return false;
            MoveTo(new Vector3(pos.x, y, pos.z), yaw);
            return true;
        }

        // The first solid thing under a point, ignoring triggers, the player and the TARDIS itself. The lake bed is
        // solid and well under water, so over the lake this is the surface: the box floats, and its door isn't 5 m down.
        // staticOnly also skips things that move (cars, items), so the ground found is the real ground, not a roof or a bonnet.
        public static bool Ground(float x, float z, float fromHeight, out float y, bool staticOnly = false)
        {
            var hits = Physics.RaycastAll(new Vector3(x, fromHeight, z), Vector3.down, fromHeight + 1000f, ~0);
            System.Array.Sort(hits, (p, q) => p.distance.CompareTo(q.distance));
            foreach (var h in hits)
            {
                if (h.collider.isTrigger) continue;
                var t = h.collider.transform;
                if (t.IsChildOf(root) || t.IsChildOf(G.Player) || t.IsChildOf(TardisInterior.Root)) continue;
                if (staticOnly && h.collider.attachedRigidbody != null && !h.collider.attachedRigidbody.isKinematic) continue;
                y = Mathf.Max(h.point.y, WaterLevel);
                return true;
            }
            y = 0f;
            return false;
        }

        const float FitHalf = 0.8f, FitHeight = 3.3f, RoofRise = 1.5f;   // RoofRise: how far above a preset's hint a landing spot may be   // the box footprint (half width) and height it needs clear

        // Room for the box at (x, y, z): nothing solid inside it, and level ground under all four corners.
        static bool Fits(float x, float y, float z)
        {
            for (int i = -1; i <= 1; i++)
                for (int j = -1; j <= 1; j++)
                {
                    float px = x + i * FitHalf, pz = z + j * FitHalf;
                    var hits = Physics.RaycastAll(new Vector3(px, y + 0.3f, pz), Vector3.up, FitHeight, ~0);
                    foreach (var h in hits)
                    {
                        if (h.collider.isTrigger) continue;
                        var t = h.collider.transform;
                        if (t.IsChildOf(root) || t.IsChildOf(G.Player) || t.IsChildOf(TardisInterior.Root)) continue;
                        return false;
                    }
                    if (i != 0 && j != 0)
                    {
                        float gy;
                        if (!Ground(px, pz, y + 1f, out gy, true) || Mathf.Abs(gy - y) > 0.5f) return false;
                    }
                }
            return true;
        }

        // The static ground under a point, looking down from the preset's hint height. A hint that is lower than the real ground (a place's y
        // is a scene object's pivot, which can sit under the terrain) starts the ray under the surface and finds nothing, so then look
        // again from the ceiling.
        static bool GroundNear(float x, float z, float from, out float y)
        {
            if (Ground(x, z, from, out y, true)) return true;
            return from < LandingCeiling && Ground(x, z, LandingCeiling, out y, true);
        }

        // The nearest spot to target that is real ground the box fits on: the target itself, else outwards in rings.
        // A preset's y is a hint a little above the spot, so roofs and trees high over it don't count.
        static bool FindLanding(Vector3 target, out Vector3 spot)
        {
            bool hinted = target.y < LandingCeiling;
            float from = hinted ? target.y + 6f : LandingCeiling;
            // Near a hint the ground is at about its height: a flat top well above it is a roof or a car, not the place. (The yard's own
            // spot doesn't fit, so its rings are searched every time, and a little landing drift reached the house's flat roof.) A hint
            // under the terrain has nothing that low near it, so then the search runs again without the limit.
            if (hinted && Search(target, from, target.y + RoofRise, out spot)) return true;
            if (Search(target, from, float.MaxValue, out spot)) return true;
            float y;
            bool any = GroundNear(target.x, target.z, from, out y);
            spot = new Vector3(target.x, y, target.z);
            return any;   // nothing clear nearby: the plain ground under the target, as before
        }

        // The target, then rings out to 24 m: the first spot with ground no higher than maxY that the box fits on.
        static bool Search(Vector3 target, float from, float maxY, out Vector3 spot)
        {
            float y;
            spot = target;
            if (GroundNear(target.x, target.z, from, out y) && y < maxY && Fits(target.x, y, target.z)) { spot = new Vector3(target.x, y, target.z); return true; }
            for (float r = 2f; r <= 24f; r += 2f)
                for (int k = 0; k < 8; k++)
                {
                    float a = k * Mathf.PI / 4f;
                    float x = target.x + Mathf.Cos(a) * r, z = target.z + Mathf.Sin(a) * r;
                    if (GroundNear(x, z, from, out y) && y < maxY && Fits(x, y, z)) { spot = new Vector3(x, y, z); return true; }
                }
            return false;
        }


        // ---------------------------------------------------------------------------------------------------------
        // Travel core: the only way the TARDIS moves in play. Returns null when under way, else why not.





        public static string TravelToPlace(Place p, bool byPilot)
        {
            return Travel(p.Pos, p.Yaw, p.Name, byPilot);
        }

        public static string TravelToCoords(float x, float z, bool byPilot = false)
        {
            return Travel(new Vector3(x, LandingCeiling, z), root.eulerAngles.y, "COORDINATES " + x.ToString("0") + " " + z.ToString("0"), byPilot);
        }

        public static string FastReturn()
        {
            if (!hasLast) return "NOWHERE TO RETURN TO";
            return Travel(lastPos, lastYaw, "FAST RETURN", true, Kind.Fast);
        }

        // Phase A, the checks: refused outright if already moving or without power; failed, at a small cost, if the handbrake
        // is on, the door is open or there isn't the fuel. HADS and a broken hull are forced past the failures.
        public static string Travel(Vector3 target, float yaw, string label, bool byPilot, Kind how = Kind.Normal)
        {
            var d = MscTardis.Data;
            if (State != Phase.Idle || moving) return "already in flight";
            if (!d.Power) return "NO POWER";
            if (Time.time < failLockUntil) return "THE TARDIS IS RECOVERING";
            if (Protocols.SiegeOn && how != Kind.Hads) return "SIEGE MODE: NO TAKE-OFF";
            if (how != Kind.Hads && !Subsystems.Up(Subsystems.Dematerialisation)) return "DEMATERIALISATION OFFLINE";
            bool forced = how == Kind.Hads || Systems.Broken;
            float cost = how == Kind.Hads ? Systems.CostHads : how == Kind.Fast ? Systems.CostFastReturn : Systems.CostDemat;
            if (Subsystems.Factor(Subsystems.Dematerialisation) < 1f) cost *= 1.5f;   // damaged: half the effect, a half more fuel
            if (!forced)
            {
                string fail = d.Handbrake ? "HANDBRAKE IS ON" : DoorOpen ? "THE DOORS ARE OPEN" : d.Artron < cost ? "NOT ENOUGH ARTRON ENERGY" : null;
                if (fail != null) { FailDemat(); return fail; }
            }
            Vector3 spot;
            if (!FindLanding(target, out spot)) return "NO GROUND AT THAT LOCATION";
            target = spot;
            var flat = target - root.position;
            flat.y = 0f;
            if (flat.magnitude < 2f) return "ALREADY THERE";

            // Phase A proper. The box stops being solid as soon as it starts going.
            Flight.Freeze();
            root.rotation = Quaternion.Euler(0f, root.eulerAngles.y, 0f);
            if (how != Kind.Fast) { lastPos = root.position; lastYaw = Yaw; hasLast = true; }
            MscTardis.Data.DoorHeldOpen = false;   // only a forced take-off gets here with them open
            doorOpenUntil = 0f;
            Systems.AddArtron(-cost);

            targetPos = target;
            targetYaw = yaw;
            piloted = byPilot;
            Heading = label;
            kind = how;
            interrupted = false;
            Stability = 1f;
            SetSolid(false);

            takeoffClip = Sfx.Takeoff();
            landClip = Sfx.Landing();
            alpha = 255f;
            StartSequence(how == Kind.Hads ? HadsDematSequence : DematSequence, FadeSpeed(how));
            Enter(Phase.Dematerialising);
            PlayExterior(takeoffClip);
            TardisInterior.PlayTravelSound(takeoffClip);
            return null;
        }

        static void FailDemat()
        {
            failLockUntil = Time.time + DematFailLock;
            Systems.AddArtron(-Systems.CostFailedDemat);
            var c = Sfx.Named("force_field_down");
            if (c != null) travelSound.PlayOneShot(c, 1f);
            TardisInterior.PlayTravelSound(c);
        }

        static float FadeSpeed(Kind how)
        {
            bool low = Systems.Damaged;
            if (how == Kind.Hads) return SequenceSpeedHads;
            if (how == Kind.Fast) return low ? SequenceSpeedWarnFast : SequenceSpeedFast;
            return low ? SequenceSpeedWarning : SequenceSpeed;
        }

        static void StartSequence(float[] seq, float speed)
        {
            sequence = seq;
            sequenceSpeed = speed;
            step = 0;
        }

        // How long a sequence takes from the given alpha at the given speed.
        static float SequenceSeconds(float[] seq, float from, float speed)
        {
            float travel = 0f, at = from;
            foreach (var s in seq) { travel += Mathf.Abs(s - at); at = s; }
            return travel / (66f * speed);
        }

        // Moves the alpha along the sequence. True at the end.
        static bool StepSequence()
        {
            alpha = Mathf.MoveTowards(alpha, sequence[step], Time.deltaTime * 66f * sequenceSpeed);
            if (alpha == sequence[step] && ++step >= sequence.Length) return true;
            return false;
        }

        static void PlayExterior(AudioClip clip)
        {
            travelSound.clip = clip != null ? clip : Sfx.Travel();
            travelSound.Play();
        }

        static void Enter(Phase p)
        {
            State = p;
            Progress = 0f;
        }

        // Phase B ends early when the pilot lands. The flight loop fades out in Phase C.
        public static void RequestLanding() { if (State == Phase.Flight) Progress = 1f; }

        // Handbrake pulled, or out of fuel: while going, it snaps back; once gone, it comes straight back down, hard.
        public static void Interrupt()
        {
            if (State == Phase.Dematerialising)
            {
                interrupted = true;
                travelSound.Stop();
                StartSequence(Solidify, DematInterruptSpeed);
                Enter(Phase.Materialising);
            }
            else if (State == Phase.Flight || State == Phase.Premat)
            {
                Stability = Mathf.Min(Stability, RoughLanding - 0.01f);
                if (State == Phase.Flight) Land();
                flightSound.Stop();
                var clip = Sfx.Named("emergency_land") ?? landClip;
                PlayExterior(clip);
                TardisInterior.PlayTravelSound(clip);
                SetVisible(true);
                alpha = 0f;
                StartSequence(MatSequence, SequenceSpeedFast);
                Enter(Phase.Materialising);
            }
        }

        static void TickTravel()
        {
            switch (State)
            {
                case Phase.Dematerialising:
                    bool gone = StepSequence();
                    SetAlpha(alpha);
                    if (gone)
                    {
                        // Phase B: the box is gone. It stays where it was, invisible, until it lands.
                        SetVisible(false);
                        var d = targetPos - root.position;
                        d.y = 0f;
                        phaseLength = kind != Kind.Normal ? FastFlight : piloted && FlightGame.Expected() ? Mathf.Clamp(d.magnitude / PilotedSpeed, PilotedMin, PilotedMax)
                                                                                                         : Mathf.Clamp(d.magnitude / FlightSpeed, MinFlight, MaxFlight);
                        Enter(Phase.Flight);
                        if (flightSound.clip != null) flightSound.Play();
                    }
                    break;
                case Phase.Flight:
                    Progress += Time.deltaTime / phaseLength;
                    if (Progress >= 1f) BeginPremat();
                    break;
                case Phase.Premat:
                    Progress += Time.deltaTime / phaseLength;
                    if (soundAt >= 0f && Progress * phaseLength >= soundAt)
                    {
                        soundAt = -1f;
                        PlayExterior(landClip);
                        TardisInterior.PlayTravelSound(landClip);
                    }
                    if (Progress >= 1f)
                    {
                        flightSound.Stop();
                        SetVisible(true);
                        alpha = 0f;
                        Enter(Phase.Materialising);
                    }
                    break;
                case Phase.Materialising:
                    bool done = StepSequence();
                    SetAlpha(alpha);
                    if (done)
                    {
                        SetAlpha(255f);
                        SetSolid(true);
                        Enter(Phase.Idle);
                        if (interrupted) G.Flash("TRAVEL INTERRUPTED", 3f);
                    }
                    break;
            }
        }

        // Phase C: the wait for the wheeze, then the fade. The box moves now, unseen, so its sounds come from the landing site.
        static void BeginPremat()
        {
            Land();
            Systems.AddArtron(-Systems.CostMat);
            if (State == Phase.Idle) return;
            var rough = piloted && Stability < RoughLanding ? Sfx.Named("emergency_land") : null;
            if (rough != null) landClip = rough;
            float speed = FadeSpeed(kind);
            StartSequence(MatSequence, speed);
            phaseLength = kind == Kind.Normal ? PrematDelay : PrematDelayFast;
            // The landing sound is timed to end as the box goes solid, or starts at once if it's longer than the whole wait and fade.
            float len = landClip != null ? landClip.length : 0f;
            soundAt = Mathf.Max(0f, phaseLength + SequenceSeconds(MatSequence, 0f, speed) - len);
            Enter(Phase.Premat);
        }

        // A piloted flight lands off target by how badly it was stabilised, on the ground wherever that is.
        static void Land()
        {
            var at = targetPos;
            if (piloted && Stability < 1f)
            {
                // Navigation damaged: twice the drift; off or failed: three times.
                float nav = Subsystems.Factor(Subsystems.Navigation);
                var off = Random.insideUnitCircle.normalized * (1f - Stability) * MaxWander * (nav >= 1f ? 1f : nav > 0f ? 2f : 3f) * Protocols.DriftFactor;
                Vector3 spot;
                if (FindLanding(new Vector3(at.x + off.x, at.y, at.z + off.y), out spot)) at = spot;
            }
            MoveTo(at, targetYaw);
        }

        // Flight has ended: stand upright, on the ground under the box.
        public static void SettleAfterFlight()
        {
            float y;
            var at = root.position;
            if (Ground(at.x, at.z, at.y + 2f, out y)) at.y = y;
            MoveTo(at, root.eulerAngles.y);
        }

        // 255 fully here, 0 gone. Uses the transparent shader only while it is partly there.
        static void SetAlpha(float a)
        {
            bool solid = a >= 255f;
            float f = a / 255f;
            lamp.intensity = solid ? 0f : 1.5f * f + 0.5f * Mathf.Abs(Mathf.Sin(Time.time * 5f));
            foreach (var m in fadeMats)
            {
                m.M.shader = solid ? m.Solid : fading;
                var c = m.Colour;
                c.a = f;
                m.M.color = c;
            }
        }

        public static void PlayBell()
        {
            var bell = Sfx.Named("cloister_bell");
            if (bell != null) doorSound.PlayOneShot(bell, 1f);
        }

        static void SetVisible(bool on)
        {
            foreach (var r in renderers) r.enabled = on && !Protocols.CloakOn;
            if (!on) lamp.intensity = 0f;
        }

        static void SetSolid(bool on)
        {
            solid = on;
            foreach (var c in colliders) c.enabled = on;
            if (doorPlug != null) doorPlug.enabled = on && plugOn;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Going in and out.




        // How far the player's transform sits above their feet.
        public static float PlayerFeetOffset()
        {
            var cc = G.Player.GetComponent<CharacterController>();
            return cc != null ? cc.height * 0.5f - cc.center.y + 0.08f : 1f;
        }

        // The door always swings; only entering (outside -> inside) makes the police box door sound.
        static void OpenDoor(bool sound)
        {
            doorOpenUntil = Time.time + 1.3f;
            doorIsOpen = sound;
            if (sound) doorSound.PlayOneShot(doorOpenClip);
        }

        // The doors proper: Use on the police box's door or the room's, or the console's switch. Open, they stay open until someone
        // shuts them, the doorway shows the other side, and the player walks through it (Doorway). Returns null, or why not.
        public static string ToggleDoor() { return SetDoor(!MscTardis.Data.DoorHeldOpen); }

        public static string SetDoor(bool open)
        {
            var d = MscTardis.Data;
            if (d.DoorHeldOpen == open) return null;
            if (open)
            {
                if (Busy) return "THE DOORS ARE LOCKED IN FLIGHT";
                if (Systems.Repairing) return "THE TARDIS IS BEING REPAIRED";
                if (d.DoorLocked) return "THE DOORS ARE LOCKED";
            }
            else if (Doorway.PlayerInDoorway()) return "STAND CLEAR OF THE DOORS";
            MoveDoor(open);
            return null;
        }

        // The sound comes from the side the player is on.
        static void MoveDoor(bool open)
        {
            MscTardis.Data.DoorHeldOpen = open;
            if (MscTardis.Data.Inside) TardisInterior.PlayDoor(open);
            else doorSound.PlayOneShot(open ? doorOpenClip : doorCloseClip);
        }


        // What an open or shut door means for the box itself, each frame: the doors shut themselves when they are locked, when a repair
        // starts or when the box leaves; the doorway's plug is in unless they are open and the box is standing; and the black box
        // behind the leaves gives way to the doorway's picture.
        static void TickDoorway()
        {
            var d = MscTardis.Data;
            if (d.DoorHeldOpen && (d.DoorLocked || Systems.Repairing || State != Phase.Idle)) MoveDoor(false);

            bool plug = !(d.DoorHeldOpen && !Busy);
            if (plug && !plugOn && Doorway.PlayerInDoorway()) plug = false;   // shut on someone standing in it: let them step clear first
            plugOn = plug;
            if (doorPlug != null) doorPlug.enabled = plugOn && solid;
            TardisInterior.SetPlug(plugOn);

            bool boxShown = renderers.Count > 0 && renderers[0].enabled, ajar = doorAngle > 0.5f, copies = ajar || CopiesShut;
            if (blackInner != null) blackInner.enabled = boxShown && !copies;
            foreach (var r in leafRenderers) if (r != null) r.enabled = boxShown && !copies;   // ajar, the leaves are in the room
            if (Doorway.Outer != null) Doorway.Outer.Shown = boxShown && copies;
            if (Doorway.Inner != null) Doorway.Inner.Shown = ajar;
        }

        // ---------------------------------------------------------------------------------------------------------

        public static void Tick()
        {
            if (State != Phase.Idle) TickTravel();
            Systems.Tick();
            Subsystems.Tick();
            Protocols.Tick();
            Takeoff.Tick();
            FlightGame.Tick();
            Pilot.Tick();

            // Keep the save's position current while the box flies itself around.
            if (Flight.Airborne) { var p = root.position; MscTardis.Data.X = p.x; MscTardis.Data.Y = p.y; MscTardis.Data.Z = p.z; MscTardis.Data.Yaw = Yaw; }

            if (doorIsOpen && Time.time >= doorOpenUntil) { doorIsOpen = false; doorSound.PlayOneShot(doorCloseClip); }
            doorAngle = Mathf.MoveTowards(doorAngle, DoorOpen ? DoorSwing : 0f, 180f * Time.deltaTime);
            SwingLeaves();
            LightLeaves();

            // Cloak hides the parked exterior; the beacon pulses its lamp.
            if (State == Phase.Idle)
            {
                bool shown = !Protocols.CloakOn;
                if (renderers.Count > 0 && renderers[0].enabled != shown) SetVisible(true);
                if (Protocols.BeaconOn && shown)
                {
                    float pulse = Mathf.Sin(Time.time * 4f);
                    lamp.intensity = 1.2f + 1.2f * pulse;
                    if (topLight != null) { var c = topLight.Colour * (1.6f + 1.4f * pulse); c.a = topLight.Colour.a; topLight.M.color = c; }   // to 3x lit at the peak
                }
                else if (lamp.intensity > 0f && State == Phase.Idle && !beaconWasOff) lamp.intensity = 0f;
                if (!Protocols.BeaconOn && !beaconWasOff && topLight != null) topLight.M.color = topLight.Colour;
                beaconWasOff = !Protocols.BeaconOn;
            }

            TickDoorway();
            Doorway.Tick();   // before the room's tick, so a step through the doorway shows the room (and loses the fog) in the same frame
            TardisInterior.Tick();
            if (!MscTardis.Data.Inside) LookAtDoor();
        }

        static void LookAtDoor()
        {
            var cam = G.Cam.transform.position;
            bool inFront = root.InverseTransformPoint(cam).z > 0.6f;
            bool looking = !Busy && !G.Seated && inFront && doorCollider.enabled && G.LookingAt(doorCollider, DoorRange);
            if (looking)
            {
                G.Interaction(MscTardis.Data.DoorHeldOpen ? "CLOSE DOOR" : "OPEN DOOR", false, true);
                promptShown = true;
                if (G.UsePressed)
                {
                    var why = ToggleDoor();
                    if (why != null) G.Fail(why);
                }
            }
            else ClearPrompt();
        }

        public static void ClearPrompt()
        {
            if (!promptShown) return;
            G.Interaction("");
            promptShown = false;
        }

        public static void ShowPrompt(string text)
        {
            G.Interaction(text, false, true);
            promptShown = true;
        }


    }
}
