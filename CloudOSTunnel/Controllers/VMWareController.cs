using System;
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
            var result = client.DoesFileExist(request.FolderPath, request.FileName);
            return Ok(new
            {
                Exists = result
            });
        }
    }
}
