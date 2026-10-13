using UnityEngine;

namespace Game.Run
{
    public enum HubStationKind : byte { Warehouse, Shop, Expedition }
    /// <summary>固定据点空间配置；交互点/范围属于SO，不让UI按钮决定玩家可从何处操作。</summary>
    [CreateAssetMenu(menuName="Norn/Single Player/Hub")]
    public sealed class HubDefinition : ScriptableObject
    {
        [System.Serializable]public struct Station{public HubStationKind Kind;public Vector3 Position;public string DisplayName;}
        public Station[] Stations;
        public float InteractionRadius=2;
        public Vector3 ExperimentCenter=new Vector3(0,0,0),ExperimentSize=new Vector3(6,6,6);
        public bool ContainsExperiment(Vector3 point)
        {
            if(!Finite(point) || !Finite(ExperimentCenter) || !Finite(ExperimentSize) || ExperimentSize.x<=0 || ExperimentSize.y<=0 || ExperimentSize.z<=0)return false;
            // 上界半开，两个相邻区域不会同时拥有同一个边界；配置尺寸是完整边长。
            Vector3 delta=point-ExperimentCenter,half=ExperimentSize*.5f;
            return delta.x>=-half.x && delta.x<half.x && delta.y>=-half.y && delta.y<half.y && delta.z>=-half.z && delta.z<half.z;
        }
        public bool TryFindNearest(Vector3 point,out Station station)
        {
            station=default;if(Stations==null || !Finite(point) || float.IsNaN(InteractionRadius) || float.IsInfinity(InteractionRadius) || InteractionRadius<=0)return false;
            float best=InteractionRadius*InteractionRadius;bool found=false;
            for(int i=0;i<Stations.Length;i++)
            {if(!Finite(Stations[i].Position))continue;float distance=(Stations[i].Position-point).sqrMagnitude;
                if(distance>best || found && distance==best)continue;best=distance;station=Stations[i];found=true;}
            return found;
        }
        private static bool Finite(Vector3 p)=>!float.IsNaN(p.x)&&!float.IsInfinity(p.x)&&!float.IsNaN(p.y)&&!float.IsInfinity(p.y)&&!float.IsNaN(p.z)&&!float.IsInfinity(p.z);
    }
}
