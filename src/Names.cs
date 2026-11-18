using System.Collections.Generic;

namespace MscTardis
{
    // Names for a handful of the console's spare controls that stand out: the two spare knobs, the coral room's cast lever and plunger, and
    // two of the Toyota's lone switches. They get a name from the Doctor Who lore, a system and a part. Every other spare control (the rows
    // of switches, the tube levers, the knobs and buttons that come in groups) keeps the plain numbered name it was given: SWITCH 9, SPARE LEVER 3.
    // The functional controls keep their own names (CIRCUIT, BREAKER, INDICATOR, SELECT, RESET, KEY, DIAL, the nudge knobs, and RELAY 1-5,
    // the take-off arm row), because the checklist and the guidebook name them.
    static class Names
    {
        static readonly Dictionary<string, string> byKey = new Dictionary<string, string>
        {
            { "d_bp9", "HELMIC REGULATOR" },    // the Toyota's spare knob
            { "d_bp10", "TACHYON CRANK" },      // the Toyota's spare crank
            { "d_lh1", "CHRONON GOVERNOR" },    // the coral room's cast lever
            { "d_cp1", "VORTEX INJECTOR" },     // the coral room's plunger
            { "d_sw22", "ZEITON BYPASS" },
            { "d_sw25", "ARTRON SHUNT" },
        };

        // The name a spare control goes by, or the one it was given where it is not one.
        public static string For(string key, string given)
        {
            string s;
            return byKey.TryGetValue(key, out s) ? s : given;
        }
    }
}
