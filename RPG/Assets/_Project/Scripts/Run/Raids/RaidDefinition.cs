using UnityEngine;
namespace Game.Run
{
    [CreateAssetMenu(menuName = "Norn/Run/Raid Definition")]
    public sealed class RaidDefinition : ScriptableObject
    {
        public string WorldScenePath;
        public Vector3 SpawnPosition = new Vector3(0, 1, -3);
        public Vector3 ExitPosition = new Vector3(0, .5f, 5);
        [Min(.1f)] public float ExitRadius = 1.5f;
        [Min(.1f)] public float ExtractionSeconds = 3;
        [Range(0, 2)] public int ProtectedSlots = 2;
        public ExtractionConditionDefinition Condition;
        public bool IsValid => !string.IsNullOrWhiteSpace(WorldScenePath) && ExitRadius > 0 && !float.IsInfinity(ExitRadius)
            && ExtractionSeconds > 0 && !float.IsInfinity(ExtractionSeconds) && ProtectedSlots >= 0 && ProtectedSlots <= 2;
    }
}
