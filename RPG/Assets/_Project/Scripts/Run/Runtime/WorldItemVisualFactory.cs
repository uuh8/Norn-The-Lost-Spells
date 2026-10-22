using UnityEngine;

namespace Game.Run
{
    /// <summary>模型只提供视觉，唯一实例由WorldItemPickup绑定；不修改源美术Prefab。</summary>
    internal static class WorldItemVisualFactory
    {
        internal static GameObject Create(ItemDefinition definition, WorldItemSpawnPoint source = null, HubWorldItemRecord record = null)
        {
            var root=new GameObject("OwnedPickup");root.SetActive(false);
            GameObject prefab=definition!=null?(definition.PickupPrefab!=null?definition.PickupPrefab:definition.WorldModel):null;
            bool copyAuthor=source!=null && source.HasVisualModel;
            root.transform.rotation=source!=null?source.transform.rotation:definition!=null && definition.PickupPrefab!=null?definition.PickupPrefab.transform.localRotation:Quaternion.identity;
            if(copyAuthor || prefab!=null)
            {
                var visual=copyAuthor?source.CreateVisualCopy(root.transform):Object.Instantiate(prefab,root.transform,false);visual.name="Visual";
                // OwnedPickup负责世界姿态，Visual保留作者模型内部变换和Scale；不能把包装根Rotation重复乘两遍。
                if(!copyAuthor)
                {
                    if(definition.PickupPrefab!=null){visual.transform.localPosition=Vector3.zero;visual.transform.localRotation=Quaternion.identity;}
                    if(record!=null && record.HasVisualScaleOverride)visual.transform.localScale=Vector3.Scale(visual.transform.localScale,record.VisualScaleRatio);
                }
                // 复制包装只为取得视觉配置。先禁用再移除投放脚本；Destroy延迟到帧末，期间不能递归Start发放。
                foreach(var point in visual.GetComponentsInChildren<WorldItemSpawnPoint>(true))
                {point.enabled=false;if(Application.isPlaying)Object.Destroy(point);else Object.DestroyImmediate(point);}
                foreach(var pickup in visual.GetComponentsInChildren<WorldItemPickup>(true))
                {pickup.enabled=false;if(Application.isPlaying)Object.Destroy(pickup);else Object.DestroyImmediate(pickup);}
                // Rewards/Spell原包装携带旧自动发奖脚本，纯视觉复制体必须剥离，防止一份地面物重复授予。
                foreach(var reward in visual.GetComponentsInChildren<SpellRewardPickup>(true))
                {reward.enabled=false;if(Application.isPlaying)Object.Destroy(reward);else Object.DestroyImmediate(reward);}
                foreach(var reward in visual.GetComponentsInChildren<DungeonFinalRewardPickup>(true))
                {reward.enabled=false;if(Application.isPlaying)Object.Destroy(reward);else Object.DestroyImmediate(reward);}
                // 静态拾取展示不增加飞行；容器G投掷仍由原模块处理。
                foreach(var body in visual.GetComponentsInChildren<Rigidbody>(true)){body.isKinematic=true;body.useGravity=false;}
                foreach(var collider in visual.GetComponentsInChildren<Collider>(true))collider.enabled=false;
                if(definition.Kind==ItemKind.Spell)
                    WorldSpellIconPresentation.Apply(visual,definition.Icon!=null?definition.Icon:definition.SpellDefinition?.Icon);
            }
            else
            {
                var visual=GameObject.CreatePrimitive(PrimitiveType.Cube);visual.name="Fallback";visual.transform.SetParent(root.transform,false);
                visual.transform.localScale=Vector3.one*.4f;visual.GetComponent<Collider>().enabled=false;
            }
            root.AddComponent<WorldItemPickup>();return root;
        }
    }
}
