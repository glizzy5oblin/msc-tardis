using System;
using System.Collections.Generic;
using MSCLoader;
using UnityEngine;

namespace MscTardis
{
    // The TARDIS computer: look at the room's monitor and press Use, and a CRT-style screen comes up over the game (Terminal.cs) with
    // the menu pages: status, subsystems, protocols, settings and the reference. The keyboard drives it: arrows (or W/A/S/D), Enter,
    // Backspace, Page Up / Down; or the mouse: the wheel selects, a click enters, a right click goes back. Use or Esc closes it. The game keeps running
    // behind it, and the room monitor (and the keyboard under it) show its last page.
    static class Computer
    {
        enum Page { Boot, Main, Status, Navigation, Subsystems, Protocols, Scanner, Settings, Reference, Text }

        public static bool Open { get; private set; }
        // Opened from the keyboard: no overlay, the page is worked live on the room monitor, the player held and looking at it.
        public static bool OnMonitor { get; private set; }
        static bool closing;
        static int closedFrame = -10;
        // The game's own menu opens on Esc: its "Open Menu" FSM (Systems/Options) is held off while the computer is up, and given back a moment after.
        static PlayMakerFSM menuFsm;
        static float menuBackAt = -1f;
        static Page page = Page.Main;
        static int sel, scroll, openFrame = -1;
        static float openedAt, revealStart, nextRefresh, bootStart;
        static string refTitle, navNote = "";
        static int statusPane;
        static string[] refLines;
        static AudioSource beeper;

        public static readonly string[] DifficultyNames = { "EASY", "ADVANCED" };
        const int ListTop = 3, ListRows = 15;   // the page body: rows 3..17, the footer rule on 19, the hint line on 20

        static readonly string[] MainItems = { "STATUS", "NAVIGATION", "SUBSYSTEMS", "PROTOCOLS", "SCANNER", "SETTINGS", "REFERENCE", "LOG OUT" };
        static readonly string[] RefTitles = { "CONTROLS", "FLYING", "PILOTING KEYS", "POWER AND REPAIR", "PROTOCOLS", "SUBSYSTEMS" };

        public static void Build(Transform root)
        {
            Open = false;
            closing = false;
            Terminal.Open = 0f;
            if (beeper != null) UnityEngine.Object.Destroy(beeper);
            beeper = root.gameObject.AddComponent<AudioSource>();
            beeper.spatialBlend = 0f;
            beeper.playOnAwake = false;
            beeper.volume = 0.5f;
            if (!initDone) { Terminal.Init(); initDone = true; }
            Terminal.Phosphor = Mathf.Clamp(MscTardis.Data.Phosphor, 0, 2);
            idleNext = 0f;
            ShowIdle();
        }
        static bool initDone;

        // The scene was reloaded: the glyph atlas and its textures died with it.
        public static void Reset() { scanCam = null; scanTex = null; initDone = false; Open = false; OnMonitor = false; closing = false; menuFsm = null; menuBackAt = -1f; }

        // ---------------------------------------------------------------------------------------------------------
        // Opening and closing.

        public static string TryOpen(bool onMonitor = false)
        {
            if (Open) return null;
            if (Time.frameCount <= closedFrame + 1) return null;   // the click or Use that just closed it must not open it again
            var d = MscTardis.Data;
            if (!d.Power) return "NO POWER";
            if (Pilot.Active) return "LEAVE THE CONTROLS FIRST";
            if (G.Seated) return "NOT WHILE SEATED";
            if (!d.Inside) return "not inside";
            Open = true;
            OnMonitor = onMonitor;
            closing = false;
            page = Page.Boot;
            sel = scroll = 0;
            openFrame = Time.frameCount;
            openedAt = bootStart = revealStart = Time.time;
            nextRefresh = 0f;
            PlayerHold.Add("computer");
            HoldMenu(true);
            Beep(660f, 0.08f);
            return null;
        }

        public static void Close()
        {
            if (!Open) return;
            Open = false;
            OnMonitor = false;
            closing = true;
            closedFrame = Time.frameCount;
            PlayerHold.Remove("computer");
            menuBackAt = Time.time + 0.3f;
            Beep(440f, 0.08f);
            G.Interaction("");
            idleNext = 0f;
            ShowIdle();
        }

