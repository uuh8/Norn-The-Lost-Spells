using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Game.Networking;
using Game.NetworkProtocol;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.TestTools;

namespace Game.Multiplayer.Tests
{
    /// <summary>
    /// 显式Temp桥才启用的跨进程控制面检查。无Gameplay/JoinTicket玩家操作，只驱动正式投递Coroutine；
    /// 后端以真实Kestrel和隔离MySQL核实事务。实例key只驻本fixture私有输入，不输出到结果/断言。
    /// </summary>
    public sealed class RunCheckpointHttpRecoveryIntegrationTests
    {
        private const string RequestPath = "Temp/OnlineLab/checkpoint-http-client.request.json";
        [Serializable] private sealed class Request { public string Directory; }
        [Serializable] private sealed class Bridge { public SceneGrant[] Scenes; }
        [Serializable] private sealed class SceneGrant
        {
            public string Name, Secret, ProtectedSecret;
            public InstanceLaunchDescriptor Launch;
            public OnlineRunAssetCheckpoint Checkpoint;
            public ulong RetirementSequence;
            public uint CheckpointSchemaVersion;
        }
        [Serializable] private sealed class SceneResult
        {
            public string Name, BodySha256;
            public bool HasPending, DeliveryStopped;
            public ulong LastConfirmedSequence, LastAcknowledgedRetirementSequence;
            public int Attempts, PendingAttempts;
            public long[] PostStatuses, ReceiptStatuses, HeadStatuses;
        }
        [Serializable] private sealed class ClientResult
        { public bool Succeeded; public string FailureCode; public SceneResult[] Scenes; }

