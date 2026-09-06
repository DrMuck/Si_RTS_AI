using System.Collections.Generic;
using Silica.AI;
using UnityEngine;

namespace Si_RTS_AI
{
    /// <summary>
    /// ONE SOURCE, TWO GAME BRANCHES. The 2026-09 beta (0.9.46) replaced the
    /// per-unit order verbs (Unit.OnMoveOrder, OnAttackOrder, ...) with an
    /// order-processor model: every receiver carries an AIOrderProcessor
    /// (BaseGameObject.OrderAgent) and orders are OrderDefinition assets pushed
    /// through IssueOrder. The main branch (0.9.42) still has the verbs.
    ///
    /// The planners call these shims; the build for the main branch defines
    /// GAME_MAIN (csproj DefineConstants) and gets the verbs, the beta build
    /// gets the processor. The Harmony gates that keep vanilla orders off our
    /// units are switched the same way in their own files.
    /// </summary>
    internal static class OrderCompat
    {
#if GAME_MAIN
        internal static bool IssueMoveOrder(this Unit u, Vector3 pos, AgentMoveSpeed speed)
        {
            if (u == null) return false;
            u.OnMoveOrder(pos, speed);
            return true;
        }

        internal static bool Move(BaseGameObject o, Vector3 dest)
        {
            var u = o as Unit;
            if (u == null) return false;
            u.OnMoveOrder(dest, AgentMoveSpeed.Fast);
            return true;
        }

        internal static bool Attack(BaseGameObject o, Vector3 at, Target target)
        {
            var u = o as Unit;
            if (u == null || target == null) return false;
            u.OnAttackOrder(target, at, AgentMoveSpeed.Fast);
            return true;
        }

        static readonly List<Unit> _one = new List<Unit>(1);
        internal static bool AttackMove(BaseGameObject o, Vector3 dest)
        {
            var u = o as Unit;
            if (u == null) return false;
            // The game's own attack-move for a single unit: no target, isAttack.
            _one.Clear(); _one.Add(u);
            StrategyMode.PerformMoveAttack(_one, dest, null, AgentMoveSpeed.Fast, true);
            _one.Clear();
            return true;
        }

        internal static bool Stop(BaseGameObject o)
        {
            var u = o as Unit;
            if (u == null) return false;
            u.OnStopOrder(AgentMoveSpeed.Fast);
            return true;
        }
#else
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

        static OrderDefinition Def(System.Func<OrderDefinition> get) { try { return get(); } catch { return null; } }

        internal static bool Move(BaseGameObject o, Vector3 dest)
        {
            var agent = o?.OrderAgent;
            if (agent == null) return false;
            var def = Def(() => OrderDefinitionRegistry.Move);
            if (def != null) return agent.IssueOrder(def, new OrderTarget(dest, null), OrderIssueParams.Ai(AgentMoveSpeed.Fast));
            return agent.IssueResolvedOrder(dest, null, OrderIssueParams.Ai(AgentMoveSpeed.Fast));
        }

        internal static bool Attack(BaseGameObject o, Vector3 at, Target target)
        {
            var agent = o?.OrderAgent;
            if (agent == null) return false;
            var def = Def(() => OrderDefinitionRegistry.Attack);
            if (def != null) return agent.IssueOrder(def, new OrderTarget(at, target), OrderIssueParams.Ai(AgentMoveSpeed.Fast));
            return agent.IssueResolvedOrder(at, target, OrderIssueParams.Ai(AgentMoveSpeed.Fast));
        }

        internal static bool AttackMove(BaseGameObject o, Vector3 dest)
        {
            var agent = o?.OrderAgent;
            if (agent == null) return false;
            var def = Def(() => OrderDefinitionRegistry.Attack);
            if (def == null) return Move(o, dest);
            return agent.IssueOrder(def, new OrderTarget(dest), OrderIssueParams.Ai(AgentMoveSpeed.Fast));
        }

        internal static bool Stop(BaseGameObject o)
        {
            var agent = o?.OrderAgent;
            if (agent == null) return false;
            var def = Def(() => OrderDefinitionRegistry.Stop);
            if (def == null) return false;
            return agent.IssueOrder(def, OrderTarget.None, OrderIssueParams.Ai(AgentMoveSpeed.Fast));
        }
#endif
    }
}
