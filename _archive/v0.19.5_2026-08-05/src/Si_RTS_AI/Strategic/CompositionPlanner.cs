using Si_RTS_AI.Perception;
using System.Collections.Generic;

namespace Si_RTS_AI.Strategic
{
    /// <summary>
    /// Composition Planner — outputs a **desired unit-count-per-type target** for a team,
    /// re-evaluated each tick from the current TeamState. No side effects; pure function.
    /// Consumed by the Unit Production Emitter (Phase 3.1.1) to decide "what to build next".
    ///
    /// MVP: hard-coded per-phase, per-faction targets. Refined in later phases with
    /// Weighting Engine input (eco%/expa%/mil%) and Composition Planner counters.
    ///
    /// Phase 3.1 is diagnostic-only: we compute the target and log it, but don't yet
    /// override stock production. Set Cfg.OverrideProduction = true to make it live.
    /// </summary>
    public static class CompositionPlanner
    {
        public class Target
        {
            // Priority-ordered list. The Emitter picks the first entry whose current
            // count is below its Desired value AND which we can afford / have tech for.
            public List<Entry> Priority = new List<Entry>();
        }

        public class Entry
        {
            public string UnitDisplayName = "?";
            public int    Desired;
            public string Rationale = "";        // human-readable "why" for the log
        }

        // ---------------------------------------------------------------------
        // MVP compositions. Numbers roughly reflect what a competent Sol commander
        // aims for — hard-coded until Weighting Engine can shape them.
        // ---------------------------------------------------------------------

        public static Target For(TeamState state)
        {
            switch (state.Faction)
            {
                case "Sol":
                case "Centauri":  return HumanTarget(state);
                case "Alien":     return AlienTarget(state);
                default:          return new Target();
            }
        }

        static Target HumanTarget(TeamState s)
        {
            var t = new Target();
            switch (s.Phase)
            {
                case Phase.Early:
                    // Focus: harvesters + scouts + core infantry for early skirmish
                    t.Priority.Add(new Entry { UnitDisplayName = "Harvester",   Desired = 4,  Rationale = "eco floor" });
                    t.Priority.Add(new Entry { UnitDisplayName = "Scout",       Desired = 4,  Rationale = "early scouting" });
                    t.Priority.Add(new Entry { UnitDisplayName = "Rifleman",    Desired = 8,  Rationale = "core infantry" });
                    t.Priority.Add(new Entry { UnitDisplayName = "Light Quad",  Desired = 2,  Rationale = "early mobility" });
                    break;
                case Phase.Mid:
                    // Focus: harvesters + combined arms + first heavy
                    t.Priority.Add(new Entry { UnitDisplayName = "Harvester",     Desired = 8,   Rationale = "eco scaling" });
                    t.Priority.Add(new Entry { UnitDisplayName = "Rifleman",      Desired = 12,  Rationale = "core infantry" });
                    t.Priority.Add(new Entry { UnitDisplayName = "Heavy",         Desired = 6,   Rationale = "AT infantry" });
                    t.Priority.Add(new Entry { UnitDisplayName = "Sniper",        Desired = 3,   Rationale = "picks" });
                    t.Priority.Add(new Entry { UnitDisplayName = "Light Striker", Desired = 4,   Rationale = "harass / raid" });
                    t.Priority.Add(new Entry { UnitDisplayName = "Hover Tank",    Desired = 3,   Rationale = "first heavy pressure" });
                    break;
                case Phase.Late:
                    // Focus: siege + AA + air-superiority stack
                    t.Priority.Add(new Entry { UnitDisplayName = "Harvester",   Desired = 10, Rationale = "eco cap" });
                    t.Priority.Add(new Entry { UnitDisplayName = "Siege Tank",  Desired = 4,  Rationale = "base crack" });
                    t.Priority.Add(new Entry { UnitDisplayName = "Hover Tank",  Desired = 6,  Rationale = "main tank line" });
                    t.Priority.Add(new Entry { UnitDisplayName = "Railgun Tank",Desired = 3,  Rationale = "counter armor" });
                    t.Priority.Add(new Entry { UnitDisplayName = "AA Truck",    Desired = 2,  Rationale = "anti-air" });
                    t.Priority.Add(new Entry { UnitDisplayName = "Heavy",       Desired = 12, Rationale = "AT floor" });
                    t.Priority.Add(new Entry { UnitDisplayName = "Rifleman",    Desired = 12, Rationale = "infantry floor" });
                    t.Priority.Add(new Entry { UnitDisplayName = "Gunship",     Desired = 2,  Rationale = "air support" });
                    break;
            }
            return t;
        }

        static Target AlienTarget(TeamState s)
        {
            var t = new Target();
            switch (s.Phase)
            {
                case Phase.Early:
                    // Shrimp target scaled for our v0.6.x eco: aim for ~10-15 shrimps
                    // PER BIOTICS, with up to 4 BCs at round start → 40-60 total. Set
                    // aggressive early so production actually fills that floor before
                    // the AI starts spending on military.
                    t.Priority.Add(new Entry { UnitDisplayName = "Shrimp",       Desired = 50, Rationale = "biotic harvest floor (~12 per BC × 4 BCs)" });
                    t.Priority.Add(new Entry { UnitDisplayName = "Crab",         Desired = 8,  Rationale = "core swarm" });
                    t.Priority.Add(new Entry { UnitDisplayName = "Horned Crab",  Desired = 4,  Rationale = "AT swarm" });
                    break;
                case Phase.Mid:
                    t.Priority.Add(new Entry { UnitDisplayName = "Shrimp",       Desired = 60, Rationale = "biotic scaling" });
                    t.Priority.Add(new Entry { UnitDisplayName = "Crab",         Desired = 14, Rationale = "core swarm" });
                    t.Priority.Add(new Entry { UnitDisplayName = "Horned Crab",  Desired = 8,  Rationale = "AT" });
                    t.Priority.Add(new Entry { UnitDisplayName = "Hunter",       Desired = 6,  Rationale = "flank" });
                    t.Priority.Add(new Entry { UnitDisplayName = "Shocker",      Desired = 6,  Rationale = "anti-infantry" });
                    t.Priority.Add(new Entry { UnitDisplayName = "Wasp",         Desired = 4,  Rationale = "air harass" });
                    break;
                case Phase.Late:
                    t.Priority.Add(new Entry { UnitDisplayName = "Shrimp",       Desired = 70, Rationale = "biotic cap" });
                    t.Priority.Add(new Entry { UnitDisplayName = "Behemoth",     Desired = 3,  Rationale = "base crack" });
                    t.Priority.Add(new Entry { UnitDisplayName = "Crab",         Desired = 16, Rationale = "swarm floor" });
                    t.Priority.Add(new Entry { UnitDisplayName = "Horned Crab",  Desired = 10, Rationale = "AT floor" });
                    t.Priority.Add(new Entry { UnitDisplayName = "Scorpion",     Desired = 4,  Rationale = "specialist AT" });
                    t.Priority.Add(new Entry { UnitDisplayName = "Wasp",         Desired = 6,  Rationale = "air pressure" });
                    break;
            }
            return t;
        }

        // ---------------------------------------------------------------------
        // Phase estimate — very rough MVP heuristic
        // ---------------------------------------------------------------------
        public static Phase EstimatePhase(TeamState s)
        {
            // Structures-owned is a reasonable proxy for build progress.
            int structs = s.StructuresOwned;
            if (structs <= 3)  return Phase.Early;
            if (structs <= 10) return Phase.Mid;
            return Phase.Late;
        }
    }
}
