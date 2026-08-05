using MelonLoader;
using Silica;
using System;
using UnityEngine;

namespace Si_RTS_AI.Planning
{
    /// <summary>
    /// Cash-flow projection calculator — a MoneyBroker component.
    /// User 2026-07-07: "our money broker needs a projection calculator"
    /// that accounts for Cyst build time, continuous shrimp production
    /// (build time + income delay), and current income rate.
    ///
    /// Model (v2 — measured):
    ///   - Per-shrimp income rate: measured from EcoRateSampler (team income
    ///     divided by live shrimp count). No more fitted magic numbers.
    ///   - New-shrimp ramp delay: SHRIMP_RAMP_S seconds after spawn before
    ///     the shrimp contributes full income (walk to patch + first cycle).
    ///   - Production rate: producers / EcoSimulator.SHRIMP_BUILD_S per second.
    ///   - Production cost: SHRIMP_COST per new spawn.
    ///
    /// Deliberately ignored (v2 scope):
    ///   - Under-construction Cysts finishing during the window (adds
    ///     conservatism — real income will be higher than projected).
    /// </summary>
    internal static class CashFlowProjector
    {
        // New shrimp income ramp — time from spawn until steady-state income.
        // Composed of: walk to patch (~10s) + first harvest fill (400/9.5 ≈ 42s)
        // + walk back (~10s) + deposit (8s) ≈ 70s. Rounded down to 40s to be
        // slightly optimistic; can be tuned from CSV log observations.
        internal const float SHRIMP_RAMP_S              = 40f;
        internal const float SHRIMP_COST_CASH           = 75f;
        // Conservative fallback per-shrimp rate — used only when sampler
        // hasn't accumulated enough data yet (first ~10s of round).
        internal const float FALLBACK_INCOME_PER_SHRIMP = 2.5f;
        // Smoothness control parameter (NOT an income estimate) — spread
        // eco spending across ticks so we don't overshoot the trajectory.
        internal const float SPEND_FRACTION_PER_TICK    = 0.5f;

        /// <summary>Measured per-shrimp income rate from EcoRateSampler.</summary>
        internal static float MeasuredIncomePerShrimp(Team team, int liveShrimps)
        {
            if (liveShrimps <= 0) return FALLBACK_INCOME_PER_SHRIMP;
            float avgTeamIncome = 0f;
            try { avgTeamIncome = Perception.EcoRateSampler.GetAvgIncomePerSec(team); } catch { }
            if (avgTeamIncome <= 0f) return FALLBACK_INCOME_PER_SHRIMP;
            float perShrimp = avgTeamIncome / liveShrimps;
            return Mathf.Clamp(perShrimp, FALLBACK_INCOME_PER_SHRIMP, 6f);
        }

        /// <summary>Count of finished, producing structures (Nest + built Cyst).</summary>
        internal static int CountProducers(Team team)
        {
            int n = 0;
            try
            {
                var structs = team.Structures;
                if (structs == null) return 0;
                for (int i = 0; i < structs.Count; i++)
                {
                    var st = structs[i];
                    if (st?.ObjectInfo == null || st.IsDestroyed) continue;
                    string name = st.ObjectInfo.DisplayName ?? "";
                    if (name != "Nest" && name != "Lesser Spawning Cyst") continue;
                    if (name == "Nest") { n++; continue; }
                    bool funcCyst = false;
                    try { funcCyst = st.IsFunctional; } catch { }
                    if (funcCyst) n++;
                }
            }
            catch { }
            return n;
        }

