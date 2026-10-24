using System;
using Game.NetworkProtocol;
using Google.Protobuf;

namespace Game.Networking
{
    /// <summary>只处理 Wire 与有限值；认证身份、Owned 和瞄准权限由上层裁决。返回对象只借用到下一次同类解码。</summary>
    public sealed class CombatCodec
    {
        public const int MaxPayloadBytes = 512;
        private readonly CombatCastIntentMessage _intent = new CombatCastIntentMessage();
        private readonly CombatFactMessage _fact = new CombatFactMessage();
        public bool TryDecodeIntent(ArraySegment<byte> payload, out CombatCastIntentMessage message)
        {
            message = null; _intent.ResetForReuse();
            if (!Payload(payload) || !Wire(new ReadOnlySpan<byte>(payload.Array, payload.Offset, payload.Count), true)) return false;
            try { _intent.MergeFrom(new ReadOnlySpan<byte>(payload.Array, payload.Offset, payload.Count)); }
            catch (InvalidProtocolBufferException) { _intent.ResetForReuse(); return false; }
            if (!Valid(_intent)) { _intent.ResetForReuse(); return false; }
            message = _intent; return true;
        }
        public bool TryDecodeFact(ArraySegment<byte> payload, out CombatFactMessage message)
        {
            message = null; _fact.ResetForReuse();
            if (!Payload(payload) || !Wire(new ReadOnlySpan<byte>(payload.Array, payload.Offset, payload.Count), false)) return false;
            try { _fact.MergeFrom(new ReadOnlySpan<byte>(payload.Array, payload.Offset, payload.Count)); }
            catch (InvalidProtocolBufferException) { _fact.ResetForReuse(); return false; }
            if (!Valid(_fact)) { _fact.ResetForReuse(); return false; }
            message = _fact; return true;
        }
        public bool TryEncodeIntent(CombatCastIntentMessage message, ArraySegment<byte> output, out int written)
        { written = 0; return Valid(message) && Encode(message, output, true, out written); }
        public bool TryEncodeFact(CombatFactMessage message, ArraySegment<byte> output, out int written)
        { written = 0; return Valid(message) && Encode(message, output, false, out written); }
        private static bool Encode(IMessage message, ArraySegment<byte> output, bool intent, out int written)
        {
            written = 0; if (output.Array == null) return false;
            int size = message.CalculateSize();
            if (size < 1 || size > MaxPayloadBytes || size > output.Count) return false;
            var bytes = new Span<byte>(output.Array, output.Offset, size); message.WriteTo(bytes);
            if (!Wire(bytes, intent)) return false;
            written = size; return true;
        }
        private static bool Payload(ArraySegment<byte> bytes) => bytes.Array != null && bytes.Count > 0 && bytes.Count <= MaxPayloadBytes;
        private static bool Finite(float value) => float.IsFinite(value) && Math.Abs(value) <= 1000000f;
        private static bool Identity(string value)
        {
            if (value == null || value.Length != 32) return false;
            for (int i = 0; i < value.Length; i++)
                if (!(value[i] >= 'a' && value[i] <= 'f') && !(value[i] >= '0' && value[i] <= '9')) return false;
            return true;
        }
        private static bool Valid(CombatCastIntentMessage m) => m != null && ProtocolCompatibility.IsSupported(m.ProtocolVersion)
            && m.ConnectionEpoch != 0 && m.CommandId != 0 && m.GameplayHash != 0 && m.ProgramRevision != 0 && Identity(m.WandInstanceId)
            && Finite(m.OriginX) && Finite(m.OriginY) && Finite(m.OriginZ) && Finite(m.AimX) && Finite(m.AimY) && Finite(m.AimZ);
        private static bool Valid(CombatFactMessage m)
        {
            if (m == null || !ProtocolCompatibility.IsSupported(m.ProtocolVersion) || m.GameplayHash == 0 || m.Sequence == 0
                || m.ServerTick == 0 || m.Kind < 1 || m.Kind > 11 || m.ActorId < 1 || m.ActorId > 8 || m.TargetActorId > 8
                || m.ConnectionEpoch == 0 || m.Team > 255 || m.Flags > 7 || m.Phase > 32 || m.AttackerActorId > 8 || m.DamageType > 2 || m.ActionKind > 2
                || !Finite(m.PositionX) || !Finite(m.PositionY) || !Finite(m.PositionZ) || !Finite(m.RotationX) || !Finite(m.RotationY)
                || !Finite(m.RotationZ) || !Finite(m.RotationW) || !Finite(m.VelocityX) || !Finite(m.VelocityY) || !Finite(m.VelocityZ)
                || !Finite(m.CurrentHp) || !Finite(m.MaxHp) || !Finite(m.CurrentMana) || !Finite(m.MaxMana) || !Finite(m.Cooldown)
                || !Finite(m.Burning) || !Finite(m.Wet) || !Finite(m.Poisoned) || !Finite(m.Sticky) || !Finite(m.MoveMultiplier)
                || !Finite(m.Amount) || m.CurrentHp < 0 || m.MaxHp < 0 || m.CurrentHp > m.MaxHp
                || m.CurrentMana < 0 || m.MaxMana < 0 || m.CurrentMana > m.MaxMana || m.Cooldown < 0) return false;
            if (m.Kind == 1) return Identity(m.WandInstanceId) && m.ProgramRevision != 0;
            if (m.Kind == 11 && (m.SoundKey == 0 || m.CommandId == 0 || m.OutputOrdinal == 0)) return false;
            if (m.WandInstanceId.Length != 0 && !Identity(m.WandInstanceId)) return false;
            if (m.Kind >= 4 && m.Kind <= 6 && m.OutputId == 0) return false;
            if (m.Kind == 4 && m.PrefabKey == 0) return false;
            if (m.Kind == 4 || m.Kind == 5)
            {
                float norm = m.RotationX * m.RotationX + m.RotationY * m.RotationY + m.RotationZ * m.RotationZ + m.RotationW * m.RotationW;
                if (norm < .9f || norm > 1.1f) return false;
            }
            if (m.Kind == 7 && (m.MaxHp <= 0 || m.MoveMultiplier < 0 || m.MoveMultiplier > 1)) return false;
            return true;
        }

