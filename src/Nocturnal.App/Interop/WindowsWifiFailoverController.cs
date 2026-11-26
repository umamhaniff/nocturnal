using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Nocturnal.Core;

namespace Nocturnal.App.Interop;

public sealed class WindowsWifiFailoverController : INetworkFailoverController
{
    private volatile bool _lastWanProbeSucceeded = true;
    private long _lastProbeTicksUtc;
    private int _probeInFlight;
    private int _connectInFlight;

    public bool HasInternetConnectivity()
    {
        try
        {
            if (!HasLocalGatewayLink())
            {
                _lastWanProbeSucceeded = false;
                return false;
            }

            TriggerBackgroundWanProbeIfDue();
            return _lastWanProbeSucceeded;
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

        if (Interlocked.CompareExchange(ref _connectInFlight, 1, 0) != 0)
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

            var proc = Process.Start(psi);
            if (proc is null)
            {
                Interlocked.Exchange(ref _connectInFlight, 0);
                return false;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    using (proc)
                    using (var cts = new CancellationTokenSource(5000))
                    {
                        try
                        {
                            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            try { proc.Kill(); } catch { }
                        }
                    }

                    // Force a fresh WAN probe right after failover attempt completes.
                    Interlocked.Exchange(ref _lastProbeTicksUtc, 0);
                    _lastWanProbeSucceeded = await ProbeInternetAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Never surface background failover exceptions to caller.
                }
                finally
                {
                    Interlocked.Exchange(ref _connectInFlight, 0);
                }
            });

            return true;
        }
        catch
        {
            Interlocked.Exchange(ref _connectInFlight, 0);
            return false;
        }
    }

    private static bool HasLocalGatewayLink()
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

    private void TriggerBackgroundWanProbeIfDue()
    {
        var nowTicks = DateTimeOffset.UtcNow.UtcTicks;
        var prevTicks = Interlocked.Read(ref _lastProbeTicksUtc);
        if (nowTicks - prevTicks < TimeSpan.FromSeconds(15).Ticks)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _probeInFlight, 1, 0) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _lastProbeTicksUtc, nowTicks);
        _ = Task.Run(async () =>
        {
            try
            {
                _lastWanProbeSucceeded = await ProbeInternetAsync().ConfigureAwait(false);
            }
            catch
            {
                _lastWanProbeSucceeded = false;
            }
            finally
            {
                Interlocked.Exchange(ref _probeInFlight, 0);
            }
        });
    }

    private static async Task<bool> ProbeInternetAsync() =>
        await ProbeEndpointAsync("1.1.1.1", 53).ConfigureAwait(false)
        || await ProbeEndpointAsync("8.8.8.8", 53).ConfigureAwait(false);

    private static async Task<bool> ProbeEndpointAsync(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(1500);
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
