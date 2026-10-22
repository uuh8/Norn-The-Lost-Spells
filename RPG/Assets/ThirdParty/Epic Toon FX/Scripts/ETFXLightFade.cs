using UnityEngine;

namespace EpicToonFX
{
    public class ETFXLightFade : MonoBehaviour
    {
        public enum OnLifeEnd { DoNothing, Disable, Destroy }

        [Header("Seconds to dim the light")]
        public float life = 0.2f;
        public OnLifeEnd onLifeEnd = OnLifeEnd.Destroy;

        private Light li;
        private float initIntensity;

        // Use this for initialization
        private void Start()
        {
            li = GetComponent<Light>();
            if (li != null)
            {
                initIntensity = li.intensity;
            }
        }

        // Update is called once per frame
        private void Update()
        {
            if (li != null)
            {
                li.intensity -= initIntensity * (Time.deltaTime / life);
                if (li.intensity <= 0f)
                {
                    switch (onLifeEnd)
                    {
                        case OnLifeEnd.DoNothing:
                            // Do nothing
                            break;
                        case OnLifeEnd.Disable:
                            li.enabled = false;
                            break;
                        case OnLifeEnd.Destroy:
                            // URP 下 Light 必带 [RequireComponent] 的 UniversalAdditionalLightData，
                            // 直接 Destroy(li) 会被拒绝且每帧重试刷屏。先删依赖的附属数据，再删 Light，并停用本组件。
                            var addData = li.GetComponent("UniversalAdditionalLightData");
                            if (addData != null) Destroy(addData);
                            Destroy(li);
                            enabled = false;
                            break;
                    }
                }
            }
        }
    }
}