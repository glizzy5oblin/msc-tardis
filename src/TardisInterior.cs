using System;
using System.Collections.Generic;
using UnityEngine;

namespace MscTardis
{
    // The inside: a big closed room far above the map, with the piloting console. Its open door shows the world where the police box
    // stands, and the player walks through it (Doorway); a fade and teleport still serves forced moves.
    // The meshes come from tools/assets/build_interior.py, with their lighting baked into the vertex colours.
    static class TardisInterior
    {
        // High over the map's middle: nothing else is up here, and the game's fall-through rescue only acts under the terrain.
        static readonly Vector3 Origin = new Vector3(0f, 500f, 0f);
        // How far the view ray may pass from a control's grip and still pick it: levers 11 cm, small sprung controls 6 cm (reference/issue_hardtoaccess:
        // at 11 cm, the lever housing's RESET answered while the player was looking at the maker's oval below it).
        const float Apothem = 7.4f, ControlRange = 2.6f, ControlSlack = 0.11f, SmallSlack = 0.06f;
        const int Sides = 12;
        const float PlatformR = 4.4f, PlatformZ = 0.22f, RotorTravel = 0.28f;
        static readonly Vector3 MonitorAt = new Vector3(0.08f, 1.78f, -0.78f);

        public static readonly int[] Steps = { 10, 100, 1000 };
        public static readonly string[] Rooms = { "CUSTOM", "TOYOTA", "2010" };
        // The rooms converted from the GMod addon's models, by room index: the file prefix tools/assets/convert_source.py wrote them under
        // (<prefix>.txt, <prefix>_anim.txt, <prefix>_controls.txt). The custom room (0) is built by build_interior.py instead.
        static readonly string[] RoomFiles = { null, "toy", "t10" };
        // Rooms whose bake counts 128 as a texture's own brightness and goes up to twice it (Res.RoomMode.Bright): the 2010 room, lit brighter
        // than its textures as the set is. The Toyota's bake was judged as the old shaders show it, which top out at the texture.
        static readonly bool[] BrightBake = { false, false, true };
        // A room's own recordings, assets/sfx/tardis_<prefix><name>.wav: its hum, door and handbrake, and a sound for each control by key.
        static readonly string[] RoomSounds = { null, null, "2010_" };
        static readonly Dictionary<string, AudioClip> controlClips = new Dictionary<string, AudioClip>();

        // What differs between rooms: where you arrive, how far you can stray, and how far you can fall before being put back.
        static Vector3 entry;
        static float entryYaw, keepRadius, keepFloor;

        // A part of the Toyota room that moves: a transform and its sampled poses, from convert_source.py.
        class AnimPart { public string Key; public Transform T; public Vector3[] P; public Quaternion[] Q; }
        static readonly List<AnimPart> animParts = new List<AnimPart>();
        static readonly Dictionary<string, PartAnim> anims = new Dictionary<string, PartAnim>();
        // The console's lamps in a converted room: parts named lamp_<role>, lit or dark by what their role shows (LampState). The converter gives
        // each lamp the role of the control it is by, or a gauge, or ambient (lit while there is power).
        class RoomLamp
        {
            public readonly List<Renderer> R = new List<Renderer>();
            public readonly List<Material> On = new List<Material>(), Off = new List<Material>();
            public Func<bool> Lit;
            public bool Shown;
        }
        static readonly Dictionary<string, RoomLamp> roomLamps = new Dictionary<string, RoomLamp>();
        // How each spare control is worked, for the flight minigame's prompt (SLIDE, FLIP, PRESS, ...): from the converter's controls file, or by the kind
        // of control AddDeco made.
        static readonly Dictionary<string, string> verbs = new Dictionary<string, string>(), decoKinds = new Dictionary<string, string>();
        static readonly Dictionary<string, Material> lampOff = new Dictionary<string, Material>();
        static Renderer coverPart;   // the power switch's cover where a converted room has one, for aiming at it where it is now
        static readonly HashSet<string> flipped = new HashSet<string>();
        static bool noMeshes;

        public static Transform Root { get; private set; }

        class Control
        {
            public string Key;
            public Vector3 At;
            public Transform Grip;      // a lever is looked at by its grip, which moves; otherwise At
            public Func<string> Label;
            public Action Use;
            public Transform Moving;
            public Func<float> Angle;   // degrees about the control's own axis; null for a plain button
            public Vector3 Axis = Vector3.right;
            public float Scale = 1f;    // of the angle: how far this model swings for the angle asked
            public Quaternion Base = Quaternion.identity;
            public bool Push;           // a push button: it slides along Axis by Scale metres from -35 (out) to +35 (in), rather than turning
            public bool Spring;         // a small switch, key or knob: it snaps over and settles (Spring()) instead of turning at a steady rate
            public float Cur, Vel;      // where a sprung control is now (in the angle's units) and how fast it is going
            public float Zeta = 1f;     // its spring's damping: 1 snaps over without a bounce (switches, knobs), less gives a push button some give
            public List<Renderer> Lamp; // a lit push button's cap, and its look unlit and lit (it lights while on and the ship has power)
            public Material LampOff, LampOn;
            public bool LampShown;
            public Func<bool> LitWhen;  // a lamp that shows something other than its own on/off (the switch board's indicators)
        }

        // A small control's snap: a stiff spring (about 9 Hz) that, critically damped, throws a switch or knob over in under a tenth of a second
        // without bouncing; push buttons are damped a little less, so a cap gives a touch. Integrated in short steps so it stays stable at any
        // frame rate.
        const float SpringW = 55f;
        static void Spring(Control c, float target, float dt)
        {
            const float step = 1f / 240f;
            for (float left = Mathf.Min(dt, 0.1f); left > 0f; left -= step)
            {
                float h = Mathf.Min(left, step);
                c.Vel += (SpringW * SpringW * (target - c.Cur) - 2f * c.Zeta * SpringW * c.Vel) * h;
                c.Cur += c.Vel * h;
            }
        }

        static readonly List<Control> controls = new List<Control>();
        // A second moving part of a control on its own pivot (tardis_controls.txt "<key>.<part>"): it swings with that control's angle.
        class Follower { public Control Of; public Transform Moving; public Quaternion Base; public Vector3 Axis; public float Scale; }
        static readonly List<Follower> followers = new List<Follower>();
        // Parts that spin freely about their control's axis (the fan on the throttle's bolt), and that axis in their own frame.
        static readonly List<KeyValuePair<Transform, Vector3>> spinners = new List<KeyValuePair<Transform, Vector3>>();
        static float spinSpeed, spinAngle;   // degrees per second; degrees turned so far
        static readonly HashSet<string> auxOn = new HashSet<string>();   // which of the spare toggles are up (not saved)
        static bool coverOpen;   // the power switch's guard, lifted by hand (not saved; it is always up while the power is on)
        static bool CoverUp { get { return coverOpen || MscTardis.Data.Power || !controls.Exists(k => k.Key == "power.cover"); } }   // no guard in the other rooms
        // The custom room's panel controls wired to nothing ("d_" keys in tardis_controls.txt): which switches are on, which click each knob
        // is at (0..4, 2 the middle), and which keys have been seen (a "toggleon" starts on the first time). Kept for the session, not saved.
        static readonly HashSet<string> decoOn = new HashSet<string>(), decoSeen = new HashSet<string>();
        static readonly Dictionary<string, int> decoStep = new Dictionary<string, int>();
        // The coloured ring under each of the Toyota's six sliders: dim when the ship has power, brighter when its slider is up.
        class RingLamp { public string Key; public Renderer R; public Material Dark, Glow; public float Shown = -2f; }
        static readonly List<RingLamp> ringLamps = new List<RingLamp>();
        const float FanFlick = 180f, FanDrag = 40f;   // a flick's speed (deg/s: half a turn a second), and how fast it runs down (4.5 s from one flick)
        static Collider doorCollider, doorPlug;
        static Transform rotor;
        // The doorway, by room. Where its plane is: where each room's own leaves were shut. The coral room's is given in room space; a
        // converted room's is so far behind its "doors" point (tardis_controls.txt), along the way out, at its floor's height. Its opening
        // is the police box's (WorldPortal.Width x Height), narrower than each room's own door frame, so jambs fill the rest out to
        // JambOut either side and up to JambTop. The room's own leaves aren't built: the doors are the police box's, which swing into the
        // room (Tardis.BuildRoomLeaves). DoorTurn turns a converted room's door frame and doorway about its "doors" point (degrees, towards
        // the console): the 2010 room's faced well to the side of it. Its alcove stays as modelled, so the turn is kept small: at 20° (Phase 8)
        // the frame stood visibly askew in it (the user: "the whole entrace has kinof been butchered").
        static readonly float[] DoorBack = { 0f, 0.165f, 0.04f }, DoorFloor = { 0f, 0f, 0.258f }, JambOut = { 0.80f, 0.60f, 0.62f }, JambTop = { 2.30f, 2.30f, 2.32f };
        static readonly float[] DoorTurn = { 0f, 0f, 10f };
        static readonly Color[] JambColour = { new Color(0.05f, 0.11f, 0.26f), new Color(0.04f, 0.09f, 0.22f), new Color(0.04f, 0.09f, 0.22f) };
        const float CoralDoorPlane = -Apothem + 0.08f;   // the leaves' backs are at -Apothem + 0.095
        static Vector3 doorPoint;
        static float doorYaw;
        static Renderer[] roomRenderers;
        static float renderersAt;
        static readonly List<Material> tinted = new List<Material>();
        static Light rotorLight;
        static Color roomLightColour = new Color(1f, 0.88f, 0.68f);
        static readonly List<Light> roomLights = new List<Light>();
        static TextMesh monitor;
        // The computer's last page on the monitor: a black backing and the terminal's own picture over the room's screen, shown unless the
        // old text readout has something to say (no power, the flight prompt, a protocol under way).
        static GameObject screenBack, screenPic, screenOver;
        static Material screenMat, screenOverMat;
        // The keyboard under the coral room's monitor (the 2010 console's, the a_selector_static mesh of t10.txt): looking at it is looking at the computer.
        static Collider keyboardCollider;
        // Where it lies: flat on the south bay's plate (the deck, sloped 23.7 degrees up towards the hub), most of it under the monitor's lip,
        // centred on the monitor (x 0.08), its front edge level with the keypad's top edge and its back edge short of the monitor's posts.
        // In build_interior.py's panel frame for that bay it covers x -0.16..0.32, y 0.0..0.19. The 2010 model is only the keys, turned
        // 58 degrees in its own room: KbTop is the way its keys face and KbBack the way its far edge lies, both in the model's space (measured
        // from that mesh). The case under them is built here.
        // The join: the panel origin (0, 1.3156, -0.82) + 0.19 up the slope (0, 0.402, 0.916) + the case's thickness and 2 mm along the normal.
        static readonly Vector3 KeyboardJoin = new Vector3(0.08f, 1.421f, -0.659f);   // the middle of the case's back edge, top face
        const float KeyboardTilt = 23.7f, CaseThick = 0.03f, CaseMargin = 0.012f;
        static readonly Vector3 KbTop = new Vector3(0.4356f, 1f, -0.2511f).normalized, KbBack = new Vector3(-0.851f, 0f, 0.525f);
        // The monitor's screen, in room space: its centre, the way it faces and its size (the computer opens by looking at it).
        static Vector3 screenAt, screenFace = Vector3.back;
        static float screenW = 0.5f, screenH = 0.4f;
        static AudioSource hum, sfx, door, lifeBell;
        static Transform roomAudio;   // the room's own 3D sounds, at its origin; with the player outside, where the open doorway puts them
        static float audioOut = 1f;   // their loudness for where the player is: 1 in the room, how far the doors stand open outside
        static AudioClip travelClip, clickClip, doorOpenClip, doorCloseClip, brakeOnClip, brakeOffClip, powerUpClip, powerDownClip, malfunctionClip;
        static AudioSource flightLoop;
        static float powerLevel = 1f;   // 0 dark and silent .. 1 fully powered; eases over about 2.5 s
        static bool malfunctionPlayed;
        static float doorOpenUntil;
        static bool doorIsOpen;
        static Vector3 camBase;
        static bool shaking;
        static GameObject rain;
        static bool rainHidden;
        static Behaviour fog;
        static bool fogHidden;
        // The game camera's GlobalFog as it is outside the room (in here it is switched off): the view out puts it on its own picture.
        public static bool WorldGlobalFog { get { return fogHidden || (fog != null && fog.enabled); } }

        public static void Build()
        {
            controls.Clear();
            followers.Clear();
            spinners.Clear();
            roomLights.Clear();
            tinted.Clear();
            shaking = false;
            roomShown = true;
            roomRenderers = null;
            doorPlug = null;
            doorPoint = new Vector3(0f, 1.15f, -8.3f);
            doorYaw = 0f;
            // A rebuild (room change) while inside: give the world back the fog and rain sound the old room had taken before forgetting them.
            if (fogHidden && fog != null) fog.enabled = true;
            if (rainMuted && rainAudio != null) rainAudio.mute = false;
            fog = null;
            fogHidden = false;
            var rainT = G.Player.Find("Rain");
            rain = rainT != null ? rainT.gameObject : null;
            var rainSoundT = G.Player.Find("Pivot/AnimPivot/Camera/FPSCamera/FPSCamera/AudioRain");
            rainAudio = rainSoundT != null ? rainSoundT.GetComponent<AudioSource>() : null;
            rainMuted = false;
            Root = new GameObject("MscTardisTardisInterior").transform;
            Root.position = Origin;
            var d = MscTardis.Data;
            d.Room = ModOptions.BetaConsoles ? Mathf.Clamp(d.Room, 0, Rooms.Length - 1) : 0;   // the Toyota and 2010 consoles are beta, behind a mod setting
            animParts.Clear();
            anims.Clear();
            flipped.Clear();
            roomLamps.Clear();
            ringLamps.Clear();
            verbs.Clear();
            decoKinds.Clear();
            lampOff.Clear();
            coverPart = null;
            rotor = null;
            noMeshes = d.Room != 0;
            entryYaw = 0f;
            if (noMeshes) BuildConverted(RoomFiles[d.Room]);
            else
            {
                BuildRoom();
                entry = new Vector3(0f, 0.22f, -Apothem + 1.3f);
                keepRadius = Apothem + 1f;
                keepFloor = -1f;
            }
            screenAt = MonitorAt;
            screenFace = Vector3.back;
            screenW = 0.50f;
            screenH = 0.40f;
            BuildConsole();
            if (noMeshes) ApplyConvertedControls(RoomFiles[d.Room]);
            else ApplyCustomControls();
            BuildIndicators(RoomFiles[d.Room] ?? "tardis");
            foreach (var kv in roomLamps) kv.Value.Lit = LampState(kv.Key);
            BuildScreen();
            BuildKeyboard();
            BuildDoorway();
            if (d.Room == 2) rotorLight.color = new Color(0.15f, 1f, 0.85f);   // the 2010 rotor glows teal
            d.Selector = Mathf.Clamp(d.Selector, 0, Tardis.Places.Length + Tardis.Extras.Length);   // an extra stands only while the telepathic jump is on (Protocols.Build)
            d.StepIndex = Mathf.Clamp(d.StepIndex, 0, Steps.Length - 1);
            controlClips.Clear();
            // A converted room with its own recordings lists each control's sound (<prefix>_sounds.txt: key, then the recording's name).
            if (RoomSounds[d.Room] != null && Res.Has(RoomFiles[d.Room] + "_sounds.txt"))
                foreach (var line in Res.Lines(RoomFiles[d.Room] + "_sounds.txt"))
                {
                    var f = line.Split(' ');
                    if (f.Length < 2) continue;
                    var c = RoomClip(f[1]);
                    if (c != null) controlClips[f[0]] = c;
                }
            roomAudio = new GameObject("RoomAudio").transform;
            roomAudio.SetParent(Root, false);
            hum = Sfx.Source(roomAudio.gameObject, RoomClip("hum") ?? Sfx.Hum(), 0.25f, 30f, true);
            hum.Play();
            sfx = Sfx.Source(roomAudio.gameObject, null, 1f, 60f);
            lifeBell = Sfx.Source(roomAudio.gameObject, Sfx.Named("cloister_bell"), 1f, 60f);   // a source of its own, to cut it off (RingLifeBell)
            door = Sfx.Source(Root.gameObject, null, 0.8f, 30f);
            door.spatialBlend = 0f;   // heard by the player on both sides of the fade, whichever room the source is in
            travelClip = Sfx.Travel();
            clickClip = Sfx.Click();
            doorOpenClip = RoomClip("door_open") ?? Sfx.InteriorDoorOpen();
            doorCloseClip = RoomClip("door_close") ?? Sfx.InteriorDoorClose();
            brakeOnClip = RoomClip("handbrake_engage") ?? Sfx.HandbrakeEngage();
            brakeOffClip = RoomClip("handbrake_release") ?? Sfx.HandbrakeRelease();
            powerUpClip = Sfx.Named("power_up");
            powerDownClip = Sfx.Named("power_down");
            malfunctionClip = Sfx.Named("malfunction");
            // The in-flight loop (time_rotor_flying), running through the flight phase.
            flightLoop = Sfx.Source(roomAudio.gameObject, Sfx.FlightLoop(), 0.6f, 30f, true);
            powerLevel = d.Power ? 1f : 0f;
            malfunctionPlayed = false;
            doorIsOpen = false;
            Computer.Build(Root);
        }

