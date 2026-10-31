using System;

namespace Game.NetworkProtocol
{
    /// <summary>
    /// 所有控制面和握手边界共用的稳定标识比较。这里只验证格式与相等性，
    /// 业务服还必须检查部署白名单，Unity 组合层还必须确认本地存在该地图配置；格式合法不等于获得授权。
    /// </summary>
    public static class DungeonIdentityRules
    {
        /// <summary>只接受 1–32 个小写 ASCII 标识字符，禁止把任意路径或大小写别名当地图身份。</summary>
        public static bool IsValidId(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 32 || value[0] < 'a' || value[0] > 'z') return false;
            for (int i = 1; i < value.Length; i++)
            {
                char c = value[i];
                if (!(c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '_' || c == '-')) return false;
            }
            return true;
        }

        /// <summary>
        /// 空值与空值也必须拒绝：否则旧 JSON/proto 的缺省值会被误认作地图匹配成功。
        /// Ordinal 保持跨机器一致，不使用受 Culture 或文件系统大小写规则影响的比较。
        /// </summary>
        public static bool Matches(string expected, string actual) =>
            IsValidId(expected) && IsValidId(actual) && string.Equals(expected, actual, StringComparison.Ordinal);
    }
}
