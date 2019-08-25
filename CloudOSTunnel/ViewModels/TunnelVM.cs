using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

namespace CloudOSTunnel.ViewModels
{
    public class TunnelVM
    {
        public int Port { get; set; }
        public string HostName { get; set; }
        public string Reference { get; set; }
        public bool Connected { get; set; }
    }
}