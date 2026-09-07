using System;
using Silica.AI;
using UnityEngine;
using Si_RTS_AI.Perception;

namespace Si_RTS_AI.Mil
{
    /// <summary>
    /// THE QUEEN STAYS HOME. Aliens can only build while the Queen is docked in
    /// the Nest. The Maw, 2026-09-07 06:48: the Queen left the Nest under attack,
    /// the executor refused thirty-four placements as "queenAway", no producer
    /// and no spire went up while 20,000 cash waited, and she was killed in the
    /// open at 06:59. The mod never moves the Queen itself, so an undocked Queen
    /// is the game's doing; after a short grace she is ordered back, and again
    /// every ten seconds until the compartment reports her docked.
    /// </summary>
    internal static class QueenKeeper
    {
        const float AWAY_GRACE_S = 15f;
        const float REORDER_S    = 10f;
        const int   LOG_CAP      = 20;

        static float _awaySince  = -1f;
        static float _lastOrderAt = -999f;
        static int   _orders;

        internal static void ResetForNewRound() { _awaySince = -1f; _lastOrderAt = -999f; _orders = 0; }

        internal static void Tick(Team team)
        {
            if (team == null) return;
            if (QueenStatus.Current != QueenStatus.State.QueenAway) { _awaySince = -1f; return; }
            float now = Time.time;
            if (_awaySince < 0f) { _awaySince = now; return; }
            if (now - _awaySince < AWAY_GRACE_S) return;
            if (now - _lastOrderAt < REORDER_S) return;
            _lastOrderAt = now;
            try
            {
                var queen = FindQueen(team);
                if (queen == null) return;
                var agent = queen.AIAgent as AIAlienQueenAgent;
                if (agent == null)
                {
                    if (_orders++ == 0) MilLog.Msg("[QUEEN] the Queen has no AIAlienQueenAgent — cannot order her home");
                    return;
                }
                Vector3 nest = Intel.Nest;
                if (nest == Vector3.zero) nest = queen.transform.position;
#if GAME_MAIN
                var comp = agent.GetQueenCompartment(nest, false);
                if (comp == null) { if (_orders++ == 0) MilLog.Msg("[QUEEN] no Nest compartment found for the Queen"); return; }
                agent.OnNestOrder(comp);
#else
                var comp = agent.FindNestCompartment(nest);
                if (comp == null) { if (_orders++ == 0) MilLog.Msg("[QUEEN] no Nest compartment found for the Queen"); return; }
                agent.Nest(comp, AgentMoveSpeed.Fast, 1f, true);
#endif
                _orders++;
                if (_orders <= LOG_CAP)
                    MilLog.Msg($"[QUEEN] away {now - _awaySince:F0}s at ({queen.transform.position.x:F0},{queen.transform.position.z:F0}) — ordered back into the Nest at ({nest.x:F0},{nest.z:F0}) (#{_orders})");
            }
            catch (Exception ex) { MilLog.Msg("[QUEEN] nest order threw: " + ex.Message); }
        }

        static Unit FindQueen(Team team)
        {
            var units = team.Units;
            if (units == null) return null;
            for (int i = 0; i < units.Count; i++)
            {
                var u = units[i];
                if (u == null || u.ObjectInfo == null || u.IsDestroyed) continue;
                if ((u.ObjectInfo.DisplayName ?? "") == "Queen") return u;
            }
            return null;
        }
    }
}
