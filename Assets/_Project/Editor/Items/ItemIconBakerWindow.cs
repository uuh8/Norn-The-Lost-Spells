using Game.Run;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>预览与导出复用同一隔离拍摄路径；预览只读模型，不写当前Scene。</summary>
    public sealed class ItemIconBakerWindow:EditorWindow
    {
        private ItemDefinition _item;
        private Vector3 _angle=new Vector3(18,135,0);
        private float _padding=1.22f;
        private Texture2D _preview;
        [MenuItem("Tools/Norn/物品/图标拍摄预览与配置")]
        private static void Open()=>GetWindow<ItemIconBakerWindow>("物品图标拍摄");
        private void OnGUI()
        {
            _item=(ItemDefinition)EditorGUILayout.ObjectField("预览物品",_item,typeof(ItemDefinition),false);
            _angle=EditorGUILayout.Vector3Field("相机Euler角度",_angle);
            _padding=EditorGUILayout.Slider("取景留白倍率",_padding,1.01f,1.8f);
            EditorGUILayout.HelpBox("角度与留白用于本次拍摄。默认批量只补缺失图标；重新拍摄也只覆盖工具目录内的PNG，人工Icon保持。不会保存当前Scene。",MessageType.Info);
            using(new EditorGUI.DisabledScope(_item==null || _item.WorldModel==null || EditorApplication.isPlaying))
                if(GUILayout.Button("预览当前模型")){Release();_preview=ItemIconBaker.Render(_item.WorldModel,512,_angle,_padding);}
            if(_preview!=null){var rect=GUILayoutUtility.GetRect(256,256);EditorGUI.DrawTextureTransparent(rect,_preview,ScaleMode.ScaleToFit);}
            using(new EditorGUI.DisabledScope(EditorApplication.isPlaying))
            {
                if(GUILayout.Button("按当前参数生成缺失图标"))ItemIconBaker.BakeAll(false,_angle,_padding);
                if(GUILayout.Button("按当前参数重新拍摄工具生成的图标"))ItemIconBaker.BakeAll(true,_angle,_padding);
            }
        }
        private void OnDisable()=>Release();
        private void Release(){if(_preview!=null)DestroyImmediate(_preview);_preview=null;}
    }
}
