using System;
using System.IO;
using System.Reflection;
using Game.Core;
using Game.NetworkProtocol;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using UnityEditor;
using UnityEngine;

namespace Game.Project.Editor
{
    /// <summary>仅供 Editor 验证共享消息包，不启动服务器，也不修改正式场景。</summary>
    public static class OnlineFoundationTools
    {
        [MenuItem("Tools/Norn/Online/Verify Shared Protocol")]
        public static void VerifySharedProtocol()
        {
            // MenuItem 是一次人工触发的 Editor 操作，不进入客户端 Update，也不向远端发包。
            // 先确认真正加载的 Runtime，再测消息，避免“引用对了源码、运行时却用了另一份 DLL”。
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            Struct dependencyLock = JsonParser.Default.Parse<Struct>(File.ReadAllText(Path.Combine(projectRoot, "Tools", "Networking", "dependency-lock.json")));
            string expectedProtobufVersion = dependencyLock.Fields["protobuf"].StructValue.Fields["version"].StringValue + ".0";
            string actualProtobufVersion = typeof(IMessage).Assembly.GetName().Version.ToString();
            // AI Assistant 内置旧的可覆盖 DLL；不能因简单消息偶然可用就接受错误 Runtime。
            if (actualProtobufVersion != expectedProtobufVersion)
                throw new InvalidOperationException("Unity 实际加载的 Protobuf 版本不符合 dependency-lock：" + actualProtobufVersion);

            // 同一个消息用二进制和 ProtoJSON 分别往返；最大 uint64 可暴露错误的 JSON 数值转换。
            ProtocolPingRequest request = new ProtocolPingRequest
            {
                ProtocolVersion = ProtocolCompatibility.CurrentVersion,
                Sequence = ulong.MaxValue,
                Message = "你好，Norn 👋"
            };
            ProtocolPingRequest decoded = ProtocolPingRequest.Parser.ParseFrom(request.ToByteArray());
            if (decoded.ProtocolVersion != request.ProtocolVersion || decoded.Sequence != request.Sequence || decoded.Message != request.Message)
                throw new InvalidOperationException("Unity 内的 Protobuf 二进制往返失败。");

            string json = JsonFormatter.Default.Format(request);
            ProtocolPingRequest fromJson = JsonParser.Default.Parse<ProtocolPingRequest>(json);
            if (!fromJson.Equals(request) || ProtocolCompatibility.IsSupported(0))
                throw new InvalidOperationException("ProtoJSON 或协议版本检查失败。");

            // 同源纯契约程序集不应引用 UnityEngine，避免业务服间接依赖 Unity Runtime。
            Assembly protocolAssembly = typeof(ProtocolPingRequest).Assembly;
            foreach (AssemblyName reference in protocolAssembly.GetReferencedAssemblies())
            {
                if (reference.Name != null && reference.Name.StartsWith("UnityEngine", StringComparison.Ordinal))
                    throw new InvalidOperationException("协议程序集出现 UnityEngine 依赖。");
            }

            string outputRoot = Path.Combine(projectRoot, "Tools", "Networking", ".artifacts");
            Directory.CreateDirectory(outputRoot);
            ProtocolEvidence evidence = new ProtocolEvidence
            {
                Status = "passed",
                UnityVersion = Application.unityVersion,
                ProtocolAssembly = protocolAssembly.GetName().Name,
                ProtobufAssemblyVersion = actualProtobufVersion,
                ProtocolVersion = ProtocolCompatibility.CurrentVersion,
                BinaryRoundTrip = true,
                ProtoJsonRoundTrip = true,
                HasUnityEngineReference = false,
                // 此工具验证的是 Editor 内协议包，不能据此声称副本服或多人已通过。
                DedicatedServerBuildValidated = false,
                KcpMultiplayerValidated = false,
                RecordedAtUtc = DateTime.UtcNow.ToString("O")
            };
            File.WriteAllText(Path.Combine(outputRoot, "unity-protocol-report.json"), JsonUtility.ToJson(evidence, true));
            GameLog.Info("共享协议包验证通过：二进制、ProtoJSON 与纯程序集边界。", "OnlineFoundation");
        }

        [Serializable]
        // 仅是供人审阅的验证报告，使用 Unity JsonUtility 写文件，不属于线上通信 Schema。
        private sealed class ProtocolEvidence
        {
            public string Status;
            public string UnityVersion;
            public string ProtocolAssembly;
            public string ProtobufAssemblyVersion;
            public uint ProtocolVersion;
            public bool BinaryRoundTrip;
            public bool ProtoJsonRoundTrip;
            public bool HasUnityEngineReference;
            public bool DedicatedServerBuildValidated;
            public bool KcpMultiplayerValidated;
            public string RecordedAtUtc;
        }
    }
}