        // The current room's own recording of that name, or null.
        static AudioClip RoomClip(string name)
        {
            var set = RoomSounds[MscTardis.Data.Room];
            if (set == null) return null;
            var file = "tardis_" + set + name + ".wav";
            return Res.Has(file) ? Res.Clip(file) : null;
        }

        static void BuildRoom()
        {
            rotor = Pivot("Rotor", Vector3.zero);

            // One line per mesh: file, part, texture, mode.
            var mats = new Dictionary<string, Material>();
            foreach (var line in Res.Lines("tardis_room.txt"))
            {
                var f = line.Split(' ');
                if (f.Length < 4 || f[1].StartsWith("ctrl_")) continue;
                string part = f[1], key = f[2] + "/" + f[3];
                if (part == "door_l" || part == "pane_l" || part == "sign_l" || part == "door_r" || part == "pane_r") continue;   // the room's own leaves
                Material mat;
                if (!mats.TryGetValue(key, out mat))
                {
                    var mode = f[3] == "cutout" ? Res.RoomMode.Cutout : f[3] == "glass" ? Res.RoomMode.Glass
                        : f[3] == "add" ? Res.RoomMode.Additive : Res.RoomMode.Baked;
                    mats[key] = mat = Res.RoomMat(f[2] + ".png", mode, Color.white);
                    if (f[3] == "baked" || f[3] == "cutout") tinted.Add(mat);   // flight strain dims these; glows stay lit
                }
                var parent = part == "rotor" || part == "rotor_core" ? rotor : Root;
                var mesh = Res.Dwm(f[0]);
                // Behind the leaves the room was modelled shut: wall panel and a dark backing fill the doorway. They come out, so the
                // doorway's picture shows there (the edges left are behind the jambs).
                if (part == "wall" || part == "dark") mesh = Res.Cut(mesh, CoralDoorway, false);
                // The room's leaves' two brass handles are part of its fixed brass: they go with the leaves.
                if (part == "brass") mesh = Res.Cut(Res.Cut(mesh, HandleL, false), HandleR, false);
                Res.MeshObject(part, mesh, mat, parent);
            }
            BuildColliders();

            var trigger = new GameObject("DoorTrigger");
            trigger.transform.SetParent(Root, false);
            trigger.transform.localPosition = new Vector3(0f, 1.15f, -Apothem + 0.12f);
            var bc = trigger.AddComponent<BoxCollider>();
            bc.size = new Vector3(1.5f, 2.3f, 0.1f);
            bc.isTrigger = true;
            doorCollider = bc;

            // The room's own light is baked; these light only what the player carries in, and the player's hands.
            for (int i = 0; i < 4; i++)
            {
                float a = (45f + i * 90f) * Mathf.Deg2Rad;
                roomLights.Add(RoomLight(new Vector3(Mathf.Cos(a) * 3.5f, 4.8f, Mathf.Sin(a) * 3.5f), 13f, 1.1f, new Color(1f, 0.88f, 0.68f)));
            }
            roomLightColour = new Color(1f, 0.88f, 0.68f);
        }

        // Room space: the doorway behind the coral room's leaves, and the two door handles.
        static readonly Bounds CoralDoorway = new Bounds(new Vector3(0f, 1.15f, -Apothem), new Vector3(1.52f, 2.34f, 0.14f));
        static readonly Bounds HandleL = new Bounds(new Vector3(-0.1f, 1.15f, -Apothem + 0.145f), new Vector3(0.08f, 0.16f, 0.14f));
        static readonly Bounds HandleR = new Bounds(new Vector3(0.1f, 1.15f, -Apothem + 0.145f), new Vector3(0.08f, 0.16f, 0.14f));

        // ---------------------------------------------------------------------------------------------------------
        // The doorway: the portal's frame, the walking room behind its plane, the jambs round it and the plug that shuts it.
        // In the frame's space x is across the doorway, y up from the floor and z into the room.

        static void BuildDoorway()
        {
            int room = MscTardis.Data.Room;
            Vector3 at;
            if (noMeshes)
            {
                at = doorPoint - Quaternion.Euler(0f, doorYaw, 0f) * Vector3.forward * DoorBack[room];
                at.y = DoorFloor[room];
            }
            else at = new Vector3(0f, 0f, CoralDoorPlane);
            var rot = Quaternion.Euler(0f, doorYaw, 0f);
            Doorway.SetInner(Root, at, rot);

            var parts = new GameObject("DoorwayParts").transform;
            parts.SetParent(Root, false);
            parts.localPosition = at;
            parts.localRotation = rot;

            // Walking room: the player's body reaches behind the plane as the view comes up to it, and stands across it on arriving.
            float w = Doorway.WalkHalf, d = Doorway.WalkDepth, h = WorldPortal.Height, side = 0.9f, top = 2.6f;
            DoorSolid(parts, "DoorwaySideL", new Vector3(-side, -0.2f, -d), new Vector3(-w, top, 0f));
            DoorSolid(parts, "DoorwaySideR", new Vector3(w, -0.2f, -d), new Vector3(side, top, 0f));
            DoorSolid(parts, "DoorwayLintel", new Vector3(-w, h, -d), new Vector3(w, top, 0f));
            DoorSolid(parts, "DoorwayBack", new Vector3(-side, -0.2f, -d - 0.3f), new Vector3(side, top, -d));
            DoorSolid(parts, "DoorwayFloor", new Vector3(-side, -0.2f, -d - 0.3f), new Vector3(side, 0f, 0.02f));
            // Shut, the leaves are solid (they had no colliders: the wall behind them was what stopped the player).
            doorPlug = DoorSolid(parts, "DoorwayPlug", new Vector3(-w, 0f, -d), new Vector3(w, h, 0.13f));

            // Jambs and a head, in the plane, from the picture's edge out under the room's own door frame.
            float edge = WorldPortal.Width / 2f - 0.003f, jo = JambOut[room], jt = JambTop[room];
            var mat = Res.RoomMat("white.png", Res.RoomMode.Lamp, JambColour[room] * 0.5f);   // that shader doubles its colour
            Res.Box("DoorwayJambL", parts, new Vector3(-(edge + jo) / 2f, jt / 2f, -0.015f), new Vector3(jo - edge, jt, 0.03f), mat);
            Res.Box("DoorwayJambR", parts, new Vector3((edge + jo) / 2f, jt / 2f, -0.015f), new Vector3(jo - edge, jt, 0.03f), mat);
            Res.Box("DoorwayHead", parts, new Vector3(0f, (h - 0.003f + jt) / 2f, -0.015f), new Vector3(edge * 2f, jt - h + 0.003f, 0.03f), mat);

            Tardis.BuildRoomLeaves(parts);
        }

        static Collider DoorSolid(Transform parent, string name, Vector3 min, Vector3 max)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var bc = go.AddComponent<BoxCollider>();
            bc.center = (min + max) / 2f;
            bc.size = max - min;
            return bc;
        }

        public static void SetPlug(bool on) { if (doorPlug != null && doorPlug.enabled != on) doorPlug.enabled = on; }

        public static void PlayDoor(bool open) { if (door != null) door.PlayOneShot(open ? doorOpenClip : doorCloseClip); }

        // Everything in the room that is drawn, but for the doorway's own surface, which has its own rules. Looked up again every couple
        // of seconds: a few things are made late (the protocol halos).
        static Renderer[] RoomRenderers()
        {
            if (roomRenderers != null && Time.time < renderersAt) return roomRenderers;
            var all = Root.GetComponentsInChildren<Renderer>(true);
            var frame = Doorway.Inner != null ? Doorway.Inner.Frame : null;
            roomRenderers = frame == null ? all : Array.FindAll(all, r => !r.transform.IsChildOf(frame));
            renderersAt = Time.time + 2f;
            return roomRenderers;
        }

        // The room isn't drawn while nobody is in it, except into the picture in the police box's open doorway.
        public static void ShowForPortal(bool on)
        {
            if (roomShown) return;
            foreach (var r in RoomRenderers()) if (r != null) r.enabled = on;
        }

        // The player has just walked through the doorway, either way. Tick, which follows in the same frame, shows or hides the room and
        // sorts out the fog and the rain.
        public static void OnCrossed()
        {
            ApplyInside();
            MuteAmbience(MscTardis.Data.Inside && !Pilot.Active && !Tardis.DoorOpen);
            PlaceAudio();
            PortalAudio.Crossed();
            Tardis.ClearPrompt();
        }

        // What being in the room or out of it means for what is drawn: the room, the rain and the fog. Each frame from Tick, and at once
        // when the player steps through the doorway (which happens after the frame's ticks, just before it is drawn).
        static void ApplyInside()
        {
            // The room hangs in the sky above the map. It only needs drawing with someone standing in it.
            bool show = MscTardis.Data.Inside && !Pilot.Active;
            if (show != roomShown)
            {
                roomShown = show;
                renderersAt = 0f;
                foreach (var r in RoomRenderers()) r.enabled = show;
            }
            // The weather follows the player, and it is raining out there, not in here.
            // With the doors open it rains on, out there, where the doorway puts the player: the rain stands where the player would be on
            // the map, so it falls past the box's doorway, in the picture looking out, and stepping out carries on in the same rain.
            if (rain != null)
            {
                bool inside = MscTardis.Data.Inside;
                bool through = inside && Doorway.RainThrough && !Pilot.Active && Doorway.Inner != null && Doorway.Inner.Shown;
                bool shell = Protocols.ShellOn && (through || !inside) && Vector3.Distance(through ? Tardis.Position : G.Player.position, Tardis.Position) < 18f;
                bool dry = shell || (inside && !through);
                if (dry && rain.activeSelf) { rain.SetActive(false); rainHidden = true; }
                else if (!dry && rainHidden) { rain.SetActive(true); rainHidden = false; }
                PlaceRain(through && !dry);
            }
            RainSound();
            // (While piloting, the camera is out in the world, where the fog and the map's sound belong.)
            // The camera's height fog is tuned for the map, and draws a grey sheet across the room up here.
            if (fog == null) fog = G.Cam.GetComponent("GlobalFog") as Behaviour;
            if (fog != null)
            {
                bool inRoom = MscTardis.Data.Inside && !Pilot.Active;
                if (inRoom && fog.enabled) { fog.enabled = false; fogHidden = true; }
                else if (!inRoom && fogHidden) { fog.enabled = true; fogHidden = false; }
            }
        }

        static Vector3 rainHomePos;
        static Quaternion rainHomeRot = Quaternion.identity;
        static bool rainMapped;
        static void PlaceRain(bool mapped)
        {
            var t = rain.transform;
            if (mapped)
            {
                if (!rainMapped) { rainHomePos = t.localPosition; rainHomeRot = t.localRotation; rainMapped = true; }
                var parent = t.parent;
                Vector3 p; Quaternion q;
                Doorway.Inner.TransformPose(parent.TransformPoint(rainHomePos), parent.rotation * rainHomeRot, out p, out q);
                t.position = p;
                t.rotation = q;
            }
            else if (rainMapped)
            {
                t.localPosition = rainHomePos;
                t.localRotation = rainHomeRot;
                rainMapped = false;
            }
        }

        // The rain's sound is the game's, flat (not from anywhere) on the player's camera, and nothing turned it down in here: it rained as
        // loud all through the room, doors shut or not. In the room it is muted, and a copy of it comes in at the doorway, from just outside:
        // at the doorway as loud and as flat as outside, so the step changes nothing, and further in from the doorway's direction, fading.
        static AudioSource rainAudio, rainTwin;
        static bool rainMuted;
        static void RainSound()
        {
            if (rainAudio == null) return;
            bool inRoom = MscTardis.Data.Inside && !Pilot.Active;
            if (inRoom && !rainAudio.mute) { rainAudio.mute = true; rainMuted = true; }
            else if (!inRoom && rainMuted) { rainAudio.mute = false; rainMuted = false; }
            float open = inRoom && Doorway.Inner != null ? Tardis.DoorFraction : 0f;
            bool on = rainAudio.isPlaying && rainAudio.clip != null;   // kept going, silent outside, so the step in starts nothing
            if (!on)
            {
                if (rainTwin != null && rainTwin.isPlaying) rainTwin.Stop();
                return;
            }
            if (rainTwin == null)
            {
                rainTwin = Sfx.Source(new GameObject("RainThrough"), null, 0f, 15f, true);
                rainTwin.minDistance = 1f;
                rainTwin.outputAudioMixerGroup = rainAudio.outputAudioMixerGroup;   // the game's ambience group, which its roofs turn down
            }
            var f = Doorway.Inner.Frame;
            rainTwin.transform.position = f.position + f.up * 1.2f - f.forward * 0.4f;   // just outside the doorway
            if (rainTwin.clip != rainAudio.clip) { rainTwin.clip = rainAudio.clip; rainTwin.Stop(); }
            rainTwin.loop = rainAudio.loop;
            rainTwin.pitch = rainAudio.pitch;
            rainTwin.volume = rainAudio.volume * open;
            float d = Vector3.Distance(G.Cam.transform.position, rainTwin.transform.position);
            rainTwin.spatialBlend = Mathf.Clamp01((d - 0.6f) / 2.5f);   // flat at the doorway, as outside; from the doorway further in
            if (!rainTwin.isPlaying) { rainTwin.timeSamples = Mathf.Min(rainAudio.timeSamples, rainAudio.clip.samples - 1); rainTwin.Play(); }
        }


        // ---------------------------------------------------------------------------------------------------------
        // The Toyota and 2010 rooms, converted from the GMod addon's models. Same console logic as the custom room: only the meshes
        // and where each control is differ.

