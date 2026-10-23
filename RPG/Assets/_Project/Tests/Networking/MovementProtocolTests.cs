using System;
using System.Collections.Generic;
using System.IO;
using Game.NetworkProtocol;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Mirror;
using NUnit.Framework;

namespace Game.Networking.Tests
{
    public sealed class MovementProtocolTests
    {
        [Test]
        public void InputRoundtrip_PreservesAllFieldsAndSegmentOffsets()
        {
            var codec = new MovementCodec(); var expected = FullInput(); var bytes = new byte[520];
            Assert.True(codec.TryEncodeInput(expected, new ArraySegment<byte>(bytes, 4, 512), out int written));
            Assert.True(codec.TryDecodeInput(new ArraySegment<byte>(bytes, 4, written), out var actual));
            Assert.That(actual, Is.EqualTo(expected));
        }

        [Test]
        public void StateRoundtrip_PreservesEveryMotorField()
        {
            var codec = new MovementCodec(); var expected = FullState(); var bytes = new byte[520];
            Assert.True(codec.TryEncodeState(expected, new ArraySegment<byte>(bytes, 3, 512), out int written));
            Assert.True(codec.TryDecodeState(new ArraySegment<byte>(bytes, 3, written), out var actual));
            Assert.That(actual, Is.EqualTo(expected));
            Assert.That(written, Is.LessThanOrEqualTo(MovementCodec.MaxPayloadBytes));
        }

        [Test]
        public void Decode_ReusesObjectsAndClearsFieldsOmittedByNextPacket()
        {
            var codec = new MovementCodec();
            Assert.True(codec.TryDecodeInput(new ArraySegment<byte>(FullInput().ToByteArray()), out var firstInput));
            var minimalInput = MinimalInput();
            Assert.True(codec.TryDecodeInput(new ArraySegment<byte>(minimalInput.ToByteArray()), out var nextInput));
            Assert.That(nextInput, Is.SameAs(firstInput)); Assert.That(nextInput, Is.EqualTo(minimalInput));
            Assert.True(codec.TryDecodeState(new ArraySegment<byte>(FullState().ToByteArray()), out var firstState));
            var minimalState = MinimalState();
            Assert.True(codec.TryDecodeState(new ArraySegment<byte>(minimalState.ToByteArray()), out var nextState));
            Assert.That(nextState, Is.SameAs(firstState)); Assert.That(nextState, Is.EqualTo(minimalState));
        }

        [Test]
        public void Reset_ClearsEveryFieldAndUnknownFields()
        {
            var input = FullInput(); var state = FullState();
            byte[] unknown = { 0xF8, 0x03, 1 }; // field63的未知varint；仅用来验证partial清理。
            input.MergeFrom(unknown); state.MergeFrom(unknown);
            input.ResetForReuse(); state.ResetForReuse();
            Assert.That(input, Is.EqualTo(new MovementInputMessage()));
            Assert.That(state, Is.EqualTo(new MovementStateMessage()));
            Assert.That(input.CalculateSize(), Is.Zero); Assert.That(state.CalculateSize(), Is.Zero);
        }

        [Test]
        public void Codec_RejectsLengthsVersionsAndInvalidNumericBoundaries()
        {
            var codec = new MovementCodec(); var bytes = new byte[512];
            Assert.False(codec.TryDecodeInput(default, out _)); Assert.False(codec.TryDecodeState(new ArraySegment<byte>(new byte[513]), out _));
            var input = MinimalInput(); input.Sequence = 0; Assert.False(codec.TryEncodeInput(input, bytes, out _));
            input = MinimalInput(); input.ConnectionEpoch = 0; Assert.False(codec.TryEncodeInput(input, bytes, out _));
            input = MinimalInput(); input.ProtocolVersion = 0; Assert.False(codec.TryEncodeInput(input, bytes, out _));
            input = MinimalInput(); input.SimulationHash = 0; Assert.False(codec.TryEncodeInput(input, bytes, out _));
            input = MinimalInput(); input.MoveX = 1.01f; Assert.False(codec.TryDecodeInput(new ArraySegment<byte>(input.ToByteArray()), out _));
            input = MinimalInput(); input.MoveY = float.NaN; Assert.False(codec.TryEncodeInput(input, bytes, out _));
            input = MinimalInput(); input.ViewYaw = float.PositiveInfinity; Assert.False(codec.TryEncodeInput(input, bytes, out _));
            var state = MinimalState(); state.ActorId = 9; Assert.False(codec.TryEncodeState(state, bytes, out _));
            state = MinimalState(); state.ServerTick = 0; Assert.False(codec.TryEncodeState(state, bytes, out _));
            state = MinimalState(); state.SnapshotSequence = 0; Assert.False(codec.TryEncodeState(state, bytes, out _));
            state = MinimalState(); state.Locomotion = 4; Assert.False(codec.TryEncodeState(state, bytes, out _));
            state = MinimalState(); state.ExtraJumpsRemaining = 9; Assert.False(codec.TryEncodeState(state, bytes, out _));
            state = MinimalState(); state.AirDashesRemaining = 9; Assert.False(codec.TryEncodeState(state, bytes, out _));
            state = MinimalState(); state.GroundNormalY = float.NegativeInfinity; Assert.False(codec.TryDecodeState(new ArraySegment<byte>(state.ToByteArray()), out _));
        }

