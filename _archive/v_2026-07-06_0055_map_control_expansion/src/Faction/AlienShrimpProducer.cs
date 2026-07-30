using MelonLoader;
using Sandbox.UnitGenerator;
using Silica;
using System;
using UnityEngine;

namespace Si_RTS_AI.Faction
{
    /// <summary>
    /// Direct Shrimp queuing at every Lesser Spawning Cyst on managed Alien teams.
    ///
    /// Why not rely on stock's AIUnitHandler + Phase 3.1 override:
    ///   We proved via 76 [P31] samples that AIUnitHandler DOES decide "Harvest preset
    ///   needs a Shrimp" repeatedly, and our postfix returns Shrimp every time. But
    ///   only 9 shrimps actually got built across a full round. Stock's picking-a-unit
    ///   step happens far more often than its actually-queueing-that-unit-in-a-Cyst
    ///   step, and the ratio isn't in our favour.
    ///
    /// So we go direct: every AI tick, for every Cyst we own that can build Shrimp,
    /// call Cyst.Construct(shrimpCd). Cyst's queue accepts what it can; the rest are
    /// no-ops. The game's normal cost + unit-cap gates still apply — we're not
    /// bypassing those.
    ///
    /// Target: SHRIMPS_PER_BC per owned BioCache (the user's "10-15 per biotics" spec).
    /// Called from the AlienConstruction.HandleTick prefix on Alien teams with override.
    /// </summary>
    internal static class AlienShrimpProducer
    {
        const int   SHRIMPS_PER_BC = 12;   // user asked for 10-15 per biotics; 12 is a floor
        // Server-performance ceiling — user reported 255 shrimps @ 12min on a
        // 19-BC network (SHRIMPS_PER_BC × BCs = 228 target that overflowed due
        // to in-flight queues). Cap here so late-game networks don't spawn
        // shrimps beyond what the eco can absorb (crowd factor drops to zero
        // at 18 shrimps per patch anyway) and the physics/AI ticks stay fast.
        const int   SHRIMP_HARD_CAP = 200;
        // Money management: each queued Shrimp reserves its cost. Keeping the queue
        // shallow (2 per Cyst) frees cash to spend elsewhere in the meantime instead
        // of pre-reserving 3+ shrimps that only trickle out one at a time anyway.
        const float CYST_QUEUE_MAX = 2;

        // Called every AI tick. Cache _shrimpCd lazily.
        static ConstructionData? _shrimpCd;

        internal static int Queued;
        internal static int Skipped;

        internal static void Tick(Team team)
        {
            try { Run(team); }
            catch (Exception ex) { MelonLogger.Warning("[RTSA/P32] Shrimp producer threw: " + ex.Message); }
        }

        static void Run(Team team)
        {
            EnsureShrimpCd(team);
            if (_shrimpCd == null) return;

            int bcCount     = CountByName(team, "Bio Cache");
            int shrimpCount = CountUnitsByName(team, "Shrimp");
            int target = System.Math.Min(SHRIMPS_PER_BC * bcCount, SHRIMP_HARD_CAP);

            var structs = team.Structures;
            if (structs == null) return;

            // Count already-queued shrimps across every Cyst so the total of
            // live + queued stays under SHRIMP_HARD_CAP. Previously we only
            // checked live count → cap 200 overshoot to 240 as queued shrimps
            // finished after the cap hit.
            int queuedTotal = 0;
            for (int qi = 0; qi < structs.Count; qi++)
            {
                var qs = structs[qi];
                if (qs?.ProductionQueue == null) continue;
                try { queuedTotal += qs.ProductionQueue.Count; } catch { }
            }

            if (bcCount == 0 || shrimpCount + queuedTotal >= target) return;

            int stillWanted = target - shrimpCount - queuedTotal;
            for (int i = 0; i < structs.Count && stillWanted > 0; i++)
            {
                var s = structs[i];
                if (s == null || s.ObjectInfo == null || s.IsDestroyed) continue;

                // Any Cyst that has Shrimp in its ConstructionOptions.
                if (s.ConstructionOptions == null || !s.ConstructionOptions.Contains(_shrimpCd)) continue;

                // Real production-queue depth. UnitGeneratorComponent.Generators
                // (previously used here) tracks ACTIVE spawn processes, not queued
                // items — so it stayed near zero and never triggered the cap. The
                // Structure.ProductionQueue property is the actual list of queued
                // units (populated by Construct(), popped when a unit spawns).
                int queueDepth = 0;
                try { queueDepth = s.ProductionQueue?.Count ?? 0; } catch { }
                if (queueDepth >= CYST_QUEUE_MAX)
                {
                    Skipped++;
                    continue;
                }

                // Fire — cost + cap enforced inside Construct.
                try
                {
                    var res = s.Construct(_shrimpCd);
                    if (res == ProductionActionResult.Success)
                    {
                        Queued++;
                        stillWanted--;
                    }
                    else
                    {
                        Skipped++;
                    }
                }
                catch (Exception ex) { MelonLogger.Warning("[RTSA/P32] Cyst.Construct threw: " + ex.Message); }
            }
        }

        static void EnsureShrimpCd(Team team)
        {
            if (_shrimpCd != null) return;
            var structs = team.Structures;
            if (structs == null) return;
            for (int i = 0; i < structs.Count; i++)
            {
                var s = structs[i];
                if (s == null || s.ConstructionOptions == null) continue;
                foreach (var opt in s.ConstructionOptions)
                {
                    if (opt?.ObjectInfo == null) continue;
                    if (string.Equals(opt.ObjectInfo.DisplayName, "Shrimp", StringComparison.OrdinalIgnoreCase))
                    {
                        _shrimpCd = opt;
                        return;
                    }
                }
            }
        }

        static int CountByName(Team team, string displayName)
        {
            int n = 0;
            var structs = team.Structures;
            if (structs != null)
                for (int i = 0; i < structs.Count; i++)
                {
                    var s = structs[i];
                    if (s?.ObjectInfo == null) continue;
                    if (string.Equals(s.ObjectInfo.DisplayName, displayName, StringComparison.OrdinalIgnoreCase)) n++;
                }
            return n;
        }

        static int CountUnitsByName(Team team, string displayName)
        {
            int n = 0;
            var units = team.Units;
            if (units == null) return n;
            for (int i = 0; i < units.Count; i++)
            {
                var u = units[i];
                if (u?.ObjectInfo == null) continue;
                if (string.Equals(u.ObjectInfo.DisplayName, displayName, StringComparison.OrdinalIgnoreCase)) n++;
            }
            return n;
        }

        internal static void ResetForNewRound()
        {
            Queued = 0;
            Skipped = 0;
        }

        internal static string BuildRoundSummaryFragment()
        {
            if (Queued == 0 && Skipped == 0) return "";
            return "--- Phase 3.2 slice 1 (direct Shrimp queuing at Cysts) ---\n" +
                   $"  Shrimps queued this round:  {Queued}\n" +
                   $"  Skipped (queue full / fail): {Skipped}\n";
        }
    }
}