        /// <summary>Count of Lesser Cysts either built OR under construction.
        /// Used as the trigger for cash-flow gating — once 3 committed, we
        /// know eco is on the tech trajectory.</summary>
        internal static int CountCommittedCysts(Team team)
        {
            int n = 0;
            try
            {
                var structs = team.Structures;
                if (structs != null)
                    for (int i = 0; i < structs.Count; i++)
                    {
                        var st = structs[i];
                        if (st?.ObjectInfo == null || st.IsDestroyed) continue;
                        if (st.ObjectInfo.DisplayName == "Lesser Spawning Cyst") n++;
                    }
                var sites = ConstructionSite.ConstructionSites;
                if (sites != null)
                    for (int i = 0; i < sites.Count; i++)
                    {
                        var cs = sites[i];
                        if (cs == null || cs.IsDestroyed || cs.ObjectInfo == null) continue;
                        if (cs.Team != team) continue;
                        if (cs.ObjectInfo.DisplayName == "Lesser Spawning Cyst") n++;
                    }
            }
            catch { }
            return n;
        }

        /// <summary>Time (seconds) until team reaches `targetShrimps`.
        /// Assumes current producer rate holds. +∞ if no producers.</summary>
        internal static float TimeToReachShrimps(int currentShrimps, int targetShrimps, int producers)
        {
            if (currentShrimps >= targetShrimps) return 0f;
            if (producers <= 0) return float.MaxValue;
            float shrimpsPerSec = producers / EcoSimulator.SHRIMP_BUILD_S;
            return (targetShrimps - currentShrimps) / shrimpsPerSec;
        }

        /// <summary>
        /// Project team cash at time `dt` from now assuming NO other spending.
        /// Uses measured per-shrimp income for current shrimps + ramp-delayed
        /// income contribution from new spawns during the window.
        ///
        /// Income integral:
        ///   income_current = N₀ × R × dt         (current shrimps producing full rate)
        ///   income_new     = if dt > RAMP: p_rate × R × (dt - RAMP)² / 2
        ///                    else: 0
        ///
        /// - N₀ = current alive shrimps
        /// - R  = measured cash/shrimp/second
        /// - p_rate = producers / SHRIMP_BUILD_S = new spawns per second
        /// - RAMP = SHRIMP_RAMP_S = time before new shrimp contributes income
        /// </summary>
        internal static int ProjectCashAt(Team team, int currentCash, int currentShrimps,
                                          int producers, float dt)
        {
            if (dt <= 0f || dt >= float.MaxValue) return currentCash;
            float R = MeasuredIncomePerShrimp(team, currentShrimps);
            float shrimpsPerSec = producers / EcoSimulator.SHRIMP_BUILD_S;

            // Current shrimps producing at full rate for whole window.
            float incomeCurrent = currentShrimps * R * dt;

            // New shrimps subject to ramp delay.
            float incomeNew = 0f;
            if (dt > SHRIMP_RAMP_S)
            {
                float t = dt - SHRIMP_RAMP_S;
                incomeNew = shrimpsPerSec * R * t * t * 0.5f;
            }

            int totalSpawns = Mathf.RoundToInt(shrimpsPerSec * dt);
            float shrimpCost = totalSpawns * SHRIMP_COST_CASH;

            return currentCash + Mathf.RoundToInt(incomeCurrent + incomeNew - shrimpCost);
        }

        /// <summary>
        /// Reservation solver: given `cashTarget` needed at `dtToDeadline` seconds
        /// from now, return cash to hold NOW so that projected cash at deadline
        /// meets target. Returns 0 (spend freely) if projection is ahead of target.
        /// Returns currentCash (hold everything) if projection is behind.
        /// </summary>
        internal static int RequiredReservationForTarget(Team team, int currentCash,
            int currentShrimps, int producers, float dtToDeadline, int cashTarget)
        {
            int projected = ProjectCashAt(team, currentCash, currentShrimps, producers, dtToDeadline);
            int allowedSpend = projected - cashTarget;
            if (allowedSpend <= 0) return currentCash;

            // Throttle: only spend a fraction of the surplus this tick. Spreads
            // spending across many ticks so projection stays adaptive.
            allowedSpend = Mathf.RoundToInt(allowedSpend * SPEND_FRACTION_PER_TICK);
            if (allowedSpend >= currentCash) return 0;
            return currentCash - allowedSpend;
        }
    }
}