        [Test]
        public void WirePreflight_RejectsUnknownDuplicateWrongWireAndNonBooleanValues()
        {
            var codec = new MovementCodec(); byte[] valid = MinimalInput().ToByteArray();
            Assert.False(codec.TryDecodeInput(Append(valid, 0xF8, 0x03, 1), out _), "未知field不能进入MergeFrom建立UnknownFieldSet。");
            Assert.False(codec.TryDecodeInput(Append(valid, 0x08, 1), out _), "重复单值field不接受last-wins语义。");
            Assert.False(codec.TryDecodeInput(Append(valid, 0x22, 1, 0), out _), "float只接受fixed32，拒绝length-delimited。");
            Assert.False(codec.TryDecodeInput(Append(valid, 0x38, 2), out _), "bool只接受规范0/1。");
            Assert.False(codec.TryDecodeInput(Append(valid, 0x80), out _), "截断varint不得交给解析器抛异常。");
            Assert.True(codec.TryDecodeInput(new ArraySegment<byte>(valid), out var recovered));
            Assert.That(recovered, Is.EqualTo(MinimalInput()), "坏包不能污染后续合法复用。");
        }

        [Test]
        public void StateWirePreflight_RejectsMalformedBooleansUnknownFieldsAndUintOverflow()
        {
            var codec = new MovementCodec(); var minimal = MinimalState(); byte[] valid = minimal.ToByteArray();
            Assert.False(codec.TryDecodeState(Append(valid, 0xF8, 0x03, 1), out _));
            Assert.False(codec.TryDecodeState(Append(valid, 0xB0, 0x02, 2), out _), "Frozen(bool field38)不能接受2。");
            minimal.SnapshotSequence = 0;
            Assert.False(codec.TryDecodeState(Append(minimal.ToByteArray(), 0xB8, 0x02, 0x80, 0x80, 0x80, 0x80, 0x10), out _), "field39拒绝uint32溢出。");
            var input = MinimalInput(); input.Sequence = 0;
            Assert.False(codec.TryDecodeInput(Append(input.ToByteArray(), 0x18, 0x80, 0x80, 0x80, 0x80, 0x10), out _));
            input = MinimalInput(); input.ProtocolVersion = 0;
            Assert.False(codec.TryDecodeInput(Append(input.ToByteArray(), 0x08, 0x81, 0), out _), "拒绝用两字节编码数值1。");
            input = MinimalInput(); input.SimulationHash = 0;
            Assert.False(codec.TryDecodeInput(Append(input.ToByteArray(), 0x58, 1), out _), "fixed64摘要不能使用varint wire。");
        }

        [Test]
        public void EveryFloatField_RejectsNonFiniteValues()
        {
            var codec = new MovementCodec(); var bytes = new byte[512];
            // 按Schema遍历，未来增加float却漏加Validate时也能直接暴露；只在测试中使用Reflection。
            foreach (var field in MovementInputMessage.Descriptor.Fields.InDeclarationOrder())
            {
                if (field.FieldType != FieldType.Float) continue;
                var input = MinimalInput(); field.Accessor.SetValue(input, float.NaN);
                Assert.False(codec.TryEncodeInput(input, bytes, out _), field.Name);
            }
            foreach (var field in MovementStateMessage.Descriptor.Fields.InDeclarationOrder())
            {
                if (field.FieldType != FieldType.Float) continue;
                var state = MinimalState(); field.Accessor.SetValue(state, float.PositiveInfinity);
                Assert.False(codec.TryEncodeState(state, bytes, out _), field.Name);
            }
        }

        [Test]
        public void Timers_AcceptExistingNegativeRemaindersButRejectUnboundedValues()
        {
            var codec = new MovementCodec(); var state = MinimalState(); var bytes = new byte[512];
            state.CoyoteTimeCounter = -.01f; state.JumpBufferCounter = -.02f; state.AttackBufferCounter = -.03f;
            state.AttackCooldownCounter = -.04f; state.DashCooldownCounter = -.05f; state.DashBufferCounter = -.06f;
            Assert.True(codec.TryEncodeState(state, bytes, out _));
            state.AttackCooldownCounter = 86401; Assert.False(codec.TryEncodeState(state, bytes, out _));
            state = MinimalState(); state.GroundAngle = 181; Assert.False(codec.TryEncodeState(state, bytes, out _));
            state = MinimalState(); state.DashElapsed = -.01f; Assert.False(codec.TryEncodeState(state, bytes, out _));
        }

