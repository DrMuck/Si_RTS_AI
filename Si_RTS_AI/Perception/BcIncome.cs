using MelonLoader;
using Silica;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Si_RTS_AI.Perception
{
    /// <summary>
    /// WHICH BIO CACHE EARNED WHAT.
    ///
    /// Income has only ever been measured team-wide, which answers "is the
    /// economy working" and none of the questions the expansion experiment
    /// actually asks: what did this expansion cost, when did it first return
    /// anything, and is it returning anything at all. A Bio Cache with no
    /// workers looks identical to one quietly out-earning the base.
    ///
    /// Shrimps deposit into the Bio Cache's own resource holder, so the sum of
    /// its POSITIVE storage deltas is what was delivered there. Sampled on the
    /// AI tick (~3s) against a deposit that takes 8s to unload, so a delivery
    /// cannot slip between samples.
    ///
    /// SELF-CHECKING, because the mechanism is an assumption. If the game moves
    /// resources to the team pool without ever parking them in the structure,
    /// every delta is zero and the attribution is silently all-zero — which
    /// would look like "the expansions earned nothing" rather than "the meter
    /// is broken". So the total attributed is compared against the team's own
    /// cumulative income and the ratio is logged. Near 1.0 means the numbers
    /// can be trusted; near 0 means this approach does not work here and the
    /// analysis must not use it.
    /// </summary>
    internal static class BcIncome
    {
        struct Entry
        {
            public Vector3 Pos;
            public int     LastStored;
            public long    Deposited;      // sum of positive deltas
            public float   FirstDepositT;  // round time of the first delivery, -1 until then
            public float   BuiltAtT;       // round time we first saw it standing
            // Sparse history, for "what has this earned LATELY". A Bio Cache on
            // a drained patch has a large lifetime total and is worth nothing to
            // defend; the recent number is the one that decides.
            public float   MarkT;
            public long    MarkDeposited;
            public long    RecentWindow;   // deposited since MarkT
        }

        /// <summary>How far back "lately" reaches. Long enough to survive a
        /// group walking between patches, short enough that a depleted site
        /// stops looking valuable within a couple of minutes.</summary>
        const float RECENT_WINDOW_S = 120f;

        // Bio Caches do not move, so position rounded to the metre is a stable
        // identity across samples and across the game's own list reordering.
        static readonly Dictionary<long, Entry> _bcs = new Dictionary<long, Entry>();
        static long _attributed;
        static float _lastCheckAt;
        static bool _probed;

        internal static void ResetForNewRound()
        {
            _bcs.Clear();
            _attributed = 0;
            _lastCheckAt = 0f;
            _probed = false;
        }

        static long Key(Vector3 p) =>
            ((long)Mathf.RoundToInt(p.x) << 20) ^ (long)Mathf.RoundToInt(p.z);

        internal static void Sample(Team team, string bcDisplayName)
        {
            if (team == null) return;
            float roundT;
            try { roundT = MapLayers.LayerReplay.CurrentRoundTime; } catch { return; }

            try
            {
                var structs = team.Structures;
                if (structs == null) return;

                // ONE-SHOT PROBE. The meter produced nothing at all on
                // 2026-08-05 — no attribution, and not even the "attribution not
                // working" warning it was built to emit, which is the worse
                // failure: a self-check that fails silently. Static reading did
                // not settle whether the structures are not matching, the
                // reflection is returning null, or the storage simply never
                // holds anything. So the next round says which.
                if (!_probed && roundT > 120f)
                {
                    _probed = true;
                    int matched = 0; string firstName = "-"; int firstStored = -1;
                    bool holdersFound = false;
                    for (int i = 0; i < structs.Count; i++)
                    {
                        var st = structs[i];
                        if (st?.ObjectInfo == null || st.IsDestroyed) continue;
                        if (firstName == "-") firstName = st.ObjectInfo.DisplayName ?? "?";
                        if (!string.Equals(st.ObjectInfo.DisplayName, bcDisplayName,
                                           StringComparison.OrdinalIgnoreCase)) continue;
                        matched++;
                        if (firstStored < 0)
                        {
                            firstStored = ReadStored(st);
                            holdersFound = _piHolders != null;
                        }
                    }
                    MelonLogger.Msg($"[BC/INCOME] probe: structures={structs.Count} " +
                                    $"matching '{bcDisplayName}'={matched} firstSeenName='{firstName}' " +
                                    $"storedOnFirstMatch={firstStored} ResourceHoldersProp=" +
                                    (holdersFound ? "found" : "NULL") +
                                    (matched == 0 ? "  <-- name mismatch, nothing will ever be attributed" :
                                     firstStored == 0 ? "  <-- storage reads 0; if it stays 0 the game does not park resources in the structure" : ""));
                }

                for (int i = 0; i < structs.Count; i++)
                {
                    var s = structs[i];
                    if (s == null || s.ObjectInfo == null || s.IsDestroyed) continue;
                    if (!string.Equals(s.ObjectInfo.DisplayName, bcDisplayName,
                                       StringComparison.OrdinalIgnoreCase)) continue;

                    Vector3 p = s.transform.position;
                    long k = Key(p);
                    int stored = ReadStored(s);

                    if (!_bcs.TryGetValue(k, out var e))
                    {
                        e = new Entry { Pos = p, LastStored = stored, Deposited = 0,
                                        FirstDepositT = -1f, BuiltAtT = roundT };
                        _bcs[k] = e;
                        continue;
                    }

                    int d = stored - e.LastStored;
                    if (d > 0)
                    {
                        e.Deposited += d;
                        _attributed += d;
                        if (e.FirstDepositT < 0f) e.FirstDepositT = roundT;
                    }
                    e.LastStored = stored;

                    // Roll the window forward once it is full: what came in
                    // since the mark becomes "recent", and a new mark is set.
                    if (roundT - e.MarkT >= RECENT_WINDOW_S)
                    {
                        e.RecentWindow  = e.Deposited - e.MarkDeposited;
                        e.MarkDeposited = e.Deposited;
                        e.MarkT         = roundT;
                    }
                    _bcs[k] = e;
                }
            }
            catch (Exception ex) { MelonLogger.Warning("[BC/INCOME] sample threw: " + ex.Message); }

            MaybeSelfCheck(team, roundT);
        }

        /// <summary>Does the attribution add up to what the team actually
        /// earned? If not, say so loudly rather than exporting zeros.</summary>
        static void MaybeSelfCheck(Team team, float roundT)
        {
            if (Time.time - _lastCheckAt < 120f) return;
            _lastCheckAt = Time.time;
            if (roundT < 180f) return;

            int teamCum = 0;
            try { teamCum = EcoRateSampler.GetCumulativeIncome(team); } catch { }
            if (teamCum <= 0)
            {
                // Was a silent return, which is how the meter managed to report
                // nothing at all rather than reporting that it could not report.
                MelonLogger.Msg($"[BC/INCOME] no team cumulative income yet " +
                                $"(roundT={roundT:F0}s, bcs tracked={_bcs.Count}) — " +
                                "cannot check attribution");
                return;
            }

            float ratio = _attributed / (float)teamCum;
            int earning = 0, idle = 0;
            foreach (var kv in _bcs)
                if (kv.Value.Deposited > 0) earning++; else idle++;

            MelonLogger.Msg($"[BC/INCOME] attributed={_attributed} teamCumulative={teamCum} " +
                            $"ratio={ratio:F2} bcs={_bcs.Count} earning={earning} untapped={idle}" +
                            (ratio < 0.2f
                                ? "  <-- ATTRIBUTION NOT WORKING, do not use per-BC income"
                                : ""));
        }

        /// <summary>
        /// What this Bio Cache has delivered LATELY — the number that decides
        /// whether it is worth defending. Lifetime totals say a drained site was
        /// once excellent, which is exactly the wrong answer for a garrison.
        /// </summary>
        internal static long RecentDeposited(UnityEngine.Vector3 pos)
        {
            if (!_bcs.TryGetValue(Key(pos), out var e)) return 0;
            // In-progress window counts too, or a site looks dead for two
            // minutes after every roll.
            return e.RecentWindow + (e.Deposited - e.MarkDeposited);
        }

        /// <summary>Every Bio Cache we have seen, with what it earned lately.</summary>
        internal static void ForEach(Action<UnityEngine.Vector3, long, long> fn)
        {
            foreach (var kv in _bcs)
                fn(kv.Value.Pos, kv.Value.Deposited,
                   kv.Value.RecentWindow + (kv.Value.Deposited - kv.Value.MarkDeposited));
        }

        /// <summary>Per-Bio-Cache totals for the metrics line. Deposited is what
        /// was delivered here; firstDepositT is time-to-first-return, which is
        /// the number an expansion is actually judged on.</summary>
        internal static bool TryGet(Vector3 pos, out long deposited, out float firstDepositT,
                                    out float builtAtT)
        {
            if (_bcs.TryGetValue(Key(pos), out var e))
            {
                deposited = e.Deposited; firstDepositT = e.FirstDepositT; builtAtT = e.BuiltAtT;
                return true;
            }
            deposited = 0; firstDepositT = -1f; builtAtT = -1f;
            return false;
        }

        // Same reflected path EcoStateBuilder uses — ResourceHolders is not
        // cleanly exposed through the typed API under Il2Cpp interop.
        static PropertyInfo _piHolders, _piAmount;
        static int ReadStored(object entity)
        {
            try
            {
                if (_piHolders == null)
                    _piHolders = entity.GetType().GetProperty("ResourceHolders",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (_piHolders == null) return 0;
                var list = _piHolders.GetValue(entity) as System.Collections.IEnumerable;
                if (list == null) return 0;
                int total = 0;
                foreach (var h in list)
                {
                    if (_piAmount == null)
                        _piAmount = h.GetType().GetProperty("AmountStored",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (_piAmount == null) return 0;
                    var v = _piAmount.GetValue(h);
                    if (v is int i) total += i;
                }
                return total;
            }
            catch { return 0; }
        }
    }
}
