using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VMWareHypervisorTunnel.Services;

namespace VMWareHypervisorTunnelConsole
{
    class Program
    {
        static void Main(string[] args)
        {
            VMwareSSHServer server = new VMwareSSHServer(22);
            System.Threading.Tasks.Task.Delay(-1).Wait();
        }
    }
}
