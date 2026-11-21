using UnityEngine;

namespace Game.Rendering
{
    internal sealed partial class CellularMaterialSurfaceMesh
    {
        // 顺序与原顶面的 winding 一致。只在真正的自由岸边生成坡肩；满池、内面和贴墙处走既有路径。
        private static readonly Vector3[] ShoreCorners = { Vector3.zero, Vector3.forward,
            Vector3.right + Vector3.forward, Vector3.right };
        private static readonly Vector3Int[] ShoreFace = { Vector3Int.left, Vector3Int.forward, Vector3Int.right, Vector3Int.back };
        private readonly sbyte[] _side = new sbyte[4], _diag = new sbyte[4];
        private readonly float[] _level = new float[4];
        private readonly Vector3[] _rim = new Vector3[16], _direction = new Vector3[16];
        private readonly bool[] _outerSegment = new bool[16];
        private readonly uint[] _wallVersions = new uint[HaloSize * HaloSize * HaloSize];
        private readonly bool[] _walls = new bool[HaloSize * HaloSize * HaloSize];
        private int _rimCount;

        // 0 是可露出的岸边，1 是连续浅液，2 是硬边界（地形、异材、满格或承托上层的水柱）。
        private sbyte ShoreNeighbor(Vector3Int cell, Vector3Int offset, CellularMaterialRenderer.Sample own)
        {
            var sample = Neighbor(cell, offset, out var address);
            // 近远两种网格相交的格边都采用原角点高度。近侧坡肩只在本侧收拢，切换距离没有裂缝。
            if (!_source.DetailedShoreAt(address)) return 2;
            if (sample.Quantity == 0)
            {
                if (_edgePage && CellularMaterialRenderer.OffsetWrapped(cell, offset, address)) return 2;
                int x = address.x - _origin.x + 1, y = address.y - _origin.y + 1, z = address.z - _origin.z + 1;
                int i = x + HaloSize * (y + HaloSize * z);
                if (_wallVersions[i] != _version)
                {
                    _wallVersions[i] = _version;
                    _walls[i] = _source.IsShoreWall(address);
                }
                return _walls[i] ? (sbyte)2 : (sbyte)0;
            }
            return sample.Material == own.Material && sample.Quantity < _capacity &&
                Neighbor(address, Vector3Int.up, out _).Quantity == 0 ? (sbyte)1 : (sbyte)2;
        }

        private bool TryAppendShore(Vector3Int cell, Vector3 p, CellularMaterialRenderer.Sample sample,
            float h00, float h01, float h11, float h10)
        {
            bool exposed = false;
            for (int i = 0; i < 4; i++)
            {
                _side[i] = ShoreNeighbor(cell, ShoreFace[i], sample);
                _diag[i] = ShoreNeighbor(cell, ShoreFace[(i+3)&3] + ShoreFace[i], sample);
                exposed |= _side[i] == 0 || _diag[i] == 0;
            }
            if (!exposed) return false;
            _level[0] = h00; _level[1] = h01; _level[2] = h11; _level[3] = h10;
            _rimCount = 0;
            for (int i = 0; i < 4; i++)
            {
                int prev = (i+3)&3, next = (i+1)&3;
                Vector3 corner = ShoreCorners[i], a = ShoreCorners[prev] - corner, b = ShoreCorners[next] - corner;
                // 同一格角最多由四格共享。角高、岸线朝向由这四个已提交样本决定；
                // 有硬边界就保留原高度，避免岸边处理在冷湖外壁或岩壁旁切出缝。
                bool locked = _side[prev] == 2 || _side[i] == 2 || _diag[i] == 2;
                Vector3 outward = Vector3.zero;
                if (!locked)
                {
                    Vector3 back = ShoreFace[prev], forward = ShoreFace[i];
                    if (_side[prev] == 0) outward += back - forward;
                    if (_side[i] == 0) outward += forward - back;
                    if (_diag[i] == 0) outward += forward + back;
                }
                int open = (_side[prev] == 0 ? 1 : 0) + (_side[i] == 0 ? 1 : 0) + (_diag[i] == 0 ? 1 : 0);
                float edgeHeight = locked || open == 0 ? _level[i] : open == 1 ? _level[i] * .42f : _level[i] * .065f;
                if (!locked && _side[prev] == 0 && _side[i] == 0)
                {
                    // 只在真正暴露的凸角切圆弧。共享边不逐格倒角，因此相连的水不会出现一串圆角方砖。
                    const float trim = .36f;
                    AddRim(corner + a * trim, edgeHeight, (Vector3)ShoreFace[prev], true);
                    AddRim(corner + (a+b) * (trim * .29f), edgeHeight, outward.normalized, true);
                    AddRim(corner + b * trim, edgeHeight, (Vector3)ShoreFace[i], true);
                }
                else AddRim(corner, edgeHeight, outward.normalized, _side[i] == 0);
                float middle = (_level[i] + _level[next]) * .5f;
                AddRim((corner + ShoreCorners[next]) * .5f, _side[i] == 0 ? middle * .045f : middle,
                    _side[i] == 0 ? (Vector3)ShoreFace[i] : Vector3.zero, _side[i] == 0);
            }
            WriteShore(p, sample.Quantity / (float)_capacity, Fill(Neighbor(cell, Vector3Int.down, out _)) < 1f);
            // 固体、异材或满池交界处仍按精确高度裁掉被遮住的侧面；自由岸线已由下面的坡肩封闭。
            int slot = Slot(sample.Material);
            for (int i = 0; i < 4; i++)
            {
                if (_side[i] != 2) continue;
                int next = (i+1)&3;
                Vector3 a = p + ShoreCorners[i], b = p + ShoreCorners[next];
                Wall(slot, sample.Material, cell, ShoreFace[i], a, b, _level[i], _level[next],
                    (int)(ShoreCorners[i].x*2 - 1) - 2*ShoreFace[i].x, (int)(ShoreCorners[i].z*2 - 1) - 2*ShoreFace[i].z,
                    (int)(ShoreCorners[next].x*2 - 1) - 2*ShoreFace[i].x, (int)(ShoreCorners[next].z*2 - 1) - 2*ShoreFace[i].z, ShoreFace[i]);
            }
            return true;
        }

