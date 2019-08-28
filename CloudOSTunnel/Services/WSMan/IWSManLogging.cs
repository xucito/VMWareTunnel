using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace CloudOSTunnel.Services.WSMan
{
    public interface IWSManLogging<T>
    {
        ILogger<T> Logger { get; }

        string Id { get; }

        void LogInformation(string msg);

        void LogDebug(string msg);

        void LogWarning(string msg);

        void LogError(string msg);
    }
}
