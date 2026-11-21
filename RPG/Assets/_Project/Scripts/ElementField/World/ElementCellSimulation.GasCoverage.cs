using UnityEngine;

namespace Game.ElementField
{
    public sealed partial class ElementCellSimulation
    {
        private sealed partial class GasEnvironment
        {
            // 两个连续时间段各保存至多 32 个活动源的空间交集。远处的玩家、投射物或相机移动时，
            // 已经连续处在同一活动区的薄雾仍能证明在整段延时内可模拟，无需逐帧访问世界里的每个气页。
            private sealed class CoverageWindow
            {
                private readonly Bounds[] _common = new Bounds[ElementWorldActivityRegions.MaximumGpuRegions];
                private uint _valid;
                private bool _all = true;
                internal void Reset() { _all = true; _valid = 0; }

                internal void Intersect(ElementWorldActivityRegions regions)
                {
                    if (regions == null) return;
                    uint next = 0;
                    for (int i = 0; i < regions.Count; i++)
                    {
                        uint bit = 1u << i;
                        Bounds bounds = regions.GetBounds(i);
                        if (!_all)
                        {
                            if ((_valid & bit) == 0) continue;
                            Vector3 low = Vector3.Max(_common[i].min, bounds.min);
                            Vector3 high = Vector3.Min(_common[i].max, bounds.max);
                            if (low.x >= high.x || low.y >= high.y || low.z >= high.z) continue;
                            bounds.SetMinMax(low, high);
                        }
                        _common[i] = bounds;
                        next |= bit;
                    }
                    _all = false; _valid = next;
                }

                internal bool Contains(Vector3 low, Vector3 high)
                {
                    if (_all) return true;
                    uint mask = _valid;
                    for (int i = 0; mask != 0; mask >>= 1, i++)
                        if ((mask & 1) != 0 && ContainsChunk(_common[i],low,high)) return true;
                    return false;
                }
            }

            private CoverageWindow _currentCoverage, _priorCoverage;
            private bool _hasPriorCoverage;
            private double _coverageStart;

            private void RecordActivityCoverage(ElementWorldActivityRegions regions, bool changed, float dt)
            {
                if (_profile.DiluteMaximumCellGmu == 0) return;
                double start = _elapsed - dt;
                if (_currentCoverage == null)
                {
                    if (regions == null || _chunks.Count == 0) return;
                    _currentCoverage = new CoverageWindow(); _priorCoverage = new CoverageWindow();
                    _coverageStart = start; changed = true;
                }
                else if (start - _coverageStart >= _profile.DiluteDelaySeconds)
                {
                    var completed = _currentCoverage; _currentCoverage = _priorCoverage; _priorCoverage = completed;
                    _hasPriorCoverage = true; _currentCoverage.Reset(); _coverageStart = start; changed = true;
                }
                if (changed) _currentCoverage.Intersect(regions);
            }

            private bool ContinuouslyCoveredForDelay(Vector3Int key)
            {
                if (_currentCoverage == null) return false;
                ChunkCenters(key, out var low, out var high);
                return _currentCoverage.Contains(low, high) && (_elapsed - _coverageStart >= _profile.DiluteDelaySeconds ||
                    _hasPriorCoverage && _priorCoverage.Contains(low, high));
            }
        }
    }
}
