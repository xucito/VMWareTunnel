using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Web.Http;
using VMWareHypervisorTunnel.Clients;
using VMWareHypervisorTunnel.Services;
using VMWareHypervisorTunnel.ViewModels;

namespace VMWareHypervisorTunnel.Controllers
{
    public class TunnelsController : ApiController
    {
        [HttpGet]
        public HttpResponseMessage Get()
        {
            var ports = GlobalTunnelRouter.AllowedPorts;

            Dictionary<int, TunnelVM> tunnelDict = new Dictionary<int, TunnelVM>();

            foreach (var port in ports)
            {
                var client = GlobalTunnelRouter.GetClient(port);
                tunnelDict.Add(port, new TunnelVM()
                {
                    HostName = client != null ? client.HostName : "",
                    MoRef = client != null ? client.MoRef : "",
                    Port = port,
                    Connected = client != null ? true : false
                });
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK);

            response.Content = new StringContent(JsonConvert.SerializeObject(tunnelDict),
                System.Text.Encoding.UTF8,
                "application/json");

            return response;
        }

        // GET api/<controller>/5
        [HttpGet]
        public HttpResponseMessage Get(int id)
        {
            var stopwatch = new Stopwatch();
            stopwatch.Start();

            var response = new HttpResponseMessage(HttpStatusCode.OK);

            response.Content = new StringContent(JsonConvert.SerializeObject(new
            {
                Port = id,
                Hostname = GlobalTunnelRouter.GetClient(id).HostName
            }), System.Text.Encoding.UTF8, "application/json");

            return response;
        }

        // POST api/<controller>
        [HttpPost]
        public HttpResponseMessage Post([FromBody]PostTunnelRequestVM value)
        {
            try
            {
                var stopwatch = new Stopwatch();
                stopwatch.Start();
                VMWareClient client = null;
                var response = new HttpResponseMessage(HttpStatusCode.OK);

                if ((value.VMName != "" && value.VMName != null))
                {
                    client = new Clients.VMWareClient(value.ServiceUrl, value.VCenterUsername, value.VCenterPassword, value.VMRootUsername, value.VMRootPassword, value.VMName, value.MoRef, value.VMExecutingUsername, value.VMExecutingPassword);
                }
                else if(value.MoRef != "" && value.MoRef != null)
                {
                    client = new Clients.VMWareClient(value.ServiceUrl, value.VCenterUsername, value.VCenterPassword, value.VMRootUsername, value.VMRootPassword, value.MoRef, value.VMExecutingUsername, value.VMExecutingPassword);
                }
                else
                {
                    response.StatusCode = HttpStatusCode.BadRequest;
                    response.Content = new StringContent("Please specify moref, name or both.");
                    return response;
                }

                response = new HttpResponseMessage(HttpStatusCode.OK);

                response.Content = new StringContent(JsonConvert.SerializeObject(new
                {
                    Port = GlobalTunnelRouter.InitializeTunnel(client),
                    MoRef = client.MoRef,
                    Hostname = client.HostName,
                    ElapsedMs = stopwatch.ElapsedMilliseconds,
                    TempFilesPath = client.TempPath,
                    PublicKey = client.PublicKey,
                    PrivateKeyLocation = client.PrivateFileLocation,
                    FullVMName = client.FullVMName
                }), System.Text.Encoding.UTF8, "application/json");

                return response;
            }
            catch (Exception e)
            {

                var response = new HttpResponseMessage();
                if (e.Message.Contains("Failed to authenticate"))
                {
                    response.StatusCode = HttpStatusCode.Unauthorized;
                }
                else
                {
                    response.StatusCode = HttpStatusCode.BadRequest;
                }
                response.Content = new StringContent(JsonConvert.SerializeObject(new ExceptionResult()
                {
                    Message = e.Message,
                    ExceptionName = e.GetType().Name
                }), System.Text.Encoding.UTF8, "application/json");

                return response;
            }

        }

        // PUT api/<controller>/5
        /*public void Put(int id, [FromBody]string value)
        {
        }*/

        // DELETE api/<controller>/5
        [HttpDelete]
        public void Delete(int id)
        {
            if (GlobalTunnelRouter.GetClient(id) != null)
                GlobalTunnelRouter.DisconnectClient(id);
        }
    }
}