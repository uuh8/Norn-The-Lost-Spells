#if UNITY_EDITOR
using Game.Core;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Run.Tests
{
    public sealed class HubFeedbackAuthoringTests
    {
        [Test] public void DepartureDoesNotQueryOrRequireResidueApproval()
        {
            int queries=0;void Query(ExperimentWorldQueryEvent e){queries++;e.Query.Handled=e.Query.Ready=true;e.Query.Total=30;}
            var go=new GameObject("Residue policy test");EventBus<ExperimentWorldQueryEvent>.Subscribe(Query);
            try { Assert.That(go.AddComponent<ExperimentAreaController>().CheckDeparture(),Is.True);Assert.That(queries,Is.Zero); }
            finally { EventBus<ExperimentWorldQueryEvent>.Unsubscribe(Query);Object.DestroyImmediate(go); }
        }
        [Test] public void BothPracticeMarkersHaveRealPortalComponents()
        {
            var scene=Open();try
            {int count=0;foreach(var root in scene.GetRootGameObjects())foreach(var node in root.GetComponentsInChildren<Transform>(true))if(node.name=="远征点 · P7" || node.name=="远征点 · P8")
                {Assert.That(node.GetComponent("HubPracticePortal"),Is.Not.Null,node.name);Assert.That(node.GetComponent<Collider>()?.isTrigger,Is.True);count++;}Assert.That(count,Is.EqualTo(2));}
            finally{EditorSceneManager.ClosePreviewScene(scene);}
        }
        [Test] public void BackpackHasGroundItemRegionAndToolProjection()
        {
            var scene=Open();try
            {SinglePlayerInventoryController ui=null;foreach(var root in scene.GetRootGameObjects())foreach(var candidate in root.GetComponentsInChildren<SinglePlayerInventoryController>(true))
                if(new SerializedObject(candidate).FindProperty("_toggleAction").objectReferenceValue!=null)ui=candidate;
                Assert.That(new SerializedObject(ui).FindProperty("_groundRoot").objectReferenceValue,Is.Not.Null,"附近物品列表不能丢弃引用。");
                var backpack=ui.GetComponent<BackpackWorkspaceView>();
                if(backpack!=null) {Assert.That(ui.GetComponentsInChildren<ToolbeltSlotView>(true).Length,Is.EqualTo(HeldItemSlots.ToolCount));Assert.That(ui.GetComponentsInChildren<WandProgramPanelView>(true).Length,Is.EqualTo(3));}
                else {var workspace=ui.GetComponent<InventoryWorkspaceView>();Assert.That(new SerializedObject(workspace).FindProperty("_toolsRoot")?.objectReferenceValue,Is.Not.Null);}}
            finally{EditorSceneManager.ClosePreviewScene(scene);}
        }
        private static Scene Open()=>EditorSceneManager.OpenPreviewScene(SessionState.GetString("Norn.HubAlignment.ReviewPath","Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity"));
    }
}
#endif
