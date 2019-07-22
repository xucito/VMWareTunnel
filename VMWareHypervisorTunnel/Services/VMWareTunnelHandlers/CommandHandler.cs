using FxSsh.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Web;
using VMWareHypervisorTunnel.Clients;
using FxSsh;

namespace VMWareHypervisorTunnel.Services.VMWareTunnelHandlers
{
    public class CommandHandler: TunnelHandler
    {
        string consoleOutput = "";
        object _sender;
        CommandRequestedArgs _session;
        string command = "";
        VMWareClient _client;
        static string mode;

        string _type = "";

        public CommandHandler(string type, VMWareClient client)
        {
            _type = type;
            _client = client; 
        }

        public override event EventHandler<byte[]> DataReceived;
        public override event EventHandler EofReceived;
        public override event EventHandler<uint> CloseReceived;

        public List<byte> cachedFile = new List<byte>();
        public List<byte> currentCommand = new List<byte>();

        public override void OnData(byte[] data, string type = "")
        {
            if (data.Length == 1)
            {
                var loop = 0;
                System.Diagnostics.Debug.WriteLine("Received Write");
                if (data.Count() == 1)
                {
                    if (data.Last() == 13)
                    {
                        System.Diagnostics.Debug.WriteLine("Detected execution request");
                    }
                    else if (data.Last() == 8)
                    {
                        System.Diagnostics.Debug.WriteLine("Detected Backspace");

                        if (currentCommand.Count() > 0)
                        {
                            currentCommand.RemoveAt(currentCommand.Count() - 1);
                        }
                    }
                }
                currentCommand.AddRange(data);
                DataReceived?.Invoke(this, Encoding.ASCII.GetBytes("\0"));
                System.Diagnostics.Debug.WriteLine("Current Command: " + System.Text.Encoding.UTF8.GetString(currentCommand.ToArray()));
            }
            else
            {
                var result = _client.ExecuteCommand(System.Text.Encoding.UTF8.GetString(data));
                result = result.Replace("Connection to 127.0.0.1 closed.", "");
                result = result.Trim(new char[] { '\n' , '\r'});
                System.Diagnostics.Debug.WriteLine("Got result: " + result);
                DataReceived?.Invoke(this, Encoding.ASCII.GetBytes(result));
                EofReceived?.Invoke(this, EventArgs.Empty);
                CloseReceived?.Invoke(this, 0);
            }
            return;
        }

        public override void OnClose()
        {

            System.Diagnostics.Debug.WriteLine("Detected close");
            //CloseReceived?.Invoke(this, 0);
        }

        public override void Print(string msg)
        {
            System.Diagnostics.Debug.WriteLine(msg);
        }

        public override void SetType(string type)
        {
            _type = type;
        }

        public override void OnEndOfFile()
        {
            System.Diagnostics.Debug.WriteLine("Detected end of file");
        }



    }
}