using UnityEngine;

namespace Game.Multiplayer
{
    /// <summary>数据驱动的业务入口，没有任何账号、数据库连接串或 Session Token。</summary>
    [CreateAssetMenu(menuName = "Game/Online/Service Profile")]
    public sealed class OnlineServiceProfile : ScriptableObject
    {
        [SerializeField] private string _baseUrl = "http://127.0.0.1:5080/";
        public string BaseUrl => _baseUrl;
    }
}
