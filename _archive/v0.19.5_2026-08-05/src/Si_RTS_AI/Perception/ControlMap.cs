using MelonLoader;
using Silica;
using System;
using UnityEngine;
using Si_RTS_AI.Perception.MapLayers;
using Si_RTS_AI.Planning;

namespace Si_RTS_AI.Perception
{
    /// <summary>
    /// How much of the map we actually hold.
    ///
    /// The economy planner has always scored a placement by what it produces —
    /// a patch tapped, a chain extended. That is a purely local question, and
    /// it is why expansion comes out as a single travelling front: once a
    /// direction is being worked, the cheapest next placement is always the
    /// next hop along the SAME direction, so the frontier walks instead of
    /// fanning. Nothing in the score ever noticed that the whole north half of
    /// the map was empty.
    ///
    /// Control is the missing term. It answers "how much ground would this
    /// placement claim that we do not already own", which is large for the
    /// first structure in an empty quadrant and near zero for the twentieth
    /// node down an existing chain — even though the local economics of the two
    /// look similar. Weighted properly it lets a long chain into open ground
    /// beat a short chain into ground we already sit on, which is exactly the
    /// trade the single-front behaviour was getting wrong.
    ///
    /// It also replaces a hardcoded workaround. BEARING_SPREAD_BONUS was a flat
    /// payment for expanding on a fresh compass bearing from the Nest — a proxy
    /// for "somewhere we are not", and a bad one, since bearing ignores distance
    /// and map shape both. Control measures the thing that proxy was pointing at.
    ///
    /// Radii come from the structures themselves (MaximumBaseStructureDistance
    /// via EcoSimulator), so a balance mod that changes Node reach changes the
    /// footprint here too.
    ///
    /// NOT fog-gated, unlike ThreatMap. Our own holdings are not something we
    /// need to scout, and enemy structures only subtract where we have seen
    /// them — so the field is honest in both directions without pretending we
    /// have forgotten where our own base is.
    /// </summary>
    internal static class ControlMap
    {
        const float TICK_S = 2f;

        static float[] _ours = new float[0];
        static float[] _theirs = new float[0];
        static float _lastBuildAt;
        static int _heldCells;

        internal static void ResetForNewRound()
        {
            _ours = new float[0];
            _theirs = new float[0];
            _lastBuildAt = 0f;
            _heldCells = 0;
        }

        /// <summary>Fraction of the whole map we hold. Cheap progress metric —
        /// this is the number that should climb through a round.</summary>
        internal static float HeldFraction =>
            GridWorld.CellCount > 0 ? _heldCells / (float)GridWorld.CellCount : 0f;

        static void EnsureSized()
        {
            int n = GridWorld.CellCount;
            if (_ours.Length != n) { _ours = new float[n]; _theirs = new float[n]; }
        }

        /// <summary>Rebuild from scratch at 2Hz-ish. Rebuilt rather than decayed
        /// because control is a property of what is STANDING — a razed base
        /// should stop counting immediately, unlike remembered threat.</summary>
        internal static void Rebuild(Team self)
        {
            if (self == null) return;
            float now = Time.time;
            if (now - _lastBuildAt < TICK_S) return;
            _lastBuildAt = now;

            EnsureSized();
            Array.Clear(_ours, 0, _ours.Length);
            Array.Clear(_theirs, 0, _theirs.Length);

            try
            {
                StampTeam(self, _ours);

                var all = Team.Teams;
                if (all != null)
                    for (int t = 0; t < all.Count; t++)
                    {
                        var other = all[t];
                        if (other == null || other == self) continue;
                        StampTeam(other, _theirs);
                    }

                int held = 0;
                for (int i = 0; i < _ours.Length; i++)
                    if (_ours[i] > _theirs[i] && _ours[i] > 0f) held++;
                _heldCells = held;
            }
            catch (Exception ex) { MelonLogger.Warning("[CONTROL] rebuild threw: " + ex.Message); }
        }

        static void StampTeam(Team team, float[] field)
        {
            var structs = team.Structures;
            if (structs == null) return;
            for (int i = 0; i < structs.Count; i++)
            {
                var st = structs[i];
                if (st == null || st.ObjectInfo == null || st.IsDestroyed) continue;

                // A structure holds roughly as much ground as it can anchor
                // further building from — that is the same reach the chain
                // rules use, so control and buildability agree by construction
                // instead of needing their own tuned radius.
                float reachM = EcoSimulator.NODE_REACH_M;
                string dn = st.ObjectInfo.DisplayName ?? "";
                if (dn == "Nest") reachM = EcoSimulator.BC_REACH_M * 2f;
                else if (dn == "Bio Cache") reachM = EcoSimulator.BC_REACH_M;

                AddDisk(field, st.transform.position, reachM);
            }
        }

        static void AddDisk(float[] field, Vector3 world, float radiusM)
        {
            int cx = GridWorld.CellX(world.x), cz = GridWorld.CellZ(world.z);
            int r = Mathf.Max(1, Mathf.CeilToInt(radiusM / GridWorld.CellSize));
            int r2 = r * r;
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                {
                    if (dx * dx + dz * dz > r2) continue;
                    int x = cx + dx, z = cz + dz;
                    if (x < 0 || z < 0 || x >= GridWorld.Width || z >= GridWorld.Height) continue;
                    field[z * GridWorld.Width + x] += 1f;
                }
        }

        /// <summary>
        /// Fraction of ground within <paramref name="radiusM"/> of this point
        /// that we do NOT already hold — 1.0 for virgin ground, ~0 for the
        /// middle of our own base.
        ///
        /// This is the term that pays for a long chain into an empty quadrant.
        /// Cells the enemy holds still count as gain: taking ground off them is
        /// worth at least as much as taking neutral ground, and the military
        /// layer can revise that later once it can judge whether we would keep it.
        /// </summary>
        internal static float ControlGain(Vector3 world, float radiusM)
        {
            EnsureSized();
            if (_ours.Length == 0) return 1f;

            int cx = GridWorld.CellX(world.x), cz = GridWorld.CellZ(world.z);
            int r = Mathf.Max(1, Mathf.CeilToInt(radiusM / GridWorld.CellSize));
            int r2 = r * r;
            int total = 0, unheld = 0;
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                {
                    if (dx * dx + dz * dz > r2) continue;
                    int x = cx + dx, z = cz + dz;
                    if (x < 0 || z < 0 || x >= GridWorld.Width || z >= GridWorld.Height) continue;
                    total++;
                    if (_ours[z * GridWorld.Width + x] <= 0f) unheld++;
                }
            return total == 0 ? 1f : unheld / (float)total;
        }
    }
}
