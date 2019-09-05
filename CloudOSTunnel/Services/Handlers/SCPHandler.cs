using CloudOSTunnel.Clients;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CloudOSTunnel.Services.Handlers
{
    public class SCPHandler: TunnelHandler
    {
        string consoleOutput = "";
        object _sender;
        string command = "";
        VMWareClient _client;
        public override event EventHandler<byte[]> DataReceived;
        public override event EventHandler EofReceived;
        public override event EventHandler<uint> CloseReceived;
        public List<byte> cachedFile = new List<byte>();
        int loop = 0;

        string scpPath = "";
        string _type = "";

        public SCPHandler(string type, VMWareClient client)
        {
            _type = type;
            _client = client; ;
        }

        public override void OnData(byte[] data, string type = "")
        {
            System.Diagnostics.Debug.WriteLine("Received Write");
            DataReceived?.Invoke(this, Encoding.ASCII.GetBytes("\0"));

            if (loop > 1)
            {
                cachedFile.AddRange(data);
            }

            if (loop == 0)
            {
                var command = System.Text.Encoding.UTF8.GetString(data);
                //set path
                scpPath = command.Split(' ').Last();
            }

            loop++;
            System.Diagnostics.Debug.WriteLine("Loop " + loop);
        }

        public override void OnClose()
        {
        }

        public override void Print(string msg)
        {
        }

        public override void SetType(string type)
        {
        }

        public override void OnEndOfFile()
        {
            // A 0 is appended on the end causing a mismatching file.
            cachedFile = cachedFile.Take(cachedFile.Count() - 1).ToList();
            System.Diagnostics.Debug.WriteLine("Last " + cachedFile.Last());
            _client.UploadFile(scpPath, cachedFile.ToArray()).GetAwaiter().GetResult();
            DataReceived?.Invoke(this, Encoding.ASCII.GetBytes(scpPath));
            EofReceived?.Invoke(this, EventArgs.Empty);
            CloseReceived?.Invoke(this, 0);
            return;
        }
    }
}
