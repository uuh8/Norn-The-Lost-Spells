using System.Collections;
using Game.NetworkProtocol;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Xunit;

namespace Norn.Business.Tests;

/// <summary>Owner嵌套状态可能被保留引用；归还时既清父字段，也清组分/unknown fields，避免下次租用漏旧量。</summary>
public sealed class InventoryMessageResetTests
{
    [Theory, Trait("Category", "InventoryReset")]
    [InlineData("InventorySnapshotMessage")]
    [InlineData("InventoryOwnedContainerStateMessage")]
    [InlineData("InventoryOwnedMaterialAmountMessage")]
    [InlineData("InventoryCommandMessage")]
    [InlineData("InventoryCommandReceiptMessage")]
    [InlineData("InventoryGroundSnapshotMessage")]
    [InlineData("InventoryGroundItemMessage")]
    public void ResetClearsEveryKnownAndUnknownFieldIncludingRetainedChildren(string name)
    {
        Type? type = typeof(InventorySnapshotMessage).Assembly.GetType("Game.NetworkProtocol." + name);
        Assert.True(type != null, "Owner组分Schema尚未生成。 ");
        var root = (IMessage)Activator.CreateInstance(type!)!;
        var retained = new List<IMessage>(); Fill(root, retained);
        Assert.True(root.CalculateSize() > 0);
        var reset = type!.GetMethod("ResetForReuse");
        Assert.True(reset != null, "Owner状态缺少完整Reset入口。 ");
        reset!.Invoke(root, null);
        Assert.All(retained, child => Assert.Equal(0, child.CalculateSize()));
        // 连续归还须幂等；不能第二次重放旧父引用或残留unknown fields。
        reset.Invoke(root, null); Assert.Equal(0, root.CalculateSize());
    }

    [Fact, Trait("Category", "InventoryReset")]
    public void ContainerTransferWireFieldsAreExplicitAndDoNotAliasOldCommands()
    {
        // 必须检查生成描述符，而不能只检查Core字段：否则Codec可能在旧Schema中静默丢掉数量/射线。
        var command = InventoryCommandMessage.Descriptor;
        Assert.Equal("transfer_budget", command.FindFieldByNumber(14)?.Name);
        Assert.Equal("aim_origin_x", command.FindFieldByNumber(15)?.Name);
        Assert.Equal("aim_direction_z", command.FindFieldByNumber(20)?.Name);
        var receipt = InventoryCommandReceiptMessage.Descriptor;
        Assert.Equal("transferred", receipt.FindFieldByNumber(7)?.Name);
        Assert.Equal("transfer_failure", receipt.FindFieldByNumber(8)?.Name);
    }

    [Fact, Trait("Category", "InventoryReset")]
    public void GroundFieldsAreExplicitAndOldApplicationCannotEnter()
    {
        Assert.Equal("ground", InventorySnapshotMessage.Descriptor.FindFieldByNumber(13)?.Name);
        Assert.Equal("world_owner_id", InventoryCommandMessage.Descriptor.FindFieldByNumber(21)?.Name);
        Assert.Equal("target_backpack_id", InventoryCommandMessage.Descriptor.FindFieldByNumber(22)?.Name);
        Assert.Equal("grid_x", InventoryCommandMessage.Descriptor.FindFieldByNumber(23)?.Name);
        Assert.Equal("auto_place", InventoryCommandMessage.Descriptor.FindFieldByNumber(26)?.Name);
        // Ground是v7引入的历史字段；它存在不表示如今仍允许v7进入当前房间。
        // 支持资格始终取Shared的当前应用版本，旧6/7以及紧邻上一版都必须被拒绝。
        Assert.False(ProtocolCompatibility.IsSupported(6)); Assert.False(ProtocolCompatibility.IsSupported(7));
        Assert.False(ProtocolCompatibility.IsSupported(ProtocolCompatibility.CurrentVersion - 1));
        Assert.True(ProtocolCompatibility.IsSupported(ProtocolCompatibility.CurrentVersion));
    }

    private static void Fill(IMessage message, List<IMessage> retained)
    {
        retained.Add(message);
        foreach (var field in message.Descriptor.Fields.InFieldNumberOrder())
        {
            object value = field.FieldType switch
            {
                FieldType.Message => Activator.CreateInstance(field.MessageType.ClrType)!,
                FieldType.String => "known", FieldType.Bool => true, FieldType.Float => 1f,
                // 研究快照是bytes；反射Setter要求ByteString，不能落入int缺省分支而在Reset前就失败。
                // 必须填非空值，随后CalculateSize==0才真正证明旧正文已清空。
                FieldType.Bytes => ByteString.CopyFromUtf8("known"),
                FieldType.UInt64 or FieldType.Fixed64 => 1ul,
                FieldType.UInt32 or FieldType.Fixed32 => 1u,
                FieldType.Int64 or FieldType.SInt64 or FieldType.SFixed64 => 1L,
                _ => 1
            };
            if (value is IMessage child) Fill(child, retained);
            if (field.IsRepeated) ((IList)field.Accessor.GetValue(message)).Add(value);
            else field.Accessor.SetValue(message, value);
        }
        message.MergeFrom(new byte[] { 0xf8, 0x07, 0x7b }); // unknown field127=123，Reset必须清除。
    }
}
