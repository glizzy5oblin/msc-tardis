using MSCLoader;

namespace MscTardis
{
    // The mod's page in MSCLoader's settings menu. These are about the machine and the player (sound, picture), so they
    // live in MSCLoader's own settings file, not in the save; the computer's SETTINGS page (difficulty, phosphor, key
    // ticks) is per save and stays as it is.
    public static class ModOptions
    {
        public static readonly string[] PortalQualityNames = { "LOW (HALF)", "MEDIUM", "HIGH (FULL)" };
        static readonly float[] PortalScales = { 0.5f, 0.75f, 1f };

        public static float EffectsVolume = 1f;   // the interior's switches, bells, doors-open flight loop
        public static float HumVolume = 1f;       // the room's constant hum
        public static bool BetaConsoles;          // the Toyota and 2010 consoles: off, only the coral console is built

        static SettingsDropDownList quality;
        static SettingsCheckBox rain, beta;
        static SettingsSlider effects, hum;

        // Setup.ModSettings: build the page.
        public static void Create()
        {
            Settings.AddHeader("Sound");
            effects = Settings.AddSlider("effectsVolume", "Interior effects volume", 0f, 1f, 1f, Apply, 2);
            hum = Settings.AddSlider("humVolume", "Interior hum volume", 0f, 1f, 1f, Apply, 2);
            Settings.AddHeader("Picture");
            Settings.AddText("The doorway portals draw the outside into the room, and the room into the doorway. A lower setting draws fewer pixels: faster, but softer.");
            quality = Settings.AddDropDownList("portalQuality", "Doorway portal quality", PortalQualityNames, 2, Apply);
            rain = Settings.AddCheckBox("rainThrough", "Rain seen through the open doors", true, Apply);
            Settings.AddHeader("Consoles");
            Settings.AddText("The coral console is the finished one. The Toyota and 2010 consoles are still in beta: their controls and the computer are not fully worked out. Turn this on to be able to switch to them. Turning it off puts you back in the coral console the next time the interior is built.");
            beta = Settings.AddCheckBox("betaConsoles", "Enable beta consoles", false, Apply);
            Settings.AddHeader("About");
            Settings.AddText("Difficulty, phosphor colour and key ticks are on the console computer's SETTINGS page, and are kept per save.");
        }

        // Setup.ModSettingsLoaded, and each time a setting changes: copy the values to where the mod reads them.
        public static void Apply()
        {
            if (effects != null) EffectsVolume = effects.GetValue();
            if (hum != null) HumVolume = hum.GetValue();
            if (quality != null) Doorway.Scale = PortalScales[UnityEngine.Mathf.Clamp(quality.GetSelectedItemIndex(), 0, PortalScales.Length - 1)];
            if (rain != null) Doorway.RainThrough = rain.GetValue();
            if (beta != null) BetaConsoles = beta.GetValue();
        }
    }
}
