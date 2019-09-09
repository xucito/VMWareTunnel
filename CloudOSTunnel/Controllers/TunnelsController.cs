using System;
using System.Net;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using CloudOSTunnel.Clients;
using CloudOSTunnel.Services;
using CloudOSTunnel.ViewModels;
using CloudOSTunnel.Services.WSMan;

// For more information on enabling Web API for empty projects, visit https://go.microsoft.com/fwlink/?LinkID=397860

namespace CloudOSTunnel.Controllers
{
    [Route("api/[controller]")]
    public class TunnelsController : Controller
    {
        private IConfiguration _configuration;
        private ILoggerFactory _loggerFactory;
        GlobalTunnelRouter _router;
        public TunnelsController(IConfiguration configuration, ILoggerFactory loggerFactory, GlobalTunnelRouter router)
        {
            this._configuration = configuration;
            this._loggerFactory = loggerFactory;
            this._router = router;
        }

        [HttpGet]
        public IActionResult Get()
        {
            var ports = GlobalTunnelRouter.AllowedPorts;

            Dictionary<int, TunnelVM> tunnelDict = new Dictionary<int, TunnelVM>();

            foreach (var port in ports)
            {
                var client = _router.GetServer(port);
                tunnelDict.Add(port, new TunnelVM()
                {
                    HostName = client != null ? client.Hostname : "",
                    Reference = client != null ? client.Reference : "",
                    Port = port,
                    Connected = client != null ? true : false
                });
            }

            return Ok(tunnelDict);
        }

        // GET api/<controller>/5
        [HttpGet("{port}")]
        public IActionResult Get(int port)
        {
            var stopwatch = new Stopwatch();
            stopwatch.Start();

            return Ok(new
            {
                Port = port,
                Hostname = _router.GetServer(port).Hostname
            });
        }

        private static ActionResult Result(HttpStatusCode statusCode, string reason) => new ContentResult
        {
            StatusCode = (int)statusCode,
            Content = $"Status Code: {(int)statusCode}; {statusCode}; {reason}",
            ContentType = "text/plain",
        };

        // POST api/<controller>
        [HttpPost]
        public IActionResult Post([FromBody]PostTunnelRequestVM value)
        {
            if(!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            try
            {
                var stopwatch = new Stopwatch();
                stopwatch.Start();
                VMWareClient client = null;
                
                if ((value.VMName != "" && value.VMName != null))
                {
                    client = new Clients.VMWareClient(_loggerFactory, value.ServiceUrl, value.VCenterUsername, value.VCenterPassword, value.OSUsername, value.OSPassword, value.VMName, value.MoRef);
                }
                else if (value.MoRef != "" && value.MoRef != null)
                {
                    client = new Clients.VMWareClient(_loggerFactory, value.ServiceUrl, value.VCenterUsername, value.VCenterPassword, value.OSUsername, value.OSPassword, value.MoRef);
                }
                else
                {
                    return BadRequest("Please specify moref, name or both.");
                }

                //Determine which client to use

                //if linux
                if (!client.GuestFamily.ToLower().Contains("window"))
                {
                    var server = new CloudSSHServer(_loggerFactory, client);
                    return Ok(new
                    {
                        Port = _router.InitializeTunnel(server),
                        MoRef = client.MoRef,
                        Hostname = client.HostName,
                        ElapsedMs = stopwatch.ElapsedMilliseconds,
                        TempFilesPath = client.TempPath,
                        PublicKey = client.PublicKey,
                        PrivateKeyLocation = client.PrivateFileLocation,
                        FullVMName = client.FullVMName,
                        GuestFamily = client.GuestFamily
                    });
                }
                //Assume windows
                else
                {
                    var wsmanServer = new WSManServer(_loggerFactory, _configuration, client);

                    return Ok(new
                    {
                        Port = _router.InitializeTunnel(wsmanServer),
                        MoRef = client.MoRef,
                        Hostname = client.HostName,
                        ElapsedMs = stopwatch.ElapsedMilliseconds,
                        FullVMName = client.FullVMName,
                        GuestFamily = client.GuestFamily
                    });
                }
            }
            catch (Exception e)
            {
                if (e.Message.Contains("Failed to authenticate"))
                {
                    return Unauthorized();
                }
                else
                {
                    return Result(HttpStatusCode.InternalServerError, e.Message);
                }
            }

        }

        // PUT api/<controller>/5
        /*public void Put(int id, [FromBody]string value)
        {
        }*/

        // DELETE api/<controller>/5
        [HttpDelete("{port}")]
        public IActionResult Delete(int port)
        {
            if (_router.GetServer(port) != null)
            {
                _router.DisconnectClient(port);
                return Ok();
            }
            else
                return BadRequest();
        }
    }
}
