using System;
using System.Text;
using Game.NetworkProtocol;
using UnityEngine.Networking;

namespace Game.Networking
{
    /// <summary>实例控制Adapter，密钥与玩家Bearer分离；目前只允许可信描述中的loopback地址。</summary>
    public static class InstanceControlHttp
    {
        public static UnityWebRequest Create(InstanceLaunchDescriptor launch, string secret, string action, string json)
        {
            if (!BusinessHttpClient.IsAllowedBaseUrl(launch.BusinessBaseUrl) || !new Uri(launch.BusinessBaseUrl).IsLoopback || !Guid.TryParseExact(launch.InstanceId, "N", out _) ||
                (action != "ready" && action != "heartbeat" && action != "admit") || secret == null || secret.Length != 64)
                throw new ArgumentException("实例控制配置无效。");
            var request = new UnityWebRequest(new Uri(new Uri(launch.BusinessBaseUrl), "/api/instances/" + launch.InstanceId + "/" + action).AbsoluteUri, "POST");
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json)); request.downloadHandler = new DownloadHandlerBuffer(); request.timeout = 8;
            request.SetRequestHeader("Content-Type", "application/json"); request.SetRequestHeader("X-Norn-Instance-Key", secret); return request;
        }
    }
}
