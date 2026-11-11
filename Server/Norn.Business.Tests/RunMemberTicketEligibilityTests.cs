using Dapper;
using Game.NetworkProtocol;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Norn.Business.Features.Rooms;
using Norn.Business.Infrastructure;
using Xunit;

namespace Norn.Business.Tests;

/// <summary>
/// 签票必须与兑换使用同一Run成员/租约资格，Party里仍有席位不等于能重新取得已失效的资产权。
/// 只修改本case新建Actor的记录，不使用尚未迁移的Exited值，不启动Unity DS或改变队友资格。
/// </summary>
public sealed class RunMemberTicketEligibilityTests
{
    [Theory, InlineData("missing-member"), InlineData("rolled-back"), InlineData("missing-lease")]
    [Trait("Category", "DatabaseIntegration")]
    public async Task IneligibleMemberCannotMintWhileOtherPlayerKeepsOriginalAdmission(string defect)
    {
        await using var f = await RunAssetCheckpointServiceTests.Fixture.Create();
        var instances = f.Factory.Services.GetRequiredService<RoomInstanceService>();
        var target = f.Ready.Members.Single(m => m.ActorId == 1);
        var other = f.Ready.Members.Single(m => m.ActorId == 2);
        f.Factory.Clock.Advance(TimeSpan.FromSeconds(6)); // 单独排除原5秒限频，拒绝必须来自资格门。
        await using var db = new MySqlConnection(f.Factory.Connection);
        string sql = defect switch
        {
            "missing-member" => "DELETE FROM RunMembers WHERE RunId=@Run AND CharacterId=@Player",
            "rolled-back" => "UPDATE RunMembers SET State='RolledBack' WHERE RunId=@Run AND CharacterId=@Player",
            "missing-lease" => "DELETE FROM AssetLeases WHERE RunId=@Run AND RoomEpoch=@Epoch AND CharacterId=@Player",
            _ => throw new ArgumentOutOfRangeException(nameof(defect))
        };
        Assert.Equal(1, await db.ExecuteAsync(sql, new
        { Run = f.Grant.Descriptor.RunId, Player = target.Profile.PlayerId, Epoch = f.Grant.Descriptor.RoomEpoch }));

        // 先走队友的真实签发/兑换；不能通过清整个Run或停实例来伪造本人被拒。
        var otherTicket = await instances.MintTicketAsync(other.Profile.PlayerId, f.Grant.Descriptor.RoomId, default);
        var admitted = await instances.ConsumeAsync(f.Grant.Descriptor.InstanceId, f.Grant.Secret!, new ConsumeJoinTicketRequest
        { Ticket = otherTicket.Ticket, PlayerId = other.Profile.PlayerId, RoomEpoch = f.Grant.Descriptor.RoomEpoch }, default);
        Assert.Equal(other.ActorId, admitted.ActorId); Assert.Equal(2u, admitted.ConnectionEpoch);
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM AssetLeases WHERE RunId=@Run AND CharacterId=@Player",
            new { Run = f.Grant.Descriptor.RunId, Player = other.Profile.PlayerId }));

        var failure = await Assert.ThrowsAsync<OnlineFailure>(() =>
            instances.MintTicketAsync(target.Profile.PlayerId, f.Grant.Descriptor.RoomId, default));
        // 账号Session仍有效；这里只结束本Run资格。401会让正式客户端清账号Session，不能复用。
        Assert.Equal(409, failure.Status); Assert.Equal("RunMemberNotActive", failure.Code);
        // 失败之后再核对，防未来把个人资格失败接到全局FailAsync而误清队友。
        Assert.Equal("Ready", await db.QuerySingleAsync<string>("SELECT State FROM ServerInstances WHERE InstanceId=@Id",
            new { Id = f.Grant.Descriptor.InstanceId }));
        Assert.Equal("Admitted", await db.QuerySingleAsync<string>("SELECT State FROM RunMembers WHERE RunId=@Run AND CharacterId=@Player",
            new { Run = f.Grant.Descriptor.RunId, Player = other.Profile.PlayerId }));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM AssetLeases WHERE RunId=@Run AND CharacterId=@Player",
            new { Run = f.Grant.Descriptor.RunId, Player = other.Profile.PlayerId }));
    }

    [Theory, InlineData(false), InlineData(true)]
    [Trait("Category", "DatabaseIntegration")]
    public async Task ReservedOrAdmittedMemberWithMatchingLeaseKeepsMintAndConsumeContract(bool alreadyAdmitted)
    {
        await using var f = await RunAssetCheckpointServiceTests.Fixture.Create(admitAll: alreadyAdmitted);
        var instances = f.Factory.Services.GetRequiredService<RoomInstanceService>();
        var target = f.Ready.Members.Single(m => m.ActorId == 2);
        f.Factory.Clock.Advance(TimeSpan.FromSeconds(6));
        var ticket = await instances.MintTicketAsync(target.Profile.PlayerId, f.Grant.Descriptor.RoomId, default);
        var admitted = await instances.ConsumeAsync(f.Grant.Descriptor.InstanceId, f.Grant.Secret!, new ConsumeJoinTicketRequest
        { Ticket = ticket.Ticket, PlayerId = target.Profile.PlayerId, RoomEpoch = f.Grant.Descriptor.RoomEpoch }, default);
        Assert.Equal(target.Profile.PlayerId, admitted.PlayerId); Assert.Equal(target.ActorId, admitted.ActorId);
        Assert.Equal(alreadyAdmitted ? 2u : 1u, admitted.ConnectionEpoch);
        Assert.Equal(f.Grant.Descriptor.RunId, admitted.RunId); Assert.Equal(f.Grant.Descriptor.RoomEpoch, admitted.RoomEpoch);
    }
}
