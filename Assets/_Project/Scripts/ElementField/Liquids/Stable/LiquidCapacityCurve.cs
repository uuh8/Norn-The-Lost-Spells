using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 单台阶、平底占地或逐列变深湖盆的单调容量曲线。每层只记录累计容量，查询格子
    /// 时用该层有效列的排名分配整数余量，因此总 GMU 精确等于区域账本。
    /// </summary>
    public sealed class LiquidCapacityCurve
    {
        public readonly struct FootprintRun
        {
            public readonly int MinimumX, MaximumX, MinimumZ, MaximumZ;

            public FootprintRun(int minimumX, int maximumX, int minimumZ, int maximumZ)
            {
                MinimumX = minimumX;
                MaximumX = maximumX;
                MinimumZ = minimumZ;
                MaximumZ = maximumZ;
            }
        }

        private readonly Vector3Int _minimum, _maximum;
        private readonly int _stepX, _stepRise, _capacity;
        private readonly ulong[] _prefix;
        private readonly int[] _footprintRanks;
        private readonly FootprintRun[] _footprintRuns;
        private readonly int _footprintCount;
        private readonly int[] _floorByColumn, _heightfieldRanks, _layerColumnCounts;
        private readonly FootprintRun[] _surfaceRuns;
        private readonly int[] _previousRunByStart, _currentRunByStart;
        private readonly int _maximumSurfaceRuns;
        private int _surfaceRunCount, _surfaceRunFloorLimit = int.MinValue;
        private uint _surfaceTopologyVersion;

        public LiquidCapacityCurve(Vector3Int minimum, Vector3Int maximum,
            int stepX, int stepRise, int cellCapacity)
        {
            if (maximum.x <= minimum.x || maximum.y <= minimum.y || maximum.z <= minimum.z
                || stepX < minimum.x || stepX > maximum.x || stepRise < 0
                || stepRise >= maximum.y - minimum.y || cellCapacity < 1)
                throw new ArgumentOutOfRangeException(nameof(maximum));
            _minimum = minimum;
            _maximum = maximum;
            _stepX = stepX;
            _stepRise = stepRise;
            _capacity = cellCapacity;
            _prefix = new ulong[maximum.y - minimum.y + 1];
            for (int layer = 0; layer < _prefix.Length - 1; layer++)
            {
                int width = WetWidthAtLayer(layer);
                ulong layerCapacity = checked((ulong)width * (ulong)(maximum.z - minimum.z)
                    * (ulong)cellCapacity);
                _prefix[layer + 1] = checked(_prefix[layer] + layerCapacity);
            }
        }

        public LiquidCapacityCurve(Vector3Int minimum, Vector3Int maximum,
            bool[] footprint, int cellCapacity)
        {
            long width = (long)maximum.x - minimum.x;
            long depth = (long)maximum.z - minimum.z;
            int height = maximum.y - minimum.y;
            if (width <= 0 || depth <= 0 || width > int.MaxValue / depth
                || height <= 0 || cellCapacity < 1 || footprint == null
                || footprint.Length < width * depth)
                throw new ArgumentOutOfRangeException(nameof(footprint));

            _minimum = minimum;
            _maximum = maximum;
            _stepX = maximum.x;
            _capacity = cellCapacity;
            // 只在注册期建立列排名和连续行段；Tick 内按格查询无需扫描岸线。
            int horizontalWidth = (int)width;
            _footprintRanks = new int[(int)(width * depth)];
            Array.Fill(_footprintRanks, -1);
            var runs = new List<FootprintRun>();
            var previousByStart = new int[horizontalWidth];
            var currentByStart = new int[horizontalWidth];
            Array.Fill(previousByStart, -1);
            Array.Fill(currentByStart, -1);
            int rank = 0;
            for (int z = 0; z < depth; z++)
            {
                int x = 0;
                while (x < horizontalWidth)
                {
                    int index = z * horizontalWidth + x;
                    if (!footprint[index]) { x++; continue; }
                    int start = x;
                    do
                    {
                        _footprintRanks[z * horizontalWidth + x] = rank++;
                        x++;
                    }
                    while (x < horizontalWidth && footprint[z * horizontalWidth + x]);
                    int previous = previousByStart[start];
                    if (previous >= 0 && runs[previous].MaximumX == minimum.x + x)
                    {
                        FootprintRun old = runs[previous];
                        runs[previous] = new FootprintRun(old.MinimumX, old.MaximumX,
                            old.MinimumZ, minimum.z + z + 1);
                        currentByStart[start] = previous;
                    }
                    else
                    {
                        currentByStart[start] = runs.Count;
                        runs.Add(new FootprintRun(minimum.x + start, minimum.x + x,
                            minimum.z + z, minimum.z + z + 1));
                    }
                }
                // 上一行同宽段延伸为矩形；两行不相同才需要新增 Patch。
                int[] swap = previousByStart;
                previousByStart = currentByStart;
                currentByStart = swap;
                Array.Fill(currentByStart, -1);
            }
            if (rank == 0) throw new ArgumentOutOfRangeException(nameof(footprint));
            _footprintCount = rank;
            _footprintRuns = runs.ToArray();
            _prefix = new ulong[height + 1];
            ulong layerCapacity = checked((ulong)rank * (ulong)cellCapacity);
            for (int layer = 0; layer < height; layer++)
                _prefix[layer + 1] = checked(_prefix[layer] + layerCapacity);
        }

        public LiquidCapacityCurve(Vector3Int minimum, Vector3Int maximum,
            int[] floorByColumn, int cellCapacity)
        {
            long width = (long)maximum.x - minimum.x;
            long depth = (long)maximum.z - minimum.z;
            long height = (long)maximum.y - minimum.y;
            if (width <= 0 || depth <= 0 || height <= 0
                || width > int.MaxValue / depth || height > int.MaxValue
                || floorByColumn == null || floorByColumn.Length < width * depth
                || cellCapacity < 1)
                throw new ArgumentOutOfRangeException(nameof(floorByColumn));

            _minimum = minimum;
            _maximum = maximum;
            _stepX = maximum.x;
            _capacity = cellCapacity;
            int area = (int)(width * depth);
            int layers = (int)height;
            int horizontalWidth = (int)width;
            _floorByColumn = new int[area];
            _heightfieldRanks = new int[area];
            _layerColumnCounts = new int[layers];
            _prefix = new ulong[layers + 1];
            Array.Fill(_heightfieldRanks, -1);
            var sorted = new long[area];
            int included = 0;
            for (int index = 0; index < area; index++)
            {
                int floor = floorByColumn[index];
                if (floor != int.MaxValue && (floor < minimum.y || floor >= maximum.y))
                    throw new ArgumentOutOfRangeException(nameof(floorByColumn));
                _floorByColumn[index] = floor;
                if (floor == int.MaxValue) continue;
                _layerColumnCounts[floor - minimum.y]++;
                sorted[included++] = ((long)floor << 32) | (uint)index;
            }
            if (included == 0) throw new ArgumentOutOfRangeException(nameof(floorByColumn));
            Array.Sort(sorted, 0, included);
            for (int rank = 0; rank < included; rank++)
                _heightfieldRanks[(int)(uint)sorted[rank]] = rank;
            int activeColumns = 0;
            for (int layer = 0; layer < layers; layer++)
            {
                activeColumns += _layerColumnCounts[layer];
                _layerColumnCounts[layer] = activeColumns;
                _prefix[layer + 1] = checked(_prefix[layer]
                    + (ulong)activeColumns * (ulong)cellCapacity);
            }

            // 预留最多每列一个 Patch。注册时遍历所有水位层得到最坏 Patch 数，
            // 后续水位跨层只改写数组，不在 Rendering 热路径扩容或漏画岸线。
            _surfaceRuns = new FootprintRun[area];
            _previousRunByStart = new int[horizontalWidth];
            _currentRunByStart = new int[horizontalWidth];
            int maximumRuns = 0;
            for (int floorLimit = minimum.y; floorLimit < maximum.y; floorLimit++)
            {
                BuildSurfaceRuns(floorLimit);
                maximumRuns = Math.Max(maximumRuns, _surfaceRunCount);
            }
            _maximumSurfaceRuns = maximumRuns;
            _surfaceRunFloorLimit = int.MinValue;
            _surfaceRunCount = 0;
        }

        public Vector3Int Minimum => _minimum;
        public Vector3Int Maximum => _maximum;
        public int StepX => _stepX;
        public int StepRise => _stepRise;
        public bool HasFootprint => _footprintRanks != null || _floorByColumn != null;
        public bool HasHeightfield => _floorByColumn != null;
        public uint SurfaceTopologyVersion => _surfaceTopologyVersion;
        public int SurfacePatchCapacity => HasHeightfield ? _maximumSurfaceRuns
            : HasFootprint ? _footprintRuns.Length : 2;
        public int FootprintRunCount => HasHeightfield ? _surfaceRunCount
            : HasFootprint ? _footprintRuns.Length : 0;
        public FootprintRun GetFootprintRun(int index) => HasHeightfield
            ? _surfaceRuns[index] : _footprintRuns[index];
        public ulong Capacity => _prefix[_prefix.Length - 1];

        public bool Contains(Vector3Int cell)
        {
            if (cell.x < _minimum.x || cell.x >= _maximum.x || cell.y < _minimum.y
                || cell.y >= _maximum.y || cell.z < _minimum.z || cell.z >= _maximum.z)
                return false;
            int index = (cell.z - _minimum.z) * (_maximum.x - _minimum.x)
                + cell.x - _minimum.x;
            if (HasHeightfield) return _floorByColumn[index] <= cell.y;
            return !HasFootprint || _footprintRanks[index] >= 0;
        }

        public uint QuantityAt(Vector3Int cell, ulong total)
        {
            if (!Contains(cell)) return 0;
            int layer = cell.y - _minimum.y;
            if (HasHeightfield)
            {
                if (total <= _prefix[layer]) return 0;
                ulong layerAmount = Math.Min(total - _prefix[layer],
                    _prefix[layer + 1] - _prefix[layer]);
                int index = (cell.z - _minimum.z) * (_maximum.x - _minimum.x)
                    + cell.x - _minimum.x;
                ulong rank = (ulong)_heightfieldRanks[index];
                ulong count = (ulong)_layerColumnCounts[layer];
                return (uint)(layerAmount / count + (rank < layerAmount % count ? 1ul : 0ul));
            }
            if (HasFootprint)
            {
                if (total <= _prefix[layer]) return 0;
                ulong layerAmount = Math.Min(total - _prefix[layer],
                    _prefix[layer + 1] - _prefix[layer]);
                int index = (cell.z - _minimum.z) * (_maximum.x - _minimum.x)
                    + cell.x - _minimum.x;
                ulong rank = (ulong)_footprintRanks[index];
                ulong count = (ulong)_footprintCount;
                return (uint)(layerAmount / count + (rank < layerAmount % count ? 1ul : 0ul));
            }
            int width = WetWidthAtLayer(layer);
            if (cell.x >= _minimum.x + width || width == 0 || total <= _prefix[layer]) return 0;
            ulong stepLayerAmount = Math.Min(total - _prefix[layer],
                _prefix[layer + 1] - _prefix[layer]);
            ulong stepCount = (ulong)width * (ulong)(_maximum.z - _minimum.z);
            ulong stepRank = (ulong)(cell.z - _minimum.z) * (ulong)width
                + (ulong)(cell.x - _minimum.x);
            return (uint)(stepLayerAmount / stepCount
                + (stepRank < stepLayerAmount % stepCount ? 1ul : 0ul));
        }

        public float SurfaceHeightInCells(ulong total)
        {
            if (total == 0) return _minimum.y;
            total = Math.Min(total, Capacity);
            int low = 0, high = _prefix.Length - 1;
            while (low < high)
            {
                int mid = (low + high + 1) / 2;
                if (_prefix[mid] <= total) low = mid;
                else high = mid - 1;
            }
            if (low == _prefix.Length - 1) return _maximum.y;
            ulong layerCapacity = _prefix[low + 1] - _prefix[low];
            return _minimum.y + low + (layerCapacity == 0 ? 0f
                : (float)(total - _prefix[low]) / layerCapacity);
        }

        public int FloorYAtX(int x) => _minimum.y + (x >= _stepX ? _stepRise : 0);

        public void RefreshSurfaceRuns(ulong total)
        {
            if (!HasHeightfield) return;
            int floorLimit = SurfaceFloorLimit(total);
            if (floorLimit == _surfaceRunFloorLimit) return;
            // 水位可以跨过没有新岸线的层；此时只记录层级变化，避免重复上传同一 Mesh。
            if (WetColumnCountAt(floorLimit) == WetColumnCountAt(_surfaceRunFloorLimit))
            {
                _surfaceRunFloorLimit = floorLimit;
                return;
            }
            BuildSurfaceRuns(floorLimit);
            _surfaceRunFloorLimit = floorLimit;
            _surfaceTopologyVersion++;
        }

        private int WetColumnCountAt(int floorLimit) => floorLimit < _minimum.y ? 0
            : _layerColumnCounts[Math.Min(floorLimit - _minimum.y,
                _layerColumnCounts.Length - 1)];

        private int SurfaceFloorLimit(ulong total)
        {
            if (total == 0) return int.MinValue;
            total = Math.Min(total, Capacity);
            int low = 0, high = _prefix.Length - 1;
            while (low < high)
            {
                int mid = (low + high + 1) / 2;
                if (_prefix[mid] <= total) low = mid;
                else high = mid - 1;
            }
            return total == _prefix[low] ? _minimum.y + low - 1
                : _minimum.y + low;
        }

        private void BuildSurfaceRuns(int floorLimit)
        {
            _surfaceRunCount = 0;
            if (floorLimit < _minimum.y) return;
            int width = _maximum.x - _minimum.x;
            int depth = _maximum.z - _minimum.z;
            int[] previous = _previousRunByStart;
            int[] current = _currentRunByStart;
            Array.Fill(previous, -1);
            Array.Fill(current, -1);
            for (int z = 0; z < depth; z++)
            {
                int x = 0;
                while (x < width)
                {
                    if (_floorByColumn[z * width + x] > floorLimit) { x++; continue; }
                    int start = x;
                    do { x++; }
                    while (x < width && _floorByColumn[z * width + x] <= floorLimit);
                    int prior = previous[start];
                    if (prior >= 0 && _surfaceRuns[prior].MaximumX == _minimum.x + x)
                    {
                        FootprintRun old = _surfaceRuns[prior];
                        _surfaceRuns[prior] = new FootprintRun(old.MinimumX, old.MaximumX,
                            old.MinimumZ, _minimum.z + z + 1);
                        current[start] = prior;
                    }
                    else
                    {
                        current[start] = _surfaceRunCount;
                        _surfaceRuns[_surfaceRunCount++] = new FootprintRun(
                            _minimum.x + start, _minimum.x + x,
                            _minimum.z + z, _minimum.z + z + 1);
                    }
                }
                int[] swap = previous;
                previous = current;
                current = swap;
                Array.Fill(current, -1);
            }
        }

        private int WetWidthAtLayer(int layer) =>
            layer < _stepRise ? _stepX - _minimum.x : _maximum.x - _minimum.x;
    }
}
