using HutongGames.PlayMaker;
using MSCLoader;
using UnityEngine;

namespace MscTardis
{
    // Takes the player's own controls away (walking and mouse look) for as long as anything holds them: piloting, the computer.
    // Apply() every frame re-disables anything the game switched back on.
    static class PlayerHold
    {
        static readonly System.Collections.Generic.HashSet<string> owners = new System.Collections.Generic.HashSet<string>();
        static readonly System.Collections.Generic.List<Behaviour> disabled = new System.Collections.Generic.List<Behaviour>();
        static readonly string[] Controls = { "CharacterMotor", "FPSInputController", "SimpleSmoothMouseLook", "MouseLook" };

        public static bool Held { get { return owners.Count > 0; } }

        public static void Add(string who) { owners.Add(who); Apply(); }

        public static void Remove(string who)
        {
            if (!owners.Remove(who) || owners.Count > 0) return;
            foreach (var b in disabled) if (b != null) b.enabled = true;
            disabled.Clear();
        }

        public static void Apply()
        {
            if (owners.Count == 0) return;
            foreach (var name in Controls)
            {
                Switch(G.Player.GetComponent(name) as Behaviour);
                Switch(G.FpsCam.GetComponent(name) as Behaviour);
            }
        }

        static void Switch(Behaviour b)
        {
            if (b == null || !b.enabled) return;
            b.enabled = false;
            if (!disabled.Contains(b)) disabled.Add(b);
        }
    }

    // Thin wrappers over the game's PlayMaker globals and common objects (names from the scene dump).
    static class G
    {
        public static Transform Player, FpsCam, FpsCamInner;
        public static Camera Cam;

        static FsmFloat money, stress, stressRate, fatigue;
        static FsmBool handLeft, handRight, helmet, seated, drinkOn, guiBuy, guiUse;
        static FsmString guiInteraction, guiSubtitle;
        static FsmBool handEmpty;
        static PlayMakerFSM pickUp;

        // GUIinteraction is cleared every frame by whatever the player looks at, so our messages are re-set
        // each frame until they expire.
        static string flash;
        static float flashUntil;

        public static void Init()
        {
            Player = GameObject.Find("PLAYER").transform;
            FpsCam = Player.Find("Pivot/AnimPivot/Camera/FPSCamera");
            FpsCamInner = FpsCam.Find("FPSCamera");
            Cam = FpsCamInner.GetComponent<Camera>();
            var g = FsmVariables.GlobalVariables;
            money = g.FindFsmFloat("PlayerMoney");
            stress = g.FindFsmFloat("PlayerStress");
            stressRate = g.FindFsmFloat("PlayerStressRate");
            fatigue = g.FindFsmFloat("PlayerFatigue");
            handLeft = g.FindFsmBool("PlayerHandLeft");
            handRight = g.FindFsmBool("PlayerHandRight");
            helmet = g.FindFsmBool("PlayerHelmet");
            seated = g.FindFsmBool("PlayerSeated");
            drinkOn = g.FindFsmBool("PlayerDrinkOn");
            guiBuy = g.FindFsmBool("GUIbuy");
            guiUse = g.FindFsmBool("GUIuse");
            guiInteraction = g.FindFsmString("GUIinteraction");
            guiSubtitle = g.FindFsmString("GUIsubtitle");
            var hand = FpsCam.Find("1Hand_Assemble/Hand");
            pickUp = hand != null ? hand.GetPlayMaker("PickUp") : null;
            handEmpty = pickUp != null ? pickUp.FsmVariables.FindFsmBool("HandEmpty") : null;
        }

        public static float Stress { get { return stress.Value; } set { stress.Value = value; } }
        public static float StressRate { get { return stressRate.Value; } set { stressRate.Value = value; } }
        public static float Fatigue { get { return fatigue.Value; } set { fatigue.Value = value; } }
        public static bool HandLeft { get { return handLeft.Value; } set { handLeft.Value = value; } }
        public static bool HandRight { get { return handRight.Value; } set { handRight.Value = value; } }
        public static bool Helmet { get { return helmet.Value; } }
        public static bool Seated { get { return seated.Value; } }
        public static bool Drinking { get { return drinkOn.Value; } }
        public static bool HoldingObject { get { return handEmpty != null && !handEmpty.Value; } }


