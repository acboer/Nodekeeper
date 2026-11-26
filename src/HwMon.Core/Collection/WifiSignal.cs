using System.Runtime.InteropServices;
using System.Text;

namespace HwMon.Core.Collection;

/// <summary>
/// Sinal do Wi-Fi pela API nativa do Windows (wlanapi): qualidade do sinal (0–100 %, a mesma das barrinhas), nome
/// da rede e velocidade do link. Uma leitura por adaptador conectado, pelo GUID do adaptador (o mesmo Id do
/// NetworkInterface).
/// </summary>
public static class WifiSignal
{
    public sealed record Link(Guid Interface, string? Ssid, int SignalQuality, double RxMbps, double TxMbps);

    public static List<Link> Read()
    {
        var list = new List<Link>();
        IntPtr handle = IntPtr.Zero, interfaces = IntPtr.Zero;
        try
        {
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out handle) != 0) return list; // sem serviço de Wi-Fi (desktop)
            if (WlanEnumInterfaces(handle, IntPtr.Zero, out interfaces) != 0) return list;
            var count = Marshal.ReadInt32(interfaces);
            // WLAN_INTERFACE_INFO_LIST: 2 DWORD + itens de 532 bytes (GUID, 256 WCHAR, estado).
            for (var i = 0; i < count; i++)
            {
                var item = interfaces + 8 + i * 532;
                var guid = Marshal.PtrToStructure<Guid>(item);
                var state = Marshal.ReadInt32(item + 16 + 512);
                if (state != 1) continue; // wlan_interface_state_connected
                var g = guid;
                if (WlanQueryInterface(handle, ref g, 7, IntPtr.Zero, out _, out var data, out _) != 0 || data == IntPtr.Zero) continue;
                try
                {
                    // WLAN_CONNECTION_ATTRIBUTES: estado, modo, perfil (256 WCHAR) e, no deslocamento 520, a associação:
                    // SSID (tamanho + 32 bytes), tipo de BSS, BSSID (6 + 2 de alinhamento), tipo PHY, índice PHY,
                    // qualidade do sinal (576), taxa de recepção (580) e de envio (584) em kbit/s.
                    var ssidLength = Math.Clamp(Marshal.ReadInt32(data + 520), 0, 32);
                    var ssidBytes = new byte[ssidLength];
                    Marshal.Copy(data + 524, ssidBytes, 0, ssidLength);
                    var quality = Marshal.ReadInt32(data + 576);
                    var rx = (uint)Marshal.ReadInt32(data + 580);
                    var tx = (uint)Marshal.ReadInt32(data + 584);
                    list.Add(new Link(guid, ssidLength > 0 ? Encoding.UTF8.GetString(ssidBytes) : null, Math.Clamp(quality, 0, 100), rx / 1000.0, tx / 1000.0));
                }
                finally
                {
                    WlanFreeMemory(data);
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Windows sem o componente de Wi-Fi (Server Core): sem leitura.
        }
        finally
        {
            if (interfaces != IntPtr.Zero) WlanFreeMemory(interfaces);
            if (handle != IntPtr.Zero) WlanCloseHandle(handle, IntPtr.Zero);
        }
        return list;
    }

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr handle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanQueryInterface(IntPtr handle, ref Guid interfaceGuid, int opCode, IntPtr reserved,
        out uint dataSize, out IntPtr data, out int valueType);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);
}
