using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;

namespace CloudOSTunnel.ViewModels
{
    public class PostTunnelUserVM
    {
        [Required]
        public string ServiceUrl { get; set; }
        [Required]
        public string VCenterUsername { get; set; }
        [Required]
        public string VCenterPassword { get; set; }
        [Required]
        public string MoRef { get; set; }
        [Required]
        public string NewUsername { get; set; }
        [Required]
        public string NewPassword { get; set; }
        [Required]
        public string OSUsername { get; set; }
        [Required]
        public string OSPassword { get; set; }
    }
}
