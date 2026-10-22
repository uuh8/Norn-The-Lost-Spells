using System.Net;
using System.Net.Http.Json;
using System.Text;
using Game.NetworkProtocol;
using Xunit;

namespace Norn.Business.Tests;

public sealed class OnlineProfileTests
{
    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task LocalSave_IsNeverImported()
    {
        await using var factory = new OnlineFactory();
        using var client = factory.CreateClient();
        string username = "n1_" + Guid.NewGuid().ToString("N")[..16];
        // 严格拒绝未知字段，不能把客户端造出的本地余额/物品当作注册资产。
        using var body = new StringContent("{\"Username\":\"" + username + "\",\"Password\":\"" + OnlineFactory.Password + "\",\"LocalSave\":{\"Gold\":999999}}", Encoding.UTF8, "application/json");
        using var attempted = await client.PostAsync("/api/online/accounts", body);
        Assert.Equal(HttpStatusCode.BadRequest, attempted.StatusCode);
        var player = await factory.NewPlayerAsync(username);
        Assert.Equal(0, player.Profile.Gold);
        Assert.Equal(1u, player.Profile.Version);
        Assert.Equal(2, player.Profile.Items.Length);
        Assert.All(player.Profile.Items, item => Assert.Contains(factory.Manifest.Items, definition => definition.ContentId == item.ContentId));
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task NewBusinessHost_ReadsSamePersistentProfile()
    {
        string username, id, instance;
        await using (var first = new OnlineFactory())
        {
            var player = await first.NewPlayerAsync(); username = player.Username; id = player.Profile.PlayerId;
            instance = player.Profile.Items[0].InstanceId;
            using var response = await player.Client.PostAsJsonAsync("/api/online/inventory/move", OnlineFactory.Move(player.Profile), OnlineFactory.Json);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        await using var second = new OnlineFactory();
        using var client = second.CreateClient(); await second.LoginAsync(client, username);
        var profile = (await client.GetFromJsonAsync<OnlineProfileReply>("/api/online/profile", OnlineFactory.Json))!;
        Assert.Equal(id, profile.PlayerId);
        Assert.Equal(2u, profile.Version);
        Assert.Contains(profile.Items, item => item.InstanceId == instance && item.Area == "Warehouse" && item.Slot == 0);
    }
}
