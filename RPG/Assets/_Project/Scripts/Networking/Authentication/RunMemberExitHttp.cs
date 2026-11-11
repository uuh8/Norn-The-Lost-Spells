using System;
using System.Globalization;
using System.Text;
using Game.NetworkProtocol;
using UnityEngine.Networking;

namespace Game.Networking
{
    /// <summary>
    /// DS个人最终退出的实例控制HTTP适配。只创建请求，不自动发送、不持有重试或修改固定候选正文。
    /// 与玩家Bearer分离；实例密钥只能发往可信描述中的loopback控制端点，不向UI/日志返回它。
    /// </summary>
    public static class RunMemberExitHttp
    {
        public static UnityWebRequest Create(InstanceLaunchDescriptor launch, string secret, string json)
        {
            if (string.IsNullOrEmpty(json) || Encoding.UTF8.GetByteCount(json) > OnlineRunMemberExitRequest.MaximumPayloadBytes)
                throw new ArgumentException("个人退出正文为空或超出既有控制面预算。");
            var request = Request(launch, secret, "member-exits", "POST");
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            request.SetRequestHeader("Content-Type", "application/json"); return request;
        }

        public static UnityWebRequest Query(InstanceLaunchDescriptor launch, string secret, string settlementId)
        {
            if (!Guid.TryParseExact(settlementId, "N", out _)) throw new ArgumentException("个人退出查询身份无效。");
            return Request(launch, secret, "member-exits/" + settlementId, "GET");
        }

        private static UnityWebRequest Request(InstanceLaunchDescriptor launch, string secret, string suffix, string method)
        {
            if (launch == null || launch.RoomEpoch == 0 || !BusinessHttpClient.IsAllowedBaseUrl(launch.BusinessBaseUrl)
                || !Uri.TryCreate(launch.BusinessBaseUrl, UriKind.Absolute, out var origin) || !origin.IsLoopback
                || !Guid.TryParseExact(launch.InstanceId, "N", out _) || secret == null || secret.Length != 64)
                throw new ArgumentException("个人退出实例控制配置无效。");
            var endpoint = new Uri(origin, "/api/instances/" + launch.InstanceId + "/" + suffix
                + "?roomEpoch=" + launch.RoomEpoch.ToString(CultureInfo.InvariantCulture));
            var request = new UnityWebRequest(endpoint.AbsoluteUri, method)
            {
                downloadHandler = new DownloadHandlerBuffer(), timeout = 8,
                // 初始URI虽是loopback，也不能让重定向把实例secret送往另一个Origin。
                redirectLimit = 0
            };
            request.SetRequestHeader("X-Norn-Instance-Key", secret); return request;
        }
    }
}
