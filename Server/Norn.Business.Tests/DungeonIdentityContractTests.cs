using System.Reflection;
using Game.NetworkProtocol;
using Google.Protobuf;
using Xunit;

namespace Norn.Business.Tests;

/// <summary>
/// 不连接数据库的协议契约回归。先验证“身份字段存在且经过同一验证入口”，
/// 再由真实 MySQL 测试证明字段确实来自 Party，而不是测试自行填写就算授权成功。
/// </summary>
public sealed class DungeonIdentityContractTests
{
    [Fact]
    public void ApplicationProtocolRejectsVersionWithoutTrustedDungeon()
    {
        // 新字段在 Protobuf wire 上可兼容，语义却是必需的；旧 Client 会忽略未知字段，必须拒绝旧应用版本。
        Assert.Equal(2u, ProtocolCompatibility.CurrentVersion);
        Assert.False(ProtocolCompatibility.IsSupported(1));
        Assert.True(ProtocolCompatibility.IsSupported(2));
    }

    [Theory]
    [InlineData(typeof(InstanceLaunchDescriptor))]
    [InlineData(typeof(InstanceReadyRequest))]
    [InlineData(typeof(JoinTicketReply))]
    [InlineData(typeof(ConsumeJoinTicketReply))]
    public void EveryControlBoundaryCarriesDungeonIdentity(Type type)
    {
        var field = type.GetField("DungeonId");
        Assert.NotNull(field);
        Assert.Equal(typeof(string), field.FieldType);
        Assert.Equal("", field.GetValue(Activator.CreateInstance(type)));
    }

    [Fact]
    public void JoinReplyPreservesAppendedDungeonField()
    {
        var field = JoinTicketReplyMessage.Descriptor.FindFieldByName("dungeon_id");
        Assert.NotNull(field);
        Assert.Equal(9, field.FieldNumber);
        var reply = new JoinTicketReplyMessage { ProtocolVersion = ProtocolCompatibility.CurrentVersion, ActorId = 1 };
        field.Accessor.SetValue(reply, "p8");
        var copy = JoinTicketReplyMessage.Parser.ParseFrom(reply.ToByteArray());
        Assert.Equal("p8", field.Accessor.GetValue(copy));
    }

    [Theory]
    [InlineData("p7", "p7", true)]
    [InlineData("p8", "p8", true)]
    [InlineData("p7", "p8", false)]
    [InlineData("p7", "", false)]
    [InlineData("", "", false)]
    [InlineData("p7", "P7", false)]
    [InlineData("../p7", "../p7", false)]
    public void DungeonMatchRequiresNonemptyStableIdentifier(string expected, string actual, bool accepted)
    {
        var rules = typeof(ProtocolCompatibility).Assembly.GetType("Game.NetworkProtocol.DungeonIdentityRules");
        Assert.NotNull(rules);
        Assert.Equal(accepted, rules.GetMethod("Matches", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [expected, actual]));
    }
}
