using Silica.AI;
using UnityEngine;

namespace Si_RTS_AI
{
    /// <summary>
    /// The 2026-09 beta replaced the per-unit order verbs (Unit.OnMoveOrder,
    /// Unit.OnAttackOrder, ...) with an order-processor model: every receiver
    /// carries an <see cref="AIOrderProcessor"/> (BaseGameObject.OrderAgent) and
    /// orders are OrderDefinition assets pushed through IssueOrder /
    /// IssueResolvedOrder.
    ///
    /// These shims keep the planner call sites reading the way they did, and give
    /// the Harmony gates a single documented chokepoint: everything — vanilla AI
    /// tasks, AIGroup, player commands — funnels through
    /// AIOrderProcessor.IssueOrder, so that is what we prefix now instead of
    /// Unit.OnMoveOrder.
    /// </summary>
    internal static class OrderCompat
    {
        /// <summary>
        /// Old Unit.OnMoveOrder(pos, speed). Resolves to the unit's default point
        /// order — a move for anything that can move — with the same defaults the
        /// old call had (calledByAI, broadcast, not queued, long range).
        /// </summary>
        internal static bool IssueMoveOrder(this Unit u, Vector3 pos, AgentMoveSpeed speed)
        {
            if (u == null) return false;
            var agent = u.OrderAgent;
            if (agent == null) return false;
            return agent.IssueResolvedOrder(pos, null, OrderIssueParams.Ai(speed));
        }

        /// <summary>The Unit an order processor belongs to, or null for structures.</summary>
        internal static Unit OwnerUnit(this AIOrderProcessor p)
        {
            if (p == null) return null;
            return p.Owner as Unit;
        }

        /// <summary>True for the order verb that used to be Unit.OnMoveOrder.</summary>
        internal static bool IsMoveOrder(OrderDefinition definition)
        {
            return definition is MoveOrderDefinition;
        }
    }
}
