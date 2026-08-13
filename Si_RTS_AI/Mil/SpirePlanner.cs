using MelonLoader;
using Silica;
using Si_RTS_AI.Planning;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Si_RTS_AI.Mil
{
    /// <summary>
    /// STATIC DEFENCE, WHICH THIS MOD HAS NEVER BUILT.
    ///
    /// Found by the shadow layer on its first played round, 2026-08-13. A
    /// top-quartile human commander has about 13 Hive Spires and 9 Thorn Spires
    /// standing by minute 30 and starts placing them at minute 10. The bot
    /// finished that round with 7 and 1 — and both of those were built by
    /// DrMuck, because no code in this mod names either structure. Not
    /// under-built: absent.
    ///
    /// WHY EARLY, AND WHY THIS IS THE HARDEST WINDOW
    /// ----------------------------------------------
    /// DrMuck: *"early defense seems the hardest. Humans can push quite
    /// aggressive early while alien still fighting to build up a good eco."*
    ///
    /// The archive agrees and puts a number on it. Across 13,043 alien-vs-human
    /// engagements, by minute of the round:
    ///
    ///     window     alien win%    cash exchange (theirs lost : ours)
    ///     0-5 min       34%            0.68
    ///     5-10          37%            0.79
    ///     10-15         43%            0.83
    ///     15-20         45%            0.88
    ///     20-30+        44-45%         0.92-0.94
    ///
    /// The alien loses about three cash for every two it destroys in the first
    /// five minutes and climbs monotonically to near-parity by twenty. That is
    /// an eleven-point win-rate hole in exactly the window where the economy
    /// cannot yet fund an army — which is what static defence is FOR. A spire
    /// costs 1,000 and 35 seconds and does not consume unit cap, so it buys
    /// time that Shockers cannot.
    ///
    /// It also means the human trajectory is a FLOOR rather than a target here.
    /// Good commanders place their first spire around minute 8-10, but they are
    /// placing it when they can afford it, not when they first need it. This
    /// planner is allowed to be earlier and never later.
    ///
    /// WHAT IT DECIDES, AND WHAT IT DELIBERATELY DOES NOT
    /// ---------------------------------------------------
    /// It decides HOW MANY (from the measured trajectory) and WHERE (the ground
    /// `DefencePlanner` already ranks by recent income x threat, falling back to
    /// the Nest, because the Queen is a loss condition and everything else is
    /// an asset). It does not decide what a spire costs, when the economy can
    /// spare it, or whether the ground is buildable — `MoneyBroker` and
    /// `AlienConstruction` own those and are asked rather than second-guessed.
    ///
    /// OFF BY DEFAULT, ON PURPOSE. `mil.spires.execute` starts false, so the
    /// first round logs every placement it WOULD make and spends nothing.
    /// `MILITARY_MODEL` section 11 has required that of every change for weeks
    /// and this session already broke it once. Read one round of [MIL/SPIRE]
    /// lines, then flip the flag.
    /// </summary>
    internal static class SpirePlanner
    {
        internal static bool Enabled { get; private set; } = true;
        internal static bool Execute { get; private set; }

        /// <summary>Both spires cost 1,000. Below this in the bank we are not
        /// buying defence with money the economy still needs to compound —
        /// early eco IS the defence if it gets far enough fast enough.</summary>
        static int _cashFloor = 4000;

        /// <summary>How far ahead of the human curve we may run in the opening,
        /// where the archive says the alien is weakest. 1.0 = follow it
        /// exactly.</summary>
        static float _earlyBias = 1.5f;
        const float EARLY_UNTIL_MIN = 12f;

        /// <summary>Spires this close to an existing one are redundant — they
        /// would cover the same ground twice and leave other assets bare.</summary>
        const float SPACING_M = 120f;

        const float TICK_S = 15f;
        static float _nextAt;
        static int _placedThisRound;

        static readonly string[] SPIRES = { "Thorn Spire", "Hive Spire" };
        static readonly Dictionary<string, ConstructionData> _cds =
            new Dictionary<string, ConstructionData>(StringComparer.OrdinalIgnoreCase);

        internal static void Configure()
        {
            Enabled    = RtsaiConfig.Bool ("mil.spires.enabled", true);
            Execute    = RtsaiConfig.Bool ("mil.spires.execute", false);
            _cashFloor = RtsaiConfig.Int  ("mil.spires.cashFloor", 4000);
            _earlyBias = RtsaiConfig.Float("mil.spires.earlyBias", 1.5f);
            MelonLogger.Msg($"[MIL/SPIRE] {(Enabled ? "on" : "off")} " +
                            $"execute={Execute} cashFloor={_cashFloor} " +
                            $"earlyBias={_earlyBias:F2} until {EARLY_UNTIL_MIN:F0}min" +
                            (Execute ? "" : " — planning only, builds nothing"));
        }

        internal static void ResetForNewRound()
        {
            _nextAt = 0f; _placedThisRound = 0; _cds.Clear();
        }

        internal static void Tick(Team team)
        {
            if (!Enabled || team == null) return;
            float now = Time.time;
            if (now < _nextAt) return;
            _nextAt = now + TICK_S;

            float minutes;
            try { minutes = Perception.MapLayers.LayerReplay.CurrentRoundTime / 60f; }
            catch { return; }
            if (minutes <= 0.5f) return;

            DiscoverCds(team);
            if (_cds.Count == 0) return;

            for (int i = 0; i < SPIRES.Length; i++)
                Consider(team, SPIRES[i], minutes);
        }

        /// <summary>
        /// Both spires are built AT THE NEST, so their ConstructionData lives in
        /// some structure's options rather than anywhere we can name up front.
        /// Same discovery TechPlanner does for the Cortex tiers.
        /// </summary>
        static void DiscoverCds(Team team)
        {
            if (_cds.Count == SPIRES.Length) return;
            try
            {
                var structs = team.Structures;
                if (structs == null) return;
                for (int i = 0; i < structs.Count; i++)
                {
                    var opts = structs[i]?.ConstructionOptions;
                    if (opts == null) continue;
                    foreach (var opt in opts)
                    {
                        string n = opt?.ObjectInfo?.DisplayName ?? "";
                        for (int s = 0; s < SPIRES.Length; s++)
                            if (string.Equals(n, SPIRES[s], StringComparison.OrdinalIgnoreCase)
                                && !_cds.ContainsKey(n))
                            {
                                _cds[n] = opt;
                                MelonLogger.Msg($"[MIL/SPIRE] found {n} buildable " +
                                                $"(cost {opt.ObjectInfo?.Cost ?? 0})");
                            }
                    }
                }
            }
            catch { }
        }

        static void Consider(Team team, string name, float minutes)
        {
            float target = Doctrine.TargetAt(name, minutes);
            if (float.IsNaN(target)) return;

            // The opening is where the hole is, so we are allowed to run ahead
            // of the human curve there and never behind it.
            if (minutes < EARLY_UNTIL_MIN) target *= _earlyBias;

            int have = CountStanding(team, name);
            if (have >= Mathf.FloorToInt(target)) return;

            if (!_cds.TryGetValue(name, out var cd)) return;

            int cash = 0;
            try { cash = (int)team.TotalResources; } catch { }
            int reserved = 0;
            try { reserved = MoneyBroker.GetReservedCash(team); } catch { }
            int spendable = cash - reserved;
            int cost = 1000;
            try { cost = cd.ObjectInfo?.Cost ?? 1000; } catch { }

            if (spendable < _cashFloor + cost)
            {
                // Not a refusal worth logging every 15s — but say it once the
                // gap is large, because "wanted defence, could not pay" is a
                // different story from "did not want defence".
                return;
            }

            if (!TryPickSite(team, name, out var where, out string why)) return;

            _placedThisRound++;
            MelonLogger.Msg($"[MIL/SPIRE] t={minutes:F1}m {name} {have}/{target:F1} " +
                            $"-> ({where.x:F0},{where.z:F0}) {why} | " +
                            $"spendable {spendable} cost {cost}" +
                            (Execute ? "" : "  [PLAN ONLY — mil.spires.execute is false]"));
            if (!Execute) return;

            try
            {
                if (!Faction.AlienConstruction.TryBuildStructureByCd(team, cd, where))
                    MelonLogger.Msg($"[MIL/SPIRE] placement refused at " +
                                    $"({where.x:F0},{where.z:F0}) — construction said no");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[MIL/SPIRE] build threw: " + ex.Message);
            }
        }

        static int CountStanding(Team team, string name)
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
                        if (string.Equals(st.ObjectInfo.DisplayName, name,
                                          StringComparison.OrdinalIgnoreCase)) n++;
                    }
            }
            catch { }
            return n;
        }

        /// <summary>
        /// The most valuable undefended ground.
        ///
        /// `DefencePlanner` already ranks assets by recent income x threat and
        /// that is the same question asked from the other side, so it is read
        /// rather than recomputed. The Nest is the fallback and the first
        /// answer of the round: the Queen is a loss condition, and at minute
        /// three there is no earning site to rank yet.
        /// </summary>
        static bool TryPickSite(Team team, string name, out Vector3 pos, out string why)
        {
            pos = Vector3.zero; why = "";
            try
            {
                var tasks = DefencePlanner.Tasks;
                for (int i = 0; i < tasks.Count; i++)
                {
                    var t = tasks[i];
                    if (AlreadyCovered(team, name, t.Pos)) continue;
                    pos = t.Pos;
                    why = $"{t.Kind} earning {t.RecentIncome} under threat {t.Threat:F0}";
                    return true;
                }

                var nest = FindNest(team);
                if (nest != Vector3.zero && !AlreadyCovered(team, name, nest))
                {
                    pos = nest; why = "the Nest — the Queen is the loss condition";
                    return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>Is there already one of these near enough that another
        /// would cover the same ground twice?</summary>
        static bool AlreadyCovered(Team team, string name, Vector3 pos)
        {
            float r2 = SPACING_M * SPACING_M;
            try
            {
                var structs = team.Structures;
                if (structs != null)
                    for (int i = 0; i < structs.Count; i++)
                    {
                        var st = structs[i];
                        if (st?.ObjectInfo == null || st.IsDestroyed) continue;
                        if (!string.Equals(st.ObjectInfo.DisplayName, name,
                                           StringComparison.OrdinalIgnoreCase)) continue;
                        var p = st.transform.position;
                        float dx = p.x - pos.x, dz = p.z - pos.z;
                        if (dx * dx + dz * dz <= r2) return true;
                    }
            }
            catch { }
            // An order already out for this spot counts as covered, or the
            // 15s tick would queue the same spire repeatedly while the first
            // one is still building.
            try
            {
                if (Faction.AlienConstruction.WasOrderedNear(name, pos, SPACING_M))
                    return true;
            }
            catch { }
            return false;
        }

        static Vector3 FindNest(Team team)
        {
            try
            {
                var structs = team.Structures;
                if (structs != null)
                    for (int i = 0; i < structs.Count; i++)
                    {
                        var st = structs[i];
                        if (st?.ObjectInfo == null || st.IsDestroyed) continue;
                        if ((st.ObjectInfo.DisplayName ?? "").IndexOf(
                                "Nest", StringComparison.OrdinalIgnoreCase) >= 0)
                            return st.transform.position;
                    }
            }
            catch { }
            return Vector3.zero;
        }

        internal static string RoundSummary() =>
            _placedThisRound > 0
                ? $"spires {(Execute ? "placed" : "planned")}={_placedThisRound}" : "";
    }
}
