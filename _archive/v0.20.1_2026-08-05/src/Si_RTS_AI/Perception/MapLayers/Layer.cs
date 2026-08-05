using System;
using UnityEngine;

namespace Si_RTS_AI.Perception.MapLayers
{
    // Two typed layer variants. Semantics + memory both benefit from picking the
    // narrowest type that fits: FoW/mask layers are boolean (byte), counts and
    // weighted-sum layers are integer (int32). We don't currently need float layers;
    // add LayerF if a future consumer wants sub-integer weighting.

    /// <summary>Boolean grid: 1 byte per cell, 0/1 semantics.</summary>
    public sealed class LayerB
    {
        byte[] _data = new byte[0];
        public byte[] Data { get { EnsureSized(); return _data; } }

        // Called at the top of every public method that touches storage. Resizes when
        // GridWorld's dimensions have changed since last use (e.g., new round with a
        // different map). Cheap when size hasn't changed.
        public void EnsureSized()
        {
            if (_data.Length != GridWorld.CellCount) _data = new byte[GridWorld.CellCount];
        }

        public void Clear() { EnsureSized(); Array.Clear(_data, 0, _data.Length); }

        public bool IsSet(int cx, int cz) { EnsureSized(); return _data[cz * GridWorld.Width + cx] != 0; }
        public void SetOne(int cx, int cz) { EnsureSized(); _data[cz * GridWorld.Width + cx] = 1; }

        public void SetOneAtWorld(Vector3 pos) =>
            SetOne(GridWorld.CellX(pos.x), GridWorld.CellZ(pos.z));

        /// <summary>Set every cell inside a filled disk of `metersRadius` around `pos` to 1.</summary>
        public void SetDiskAtWorld(Vector3 pos, float metersRadius)
        {
            EnsureSized();
            int cx = GridWorld.CellX(pos.x), cz = GridWorld.CellZ(pos.z);
            int r  = GridWorld.MetersToCellsInt(metersRadius);
            int r2 = r * r;
            int w  = GridWorld.Width, h = GridWorld.Height;
            int xmin = cx - r; if (xmin < 0) xmin = 0;
            int xmax = cx + r; if (xmax >= w) xmax = w - 1;
            int zmin = cz - r; if (zmin < 0) zmin = 0;
            int zmax = cz + r; if (zmax >= h) zmax = h - 1;
            for (int z = zmin; z <= zmax; z++)
            {
                int dz = z - cz, rowBase = z * w;
                for (int x = xmin; x <= xmax; x++)
                {
                    int dx = x - cx;
                    if (dx * dx + dz * dz > r2) continue;
                    _data[rowBase + x] = 1;
                }
            }
        }

        /// <summary>Bitwise-OR every set cell from `other` into this layer.</summary>
        public void OrFrom(LayerB other)
        {
            EnsureSized(); other.EnsureSized();
            byte[] o = other._data;
            for (int i = 0; i < _data.Length; i++)
                if (o[i] != 0) _data[i] = 1;
        }

        /// <summary>
        /// Morphological dilation: for each cell, output = 1 iff any input cell within
        /// (2r+1)² window is 1. Result written to `into` (which may be `this`).
        /// Uses an int running-sum internally to avoid an intermediate int layer.
        /// </summary>
        public void DilateInto(int r, LayerB into)
        {
            EnsureSized();
            into.EnsureSized();
            int w = GridWorld.Width, h = GridWorld.Height;
            byte[] src = _data;
            // Two-pass: work through a temporary since we can't safely alias input/output.
            byte[] tmp = (into == this || into._data.Length != _data.Length) ? new byte[_data.Length] : into._data;
            for (int z = 0; z < h; z++)
            {
                int zmin = z - r; if (zmin < 0) zmin = 0;
                int zmax = z + r; if (zmax >= h) zmax = h - 1;
                for (int x = 0; x < w; x++)
                {
                    int xmin = x - r; if (xmin < 0) xmin = 0;
                    int xmax = x + r; if (xmax >= w) xmax = w - 1;
                    byte hit = 0;
                    for (int zz = zmin; zz <= zmax && hit == 0; zz++)
                    {
                        int rowBase = zz * w;
                        for (int xx = xmin; xx <= xmax; xx++)
                        {
                            if (src[rowBase + xx] != 0) { hit = 1; break; }
                        }
                    }
                    tmp[z * w + x] = hit;
                }
            }
            if (tmp != into._data) Array.Copy(tmp, into._data, tmp.Length);
        }
    }

    /// <summary>Integer grid: 4 bytes per cell. Used for weighted sums and pressures.</summary>
    public sealed class LayerI
    {
        int[] _data = new int[0];
        public int[] Data { get { EnsureSized(); return _data; } }

        public void EnsureSized()
        {
            if (_data.Length != GridWorld.CellCount) _data = new int[GridWorld.CellCount];
        }