        static void HoldMenu(bool hold)
        {
            if (menuFsm == null)
            {
                var t = G.Find("Systems/Options");
                menuFsm = t != null ? t.GetPlayMaker("Open Menu") : null;
            }
            if (menuFsm != null) menuFsm.enabled = !hold;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Per frame.

        public static void Tick()
        {
            // The CRT's power-on and collapse, about a quarter of a second each way.
            float target = Open && !OnMonitor ? 1f : 0f;
            if (Terminal.Open != target) Terminal.Open = Mathf.MoveTowards(Terminal.Open, target, Time.unscaledDeltaTime / 0.25f);
            else if (!Open) closing = false;
            TickScanner();
            if (menuBackAt >= 0f && Time.time >= menuBackAt) { menuBackAt = -1f; HoldMenu(false); }
            if (!Open) { TickIdle(); return; }

            var d = MscTardis.Data;
            if (!d.Inside || !d.Power || Pilot.Active || G.Seated) { Close(); return; }
            PlayerHold.Apply();
            Tardis.ClearPrompt();

            if (Time.frameCount != openFrame)
            {
                if (G.UsePressed || Input.GetKeyDown(KeyCode.Escape)) { Close(); return; }
                if (Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.W)) { if (Press("up")) return; }
                if (Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.S)) { if (Press("down")) return; }
                if (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.A)) { if (Press("left")) return; }
                if (Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.D)) { if (Press("right")) return; }
                if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) { if (Press("enter")) return; }
                if (Input.GetKeyDown(KeyCode.Backspace)) { if (Press("back")) return; }
                if (Input.GetKeyDown(KeyCode.PageUp)) Press("pgup");
                if (Input.GetKeyDown(KeyCode.PageDown)) Press("pgdn");
                // The mouse: the wheel moves the selection (or scrolls a page of text), a click opens or changes the selected line and a right
                // click goes back (and logs out from the main menu).
                float wheel = Input.GetAxis("Mouse ScrollWheel");
                if (wheel > 0.01f) { if (Press("wheelup")) return; } else if (wheel < -0.01f) { if (Press("wheeldn")) return; }
                if (G.Click) { if (Press("enter")) return; }
                if (Input.GetMouseButtonDown(1)) { if (Press("back")) return; }
            }

            if (Time.time >= nextRefresh || page == Page.Boot || Time.time - revealStart < 0.9f)
            {
                nextRefresh = Time.time + 0.25f;
                Redraw();
            }
        }


        // A key press. True when it closed the computer (nothing else should be handled this frame).
        static bool Press(string key)
        {
            if (page == Page.Boot)
            {
                page = Page.Main;
                sel = 0;
                revealStart = Time.time;
                Click();
                Redraw();
                return false;
            }
            switch (key)
            {
                case "up": Move(-1); break;
                case "down": Move(1); break;
                case "wheelup": if (ItemCount() == 0) Scroll(-3); else Move(-1); break;
                case "wheeldn": if (ItemCount() == 0) Scroll(3); else Move(1); break;
                case "pgup": Scroll(-ListRows + 1); break;
                case "pgdn": Scroll(ListRows - 1); break;
                case "left": Change(-1); break;
                case "right": Change(1); break;
                case "enter": Enter(); break;
                case "back":
                    if (page == Page.Main) { Close(); return true; }
                    GoTo(page == Page.Text ? Page.Reference : Page.Main, page == Page.Text ? Array.IndexOf(RefTitles, refTitle) : (int)page - 2);
                    break;
                case "esc": Close(); return true;
            }
            Redraw();
            return false;
        }

        static void GoTo(Page p, int select = 0)
        {
            page = p;
            sel = Mathf.Max(0, select);
            scroll = 0;
            revealStart = Time.time;
            Click();
        }

        static int ItemCount()
        {
            switch (page)
            {
                case Page.Main: return MainItems.Length;
                case Page.Settings: return 3;
                case Page.Navigation: return TardisInterior.SelectorSeq().Count;
                case Page.Protocols: return Protocols.Table.Length;
                case Page.Reference: return RefTitles.Length;
                default: return 0;
            }
        }

        static void Move(int dir)
        {
            int n = ItemCount();
            if (n == 0) { Scroll(dir); return; }
            sel = (sel + dir + n) % n;
            Click();
        }

        static void Scroll(int by)
        {
            if (page == Page.Text) scroll = Mathf.Clamp(scroll + by, 0, Mathf.Max(0, refLines.Length - ListRows));
        }

        static void Enter()
        {
            switch (page)
            {
                case Page.Main:
                    if (sel == MainItems.Length - 1) { Close(); return; }
                    GoTo((Page)(sel + 2));
                    if (page == Page.Navigation) sel = Mathf.Max(0, TardisInterior.SelectorSeq().IndexOf(MscTardis.Data.Selector));
                    break;
                case Page.Navigation:
                    {
                        var seq = TardisInterior.SelectorSeq();
                        int v = seq[Mathf.Clamp(sel, 0, seq.Count - 1)];
                        if (v != Tardis.Places.Length && !Subsystems.Up(Subsystems.Navigation)) { navNote = "NAVIGATION OFFLINE: MANUAL ONLY"; Beep(220f, 0.15f); break; }
                        MscTardis.Data.Selector = v;
                        navNote = "DESTINATION SET";
                        Click();
                        break;
                    }
                case Page.Settings:
                case Page.Status:
                case Page.Scanner: Change(1); break;
                case Page.Reference:
                    refTitle = RefTitles[sel];
                    refLines = Wrap(RefText(refTitle), Terminal.Cols - 4);
                    GoTo(Page.Text, sel);
                    sel = 0;
                    break;
            }
        }

        static void Change(int dir)
        {
            if (page == Page.Scanner) { scanYaw += dir * 30f; Click(); return; }
            if (page == Page.Status) { statusPane = 1 - statusPane; Click(); return; }
            if (page != Page.Settings) return;
            var d = MscTardis.Data;
            if (sel == 0) d.Difficulty = (d.Difficulty + dir + DifficultyNames.Length) % DifficultyNames.Length;
            else if (sel == 1) { d.Phosphor = (d.Phosphor + dir + 3) % 3; Terminal.Phosphor = d.Phosphor; }
            else d.KeySounds = !d.KeySounds;
            Click();
        }

        // ---------------------------------------------------------------------------------------------------------
        // The screen with the computer shut: the room monitor shows the page it was left on, redrawn twice a second so STATUS stays live.

        static float idleNext;

        static void TickIdle()
        {
            if (Time.time < idleNext) return;
            idleNext = Time.time + 0.5f;
            if (MscTardis.Data.Inside && MscTardis.Data.Power) ShowIdle();
        }

        static void ShowIdle()
        {
            if (!initDone) return;
            if (page == Page.Boot || page == Page.Scanner) { page = Page.Main; sel = 0; }
            revealStart = Time.time - 100f;   // whole, not typing itself in
            Redraw();
        }

        // ---------------------------------------------------------------------------------------------------------
        // Sounds.

        static void Click() { if (beeper != null && MscTardis.Data.KeySounds) beeper.PlayOneShot(Sfx.Beep(1100f, 0.025f), 0.6f); }
        static void Beep(float hz, float s) { if (beeper != null) beeper.PlayOneShot(Sfx.Beep(hz, s), 0.8f); }

        // ---------------------------------------------------------------------------------------------------------
        // Drawing the pages.

        static void Redraw()
        {
            Terminal.Clear();
            if (page == Page.Boot) DrawBoot();
            else
            {
                DrawFrame();
                switch (page)
                {
                    case Page.Main: DrawMain(); break;
                    case Page.Status: DrawStatus(); break;
                    case Page.Navigation: DrawNavigation(); break;
                    case Page.Subsystems: DrawSubsystems(); break;
                    case Page.Scanner: DrawScanner(); break;
                    case Page.Protocols: DrawProtocols(); break;
                    case Page.Settings: DrawSettings(); break;
                    case Page.Reference: DrawReferenceList(); break;
                    case Page.Text: DrawText(); break;
                }
                // Pages type themselves in; the footer and frame come with them.
                float sec = Time.time - revealStart;
                int limit = page == Page.Status ? -1 : Mathf.RoundToInt(sec * 900f);
                if (limit >= 0 && limit < 700)
                {
                    int last = Terminal.Mask(limit);
                    if (last >= 0 && (int)(Time.time * 4f) % 2 == 0) Terminal.Put(last % Terminal.Cols + 1, last / Terminal.Cols, "█");
                }
                else if ((int)(Time.time * 2f) % 2 == 0) Terminal.Put(Terminal.Cols - 2, 20, "█", Terminal.Dim);
            }
            Terminal.Flush();
        }

        static void DrawFrame()
        {
            string title = " TARDIS CONSOLE COMPUTER";
            Terminal.Put(0, 0, title.PadRight(Terminal.Cols), Terminal.Inverse);
            string where = page == Page.Main ? "MAIN" : page == Page.Text ? "REFERENCE" : page.ToString().ToUpperInvariant();
            Terminal.Put(Terminal.Cols - where.Length - 2, 0, where, Terminal.Inverse);
            Terminal.Put(0, 1, new string('═', Terminal.Cols));
            Terminal.Put(0, 19, new string('─', Terminal.Cols));
            string hint = page == Page.Main ? "↑↓ OR WHEEL SELECT  CLICK OPEN  R-CLICK LOG OUT" : page == Page.Text ? "↑↓ PGUP PGDN OR WHEEL SCROLL  BKSP OR R-CLICK BACK" :
                page == Page.Status ? "L/R OR CLICK READINGS OR CHECKLIST  BKSP BACK" : page == Page.Settings ? "↑↓ SELECT  ←→ OR CLICK CHANGE  BKSP BACK" : page == Page.Navigation ? "↑↓ OR WHEEL SELECT  ENTER OR CLICK SET  BKSP BACK" : page == Page.Protocols ? "↑↓ OR WHEEL SELECT  BKSP BACK  USE / ESC CLOSE" : page == Page.Reference ? "↑↓ OR WHEEL SELECT  ENTER OR CLICK OPEN  BKSP BACK" : "BKSP OR R-CLICK BACK  USE / ESC CLOSE";
            hint = hint.Replace("↑↓", "UP/DN").Replace("←→", "L/R");
            Terminal.Put(1, 20, hint, Terminal.Dim);
        }

        const float BootLine = 0.08f, BootHold = 0.25f;   // seconds per boot line, and the pause on the last one (about a second in all)

        static void DrawBoot()
        {
            float t = Time.time - bootStart;
            string[] lines =
            {
                "TARDIS CONSOLE COMPUTER",
                "TYPE 40 TT CAPSULE",
                "",
                "CHECKING ARTRON CELLS ........ OK",
                "CHECKING TIME ROTOR .......... OK",
                "CHECKING NAVIGATION .......... OK",
                "CHECKING LIFE SUPPORT ........ OK",
                "",
                "WELCOME, PILOT",
            };
            for (int i = 0; i < lines.Length; i++)
            {
                if (t < i * BootLine) break;
                Terminal.Put(2, 3 + i, lines[i], i == 0 || i == lines.Length - 1 ? Terminal.Bright : Terminal.Normal);
            }
            if (t > lines.Length * BootLine + BootHold) { page = Page.Main; revealStart = Time.time; Redraw(); return; }
            Terminal.Put(2, 19, "PRESS ANY KEY", Terminal.Dim);
        }

        static void DrawMain()
        {
            Terminal.Put(2, 2, "MAIN MENU", Terminal.Bright);
            string[] notes = { "TARDIS STATE AND READINGS", "PICK A DESTINATION", "HEALTH AND BREAKERS", "WHAT EACH ID DOES", "VIEW OF OUTSIDE", "DIFFICULTY, SCREEN, SOUND", "HOW THE CONSOLE WORKS", "END THE SESSION" };
            for (int i = 0; i < MainItems.Length; i++)
            {
                int y = 4 + i * 2;
                bool on = i == sel;
                Terminal.Put(2, y, (on ? "► " : "  ") + MainItems[i].PadRight(14), on ? Terminal.Inverse : Terminal.Normal);
                if (on) Terminal.Fill(2, y, 50, Terminal.Inverse);
                Terminal.Put(19, y, notes[i], on ? Terminal.Inverse : Terminal.Dim);
            }
        }

        static void DrawNotInstalled(string what)
        {
            Terminal.Center(9, what, Terminal.Bright);
            Terminal.Center(11, "NOT INSTALLED");
        }

        static void DrawNavigation()
        {
            var d = MscTardis.Data;
            Terminal.Put(2, 3, "NAVIGATION", Terminal.Bright);
            var seq = TardisInterior.SelectorSeq();
            for (int i = 0; i < seq.Count; i++)
            {
                int v = seq[i];
                bool on = i == sel;
                string name = v < Tardis.Places.Length ? Tardis.Places[v].Name : v == Tardis.Places.Length ? "MANUAL COORDINATES" : Tardis.Extras[v - Tardis.Places.Length - 1].Name;
                string tag = v == d.Selector ? "◄ SET" : "";
                Terminal.Put(2, 5 + i, (on ? "► " : "  ") + name.PadRight(30) + tag, on ? Terminal.Inverse : Terminal.Normal);
                if (on) Terminal.Fill(2, 5 + i, 50, Terminal.Inverse);
            }
            Terminal.Put(2, 15, "NOW " + TardisInterior.SelectorName() + "   STEP " + TardisInterior.Steps[d.StepIndex] + " M", Terminal.Dim);
            Terminal.Put(2, 16, "COORDINATES, STEP AND NEW PRESETS ARE SET ON THE CONSOLE.", Terminal.Dim);
            if (navNote.Length > 0) Terminal.Put(2, 17, navNote, Terminal.Bright);
            if (!Protocols.TelepathicOn) Terminal.Put(2, 18, "MORE PLACES: TELEPATHIC JUMP PROTOCOL C1273.", Terminal.Dim);
        }

        static void DrawProtocols()
        {
            Terminal.Put(2, 3, "PROTOCOLS", Terminal.Bright);
            Terminal.Put(2, 4, "ID", Terminal.Dim);
            Terminal.Put(9, 4, "PROTOCOL", Terminal.Dim);
            Terminal.Put(36, 4, "STATE", Terminal.Dim);
            const int rows = 11;
            int n = Protocols.Table.Length;
            if (sel < scroll) scroll = sel;
            if (sel >= scroll + rows) scroll = sel - rows + 1;
            for (int r = 0; r < rows; r++)
            {
                int i = scroll + r;
                if (i >= n) break;
                var p = Protocols.Table[i];
                bool on = i == sel;
                bool act = Protocols.IsActive(i);
                string state = Protocols.StateText(i);
                Terminal.Put(2, 5 + r, (on ? "►" : " ") + p.Id.PadRight(10) + p.Name.PadRight(24) + state, on ? Terminal.Inverse : act ? Terminal.Bright : state == "UNAVAILABLE" ? Terminal.Dim : Terminal.Normal);
                if (on) Terminal.Fill(2, 5 + r, 50, Terminal.Inverse);
            }
            if (scroll > 0) Terminal.Put(Terminal.Cols - 2, 5, "▲", Terminal.Dim);
            if (scroll + rows < n) Terminal.Put(Terminal.Cols - 2, 15, "▼", Terminal.Dim);
            var sp = Protocols.Table[Mathf.Clamp(sel, 0, n - 1)];
            var why = Protocols.Unavailable(Mathf.Clamp(sel, 0, n - 1));
            Terminal.Put(2, 17, sp.Desc, Terminal.Normal);
            Terminal.Put(2, 18, why != null ? "NOT NOW: " + why : "ENTER " + sp.Id + " ON THE KEYPAD, THEN #.", Terminal.Dim);
        }

        // The exterior scanner: a camera on the box, drawn into the middle of the screen.
        static Camera scanCam;
        static RenderTexture scanTex;
        static float scanYaw, scanNext;

        static void DrawScanner()
        {
            Terminal.Put(2, 3, "EXTERIOR SCANNER", Terminal.Bright);
            if (!Protocols.ScannerOn) { Terminal.Center(10, "SCANNER OFFLINE", Terminal.Bright); Terminal.Center(12, "ENTER PROTOCOL 3 ON THE KEYPAD", Terminal.Dim); return; }
            if (Tardis.Busy) { Terminal.Center(10, "NO SIGNAL", Terminal.Bright); return; }
            Terminal.Box(2, 4, 52, 14, false, Terminal.Dim);
            Terminal.OverlayCells = new Rect(3, 5, 50, 12);
            Terminal.Put(3, 18, "HEADING " + Mathf.RoundToInt(Mathf.Repeat(Tardis.Yaw + scanYaw, 360f)).ToString("000") + "   L/R TURN THE VIEW", Terminal.Dim);
        }

        static void TickScanner()
        {
            bool want = Open && page == Page.Scanner && Protocols.ScannerOn && !Tardis.Busy && (Terminal.Open > 0.95f || OnMonitor);
            if (!want) { Terminal.Overlay = null; return; }
            if (scanCam == null)
            {
                scanTex = new RenderTexture(400, 240, 16);
                var go = new GameObject("MscTardisScanner");
                scanCam = go.AddComponent<Camera>();
                scanCam.targetTexture = scanTex;
                scanCam.fieldOfView = 70f;
                scanCam.nearClipPlane = 0.1f;
                scanCam.farClipPlane = 600f;
                scanCam.clearFlags = CameraClearFlags.Skybox;
                scanCam.enabled = false;
            }
            Terminal.Overlay = scanTex;
            if (Time.time < scanNext) return;
            scanNext = Time.time + 0.1f;
            var dir = Quaternion.Euler(0f, Tardis.Yaw + scanYaw, 0f) * Vector3.forward;
            scanCam.transform.position = Tardis.Position + Vector3.up * 1.7f + dir * 1.0f;
            scanCam.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
            scanCam.Render();
        }

        static void DrawSubsystems()
        {
            Terminal.Put(2, 3, "SUBSYSTEMS", Terminal.Bright);
            Terminal.Put(2, 5, "SYSTEM", Terminal.Dim);
            Terminal.Put(22, 5, "HEALTH", Terminal.Dim);
            Terminal.Put(38, 5, "BKR", Terminal.Dim);
            Terminal.Put(42, 5, "CIR", Terminal.Dim);
            Terminal.Put(46, 5, "STATE", Terminal.Dim);
            for (int i = 0; i < Subsystems.Count; i++)
            {
                int y = 6 + i;
                var st = Subsystems.StateOf(i);
                int a = st == Subsystems.State.Ok ? Terminal.Normal : st == Subsystems.State.Off ? Terminal.Dim : Terminal.Bright;
                float h = Subsystems.HealthOf(i) / 100f;
                Terminal.Put(2, y, Subsystems.Names[i], a);
                Terminal.Put(22, y, Terminal.Bar(h, 10), a);
                Terminal.Put(33, y, Pct(h).PadLeft(4), a);
                Terminal.Put(38, y, Subsystems.BreakerOn(i) ? "ON" : "TRP", Subsystems.BreakerOn(i) ? Terminal.Normal : Terminal.Bright);
                Terminal.Put(42, y, Subsystems.CircuitOn(i) ? "ON" : "OFF", Subsystems.CircuitOn(i) ? Terminal.Normal : Terminal.Bright);
                Terminal.Put(46, y, Subsystems.StateName(i), a);
                if (st == Subsystems.State.Failed || (Subsystems.MasterOn && !Subsystems.BreakerOn(i))) Terminal.Put(0, y, "!", Terminal.Bright);
            }
            Terminal.Put(2, 13, "MASTER BUS", Terminal.Dim);
            Terminal.Put(22, 13, Subsystems.MasterOn ? "ON" : "OFF", Subsystems.MasterOn ? Terminal.Bright : Terminal.Inverse);
            var ins = Subsystems.Instruction();
            Terminal.Put(2, 15, ins != null ? "DO THIS:" : "ALL SYSTEMS NOMINAL", Terminal.Dim);
            if (ins != null)
            {
                int y = 16;
                foreach (var l in Wrap(ins, Terminal.Cols - 4)) { Terminal.Put(2, y++, l, Terminal.Bright); if (y > 17) break; }
            }
        }

        static string Pct(float v) { return Mathf.RoundToInt(v * 100f) + "%"; }

        static void DrawStatus()
        {
            var d = MscTardis.Data;
            bool alerts = StatusExtras().Count > 0;
            Terminal.Put(2, 2, statusPane == 0 ? "[READINGS]  CHECKLIST AND ALERTS" + (alerts ? " !" : "") : " READINGS  [CHECKLIST AND ALERTS]" + (alerts ? " !" : ""), Terminal.Dim);
            if (statusPane == 1) { DrawChecklist(); return; }
            int y = 3;
            Action<string, string, string, string> two = (a, av, b, bv) =>
            {
                Terminal.Put(2, y, a.PadRight(11), Terminal.Dim);
                // 17 places to the right column: a long MANUAL destination ("MANUAL -1422 / 1245") is shortened to fit.
                if (av.Length > 17) av = av.Replace("MANUAL ", "MAN ");
                if (av.Length > 17) av = av.Replace(" / ", "/");
                if (av.Length > 17) av = av.Substring(0, 17);
                Terminal.Put(13, y, av.PadRight(18), Terminal.Bright);
                if (b != null) { Terminal.Put(31, y, b.PadRight(9), Terminal.Dim); Terminal.Put(40, y, bv, Terminal.Bright); }
                y++;
            };
            two("STATE", Tardis.State.ToString().ToUpperInvariant(), "LOCATION", Tardis.Position.x.ToString("0") + " / " + Tardis.Position.z.ToString("0"));
            two("DEST", TardisInterior.SelectorName(), "STEP", TardisInterior.Steps[d.StepIndex] + " M");
            two("HANDBRAKE", d.Handbrake ? "ON" : "OFF", "POWER", d.Power ? "ON" : "OFF");
            y++;
            Action<string, float, string> bar = (label, v, text) =>
            {
                Terminal.Put(2, y, label.PadRight(11), Terminal.Dim);
                Terminal.Put(13, y, Terminal.Bar(v, 24), Terminal.Normal);
                Terminal.Put(38, y, text, Terminal.Bright);
                y += 2;   // a row between bars: full blocks fill their cells, so bars on touching rows ran together into one slab
            };
            bar("STABILITY", Tardis.Stability, Pct(Tardis.Stability));
            bar("ARTRON", Systems.Artron / Systems.ArtronMax, Systems.Artron.ToString("0"));
            bar("HULL", Systems.HealthPercent / 100f, Systems.HealthPercent.ToString("0") + "%");
            two("HADS", d.Hads ? "ARMED" : "OFF", "DOORS", (d.DoorHeldOpen ? "OPEN" : "CLOSED") + (d.DoorLocked ? " LOCKED" : ""));
            two("FLIGHT", Flight.On ? "ON" : "OFF", "FLOAT", Flight.FloatOn ? "ON" : "OFF");
            two("SPIN", Flight.SpinName(), "INTERIOR", TardisInterior.Rooms[d.Room]);
            if (Tardis.State != Tardis.Phase.Idle) two("HEADING", Tardis.Heading ?? "-", "PROGRESS", Pct(Tardis.Progress));
            y++;
        }

        static void DrawChecklist()
        {
            Terminal.Put(2, 3, "TAKE-OFF CHECKLIST", Terminal.Bright);
            for (int i = 0; i < Takeoff.Names.Length; i++)
            {
                bool ok = Takeoff.Done(i);
                Terminal.Put(2 + (i % 2) * 27, 4 + i / 2, (ok ? "[X] " : "[ ] ") + Takeoff.Names[i], ok ? Terminal.Normal : Terminal.Bright);
            }
            Terminal.Put(2, 8, "THEN THROW THE TAKE-OFF CONTROL." + (Takeoff.Enforce ? "" : "  (CHECKLIST OFF)"), Terminal.Dim);
            Terminal.Put(2, 10, "ALERTS", Terminal.Bright);
            int y = 11;
            var gp = FlightGame.PromptText;
            if (gp != null) { Terminal.Put(2, y++, gp + " NOW: " + Mathf.Max(0, Mathf.CeilToInt(FlightGame.Until - Time.time)) + "S", Terminal.Inverse); Terminal.Fill(2, y - 1, 50, Terminal.Inverse); }
            else if (FlightGame.Active) Terminal.Put(2, y++, "FLIGHT: HITS " + FlightGame.Hits + "  MISSES " + FlightGame.Misses, Terminal.Normal);
            foreach (var l in StatusExtras()) { if (y > 17) break; Terminal.Put(2, y++, l.Length > 52 ? l.Substring(0, 52) : l, Terminal.Normal); }
            if (y == 11) Terminal.Put(2, y, "NONE", Terminal.Dim);
        }

        // Lines added to the status page by systems built on top of the computer (faults, take-off steps, a flight prompt).
        static List<string> StatusExtras()
        {
            var l = new List<string>();
            var ins = Subsystems.FaultLine();
            if (ins != null) l.Add("! " + ins);
            var act = new List<string>();
            for (int i = 0; i < Protocols.Table.Length; i++) if (Protocols.IsActive(i)) act.Add(Protocols.Table[i].Id);
            if (act.Count > 0) l.Add("PROTOCOLS " + string.Join(" ", act.ToArray()));
            if (Protocols.DestructLeft >= 0f) l.Add("SELF-DESTRUCT T-" + Mathf.CeilToInt(Protocols.DestructLeft));
            var loc = Protocols.LocatorLine();
            if (loc != null) l.Add(loc);
            var scan = Protocols.ScanReport;
            if (scan != null) foreach (var s in scan) { l.Add(s); if (l.Count > 6) break; }
            return l;
        }

        

        static void DrawSettings()
        {
            var d = MscTardis.Data;
            Terminal.Put(2, 3, "SETTINGS", Terminal.Bright);
            string[] names = { "DIFFICULTY", "PHOSPHOR", "KEY SOUNDS" };
            string[] vals = { DifficultyNames[Mathf.Clamp(d.Difficulty, 0, 1)], Terminal.PhosphorNames[Mathf.Clamp(d.Phosphor, 0, 2)], d.KeySounds ? "ON" : "OFF" };
            for (int i = 0; i < 3; i++)
            {
                int y = 5 + i * 2;
                bool on = i == sel;
                Terminal.Put(2, y, (on ? "► " : "  ") + names[i].PadRight(14) + "< " + vals[i].PadRight(9) + ">", on ? Terminal.Inverse : Terminal.Normal);
                if (on) Terminal.Fill(2, y, 50, Terminal.Inverse);
            }
            string note = sel == 0 ? "HOW HARD THE TAKE-OFF AND THE FLIGHT ARE." : sel == 1 ? "THE COLOUR OF THE SCREEN." : "THE TICK WHEN A KEY IS PRESSED.";
            Terminal.Put(2, 12, note, Terminal.Dim);
            if (sel == 0)
            {
                Terminal.Put(2, 14, d.Difficulty == 0 ? "EASY: " + Secs(FlightGame.EasyWindow) + " SECOND WINDOWS, THE BOARD'S CAPS AND LEVERS, AUTOPILOT ARMS THE TAKE-OFF." : "ADVANCED: " + Secs(FlightGame.AdvancedWindow) + " SECOND WINDOWS, ANY CONTROL, NO AUTOPILOT.", Terminal.Dim);
                Terminal.Put(2, 15, "THE FLIGHT MINIGAME RUNS THROUGH A CONSOLE-FLOWN FLIGHT, ABOUT A MINUTE AND A HALF.", Terminal.Dim);
            }
        }

        static string Secs(float s) { return s.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture); }

        static void DrawReferenceList()
        {
            Terminal.Put(2, 3, "REFERENCE", Terminal.Bright);
            for (int i = 0; i < RefTitles.Length; i++)
            {
                int y = 5 + i * 2;
                bool on = i == sel;
                Terminal.Put(2, y, (on ? "► " : "  ") + RefTitles[i].PadRight(30), on ? Terminal.Inverse : Terminal.Normal);
                if (on) Terminal.Fill(2, y, 50, Terminal.Inverse);
            }
        }

        static void DrawText()
        {
            Terminal.Put(2, 2, refTitle, Terminal.Bright);
            for (int i = 0; i < ListRows; i++)
            {
                int k = scroll + i;
                if (k >= refLines.Length) break;
                Terminal.Put(2, 3 + i, refLines[k]);
            }
            if (refLines.Length > ListRows)
            {
                Terminal.Put(Terminal.Cols - 3, 3, scroll > 0 ? "▲" : " ", Terminal.Dim);
                Terminal.Put(Terminal.Cols - 3, 17, scroll + ListRows < refLines.Length ? "▼" : " ", Terminal.Dim);
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Text.

        static string[] Wrap(string text, int width)
        {
            var outl = new List<string>();
            foreach (var para in text.Split('\n'))
            {
                if (para.Length == 0) { outl.Add(""); continue; }
                string indent = "";
                int i = 0;
                while (i < para.Length && para[i] == ' ') { indent += " "; i++; }
                var line = indent;
                foreach (var word in para.Substring(i).Split(' '))
                {
                    if (line.Length > indent.Length && line.Length + 1 + word.Length > width) { outl.Add(line); line = indent; }
                    line += (line.Length > indent.Length ? " " : "") + word;
                }
                outl.Add(line);
            }
            return outl.ToArray();
        }

        static string RefText(string title)
        {
            switch (title)
            {
                case "CONTROLS":
                    return "LOOK AT A CONTROL AND PRESS USE.\nWITHOUT POWER ONLY POWER, REPAIR AND THE DOOR LOCK WORK.\n\n" +
                           "SELECTOR   PICKS THE DESTINATION: HOME YARD, PERAJARVI, TEIMO'S SHOP OR MANUAL.\n" +
                           "STEP       HOW FAR A COORDINATE BUTTON MOVES THE MANUAL TARGET: 10, 100 OR 1000 M.\n" +
                           "X- X+ Z- Z+ MOVE THE MANUAL TARGET.\n" +
                           "HANDBRAKE  MUST BE OFF TO TAKE OFF OR FLY FREELY. ON, IT RECHARGES THE FUEL.\n" +
                           "TAKE OFF   STARTS THE FLIGHT. ON THE CORAL CONSOLE IT IS THE CRANK BY THE MONITOR.\n" +
                           "STABILISER BRINGS STABILITY BACK UP DURING A FLIGHT.\n" +
                           "POWER      SWITCHES THE TARDIS ON AND OFF.\n" +
                           "PILOT      TAKES THE CONTROLS FOR HAND FLYING.\n" +
                           "FLIGHT     FREE FLIGHT ON OR OFF.   FLOAT  HOVER ON OR OFF.\n" +
                           "SPIN       NONE, CLOCKWISE OR ANTICLOCKWISE.\n" +
                           "DOOR HOLDS THE DOORS OPEN.   LOCK  LOCKS THEM.\n" +
                           "AUTOPILOT  (THE OLD HADS SWITCH) ARMS THE TAKE-OFF STEPS. EASY ONLY.\n" +
                           "PROTOCOL CANCEL (THE OLD FAST RETURN) ENDS THE LATEST PROTOCOL.\n" +
                           "REPAIR     PRIMES A REPAIR. LEAVE AND IT REPAIRS WITH THE DOORS LOCKED.\n" +
                           "INTERIOR   ENTER PROTOCOL " + Protocols.Table[Protocols.Interior].Id + " TO CHANGE THE INTERIOR.";
                case "FLYING":
                    return "1. TURN THE HANDBRAKE OFF.\n2. PICK A DESTINATION WITH THE SELECTOR, OR DIAL COORDINATES.\n3. THROW THE TAKE-OFF CONTROL.\n\n" +
                           "THE TARDIS DEMATERIALISES, FLIES THROUGH THE VORTEX AND MATERIALISES AT THE TARGET. STAY INSIDE UNTIL IT LANDS.\n\n" +
                           "STABILITY DROPS IN FLIGHT. LOW STABILITY LANDS THE TARDIS OFF TARGET, UP TO 30 M AWAY WHEN PILOTED WITH NO STABILISING AT ALL. WORK THE STABILISER DURING THE FLIGHT.\n\n" +
                           "FLYING FROM THE CONSOLE IS A MINIGAME. A CONTROL IS NAMED ON THE MONITOR AND FLASHES, WITH THE WAY TO WORK IT (SLIDE, FLIP, PRESS, THROW, TURN): DO IT WITHIN " + Secs(FlightGame.EasyWindow) + " SECONDS ON EASY, " + Secs(FlightGame.AdvancedWindow) + " ON ADVANCED. LATER IN THE FLIGHT A PROMPT MAY BE A RUN OF TWO OR THREE CONTROLS TO WORK IN ORDER. A HIT BRINGS STABILITY BACK. A MISS OR A WRONG CONTROL TAKES IT AWAY, AND THREE MISSES IN A ROW FORCE AN EMERGENCY LANDING.\n\n" +
                           "LANDING ON THE LAKE FLOATS THE TARDIS AT THE SURFACE.";
                case "PILOTING KEYS":
                    return "PULL THE PILOT LEVER AND THE CAMERA MOVES OUTSIDE.\n\n" +
                           "R           FREE FLIGHT ON / OFF\nW / S       FORWARD / BACK\nA / D       STRAFE\n" +
                           "SPACE/CTRL  UP / DOWN\nSHIFT       BOOST\nALT + A / D ROTATE\nT           HOVER ON / OFF\n" +
                           "J           HANDBRAKE\nP           POWER\nRIGHT MOUSE CYCLE THE SPIN\n" +
                           "H           PLACE A DESTINATION MARKER; PRESS H OR CLICK TO SET IT\n" +
                           "LEFT MOUSE  HOLD AND RELEASE TO AIM AND TAKE OFF; IN THE VORTEX A CLICK LANDS\n" +
                           "USE         LEAVE THE CONTROLS";
                case "PROTOCOLS":
                    {
                        var sb = new System.Text.StringBuilder("TYPE A PROTOCOL'S ID ON THE KEYPAD (1-0, A-D) AND PRESS #. * CLEARS THE ENTRY. SOME BUTTONS ON THE SWITCH BOARD START FLASHING: PRESS THEM ALL, IN ANY ORDER. YOU GET " + Secs(Protocols.ChallengeStart) + " SECONDS, AND EVERY CORRECT PRESS ADDS " + Secs(Protocols.ChallengePerPress) + " MORE, WITH TIME ON TOP TO WALK ROUND TO THE NEXT ONE. A WRONG BUTTON OR RUNNING OUT OF TIME FAILS IT WITH NO HARM. ENTER AN ACTIVE PROTOCOL'S ID AGAIN TO TURN IT OFF. PROTOCOL CANCEL TURNS OFF THE LATEST ONE.\n\nIDS ARE 4 TO 9 CHARACTERS. LONGER IDS MEAN HEAVIER PROTOCOLS AND MORE BUTTONS.\n\n");
                        foreach (var p in Protocols.Table) sb.Append(p.Id.PadRight(11) + p.Name + "\n");
                        return sb.ToString();
                    }
                case "SUBSYSTEMS":
                    return "SIX SYSTEMS RUN THE TARDIS: NAVIGATION, DEMATERIALISATION, SHIELDS, CHAMELEON, TELEPATHIC AND LIFE SUPPORT.\n\n" +
                           "EACH NEEDS THE MASTER BUS, ITS OWN CIRCUIT AND ITS OWN BREAKER SWITCHED ON, ON THE SWITCH BOARD. MASTER IS THE BAT LEVER. CIRCUITS 1-6 AND BREAKERS 1-6 ARE THE FIRST SIX ROCKERS IN THEIR ROWS.\n\n" +
                           "DAMAGE TRIPS A BREAKER. RESET IT BY HAND. BELOW 40% A SYSTEM IS DAMAGED AND HALF AS GOOD. BELOW 10% IT HAS FAILED, AND TRIPS ITS BREAKER AGAIN UNTIL IT IS REPAIRED.\n\n" +
                           "NAVIGATION OFF: PRESETS REFUSE, LANDING DRIFT TRIPLES.\nDEMATERIALISATION OFF: NO TAKE-OFF.\nSHIELDS OFF: DOUBLE HULL DAMAGE.\nTELEPATHIC OFF: NO TELEPATHIC JUMP OR LOCATOR.\n" +
                           "LIFE SUPPORT OFF: EMERGENCY LIGHTING, THE BELL, AND THE AIR WEARS YOU DOWN.";
                default:
                    return "ARTRON ENERGY IS THE FUEL, 0 TO 100. TRAVEL, FLIGHT, BOOST, HOVERING AND HADS SPEND IT. IT RECHARGES SLOWLY WHILE PARKED OR HOVERING. A FLAT BATTERY CUTS THE POWER.\n\n" +
                           "THE HULL STARTS AT 3000. BELOW 40% IT IS DAMAGED AND SMOKES. BELOW 10% IT IS BROKEN: FLIGHT TURNS UNRELIABLE. AT ZERO THE TARDIS IS DESTROYED AND THE POWER GOES OFF UNTIL IT IS REPAIRED.\n\n" +
                           "HADS, WHEN ARMED, MAKES A HIT SEND THE TARDIS UP TO 300 M AWAY, AND BRING IT BACK 10 TO 25 SECONDS LATER.\n\n" +
                           "REPAIR: PRIME IT FROM THE CONSOLE, LEAVE THE TARDIS, AND IT REPAIRS ITSELF WITH THE DOORS LOCKED, TAKING UP TO A MINUTE.";
            }
        }



    }
}
