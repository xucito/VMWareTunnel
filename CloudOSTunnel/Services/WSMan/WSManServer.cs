using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Text;
using System.Xml;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;
using CloudOSTunnel.Clients;

namespace CloudOSTunnel.Services.WSMan
{
    public class WSManServer : ITunnel, IDisposable, IWSManLogging
    {
        // Shared client to access vCenter
        public VMWareClient Client { get; private set; }

        private Dictionary<string, WSManRuntime> runtimes;
        // Used only when assigning shell id
        private readonly string vmUser;
        // Used to validate web request credential header
        private readonly string vmAuthCode;

        // Signal stopped server (used by WsmanServerManager for cleanup)
        public bool IsStopped { get; private set; }

        private readonly ILoggerFactory loggerFactory;
        private readonly ILogger<WSManServer> logger;
        private readonly IWebHost host;
        private readonly int port;
        private readonly string proto;
        private readonly bool debug;

        public WSManServer(ILoggerFactory loggerFactory, VMWareClient client, int port, bool useSsl,
            bool debug, string certPath = null, string certPassword = null)
        {
            this.logger = loggerFactory.CreateLogger<WSManServer>();
            this.loggerFactory = loggerFactory;
            this.Client = client;
            this.port = port;
            this.proto = useSsl ? "https" : "http";
            this.debug = debug;
            this.IsStopped = false;
            this.vmAuthCode = Convert.ToBase64String(Encoding.UTF8.GetBytes(client.VmUser + ":" + client.VmPassword));
            this.runtimes = new Dictionary<string, WSManRuntime>();

            // Create web host
            this.host = new WebHostBuilder()
                .ConfigureServices(services =>
                {
                    // Inject WSManServer object
                    services.Add(ServiceDescriptor.Singleton(typeof(WSManServer), this));
                })
                .UseKestrel(options =>
                {
                    options.ListenAnyIP((int)port, listenOptions =>
                    {
                        if (useSsl)
                        {
                            LogInformation(string.Format("Configuring https web host on port {0}", port));
                            listenOptions.UseHttps(certPath, certPassword);
                        }
                        else
                        {
                            LogInformation(string.Format("Configuring http web host on port {0}", port));
                        }
                    });
                })
                .UseStartup<WSManStartup>()
                .Build();
        }

        #region TO_BE_IMPLEMENTED
        public string Hostname => throw new NotImplementedException();

        public string Reference => throw new NotImplementedException();

        public void Shutdown()
        {
            throw new NotImplementedException();
        }

        public void StartListening(int port)
        {
            throw new NotImplementedException();
        }
        #endregion TO_BE_IMPLEMENTED

        /// <summary>
        /// Validate guest credential by comparing the Base64 encdoed username and password  
        /// </summary>
        /// <param name="authCode">Base64 encoded authorization code</param>
        /// <returns></returns>
        private bool ValidateGuestCredential(string authCode)
        {
            return authCode == this.vmAuthCode;
        }

        #region Logging
        // ID for logging e.g. https://*:59860 (for server level logging)
        public string Id
        {
            get { return string.Format("{0}://{1}:{2}", proto, "*", port); }
        }

        public void LogInformation(string msg)
        {
            logger.LogInformation(string.Format("{0} {1}", Id, msg));
        }

        public void LogDebug(string msg)
        {
            logger.LogDebug(string.Format("{0} {1}", Id, msg));
        }

        public void LogWarning(string msg)
        {
            logger.LogWarning(string.Format("{0} {1}", Id, msg));
        }

        public void LogError(string msg)
        {
            logger.LogError(string.Format("{0} {1}", Id, msg));
        }
        #endregion Logging

        /// <summary>
        /// Start server
        /// </summary>
        public void Start()
        {
            if (IsStopped)
            {
                throw new WSManException("Unable to start a wsman server again after it has been stopped");
            }

            // Indicate server started
            IsStopped = false;

            // Start web host
            host.Start();
        }

