using UnityEngine;

namespace Game.Run
{
    /// <summary>视觉绑定不发奖。PropertyBlock按实例覆写贴图，不生成/污染共享Material。</summary>
    public static class WorldSpellIconPresentation
    {
        public static bool Apply(GameObject visual,Sprite icon)
        {
            if(visual==null||icon==null)return false;
            var cube=visual.transform.Find("Visual/Cube");
            if(cube==null)cube=visual.transform.Find("Cube");
            var renderer=cube!=null?cube.GetComponent<MeshRenderer>():null;if(renderer==null)return false;
            var rect=icon.textureRect;var texture=icon.texture;
            // Sprite裁剪坐标是像素；Material ST是归一化UV的scale/offset。
            var st=new Vector4(rect.width/texture.width,rect.height/texture.height,rect.x/texture.width,rect.y/texture.height);
            var block=new MaterialPropertyBlock();renderer.GetPropertyBlock(block);
            block.SetTexture("_BaseMap",texture);block.SetTexture("_MainTex",texture);
            block.SetVector("_BaseMap_ST",st);block.SetVector("_MainTex_ST",st);
            block.SetColor("_BaseColor",Color.white);renderer.SetPropertyBlock(block);return true;
        }
    }
}
