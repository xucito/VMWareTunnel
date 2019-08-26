using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using VMware.Vim;
using CloudOSTunnel.Services.WSMan;

namespace CloudOSTunnel.Clients
{
    public partial class VMWareClient : IDisposable, IWSManLogging
    {
        public void Dispose()
        {
            // Minimally erase credential
            _executingCredentials = null;
        }

        public string VmUser
        {
            get { return _executingCredentials.Username; }
        }

        public string VmPassword
        {
            get { return _executingCredentials.Password; }
        }

        #region Logging
        private ILogger<VMWareClient> logger;
        public string Id
        {
            get
            {
                return string.Format("{0}:{1}", _serviceUrl, FullVMName);
            }
        }
        public void LogInformation(string msg)
        {
            logger.LogInformation(string.Format("{0} {1}", Id, msg));
        }

        public void LogWarning(string msg)
        {
            logger.LogWarning(string.Format("{0} {1}", Id, msg));
        }

        public void LogDebug(string msg)
        {
            logger.LogDebug(string.Format("{0} {1}", Id, msg));
        }

        public void LogError(string msg)
        {
            logger.LogError(string.Format("{0} {1}", Id, msg));
        }
        #endregion Logging
    }
}
