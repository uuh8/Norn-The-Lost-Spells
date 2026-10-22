using UnityEngine;

namespace Game.Combat
{
    [CreateAssetMenu(menuName = "Game/Combat/Status Database", fileName = "StatusDatabase")]
    public class StatusDatabase : ScriptableObject
    {
        [SerializeField] private StatusDefinition[] _definitions;

        public bool TryGet(StatusKind kind, out StatusDefinition definition)
        {
            if (_definitions != null)
            {
                for (int i = 0; i < _definitions.Length; i++)
                {
                    StatusDefinition candidate = _definitions[i];
                    if (candidate != null && candidate.Kind == kind)
                    {
                        definition = candidate;
                        return true;
                    }
                }
            }

            definition = null;
            return false;
        }
    }
}
