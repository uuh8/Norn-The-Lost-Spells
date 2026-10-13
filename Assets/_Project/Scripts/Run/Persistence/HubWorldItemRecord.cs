using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Run
{
    // 缺省0兼容旧Schema的悬空姿态；新投放保留作者摆放，新Drop保存经验证的地面姿态。
    public enum WorldItemPoseKind : byte { Legacy=0, Authored=1, Grounded=2 }
    /// <summary>据点生成账与地面资产坐标；出售后的记录仍保留，不能因物品已移除再次发放。</summary>
    [Serializable]
    public sealed class HubWorldItemRecord
    {
        public string SceneKey,WorldSceneKey,SpawnId,InstanceId;
        public Vector3 Position;
        public Quaternion Rotation=Quaternion.identity;
        public WorldItemPoseKind PoseKind;
        public bool HasVisualScaleOverride;
        public Vector3 VisualScaleRatio=Vector3.one;
        public HubWorldItemRecord Copy()=>new HubWorldItemRecord
        {SceneKey=SceneKey,WorldSceneKey=WorldSceneKey,SpawnId=SpawnId,InstanceId=InstanceId,Position=Position,Rotation=Rotation,
            PoseKind=PoseKind,HasVisualScaleOverride=HasVisualScaleOverride,VisualScaleRatio=VisualScaleRatio};
        internal static HubWorldItemRecord[] CopyAll(IReadOnlyList<HubWorldItemRecord> records)
        {var copy=new HubWorldItemRecord[records.Count];for(int i=0;i<copy.Length;i++)copy[i]=records[i].Copy();return copy;}
        internal static bool IsValid(IReadOnlyList<HubWorldItemRecord> records)
        {
            if(records==null)return true;
            var ids=new HashSet<string>(StringComparer.Ordinal);var spawns=new HashSet<(string,string)>();
            for(int i=0;i<records.Count;i++)
            {
                var r=records[i];
                if(r==null || string.IsNullOrWhiteSpace(r.SceneKey) || string.IsNullOrWhiteSpace(r.InstanceId) || !ids.Add(r.InstanceId)
                    || !Finite(r.Position.x) || !Finite(r.Position.y) || !Finite(r.Position.z)
                    || !Finite(r.Rotation.x) || !Finite(r.Rotation.y) || !Finite(r.Rotation.z) || !Finite(r.Rotation.w)
                    || Mathf.Abs(Quaternion.Dot(r.Rotation,r.Rotation)-1)>.01f || r.PoseKind>WorldItemPoseKind.Grounded
                    || r.HasVisualScaleOverride && (!ValidScale(r.VisualScaleRatio.x) || !ValidScale(r.VisualScaleRatio.y) || !ValidScale(r.VisualScaleRatio.z))
                    || !string.IsNullOrEmpty(r.SpawnId) && (!spawns.Add((r.SceneKey,r.SpawnId)) || string.IsNullOrWhiteSpace(r.SpawnId)))return false;
            }
            return true;
        }
        private static bool Finite(float value)=>!float.IsNaN(value) && !float.IsInfinity(value);
        private static bool ValidScale(float value)=>Finite(value) && Mathf.Abs(value)>.00001f;
    }
}
