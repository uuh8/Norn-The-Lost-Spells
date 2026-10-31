using Game.Core;
using UnityEngine;

namespace Game.Run.Tests
{
    /// <summary>
    /// 仅测试Scene使用：在真实加载的Awake阶段发布本机Actor，保留当时Scene.isLoaded原值。
    /// CreateScene后手动Bind的旧测试已经错过这段生命周期，不能复现正式OnlineWorldBootstrap.Awake的时序。
    /// </summary>
    [DefaultExecutionOrder(-2000)]
    public sealed class EarlyCombatPresentationPublisher : MonoBehaviour
    {
        public bool Published { get; private set; }
        public bool SceneWasLoadedWhenPublished { get; private set; }
        private void Awake()
        {
            SceneWasLoadedWhenPublished = gameObject.scene.isLoaded;
            Published = true;
            LocalActorPresentation.Bind(GetInstanceID(), gameObject, true);
        }
        private void OnDestroy() => LocalActorPresentation.Clear(GetInstanceID());
    }
}
