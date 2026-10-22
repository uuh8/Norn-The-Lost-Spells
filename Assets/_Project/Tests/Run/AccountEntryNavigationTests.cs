using Game.Core;
using Game.UI;
using NUnit.Framework;
using UnityEngine;

namespace Game.Run.Tests
{
    /// <summary>检查主菜单旧Play入口也只申请身份验证，避免留下可直接LoadScene的旁路。</summary>
    public sealed class AccountEntryNavigationTests
    {
        private int _requests;
        private OnlineLobbyAction _action;
        private void OnRequest(OnlineLobbyRequestEvent request) { _requests++; _action = request.Action; }
        [Test] public void StartGame_RequiresFreshBusinessValidation()
        {
            var root = new GameObject("Menu entry test");
            EventBus<OnlineLobbyRequestEvent>.Subscribe(OnRequest);
            try
            {
                var menu = root.AddComponent<DemoMainMenuController>(); menu.StartGame();
                Assert.That(_requests, Is.EqualTo(1)); Assert.That(_action, Is.EqualTo(OnlineLobbyAction.EnterHub));
            }
            finally { EventBus<OnlineLobbyRequestEvent>.Unsubscribe(OnRequest); Object.DestroyImmediate(root); }
        }
    }
}