        // An FSM into a state by name (this PlayMaker has no SetState; Fsm.SwitchState isn't public).
        static readonly System.Reflection.MethodInfo switchState =
            typeof(Fsm).GetMethod("SwitchState", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        static void Goto(PlayMakerFSM f, string state)
        {
            foreach (var st in f.FsmStates) if (st.Name == state) { switchState.Invoke(f.Fsm, new object[] { st }); return; }
        }

        // Let go of what the hand holds, as the game's own drop does (tests).
        public static void DropFromHand() { if (pickUp != null && HoldingObject) pickUp.SendEvent("PROCEED Drop"); }   // "Part picked" -> "Drop part"

        // In the player's hand (the game parents a picked-up item to its ItemPivot).
        public static bool InHand(Transform t) { return t.parent != null && t.parent.name == "ItemPivot"; }

        public static bool UsePressed { get { return cInput.GetKeyDown("Use"); } }
        public static bool UseHeld { get { return cInput.GetKey("Use"); } }
        public static bool Click { get { return Input.GetMouseButtonDown(0); } }

        public static void Interaction(string text, bool buy = false, bool use = false)
        {
            // A message (Flash/Fail) outranks a prompt while it lasts: the HUD reads the text before LateTick runs.
            guiInteraction.Value = flash != null && Time.time <= flashUntil ? flash : text;
            guiBuy.Value = buy;
            guiUse.Value = use;
        }

        public static void Subtitle(string text) { guiSubtitle.Value = text; }

        public static void Flash(string text, float seconds = 2.5f)
        {
            flash = text;
            flashUntil = Time.time + seconds;
        }

        // Called from Update after everything else so the flash wins over the vanilla "nothing in view" clear.
        public static void LateTick()
        {
            if (flash == null) return;
            if (Time.time > flashUntil) { flash = null; guiInteraction.Value = ""; return; }
            guiInteraction.Value = flash;
        }

        public static void Swear()
        {
            MasterAudio.PlaySound3DAtTransformAndForget("Swearing", FpsCam);
        }

        public static void Sound(string group, string variation, Transform at, float vol = 1f)
        {
            MasterAudio.PlaySound3DAtTransformAndForget(group, at, vol, null, 0f, variation);
        }

        // Fail message: text on screen + the player cusses, like "NOT ENOUGH MONEY".
        public static void Fail(string text)
        {
            Flash(text, 2.5f);
            Swear();
        }

        public static bool LookingAt(Collider c, float range, out RaycastHit hit)
        {
            var ray = new Ray(Cam.transform.position, Cam.transform.forward);
            return c.Raycast(ray, out hit, range);
        }

        // Forgiving check for small items: the view ray passes within `slack` metres of the collider's centre.
        public static bool LookingNear(Collider c, float range, float slack)
        {
            if (LookingAt(c, range)) return true;
            var ray = new Ray(Cam.transform.position, Cam.transform.forward);
            var to = c.bounds.center - ray.origin;
            float along = Vector3.Dot(to, ray.direction);
            return along > 0 && along < range && Vector3.Cross(ray.direction, to).magnitude < slack;
        }

        // How far along the view a collider is when LookingNear would accept it, else -1.
        public static float LookDistance(Collider c, float range, float slack)
        {
            RaycastHit h;
            if (LookingAt(c, range, out h)) return h.distance;
            var ray = new Ray(Cam.transform.position, Cam.transform.forward);
            var to = c.bounds.center - ray.origin;
            float along = Vector3.Dot(to, ray.direction);
            return along > 0 && along < range && Vector3.Cross(ray.direction, to).magnitude < slack ? along : -1f;
        }

        public static bool LookingAt(Collider c, float range)
        {
            RaycastHit h;
            return LookingAt(c, range, out h);
        }

        public static Transform Find(string path)
        {
            var parts = path.Split('/');
            foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (go.transform.parent != null || go.name != parts[0] || go.hideFlags != HideFlags.None) continue;
                var t = go.transform;
                for (int i = 1; i < parts.Length && t != null; i++) t = t.Find(parts[i]);
                if (t != null) return t;
            }
            return null;
        }


        public static void SetLayerRecursive(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            for (int i = 0; i < t.childCount; i++) SetLayerRecursive(t.GetChild(i), layer);
        }

        public static void StripFsms(GameObject go)
        {
            foreach (var f in go.GetComponentsInChildren<PlayMakerFSM>(true)) Object.Destroy(f);
        }

        public static float F(string s) { return float.Parse(s, System.Globalization.CultureInfo.InvariantCulture); }
    }
}