        private void AddRim(Vector3 point, float height, Vector3 outward, bool exposed)
        {
            point.y = height;
            _rim[_rimCount] = point;
            _direction[_rimCount] = outward;
            _outerSegment[_rimCount++] = exposed;
        }

        private void WriteShore(Vector3 p, float fill, bool bottom)
        {
            int first = _outVertices.Count;
            var center = new Vector3(.5f, fill, .5f);
            _outVertices.Add(p + center); _outNormals.Add(Vector3.up);
            // 两环共享顶点。内环按已提交液面采样，水边向下收拢；整个外壳留在有水的格内。
            for (int ring = 0; ring < (bottom ? 3 : 2); ring++)
            for (int i = 0; i < _rimCount; i++)
            {
                Vector3 point = _rim[i];
                Vector3 direction = _direction[i];
                float x = point.x, z = point.z;
                float surface = Mathf.Lerp(Mathf.Lerp(_level[0], _level[3], x), Mathf.Lerp(_level[1], _level[2], x), z);
                Vector3 normal = (Vector3.up + direction * (Mathf.Min(2f, surface / .22f) * (ring == 0 ? .28f : 1f))).normalized;
                if (ring == 0)
                {
                    point = Vector3.Lerp(point, center, .40f);
                    point.y = Mathf.Lerp(surface, fill, .40f);
                }
                else if (ring == 2) { point.y = 0; normal = Vector3.down; }
                _outVertices.Add(p + point); _outNormals.Add(normal);
            }
            int inner = first + 1, outer = inner + _rimCount, floor = outer + _rimCount;
            for (int i = 0; i < _rimCount; i++)
            {
                int next = (i+1) % _rimCount;
                Triangle(first, inner+i, inner+next);
                Triangle(inner+i, outer+i, outer+next);
                Triangle(inner+i, outer+next, inner+next);
                if (bottom && i > 0 && next != 0) Triangle(floor, floor+next, floor+i);
                if (!_outerSegment[i]) continue;
                Vector3 topA = _rim[i], topB = _rim[next], baseA = topA, baseB = topB;
                baseA.y = baseB.y = 0;
                Vector3 normal = Vector3.Cross(topB - topA, Vector3.up).normalized;
                AddQuad(_outVertices, _outNormals, _outIndices, p + baseA, p + baseB, p + topB, p + topA, normal);
            }
        }

        private void Triangle(int a, int b, int c)
        { _outIndices.Add(a); _outIndices.Add(b); _outIndices.Add(c); }
    }
}
