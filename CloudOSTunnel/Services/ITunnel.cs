using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CloudOSTunnel.Services
{
    public interface ITunnel
    {
        void Shutdown();
        string Hostname { get; }
        string Reference { get; }
        void StartListening(int port);
    }
}