        private static bool Wire(ReadOnlySpan<byte> bytes, bool intent)
        {
            int offset = 0; ulong seen = 0;
            while (offset < bytes.Length)
            {
                if (!Varint(bytes, ref offset, out ulong tag) || tag > uint.MaxValue) return false;
                int field = (int)(tag >> 3), maximum = intent ? 12 : 45;
                if (field < 1 || field > maximum) return false;
                ulong bit = 1UL << field; if ((seen & bit) != 0) return false; seen |= bit;
                bool text = field == (intent ? 4 : 37);
                bool scalarFloat = intent ? field >= 6 && field <= 11 : field >= 12 && field <= 31 || field == 35;
                bool fixed64 = intent ? field == 3 || field == 12 : field == 2 || field == 8 || field == 9;
                int expected = text ? 2 : scalarFloat ? 5 : fixed64 ? 1 : 0;
                if ((tag & 7) != (ulong)expected) return false;
                if (text)
                {
                    if (!Varint(bytes, ref offset, out ulong length) || length > 64 || length > (ulong)(bytes.Length - offset)) return false;
                    offset += (int)length;
                }
                else if (expected != 0)
                {
                    int width = expected == 5 ? 4 : 8; if (bytes.Length - offset < width) return false; offset += width;
                }
                else
                {
                    if (!Varint(bytes, ref offset, out ulong value)) return false;
                    if ((intent || field != 6 && field != 34 && field != 41) && value > uint.MaxValue) return false;
                }
            }
            return true;
        }
        private static bool Varint(ReadOnlySpan<byte> bytes, ref int offset, out ulong value)
        {
            value = 0;
            for (int shift = 0; shift <= 63; shift += 7)
            {
                if (offset >= bytes.Length) return false;
                byte next = bytes[offset++]; if (shift == 63 && next > 1) return false;
                value |= (ulong)(next & 127) << shift;
                if ((next & 128) == 0) return shift == 0 || next != 0;
            }
            return false;
        }
    }
}
