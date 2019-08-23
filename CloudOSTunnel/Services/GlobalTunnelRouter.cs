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
            5001,
            5002,
            5003
        };

        private static Dictionary<int, VMWareClient> ConnectedClients = new Dictionary<int, VMWareClient>();
        private static Dictionary<int, CloudSSHServer> HostedServers = new Dictionary<int, CloudSSHServer>();
        static readonly object _locker = new object();

        public static int InitializeTunnel(VMWareClient client)
        {
            lock (_locker)
            {
                foreach (var port in AllowedPorts)
                {
                    if (!ConnectedClients.ContainsKey(port))
                    {
                        ConnectedClients.Add(port, client);
                        HostedServers.Add(port, new CloudSSHServer(port));
                        return port;
                    }
                }
                throw new Exception("No available ports.");
            }
        }


        public static VMWareClient GetClient(int port)
        {
            if (ConnectedClients.ContainsKey(port))
            {
                return ConnectedClients[port];
            }
            return null;
        }

        public static CloudSSHServer GetSSHServer(int port)
        {
            if (HostedServers.ContainsKey(port))
            {
                return HostedServers[port];
            }
            return null;
        }

        public static void DisconnectClient(int port)
        {
            lock (_locker)
            {
                HostedServers[port].Shutdown();
                ConnectedClients[port].Logout();
                HostedServers.Remove(port);
                ConnectedClients.Remove(port);
            }
        }
    }
}
