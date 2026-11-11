using System;
using System.Text;
using Game.NetworkProtocol;
using UnityEngine.Networking;

namespace Game.Networking
{
    /// <summary>实例控制Adapter，密钥与玩家Bearer分离；目前只允许可信描述中的loopback地址。</summary>
    public static class InstanceControlHttp
    {
        /// <summary>此能力只控制新检查点功能启用，不改变旧房间Ready或授予新的玩法写权限。</summary>
        public static bool SupportsCompleteCheckpoints(InstanceReadyReply reply)
            => reply != null && reply.CheckpointSchemaVersion == OnlineRunAssetCheckpoint.CompleteSchemaVersion;
        /// <summary>沿原实例凭据提交完整事实；只创建请求，网络执行由Bootstrap生命周期统一管理。</summary>
        public static UnityWebRequest CreateCheckpoint(InstanceLaunchDescriptor launch, string secret, string json)
            => CreateCheckpointCore(launch, secret, json, false);
        public static UnityWebRequest CreateTerminalCheckpoint(InstanceLaunchDescriptor launch, string secret, string json)
            => CreateCheckpointCore(launch, secret, json, true);
        private static UnityWebRequest CreateCheckpointCore(InstanceLaunchDescriptor launch, string secret, string json, bool terminal)
        {
            if (string.IsNullOrEmpty(json) || Encoding.UTF8.GetByteCount(json) > InstanceCheckpointJson.MaximumUtf8Bytes)
                throw new ArgumentException("实例检查点正文无效。");
            var request = CheckpointRequest(launch, secret, terminal ? "terminal-checkpoints" : "checkpoints", "POST");
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            request.SetRequestHeader("Content-Type", "application/json"); return request;
        }
        public static UnityWebRequest QueryCheckpoint(InstanceLaunchDescriptor launch, string secret, string checkpoint)
        {
            if (!Guid.TryParseExact(checkpoint, "N", out _)) throw new ArgumentException("检查点身份无效。");
            return CheckpointRequest(launch, secret, "checkpoints/" + checkpoint, "GET");
        }
        public static UnityWebRequest ReadCheckpointHead(InstanceLaunchDescriptor launch, string secret)
            => CheckpointRequest(launch, secret, "checkpoints/head", "GET");
        public static UnityWebRequest Complete(InstanceLaunchDescriptor launch, string secret, string json)
        {
            if (string.IsNullOrEmpty(json) || Encoding.UTF8.GetByteCount(json) > 4096)
                throw new ArgumentException("实例完成正文无效。");
            var request = CheckpointRequest(launch, secret, "complete", "POST");
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            request.SetRequestHeader("Content-Type", "application/json"); return request;
        }

        private static UnityWebRequest CheckpointRequest(InstanceLaunchDescriptor launch, string secret, string suffix, string method)
        {
            if (launch == null || launch.RoomEpoch == 0 || !BusinessHttpClient.IsAllowedBaseUrl(launch.BusinessBaseUrl)
                || !Uri.TryCreate(launch.BusinessBaseUrl, UriKind.Absolute, out var origin) || !origin.IsLoopback
                || !Guid.TryParseExact(launch.InstanceId, "N", out _) || secret == null || secret.Length != 64)
                throw new ArgumentException("实例控制配置无效。");
            // 固定后缀只由本类生成，查询值只来自可信描述。不能借普通HTTPS能力把实例secret发往远端。
            var endpoint = new Uri(origin, "/api/instances/" + launch.InstanceId + "/" + suffix
                + "?roomEpoch=" + launch.RoomEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var request = new UnityWebRequest(endpoint.AbsoluteUri, method)
                { downloadHandler = new DownloadHandlerBuffer(), timeout = 8 };
            request.SetRequestHeader("X-Norn-Instance-Key", secret); return request;
        }

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
