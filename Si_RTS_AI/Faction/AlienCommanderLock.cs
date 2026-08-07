using MelonLoader;
using Silica;
using Silica.AI;
using System;
using UnityEngine;

namespace Si_RTS_AI.Faction
{
    /// <summary>
    /// THE ALIEN SEAT BELONGS TO THE BOT.
    ///
    /// DrMuck, 2026-08-07, setting up the first game against the AI: "Alien
    /// command cannot be taken by a player (only ai)."
    ///
    /// It is not a preference about who gets to play — it is what makes the test
    /// a test. The moment a human takes the alien commander seat the game
    /// disables the AI commander for that team, `EcoPlanner.MaybePlan` sees
    /// `IsCommanderEnabled == false` and stands down, and every military tick
    /// declines to issue an order. Half a second of somebody clicking the wrong
    /// button and the thing being measured has stopped running, quietly, with the
    /// round continuing as though nothing happened.
    ///
    /// HOW, AND WHY IT IS REACTIVE RATHER THAN A REFUSAL. The clean version
    /// prefixes whatever the game calls when a player asks for the seat. That
    /// method's name and signature are not something this project has verified,
    /// and an attribute patch against a member that does not exist takes the
    /// whole mod's PatchAll down with it — a broken bot is a worse outcome than a
    /// human briefly appearing as alien commander. So: every second, if the seat
    /// is occupied on an alien team, empty it and give it back to the AI, using
    /// only the three calls the headless harness has already proved work.
    ///
    /// The cost is up to one tick of a human holding the seat, which is visible
    /// and self-correcting. The failure mode of the alternative is silent.
    /// </summary>
    internal static class AlienCommanderLock
    {
        /// <summary>Off unless rtsai.json asks for it. A lock that defaults on
        /// would break co-op — a human commanding aliens with the eco planner
        /// assisting is a supported configuration, see EcoAssistWithHumanCommander.</summary>
        internal static bool Enabled;

        const float TICK_S = 1f;

        static float _lastTickAt;
        static int   _ejections;
        static string _lastEjected;

        internal static void Reload()
        {
            Enabled = Planning.RtsaiConfig.Bool("lockAlienCommander", false);
        }

        internal static void ResetForNewRound()
        {
            _lastTickAt = 0f;
            _ejections  = 0;
            _lastEjected = null;
        }

        /// <summary>
        /// Called once per periodic tick with the strategy game mode, before the
        /// per-team work: standing the planner down for a seat we are about to
        /// take back would waste the tick.
        /// </summary>
        internal static void Tick(MP_Strategy gm)
        {
            if (!Enabled || gm == null) return;
            float now = Time.time;
            if (now - _lastTickAt < TICK_S) return;
            _lastTickAt = now;

            try
            {
                var setups = gm.TeamSetups;
                if (setups == null) return;
                for (int i = 0; i < setups.Count; i++)
                {
                    var setup = setups[i];
                    var team  = setup?.Team;
                    if (team == null) continue;
                    if (!(team.name ?? "").Contains("Alien")) continue;
                    if (setup.Commander == null) continue;

                    string who = "?";
                    try { who = setup.Commander.PlayerName ?? "?"; } catch { }

                    setup.Commander = null;
                    RestoreAiCommander(team, setup);

                    _ejections++;
                    // Once per player rather than once per tick — the seat can be
                    // re-taken immediately and a log line every second would bury
                    // the round.
                    if (_lastEjected != who)
                    {
                        _lastEjected = who;
                        MelonLogger.Msg($"[ALIEN/LOCK] '{who}' took the {team.name} commander seat — " +
                                        "returned to the AI (lockAlienCommander is on in rtsai.json).");
                    }
                }
            }
            catch (Exception ex) { MelonLogger.Warning("[ALIEN/LOCK] tick threw: " + ex.Message); }
        }

        /// <summary>
        /// Put the AI back in the seat. Both calls are idempotent — the same pair
        /// the harness fires per team at round start — so re-running them on a
        /// team that already has its commander costs nothing.
        /// </summary>
        static void RestoreAiCommander(Team team, StrategyTeamSetup setup)
        {
            try
            {
                var settings = setup.AICommanderSettings ?? team.DefaultAICommanderSettings;
                AIManager.AddCommander(team, settings);
            }
            catch (Exception ex) { MelonLogger.Warning("[ALIEN/LOCK] AddCommander threw: " + ex.Message); }
            try { AIManager.EnableCommander(team, true); }
            catch (Exception ex) { MelonLogger.Warning("[ALIEN/LOCK] EnableCommander threw: " + ex.Message); }
        }

        internal static string BuildRoundSummaryFragment()
        {
            if (_ejections == 0) return "";
            return "--- Alien commander lock ---\n" +
                   $"  seat returned to the AI {_ejections} times\n";
        }
    }
}
