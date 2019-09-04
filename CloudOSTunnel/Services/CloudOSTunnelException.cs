using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CloudOSTunnel.Services
{
    public class CloudOSTunnelException : Exception
    {
        public CloudOSTunnelException() { }

        public CloudOSTunnelException(string msg) : base(msg) { }

        public CloudOSTunnelException(string msg, Exception innerException) : base(msg, innerException) { }
    }
}
