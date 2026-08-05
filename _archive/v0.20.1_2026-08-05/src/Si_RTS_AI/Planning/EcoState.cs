using System.Collections.Generic;
using UnityEngine;

namespace Si_RTS_AI.Planning
{
    /// <summary>
    /// Snapshot of one team's eco-relevant world. Fed to EcoSimulator for
    /// deterministic forward rollout and scored by EcoPlanner. Aggregate model
    /// (not per-shrimp) — BC income is derived from #shrimps assigned + distance
    /// to nearest non-depleted patch, using the constants measured in the
    /// 1-shrimp / 10-shrimp scenario tests (harvest=9.5, deposit=50, speed=9,
    /// carry=400, N_cap=18).
    ///
    /// EcoState is cloneable so a planner can fork and simulate multiple
    /// candidate futures without touching live game state.
    /// </summary>
    public sealed class EcoState
    {
        // ---- Team-level scalars ----
        public float   t;               // sim time in seconds, 0 at Build()
        public int     cash;
        public int     cap;             // team.ResourceCapacity
        public Vector3 nestPos;         // used for chain-reach checks + fallback
        public int     totalShrimps;    // aggregate count; also indexed via shrimpsPerBc
        public bool    hasResearchStructure;   // Cortex built OR under construction — populated by EcoStateBuilder on main thread

        // Uncapped raw income produced by patches during the current simulation.
        // Reset to 0 at the start of every SimulateForward call. Score metric —
        // final cash saturates at cap and stops discriminating between actions;
        // grossEarned keeps accumulating so a BC that unlocks a 3rd patch scores
        // higher than one that doubles up on an already-crowded existing patch.
        public int     grossEarned;

        // ---- Entity structs (value types for cheap cloning) ----
        public struct Patch { public Vector3 pos; public int remaining; public int maxCap; }
        public struct Bc    { public Vector3 pos; public bool finished; public float readyAt; public int storage; public int storageCap; }
        public struct Cyst  { public Vector3 pos; public bool finished; public float readyAt; public float nextSpawnAt; }
        public struct Node  { public Vector3 pos; public bool finished; public float readyAt; }

        public List<Patch> patches = new List<Patch>();
        public List<Bc>    bcs     = new List<Bc>();
        public List<Cyst>  cysts   = new List<Cyst>();
        public List<Node>  nodes   = new List<Node>();

        // Per-BC index -> #shrimps assigned. Rebuilt on Build() by nearest-BC
        // heuristic; adjusted by simulator when new shrimps spawn from cysts.
        public Dictionary<int, int> shrimpsPerBc = new Dictionary<int, int>();

        /// <summary>
        /// Shrimps that exist but have not yet delivered anything. A shrimp
        /// spawns at its Cyst, walks to the patch, fills up, walks to the Bio
        /// Cache and only then deposits — until that first load lands it earns
        /// nothing, and it was previously credited full steady-state income from
        /// the instant it spawned. See EcoSimulator where ActiveAt is computed.
        /// </summary>
        public struct PendingShrimp { public int bcIdx; public float activeAt; }
        public List<PendingShrimp> pendingShrimps = new List<PendingShrimp>();

        public EcoState Clone()
        {
            var c = new EcoState
            {
                t            = t,
                cash         = cash,
                cap          = cap,
                nestPos      = nestPos,
                totalShrimps = totalShrimps,
                hasResearchStructure = hasResearchStructure,
                grossEarned  = grossEarned,
                patches      = new List<Patch>(patches),
                bcs          = new List<Bc>(bcs),
                cysts        = new List<Cyst>(cysts),
                nodes        = new List<Node>(nodes),
                shrimpsPerBc = new Dictionary<int, int>(shrimpsPerBc),
                pendingShrimps = new List<PendingShrimp>(pendingShrimps),
            };
            return c;
        }
    }
}
