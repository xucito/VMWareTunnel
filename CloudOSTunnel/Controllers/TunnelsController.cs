using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CloudOSTunnel.Clients;
using CloudOSTunnel.Services;
using CloudOSTunnel.ViewModels;
using Microsoft.AspNetCore.Mvc;

// For more information on enabling Web API for empty projects, visit https://go.microsoft.com/fwlink/?LinkID=397860

namespace CloudOSTunnel.Controllers
{
    [Route("api/[controller]")]
    public class TunnelsController : Controller
    {
        GlobalTunnelRouter _router;
        public TunnelsController(GlobalTunnelRouter router)
        {
            _router = router;
        }

        [HttpGet]
        public IActionResult Get()
        {
            var ports = GlobalTunnelRouter.AllowedPorts;

            Dictionary<int, TunnelVM> tunnelDict = new Dictionary<int, TunnelVM>();

            foreach (var port in ports)
            {
                var client = _router.GetSSHServer(port)?.VMWareClient;
                tunnelDict.Add(port, new TunnelVM()
                {
                    HostName = client != null ? client.HostName : "",
                    MoRef = client != null ? client.MoRef : "",
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
                Hostname = _router.GetSSHServer(port).VMWareClient.HostName
            });
        }

        // POST api/<controller>
        [HttpPost]
        public IActionResult Post([FromBody]PostTunnelRequestVM value)
        {
            try
            {
                var stopwatch = new Stopwatch();
                stopwatch.Start();
                VMWareClient client = null;
                
                if ((value.VMName != "" && value.VMName != null))
                {
                    client = new Clients.VMWareClient(value.ServiceUrl, value.VCenterUsername, value.VCenterPassword, value.VMRootUsername, value.VMRootPassword, value.VMName, value.MoRef, value.VMExecutingUsername, value.VMExecutingPassword);
                }
                else if (value.MoRef != "" && value.MoRef != null)
                {
                    client = new Clients.VMWareClient(value.ServiceUrl, value.VCenterUsername, value.VCenterPassword, value.VMRootUsername, value.VMRootPassword, value.MoRef, value.VMExecutingUsername, value.VMExecutingPassword);
                }
                else
                {
                    return BadRequest("Please specify moref, name or both.");
                }
                

                return Ok(new
                {
                    Port = _router.InitializeTunnel(client),
                    MoRef = client.MoRef,
                    Hostname = client.HostName,
                    ElapsedMs = stopwatch.ElapsedMilliseconds,
                    TempFilesPath = client.TempPath,
                    PublicKey = client.PublicKey,
                    PrivateKeyLocation = client.PrivateFileLocation,
                    FullVMName = client.FullVMName
                });
            }
            catch (Exception e)
            {
                if (e.Message.Contains("Failed to authenticate"))
                {
                    return Unauthorized();
                }
                else
                {
                    return BadRequest(new ExceptionResult()
                    {
                        Message = e.Message,
                        ExceptionName = e.GetType().Name
                    });
                }
            }

        }

        // PUT api/<controller>/5
        /*public void Put(int id, [FromBody]string value)
        {
        }*/

        // DELETE api/<controller>/5
        [HttpDelete("{port}")]
        public void Delete(int port)
        {
            if (_router.GetSSHServer(port) != null)
                _router.DisconnectClient(port);
        }
    }
}