        [UnityTest]
        public IEnumerator FormalDeliveryUsesFixedBodyAndAcknowledgesOnlyThePersistedCompleteHead()
        {
            if (!File.Exists(RequestPath)) Assert.Ignore("需要主Agent显式启动隔离Kestrel并提供本轮私有Temp桥；未执行网络检查。");
            var request = JsonUtility.FromJson<Request>(File.ReadAllText(RequestPath));
            string directory = Path.GetFullPath(request.Directory);
            string allowed = Path.GetFullPath("Temp/OnlineLab/CheckpointHttpRecovery") + Path.DirectorySeparatorChar;
            Assert.That(directory.StartsWith(allowed, StringComparison.OrdinalIgnoreCase), Is.True, "仅允许本项目隔离Temp桥。");
            string input = Path.Combine(directory, "ready.json"), output = Path.Combine(directory, "unity-result.json");
            Assert.That(File.Exists(input), Is.True); Assert.That(File.Exists(output), Is.False, "旧结果不能作为新一轮执行。");
            Assert.That((DateTime.UtcNow - File.GetLastWriteTimeUtc(input)).TotalMinutes, Is.LessThan(3));
            var bridge = JsonUtility.FromJson<Bridge>(File.ReadAllText(input));
            Assert.That(bridge.Scenes, Has.Length.EqualTo(2));
            var results = new List<SceneResult>();
            var report = new ClientResult { FailureCode = "ClientAssertionOrTransportFailure" };
            try
            {
                foreach (var scene in bridge.Scenes)
                {
                    Assert.That(scene.Name == "Recovery" || scene.Name == "Bounded", Is.True);
                    Assert.That(string.IsNullOrEmpty(scene.Secret), Is.True, "磁盘桥不能包含明文实例key。");
                    scene.Secret = UnprotectSecret(scene.ProtectedSecret);
                    Assert.That(scene.Secret != null && scene.Secret.Length == 64, Is.True, "隔离桥凭据解密失败。");
                    Assert.That(InstanceControlHttp.SupportsCompleteCheckpoints(new InstanceReadyReply
                        { CheckpointSchemaVersion = scene.CheckpointSchemaVersion }), Is.True);
                    // 入口DTO经真实JsonUtility后，复用正式控制面映射归一化可选null壳；不猜造任何属性。
                    foreach (var member in scene.Checkpoint.Members)
                        member.Assets = OnlineOwnedAssetMapping.ToShared(OnlineOwnedAssetMapping.ToCore(member.Assets));
                    var host = new GameObject("Isolated checkpoint HTTP delivery"); host.SetActive(false);
                    var bootstrap = host.AddComponent<RoomInstanceBootstrap>();
                    var posts = new List<long>(); var receipts = new List<long>(); var heads = new List<long>();
                    try
                    {
                        Set(bootstrap, "_launch", scene.Launch); Set(bootstrap, "_secret", scene.Secret);
                        Set(bootstrap, "_checkpointProviderSupported", true);
                        typeof(RoomInstanceBootstrap).GetProperty("Ready").SetValue(bootstrap, true);
                        Invoke(bootstrap, "StartCheckpointPublisher");
                        var pending = Get<RunCheckpointPending>(bootstrap, "_checkpointPending");
                        var captured = new CapturedRunCheckpoint { Value = scene.Checkpoint, RetirementSequence = scene.RetirementSequence };
                        Assert.That(pending.TryPrepare(captured, out _), Is.True);
                        string body = pending.Body, hash = Digest(Encoding.UTF8.GetBytes(body));
                        Assert.That(scene.RetirementSequence, Is.EqualTo(17));
                        // 后续帧/源DTO变化不能改写原候选，确认时也不能误ACK捕获后新增的退役水位。
                        scene.Checkpoint.Members[0].Wands[0].CurrentMana = 9.875f; captured.RetirementSequence = 999;
                        for (int round = 1; round <= 3; round++)
                        {
                            if (scene.Name == "Bounded") Set(bootstrap, "_secret", round == 1 ? DifferentSecret(scene.Secret) : scene.Secret);
                            Assert.That(pending.Body, Is.EqualTo(body));
                            var delivery = (IEnumerator)Invoke(bootstrap, "DeliverCheckpointOnce");
                            try
                            {
                                while (delivery.MoveNext())
                                {
                                    var operation = delivery.Current as UnityWebRequestAsyncOperation;
                                    Assert.That(operation, Is.Not.Null, "正式投递只等待真实HTTP操作。");
                                    double deadline = UnityEditor.EditorApplication.timeSinceStartup + 12;
                                    while (!operation.isDone)
                                    {
                                        Assert.That(UnityEditor.EditorApplication.timeSinceStartup, Is.LessThan(deadline), "有界等待HTTP，不取消用户进程。");
                                        yield return null;
                                    }
                                    var wire = Get<UnityWebRequest>(bootstrap, "_checkpointRequest");
                                    Assert.That(wire, Is.Not.Null);
                                    string path = new Uri(wire.url).AbsolutePath;
                                    if (wire.method == "POST")
                                    {
                                        posts.Add(wire.responseCode);
                                        Assert.That(Digest(wire.uploadHandler.data), Is.EqualTo(hash), "所有真实重试必须保持同一正文。");
                                    }
                                    else if (path.EndsWith("/head", StringComparison.Ordinal)) heads.Add(wire.responseCode);
                                    else receipts.Add(wire.responseCode);
                                }
                            }
                            finally { (delivery as IDisposable)?.Dispose(); }
                            if (scene.Name == "Bounded" || round < 3)
                            {
                                Assert.That(pending.HasPending, Is.True);
                                Assert.That(pending.Body, Is.EqualTo(body));
                                Assert.That(pending.LastConfirmedSequence, Is.Zero);
                                Assert.That(pending.LastAcknowledgedRetirementSequence, Is.Zero);
                            }
                        }
                        bool bounded = scene.Name == "Bounded";
                        Assert.That(pending.HasPending, Is.EqualTo(bounded));
                        Assert.That(pending.LastConfirmedSequence, Is.EqualTo(bounded ? 0UL : 1UL));
                        Assert.That(pending.LastAcknowledgedRetirementSequence, Is.EqualTo(bounded ? 0UL : 17UL));
                        Assert.That(Get<bool>(bootstrap, "_checkpointDeliveryStopped"), Is.EqualTo(bounded));
                        Assert.That(posts.Count, Is.EqualTo(3));
                        if (bounded)
                        {
                            Assert.That(posts, Is.EqualTo(new long[] { 401, 503, 200 }));
                            Assert.That(receipts, Is.EqualTo(new long[] { 401, 404 }));
                            Assert.That(heads, Is.EqualTo(new long[] { 200 }));
                        }
                        else
                        {
                            Assert.That(posts[0], Is.LessThanOrEqualTo(0)); Assert.That(posts[1], Is.LessThanOrEqualTo(0));
                            Assert.That(posts[2], Is.EqualTo(200));
                            Assert.That(receipts, Is.EqualTo(new long[] { 200, 503 }));
                            Assert.That(heads, Is.EqualTo(new long[] { 200, 200 }));
                        }
                        results.Add(new SceneResult { Name = scene.Name, BodySha256 = hash, Attempts = 3,
                            PendingAttempts = Get<int>(bootstrap, "_checkpointAttempts"), HasPending = pending.HasPending,
                            DeliveryStopped = Get<bool>(bootstrap, "_checkpointDeliveryStopped"),
                            LastConfirmedSequence = pending.LastConfirmedSequence,
                            LastAcknowledgedRetirementSequence = pending.LastAcknowledgedRetirementSequence,
                            PostStatuses = posts.ToArray(), ReceiptStatuses = receipts.ToArray(), HeadStatuses = heads.ToArray() });
                    }
                    finally
                    {
                        Invoke(bootstrap, "StopCheckpointPublisher");
                        UnityEngine.Object.DestroyImmediate(host);
                    }
                }
                report.Succeeded = true; report.FailureCode = null;
            }
            finally
            {
                report.Scenes = results.ToArray();
                string temporary = output + ".tmp";
                File.WriteAllText(temporary, JsonUtility.ToJson(report, true)); File.Move(temporary, output);
            }
        }

