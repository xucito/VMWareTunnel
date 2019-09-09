using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text;
using System.Xml;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using CloudOSTunnel.Clients;

namespace CloudOSTunnel.Services.WSMan
{
    public class WSManServer : ITunnel, IDisposable
    {
        #region Configuration
        private readonly bool useSsl;
        private readonly string certPath;
        private readonly string certPassword;
        #endregion Configuration

        // vCenter client
        public VMWareClient Client { get; private set; }

        // WSMan runtime dictionary: command id as key
        private Dictionary<string, WSManRuntime> runtime;
        // Signal stopped WSMan server
        public bool IsStopped { get; private set; }

        private readonly ILoggerFactory loggerFactory;
        private IWebHost host;
        private int port;
        private string proto;

        #region Logging
        // ID for logging e.g. https://*:59860
        public string Id
        {
            get { return string.Format("{0}://{1}:{2}", proto, "*", port); }
        }

        public ILogger<WSManServer> Logger { get; private set; }

        public void LogInformation(string msg)
        {
            Logger.LogInformation(string.Format("{0} {1}", Id, msg));
        }

        public void LogDebug(string msg)
        {
            Logger.LogDebug(string.Format("{0} {1}", Id, msg));
        }

        public void LogWarning(string msg)
        {
            Logger.LogWarning(string.Format("{0} {1}", Id, msg));
        }

        public void LogError(string msg)
        {
            Logger.LogError(string.Format("{0} {1}", Id, msg));
        }
        #endregion Logging

        #region ITunnel
        public string Hostname { get { return Client.HostName; } }
        public string Reference { get { return Client.MoRef; } }

        public void Shutdown()
        {
            if (IsStopped)
            {
                LogWarning("Already shut down");
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

        public void StartListening(int port)
        {
            this.port = port;
            this.proto = useSsl ? "https" : "http";

            // Create web host
            this.host = new WebHostBuilder()
                .ConfigureServices(services =>
                {
                    // Inject WSManHandler object for web host to use
                    services.AddSingleton(
                        new WSManHandler(loggerFactory, this, proto, port, Client.VmUser, Client.VmPassword));
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

            if (IsStopped)
            {
                throw new WSManException("Unable to start a wsman server again after it has been stopped");
            }

            // Indicate server started
            IsStopped = false;

            // Start web host
            host.Start();
        }
        #endregion ITunnel

        public WSManServer(ILoggerFactory loggerFactory, IConfiguration configuration, VMWareClient client)
        {
            this.useSsl = configuration.GetSection("WSManServer").GetValue<bool>("UseSsl");
            this.certPath = configuration.GetSection("WSManServer").GetValue<string>("CertPath");
            this.certPassword = configuration.GetSection("WSManServer").GetValue<string>("CertPassword");

            this.Logger = loggerFactory.CreateLogger<WSManServer>();
            this.loggerFactory = loggerFactory;
            this.Client = client;

            this.IsStopped = false;
            this.runtime = new Dictionary<string, WSManRuntime>();
        }

        /// <summary>
        /// Handle Wsman protocol request 
        /// </summary>
        /// <param name="xml">XML request</param>
        /// <returns>Wsman response</returns>
        public async Task<string> HandleWsman(XmlDocument xml)
        {
            string response;
            // <w:ResourceURI mustUnderstand="true">http://schemas.microsoft.com/wbem/wsman/1/windows/shell/cmd</w:ResourceURI>
            string resourceUri = xml.GetElementsByTagName("w:ResourceURI").Item(0).InnerText;
            string action = xml.GetElementsByTagName("a:Action").Item(0).InnerText;

            // Safe guard actions to run command
            var actionToGuard = new List<string>() {
                "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/Command",
                "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/Send",
                "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/Receive"
            };
            if (actionToGuard.Contains(action))
            {
                // Block request while VM is rebooting
                if (Client.IsRebooting)
                {
                    LogWarning(string.Format("VM is rebooting, block wsman action {0}", action));
                    return null;
                }

                // Block request while VM is executing
                if (Client.IsExecuting)
                {
                    LogWarning(string.Format("VM is executing, block wsman action {0}", action));
                    return null;
                }
            }

            if (action == "http://schemas.xmlsoap.org/ws/2004/09/transfer/Create")
            {
                response = WSManRuntime.HandleCreateShellAction(xml, Client.VmUser, Logger);
            }
            else if (action == "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/Command")
            {
                // Generate a command id and create runtime
                string commandId = "" + Guid.NewGuid();
                var wsmanRuntime = new WSManRuntime(loggerFactory, Client, port, commandId, Id);
                runtime.Add(commandId, wsmanRuntime);

                response = wsmanRuntime.HandleExecuteCommandAction(xml);
            }
            else if (action == "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/Send")
            {
                string commandId = xml.GetElementsByTagName("rsp:Stream").Item(0).Attributes["CommandId"].Value;
                response = await runtime[commandId].HandleSendInputAction(xml, port);
            }
            else if (action == "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/Receive")
            {
                string commandId = xml.GetElementsByTagName("rsp:DesiredStream").Item(0).Attributes["CommandId"].Value;
                response = await runtime[commandId].HandleReceiveAction(xml, commandId);
            }
            else if (action == "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/Signal")
            {
                string commandId = xml.GetElementsByTagName("rsp:Signal").Item(0).Attributes["CommandId"].Value;
                // Delete runtime for the command id
                response = runtime[commandId].HandleSignalAction(xml);
                runtime.Remove(commandId);
            }
            else if (action == "http://schemas.xmlsoap.org/ws/2004/09/transfer/Delete")
            {
                response = WSManRuntime.HandleDeleteShellAction(xml, Logger);
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
            runtime.Clear();
        }
    }
}
