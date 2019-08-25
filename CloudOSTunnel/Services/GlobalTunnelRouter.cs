using CloudOSTunnel.Clients;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CloudOSTunnel.Services
{
    public class GlobalTunnelRouter
    {
        public static int[] AllowedPorts = new int[] {
            5002,
            5003,
            5004
        };

        //private Dictionary<int, VMWareClient> ConnectedClients = new Dictionary<int, VMWareClient>();
        private Dictionary<int, ITunnel> HostedServers = new Dictionary<int, ITunnel>();
        static readonly object _locker = new object();

        public GlobalTunnelRouter()
        {

        }

        public int InitializeTunnel<T>(T tunnel) where
            T : ITunnel
        {
            lock (_locker)
            {
                foreach (var port in AllowedPorts)
                {
                    //if (!ConnectedClients.ContainsKey(port))
                    //{
                    // ConnectedClients.Add(port, client);
                    HostedServers.Add(port, tunnel);
                    tunnel.StartListening(port);
                    return port;
                    //  }
                }
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
