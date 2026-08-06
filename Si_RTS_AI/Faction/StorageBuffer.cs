using MelonLoader;
using Silica;
using SilicaAdminMod;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Si_RTS_AI.Faction
{
    /// <summary>
    /// A pile of free Bio Caches next to the Nest, placed once at a fixed minute,
    /// for one purpose: STOP THE INCOME CLIPPING AT THE STORAGE CAP.
    ///
    /// Team resource capacity is about 4,000 per Bio Cache, so an economy earning
    /// 360/s fills its bank and the game then discards everything it earns. The
    /// old workaround was AutoResourceDrain, which kept the measurement alive by
    /// DESTROYING the money — 315k of a 988k round on 2026-08-06 — and credited
    /// it back into cumulIncome, so the headline number was never money the AI
    /// had. DrMuck, 2026-08-06: "it is just about having a money buffer (so
    /// seeing no money clipping there because biocaches getting full there)."
    ///
    /// This raises the ceiling instead of emptying the bank. 100 caches ≈ +400k
    /// capacity, which no 25-minute round comes close to filling, so income is
    /// neither clipped nor faked and cash-in-hand is real.
    ///
    /// SPAWNED, NOT BUILT. HelperMethods.SpawnAtLocation is the admin path — no
    /// cost, no anchor, no build time, no placement validation — which is why
    /// they may be stacked on one spot. They therefore do NOT measure anything
    /// about the bot's ability to expand, and every consumer that counts Bio
    /// Caches has to ignore them: see IsBufferPos, called from BcMetrics and
    /// BcIncome.
    ///
    /// This is a MEASUREMENT SCAFFOLD, not a strategy. Off unless
    /// storageBufferCaches is set in rtsai.json.
    /// </summary>
    internal static class StorageBuffer
    {
        /// <summary>Anything inside this of the pile centre is scaffolding, not
        /// economy. Generous — the pile is jittered over a few metres and a real
        /// Bio Cache this close to the Nest would be working a patch the Nest
        /// already covers.</summary>
        const float TAG_RADIUS_M = 90f;

        /// <summary>Spawns per tick. 100 GameObjects in one frame is a visible
        /// hitch on a box that already logs 50ms spikes, and the pile has no
        /// reason to appear all at once.</summary>
        const int PER_TICK = 10;

        static readonly Dictionary<Team, int> _placed = new Dictionary<Team, int>();
        static Vector3 _centre;
        static bool _haveCentre;
        static bool _announced;
        static string _classname;

        internal static void ResetForNewRound()
        {
            _placed.Clear();
            _centre = Vector3.zero;
            _haveCentre = false;
            _announced = false;
            _classname = null;
        }

        /// <summary>Prefab names worth trying, best first, then the first one
        /// that actually spawns. A probe costs one throwaway structure, which is
        /// cheaper than a night of empty piles.</summary>
        static string ResolveClassname(ConstructionData cd, Team team)
        {
            var names = new List<string>(4);
            void Add(string s)
            {
                if (string.IsNullOrEmpty(s)) return;
                s = s.Replace("(Clone)", "").Trim();
                if (!names.Contains(s)) names.Add(s);
            }

            // ObjectToBuild and ObjectInfo.Prefab both point at the real
            // structure prefab ("Alien_BioCache"); ObjectInfo.name is the data
            // asset and is only worth trying last.
            Add(NameOfMember(cd, "ObjectToBuild"));
            try { Add(NameOfMember(cd.ObjectInfo, "Prefab")); } catch { }
            try { Add(cd.ObjectInfo?.name); } catch { }

            foreach (var n in names)
            {
                try
                {
                    var probe = HelperMethods.SpawnAtLocation(n, _centre, Quaternion.identity, team.Index);
                    if (probe != null)
                    {
                        MelonLogger.Msg($"[BUFFER] prefab '{n}' spawns (tried: {string.Join(", ", names)})");
                        return n;      // the probe itself counts as the first cache
                    }
                }
                catch (Exception ex)
                { MelonLogger.Warning($"[BUFFER] probe '{n}' threw: {ex.Message}"); }
            }
            MelonLogger.Warning($"[BUFFER] none of these spawned: {string.Join(", ", names)}");
            return null;
        }

        /// <summary>UnityEngine.Object.name of a member, whether the build
        /// exposes it as a property or a field.</summary>
        static string NameOfMember(object owner, string member)
        {
            if (owner == null) return null;
            try
            {
                var t = owner.GetType();
                object v = t.GetProperty(member)?.GetValue(owner) ?? t.GetField(member)?.GetValue(owner);
                return v is UnityEngine.Object uo ? uo.name : null;
            }
            catch { return null; }
        }

        /// <summary>True for a position inside the scaffold pile. Consumers that
        /// count or attribute Bio Caches must skip these — they never earn, they
        /// have no patch, and left in they would read as a hundred idle sites.</summary>
        internal static bool IsBufferPos(Vector3 p)
        {
            if (!_haveCentre) return false;
            float dx = p.x - _centre.x, dz = p.z - _centre.z;
            return dx * dx + dz * dz <= TAG_RADIUS_M * TAG_RADIUS_M;
        }

        internal static void Tick(Team team, Vector3 nestPos, ConstructionData bcCd, float roundT)
        {
            int want = Planning.RtsaiConfig.Int("storageBufferCaches", 0);
            if (want <= 0 || team == null || bcCd == null) return;
            if (nestPos == Vector3.zero) return;

            float atMin = Planning.RtsaiConfig.Float("storageBufferAtMinute", 10f);
            if (roundT < atMin * 60f) return;

            _placed.TryGetValue(team, out int done);
            if (done >= want) return;

            // South of the Nest — "below" on the map view — far enough out that
            // the pile is not inside the Nest's own footprint but well within
            // the base, so it is never contested ground.
            if (!_haveCentre)
            {
                float off = Planning.RtsaiConfig.Float("storageBufferOffsetM", 150f);
                _centre = new Vector3(nestPos.x, nestPos.y, nestPos.z - off);
                _haveCentre = true;
            }

            // SPAWN BY PREFAB NAME, NOT BY ObjectInfo NAME.
            //
            // The first version passed bcCd.ObjectInfo.name, which is
            // "ObjectInfo_Alien_BioCache" — a data asset, not something
            // SpawnAtLocation can instantiate. All 100 calls returned null and
            // the round got no buffer. The prefab is "Alien_BioCache", reachable
            // as ObjectToBuild or ObjectInfo.Prefab. Both are reflected because
            // the Il2Cpp surface differs between builds, and every candidate is
            // tried until one spawns rather than trusting any single field.
            if (_classname == null)
            {
                _classname = ResolveClassname(bcCd, team);
                if (_classname == null)
                {
                    MelonLogger.Warning("[BUFFER] no spawnable prefab name found on the Bio Cache " +
                                        "ConstructionData — storage buffer disabled for this round");
                    _placed[team] = want;      // do not retry every tick
                    return;
                }
                done += 1;                     // the successful probe is cache #1
                _placed[team] = done;
            }
            string classname = _classname;

            int capBefore = 0;
            try { capBefore = team.ResourceCapacity; } catch { }

            int spawned = 0, failed = 0;
            for (int i = done; i < want && spawned < PER_TICK; i++)
            {
                // A few metres of jitter rather than one exact point. They are
                // allowed to intersect — the admin spawn does no placement
                // validation — but identical transforms are the kind of thing
                // that upsets physics and networking for no benefit.
                float a = i * 2.39996f;                       // golden angle, radians
                float r = 3f * Mathf.Sqrt(i);
                var p = new Vector3(_centre.x + Mathf.Cos(a) * r,
                                    _centre.y,
                                    _centre.z + Mathf.Sin(a) * r);
                try
                {
                    var go = HelperMethods.SpawnAtLocation(classname, p, Quaternion.identity, team.Index);
                    if (go != null) spawned++; else failed++;
                }
                catch (Exception ex)
                {
                    failed++;
                    if (failed == 1)
                        MelonLogger.Warning("[BUFFER] SpawnAtLocation threw: " + ex.Message);
                }
            }

            done += spawned;
            _placed[team] = done + failed;      // failures count against the budget too

            int capAfter = 0;
            try { capAfter = team.ResourceCapacity; } catch { }

            if (!_announced)
            {
                _announced = true;
                MelonLogger.Msg($"[BUFFER] storage scaffold: {want} free Bio Caches at " +
                                $"({_centre.x:F0},{_centre.z:F0}), {atMin:F0} min into the round. " +
                                $"Capacity {capBefore:N0} -> {capAfter:N0} so far. " +
                                "These are SPAWNED, not built — excluded from Bio Cache metrics.");
            }
            if (done + failed >= want)
                MelonLogger.Msg($"[BUFFER] complete: {done} placed, {failed} failed. " +
                                $"team capacity now {capAfter:N0}" +
                                (capAfter <= capBefore
                                    ? "  — WARNING: capacity did not rise, a spawned Bio Cache may not count toward storage"
                                    : ""));
        }
    }
}
