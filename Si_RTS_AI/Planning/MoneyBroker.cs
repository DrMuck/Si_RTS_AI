using MelonLoader;
using Silica;
using System.Collections.Generic;
using UnityEngine;

namespace Si_RTS_AI.Planning
{
    /// <summary>
    /// Multi-source money broker (Option B from the design discussion).
    ///
    /// The broker is a coordinator: each sub-planner (Eco, Tech, later
    /// Military/Defense) exposes its state and proposes actions with cost
    /// and priority. The broker picks the highest-value actions across all
    /// sub-planners each tick, subject to available cash and any cash
    /// reservations that a sub-planner has requested.
    ///
    /// This first cut runs ALONGSIDE the existing EcoPlanner without
    /// replacing it — the Eco source is a thin shim that reports "no
    /// candidates" (Eco keeps firing directly for now). TechPlanner is the
    /// first real sub-planner going through the broker so we can prove the
    /// pipeline before refactoring Eco.
    ///
    /// Shared context: sub-planners can READ each other's published state
    /// via BrokerContext.SubsystemState — that's the "richer information
    /// exchange" the user asked for.
    /// </summary>
    internal static class MoneyBroker
    {
        static readonly List<IActionSource> _sources = new List<IActionSource>();
        static BrokerContext _ctx = new BrokerContext();

        // Rolling recent-fire log — subsystems can see what fired in the last
        // ~30 seconds to inform their own scoring / urgency.
        const float RECENT_FIRE_WINDOW_S = 30f;
        static readonly List<FiredAction> _recentFires = new List<FiredAction>();

        /// <summary>
        /// Cash-flow projection accessor. Sub-planners use this to compute
        /// reservations that satisfy their deadlines. User 2026-07-07:
        /// "the cashflow projector is a part of the moneybroker."
        /// </summary>
        internal static class CashFlow
        {
            public static int CountProducers(Team team) => CashFlowProjector.CountProducers(team);
            public static int CountCommittedCysts(Team team) => CashFlowProjector.CountCommittedCysts(team);
            public static float MeasuredIncomePerShrimp(Team team, int liveShrimps)
                => CashFlowProjector.MeasuredIncomePerShrimp(team, liveShrimps);
            public static float TimeToReachShrimps(int currentShrimps, int targetShrimps, int producers)
                => CashFlowProjector.TimeToReachShrimps(currentShrimps, targetShrimps, producers);
            public static int ProjectCashAt(Team team, int currentCash, int currentShrimps, int producers, float dt)
                => CashFlowProjector.ProjectCashAt(team, currentCash, currentShrimps, producers, dt);
            public static int RequiredReservationForTarget(
                Team team, int currentCash, int currentShrimps, int producers,
                float dtToDeadline, int cashTarget)
                => CashFlowProjector.RequiredReservationForTarget(
                    team, currentCash, currentShrimps, producers, dtToDeadline, cashTarget);
        }

        internal static void Register(IActionSource source)
        {
            if (source == null) return;
            if (_sources.Contains(source)) return;
            _sources.Add(source);
            MelonLogger.Msg("[BROKER] registered source: " + source.SourceId);
        }

        /// <summary>
        /// Total cash currently reserved by all sub-planners for this team.
        /// External planners (EcoPlanner right now) should subtract this from
        /// team.TotalResources before deciding what they can afford — that's
        /// what lets TechPlanner "hold" cash for the next Cortex while eco
        /// keeps ticking normally with what's left over.
        /// Returns 0 for the wrong team or before any tick.
        /// </summary>
        internal static int GetReservedCash(Team team)
        {
            if (team == null) return 0;
            if (_ctx == null || _ctx.Team != team) return 0;
            int total = 0;
            try { foreach (var kv in _ctx.CashReservations) total += kv.Value; } catch { }
            return total;
        }

        internal static void ResetForNewRound()
        {
            _recentFires.Clear();
            _ctx = new BrokerContext();
        }