        static void BuildConverted(string prefix)
        {
            bool toyota = prefix == "toy";
            // The Toyota room is a big hall with its door on the -Z side. A converted room can name its own arrival point (ApplyConvertedControls).
            entry = new Vector3(0f, 0.3f, -7.1f);
            keepRadius = toyota ? 45f : 30f;
            keepFloor = toyota ? -6f : -4f;

            var movers = new Dictionary<string, Transform>();
            foreach (var line in Res.Lines(prefix + "_anim.txt"))
            {
                var f = line.Split(' ');
                if (f.Length < 5) continue;
                var part = f[0];
                var t = Pivot(part, new Vector3(G.F(f[1]), G.F(f[2]), G.F(f[3])));
                movers[part] = t;
                int n = int.Parse(f[4]);
                var a = new AnimPart { Key = part.Substring(2, part.LastIndexOf('_') - 2), T = t, P = new Vector3[n], Q = new Quaternion[n] };
                for (int i = 0; i < n; i++)
                {
                    int o = 5 + i * 7;
                    a.P[i] = new Vector3(G.F(f[o]), G.F(f[o + 1]), G.F(f[o + 2]));
                    a.Q[i] = new Quaternion(G.F(f[o + 3]), G.F(f[o + 4]), G.F(f[o + 5]), G.F(f[o + 6]));
                }
                animParts.Add(a);
            }

            var mats = new Dictionary<string, Material>();
            bool bright = BrightBake[MscTardis.Data.Room];
            foreach (var line in Res.Lines(prefix + ".txt"))
            {
                var f = line.Split(' ');
                if (f.Length < 4) continue;
                if (f[1] == "collider")   // a mesh that is only there to be stood on, e.g. the glass panes between the 2010 platform's struts
                {
                    var cg = new GameObject("WalkSolid");
                    cg.transform.SetParent(Root, false);
                    cg.AddComponent<MeshCollider>().sharedMesh = Res.Dwm(f[0]);
                    continue;
                }
                string key = f[2] + "/" + f[3];
                Material mat;
                if (!mats.TryGetValue(key, out mat))
                {
                    var mode = f[3] == "cutout" ? Res.RoomMode.Cutout : f[3] == "glass" ? Res.RoomMode.Glass : f[3] == "add" ? Res.RoomMode.Additive
                        : f[3] == "self" ? (bright ? Res.RoomMode.BrightSelfIllum : Res.RoomMode.SelfIllum) : bright ? Res.RoomMode.Bright : Res.RoomMode.Baked;
                    mats[key] = mat = Res.RoomMat(f[2], mode, Color.white);
                    if (f[3] == "lit" || f[3] == "cutout" || f[3] == "self") tinted.Add(mat);   // flight strain dims these; glows stay lit
                }
                if (f[1].StartsWith("a_doors_") && f[1] != "a_doors_static") continue;   // the room's own leaves (its frame stays)
                Transform parent;
                if (!movers.TryGetValue(f[1], out parent)) parent = Root;
                var go = Res.MeshObject(f[1], Res.Dwm(f[0]), mat, parent);
                // The coloured ring under a slider (ring_<slider's key>): as the model has it while the ship is dark, lit in its own colour by the
                // lamp shader while it has power, brighter when its slider is up.
                if (f[1].StartsWith("ring_"))
                    ringLamps.Add(new RingLamp { Key = f[1].Substring(5), R = go.GetComponent<Renderer>(), Dark = mat, Glow = Res.RoomMat(f[2], Res.RoomMode.Lamp, new Color(0.5f, 0.5f, 0.5f)) });
                // A pill button's red lamp (a_<key>_dot) moves with the button and is its indicator: a lamp of the role dot_<key>.
                bool dot = f[1].StartsWith("a_") && f[1].EndsWith("_dot");
                if (f[1].StartsWith("lamp_") || dot)
                {
                    string role = dot ? "dot_" + f[1].Substring(2, f[1].Length - 6) : f[1].Substring(5);
                    RoomLamp rl;
                    if (!roomLamps.TryGetValue(role, out rl)) roomLamps[role] = rl = new RoomLamp();
                    Material dark;
                    if (!lampOff.TryGetValue(f[2], out dark)) lampOff[f[2]] = dark = Res.RoomMat(f[2], Res.RoomMode.Lamp, new Color(0.1f, 0.1f, 0.1f));
                    var rend = go.GetComponent<Renderer>();
                    rl.R.Add(rend); rl.On.Add(mat); rl.Off.Add(dark);
                    rend.sharedMaterial = dark;
                }
                if (f[1] == "a_power.cover_0") coverPart = go.GetComponent<Renderer>();
                if (f[1] == "solid") go.AddComponent<MeshCollider>().sharedMesh = go.GetComponent<MeshFilter>().sharedMesh;
            }

            for (int i = 0; i < 4; i++)
            {
                float a = (45f + i * 90f) * Mathf.Deg2Rad;
                roomLights.Add(RoomLight(new Vector3(Mathf.Cos(a) * 3.5f, 4.8f, Mathf.Sin(a) * 3.5f), 13f, 1.1f, toyota ? new Color(0.8f, 0.9f, 1f) : new Color(1f, 0.55f, 0.22f)));
            }
            roomLightColour = toyota ? new Color(0.8f, 0.9f, 1f) : new Color(1f, 0.55f, 0.22f);
        }

        // After BuildConsole has made the controls: put each where its model is, and run it by its own animation.
        static void ApplyConvertedControls(string prefix)
        {
            var at = new Dictionary<string, Vector3>();
            var panel = new List<string[]>();   // panel controls (d_ keys) with a kind and a label: made below unless the code wires them
            var monitorNormal = Vector3.forward;
            float doorYaw = 0f, screenW = 0.60f, screenH = 0.31f;   // the Toyota monitor's screen, unless the file gives its own
            foreach (var line in Res.Lines(prefix + "_controls.txt"))
            {
                var f = line.Split(' ');
                if (f.Length < 6) continue;
                string key = f[0], kind = f[1];
                at[key] = new Vector3(G.F(f[3]), G.F(f[4]), G.F(f[5]));
                if (key.StartsWith("d_") && f.Length >= 8) panel.Add(new[] { key, f[6], f[7].Replace('_', ' '), f.Length > 8 ? f[8] : null });
                if (key == "monitor")
                {
                    monitorNormal = new Vector3(G.F(f[6]), G.F(f[7]), G.F(f[8]));
                    if (f.Length > 10) { screenW = G.F(f[9]); screenH = G.F(f[10]); }
                }
                // A door that isn't square to the room's axes, and where to arrive: the yaw (degrees) of the way into the room.
                if (key == "doors" && f.Length > 6) doorYaw = G.F(f[6]);
                if (key == "entry") { entry = at[key]; if (f.Length > 6) entryYaw = G.F(f[6]); continue; }
                float speed = G.F(f[2]);
                if (kind == "travel") anims[key] = new PartAnim(PartAnim.Type.Travel, speed);
                else if (kind == "idle") anims[key] = new PartAnim(PartAnim.Type.Idle, speed);
                else if (kind == "door") anims[key] = new PartAnim(PartAnim.Type.Toggle, speed, () => Tardis.DoorOpen || Time.time < doorOpenUntil);
                else if (kind == "press")
                {
                    var k = key;
                    // PROTOCOL CANCEL (where there is a board) is a button that springs back; the rest stay where they are put.
                    if (k == "fastreturn" && Subsystems.Board) anims[key] = new PartAnim(PartAnim.Type.Toggle, speed, () => Pulse("fastreturn") > 0f);
                    else anims[key] = new PartAnim(PartAnim.Type.Toggle, speed, () => flipped.Contains(k));
                }
                else if (kind == "turn") anims[key] = new PartAnim(PartAnim.Type.Cycle, speed);
                else if (kind == "toggle") anims[key] = new PartAnim(PartAnim.Type.Toggle, speed, ToggleState(key));
            }
            foreach (var pc in panel)
            {
                if (!controls.Exists(c => c.Key == pc[0])) AddDeco(pc[0], pc[1], pc[2]);
                if (pc[3] != null) verbs[pc[0]] = pc[3].Replace('_', ' ');
            }
            controls.RemoveAll(c => !at.ContainsKey(c.Key));
            foreach (var c in controls)
            {
                c.At = at[c.Key];
                c.Grip = null;
                c.Moving = null;
                c.Angle = null;
                c.Spring = c.Key.StartsWith("d_");   // a panel switch only when looked at, not from the one beside it
            }

            Vector3 v;
            float turn = DoorTurn[MscTardis.Data.Room];
            if (turn != 0f && at.TryGetValue("doors", out v))
            {
                // The door frame (and the walkway on its top) turn with the doorway, and so does the arrival point in front of it. The walk
                // collider also holds the platform's glass panes, which stay where they are drawn: only its part round the door frame turns.
                // The other parts lying wholly by the doors (ByDoors) each plug a hole the shape of itself: the 2010 room's doormat one in the
                // floor, the top of its doorway one in the ceiling over it. Left behind, they showed askew through the open doors with a gap
                // at the mat's corner; turned, a corner of each hole opened onto the void under the room. So each stays, and a copy turned
                // with the frame lies over it, 2 mm towards its face (with its collider too, if it has one).
                var pivot = Root.TransformPoint(v);
                var turned = new List<Transform>();
                var plugs = new List<Transform>();
                foreach (Transform c in Root)
                {
                    if (c.name == "a_doors_static") { turned.Add(c); continue; }
                    if (ByDoors(c, v)) { plugs.Add(c); continue; }
                    var mc = c.GetComponent<MeshCollider>();
                    if (mc == null || mc.sharedMesh == null || mc.sharedMesh.name != prefix + "_walk") continue;
                    var doorBox = new Bounds(v, new Vector3(4f, 8f, 4f));   // the collider is in room space; the nearest pane is 4 m from the doors
                    var walkway = Res.Cut(mc.sharedMesh, doorBox, true);
                    if (walkway == mc.sharedMesh) { turned.Add(c); continue; }
                    mc.sharedMesh = Res.Cut(mc.sharedMesh, doorBox, false);
                    if (walkway.triangles.Length == 0) continue;
                    var wg = new GameObject("WalkSolidDoor");
                    wg.transform.SetParent(Root, false);
                    wg.AddComponent<MeshCollider>().sharedMesh = walkway;
                    turned.Add(wg.transform);
                }
                foreach (var c in plugs)
                {
                    var mesh = c.GetComponent<MeshFilter>().sharedMesh;
                    var vs = mesh.vertices; var ts = mesh.triangles;   // its facing from its triangles (the model files have no normals)
                    var facing = Vector3.zero;
                    for (int k = 0; k + 2 < ts.Length; k += 3) facing += Vector3.Cross(vs[ts[k + 1]] - vs[ts[k]], vs[ts[k + 2]] - vs[ts[k]]);
                    var copy = Res.MeshObject(c.name, mesh, c.GetComponent<Renderer>().sharedMaterial, Root);
                    copy.transform.localPosition = facing.normalized * 0.002f;
                    if (c.GetComponent<MeshCollider>() != null) copy.AddComponent<MeshCollider>().sharedMesh = mesh;
                    turned.Add(copy.transform);
                    DressAsSurround(c, mesh);   // where the copy leaves a corner of the plug showing, it looks like what is round the hole
                }
                foreach (var tr in turned) tr.RotateAround(pivot, Vector3.up, turn);
                doorYaw += turn;
                var spin = Quaternion.Euler(0f, turn, 0f);
                entry = v + spin * (entry - v);
                entryYaw += turn;
            }
            var trigger = new GameObject("DoorTrigger");
            trigger.transform.SetParent(Root, false);
            trigger.transform.localPosition = at.TryGetValue("doors", out v) ? v : new Vector3(0f, 1.15f, -8.3f);
            doorPoint = trigger.transform.localPosition;
            TardisInterior.doorYaw = doorYaw;
            trigger.transform.localRotation = Quaternion.Euler(0f, doorYaw, 0f);
            var bc = trigger.AddComponent<BoxCollider>();
            bc.size = new Vector3(1.5f, 2.3f, 0.3f);
            bc.isTrigger = true;
            doorCollider = bc;

            // On the screen itself. The text starts at its top left, seen from the front.
            var face = monitorNormal.normalized;
            screenAt = at["monitor"];
            screenFace = face;
            TardisInterior.screenW = screenW;
            TardisInterior.screenH = screenH;
            
            monitor.characterSize *= 0.85f * Mathf.Min(screenW / 0.60f, screenH / 0.31f);   // eight lines in flight, on a screen that fits seven at the custom room's size
            monitor.transform.localRotation = Quaternion.LookRotation(-face, Vector3.up);
            monitor.transform.localPosition = at["monitor"] + face * 0.015f + monitor.transform.up * (screenH * 0.45f) - monitor.transform.right * (screenW * 0.45f);
        }

        // The picture on the monitor: the terminal's texture, fitted at 4:3 inside the screen, over a black backing.
        static void BuildScreen()
        {
            float h = Mathf.Min(screenH, screenW * 3f / 4f), w = h * 4f / 3f;
            float lift = MscTardis.Data.Room == 0 ? 0.030f : 0.014f;   // the custom monitor's glass is 0.028 in front of MonitorAt, under its post-it
            var face = screenFace.normalized;
            var rot = Quaternion.LookRotation(-face, Vector3.up);
            screenMat = Res.RoomMat("white.png", Res.RoomMode.Lamp, Color.white);
            screenBack = Res.MeshObject("MonitorBack", ScreenQuad(screenW, screenH), Res.RoomMat("white.png", Res.RoomMode.Lamp, Color.black), Root);
            screenBack.transform.localPosition = screenAt + face * (lift - 0.001f);
            screenBack.transform.localRotation = rot;
            screenPic = Res.MeshObject("MonitorPicture", ScreenQuad(w, h), screenMat, Root);
            screenPic.transform.localPosition = screenAt + face * lift;
            screenPic.transform.localRotation = rot;
            // The scanner's live picture, over its block of cells.
            var cells = new Rect(3, 5, 50, 12);
            screenOverMat = Res.RoomMat("white.png", Res.RoomMode.Lamp, Color.white);
            screenOver = Res.MeshObject("MonitorScanner", ScreenQuad(1f, 1f), screenOverMat, screenPic.transform);
            screenOver.transform.localPosition = new Vector3(((cells.x + cells.width / 2f) / Terminal.Cols - 0.5f) * w, (0.5f - (cells.y + cells.height / 2f) / Terminal.Rows) * h, -0.0005f);
            screenOver.transform.localScale = new Vector3(w * cells.width / Terminal.Cols, h * cells.height / Terminal.Rows, 1f);
            screenOver.SetActive(false);
            screenBack.SetActive(false);
            screenPic.SetActive(false);
        }

