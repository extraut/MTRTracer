using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MTRTracer.Core.Native;

/// <summary>P/Invoke surface of iphlpapi.dll used by the app. No third-party drivers required.</summary>
internal static class IpHlpApi
{
    public const int AF_INET = 2;
    public const int AF_INET6 = 23;

    public const byte IP_FLAG_DF = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    public struct IP_OPTION_INFORMATION
    {
        public byte Ttl;
        public byte Tos;
        public byte Flags;
        public byte OptionsSize;
        public IntPtr OptionsData;
    }

    public sealed class SafeIcmpHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeIcmpHandle() : base(true) { }
        protected override bool ReleaseHandle() => IcmpCloseHandle(handle);
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern SafeIcmpHandle IcmpCreateFile();

    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern SafeIcmpHandle Icmp6CreateFile();

    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern bool IcmpCloseHandle(IntPtr handle);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern uint IcmpSendEcho2Ex(
        SafeIcmpHandle icmpHandle, IntPtr evt, IntPtr apcRoutine, IntPtr apcContext,
        uint sourceAddress, uint destinationAddress,
        byte[] requestData, ushort requestSize, ref IP_OPTION_INFORMATION requestOptions,
        IntPtr replyBuffer, uint replySize, uint timeout);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern uint Icmp6SendEcho2(
        SafeIcmpHandle icmpHandle, IntPtr evt, IntPtr apcRoutine, IntPtr apcContext,
        byte[] sourceAddress, byte[] destinationAddress,
        byte[] requestData, ushort requestSize, ref IP_OPTION_INFORMATION requestOptions,
        IntPtr replyBuffer, uint replySize, uint timeout);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern int GetBestRoute2(
        IntPtr interfaceLuid, uint interfaceIndex, byte[]? sourceAddress, byte[] destinationAddress,
        uint addressSortOptions, byte[] bestRoute, byte[] bestSourceAddress);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

    public const int TCP_TABLE_OWNER_PID_ALL = 5;
    public const int UDP_TABLE_OWNER_PID = 1;
    public const uint ERROR_INSUFFICIENT_BUFFER = 122;

    /// <summary>Size of a SOCKADDR_INET union (sockaddr_in6 is the largest member).</summary>
    public const int SockAddrInetSize = 28;

    /// <summary>Encodes an address as SOCKADDR_IN / SOCKADDR_IN6 (28 bytes, zero padded).</summary>
    public static byte[] ToSockAddr(IPAddress? address, AddressFamily family)
    {
        var buf = new byte[SockAddrInetSize];
        if (family == AddressFamily.InterNetworkV6)
        {
            BitConverter.TryWriteBytes(buf.AsSpan(0, 2), (ushort)AF_INET6);
            if (address != null)
            {
                address.GetAddressBytes().CopyTo(buf, 8);
                BitConverter.TryWriteBytes(buf.AsSpan(24, 4), (uint)address.ScopeId);
            }
        }
        else
        {
            BitConverter.TryWriteBytes(buf.AsSpan(0, 2), (ushort)AF_INET);
            address?.GetAddressBytes().CopyTo(buf, 4);
        }
        return buf;
    }

    public static IPAddress? FromSockAddr(ReadOnlySpan<byte> buf)
    {
        var family = BitConverter.ToUInt16(buf[..2]);
        if (family == AF_INET)
        {
            var a = new IPAddress(buf.Slice(4, 4));
            return a.Equals(IPAddress.Any) ? null : a;
        }
        if (family == AF_INET6)
        {
            var a = new IPAddress(buf.Slice(8, 16).ToArray(), BitConverter.ToUInt32(buf.Slice(24, 4)));
            return a.Equals(IPAddress.IPv6Any) ? null : a;
        }
        return null;
    }

    /// <summary>IPv4 address as the in_addr DWORD expected by IcmpSendEcho2Ex.</summary>
    public static uint ToInAddr(IPAddress address)
    {
        Span<byte> b = stackalloc byte[4];
        address.TryWriteBytes(b, out _);
        return BitConverter.ToUInt32(b);
    }
}