        /// <summary>
        /// Stop server
        /// </summary>
        public void Stop()
        {
            if (IsStopped)
            {
                LogWarning("Already stopped");
            }
            else
            {
                // Stop web host
                host.StopAsync().Wait();

                lock (this)
                {
                    // Indicate server stopped for removal
                    IsStopped = true;
                }

                // Fast dispose to avoid processing queued requests
                Dispose();
            }
        }

        /// <summary>
        /// Handle web request
        /// </summary>
        /// <param name="context">HttpContext</param>
        /// <returns>Async task</returns>
        public async Task HandleRequest(HttpContext context)
        {
            try
            {
                var request = context.Request;
                var response = context.Response;
                var body = "";

                // Read request as string
                using (StreamReader reader = new StreamReader(request.Body, Encoding.UTF8, true))
                {
                    body = reader.ReadToEnd();
                }

                // Convert request to xml
                var xml = new XmlDocument();
                xml.LoadXml(body);

                // Authorization: Basic XXXXXXXXXXXXXXXXXXXX
                var authType = request.Headers["Authorization"].First().Split(" ")[0];
                var authCode = request.Headers["Authorization"].First().Split(" ")[1];
                if (authType == "Basic" && ValidateGuestCredential(authCode))
                {
                    var xmlResponse = HandleWsman(xml);
                    if (xmlResponse != null)
                    {
                        response.ContentType = "application/soap+xml;charset=UTF-8";
                        response.StatusCode = (int)HttpStatusCode.OK;
                        await response.WriteAsync(xmlResponse, Encoding.UTF8);
                    }
                }
                else
                {
                    response.StatusCode = (int)HttpStatusCode.Unauthorized;
                }
            }
            catch (Exception ex)
            {
                LogError(string.Format("Exception while handing request: {0}", ex));
                LogError("Stopping server for safety");
                Stop();
            }
        }

        /// <summary>
        /// Handle Wsman protocol request 
        /// </summary>
        /// <param name="xml">XML request</param>
        /// <returns>Wsman response</returns>
        public string HandleWsman(XmlDocument xml)
        {
            string response;
            // <w:ResourceURI mustUnderstand="true">http://schemas.microsoft.com/wbem/wsman/1/windows/shell/cmd</w:ResourceURI>
            string resourceUri = xml.GetElementsByTagName("w:ResourceURI").Item(0).InnerText;
            string action = xml.GetElementsByTagName("a:Action").Item(0).InnerText;

            if (action == "http://schemas.xmlsoap.org/ws/2004/09/transfer/Create")
            {
                response = WSManRuntime.HandleCreateShellAction(xml, vmUser, this);
            }
            else if (action == "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/Command")
            {
                // Generate a command id and create runtime
                string commandId = "" + Guid.NewGuid();
                var wsmanRuntime = new WSManRuntime(loggerFactory, Client, commandId, Id);
                runtimes.Add(commandId, wsmanRuntime);

                response = wsmanRuntime.HandleExecuteCommandAction(xml);
            }
            else if (action == "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/Send")
            {
                string commandId = xml.GetElementsByTagName("rsp:Stream").Item(0).Attributes["CommandId"].Value;
                response = runtimes[commandId].HandleSendInputAction(xml, port);
            }
            else if (action == "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/Receive")
            {
                string commandId = xml.GetElementsByTagName("rsp:DesiredStream").Item(0).Attributes["CommandId"].Value;
                response = runtimes[commandId].HandleReceiveAction(xml, commandId);
            }
            else if (action == "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/Signal")
            {
                string commandId = xml.GetElementsByTagName("rsp:Signal").Item(0).Attributes["CommandId"].Value;
                // Delete runtime for the command id
                runtimes.Remove(commandId);
                response = WSManRuntime.HandleSignalAction(xml, this);
            }
            else if (action == "http://schemas.xmlsoap.org/ws/2004/09/transfer/Delete")
            {
                response = WSManRuntime.HandleDeleteShellAction(xml, this);
            }
            else
            {
                throw new InvalidOperationException(string.Format("Unknown wsman action {0}", action));
            }

            return response;
        }

        /// <summary>
        /// Dispose WsmanServer resources
        /// </summary>
        public void Dispose()
        {
            Client.Dispose();
            host.Dispose();

            // Delete all runtime commands
            runtimes.Clear();
        }
    }
}
