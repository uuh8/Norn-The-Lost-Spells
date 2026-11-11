using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;
using Game.NetworkProtocol;

namespace Game.Networking
{
    /// <summary>仅实例检查点使用的有界控制面编码；可选class必须输出JSON null而不是Unity inline空壳。</summary>
    public static class InstanceCheckpointJson
    {
        public const int MaximumUtf8Bytes = OnlineRunAssetCheckpoint.MaximumPayloadBytes;
        private static readonly ConcurrentDictionary<Type, FieldInfo[]> Fields = new ConcurrentDictionary<Type, FieldInfo[]>();
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        /// <summary>私人Hub库存同样需要真正的null姿态，复用既有Shared受控编码器，不引入第二套JSON依赖。</summary>
        public static bool TrySerializeInventoryMove(InventoryMoveRequest value, out string json, out string error)
        {
            json = null; error = null;
            if (value == null) { error = "AccountInventoryRequestMissing"; return false; }
            try
            {
                var writer = new StringBuilder(1024); Write(writer, value, 0);
                string candidate = writer.ToString();
                // 单条意图只含身份、位置及一个有限pose，不允许误把整份资产塞进离散命令。
                if (Utf8.GetByteCount(candidate) > 16384) throw new EncodingFailure("AccountInventoryEncodingBudget");
                json = candidate; return true;
            }
            catch (EncodingFailure failure) { error = failure.Code; return false; }
            catch (EncoderFallbackException) { error = "AccountInventoryInvalidUnicode"; return false; }
        }

        public static bool TrySerialize(OnlineRunAssetCheckpoint snapshot, out string json, out string error)
        {
            json = null; error = null;
            if (snapshot == null || snapshot.SchemaVersion != OnlineRunAssetCheckpoint.CompleteSchemaVersion
                || !OnlineRunAssetCheckpoint.HasValidCoinFacts(snapshot.Coins))
            { error = "RunCheckpointCompleteSchemaRequired"; return false; }
            try
            {
                // 控制面只在离散完整捕获后编码，不进入每帧热路径。反射只用于本合同程序集的公开字段，
                // 不引入新JSON依赖，也不让Unity把null可选状态写成非法的全默认属性壳。
                var writer = new StringBuilder(4096);
                Write(writer, snapshot, 0);
                string candidate = writer.ToString();
                if (Utf8.GetByteCount(candidate) > MaximumUtf8Bytes) throw new EncodingFailure("RunCheckpointEncodingBudget");
                json = candidate; return true;
            }
            catch (EncodingFailure failure) { error = failure.Code; return false; }
            catch (EncoderFallbackException) { error = "RunCheckpointInvalidUnicode"; return false; }
        }

        /// <summary>
        /// 个人退出复用同一个受控Shared字段编码器；可选类保持真正null，long金币不转float/int。
        /// 只编码已经捕获的候选，不读取Entry、不补版本，不将编码成功误当业务事务已经提交。
        /// 完整形状/归属仍由DS捕获器和业务规则检查；此处只拒绝旧Schema/缺Owned3及字节越界。
        /// </summary>
        public static bool TrySerializeMemberExit(OnlineRunMemberExitRequest snapshot, out string json, out string error)
        {
            json = null; error = null;
            if (snapshot == null || snapshot.SchemaVersion != OnlineRunMemberExitRequest.CurrentSchemaVersion
                || snapshot.Assets == null || snapshot.Assets.SchemaVersion != OnlineOwnedAssetSnapshot.CurrentSchemaVersion
                || snapshot.Reason < OnlineRunMemberExitReason.VoluntaryExit || snapshot.Reason > OnlineRunMemberExitReason.FinalDeath
                || snapshot.FinalRetiredItems == null || snapshot.FinalRetiredItems.Length > OnlineOwnedAssetSnapshot.MaximumItems
                || snapshot.FinalDeathLostMaterials == null || snapshot.FinalDeathLostMaterials.Length > 8
                || snapshot.PracticeGroundItems == null || snapshot.PracticeGroundItems.Length > OnlineOwnedAssetSnapshot.MaximumItems + 24
                || snapshot.Reason == OnlineRunMemberExitReason.VoluntaryExit
                    && (snapshot.FinalDeathLostMaterials.Length != 0 || snapshot.FinalRetiredItems.Length != 0
                        && (string.IsNullOrEmpty(snapshot.PracticeSessionScope) || string.IsNullOrEmpty(snapshot.PracticeContinuityJson)
                            || string.IsNullOrEmpty(snapshot.PracticeContinuityHash))))
            { error = "RunMemberExitSchemaRequired"; return false; }
            try
            {
                var writer = new StringBuilder(4096); Write(writer, snapshot, 0);
                string candidate = writer.ToString();
                // 当前个人候选与既有检查点共用64MiB上界；不因私有Write复用而绕过个人DTO预算。
                if (Utf8.GetByteCount(candidate) > OnlineRunMemberExitRequest.MaximumPayloadBytes)
                    throw new EncodingFailure("RunMemberExitEncodingBudget");
                json = candidate; return true;
            }
            catch (EncodingFailure failure) { error = failure.Code; return false; }
            catch (EncoderFallbackException) { error = "RunMemberExitInvalidUnicode"; return false; }
        }

