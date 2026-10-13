using UnityEngine;
namespace Game.Run
{
    [CreateAssetMenu(menuName = "Norn/Run/Extraction Condition")]
    public sealed class ExtractionConditionDefinition : ScriptableObject
    { [Min(0)] public int RequiredCollectedGmu = 10; public string RequiredObjectiveKey; }
}
