using Game.NetworkProtocol;

namespace Game.Networking
{
    /// <summary>
    /// 入场握手的纯验证入口。这里只判断身份是否与已经可信的上下文一致，不读网络、不改 Epoch，
    /// Authenticator 必须在这些检查成功后才替换连接或调用 Mirror 的 Accept。
    /// </summary>
    public static class JoinAdmissionRules
    {
        /// <summary>
        /// 业务兑换回复必须属于当前 DS 的 Run/Room/Dungeon。尤其先比较 Dungeon，再调用 TryAdvance：
        /// 否则一次错误地图回复也会消耗 Epoch，使随后正确回复被误当作过期连接。
        /// </summary>
        public static bool AcceptsServerReply(ConsumeJoinTicketReply reply, InstanceLaunchDescriptor launch, string expectedPlayer)
        {
            return reply != null && launch != null && !string.IsNullOrEmpty(expectedPlayer) &&
                !string.IsNullOrEmpty(launch.RoomId) && !string.IsNullOrEmpty(launch.RunId) &&
                DungeonIdentityRules.Matches(launch.DungeonId, reply.DungeonId) &&
                reply.PlayerId == expectedPlayer && reply.RoomId == launch.RoomId && reply.RunId == launch.RunId &&
                reply.RoomEpoch == launch.RoomEpoch && reply.ActorId >= 1 && reply.ActorId <= 4 && reply.ConnectionEpoch != 0;
        }

        /// <summary>
        /// Client 的期望来自业务 Ticket，而不是本地门名；确认同时匹配 nonce 与应用版本，
        /// 防止旧握手、旧应用语义或另一张地图的确认建立可操作 Identity。
        /// </summary>
        public static bool AcceptsClientReply(JoinTicketReplyMessage reply, JoinTicketReply expected, ulong nonce)
        {
            return reply != null && expected != null && nonce != 0 && !string.IsNullOrEmpty(expected.PlayerId) &&
                !string.IsNullOrEmpty(expected.RoomId) && !string.IsNullOrEmpty(expected.RunId) && ProtocolCompatibility.IsSupported(reply.ProtocolVersion) &&
                reply.Nonce == nonce && DungeonIdentityRules.Matches(expected.DungeonId, reply.DungeonId) &&
                reply.PlayerId == expected.PlayerId && reply.RoomId == expected.RoomId && reply.RunId == expected.RunId &&
                reply.RoomEpoch == expected.RoomEpoch && reply.ActorId >= 1 && reply.ActorId <= 4 && reply.ConnectionEpoch != 0;
        }
    }
}
