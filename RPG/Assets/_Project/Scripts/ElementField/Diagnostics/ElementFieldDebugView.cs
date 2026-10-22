using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// Scene View 数据诊断工具。这里故意把 Cell 画成方块，以便直接观察 Material/Amount/Solid；
    /// 它不是最终 Water Mesh 或 Fire Particle，也不会参与 Game View Rendering。
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(ElementFieldRuntime))]
    public sealed class ElementFieldDebugView : MonoBehaviour
    {
        [Header("Visibility")]
        [SerializeField] private bool _onlyActiveCells = true;
        [SerializeField] private bool _drawSolidCells = true;
        [SerializeField, Min(-1)] private int _ySlice = -1;
        [SerializeField, Min(1)] private int _maxGizmos = 4096;
        [SerializeField, Range(0f, 1f)] private float _minimumActiveAlpha = 0.25f;

        [Header("Debug Colors")]
        [SerializeField] private Color _waterColor = new Color(0.1f, 0.55f, 1f, 1f);
        [SerializeField] private Color _fireColor = new Color(1f, 0.3f, 0.05f, 1f);
        [SerializeField] private Color _solidColor = new Color(0.25f, 0.25f, 0.25f, 0.45f);
        [SerializeField] private Color _emptyColor = new Color(0.55f, 0.55f, 0.55f, 0.04f);

        private ElementFieldRuntime _runtime;

        private void OnDrawGizmosSelected()
        {
            if (_runtime == null)
                _runtime = GetComponent<ElementFieldRuntime>();
            if (_runtime == null || !_runtime.IsInitialized)
                return;

            IElementFieldReadOnly field = _runtime.ReadOnlyField;
            Vector3Int dimensions = field.Dimensions;
            float cellSize = field.CellSize;
            Vector3 cubeSize = Vector3.one * (cellSize * 0.9f);
            int drawn = 0;

            for (int z = 0; z < dimensions.z; z++)
            for (int y = 0; y < dimensions.y; y++)
            for (int x = 0; x < dimensions.x; x++)
            {
                if (_ySlice >= 0 && y != _ySlice)
                    continue;

                bool solid = field.IsSolid(x, y, z);
                ElementCell cell = field.GetCell(x, y, z);
                if (_onlyActiveCells && cell.IsEmpty && (!solid || !_drawSolidCells))
                    continue;
                if (solid && !_drawSolidCells && cell.IsEmpty)
                    continue;
                if (drawn >= _maxGizmos)
                    return;

                Color color = ResolveColor(in cell, solid);
                Gizmos.color = color;
                Gizmos.DrawCube(
                    field.Origin + new Vector3(
                        (x + 0.5f) * cellSize,
                        (y + 0.5f) * cellSize,
                        (z + 0.5f) * cellSize),
                    cubeSize);
                drawn++;
            }
        }

        private Color ResolveColor(in ElementCell cell, bool solid)
        {
            if (cell.IsEmpty)
                return solid ? _solidColor : _emptyColor;

            Color color = cell.MaterialKind == MaterialId.Fire
                ? _fireColor
                : _waterColor;
            // Gameplay 强度仍按 Amount/255 归一化，但 Debug View 额外保留最小可见 Alpha。
            // 这只改变 Scene Gizmo 的诊断显示，不会放大真实 Amount，也不会影响后续 Water Mesh/Fire Particle。
            color.a *= CalculateActiveAlpha(cell.Amount, _minimumActiveAlpha);
            return color;
        }

        internal static float CalculateActiveAlpha(byte amount, float minimumActiveAlpha)
        {
            float normalizedAmount = amount / (float)byte.MaxValue;
            return Mathf.Max(Mathf.Clamp01(minimumActiveAlpha), normalizedAmount);
        }
    }
}
