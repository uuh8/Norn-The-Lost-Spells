using UnityEngine;
namespace Game.Rendering
{
    [CreateAssetMenu(menuName="Game/Rendering/World Item Highlight")]
    public sealed class WorldItemHighlightProfile:ScriptableObject
    {
        [ColorUsage(false,true)] public Color Color=new Color(.65f,.95f,1,1);
        [Range(0,2)]public float Strength=1.15f;
        [Range(0,2)]public float HaloStrength=.8f;
        [ColorUsage(false,true)]public Color HaloColor=new Color(.12f,.65f,1,1);
        [Range(2,24)]public float HaloRadiusPixels=12;
        public Shader FallbackShader;
        public Shader MaskShader;
        [Range(.5f,4)] public float OutlinePixels=2.5f;
        // 外壳使用平滑法线派生Mesh；原Mesh保持硬边光照，Editor处理避免Runtime读取不可读网格。
        [System.Serializable] public struct OutlineMeshEntry {public Mesh Source;public Mesh Shell;}
        public OutlineMeshEntry[] OutlineMeshes=System.Array.Empty<OutlineMeshEntry>();
        public Mesh GetShell(Mesh source)
        {for(int i=0;i<OutlineMeshes.Length;i++)if(OutlineMeshes[i].Source==source)return OutlineMeshes[i].Shell;return source;}
    }
}
