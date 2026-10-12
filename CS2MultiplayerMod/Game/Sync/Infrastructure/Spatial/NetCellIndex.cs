using Unity.Collections;
using Unity.Mathematics;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    /// <summary>
    /// Uniform XZ grid over one realize cycle's net snapshot, so endpoint lookups scale with local
    /// density. Candidates only: callers keep their filters, and a multi-cell item can repeat.
    /// </summary>
    public struct NetCellIndex : System.IDisposable
    {
        private const int MaxAxisCells = 256;
        private const float MinCellSize = 8f;

        /// <summary>Larger items are visited by every query instead of filling thousands of buckets.</summary>
        private const int MaxCellsPerItem = 32;

        private float2 _origin;
        private float _cellSize;
        private int2 _dims;
        private NativeArray<int> _cellStart;
        private NativeArray<int> _cellItems;
        private NativeList<int> _spanning;

        /// <summary>Indexes by XZ bounds (<c>xy</c> min, <c>zw</c> max).</summary>
        public static NetCellIndex Build(NativeArray<float4> bounds)
        {
            var index = default(NetCellIndex);
            index._spanning = new NativeList<int>(4, Allocator.Temp);
            index._dims = new int2(1, 1);
            index._cellSize = MinCellSize;

            int count = bounds.Length;
            if (count == 0)
            {
                index._cellStart = new NativeArray<int>(2, Allocator.Temp);
                index._cellItems = new NativeArray<int>(0, Allocator.Temp);
                return index;
            }

            float2 min = bounds[0].xy;
            float2 max = bounds[0].zw;
            for (int i = 1; i < count; i++)
            {
                min = math.min(min, bounds[i].xy);
                max = math.max(max, bounds[i].zw);
            }

            // Aim for a handful of items per cell, whatever the city's size.
            float2 extent = math.max(max - min, MinCellSize);
            int axis = math.clamp((int)math.ceil(math.sqrt(count * 0.25f)), 1, MaxAxisCells);
            index._origin = min;
            index._cellSize = math.max(MinCellSize, math.cmax(extent) / axis);
            index._dims = math.clamp((int2)math.ceil(extent / index._cellSize), 1, MaxAxisCells);

            int cells = index._dims.x * index._dims.y;
            var starts = new NativeArray<int>(cells + 1, Allocator.Temp);
            int total = 0;
            for (int i = 0; i < count; i++)
            {
                if (!index.CellRange(bounds[i], out int2 lo, out int2 hi))
                {
                    index._spanning.Add(i);
                    continue;
                }
                for (int z = lo.y; z <= hi.y; z++)
                for (int x = lo.x; x <= hi.x; x++)
                {
                    starts[z * index._dims.x + x + 1]++;
                    total++;
                }
            }
            for (int c = 0; c < cells; c++) starts[c + 1] += starts[c];

            var items = new NativeArray<int>(total, Allocator.Temp);
            var fill = new NativeArray<int>(cells, Allocator.Temp);
            try
            {
                for (int c = 0; c < cells; c++) fill[c] = starts[c];
                for (int i = 0; i < count; i++)
                {
                    if (!index.CellRange(bounds[i], out int2 lo, out int2 hi)) continue;
                    for (int z = lo.y; z <= hi.y; z++)
                    for (int x = lo.x; x <= hi.x; x++)
                    {
                        int cell = z * index._dims.x + x;
                        items[fill[cell]++] = i;
                    }
                }
            }
            finally
            {
                fill.Dispose();
            }

            index._cellStart = starts;
            index._cellItems = items;
            return index;
        }

        public Enumerator Near(float2 point, float radius) =>
            Overlapping(point - radius, point + radius);

        public Enumerator Overlapping(float2 min, float2 max)
        {
            int2 lo = Cell(min);
            int2 hi = Cell(max);
            return new Enumerator(_cellStart, _cellItems, _spanning, _dims.x, lo, hi);
        }

        public void Dispose()
        {
            if (_cellStart.IsCreated) _cellStart.Dispose();
            if (_cellItems.IsCreated) _cellItems.Dispose();
            if (_spanning.IsCreated) _spanning.Dispose();
        }

        private bool CellRange(float4 bound, out int2 lo, out int2 hi)
        {
            lo = Cell(bound.xy);
            hi = Cell(bound.zw);
            int2 span = hi - lo + 1;
            return span.x * span.y <= MaxCellsPerItem;
        }

        private int2 Cell(float2 position)
        {
            // Clamped: out-of-range or non-finite positions land on the boundary cell.
            return math.clamp((int2)math.floor((position - _origin) / _cellSize),
                int2.zero, _dims - 1);
        }

        public struct Enumerator
        {
            private readonly NativeArray<int> _cellStart;
            private readonly NativeArray<int> _cellItems;
            private readonly NativeList<int> _spanning;
            private readonly int _stride;
            private readonly int _x0, _x1, _z1;
            private int _x, _z;
            private int _cursor, _cursorEnd;
            private int _spanCursor;
            private int _current;

            internal Enumerator(NativeArray<int> cellStart, NativeArray<int> cellItems,
                NativeList<int> spanning, int stride, int2 lo, int2 hi)
            {
                _cellStart = cellStart;
                _cellItems = cellItems;
                _spanning = spanning;
                _stride = stride;
                _x0 = lo.x;
                _x1 = hi.x;
                _z1 = hi.y;
                _x = lo.x;
                _z = lo.y;
                _cursor = 0;
                _cursorEnd = 0;
                _spanCursor = 0;
                _current = -1;
            }

            public int Current => _current;

            public bool MoveNext()
            {
                while (true)
                {
                    if (_cursor < _cursorEnd)
                    {
                        _current = _cellItems[_cursor++];
                        return true;
                    }
                    if (_z <= _z1)
                    {
                        int cell = _z * _stride + _x;
                        _cursor = _cellStart[cell];
                        _cursorEnd = _cellStart[cell + 1];
                        if (++_x > _x1) { _x = _x0; _z++; }
                        continue;
                    }
                    if (_spanning.IsCreated && _spanCursor < _spanning.Length)
                    {
                        _current = _spanning[_spanCursor++];
                        return true;
                    }
                    return false;
                }
            }
        }
    }
}
