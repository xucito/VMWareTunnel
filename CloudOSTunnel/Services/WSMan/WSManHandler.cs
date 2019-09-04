using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Xml;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using CloudOSTunnel.Clients;

namespace CloudOSTunnel.Services.WSMan
{
    public class WSManHandler
    {
        private WSManServer wsmanServer;
        private readonly string proto;
        private readonly int port;
        private readonly string authCode;

        public WSManHandler(ILoggerFactory loggerFactory, WSManServer wsmanServer, string proto, int port, string vmUser, string vmPassword)
        {
            this.Logger = loggerFactory.CreateLogger<WSManHandler>();
            this.wsmanServer = wsmanServer;
            this.proto = proto;
            this.port = port;
            this.authCode = Convert.ToBase64String(Encoding.UTF8.GetBytes(vmUser + ":" + vmPassword));
        }

        #region Logging
        public string Id
        {
            get { return string.Format("{0}://{1}:{2}", proto, "*", port); }
        }

        public ILogger<WSManHandler> Logger { get; private set; }

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

        /// <summary>
        /// Validate guest credential by comparing the Base64 encdoed username and password  
        /// </summary>
        /// <param name="authCode">Base64 encoded authorization code</param>
        /// <returns></returns>
        private bool ValidateGuestCredential(string authCode)
        {
            return authCode == this.authCode;
        }

        /// <summary>
        /// Handle web request
        /// </summary>
        /// <param name="context">HttpContext</param>
        /// <returns>Async task</returns>
        public async Task HandleRequest(HttpContext context)
        {
            void ExtractAuthorization(HttpRequest request, out string authType, out string authCode)
            {
                var split = request.Headers["Authorization"].First().Split(" ");
                authType = split[0];
                authCode = split[1];
            }
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
                string authType, authCode;
                ExtractAuthorization(request, out authType, out authCode);
                if (authType == "Basic" && ValidateGuestCredential(authCode))
                {
                    var xmlResponse = await wsmanServer.HandleWsman(xml);
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
                LogError("Stopping wsman server for safety");
                wsmanServer.Shutdown();
            }
        }
    }
}