        internal static void Tick(Team team)
        {
            if (team == null) return;
            if (!TestHarnessNs.TestHarness.IsRoundActive) return;
            string tn = team.name ?? "";
            if (!tn.Contains("Alien")) return;

            long ts = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                // 1) Build the shared context.
                _ctx.Team       = team;
                _ctx.Time       = Time.time;
                try { _ctx.Cash = team.TotalResources; } catch { _ctx.Cash = 0; }
                try { _ctx.Cap  = team.ResourceCapacity; } catch { _ctx.Cap = 0; }
                _ctx.RecentFires = _recentFires;
                // Clear stale reservations — sources re-publish per tick.
                _ctx.CashReservations.Clear();

                // Prune old fires.
                for (int i = _recentFires.Count - 1; i >= 0; i--)
                    if (_ctx.Time - _recentFires[i].FiredAt > RECENT_FIRE_WINDOW_S) _recentFires.RemoveAt(i);

                // 2) Ask each source to publish its state to the context.
                foreach (var s in _sources)
                {
                    try { s.PublishState(_ctx); }
                    catch (System.Exception ex) { MelonLogger.Warning("[BROKER] " + s.SourceId + ".PublishState threw: " + ex.Message); }
                }

                // 3) Gather proposals from all sources.
                var proposals = new List<Proposal>();
                foreach (var s in _sources)
                {
                    try
                    {
                        var list = s.ProposeCandidates(_ctx);
                        if (list != null) proposals.AddRange(list);
                    }
                    catch (System.Exception ex) { MelonLogger.Warning("[BROKER] " + s.SourceId + ".ProposeCandidates threw: " + ex.Message); }
                }

                if (proposals.Count == 0) return;

                // 4) Sort by effective priority: score × (1 + urgency).
                proposals.Sort((a, b) =>
                    (b.Score * (1f + b.Urgency)).CompareTo(a.Score * (1f + a.Urgency)));

                // 5) Fire in order, respecting cash + reservations from OTHER sources.
                int cashLeft = _ctx.Cash;
                int reservedByOthers = 0;
                foreach (var kv in _ctx.CashReservations)
                    reservedByOthers += kv.Value;

                foreach (var p in proposals)
                {
                    // A proposal's own source's reservation shouldn't block it
                    // from firing — the reserve was for THIS proposal.
                    int effectiveReserved = reservedByOthers;
                    if (_ctx.CashReservations.TryGetValue(p.SourceId, out int mine))
                        effectiveReserved -= mine;

                    if (p.Cost > cashLeft - effectiveReserved) continue;
                    bool fired = false;
                    try { fired = p.Fire != null && p.Fire(team); }
                    catch (System.Exception ex) { MelonLogger.Warning("[BROKER] Fire threw: " + ex.Message); }
                    if (!fired) continue;

                    cashLeft -= p.Cost;
                    _recentFires.Add(new FiredAction
                    {
                        SourceId = p.SourceId,
                        Kind     = p.Kind,
                        Target   = p.Target,
                        Cost     = p.Cost,
                        FiredAt  = _ctx.Time,
                    });
                    MelonLogger.Msg("[BROKER] fired " + p.SourceId + "/" + p.Kind +
                                    " cost=" + p.Cost + " score=" + p.Score.ToString("F0") +
                                    " urgency=" + p.Urgency.ToString("F2"));
                }
            }
            catch (System.Exception ex)
            {
                MelonLogger.Warning("[BROKER] Tick threw: " + ex.Message);
            }
            long ms = (System.Diagnostics.Stopwatch.GetTimestamp() - ts) * 1000L / System.Diagnostics.Stopwatch.Frequency;
            Core.RecentModWork.AddBroker(ms);
        }
    }

    /// <summary>Every sub-planner implements this.</summary>
    internal interface IActionSource
    {
        string SourceId { get; }

        /// <summary>
        /// Called once per broker tick BEFORE ProposeCandidates. The source
        /// writes into ctx.SubsystemState so peer sources can read it during
        /// their own ProposeCandidates phase. Also the place to update
        /// ctx.CashReservations if this source wants to reserve cash.
        /// </summary>
        void PublishState(BrokerContext ctx);

        /// <summary>
        /// Return the top candidate actions this source recommends firing
        /// this tick. Empty list is fine — the source may just be observing.
        /// </summary>
        List<Proposal> ProposeCandidates(BrokerContext ctx);
    }

    internal struct Proposal
    {
        public string     SourceId;
        public string     Kind;        // e.g. "PlaceBc" / "PlaceCortex" / "QueueUnit_Crab"
        public Vector3    Target;      // meaningful only for placement actions
        public int        Cost;
        public float      Score;       // higher = more valuable
        public float      Urgency;     // 0..1 multiplier — high urgency short-circuits normal priority
        public System.Func<Team, bool> Fire;   // called by broker to execute; return true iff actually fired
    }

    internal struct FiredAction
    {
        public string  SourceId;
        public string  Kind;
        public Vector3 Target;
        public int     Cost;
        public float   FiredAt;
    }

    /// <summary>Shared blackboard the sources read and write per tick.</summary>
    internal class BrokerContext
    {
        public Team              Team;
        public float             Time;
        public int               Cash;
        public int               Cap;

        // source_id → arbitrary object holding that source's exposed state
        public Dictionary<string, object> SubsystemState = new Dictionary<string, object>();

        // source_id → cash amount that source wants to reserve for its next fire
        public Dictionary<string, int> CashReservations = new Dictionary<string, int>();

        // Last ~30s of fires across all sources — sources can use this to
        // detect "eco just placed 3 BCs, no urgent expansion signal now".
        public List<FiredAction> RecentFires;
    }
}
