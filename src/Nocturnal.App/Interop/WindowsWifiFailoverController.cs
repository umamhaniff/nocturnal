using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using Nocturnal.Core;

namespace Nocturnal.App.Interop;

public sealed class WindowsWifiFailoverController : INetworkFailoverController
{
    public bool HasInternetConnectivity()
    {
        try
        {
            if (!NetworkInterface.GetIsNetworkAvailable())
            {
                return false;
            }

            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                if (ni.NetworkInterfaceType is not (NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet))
                {
                    continue;
                }

                var props = ni.GetIPProperties();
                if (props.GatewayAddresses.Count > 0)
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return true;
        }
    }

    public bool TryConnectToProfile(string ssid)
    {
        if (!NetworkFailoverPolicy.IsValidSsid(ssid))
        {
            return false;
        }

        try
        {
            var netshPath = Path.Combine(Environment.SystemDirectory, "netsh.exe");
            var psi = new ProcessStartInfo
            {
                FileName = netshPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("wlan");
            psi.ArgumentList.Add("connect");
            psi.ArgumentList.Add($"name={ssid.Trim()}");

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return false;
            }

            if (!proc.WaitForExit(5000))
            {
                try { proc.Kill(); } catch { }
                return false;
            }

            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
