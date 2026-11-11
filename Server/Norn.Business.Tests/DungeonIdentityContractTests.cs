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
        // v4要求订阅生命周期与代次栅栏；v3虽有世界握手，却会忽略订阅并允许页自行填缓存，必须明确拒绝。
        // v5 的真实携带/组分不能让 v4 忽略后当作空瓶；旧版门禁仍须明确拒绝。
        // v6增加权威容器工具选择；旧v5忽略新选择会继续把工具操作当施杖，必须拒绝混用。
        // v7增加共享地面；v8的G在途与Waiting事实不能让旧端忽略后仍操作库存。
        // v9 显式携带真实固体数量并新增 K 权威意图；旧 v8 不具备该状态，必须拒绝混用。
        // v10保存具体卡供料设置与材料输出表现；v16新增DS裁决毒爆表现事实。
        // 版本号提升必须覆盖所有旧版本（包括proto缺省0），不能只检查某次历史升级的相邻值。
        Assert.Equal(16u, ProtocolCompatibility.CurrentVersion);
        for (uint legacy = 0; legacy < ProtocolCompatibility.CurrentVersion; legacy++)
            Assert.False(ProtocolCompatibility.IsSupported(legacy));
        Assert.False(ProtocolCompatibility.IsSupported(ProtocolCompatibility.CurrentVersion + 1));
        Assert.True(ProtocolCompatibility.IsSupported(ProtocolCompatibility.CurrentVersion));
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