        [Test]
        public void Encode_RejectsUnknownFieldsShortDestinationAndMissingStateIdentity()
        {
            var codec = new MovementCodec(); var input = MinimalInput(); var bytes = new byte[512];
            input.MergeFrom(new byte[] { 0xF8, 0x03, 1 });
            Assert.False(codec.TryEncodeInput(input, bytes, out int written)); Assert.That(written, Is.Zero);
            input.ResetForReuse(); input.MergeFrom(MinimalInput().ToByteArray());
            Assert.False(codec.TryEncodeInput(input, new ArraySegment<byte>(bytes, 0, input.CalculateSize() - 1), out written));
            Assert.That(written, Is.Zero); Assert.False(codec.TryEncodeInput(input, default, out _));
            var state = MinimalState(); state.ActorId = 0; Assert.False(codec.TryEncodeState(state, bytes, out _));
            state = MinimalState(); state.ConnectionEpoch = 0; Assert.False(codec.TryEncodeState(state, bytes, out _));
            state = MinimalState(); state.ProtocolVersion = 2; Assert.False(codec.TryDecodeState(new ArraySegment<byte>(state.ToByteArray()), out _));
            state = MinimalState(); state.SimulationHash = 0; Assert.False(codec.TryEncodeState(state, bytes, out _));
            Assert.That(MovementInputMessage.Descriptor.FindFieldByName("actor_id"), Is.Null, "输入身份必须来自认证连接。");
        }

        [Test]
        public void SteadyCodecAndMalformedWire_ReusedBuffersHaveZeroCalibratedAllocationEvents()
        {
            var codec = new MovementCodec(); var input = FullInput(); var state = FullState();
            var inputBytes = new byte[512]; var stateBytes = new byte[512];
            var unknownInput = Append(MinimalInput().ToByteArray(), 0xF8, 0x03, 1);
            var truncatedState = new ArraySegment<byte>(new byte[] { 0xA9, 0x02, 1 });
            // 建立对象、生成数据与JIT/Parser预热均发生在计量之外。
            for (int i = 0; i < 64; i++)
            {
                codec.TryEncodeInput(input, inputBytes, out int inputSize);
                codec.TryEncodeState(state, stateBytes, out int stateSize);
                codec.TryDecodeInput(new ArraySegment<byte>(inputBytes, 0, inputSize), out _);
                codec.TryDecodeState(new ArraySegment<byte>(stateBytes, 0, stateSize), out _);
                codec.TryDecodeInput(unknownInput, out _); codec.TryDecodeState(truncatedState, out _);
            }
            using var meter = new CodecAllocationMeter();
            Assert.True(meter.IsValid, "必须先检测到已知1KiB分配，不能把失效计数器的0当证据。");
            bool accepted = true;
            meter.Begin();
            for (int i = 0; i < 256; i++)
            {
                accepted &= codec.TryEncodeInput(input, inputBytes, out int inputSize);
                accepted &= codec.TryEncodeState(state, stateBytes, out int stateSize);
                accepted &= codec.TryDecodeInput(new ArraySegment<byte>(inputBytes, 0, inputSize), out _);
                accepted &= codec.TryDecodeState(new ArraySegment<byte>(stateBytes, 0, stateSize), out _);
                accepted &= !codec.TryDecodeInput(unknownInput, out _);
                accepted &= !codec.TryDecodeState(truncatedState, out _);
            }
            long events = meter.End();
            Assert.True(accepted); Assert.True(meter.IsValid); Assert.That(events, Is.Zero);
        }

