using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PkgRelay.Services;

public sealed class NetworkService
{
    public IPAddress ResolveLocalAddress(
        string targetAddress,
        int targetPort,
        string configuredAddress)
    {
        if (!string.IsNullOrWhiteSpace(configuredAddress) &&
            IPAddress.TryParse(configuredAddress, out var configured) &&
            configured.AddressFamily == AddressFamily.InterNetwork)
        {
            return configured;
        }

        if (IPAddress.TryParse(targetAddress, out var target) &&
            target.AddressFamily == AddressFamily.InterNetwork)
        {
            try
            {
                using var socket = new Socket(
                    AddressFamily.InterNetwork,
                    SocketType.Dgram,
                    ProtocolType.Udp);

                socket.Connect(new IPEndPoint(target, targetPort));

                if (socket.LocalEndPoint is IPEndPoint local &&
                    !IPAddress.IsLoopback(local.Address))
                {
                    return local.Address;
                }
            }
            catch
            {
                // Fall through to NIC enumeration.
            }
        }

        return GetLanAddresses().FirstOrDefault()
            ?? throw new InvalidOperationException(
                "No usable IPv4 network interface was found.");
    }

    public async Task<IReadOnlyList<IPAddress>> FindRpiHostsAsync(
        int port,
        int connectTimeoutMs = 300,
        int maxConcurrency = 48,
        CancellationToken cancellationToken = default)
    {
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));

        var localAddresses = GetLanAddresses();
        if (localAddresses.Count == 0)
            return [];

        var candidates = new HashSet<IPAddress>();

        foreach (var local in localAddresses)
        {
            var bytes = local.GetAddressBytes();

            // Most PS4 home setups are /24. Keeping discovery deliberately
            // bounded avoids turning a convenience feature into a broad scanner.
            for (var host = 1; host < 255; host++)
            {
                var candidate = new IPAddress(
                    new byte[] { bytes[0], bytes[1], bytes[2], (byte)host });

                if (!candidate.Equals(local))
                    candidates.Add(candidate);
            }
        }

        var results = new ConcurrentBag<IPAddress>();
        using var gate = new SemaphoreSlim(Math.Max(1, maxConcurrency));

        var tasks = candidates.Select(async candidate =>
        {
            await gate.WaitAsync(cancellationToken);

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);

                timeout.CancelAfter(Math.Max(100, connectTimeoutMs));

                using var client = new TcpClient(AddressFamily.InterNetwork);

                try
                {
                    await client.ConnectAsync(candidate, port, timeout.Token);
                    results.Add(candidate);
                }
                catch (OperationCanceledException)
                    when (!cancellationToken.IsCancellationRequested)
                {
                }
                catch (SocketException)
                {
                }
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);

        return results
            .Distinct()
            .OrderBy(x => IpToUInt32(x))
            .ToArray();
    }

    public IReadOnlyList<IPAddress> GetLanAddresses()
    {
        var result = new List<IPAddress>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;

            if (nic.NetworkInterfaceType is
                NetworkInterfaceType.Loopback or
                NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            IPInterfaceProperties properties;

            try
            {
                properties = nic.GetIPProperties();
            }
            catch
            {
                continue;
            }

            var hasIpv4Gateway = properties.GatewayAddresses.Any(x =>
                x.Address.AddressFamily == AddressFamily.InterNetwork &&
                !x.Address.Equals(IPAddress.Any) &&
                !x.Address.Equals(IPAddress.None));

            if (!hasIpv4Gateway)
                continue;

            foreach (var address in properties.UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork ||
                    IPAddress.IsLoopback(address.Address) ||
                    !IsPrivateIpv4(address.Address))
                {
                    continue;
                }

                if (!result.Contains(address.Address))
                    result.Add(address.Address);
            }
        }

        return result;
    }

    private static bool IsPrivateIpv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4)
            return false;

        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168) ||
               (bytes[0] == 169 && bytes[1] == 254);
    }

    private static uint IpToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();

        return ((uint)bytes[0] << 24) |
               ((uint)bytes[1] << 16) |
               ((uint)bytes[2] << 8) |
               bytes[3];
    }
}
