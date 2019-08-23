using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

namespace CloudOSTunnel.Services.Handlers
{
    public abstract class TunnelHandler
    {
        public abstract void OnData(byte[] data, string type = "");
        public abstract void OnClose();
        public abstract void Print(string msg);
        public abstract void SetType(string type);
        public abstract void OnEndOfFile();
        public abstract event EventHandler<byte[]> DataReceived;
        public abstract event EventHandler EofReceived;
        public abstract event EventHandler<uint> CloseReceived;
    }
}
