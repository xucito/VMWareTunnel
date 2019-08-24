using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

namespace CloudOSTunnel.ViewModels
{
    public class PostTunnelRequestVM
    {
        public string VMName { get; set; }
        public string MoRef { get; set; }
        public string ServiceUrl { get; set; }
        public string VCenterUsername { get; set; }
        public string VCenterPassword { get; set; }
        public string VMRootUsername { get; set; }
        public string VMRootPassword { get; set; }
        public string VMExecutingUsername { get; set; }
        public string VMExecutingPassword { get; set; }
    }
}