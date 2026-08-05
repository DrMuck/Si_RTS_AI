using Silica.AI;
using System.Collections.Generic;
using UnityEngine;

namespace Si_RTS_AI.Perception
{
    /// <summary>
    /// Snapshot of everything we know about a team at a given tick. Read-only for the
    /// Strategic layer — Perception is the only writer. Fields kept sparse for MVP;
    /// grow as planners actually consume more.
    /// </summary>
    public class TeamState
    {
        public Team Team = null!;
        public string TeamName = "?";
        public string Faction = "?";                    // "Sol" / "Centauri" / "Alien"

        // From GameEvents.OnStructureSpawned/Destroyed accumulators (Phase2.RoundStats)
        public int StructuresOwned;                     // cumulative built - lost
        public int UnitsOwned;                          // cumulative built - lost
        public Dictionary<string, int> StructureCount = new Dictionary<string, int>();
        public Dictionary<string, int> UnitCount = new Dictionary<string, int>();

        // From Phase2 handler observability
        public float AvgBuildableStructures;            // last-window average
        public float AvgBuildableUnits;
        public int   LastMissingResources;              // most recent tick's value
        public int   RecentZeroBuildableTickPct;        // % of recent ticks where 0 could be built

        // Phase estimate (Early / Mid / Late) — filled by CompositionPlanner
        public Phase Phase = Phase.Early;
    }

    public enum Phase { Early, Mid, Late }
}