        [Test]
        public void MovementEnvelope_RejectsOversizeAndTruncationAndBorrowsReadBuffer()
        {
            using var writer = NetworkWriterPool.Get(); writer.WriteUInt(513);
            var reader = new NetworkReader(writer.ToArraySegment());
            Assert.Throws<InvalidDataException>(() => reader.ReadMovementInputEnvelope());
            writer.Reset(); writer.WriteUInt(3); writer.WriteByte(1); reader = new NetworkReader(writer.ToArraySegment());
            Assert.Throws<InvalidDataException>(() => reader.ReadMovementStateEnvelope());
            writer.Reset(); byte[] original = { 9, 1, 2, 3, 9 };
            writer.WriteMovementInputEnvelope(new MovementInputEnvelope { Payload = new ArraySegment<byte>(original, 1, 3) });
            var packed = writer.ToArraySegment(); var decoded = new NetworkReader(packed).ReadMovementInputEnvelope();
            Assert.That(decoded.Payload.Array, Is.SameAs(packed.Array));
            Assert.That(decoded.Payload.Count, Is.EqualTo(3)); Assert.That(decoded.Payload.Array[decoded.Payload.Offset], Is.EqualTo(1));
            writer.Reset(); writer.WriteMovementStateEnvelope(new MovementStateEnvelope { Payload = new ArraySegment<byte>(new byte[512]) });
            Assert.That(new NetworkReader(writer.ToArraySegment()).ReadMovementStateEnvelope().Payload.Count, Is.EqualTo(512));
            writer.Reset(); reader = new NetworkReader(new ArraySegment<byte>(new byte[3]));
            Assert.Throws<InvalidDataException>(() => reader.ReadMovementInputEnvelope());
            Assert.Throws<InvalidDataException>(() => writer.WriteMovementInputEnvelope(new MovementInputEnvelope()));
            Assert.Throws<InvalidDataException>(() => writer.WriteMovementStateEnvelope(new MovementStateEnvelope { Payload = new ArraySegment<byte>(new byte[513]) }));
        }

        [Test]
        public void MovementMessageIds_AreDistinctFromAllMirrorJoinAndRoomMessages()
        {
            var ids = new Dictionary<ushort, Type>();
            foreach (var type in typeof(NetworkManager).Assembly.GetTypes())
                if (type.IsValueType && !type.ContainsGenericParameters && typeof(NetworkMessage).IsAssignableFrom(type))
                    ids[type.FullName.GetStableHashCode16()] = type;
            foreach (var type in new[] { typeof(JoinTicketRequestEnvelope), typeof(JoinTicketReplyEnvelope), typeof(RoomProtocolEnvelope), typeof(MovementInputEnvelope), typeof(MovementStateEnvelope) })
            {
                ushort id = type.FullName.GetStableHashCode16();
                Assert.False(ids.TryGetValue(id, out var prior), type.FullName + " conflicts with " + prior?.FullName + " at " + id);
                ids.Add(id, type);
            }
        }

        private static MovementInputMessage MinimalInput() => new MovementInputMessage
        { ProtocolVersion = ProtocolCompatibility.CurrentVersion, ConnectionEpoch = 1, Sequence = 1, SimulationHash = 1 };
        private static MovementStateMessage MinimalState() => new MovementStateMessage
        { ProtocolVersion = ProtocolCompatibility.CurrentVersion, ActorId = 1, ConnectionEpoch = 1, ServerTick = 1, SimulationHash = 1, SnapshotSequence = 1 };
        private static MovementInputMessage FullInput() => new MovementInputMessage
        {
            ProtocolVersion = ProtocolCompatibility.CurrentVersion, ConnectionEpoch = 3, Sequence = uint.MaxValue,
            MoveX = -.75f, MoveY = .5f, ViewYaw = 123f, JumpPressed = true, JumpHeld = true,
            DashPressed = true, InputSuppressed = true, SimulationHash = 1
        };
        private static MovementStateMessage FullState() => new MovementStateMessage
        {
            ProtocolVersion = ProtocolCompatibility.CurrentVersion, ActorId = 8, ConnectionEpoch = 3,
            ServerTick = uint.MaxValue, AcknowledgedInput = uint.MaxValue - 1, IsEnemy = true,
            Connected = true, Targetable = true, PositionX = 1.25f, PositionY = -3f, PositionZ = 4.5f,
            Yaw = 123, VerticalVelocity = -9.8f, TargetFacingX = .1f, TargetFacingY = .2f, TargetFacingZ = .3f,
            Locomotion = 3, IsGrounded = true, GroundNormalX = .2f, GroundNormalY = .9f, GroundNormalZ = .1f,
            GroundAngle = 25, CoyoteTimeCounter = .1f, JumpBufferCounter = .2f, AttackBufferCounter = .3f,
            AttackCooldownCounter = .4f, DashCooldownCounter = .5f, DashBufferCounter = .6f,
            IsAirborneForDash = true, DashDirectionX = .3f, DashDirectionY = .4f, DashDirectionZ = .5f,
            DashElapsed = .15f, ExtraJumpsRemaining = 8, AirDashesRemaining = 7, JumpCutArmed = true, SimulationHash = 1,
            Frozen = true, SnapshotSequence = 1
        };
        private static ArraySegment<byte> Append(byte[] source, params byte[] suffix)
        {
            byte[] bytes = new byte[source.Length + suffix.Length]; Array.Copy(source, bytes, source.Length);
            Array.Copy(suffix, 0, bytes, source.Length, suffix.Length); return new ArraySegment<byte>(bytes);
        }
    }
}
