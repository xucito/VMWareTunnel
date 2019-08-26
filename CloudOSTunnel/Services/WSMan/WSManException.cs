using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CloudOSTunnel.Services.WSMan
{
    public class WSManException : Exception
    {
        public WSManException() { }

        public WSManException(string msg) : base(msg) { }

        public WSManException(string msg, Exception innerException) : base(msg, innerException) { }
    }
}
