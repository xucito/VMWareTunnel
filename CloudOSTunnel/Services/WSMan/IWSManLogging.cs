using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CloudOSTunnel.Services.WSMan
{
    public interface IWSManLogging
    {
        string Id { get; }

        void LogInformation(string msg);

        void LogDebug(string msg);

        void LogWarning(string msg);

        void LogError(string msg);
    }
}
