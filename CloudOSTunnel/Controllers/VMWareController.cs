﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CloudOSTunnel.Clients;
using CloudOSTunnel.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

// For more information on enabling MVC for empty projects, visit https://go.microsoft.com/fwlink/?LinkID=397860

namespace CloudOSTunnel.Controllers
{
    [Route("api/[controller]")]
    public class VMWareController : Controller
    {
        private ILoggerFactory _logger;
        public VMWareController(ILoggerFactory logger)
        {
            _logger = logger;
        }

        [HttpPost]
        [Route("file-exists")]
        public IActionResult FileExists([FromBody] GetVMWareFileExists request)
        {
            var client = new VMWareClient(_logger, request.ServiceUrl, request.VCenterUsername, request.VCenterPassword, request.OSUsername, request.OSPassword, request.MoRef);
            var result = client.FileExist(request.FolderPath, request.FileName);
            client.Logout();
            return Ok(new
            {
                Exists = result
            });
        }

        [HttpPost]
        [Route("guest-os")]
        public IActionResult GetGuestOs([FromBody] GetVMWareGuestOs request)
        {
            var client = new VMWareClient(_logger, request.ServiceUrl, request.VCenterUsername, request.VCenterPassword, request.OSUsername, request.OSPassword, request.MoRef);
            var result = client.GetGuestOs();
            client.Logout();
            return Ok(new
            {
                GuestOs = result
            });
        }

        [HttpPost]
        [Route("tunnel-user")]
        public async Task<IActionResult> AddTunnelUser([FromBody] PostTunnelUserVM request)
        {
            var client = new VMWareClient(_logger, request.ServiceUrl, request.VCenterUsername, request.VCenterPassword, request.OSUsername, request.OSPassword, request.MoRef);
            var result = await client.AddTunnelUser(request.NewUsername, request.NewPassword);
            client.Logout();
            return Ok(new
            {
                successful = result
            });
        }
    }
}