        public void Clear() { EnsureSized(); Array.Clear(_data, 0, _data.Length); }

        public int  Get(int cx, int cz) { EnsureSized(); return _data[cz * GridWorld.Width + cx]; }
        public void Set(int cx, int cz, int v) { EnsureSized(); _data[cz * GridWorld.Width + cx] = v; }
        public void Add(int cx, int cz, int v) { EnsureSized(); _data[cz * GridWorld.Width + cx] += v; }

        public void AddAtWorld(Vector3 pos, int amount) =>
            Add(GridWorld.CellX(pos.x), GridWorld.CellZ(pos.z), amount);

        /// <summary>Sum a byte layer into this layer using a (2r+1)² box kernel (cluster count).</summary>
        public void SumBoxFrom(LayerB src, int r)
        {
            EnsureSized();
            src.EnsureSized();
            int w = GridWorld.Width, h = GridWorld.Height;
            byte[] a = src.Data;
            for (int z = 0; z < h; z++)
            {
                int zmin = z - r; if (zmin < 0) zmin = 0;
                int zmax = z + r; if (zmax >= h) zmax = h - 1;
                for (int x = 0; x < w; x++)
                {
                    int xmin = x - r; if (xmin < 0) xmin = 0;
                    int xmax = x + r; if (xmax >= w) xmax = w - 1;
                    int sum = 0;
                    for (int zz = zmin; zz <= zmax; zz++)
                    {
                        int rowBase = zz * w;
                        for (int xx = xmin; xx <= xmax; xx++)
                            sum += a[rowBase + xx];
                    }
                    _data[z * w + x] = sum;
                }
            }
        }

        /// <summary>
        /// Sum a byte layer into this layer using a filled DISK (circle) kernel of
        /// radius r cells. Cell value = count of source cells inside the disk.
        /// Correct model for "how many resources would fall inside a circular build
        /// radius if a structure was placed at this cell" — the box kernel over-counts
        /// on the diagonals.
        /// </summary>
        public void SumCircleFrom(LayerB src, int r)
        {
            EnsureSized();
            src.EnsureSized();
            int w = GridWorld.Width, h = GridWorld.Height;
            byte[] a = src.Data;
            int r2 = r * r;
            for (int z = 0; z < h; z++)
            {
                int zmin = z - r; if (zmin < 0) zmin = 0;
                int zmax = z + r; if (zmax >= h) zmax = h - 1;
                for (int x = 0; x < w; x++)
                {
                    int xmin = x - r; if (xmin < 0) xmin = 0;
                    int xmax = x + r; if (xmax >= w) xmax = w - 1;
                    int sum = 0;
                    for (int zz = zmin; zz <= zmax; zz++)
                    {
                        int dz = zz - z;
                        int rowBase = zz * w;
                        int dz2 = dz * dz;
                        for (int xx = xmin; xx <= xmax; xx++)
                        {
                            int dx = xx - x;
                            if (dx * dx + dz2 > r2) continue;
                            sum += a[rowBase + xx];
                        }
                    }
                    _data[z * w + x] = sum;
                }
            }
        }

        /// <summary>Multiply this layer by a boolean mask elementwise (mask=0 → 0).</summary>
        public void MaskInPlace(LayerB mask)
        {
            EnsureSized();
            byte[] m = mask.Data;
            for (int i = 0; i < _data.Length; i++)
                if (m[i] == 0) _data[i] = 0;
        }

        public (int cx, int cz, int value) ArgMax()
        {
            EnsureSized();
            int bx = -1, bz = -1, best = 0;   // treat all-zero/negative as "no candidate"
            int w = GridWorld.Width, h = GridWorld.Height;
            for (int z = 0; z < h; z++)
            {
                int rowBase = z * w;
                for (int x = 0; x < w; x++)
                {
                    int v = _data[rowBase + x];
                    if (v > best) { best = v; bx = x; bz = z; }
                }
            }
            return (bx, bz, best);
        }

        public void SubtractDiskAtWorld(Vector3 pos, float metersRadius, int amount)
        {
            EnsureSized();
            int cx = GridWorld.CellX(pos.x), cz = GridWorld.CellZ(pos.z);
            int r  = GridWorld.MetersToCellsInt(metersRadius);
            int r2 = r * r;
            int w  = GridWorld.Width, h = GridWorld.Height;
            int xmin = cx - r; if (xmin < 0) xmin = 0;
            int xmax = cx + r; if (xmax >= w) xmax = w - 1;
            int zmin = cz - r; if (zmin < 0) zmin = 0;
            int zmax = cz + r; if (zmax >= h) zmax = h - 1;
            for (int z = zmin; z <= zmax; z++)
            {
                int dz = z - cz, rowBase = z * w;
                for (int x = xmin; x <= xmax; x++)
                {
                    int dx = x - cx;
                    if (dx * dx + dz * dz > r2) continue;
                    _data[rowBase + x] -= amount;
                }
            }
        }
    }
}
