using System;
using UnityEngine;

namespace Game.UI
{
    /// <summary>
    /// 开局玩法介绍的数据资产。文案与翻页数量不硬编码在 MonoBehaviour，
    /// 便于在 Inspector 调整操作键、Boss 目标和法术编程说明。
    /// </summary>
    [CreateAssetMenu(fileName = "GameplayGuide", menuName = "Game/UI/Gameplay Guide")]
    public sealed class GameplayGuideDefinition : ScriptableObject
    {
        [Serializable]
        public struct Page
        {
            public string Title;
            [TextArea(4, 12)] public string Body;
        }

        public Page[] Pages = Array.Empty<Page>();
    }
}
