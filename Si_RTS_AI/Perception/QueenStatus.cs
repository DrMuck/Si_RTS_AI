using MelonLoader;
using Silica;
using System;
using System.Reflection;
using UnityEngine;

namespace Si_RTS_AI.Perception
{
    /// <summary>
    /// Can the aliens build right now?
    ///
    /// They cannot while the Queen is out of the Nest, and they cannot at all
    /// once she is dead. Neither state is visible to the planner, and the cost
    /// of not knowing is severe: on 2026-07-30 a player un-nested the Queen at
    /// the start of a match and disconnected, and the planner then queued
    /// placement searches for the same handful of targets for the rest of the
    /// round — every one refused — while cash climbed to 81,000 behind three
    /// shrimps. Seventeen were still outstanding when the map was changed to
    /// clear it, and resolving those against a dying scene killed the server.
    ///
    /// The signal is the Nest's AlienQueenCompartment: DockedQueen is non-null
    /// exactly while she is home. That is a direct read of the state the game
    /// enforces, rather than the failure-counting backoff in AlienConstruction,
    /// which infers the same thing after six refusals.
    ///
    /// FAILS OPEN. If the compartment cannot be found or the interop throws,
    /// this reports buildable. A wrong "yes" costs a refused placement; a wrong
    /// "no" would silently stop the economy for a whole round, which is far
    /// worse than the problem being solved.
    /// </summary>
    internal static class QueenStatus
    {
        internal enum State { Buildable, QueenAway, QueenLost, Unknown }

        const float POLL_S = 1f;

        static float _lastPollAt;
        static State _last = State.Unknown;
        static State _lastLogged = State.Unknown;

        internal static void ResetForNewRound()
        {
            _lastPollAt = 0f;
            _last = State.Unknown;
            _lastLogged = State.Unknown;
        }

        /// <summary>Cached answer; polled at 1Hz. True unless we positively know
        /// otherwise.</summary>
        internal static bool CanBuild(Team team)
        {
            var s = Evaluate(team);
            return s != State.QueenAway && s != State.QueenLost;
        }

        internal static State Current => _last;

        internal static State Evaluate(Team team)
        {
            float now = Time.time;
            if (now - _lastPollAt < POLL_S) return _last;
            _lastPollAt = now;

            State s = Probe(team);
            _last = s;

            if (s != _lastLogged)
            {
                _lastLogged = s;
                if (s == State.QueenAway)
                    MelonLogger.Warning("[QUEEN] not docked in the Nest — aliens cannot build. " +
                                        "Holding all construction until she returns.");
                else if (s == State.QueenLost)
                    MelonLogger.Warning("[QUEEN] no Queen found for the alien team — construction " +
                                        "is impossible for the rest of the round.");
                else if (s == State.Buildable)
                    MelonLogger.Msg("[QUEEN] docked — construction available.");
            }
            return s;
        }

        static State Probe(Team team)
        {
            if (team == null) return State.Unknown;
            try
            {
                var structs = team.Structures;
                if (structs == null) return State.Unknown;

                bool sawNest = false;
                for (int i = 0; i < structs.Count; i++)
                {
                    var st = structs[i];
                    if (st == null || st.ObjectInfo == null || st.IsDestroyed) continue;
                    if ((st.ObjectInfo.DisplayName ?? "") != "Nest") continue;
                    sawNest = true;

                    var comps = st.UnitCompartments;
                    if (comps == null) continue;
                    for (int c = 0; c < comps.Count; c++)
                    {
                        var comp = comps[c];
                        if (comp == null) continue;
                        // Read DockedQueen by reflection rather than casting to
                        // AlienQueenCompartment. We compile against netstandard
                        // reference stubs, so the Il2Cpp TryCast<T> helper does
                        // not exist here, and a plain managed cast on an interop
                        // proxy is not a reliable runtime type test. The property
                        // is only present on the queen compartment, so finding it
                        // IS the type check.
                        var prop = comp.GetType().GetProperty("DockedQueen");
                        if (prop == null) continue;
                        object docked = prop.GetValue(comp, null);
                        return docked != null ? State.Buildable : State.QueenAway;
                    }
                }

                // A Nest with no queen compartment we could read, or no Nest at
                // all: say nothing rather than guess.
                return sawNest ? State.Unknown : State.Unknown;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[QUEEN] probe threw: " + ex.Message);
                return State.Unknown;
            }
        }
    }
}
