using System;

namespace kcp2k
{
    // header for messages processed by kcp.
    // this is NOT for the raw receive messages(!) because handshake/disconnect
    // need to be sent reliably. it's not enough to have those in rawreceive
    // because those messages might get lost without being resent!
    public enum KcpHeaderReliable : byte
    {
        // don't react on 0x00. might help to filter out random noise.
        Hello      = 1,
        // ping goes over reliable & KcpHeader for now. could go over unreliable
        // too. there is no real difference except that this is easier because
        // we already have a KcpHeader for reliable messages.
        // ping is only used to keep it alive, so latency doesn't matter.
        Ping       = 2,
        Data       = 3,
    }

    public enum KcpHeaderUnreliable : byte
    {
        // users may send unreliable messages
        Data = 4,
        // disconnect always goes through rapid fire unreliable (glenn fielder)
        Disconnect = 5,
    }

    // save convert the enums from/to byte.
    // attackers may attempt to send invalid values, so '255' may not convert.
    public static class KcpHeader
    {
        public static bool ParseReliable(byte value, out KcpHeaderReliable header)
        {
            // Norn 局部补丁：Enum.IsDefined(Type, object) 会装箱 byte；显式合法值保留全部 wire 校验语义。
            if (value == (byte)KcpHeaderReliable.Hello || value == (byte)KcpHeaderReliable.Ping || value == (byte)KcpHeaderReliable.Data)
            {
                header = (KcpHeaderReliable)value;
                return true;
            }

            header = KcpHeaderReliable.Ping; // any default
            return false;
        }

        public static bool ParseUnreliable(byte value, out KcpHeaderUnreliable header)
        {
            if (value == (byte)KcpHeaderUnreliable.Data || value == (byte)KcpHeaderUnreliable.Disconnect)
            {
                header = (KcpHeaderUnreliable)value;
                return true;
            }

            header = KcpHeaderUnreliable.Disconnect; // any default
            return false;
        }
    }
}