        static Mesh ScreenQuad(float w, float h)
        {
            var m = new Mesh { name = "ScreenQuad" };
            m.vertices = new[] { new Vector3(-w / 2f, -h / 2f, 0f), new Vector3(w / 2f, -h / 2f, 0f), new Vector3(w / 2f, h / 2f, 0f), new Vector3(-w / 2f, h / 2f, 0f) };
            m.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };
            m.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            m.RecalculateBounds();
            return m;
        }

        // The 2010 console's keys on the coral console, on a case built here, lying on the plate under the monitor. Their baked light
        // is that room's orange: here it is turned to a neutral grey shaded by the face's direction. Looking at it is looking at the computer.
        static void BuildKeyboard()
        {
            keyboardCollider = null;
            if (MscTardis.Data.Room != 0) return;
            // The 2010 room's selector keyboard, by its part name: its file number changes whenever that room is converted again.
            string file = null;
            foreach (var line in Res.Lines("t10.txt"))
            {
                var f = line.Split(' ');
                if (f.Length > 1 && f[1] == "a_selector_static") { file = f[0]; break; }
            }
            if (file == null || !Res.Has(file)) return;
            var src = Res.Dwm(file);
            var mesh = UnityEngine.Object.Instantiate(src);
            mesh.name = "coral_keyboard";
            var nrm = mesh.normals;
            var cols = new Color32[mesh.vertexCount];
            for (int i = 0; i < cols.Length; i++)
            {
                float sh = nrm.Length == cols.Length ? Mathf.Clamp01(0.8f + 0.15f * nrm[i].y) : 0.5f;
                byte g = (byte)(sh * 255f);
                cols[i] = new Color32(g, g, g, 255);
            }
            mesh.colors32 = cols;

            // The frame the case lies in: x along its width, y out of its top, z towards the monitor.
            float tilt = KeyboardTilt * Mathf.Deg2Rad;
            var keysUp = new Vector3(0f, Mathf.Cos(tilt), -Mathf.Sin(tilt));
            var keysBack = new Vector3(0f, Mathf.Sin(tilt), Mathf.Cos(tilt));
            var plane = Quaternion.LookRotation(keysBack, keysUp);
            // The same frame in the model: its keys face KbTop with KbBack as the far edge. Turned so, the keys are level with the case's top.
            var back = (KbBack - KbTop * Vector3.Dot(KbBack, KbTop)).normalized;
            var toPlane = Quaternion.Inverse(Quaternion.LookRotation(back, KbTop));
            var verts = src.vertices;
            var fit = new Bounds(toPlane * verts[0], Vector3.zero);
            foreach (var v in verts) fit.Encapsulate(toPlane * v);
            float caseW = fit.size.x + CaseMargin, caseD = fit.size.z + CaseMargin;
            // The case follows the keys' outline: their convex hull in the case's plane, grown by the margin and rounded off.
            var hull = KeyHull(verts, toPlane, fit.center, CaseMargin);

            var root = new GameObject("CoralKeyboard");
            root.transform.SetParent(Root, false);
            var frame = new GameObject("KeyboardCase").transform;
            frame.SetParent(root.transform, false);
            frame.localRotation = plane;
            frame.localPosition = KeyboardJoin - plane * new Vector3(0f, 0f, caseD / 2f);
            // The keys, their lowest point just inside the case's top face.
            var keys = Res.MeshObject("KeyboardKeys", mesh, Res.RoomMat("t10_details.png", Res.RoomMode.Baked, Color.white), frame);
            keys.transform.localRotation = toPlane;
            keys.transform.localPosition = new Vector3(-fit.center.x, -fit.min.y - 0.002f, -fit.center.z);
            // The case: a slab under them cut to that outline, silver like the monitor.
            var silver = Res.RoomMat("silver.png", Res.RoomMode.Baked, Color.white);
            var slab = Res.MeshObject("KeyboardSlab", CaseMesh(hull, CaseThick), silver, frame);
            slab.transform.localPosition = Vector3.zero;
            var bc = frame.gameObject.AddComponent<BoxCollider>();
            bc.isTrigger = true;
            bc.center = new Vector3(0f, 0.02f - CaseThick / 2f, 0f);
            bc.size = new Vector3(caseW + 0.04f, CaseThick + 0.08f, caseD + 0.04f);
            keyboardCollider = bc;
        }

        // The convex hull of the model's vertices in the case plane (x, z about the centre), pushed out by margin and rounded; counter-clockwise.
        static List<Vector2> KeyHull(Vector3[] verts, Quaternion toPlane, Vector3 centre, float margin)
        {
            var pts = new List<Vector2>(verts.Length);
            foreach (var v in verts) { var q = toPlane * v - centre; pts.Add(new Vector2(q.x, q.z)); }
            pts.Sort((p, q) => p.x != q.x ? p.x.CompareTo(q.x) : p.y.CompareTo(q.y));
            var h = new List<Vector2>();
            for (int pass = 0; pass < 2; pass++)
            {
                int start = h.Count;
                for (int i = 0; i < pts.Count; i++)
                {
                    var p = pts[pass == 0 ? i : pts.Count - 1 - i];
                    while (h.Count >= start + 2 && Cross(h[h.Count - 2], h[h.Count - 1], p) <= 0f) h.RemoveAt(h.Count - 1);
                    h.Add(p);
                }
                h.RemoveAt(h.Count - 1);
            }
            // Grow each corner along its mitre.
            var grown = new List<Vector2>();
            for (int i = 0; i < h.Count; i++)
            {
                var prev = h[(i + h.Count - 1) % h.Count]; var cur = h[i]; var next = h[(i + 1) % h.Count];
                var e1 = (cur - prev).normalized; var e2 = (next - cur).normalized;
                var n1 = new Vector2(e1.y, -e1.x); var n2 = new Vector2(e2.y, -e2.x);
                var m = (n1 + n2) / Mathf.Max(0.5f, 1f + Vector2.Dot(n1, n2));
                grown.Add(cur + m * margin);
            }
            // Round the corners: cut each one twice.
            for (int pass = 0; pass < 2; pass++)
            {
                var cut = new List<Vector2>();
                for (int i = 0; i < grown.Count; i++)
                {
                    var a = grown[i]; var b = grown[(i + 1) % grown.Count];
                    cut.Add(Vector2.Lerp(a, b, 0.25f)); cut.Add(Vector2.Lerp(a, b, 0.75f));
                }
                grown = cut;
            }
            return grown;
        }

        static float Cross(Vector2 o, Vector2 a, Vector2 b) { return (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x); }

        // A slab of the given thickness: top face at y = 0 and sides down to -thick, over a counter-clockwise outline in (x, z).
        static Mesh CaseMesh(List<Vector2> outline, float thick)
        {
            int n = outline.Count;
            var vs = new List<Vector3>(); var uv = new List<Vector2>(); var tris = new List<int>();
            var c = Vector2.zero; foreach (var p in outline) c += p; c /= n;
            vs.Add(new Vector3(c.x, 0f, c.y)); uv.Add(c * 2f);
            for (int i = 0; i < n; i++) { vs.Add(new Vector3(outline[i].x, 0f, outline[i].y)); uv.Add(outline[i] * 2f); }
            for (int i = 0; i < n; i++) { tris.Add(0); tris.Add(1 + (i + 1) % n); tris.Add(1 + i); }   // seen from above, clockwise
            for (int i = 0; i < n; i++)
            {
                var a = outline[i]; var b = outline[(i + 1) % n];
                int k = vs.Count;
                vs.Add(new Vector3(a.x, 0f, a.y)); vs.Add(new Vector3(b.x, 0f, b.y));
                vs.Add(new Vector3(b.x, -thick, b.y)); vs.Add(new Vector3(a.x, -thick, a.y));
                uv.Add(a * 2f); uv.Add(b * 2f); uv.Add(b * 2f); uv.Add(a * 2f);
                tris.AddRange(new[] { k, k + 1, k + 2, k, k + 2, k + 3 });
            }
            var m = new Mesh { name = "keyboard_case" };
            m.vertices = vs.ToArray(); m.uv = uv.ToArray(); m.triangles = tris.ToArray();
            var grey = new Color32[vs.Count];
            for (int i = 0; i < grey.Length; i++) grey[i] = new Color32(120, 120, 120, 255);
            m.colors32 = grey;
            m.RecalculateNormals(); m.RecalculateBounds();
            return m;
        }

        // What a lamp shows. Its role is a control's key (that control's state, or its lamp's own rule: the two indicators), or a group the converter
        // names: the keypad's lamps (a code is being asked for, or a key is down), the selector's (one for each of its positions), the two rows of eight
        // on the rim (hull and stability, as bars), the nudge knobs' (they flash when one is used), the throttle's (ready to take off), and ambient (just the power).
        static Func<bool> LampState(string role)
        {
            int n;
            switch (role)
            {
                case "ambient": return () => true;
                case "keypad": return () => Protocols.Current != null || KeyDown();
                case "ready": return () => Tardis.State != Tardis.Phase.Idle || Takeoff.Missing() == null;   // the throttle's lamps: nothing left to do before take-off
                case "sel0": return () => MscTardis.Data.Selector == 0;
                case "sel1": return () => MscTardis.Data.Selector == 1;
                case "sel2": return () => MscTardis.Data.Selector == 2;
                case "sel3": return () => MscTardis.Data.Selector >= Tardis.Places.Length;   // MANUAL, or one of the telepathic jump's extras
                case "spin": return () => MscTardis.Data.SpinDir != 0;
                // The two indicators, where a room has lamps for them but no INDICATOR control of its own (the 2010 rail).
                case "d_sbl1": return () => Subsystems.AnyFault;
                case "d_sbl2": return () => !Subsystems.MasterOn;
                // The alarm lamps (the 2010 console's red lamps): flashing while a subsystem is at fault, or the flight is coming apart.
                case "alarm": return () => (Subsystems.AnyFault || (Tardis.State != Tardis.Phase.Idle && Tardis.Stability < 0.35f)) && (int)(Time.time * 3f) % 2 == 0;
            }
            if (role.StartsWith("dot_"))
            {
                // The little red lamp on a button: lit with the control, and blinking while the flight minigame asks for it.
                var key = role.Substring(4);
                var own = LampState(key);
                return () => own() || (FlightGame.Prompt == key && (int)(Time.time * 5f) % 2 == 0);
            }
            if (role.StartsWith("hull") && int.TryParse(role.Substring(4), out n)) return () => Systems.HealthPercent > (n - 1) * 12.5f;
            if (role.StartsWith("stab") && int.TryParse(role.Substring(4), out n)) return () => Tardis.Stability * 8f > n - 1;
            if (role.StartsWith("d_bp")) { var k = role; return () => Pulse(k) > 0f; }
            var c = controls.Find(k => k.Key == role);
            if (c != null && c.LitWhen != null) return c.LitWhen;
            // A control's own lamp: its state, and blinking while the flight minigame or a protocol challenge asks for that control.
            var state = ToggleState(role);
            return () => state() || ((FlightGame.Prompt == role || flashing.Contains(role)) && (int)(Time.time * 5f) % 2 == 0);
        }

        // Indicator lamps the room's model doesn't have: small domes on a bezel, from <prefix>_lamps.txt (written by convert_source.py for the
        // 2010 room, by coral_lamps.py for the coral room). A line is "role x y z nx ny nz rrggbb [radius mm]", in room space, the normal pointing
        // out of the surface the lamp sits on. Each is a lamp of that role (LampState), lit in its colour, dark glass otherwise.
        static void BuildIndicators(string prefix)
        {
            string file = prefix + "_lamps.txt";
            if (!Res.Has(file)) return;
            var bezel = Res.RoomMat("white.png", Res.RoomMode.Lamp, new Color(0.035f, 0.032f, 0.03f));
            var lit = new Dictionary<string, Material>();
            var dark = new Dictionary<string, Material>();
            var meshes = new Dictionary<int, Mesh[]>();
            foreach (var line in Res.Lines(file))
            {
                var f = line.Trim().Split(' ');
                if (f.Length < 8 || f[0].StartsWith("#")) continue;
                string role = f[0], hex = f[7];
                var at = new Vector3(G.F(f[1]), G.F(f[2]), G.F(f[3]));
                var n = new Vector3(G.F(f[4]), G.F(f[5]), G.F(f[6])).normalized;
                int mm = f.Length > 8 ? int.Parse(f[8]) : 4;
                Material on, off;
                if (!lit.TryGetValue(hex, out on))
                {
                    Func<int, float> ch = i => System.Convert.ToInt32(hex.Substring(i, 2), 16) / 255f;
                    var col = new Color(ch(0), ch(2), ch(4));
                    lit[hex] = on = Res.RoomMat("white.png", Res.RoomMode.Lamp, col * 0.62f);   // the lamp shader doubles it
                    dark[hex] = Res.RoomMat("white.png", Res.RoomMode.Lamp, Color.Lerp(col, new Color(0.25f, 0.22f, 0.2f), 0.55f) * 0.16f);
                }
                off = dark[hex];
                Mesh[] m;
                if (!meshes.TryGetValue(mm, out m)) meshes[mm] = m = new[] { LampDome(mm / 1000f), LampBezel(mm / 1000f * 1.45f) };
                var holder = new GameObject("Indicator_" + role).transform;
                holder.SetParent(Root, false);
                holder.localPosition = at;
                holder.localRotation = Quaternion.FromToRotation(Vector3.up, n);
                Res.MeshObject("Bezel", m[1], bezel, holder);
                var go = Res.MeshObject("Glass", m[0], off, holder);
                RoomLamp rl;
                if (!roomLamps.TryGetValue(role, out rl)) roomLamps[role] = rl = new RoomLamp();
                rl.R.Add(go.GetComponent<Renderer>()); rl.On.Add(on); rl.Off.Add(off);
            }
        }

        // A lamp's glass: a low dome of radius r on the y axis, standing on y = 0.
        static Mesh LampDome(float r)
        {
            const int segs = 12, rings = 4;
            var v = new List<Vector3>();
            var t = new List<int>();
            v.Add(new Vector3(0f, r * 0.7f + 0.0015f, 0f));
            for (int i = 1; i <= rings; i++)
            {
                float th = i * Mathf.PI / 2f / rings;
                for (int k = 0; k < segs; k++)
                {
                    float ph = k * Mathf.PI * 2f / segs;
                    v.Add(new Vector3(Mathf.Sin(th) * r * Mathf.Cos(ph), Mathf.Cos(th) * r * 0.7f + 0.0015f, Mathf.Sin(th) * r * Mathf.Sin(ph)));
                }
            }
            for (int k = 0; k < segs; k++) { t.Add(0); t.Add(1 + (k + 1) % segs); t.Add(1 + k); }
            for (int i = 1; i < rings; i++)
            {
                int lo = 1 + (i - 1) * segs, hi = 1 + i * segs;
                for (int k = 0; k < segs; k++)
                {
                    int k2 = (k + 1) % segs;
                    t.Add(lo + k); t.Add(lo + k2); t.Add(hi + k);
                    t.Add(hi + k); t.Add(lo + k2); t.Add(hi + k2);
                }
            }
            var m = new Mesh { vertices = v.ToArray(), triangles = t.ToArray(), uv = new Vector2[v.Count] };
            m.RecalculateNormals(); m.RecalculateBounds();
            return m;
        }

        // A lamp's bezel: a flat ring's top and a short wall, radius r, 1.5 mm proud of the surface.
        static Mesh LampBezel(float r)
        {
            const int segs = 12;
            var v = new List<Vector3>();
            var t = new List<int>();
            v.Add(new Vector3(0f, 0.0015f, 0f));
            for (int k = 0; k < segs; k++)
            {
                float ph = k * Mathf.PI * 2f / segs;
                v.Add(new Vector3(r * Mathf.Cos(ph), 0.0015f, r * Mathf.Sin(ph)));
            }
            for (int k = 0; k < segs; k++)
            {
                float ph = k * Mathf.PI * 2f / segs;
                v.Add(new Vector3(r * Mathf.Cos(ph), -0.001f, r * Mathf.Sin(ph)));
            }
            for (int k = 0; k < segs; k++)
            {
                int k2 = (k + 1) % segs;
                t.Add(0); t.Add(1 + k2); t.Add(1 + k);
                t.Add(1 + k); t.Add(1 + k2); t.Add(1 + segs + k);
                t.Add(1 + segs + k); t.Add(1 + k2); t.Add(1 + segs + k2);
            }
            var m = new Mesh { vertices = v.ToArray(), triangles = t.ToArray(), uv = new Vector2[v.Count] };
            m.RecalculateNormals(); m.RecalculateBounds();
            return m;
        }


        // A keypad key was pressed within the last moment.
        static bool KeyDown()
        {
            foreach (var kv in usedAt) if (Time.time - kv.Value < 0.35f && Protocols.KeyFor(kv.Key) != null) return true;
            return false;
        }

        static Func<bool> ToggleState(string key)
        {
            switch (key)
            {
                case "handbrake": return () => MscTardis.Data.Handbrake;
                case "power": return () => MscTardis.Data.Power;
                case "power.cover": return () => CoverUp;   // lifted: where the room has the guard (the coral console's is its own model)
                case "takeoff": return () => Tardis.State != Tardis.Phase.Idle;
                case "door": return () => MscTardis.Data.DoorHeldOpen;
                case "lock": return () => MscTardis.Data.DoorLocked;
                case "hads": return () => Subsystems.Board ? MscTardis.Data.Autopilot : MscTardis.Data.Hads;   // the AUTOPILOT switch where there is a board
                case "flight": return () => Flight.On;
                case "float": return () => Flight.FloatOn;
                case "pilot": return () => Pilot.Active;
                case "d_sbm1": return () => Subsystems.MasterOn;
            }
            if (HasKeypad && Protocols.KeyFor(key) != null) return () => Pulse(key) > 0f;   // a keypad key dips while it is pressed
            int n;
            if (key.StartsWith("d_sb") && int.TryParse(key.Substring(4), out n) && n >= 1 && n <= 16)
            {
                // CIRCUIT 1-6 and BREAKER 1-6 are the subsystems', CIRCUIT 7-10 and the rest of the board's switches keep their own state.
                if (n <= Subsystems.Count) return () => Subsystems.CircuitOn(n - 1);
                if (n >= 11 && n - 11 < Subsystems.Count) return () => Subsystems.BreakerOn(n - 11);
            }
            if (key.StartsWith("d_")) return () => decoOn.Contains(key);
            if (key.StartsWith("aux")) return () => auxOn.Contains(key);   // the coral console's two spare switches by FLIGHT
            return () => false;
        }

        // The other room, built afresh. Only while nothing is going on, so nothing is left pointing at the old one.
        public static string SetRoom(int n)
        {
            if (Tardis.Busy) return "NOT WHILE IN FLIGHT";
            if (Pilot.Active) return "LEAVE THE CONTROLS FIRST";
            Computer.Close();
            if (n < 0 || n >= Rooms.Length) return "no room " + n;
            if (n != 0 && !ModOptions.BetaConsoles)
            {
                if (MscTardis.Data.Room == 0) return "BETA CONSOLES ARE OFF (MOD SETTINGS)";
                n = 0;   // switched off while in one: the button leads back to the coral console
            }
            MscTardis.Data.Room = n;
            UnityEngine.Object.Destroy(Root.gameObject);
            Build();
            if (MscTardis.Data.Inside) PlacePlayer();
            Guidebook.RoomChanged();
            return null;
        }

        // Where the guidebook lives in each room (room-local, xz; y is where the downward look starts, less 2 m). The custom room's is on the
        // inner slope of the rim's shoulder, between r 1.24 and 1.36, a 12 cm run at 7 degrees that the 10.8 cm book lies flush on, long side
        // along the rim, by the bay that faces the doors; on the rim's peak it would only balance. The Toyota console's is the recess in the middle
        // of its front panel (14 degrees), clear of the door and lock switches. The 2010 console has nowhere flat that isn't inside it (its wings
        // slope 24 degrees, its display 25, and its control panel at r 1.2 stands 1 m above the floor, among the controls): the book lies on the
        // platform floor just outside the console, 1.6 m out on the side that faces the doors (room-local 136 degrees; the floor there is flat to r 2.4).
        static readonly Vector3[] bookSpots =
        {
            new Vector3(Mathf.Cos(-80f * Mathf.Deg2Rad) * 1.30f, 1.24f, Mathf.Sin(-80f * Mathf.Deg2Rad) * 1.30f),
            new Vector3(0f, 1.2f, -1.2f),
            new Vector3(-1.15f, 1.2f, 1.11f),
        };

        public static void BookHome(out Vector3 at, out Quaternion rot) { BookSpot(bookSpots[Mathf.Clamp(MscTardis.Data.Room, 0, bookSpots.Length - 1)], out at, out rot); }

        // On whatever is under room-local (x, z), from above `local.y` (lets a spot be tried without a rebuild).
        public static void BookSpot(Vector3 local, out Vector3 at, out Quaternion rot)
        {
            var flat = new Vector3(local.x, 0f, local.z).normalized;
            // Long side round the room (book z along the tangent), spine (-x) towards the centre.
            rot = Root.rotation * Quaternion.LookRotation(new Vector3(-flat.z, 0f, flat.x), Vector3.up);
            at = Root.TransformPoint(local);
            float best = float.MaxValue;
            foreach (var h in Physics.RaycastAll(Root.TransformPoint(new Vector3(local.x, local.y + 2f, local.z)), Vector3.down, 4f))
                if (!h.collider.isTrigger && h.collider.transform.IsChildOf(Root) && h.distance < best) { best = h.distance; at = h.point; }
            at += Vector3.up * 0.016f;   // the book's half thickness and a little more, to settle
        }

        // A world point in the space the rooms hang in (a sphere round them, well clear of the map 500 m below).
        public static bool InSpace(Vector3 p) { return Root != null && Root.InverseTransformPoint(p).magnitude < 150f; }

        // A thing that has fallen out of the room: through the floor, or out past the walls.
        public static bool Fallen(Vector3 p)
        {
            if (!InSpace(p)) return false;
            var local = Root.InverseTransformPoint(p);
            return local.y < keepFloor - 1f || new Vector2(local.x, local.z).magnitude > keepRadius + 3f;
        }

        static void TickToyotaParts()
        {
            float dt = Time.deltaTime;
            bool travelling = Tardis.State != Tardis.Phase.Idle || Flight.On;
            foreach (var kv in anims) kv.Value.Tick(dt, MscTardis.Data.Power, travelling, false);
            foreach (var a in animParts)
            {
                PartAnim anim;
                if (!anims.TryGetValue(a.Key, out anim)) continue;
                float at = anim.Pos * (a.P.Length - 1);
                int i = Mathf.Min((int)at, a.P.Length - 2);
                float f = at - i;
                a.T.localPosition = Vector3.Lerp(a.P[i], a.P[i + 1], f);
                a.T.localRotation = Quaternion.Slerp(a.Q[i], a.Q[i + 1], f);
            }
        }

        static Transform Pivot(string name, Vector3 at)
        {
            var t = new GameObject(name).transform;
            t.SetParent(Root, false);
            t.localPosition = at;
            return t;
        }

        static void Solid(string name, Vector3 at, Quaternion rot, Vector3 size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(Root, false);
            go.transform.localPosition = at;
            go.transform.localRotation = rot;
            go.AddComponent<BoxCollider>().size = size;
        }

        static void BuildColliders()
        {
            float width = 2f * Apothem * Mathf.Tan(Mathf.PI / Sides) + 1f;
            Solid("FloorSolid", new Vector3(0f, -0.5f, 0f), Quaternion.identity, new Vector3(20f, 1f, 20f));
            Solid("RoofSolid", new Vector3(0f, 8.3f, 0f), Quaternion.identity, new Vector3(20f, 1f, 20f));
            for (int i = 0; i < Sides; i++)
            {
                float a = (-90f + i * 360f / Sides) * Mathf.Deg2Rad;
                var out_ = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                if (i == 0)
                {
                    // The door's side: the wall either side of the doorway and over it. The doorway's own walking room (BuildDoorway)
                    // closes the gap.
                    float half = width / 2f, gap = 0.9f, over = 2.6f;
                    Solid("WallSolid", new Vector3(-(half + gap) / 2f, 4f, -Apothem - 0.5f), Quaternion.identity, new Vector3(half - gap, 8.6f, 1f));
                    Solid("WallSolid", new Vector3((half + gap) / 2f, 4f, -Apothem - 0.5f), Quaternion.identity, new Vector3(half - gap, 8.6f, 1f));
                    Solid("WallSolid", new Vector3(0f, (over + 8.3f) / 2f, -Apothem - 0.5f), Quaternion.identity, new Vector3(gap * 2f, 8.3f - over, 1f));
                    continue;
                }
                Solid("WallSolid", out_ * (Apothem + 0.5f) + Vector3.up * 4f, Quaternion.LookRotation(out_), new Vector3(width, 8.6f, 1f));
            }
            // The wishbone pillars, as one slab each: nobody needs to walk between the legs.
            for (int i = 0; i < 6; i++)
            {
                float a = i * 60f * Mathf.Deg2Rad;
                var out_ = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                Solid("PillarSolid", out_ * 5.35f + Vector3.up * 2f, Quaternion.LookRotation(out_), new Vector3(2.7f, 4f, 0.8f));
            }
            // The console: turned to its own profile (build_interior.py: the rim, the decks, the saucers, the column), not a tall capsule, so
            // a thing put down on it (the guidebook) rests on the rim's shoulder or a deck. Round the outside it is as wide as the capsule was.
            var console = new GameObject("ConsoleSolid");
            console.transform.SetParent(Root, false);
            console.AddComponent<MeshCollider>().sharedMesh = LatheMesh(new[]
            {
                new Vector2(1.52f, 0f), new Vector2(1.52f, 1.00f), new Vector2(1.50f, 1.14f), new Vector2(1.445f, 1.19f), new Vector2(1.36f, 1.215f),
                new Vector2(1.24f, 1.20f), new Vector2(1.20f, 1.15f),   // over the rim's shoulder and down onto the deck
                new Vector2(0.74f, 1.351f), new Vector2(0.74f, 1.52f), new Vector2(0.60f, 1.66f), new Vector2(0.37f, 1.80f),   // up the deck, the saucers
                new Vector2(0.37f, 3.64f), new Vector2(0f, 3.64f),   // the column
            }, 48);
            // The captain's seat.
            var seat = new GameObject("SeatSolid");
            seat.transform.SetParent(Root, false);
            seat.transform.localPosition = new Vector3(2.75f, 1f, 0.45f);
            var sc = seat.AddComponent<CapsuleCollider>();
            sc.height = 2.4f;
            sc.radius = 0.62f;
            // The walkway from the doors, sloping up to the platform, and its two rails.
            var from = new Vector3(0f, 0f, -Apothem + 0.1f);
            var to = new Vector3(0f, PlatformZ, -PlatformR + 0.02f);
            var rot = Quaternion.LookRotation(to - from);
            Solid("WalkwaySolid", (from + to) / 2f - rot * Vector3.up * 0.1f, rot, new Vector3(1.76f, 0.2f, (to - from).magnitude + 0.2f));
            foreach (var x in new[] { -0.9f, 0.9f })
                Solid("RailSolid", new Vector3(x, 1f, -5.5f), Quaternion.identity, new Vector3(0.1f, 2f, 2.2f));
            // The platform: a gentle bevel all round its edge, so the player walks up rather than catching on the step.
            var plat = new GameObject("PlatformSolid");
            plat.transform.SetParent(Root, false);
            plat.AddComponent<MeshCollider>().sharedMesh = PlatformMesh();
        }

        // A solid of revolution about the room's y axis: profile (radius, height) from the foot up and over, faces out.
        static Mesh LatheMesh(Vector2[] profile, int n)
        {
            var v = new Vector3[profile.Length * n];
            for (int i = 0; i < profile.Length; i++)
                for (int j = 0; j < n; j++)
                {
                    float a = j * 2f * Mathf.PI / n;
                    v[i * n + j] = new Vector3(profile[i].x * Mathf.Cos(a), profile[i].y, profile[i].x * Mathf.Sin(a));
                }
            var tris = new List<int>();
            for (int i = 0; i + 1 < profile.Length; i++)
            {
                var d = profile[i + 1] - profile[i];
                var n2 = new Vector2(d.y, -d.x);   // the profile's outward normal (radius, height)
                for (int j = 0; j < n; j++)
                {
                    int a = i * n + j, b = i * n + (j + 1) % n, c = (i + 1) * n + (j + 1) % n, e = (i + 1) * n + j;
                    var mid = (v[a] + v[c]) * 0.5f;
                    var radial = new Vector3(mid.x, 0f, mid.z).normalized;
                    var outward = radial * n2.x + Vector3.up * n2.y;
                    bool flip = Vector3.Dot(Vector3.Cross(v[b] - v[a], v[c] - v[a]), outward) < 0f;
                    if (flip) tris.AddRange(new[] { a, c, b, a, e, c });
                    else tris.AddRange(new[] { a, b, c, a, c, e });
                }
            }
            var m = new Mesh { name = "ConsoleLathe", vertices = v, triangles = tris.ToArray() };
            m.RecalculateBounds();
            return m;
        }

        static Mesh PlatformMesh()
        {
            const int n = 32;
            float foot = PlatformR + 0.7f;
            var v = new Vector3[n * 2 + 1];
            var tris = new List<int>();
            v[n * 2] = new Vector3(0f, PlatformZ, 0f);
            for (int i = 0; i < n; i++)
            {
                float a = i * 2f * Mathf.PI / n, c = Mathf.Cos(a), s = Mathf.Sin(a);
                v[i] = new Vector3(c * PlatformR, PlatformZ, s * PlatformR);
                v[n + i] = new Vector3(c * foot, 0f, s * foot);
                int j = (i + 1) % n;
                tris.AddRange(new[] { n * 2, j, i, i, j, n + j, i, n + j, n + i });
            }
            var m = new Mesh { name = "MscTardisPlatformSolid", vertices = v, triangles = tris.ToArray() };
            m.RecalculateBounds();
            return m;
        }

        static Light RoomLight(Vector3 at, float range, float intensity, Color c)
        {
            var go = new GameObject("RoomLight");
            go.transform.SetParent(Root, false);
            go.transform.localPosition = at;
            var l = go.AddComponent<Light>();
            l.type = LightType.Point;
            l.range = range;
            l.intensity = intensity;
            l.color = c;
            l.shadows = LightShadows.None;
            return l;
        }

        // Whether this room has a control by this key: the coral console's are listed in tardis_controls.txt, a converted room's in its own file.
        public static bool HasKeypad;   // this room's keypad is there, so a key in the coral console's names (x-, z+, ...) is a key only here

        static bool RoomHas(string key)
        {
            var d = MscTardis.Data;
            if (d.Room == 0) return true;   // the coral console has them all
            foreach (var line in Res.Lines(RoomFiles[d.Room] + "_controls.txt"))
                if (line.StartsWith(key + " ")) return true;
            return false;
        }

        // What each control does. Where it stands, what it looks like and how it moves is the room's business: the custom room's controls are
        // modelled and placed in tools/assets/build_interior.py (tardis_controls.txt), the converted rooms' come with their models.
        static void BuildConsole()
        {
            rotorLight = RoomLight(new Vector3(0f, 2.4f, 0f), 7f, 0.6f, new Color(0.45f, 1f, 0.65f));
            monitor = Res.Text("MonitorText", Root, MonitorAt + new Vector3(-0.235f, 0.185f, -0.035f), "", 0.0058f, new Color(0.82f, 0.96f, 1f), TextAnchor.UpperLeft);
            usedAt.Clear();
            stabTurn = 0f;

            // Navigation: the fin knob and the knurled knob on the strip, and four keys of the keypad.
            Add("selector", () => "DESTINATION: " + SelectorName(), () => CycleSelector(1), () => SelectorSeq().IndexOf(MscTardis.Data.Selector) * 60f);
            Add("step", () => "COORDINATE STEP: " + Steps[MscTardis.Data.StepIndex] + " M",
                () => { var d = MscTardis.Data; d.StepIndex = (d.StepIndex + 1) % Steps.Length; },
                () => MscTardis.Data.StepIndex * 90f);
            // The keypad: all 16 keys are for entry (protocol IDs). The X/Z nudges are four knobs (on the coral console, the second black
            // plate). A room without a keypad or switch board yet (Phase 7 round 7: the Toyota and 2010 consoles get them as their models do)
            // has the four buttons still nudge, HADS and FAST RETURN keep their own controls, and the subsystem breakers can't trip.
            // A room has the new controls when its own list has them: the coral console always, a converted room as its model gets them.
            bool board = RoomHas("d_sbm1"), keypad = RoomHas("d_kp1");
            Subsystems.Board = board;
            HasKeypad = keypad;
            if (keypad)
                foreach (var kc in Protocols.KeypadControls)
                {
                    var ck = kc;
                    var ch = Protocols.KeyFor(ck);
                    Add(ck, () => "KEY " + ch + (Protocols.Entry.Length > 0 ? "   ENTRY: " + Protocols.Entry : ""), () => { });
                }
            if (board)
            {
                AddNudge("d_bp5", "BIAS", "X -", -1, 0);
                AddNudge("d_bp6", "DRIFT", "X +", 1, 0);
                AddNudge("d_bp7", "PHASE", "Z -", 0, -1);
                AddNudge("d_bp8", "OFFSET", "Z +", 0, 1);
            }
            else
            {
                Add("x-", () => "X -: " + CoordsText(), () => Nudge(-1, 0));
                Add("x+", () => "X +: " + CoordsText(), () => Nudge(1, 0));
                Add("z-", () => "Z -: " + CoordsText(), () => Nudge(0, -1));
                Add("z+", () => "Z +: " + CoordsText(), () => Nudge(0, 1));
            }

            // The stabiliser is a hand wheel: each use gives it a third of a turn.
            Add("stabiliser", () => "STABILISER (" + (Tardis.Stability * 100f).ToString("0") + "%)",
                () =>
                {
                    stabTurn += 120f;
                    if (Tardis.State == Tardis.Phase.Idle) { Takeoff.Stabiliser(); return; }
                    Tardis.Stability = Mathf.Min(1f, Tardis.Stability + 0.3f);
                    stabPull = 1f;
                },
                () => stabTurn);
            // The guarded power switch takes two steps each way: lift the cover, then flip the switch on; flip it off, then close the cover.
            // The cover can't close over the switch while it is up (on), and the switch can't be reached under the closed cover.
            Add("power", () => MscTardis.Data.Power ? "POWER: ON" : "POWER: OFF",
                () =>
                {
                    if (!CoverUp) { G.Fail("LIFT THE COVER FIRST"); return; }
                    if (Tardis.State != Tardis.Phase.Idle) { G.Fail("NOT WHILE IN FLIGHT"); return; }
                    MscTardis.Data.Power = !MscTardis.Data.Power;
                    coverOpen = true;   // switched off, the cover stays up until it is closed by hand
                },
                () => MscTardis.Data.Power ? 35f : -35f);
            Add("power.cover", () => CoverUp ? "CLOSE POWER COVER" : "LIFT POWER COVER",
                () =>
                {
                    if (MscTardis.Data.Power) { G.Fail("SWITCH POWER OFF FIRST"); return; }
                    coverOpen = !coverOpen;
                },
                () => CoverUp ? 35f : -35f);
            // Flying by hand and the ship's systems.
            Add("pilot", () => Pilot.Active ? "LEAVE THE CONTROLS" : "TAKE THE CONTROLS",
                () => { var why = Pilot.Enter(); if (why != null) G.Fail(why); },
                () => Pilot.Active ? 35f : -35f);
            Add("flight", () => Flight.On ? "FLIGHT: ON" : "FLIGHT: OFF",
                () => { var why = Flight.ToggleFlight(); if (why != null) G.Fail(why); },
                () => Flight.On ? 35f : -35f);
            // The toggles on the olive strip by the stabiliser wheel: the first is FLIGHT (above, key "flight"); the other two are real switches
            // that flip, wired to nothing.
            for (int i = 2; i <= 3; i++)
            {
                var k = "aux" + i;
                Add(k, () => "SWITCH " + k.Substring(3) + (auxOn.Contains(k) ? ": ON" : ": OFF"),
                    () => { if (!auxOn.Remove(k)) auxOn.Add(k); }, () => auxOn.Contains(k) ? 35f : -35f);
            }
            // The pale fan on the end of the throttle's bolt (the crank on rib 0): a flick sends it round, and it coasts down (TickFlightEffects).
            Add("fan", () => "FAN", () => spinSpeed = Mathf.Min(spinSpeed + FanFlick, FanFlick * 1.6f));
            Add("float", () => Flight.FloatOn ? "FLOAT: ON" : "FLOAT: OFF",
                () => { var why = Flight.ToggleFloat(); if (why != null) G.Fail(why); },
                () => Flight.FloatOn ? 35f : -35f);
            Add("spin", () => "SPIN: " + Flight.SpinName(), () => Flight.CycleSpin(), () => MscTardis.Data.SpinDir * 90f);
            Add("door", () => MscTardis.Data.DoorHeldOpen ? "DOORS: OPEN" : "DOORS: CLOSED", () =>
            {
                var why = Tardis.ToggleDoor();
                if (why != null) G.Fail(why);
            }, () => MscTardis.Data.DoorHeldOpen ? 35f : -35f);
            Add("lock", () => MscTardis.Data.DoorLocked ? "DOOR LOCK: ON" : "DOOR LOCK: OFF", () =>
            {
                if (Protocols.LockdownOn) G.Fail("LOCKDOWN IS ACTIVE");
                else MscTardis.Data.DoorLocked = !MscTardis.Data.DoorLocked;
            },
                () => MscTardis.Data.DoorLocked ? 35f : -35f);
            // HADS and FAST RETURN are protocols now. Their controls take new jobs: the switch is the AUTOPILOT (EASY mode only, wired in a later
            // round), the button is PROTOCOL CANCEL.
            if (board)
            {
                Add("hads", () => "AUTOPILOT: " + (MscTardis.Data.Autopilot ? "ON" : "OFF"), () =>
                    {
                        bool on = !MscTardis.Data.Autopilot;
                        var why = Takeoff.Autopilot(on);
                        if (why != null) { G.Fail(why); return; }
                        MscTardis.Data.Autopilot = on;
                    }, () => MscTardis.Data.Autopilot ? 35f : -35f);
                Add("fastreturn", () => "PROTOCOL CANCEL", () => { var why = Protocols.Cancel(); if (why != null) G.Flash(why, 2f); }, () => Pulse("fastreturn"));
            }
            else
            {
                Add("hads", () => MscTardis.Data.Hads ? "HADS: ARMED" : "HADS: OFF", () => MscTardis.Data.Hads = !MscTardis.Data.Hads, () => MscTardis.Data.Hads ? 35f : -35f);
                Add("fastreturn", () => "FAST RETURN", () => { var why = Tardis.FastReturn(); if (why != null) G.Fail(why); }, () => Pulse("fastreturn"));
            }
            Add("repair", () => Systems.RepairPrimed ? "REPAIR: PRIMED" : "PRIME REPAIR", () => { var why = Systems.TogglePrimeRepair(); if (why != null) G.Fail(why); },
                () => Systems.RepairPrimed ? 35f : -35f);
            // A console with a keypad changes interior by protocol (INTERIOR CHANGE). One without keeps the button; without it there would
            // be no way back out of it.
            if (!keypad)
                Add("room", () => "INTERIOR: " + Rooms[MscTardis.Data.Room], () =>
                {
                    var why = SetRoom((MscTardis.Data.Room + 1) % Rooms.Length);
                    if (why != null) G.Fail(why);
                }, () => Pulse("room"));

            Add("handbrake", () => MscTardis.Data.Handbrake ? "HANDBRAKE: ON" : "HANDBRAKE: OFF",
                () =>
                {
                    var d = MscTardis.Data;
                    d.Handbrake = !d.Handbrake;
                    if (!d.Handbrake) return;
                    Flight.Stop();   // on: it stops flight, and interrupts travel
                    if (Tardis.State != Tardis.Phase.Idle) Tardis.Interrupt();
                },
                () => MscTardis.Data.Handbrake ? 35f : -35f);   // on: pushed in, upright by the rim; off: pulled out towards you
            // The take-off control: on the coral console the space-time throttle, the crank on rib 0 (over the top to take off); in the Toyota and
            // 2010 rooms, each room's own throttle.
            Add("takeoff", () => Tardis.State == Tardis.Phase.Idle ? "TAKE OFF" : "IN FLIGHT",
                () => { var why = TakeOffSelected(); if (why != null) G.Fail(why); },
                () => Tardis.State == Tardis.Phase.Idle ? -35f : 35f);
            // The two red buttons on the lever housing: the upper hastens the landing, the lower aborts without latching the handbrake.
            Add("landnow", () => "LAND NOW", () =>
            {
                if (Tardis.State == Tardis.Phase.Flight) { Tardis.RequestLanding(); G.Flash("LANDING"); }
                else G.Fail("NOT IN FLIGHT");
            });
            Add("estop", () => "EMERGENCY STOP", () =>
            {
                if (Tardis.State == Tardis.Phase.Idle && !Flight.On && !Flight.Airborne) { G.Fail("NOTHING TO STOP"); return; }
                Flight.Stop();
                if (Tardis.State != Tardis.Phase.Idle) Tardis.Interrupt();
                G.Flash("EMERGENCY STOP");
            });
            WireSwitchBoard();
        }

        // The switch board (coral console; the other rooms have no such controls and the unmatched ones are dropped): MASTER, CIRCUIT 1-6 and
        // BREAKER 1-6 are the subsystem panel. The rest of the board stays decor until the take-off and minigame rounds use it.
        static void WireSwitchBoard()
        {
            Add("d_sbm1", () => "MASTER: " + (Subsystems.MasterOn ? "ON" : "OFF"), () => Subsystems.SetMaster(!Subsystems.MasterOn), () => Subsystems.MasterOn ? 35f : -35f);
            for (int i = 0; i < Subsystems.Count; i++)
            {
                int n = i;
                Add("d_sb" + (n + 1), () => "CIRCUIT " + (n + 1) + " (" + Subsystems.Names[n] + "): " + (Subsystems.CircuitOn(n) ? "ON" : "OFF"),
                    () => Subsystems.SetCircuit(n, !Subsystems.CircuitOn(n)), () => Subsystems.CircuitOn(n) ? 35f : -35f);
                Add("d_sb" + (11 + n), () => "BREAKER " + (n + 1) + " (" + Subsystems.Names[n] + "): " + (Subsystems.BreakerOn(n) ? "ON" : "TRIPPED"),
                    () =>
                    {
                        if (!Subsystems.BreakerOn(n) && Subsystems.Failed(n)) G.Flash(Subsystems.Names[n] + " HAS FAILED: REPAIR NEEDED", 3f);
                        Subsystems.SetBreaker(n, !Subsystems.BreakerOn(n));
                    }, () => Subsystems.BreakerOn(n) ? 35f : -35f);
            }
            Add("d_sbl1", () => "INDICATOR 1: " + (Subsystems.AnyFault ? "FAULT" : "NO FAULTS"), () => { });
            controls[controls.Count - 1].LitWhen = () => Subsystems.AnyFault;
            Add("d_sbl2", () => "INDICATOR 2: " + (Subsystems.MasterOn ? "MASTER ON" : "MASTER OFF"), () => { });
            controls[controls.Count - 1].LitWhen = () => !Subsystems.MasterOn;
        }

        // The switch board's decor toggles and lit caps keep their state in decoOn.
        public static bool DecoIs(string key) { return decoOn.Contains(key); }
        public static void DecoSet(string key, bool on) { decoSeen.Add(key); if (on) decoOn.Add(key); else decoOn.Remove(key); }

        // What to do to a spare control: SLIDE, FLIP, PRESS, THROW, TURN.
        public static string Verb(string key)
        {
            string v, kind;
            if (verbs.TryGetValue(key, out v)) return v;
            if (decoKinds.TryGetValue(key, out kind)) return kind == "knob" ? "TURN" : kind != null && kind.StartsWith("toggle") ? "THROW" : "PRESS";
            return "PRESS";
        }

        // A control's name without its state ("RELAY 12: LIT" -> "RELAY 12").
        public static string ControlName(string key)
        {
            var c = controls.Find(k => k.Key == key);
            if (c == null) return key;
            var s = c.Label();
            int i = s.IndexOf(':');
            return (i > 0 ? s.Substring(0, i) : s).Trim();
        }

        public static bool HasControl(string key) { return controls.Exists(c => c.Key == key); }

        // Where a control is, in room space.
        public static bool ControlPoint(string key, out Vector3 at)
        {
            foreach (var c in controls)
                if (c.Key == key) { at = c.At; return true; }
            at = Vector3.zero;
            return false;
        }

        // A plug (see the door turn) dressed as the part round its hole, the one with a corner at each of the plug's own: its material, and at
        // each corner that part's texture place and colour, so the surround's texture runs on across the hole. The 2010 doormat's is the
        // floor, the top of its doorway's the wall. False, and the plug as it was, if no part has all its corners.
        static bool DressAsSurround(Transform plug, Mesh mesh)
        {
            var pv = mesh.vertices;
            var reach = mesh.bounds;
            reach.Expand(0.02f);
            var at = new int[pv.Length];
            foreach (Transform c in Root)
            {
                if (c == plug || c.name.StartsWith("a_doors_")) continue;
                var mf = c.GetComponent<MeshFilter>();
                var r = c.GetComponent<Renderer>();
                if (mf == null || r == null || mf.sharedMesh == null || mf.sharedMesh == mesh || !mf.sharedMesh.bounds.Intersects(reach)) continue;
                var sv = mf.sharedMesh.vertices;
                bool all = true;
                for (int i = 0; i < pv.Length && all; i++)
                {
                    at[i] = -1;
                    for (int k = 0; k < sv.Length; k++)
                        if ((sv[k] - pv[i]).sqrMagnitude < 1e-4f) { at[i] = k; break; }
                    all = at[i] >= 0;
                }
                if (!all) continue;
                var su = mf.sharedMesh.uv;
                var sc = mf.sharedMesh.colors32;
                var m = (Mesh)UnityEngine.Object.Instantiate(mesh);
                m.name = mesh.name + "_plug";
                var uv = new Vector2[pv.Length];
                for (int i = 0; i < pv.Length; i++) uv[i] = su.Length == sv.Length ? su[at[i]] : Vector2.zero;
                m.uv = uv;
                if (sc.Length == sv.Length)
                {
                    var col = new Color32[pv.Length];
                    for (int i = 0; i < pv.Length; i++) col[i] = sc[at[i]];
                    m.colors32 = col;
                }
                plug.GetComponent<MeshFilter>().sharedMesh = m;
                plug.GetComponent<Renderer>().sharedMaterial = r.sharedMaterial;
                return true;
            }
            return false;
        }

        // A drawn part every point of which is within 2 m of the doors point across (room space; the room's parts stand at its origin).
        // In the 2010 room that is the door frame's parts, its doormat (1.5 m) and the top of its doorway (0.9 m); the next part out is
        // the hat stand, at 3.6 m.
        const float ByDoorsReach = 2f;
        static bool ByDoors(Transform c, Vector3 doors)
        {
            var mf = c.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return false;
            var b = mf.sharedMesh.bounds;
            if (b.size.x > 2f * ByDoorsReach || b.size.z > 2f * ByDoorsReach) return false;
            foreach (var p in mf.sharedMesh.vertices)
            {
                float x = p.x - doors.x, z = p.z - doors.z;
                if (x * x + z * z > ByDoorsReach * ByDoorsReach) return false;
            }
            return true;
        }

        public static void PlayClip(AudioClip c) { if (c != null && sfx != null) sfx.PlayOneShot(c, 1f); }
        public static void PlayBeep(float hz) { if (sfx != null) sfx.PlayOneShot(Sfx.Beep(hz, 0.09f), 0.7f); }

        // ---------------------------------------------------------------------------------------------------------
        // Halos: a pulsing glow over a control, to say "this one". Used by the protocol challenges, the fault locator and the flight minigame.
        static readonly Dictionary<string, Transform> halos = new Dictionary<string, Transform>();
        static readonly Dictionary<string, Color> haloColours = new Dictionary<string, Color>();
        static Mesh quad;

        public static void SetHalo(string key, bool on, Color colour)
        {
            Transform t;
            halos.TryGetValue(key, out t);
            if (!on)
            {
                if (t != null) UnityEngine.Object.Destroy(t.gameObject);
                halos.Remove(key);
                flashing.Remove(key);
                return;
            }
            if (t == null)
            {
                if (quad == null)
                {
                    quad = new Mesh { name = "MscTardisQuad" };
                    quad.vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f), new Vector3(0.5f, -0.5f, 0f) };
                    quad.uv = new[] { new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0) };
                    quad.colors32 = new[] { new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255) };
                    quad.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                    quad.RecalculateBounds();
                }
                var go = Res.MeshObject("Halo_" + key, quad, Res.RoomMat("halo.png", Res.RoomMode.Overlay, colour), Root);
                go.GetComponent<Renderer>().enabled = roomShown;
                halos[key] = t = go.transform;
            }
            haloColours[key] = colour;
            flashing.Add(key);
        }
        static readonly HashSet<string> flashing = new HashSet<string>();

        static void TickHalos()
        {
            if (halos.Count == 0) return;
            var cam = G.Cam.transform;
            foreach (var kv in halos)
            {
                var c = controls.Find(k => k.Key == kv.Key);
                if (c == null || kv.Value == null) continue;
                var p = c.Grip != null ? c.Grip.position : Root.TransformPoint(c.At);
                kv.Value.position = p + (cam.position - p).normalized * 0.02f;
                kv.Value.rotation = cam.rotation;
                float pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * 9f);
                kv.Value.localScale = Vector3.one * (0.13f + 0.06f * pulse);
                var col = haloColours[kv.Key];
                var m = kv.Value.GetComponent<Renderer>().sharedMaterial;
                m.color = new Color(col.r, col.g, col.b, 1f) * (0.4f + 0.6f * pulse);
            }
        }

        public static void PlayBell()
        {
            var bell = Sfx.Named("cloister_bell");
            if (bell != null && sfx != null) sfx.PlayOneShot(bell, 1f);
        }

        // Life support's bell has a source of its own, which can be stopped: a one-shot plays its 19.5 s out, and went on ringing after life
        // support was switched back on and the red had gone.
        public static void RingLifeBell()
        {
            if (lifeBell == null || lifeBell.clip == null) { PlayBell(); return; }
            lifeBell.Stop();
            lifeBell.Play();
        }

        public static void StopLifeBell() { if (lifeBell != null && lifeBell.isPlaying) lifeBell.Stop(); }

        // A sprung knob that nudges the manual destination by one step each time it is turned.
        static void AddNudge(string key, string name, string what, int dx, int dz)
        {
            Add(key, () => name + " (" + what + "): " + CoordsText(), () => Nudge(dx, dz), () => Pulse(key));
        }

        static void Add(string key, Func<string> label, Action use, Func<float> angle = null)
        {
            controls.Add(new Control { Key = key, Label = label, Use = use, Angle = angle });
        }

        // A panel control wired to nothing, made up from its line in tardis_controls.txt: a switch that stays where it is put ("toggle", or
        // "toggleon" to start on; a label "A|B" reads A when on, B when off), a knob of five clicks, or a push button (anything else).
        static Control AddDeco(string key, string kind, string label)
        {
            label = Names.For(key, label);
            decoKinds[key] = kind;
            if (kind == "pushlit" || kind == "pushliton")
            {
                // A lit push button ("pushlit", "pushliton" to start lit): it springs back, but each press turns its lamp on or off.
                if (decoSeen.Add(key) && kind == "pushliton") decoOn.Add(key);
                Add(key, () => label + (decoOn.Contains(key) ? ": LIT" : ": OFF"), () => { if (!decoOn.Remove(key)) decoOn.Add(key); });
            }
            else if (kind == "toggle" || kind == "toggleon")
            {
                if (decoSeen.Add(key) && kind == "toggleon") decoOn.Add(key);
                int bar = label.IndexOf('|');
                Func<string> text;
                if (bar >= 0) text = () => decoOn.Contains(key) ? label.Substring(0, bar) : label.Substring(bar + 1);
                else text = () => label + (decoOn.Contains(key) ? ": ON" : ": OFF");
                Add(key, text, () => { if (!decoOn.Remove(key)) decoOn.Add(key); }, () => decoOn.Contains(key) ? 35f : -35f);
            }
            else if (kind == "knob")
                Add(key, () => label + ": " + (DecoStep(key) + 1), () => decoStep[key] = (DecoStep(key) + 1) % 5, () => (DecoStep(key) - 2) * 30f);
            else
                Add(key, () => label, () => { });
            return controls[controls.Count - 1];
        }

        static int DecoStep(string key)
        {
            int s;
            return decoStep.TryGetValue(key, out s) ? s : 2;
        }

        // A sprung switch: over for a moment after it is used, then back.
        static readonly Dictionary<string, float> usedAt = new Dictionary<string, float>();
        static float Pulse(string key)
        {
            float at;
            return usedAt.TryGetValue(key, out at) && Time.time - at < 0.35f ? 35f : -35f;
        }

        static float stabTurn;
        static float stabPull;
        static bool roomShown = true;
        static readonly PartAnim rotorAnim = new PartAnim(PartAnim.Type.Travel, 0.075f);

        static readonly List<AudioSource> muted = new List<AudioSource>();
        static void MuteAmbience(bool inside)
        {
            if (inside)
            {
                var amb = GameObject.Find("MAP/SoundAmbience");
                if (amb == null) return;
                foreach (var s in amb.GetComponentsInChildren<AudioSource>(true))
                    if (!s.mute) { s.mute = true; muted.Add(s); }   // mute, not volume: the game's own FSMs set the volume
            }
            else if (muted.Count > 0)
            {
                foreach (var s in muted) if (s != null) s.mute = false;
                muted.Clear();
            }
        }

        // Sound through the doorway, both ways, so stepping through changes nothing that is heard. Outside, the room's own sounds stand where
        // the open doorway puts the room (the hum carries on from the same place, just as the picture does) and as loud as the doors are
        // open; inside, the map's ambience stands where the doorway puts the map (PortalAudio).
        static void PlaceAudio()
        {
            if (roomAudio == null) return;
            bool outside = !MscTardis.Data.Inside && Doorway.Inner != null && Doorway.Outer != null;
            if (outside)
            {
                Vector3 p; Quaternion q;
                Doorway.Inner.TransformPose(Root.position, Root.rotation, out p, out q);
                roomAudio.position = p;
                roomAudio.rotation = q;
            }
            else
            {
                roomAudio.localPosition = Vector3.zero;
                roomAudio.localRotation = Quaternion.identity;
            }
            audioOut = outside ? Tardis.DoorFraction : 1f;
            sfx.volume = audioOut * ModOptions.EffectsVolume;
            if (lifeBell != null) lifeBell.volume = audioOut * ModOptions.EffectsVolume;
            flightLoop.volume = 0.6f * audioOut * ModOptions.EffectsVolume;
            PortalAudio.Place(MscTardis.Data.Inside && !Pilot.Active);
        }

        // The custom room's controls, as build_interior.py modelled and placed them. Two kinds of line in tardis_controls.txt, in room space:
        //   control <key> <pivot xyz> <forward xyz> <up xyz> <axis xyz, in the pivot's frame> <where to look, in the pivot's frame> <angle scale>
        //     (the pivot's frame is the control at angle 0, the middle of a lever's travel; the mod asks for +-35 and the scale makes that the model's swing)
        //   mesh <key> <s|m|r> <file> <texture>    s stands still, m turns with the control, r spins by itself about the control's axis
        static void ApplyCustomControls()
        {
            var fixedAt = new Dictionary<string, Transform>();
            var movingAt = new Dictionary<string, Transform>();
            var spinAt = new Dictionary<string, Transform>();
            var mats = new Dictionary<string, Material>();
            foreach (var line in Res.Lines("tardis_controls.txt"))
            {
                var f = line.Split(' ');
                if (f[0] == "control" && f.Length >= 18)
                {
                    // <key>.<part>: a second moving part of that control on its own pivot (the guard over the power switch). It follows the
                    // control's angle with its own axis and scale, and is not looked at or used by itself.
                    int dot = f[1].IndexOf('.');
                    var c = controls.Find(k => k.Key == f[1]);   // a "<key>.<part>" with its own control (power.cover) is a control, not a follower
                    if (c != null) dot = -1;
                    else c = controls.Find(k => k.Key == (dot > 0 ? f[1].Substring(0, dot) : f[1]));
                    // Optional: how it moves ("push" slides; toggle / toggleon / knob for the d_ controls) and a d_ control's label.
                    string kind = f.Length >= 19 ? f[18] : null, label = f.Length >= 20 ? f[19].Replace('_', ' ') : f[1];
                    if (c == null && dot < 0 && f[1].StartsWith("d_")) c = AddDeco(f[1], kind, label);
                    if (c == null) continue;
                    var at = new Vector3(G.F(f[2]), G.F(f[3]), G.F(f[4]));
                    var rot = Quaternion.LookRotation(new Vector3(G.F(f[5]), G.F(f[6]), G.F(f[7])), new Vector3(G.F(f[8]), G.F(f[9]), G.F(f[10])));
                    if (dot > 0)
                    {
                        var fs = Pivot(f[1] + "Fixed", at);
                        fs.localRotation = rot;
                        var fm = Pivot(f[1], at);
                        fm.localRotation = rot;
                        fixedAt[f[1]] = fs;
                        movingAt[f[1]] = fm;
                        var fo = new Follower { Of = c, Moving = fm, Base = rot, Axis = new Vector3(G.F(f[11]), G.F(f[12]), G.F(f[13])), Scale = G.F(f[17]) };
                        followers.Add(fo);
                        if (c.Angle != null) fm.localRotation = rot * Quaternion.AngleAxis(c.Angle() * fo.Scale, fo.Axis);
                        continue;
                    }
                    var still = Pivot(c.Key + "Fixed", at);
                    still.localRotation = rot;
                    var moving = Pivot(c.Key, at);
                    moving.localRotation = rot;
                    fixedAt[c.Key] = still;
                    movingAt[c.Key] = moving;
                    var grip = new GameObject("GripPoint").transform;   // what the player looks at: it moves with the lever
                    grip.SetParent(moving, false);
                    grip.localPosition = new Vector3(G.F(f[14]), G.F(f[15]), G.F(f[16]));
                    c.At = at;
                    c.Grip = grip;
                    c.Base = rot;
                    c.Axis = new Vector3(G.F(f[11]), G.F(f[12]), G.F(f[13]));
                    c.Scale = G.F(f[17]);
                    // A lit button's lamp colour (rrggbb): its cap is drawn with that colour, unlit, while it is on.
                    if (f[1] == "d_sbl1" || f[1] == "d_sbl2")
                    {
                        c.LampOn = Res.RoomMat("ctl_plastic_cap.png", Res.RoomMode.Lamp, f[1] == "d_sbl1" ? new Color(1f, 0.16f, 0.10f) : new Color(1f, 0.58f, 0.08f));
                        c.Lamp = new List<Renderer>();
                    }
                    else if (f.Length >= 21 && f[20].Length >= 6)
                    {
                        var hex = f[20];
                        Func<int, float> ch = i => System.Convert.ToInt32(hex.Substring(i, 2), 16) / 255f;
                        c.LampOn = Res.RoomMat("ctl_plastic_cap.png", Res.RoomMode.Lamp, new Color(ch(0), ch(2), ch(4)));
                        c.Lamp = new List<Renderer>();
                    }
                    if (kind != null && kind.StartsWith("push"))
                    {
                        c.Push = true;
                        var pk = c.Key;
                        if (c.Angle == null) c.Angle = () => Pulse(pk);   // a plain button: in for a moment when pressed
                    }
                    c.Moving = c.Angle != null ? moving : null;
                    c.Spring = c.Push || grip.localPosition.magnitude < 0.05f;   // switches, keys and knobs, not the levers
                    c.Zeta = c.Push ? 0.8f : 1f;
                    c.Cur = c.Angle != null ? c.Angle() : 0f;
                    c.Vel = 0f;
                    // It starts where it belongs: left at its middle, a lever with a long throw would swing down as the room appears.
                    if (c.Moving != null && !c.Push) moving.localRotation = rot * Quaternion.AngleAxis(c.Cur * c.Scale, c.Axis);
                }
                else if (f[0] == "mesh" && f.Length >= 5 && fixedAt.ContainsKey(f[1]))
                {
                    Material mat;
                    if (!mats.TryGetValue(f[4], out mat))
                    {
                        mats[f[4]] = mat = Res.RoomMat(f[4] + ".png", Res.RoomMode.Baked, Color.white);
                        tinted.Add(mat);   // they dim with the room, as the rest of the console does
                    }
                    Transform spin = null;
                    if (f[2] == "r" && !spinAt.TryGetValue(f[1], out spin))
                    {
                        spinAt[f[1]] = spin = new GameObject(f[1] + "Spin").transform;
                        spin.SetParent(fixedAt[f[1]], false);
                        spinners.Add(new KeyValuePair<Transform, Vector3>(spin, controls.Find(k => k.Key == f[1]).Axis));
                    }
                    var mo = Res.MeshObject(f[3], Res.Dwm(f[3]), mat, f[2] == "m" ? movingAt[f[1]] : spin ?? fixedAt[f[1]]);
                    var lc = f[2] == "m" ? controls.Find(k => k.Key == f[1]) : null;
                    if (lc != null && lc.Lamp != null && f[4] == "ctl_plastic_cap") { lc.Lamp.Add(mo.GetComponent<Renderer>()); lc.LampOff = mat; }
                }
            }
            controls.RemoveAll(c => c.Key == "power.cover" && !fixedAt.ContainsKey(c.Key));   // only the coral console's switch is guarded
            int lost = controls.RemoveAll(c => !fixedAt.ContainsKey(c.Key));
            if (lost > 0) MSCLoader.ModConsole.Error("MSC-TARDIS: " + lost + " console controls are missing from tardis_controls.txt");
        }

        // ---------------------------------------------------------------------------------------------------------
        // Controls.

        // The selector's positions: the presets, the telepathic jump's extras while it's on, then MANUAL (index Places.Length). Extras are
        // numbered from Places.Length + 1.
        public static List<int> SelectorSeq()
        {
            var seq = new List<int>();
            for (int i = 0; i < Tardis.Places.Length; i++) seq.Add(i);
            if (Protocols.TelepathicOn) for (int k = 0; k < Tardis.Extras.Length; k++) seq.Add(Tardis.Places.Length + 1 + k);
            seq.Add(Tardis.Places.Length);
            return seq;
        }

        public static void CycleSelector(int dir)
        {
            var seq = SelectorSeq();
            int i = seq.IndexOf(MscTardis.Data.Selector);
            MscTardis.Data.Selector = seq[((i < 0 ? seq.Count - 1 : i) + dir + seq.Count) % seq.Count];
        }

        // The destination as a place, or null for MANUAL.
        public static Tardis.Place SelectedPlace()
        {
            int s = MscTardis.Data.Selector;
            if (s < Tardis.Places.Length) return Tardis.Places[s];
            int k = s - Tardis.Places.Length - 1;
            return k >= 0 && k < Tardis.Extras.Length && Protocols.TelepathicOn ? Tardis.Extras[k] : null;
        }

        public static string SelectorName()
        {
            var p = SelectedPlace();
            return p != null ? p.Name : "MANUAL " + CoordsText();
        }

        static string CoordsText() { return MscTardis.Data.ManualX.ToString("0") + " / " + MscTardis.Data.ManualZ.ToString("0"); }

        // Any coordinate button switches to manual. The first time, the coordinates start from where the TARDIS stands.
        static void Nudge(int dx, int dz)
        {
            var d = MscTardis.Data;
            if (d.Selector != Tardis.Places.Length)
            {
                d.Selector = Tardis.Places.Length;
                if (d.ManualX == 0f && d.ManualZ == 0f) { d.ManualX = Mathf.Round(Tardis.Position.x); d.ManualZ = Mathf.Round(Tardis.Position.z); }
            }
            d.ManualX += dx * Steps[d.StepIndex];
            d.ManualZ += dz * Steps[d.StepIndex];
        }

        // Null when under way, else why not. Failing is the travel core's job: the handbrake check there costs fuel.
        public static string TakeOffSelected(bool force = false)
        {
            if (Tardis.State != Tardis.Phase.Idle) return "ALREADY IN FLIGHT";
            var d = MscTardis.Data;
            if (!force) { var missing = Takeoff.Missing(); if (missing != null) return missing; }
            var why = TravelSelected(d);
            if (why == null && !force && Takeoff.Enforce) Takeoff.Consume();
            return why;
        }

        static string TravelSelected(SaveData d)
        {
            var place = SelectedPlace();
            if (place != null && !Subsystems.Up(Subsystems.Navigation)) return "NAVIGATION OFFLINE: MANUAL COORDINATES ONLY";
            return place != null
                ? Tardis.TravelToPlace(place, true)
                : Tardis.TravelToCoords(d.ManualX, d.ManualZ, true);
        }

        public static string ControlKeys()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var c in controls) sb.Append(c.Key + " ");
            return sb.ToString().Trim();
        }



        public static string UseControl(string key)
        {
            foreach (var c in controls)
                if (c.Key == key)
                {
                    Activate(c);
                    return c.Label();
                }
            return "no control " + key + ": " + ControlKeys();
        }

        static void Activate(Control c)
        {
            // As in the original, a control moves when used, whatever comes of it.
            if (!flipped.Remove(c.Key)) flipped.Add(c.Key);
            usedAt[c.Key] = Time.time;
            PartAnim anim;
            if (anims.TryGetValue(c.Key, out anim) && anim.Kind == PartAnim.Type.Cycle) anim.Run = true;
            // Power, and the repair control that turns it off, work without power; so do the hand-turned fan, the spare toggles and the panel
            // controls wired to nothing.
            if (!MscTardis.Data.Power && c.Key != "power" && c.Key != "power.cover" && c.Key != "repair" && c.Key != "lock" && c.Key != "fan" && !c.Key.StartsWith("aux") && !c.Key.StartsWith("d_"))
            { G.Fail("NO POWER"); return; }
            bool brake = MscTardis.Data.Handbrake, power = MscTardis.Data.Power;
            c.Use();
            Protocols.OnControlUsed(c.Key);
            FlightGame.OnControlUsed(c.Key);
            if (c.Key == "power")
            {
                var clip = MscTardis.Data.Power ? powerUpClip : powerDownClip;
                if (MscTardis.Data.Power != power && clip != null) sfx.PlayOneShot(clip, 1f);
                return;
            }
            if (c.Key == "handbrake")
            {
                // The lever has its own sounds, and stays silent when it refused to move (in flight).
                if (MscTardis.Data.Handbrake != brake) sfx.PlayOneShot(MscTardis.Data.Handbrake ? brakeOnClip : brakeOffClip, 0.9f);
                return;
            }
            AudioClip own;
            if (controlClips.TryGetValue(c.Key, out own)) sfx.PlayOneShot(own, 0.9f);
            else sfx.PlayOneShot(clickClip, 0.8f);
        }

        // ---------------------------------------------------------------------------------------------------------
        // Player.

        public static void PlacePlayer()
        {
            var at = Root.TransformPoint(entry + Vector3.up * Tardis.PlayerFeetOffset());
            G.Player.position = at;
            G.Player.rotation = Quaternion.Euler(0f, entryYaw, 0f);
            OpenDoor(false);   // arriving: the swing only, the police box door already made the sound
        }

        // The door always swings; only leaving (inside -> outside) makes the interior door sound.
        public static void OpenDoor(bool sound)
        {
            Tardis.SwingDoor();
            doorOpenUntil = Time.time + 1.3f;
            doorIsOpen = sound;
            if (sound) door.PlayOneShot(doorOpenClip);
        }

        public static void PlayTravelSound(AudioClip clip) { sfx.PlayOneShot(clip != null ? clip : travelClip, 1f); }

        public static void Tick()
        {
            if (doorIsOpen && Time.time >= doorOpenUntil) { doorIsOpen = false; door.PlayOneShot(doorCloseClip); }
            if (noMeshes) TickToyotaParts();
            Computer.Tick();
            TickHalos();

            TickFlightEffects();
            ApplyInside();
            PlaceAudio();
            // The map's ambience (birds, with a cuckoo call) is only 90% 3D, so a tenth of it is heard from anywhere, even up here.
            // With the doors open it comes in through them.
            if (Time.frameCount % 30 == 0) MuteAmbience(MscTardis.Data.Inside && !Pilot.Active && !Tardis.DoorOpen);
            if (roomLamps.Count > 0)
            {
                bool powered = MscTardis.Data.Power;
                foreach (var l in roomLamps.Values)
                {
                    if (l.Lit == null) continue;
                    bool lit = powered && l.Lit();
                    if (lit == l.Shown) continue;
                    l.Shown = lit;
                    for (int i = 0; i < l.R.Count; i++) l.R[i].sharedMaterial = lit ? l.On[i] : l.Off[i];
                }
            }
            if (ringLamps.Count > 0)
            {
                bool pw = MscTardis.Data.Power;
                foreach (var rl in ringLamps)
                {
                    float level = pw ? (decoOn.Contains(rl.Key) ? 1f : 0f) : -1f;
                    if (pw && FlightGame.Prompt == rl.Key && (int)(Time.time * 5f) % 2 == 0) level = 1f;   // blinking while the minigame asks for the slider
                    if (Mathf.Abs(level - rl.Shown) < 0.001f) continue;
                    rl.Shown = level;
                    if (level < 0f) rl.R.sharedMaterial = rl.Dark;
                    else
                    {
                        float i = 0.34f + 0.46f * level;   // the lamp shader doubles it: from 0.7 of the texture's own colour up to 1.6
                        rl.Glow.color = new Color(i, i, i, 1f);
                        rl.R.sharedMaterial = rl.Glow;
                    }
                }
            }
            foreach (var c in controls)
            {
                if (c.Lamp == null) continue;
                bool lit = (c.LitWhen != null ? c.LitWhen() : decoOn.Contains(c.Key)) && MscTardis.Data.Power;
                if (flashing.Contains(c.Key)) lit = MscTardis.Data.Power && (int)(Time.time * 5f) % 2 == 0;
                if (lit == c.LampShown) continue;
                c.LampShown = lit;
                foreach (var r in c.Lamp) if (r != null) r.sharedMaterial = lit ? c.LampOn : c.LampOff;
            }
            foreach (var c in controls)
                if (c.Moving != null && c.Angle != null)
                {
                    if (c.Spring)
                    {
                        Spring(c, c.Angle(), Time.deltaTime);
                        if (c.Push) c.Moving.localPosition = c.At + c.Base * (c.Axis * (c.Scale * (c.Cur + 35f) / 70f));
                        else c.Moving.localRotation = c.Base * Quaternion.AngleAxis(c.Cur * c.Scale, c.Axis);
                        continue;
                    }
                    var target = c.Base * Quaternion.AngleAxis(c.Angle() * c.Scale, c.Axis);
                    // A lever that goes right over (180 degrees end to end, as the coral throttle) has two equally short ways round to its
                    // other end. Send it by its middle first, where it stands up: over the top, never down through the console.
                    if (c.Scale * 70f > 170f && Quaternion.Angle(c.Moving.localRotation, target) > 170f) target = c.Base;
                    c.Moving.localRotation = Quaternion.RotateTowards(c.Moving.localRotation, target, 240f * Time.deltaTime);
                }
            foreach (var fo in followers)
                if (fo.Moving != null && fo.Of.Angle != null)
                {
                    // A sprung control's follower moves with it, overshoot and all.
                    if (fo.Of.Spring) { fo.Moving.localRotation = fo.Base * Quaternion.AngleAxis(fo.Of.Cur * fo.Scale, fo.Axis); continue; }
                    var target = fo.Base * Quaternion.AngleAxis(fo.Of.Angle() * fo.Scale, fo.Axis);
                    fo.Moving.localRotation = Quaternion.RotateTowards(fo.Moving.localRotation, target, 240f * Time.deltaTime);
                }
            stabPull = Mathf.MoveTowards(stabPull, 0f, Time.deltaTime * 2f);
            if (Time.frameCount % 10 == 0) UpdateMonitor();

            if (!MscTardis.Data.Inside) { StopShake(); return; }
            Keep();
            if (!Pilot.Active && !Computer.Open) LookAtThings();
        }

        // Inside, with something that moved or dropped the player: under the floor goes back to the door; far away
        // (a vanilla teleport such as waking up after a death) means they are not inside any more.
        static void Keep()
        {
            var local = Root.InverseTransformPoint(G.Player.position);
            if (local.magnitude > 100f) { MscTardis.Data.Inside = false; return; }
            if (local.y < keepFloor || new Vector2(local.x, local.z).magnitude > keepRadius) PlacePlayer();
        }


        static void LookAtThings()
        {
            if (G.Seated) return;
            var ray = new Ray(G.Cam.transform.position, G.Cam.transform.forward);

            Control best = null;
            float bestMiss = ControlSlack;
            foreach (var c in controls)
            {
                if (c.Key == "power" && !CoverUp) continue;   // under its closed cover: only the cover can be reached
                var pt = c.Grip != null ? c.Grip.position : c.Key == "power.cover" && coverPart != null ? coverPart.bounds.center : Root.TransformPoint(c.At);
                var to = pt - ray.origin;
                float along = Vector3.Dot(to, ray.direction);
                if (along <= 0f || along > ControlRange) continue;
                float miss = Vector3.Cross(ray.direction, to).magnitude;
                if (c.Spring && miss > SmallSlack) continue;   // a button, switch or knob only when looked at, not from the plate beside it
                if (miss < bestMiss) { bestMiss = miss; best = c; }
            }
            if (best != null)
            {
                Tardis.ShowPrompt(best.Label());
                if (G.UsePressed) Activate(best);
                return;
            }
            // Where there is a keyboard (the coral console) it is the only way in: it works the computer live on the monitor, the player held
            // and facing it. The Toyota and 2010 rooms have none, so their monitor's screen brings up the overlay.
            bool atKeyboard = keyboardCollider != null && G.LookingAt(keyboardCollider, ScreenRange);
            if (atKeyboard || (keyboardCollider == null && LookingAtScreen()))
            {
                Tardis.ShowPrompt(atKeyboard ? "USE KEYBOARD" : "USE COMPUTER");
                if (G.UsePressed || (G.Click && !G.HoldingObject))
                {
                    var why = Computer.TryOpen(atKeyboard);
                    if (why != null) G.Fail(why);
                }
                return;
            }
            if (G.LookingAt(doorCollider, 2.5f))
            {
                bool open = MscTardis.Data.DoorHeldOpen;
                Tardis.ShowPrompt(open ? "CLOSE DOOR" : Tardis.Busy ? "DOORS LOCKED IN FLIGHT" : "OPEN DOOR");
                if (G.UsePressed)
                {
                    var why = Tardis.ToggleDoor();
                    if (why != null) G.Fail(why);
                }
                return;
            }
            Tardis.ClearPrompt();
        }

        // The view ray crosses the monitor's screen within reach.
        const float ScreenRange = 2.6f;
        public static bool LookingAtScreen()
        {
            var c = G.Cam.transform;
            var o = Root.InverseTransformPoint(c.position);
            var dir = Root.InverseTransformDirection(c.forward);
            var n = screenFace.normalized;
            float denom = Vector3.Dot(dir, n);
            if (denom > -0.05f) return false;
            float t = Vector3.Dot(screenAt - o, n) / denom;
            if (t <= 0f || t > ScreenRange) return false;
            var p = o + dir * t - screenAt;
            var right = Vector3.Cross(Vector3.up, n);
            if (right.sqrMagnitude < 0.01f) return false;
            right.Normalize();
            var up = Vector3.Cross(n, right);
            return Mathf.Abs(Vector3.Dot(p, right)) < screenW / 2f + 0.05f && Mathf.Abs(Vector3.Dot(p, up)) < screenH / 2f + 0.05f;
        }


        // ---------------------------------------------------------------------------------------------------------
        // Flight feedback: rotor, lights, shake, and stability running down unless the stabiliser is worked.

        const float StabilityLossPerSecond = 0.08f;
        // During the flight minigame the passive loss is a quarter, so the prompts drive stability: a player takes 1-3 s to find and work a
        // control, and at half (0.04/s) a prompt cycle of 5-7 s lost more than a hit's +0.15 gives back, so even a perfect flight ran down to 0.
        const float MinigameLoss = 0.25f;
        const float ShakeBase = 0.003f, ShakeStrain = 0.015f;   // camera shake in flight (metres): always some, plus more as stability drops

        static void TickFlightEffects()
        {
            bool flying = Tardis.State != Tardis.Phase.Idle;
            float t = Time.time;
            // The rotor: a "travel" part at the original's speed, one rise and fall every 13 s.
            rotorAnim.Tick(Time.deltaTime, MscTardis.Data.Power, flying || Flight.On, false);
            float rise = Mathf.Sin(rotorAnim.Pos * 2f * Mathf.PI);
            if (rotor != null) rotor.localPosition = new Vector3(0f, RotorTravel * rise, 0f);
            rotorLight.intensity = flying || Flight.On ? 1.2f + 0.8f * rise : 0.6f;

            // The fan on the throttle's bolt coasts down from its last flick.
            spinSpeed = Mathf.MoveTowards(spinSpeed, 0f, FanDrag * Time.deltaTime);
            spinAngle = Mathf.Repeat(spinAngle + spinSpeed * Time.deltaTime, 360f);
            foreach (var s in spinners) if (s.Key != null) s.Key.localRotation = Quaternion.AngleAxis(spinAngle, s.Value);

            if (flying && Tardis.State == Tardis.Phase.Flight)
                Tardis.Stability = Mathf.Max(0f, Tardis.Stability - StabilityLossPerSecond * (FlightGame.Active ? MinigameLoss : 1f) * Time.deltaTime);

            // POWER: the lights and the hum ease down to a dim glow (never black, so the lever can still be found) and back.
            powerLevel = Mathf.MoveTowards(powerLevel, MscTardis.Data.Power ? 1f : 0f, Time.deltaTime / 2.5f);
            float lvl = Mathf.Lerp(0.15f, 1f, powerLevel) * (Protocols.LightsOn ? 0.45f : 1f);

            float strain = flying ? 1f - Tardis.Stability : 0f;
            float flicker = (1.3f - strain * (0.5f + 0.5f * Mathf.Sin(t * 19f)) * 0.9f) * lvl;
            foreach (var l in roomLights) l.intensity = flicker;
            rotorLight.intensity *= lvl;
            var tint = Color.white * (flicker / 1.3f);
            // Life support down: the room goes to emergency red.
            float emer = Subsystems.Emergency;
            if (emer > 0f)
            {
                tint = Color.Lerp(tint, new Color(1f, 0.18f, 0.12f) * (flicker / 1.3f) * (0.55f + 0.2f * Mathf.Sin(t * 3f)), emer);
                foreach (var l in roomLights) l.color = Color.Lerp(roomLightColour, new Color(1f, 0.15f, 0.1f), emer);
            }
            else if (roomLights.Count > 0 && roomLights[0].color != roomLightColour) foreach (var l in roomLights) l.color = roomLightColour;
            tint.a = 1f;
            foreach (var m in tinted) m.color = tint;
            hum.volume = (flying ? 0.2f : 0.07f) * powerLevel * (1f - emer) * audioOut * ModOptions.HumVolume;

            // The in-flight loop runs only during the flight phase, between the takeoff and landing sounds.
            SetLoop(flightLoop, Tardis.State == Tardis.Phase.Flight);
            // A badly stabilised piloted flight sets off the malfunction alarm, once.
            if (Tardis.State == Tardis.Phase.Idle) malfunctionPlayed = false;
            else if (!malfunctionPlayed && Tardis.State == Tardis.Phase.Flight && Tardis.Piloted && Tardis.Stability < Tardis.RoughLanding && malfunctionClip != null)
            {
                malfunctionPlayed = true;
                sfx.PlayOneShot(malfunctionClip, 1f);
            }

            if (flying && MscTardis.Data.Inside && !Pilot.Active && !Computer.Open)
            {
                if (!shaking) { camBase = G.FpsCamInner.localPosition; shaking = true; }
                float amp = ShakeBase + strain * ShakeStrain;
                G.FpsCamInner.localPosition = camBase + new Vector3(Mathf.Sin(t * 37f), Mathf.Sin(t * 43f), 0f) * amp;
            }
            else StopShake();
        }

        static void SetLoop(AudioSource s, bool on)
        {
            if (s.clip == null) return;
            if (on && !s.isPlaying) s.Play();
            else if (!on && s.isPlaying) s.Stop();
        }

        static void StopShake()
        {
            if (!shaking) return;
            G.FpsCamInner.localPosition = camBase;
            shaking = false;
        }

        static void UpdateMonitor()
        {
            var d = MscTardis.Data;
            // The computer's page, as it was left, unless the readout below has something that can't wait: no power, the flight game's
            // prompt, a protocol being keyed in, run or counting down.
            bool live = Computer.OnMonitor && d.Power;   // worked from the keyboard: the page, whatever else wants the readout
            bool alert = !d.Power || FlightGame.Prompt != null || Protocols.Current != null || Protocols.Entry.Length > 0 || Protocols.DestructLeft >= 0f;
            bool pic = (live || !alert) && screenPic != null && Terminal.Picture != null;
            if (screenPic != null && screenPic.activeSelf != pic) { screenPic.SetActive(pic); screenBack.SetActive(pic); }
            if (pic)
            {
                screenMat.mainTexture = Terminal.Picture;
                screenMat.color = Terminal.Phosphors[Mathf.Clamp(Terminal.Phosphor, 0, Terminal.Phosphors.Length - 1)] * 0.5f;
                if (monitor.text != "") monitor.text = "";
                bool over = Terminal.Overlay != null && Computer.OnMonitor;   // the exterior scanner's picture
                if (screenOver.activeSelf != over) screenOver.SetActive(over);
                if (over)
                {
                    screenOverMat.mainTexture = Terminal.Overlay;
                    screenOverMat.color = screenMat.color * 0.95f;
                }
                return;
            }
            if (screenOver != null && screenOver.activeSelf) screenOver.SetActive(false);
            var sb = new System.Text.StringBuilder();
            if (!d.Power)
            {
                var off = "TARDIS\n\n\nNO POWER";
                if (monitor.text != off) monitor.text = off;
                return;
            }
            sb.Append("TARDIS  ").Append(Tardis.State.ToString().ToUpperInvariant()).Append('\n');
            sb.Append("HERE ").Append(Tardis.Position.x.ToString("0")).Append(" / ").Append(Tardis.Position.z.ToString("0")).Append('\n');
            sb.Append("DEST ").Append(SelectorName()).Append('\n');
            var fault = Subsystems.FaultLine();
            var ch = Protocols.Current;
            if (FlightGame.Prompt != null) sb.Append(FlightGame.PromptText + " " + Mathf.Max(0, Mathf.CeilToInt(FlightGame.Until - Time.time)) + "S\n");
            else if (ch != null)
                sb.Append("PROTOCOL " + Protocols.Table[ch.Index].Id + ": PRESS " + ch.Pressed.Count + "/" + ch.Targets.Count + " " + Mathf.Max(0, Mathf.CeilToInt(ch.Until - Time.time)) + "S\n");
            else if (Protocols.Entry.Length > 0) sb.Append("PROTOCOL: " + Protocols.Entry + "_\n");
            else if (Protocols.DestructLeft >= 0f) sb.Append("SELF-DESTRUCT T-" + Mathf.CeilToInt(Protocols.DestructLeft) + "\n");
            else if (Tardis.State == Tardis.Phase.Idle && Takeoff.Missing() != null && !d.Handbrake) sb.Append(Takeoff.Missing().Length > 26 ? Takeoff.Missing().Substring(0, 26) : Takeoff.Missing()).Append('\n');
            else if (fault != null) sb.Append(fault.Length > 26 ? fault.Substring(0, 26) : fault).Append('\n');
            else sb.Append("STEP ").Append(Steps[d.StepIndex]).Append(" M\n");
            sb.Append("BRAKE ").Append(d.Handbrake ? "ON" : "OFF").Append('\n');
            sb.Append("STABILITY ").Append((Tardis.Stability * 100f).ToString("0")).Append("%\n");
            sb.Append("FUEL ").Append(Systems.Artron.ToString("0")).Append("  HULL ").Append(Systems.HealthPercent.ToString("0")).Append("%");
            if (Tardis.State != Tardis.Phase.Idle) sb.Append("\nTO ").Append(Tardis.Heading);
            var s = sb.ToString();
            if (monitor.text != s) monitor.text = s;
        }
    }
}