        private static string Digest(byte[] bytes)
        { using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        private static string DifferentSecret(string value) => (value[0] == '0' ? "1" : "0") + value.Substring(1);
        private static T Get<T>(object target, string name)
            => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
        private static void Set(object target, string name, object value)
            => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
        private static object Invoke(object target, string name)
            => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, null);

        // 只用于本轮测试进程之间的临时桥，不是Gameplay加密协议。CurrentUser DPAPI无需修改目录ACL，
        // UI_FORBIDDEN确保不会弹系统对话框；明文只在内存给正式HTTP Adapter，结果文件无key。
        [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
        [DllImport("crypt32.dll", SetLastError = true)]
        private static extern bool CryptUnprotectData(ref Blob input, out IntPtr description, IntPtr entropy,
            IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
        private static string UnprotectSecret(string encoded)
        {
            byte[] cipher = Convert.FromBase64String(encoded ?? string.Empty);
            Assert.That(cipher.Length > 0 && cipher.Length <= 4096, Is.True, "隔离桥密文格式无效。");
            var input = new Blob { Length = cipher.Length, Data = Marshal.AllocHGlobal(cipher.Length) };
            Blob output = default; IntPtr description = IntPtr.Zero;
            try
            {
                Marshal.Copy(cipher, 0, input.Data, cipher.Length);
                if (!CryptUnprotectData(ref input, out description, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output))
                    throw new InvalidOperationException("PrivateBridgeDecryptionRejected");
                byte[] plain = new byte[output.Length]; Marshal.Copy(output.Data, plain, 0, plain.Length);
                string secret = Encoding.UTF8.GetString(plain); Array.Clear(plain, 0, plain.Length);
                Marshal.Copy(plain, 0, output.Data, plain.Length); return secret;
            }
            finally
            {
                Marshal.FreeHGlobal(input.Data);
                if (output.Data != IntPtr.Zero) LocalFree(output.Data);
                if (description != IntPtr.Zero) LocalFree(description);
            }
        }
    }
}
