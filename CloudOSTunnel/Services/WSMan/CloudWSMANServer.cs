using CloudOSTunnel.Clients;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CloudOSTunnel.Services
{
    public class WSManServer : ITunnel
    {
        private IWebHost host;

        public WSManServer(int port, bool useSsl)
        {
        }

        public string Hostname => throw new NotImplementedException();

        public string Reference => throw new NotImplementedException();

        public void Shutdown()
        {
            throw new NotImplementedException();
        }

        public void StartListening(int port)
        {
            throw new NotImplementedException();
        }
    }
}
