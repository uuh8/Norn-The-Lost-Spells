using System;
using System.Text;
using UnityEngine.Networking;

namespace Game.Networking
{
    /// <summary>业务 HTTP Adapter；与 Mirror/KCP 连接无关，不保存身份或操作本地资产。</summary>
    public static class BusinessHttpClient
    {
        public static bool IsAllowedBaseUrl(string value)
        {
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) && string.IsNullOrEmpty(uri.UserInfo) &&
                string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) && uri.AbsolutePath == "/" &&
                (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
        }

        public static UnityWebRequest Create(string baseUrl, string method, string path, string json, string token, string manifest)
        {
            if (!IsAllowedBaseUrl(baseUrl) || !path.StartsWith("/api/online/", StringComparison.Ordinal)) throw new ArgumentException("业务地址必须是 HTTPS；开发 HTTP 仅允许 loopback。");
            var request = new UnityWebRequest(new Uri(new Uri(baseUrl), path).AbsoluteUri, method);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.timeout = 8;
            if (json != null)
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                request.SetRequestHeader("Content-Type", "application/json");
            }
            if (!string.IsNullOrEmpty(token)) request.SetRequestHeader("Authorization", "Bearer " + token);
            request.SetRequestHeader("X-Norn-Protocol", "1");
            request.SetRequestHeader("X-Norn-Manifest", manifest);
            return request;
        }
    }
}
