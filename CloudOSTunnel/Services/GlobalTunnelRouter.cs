using CloudOSTunnel.Clients;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CloudOSTunnel.Services
{
    public class GlobalTunnelRouter
    {
        public static int[] AllowedPorts = new int[] {
        };

        //private Dictionary<int, VMWareClient> ConnectedClients = new Dictionary<int, VMWareClient>();
        private Dictionary<int, ITunnel> HostedServers = new Dictionary<int, ITunnel>();
        static readonly object _locker = new object();

        public GlobalTunnelRouter(IConfiguration configuration)
        {
            AllowedPorts = configuration.GetValue<string>("TunnelPorts").Split(",").Select(ap => Int32.Parse(ap)).ToArray();
        }

        public int InitializeTunnel<T>(T tunnel) where
            T : ITunnel
        {
            lock (_locker)
            {
                foreach (var port in AllowedPorts)

                    if (!HostedServers.ContainsKey(port))
                    {
                        HostedServers.Add(port, tunnel);
                        tunnel.StartListening(port);
                        return port;
                    }
                tunnel.Shutdown();
                throw new Exception("No available ports.");
            }
        }

        public ITunnel GetServer(int port)
        {
            if (HostedServers.ContainsKey(port))
            {
                return HostedServers[port];
            }
            return null;
        }

        public void DisconnectClient(int port)
        {
            lock (_locker)
            {
                HostedServers[port].Shutdown();
                HostedServers.Remove(port);
            }
        }
    }
}
