using MSCLoader;
using UnityEngine;

namespace MscTardis
{
    // Everything the mod keeps in the game's own save (SaveLoad). Add fields over time; older saves load the new
    // ones with their defaults.
    public class SaveData
    {
        public bool Placed;              // false until the TARDIS has first been put in the yard
        public float X, Y, Z, Yaw;       // where the exterior stands
        public bool Inside;              // the player is in the interior
        public int Selector;             // console: index into Tardis.Places, or Places.Length for MANUAL
        public float ManualX, ManualZ;   // console: manual coordinates
        public int StepIndex = 1;        // console: index into TardisInterior.Steps
        public bool Handbrake = true;    // console: must be off to take off
        public bool Power = true;        // console: with it off the room dims and no control works except POWER
        public float Artron = 25f;       // fuel, 0..100; the original starts at a quarter
        public float Health = 3000f;     // hull
        public bool Hads;                // armed: escapes on its own when hit
        public int SpinDir = -1, SpinLast = -1;
        public bool DoorHeldOpen, DoorLocked;
        public int Room;                 // which interior: index into TardisInterior.Rooms
        public bool BookPlaced;          // the guidebook has been put out on the console (then it stays wherever it is left)
        public int Difficulty;           // computer settings: 0 EASY, 1 ADVANCED
        public int Phosphor;             // computer settings: 0 GREEN, 1 AMBER, 2 WHITE
        public bool KeySounds = true;    // computer settings: a tick for each key
        public float[] SubHealth = { 100f, 100f, 100f, 100f, 100f, 100f };   // the six subsystems' health
        public string Protocols = "";    // the toggle protocols that are on, by ID, in the order they were enabled
        public bool Autopilot;           // the AUTOPILOT switch (the old HADS switch)
        public bool MasterOn = true;     // switch board: the main bus
        public int Circuits = 0x3F, Breakers = 0x3F;   // switch board: CIRCUIT 1-6 and BREAKER 1-6, a bit each (on)
        public float BookX, BookY, BookZ, BookQX, BookQY, BookQZ, BookQW = 1f;   // where it is, and how it lies (world)
    }

    public class MscTardis : Mod
    {
        public override string ID => "MscTardis";
        public override string Name => "MSC-TARDIS";
        public override string Version => "0.2.0";
        public override string Author => "glizzy5oblin";
        public override string Description => "Adds the TARDIS: a police box that is bigger on the inside and can fly itself around the map.";

        // The icon in MSCLoader's mod list: assets/icon.png, embedded in the dll (read before any setup runs).
        static byte[] icon;   // read once
        public override byte[] Icon
        {
            get
            {
                if (icon != null) return icon;
                using (var s = typeof(MscTardis).Assembly.GetManifestResourceStream("MscTardis.icon.png"))
                {
                    if (s == null) return null;
                    var b = new byte[s.Length];
                    int n = 0;
                    while (n < b.Length) { int r = s.Read(b, n, b.Length - n); if (r <= 0) break; n += r; }
                    return icon = b;
                }
            }
            set { }
        }

        public static MscTardis Instance;
        public static SaveData Data = new SaveData();

        bool ready;

        // Other hooks: Setup.OnNewGame, OnMenuLoad, PreLoad, PostLoad, OnGUI, FixedUpdate, ModSettings.
        public override void ModSetup()
        {
            Instance = this;
            SetupFunction(Setup.OnLoad, OnLoad);
            SetupFunction(Setup.OnSave, OnSave);
            SetupFunction(Setup.Update, OnUpdate);
            SetupFunction(Setup.OnGUI, OnGUI);
            SetupFunction(Setup.ModSettings, ModOptions.Create);
            SetupFunction(Setup.ModSettingsLoaded, ModOptions.Apply);
        }

        void OnLoad()
        {
            Data = SaveLoad.ValueExists(this, "state") ? SaveLoad.DeserializeClass<SaveData>(this, "state") : new SaveData();
            G.Init();
            Res.Init(ModLoader.GetModAssetsFolder(this));
            Computer.Reset();
            Protocols.Reset();
            Tardis.Build();
            Guidebook.Build();
            ready = true;
            // tools/launch.sh waits for this line to know the save is in.
            ModConsole.Print("<color=cyan>MSC-TARDIS loaded:</color> version " + Version);
        }

        void OnSave()
        {
            if (!ready) return;
            Guidebook.Save();
            Protocols.Save();
            SaveLoad.SerializeClass(this, Data, "state");
        }

        void OnGUI()
        {
            if (ready) Terminal.Draw();
        }

        void OnUpdate()
        {
            if (!ready) return;
            Tardis.Tick();
            Guidebook.Tick();
            G.LateTick();
        }
    }
}