        private static void Write(StringBuilder writer, object value, int depth)
        {
            if (depth > 32 || writer.Length > MaximumUtf8Bytes) throw new EncodingFailure("RunCheckpointEncodingBudget");
            if (value == null) { writer.Append("null"); return; }
            if (value is string text) { Quote(writer, text); return; }
            if (value is bool flag) { writer.Append(flag ? "true" : "false"); return; }
            if (value is float number)
            {
                if (!float.IsFinite(number)) throw new EncodingFailure("RunCheckpointNonFiniteNumber");
                // SQL JSON把标量-0读回为0；两者数值相同。原uint材料bit数组走独立分支，绝不改写其符号位。
                if (number == 0) { writer.Append('0'); return; }
                writer.Append(number.ToString("R", CultureInfo.InvariantCulture)); return;
            }
            if (value is uint[] words)
            {
                // 材料payload可能有数百万uint；显式数组分支避免每个word反射/boxing。
                writer.Append('[');
                for (int i = 0; i < words.Length; i++)
                {
                    if (i != 0) writer.Append(',');
                    writer.Append(words[i].ToString(CultureInfo.InvariantCulture));
                    if ((i & 1023) == 0 && writer.Length > MaximumUtf8Bytes) throw new EncodingFailure("RunCheckpointEncodingBudget");
                }
                writer.Append(']'); return;
            }
            var type = value.GetType();
            if (type.IsEnum) { writer.Append(Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)); return; }
            if (value is byte || value is int || value is uint || value is long || value is ulong)
            { writer.Append(((IFormattable)value).ToString(null, CultureInfo.InvariantCulture)); return; }
            if (value is Array array)
            {
                writer.Append('[');
                for (int i = 0; i < array.Length; i++)
                { if (i != 0) writer.Append(','); Write(writer, array.GetValue(i), depth + 1); }
                writer.Append(']'); return;
            }
            // 没有object扩展入口；遇到未来增加的非合同类型必须拒绝，不能ToString静默丢字段。
            if (type.Assembly != typeof(OnlineRunAssetCheckpoint).Assembly || type.Namespace != "Game.NetworkProtocol")
                throw new EncodingFailure("RunCheckpointUnsupportedValue");
            var fields = Fields.GetOrAdd(type, PublicFields);
            writer.Append('{');
            for (int i = 0; i < fields.Length; i++)
            {
                if (i != 0) writer.Append(','); Quote(writer, fields[i].Name); writer.Append(':');
                Write(writer, fields[i].GetValue(value), depth + 1);
            }
            writer.Append('}');
        }
        private static FieldInfo[] PublicFields(Type type)
            => type.GetFields(BindingFlags.Instance | BindingFlags.Public);
        private static void Quote(StringBuilder writer, string value)
        {
            writer.Append('"');
            foreach (char ch in value)
            {
                switch (ch)
                {
                    case '"': writer.Append("\\\""); break;
                    case '\\': writer.Append("\\\\"); break;
                    case '\n': writer.Append("\\n"); break;
                    case '\r': writer.Append("\\r"); break;
                    case '\t': writer.Append("\\t"); break;
                    default:
                        if (ch < 32) writer.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                        else writer.Append(ch);
                        break;
                }
                if (writer.Length > MaximumUtf8Bytes) throw new EncodingFailure("RunCheckpointEncodingBudget");
            }
            writer.Append('"');
        }
        private sealed class EncodingFailure : Exception
        {
            internal readonly string Code;
            internal EncodingFailure(string code) { Code = code; }
        }
    }
}
