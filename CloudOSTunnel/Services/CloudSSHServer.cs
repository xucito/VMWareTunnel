using CloudOSTunnel.Clients;
using CloudOSTunnel.Services.Handlers;
using CloudOSTunnel.Utilities;
using FxSsh;
using FxSsh.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CloudOSTunnel.Services
{
    public class CloudSSHServer: ITunnel
    {
        static string clientVersion = "";
        static Dictionary<string, TunnelHandler> channels = new Dictionary<string, TunnelHandler>();
        public readonly VMWareClient _client;
        SshServer _server;
        private int _port;
        public VMWareClient VMWareClient { get { return _client; } }
        public string Hostname { get { return _client.HostName; } }
        public string Reference { get { return _client.MoRef; } }

        public CloudSSHServer(VMWareClient client)
        { 
            _client = client;
        }

        public void StartListening(int port)
        {
            _server = new SshServer(new StartingInfo(System.Net.IPAddress.Parse("0.0.0.0"), port, "SSH-2.0-FxSsh"));
            _server.AddHostKey("ssh-rsa", "BwIAAACkAABSU0EyAAQAAAEAAQADKjiW5UyIad8ITutLjcdtejF4wPA1dk1JFHesDMEhU9pGUUs+HPTmSn67ar3UvVj/1t/+YK01FzMtgq4GHKzQHHl2+N+onWK4qbIAMgC6vIcs8u3d38f3NFUfX+lMnngeyxzbYITtDeVVXcLnFd7NgaOcouQyGzYrHBPbyEivswsnqcnF4JpUTln29E1mqt0a49GL8kZtDfNrdRSt/opeexhCuzSjLPuwzTPc6fKgMc6q4MBDBk53vrFY2LtGALrpg3tuydh3RbMLcrVyTNT+7st37goubQ2xWGgkLvo+TZqu3yutxr1oLSaPMSmf9bTACMi5QDicB3CaWNe9eU73MzhXaFLpNpBpLfIuhUaZ3COlMazs7H9LCJMXEL95V6ydnATf7tyO0O+jQp7hgYJdRLR3kNAKT0HU8enE9ZbQEXG88hSCbpf1PvFUytb1QBcotDy6bQ6vTtEAZV+XwnUGwFRexERWuu9XD6eVkYjA4Y3PGtSXbsvhwgH0mTlBOuH4soy8MV4dxGkxM8fIMM0NISTYrPvCeyozSq+NDkekXztFau7zdVEYmhCqIjeMNmRGuiEo8ppJYj4CvR1hc8xScUIw7N4OnLISeAdptm97ADxZqWWFZHno7j7rbNsq5ysdx08OtplghFPx4vNHlS09LwdStumtUel5oIEVMYv+yWBYSPPZBcVY5YFyZFJzd0AOkVtUbEbLuzRs5AtKZG01Ip/8+pZQvJvdbBMLT1BUvHTrccuRbY03SHIaUM3cTUc=");
            _server.AddHostKey("ssh-dss", "BwIAAAAiAABEU1MyAAQAAG+6KQWB+crih2Ivb6CZsMe/7NHLimiTl0ap97KyBoBOs1amqXB8IRwI2h9A10R/v0BHmdyjwe0c0lPsegqDuBUfD2VmsDgrZ/i78t7EJ6Sb6m2lVQfTT0w7FYgVk3J1Deygh7UcbIbDoQ+refeRNM7CjSKtdR+/zIwO3Qub2qH+p6iol2iAlh0LP+cw+XlH0LW5YKPqOXOLgMIiO+48HZjvV67pn5LDubxru3ZQLvjOcDY0pqi5g7AJ3wkLq5dezzDOOun72E42uUHTXOzo+Ct6OZXFP53ZzOfjNw0SiL66353c9igBiRMTGn2gZ+au0jMeIaSsQNjQmWD+Lnri39n0gSCXurDaPkec+uaufGSG9tWgGnBdJhUDqwab8P/Ipvo5lS5p6PlzAQAAACqx1Nid0Ea0YAuYPhg+YolsJ/ce");
            _server.ConnectionAccepted += server_ConnectionAccepted;
            _server.Start();
            _port = port;
        }

        public static string GetChannelId(int port, uint channel)
        {
            return port + ":" + channel;
        }

        public void Shutdown()
        {
            List<string> markForDeletion = new List<string>();

            foreach (var channel in channels)
            {
                if (channel.Key.Contains(_port + ":"))
                {
                    markForDeletion.Add(channel.Key);
                }
            }

            foreach (var deletionKey in markForDeletion)
            {
                channels.Remove(deletionKey);
            }

            try
            {
                _server.Stop();
                //Logout from vmware
            }
            catch(Exception e)
            {
                Console.WriteLine("Failed to stop server with error " + e.Message);
            }

            try
            {
                _client.CleanUpLinuxTempFiles();
                _client.Logout();
            }
            catch(Exception e)
            {

                Console.WriteLine("Failed to stop client with error " + e.Message);
            }
            //_server.Stop();
        }

        void server_ConnectionAccepted(object sender, Session e)
        {
            System.Diagnostics.Debug.WriteLine("Accepted a client.");
            int port = ((StartingInfo)Utility.GetValObjDy(sender, "StartingInfo")).Port;
            e.ServiceRegistered += (ss, ee) => e_ServiceRegistered(ss, ee, port);
            e.KeysExchanged += (ss, ee) => e_KeysExchanged(ss, ee, port);
        }

        private static void e_KeysExchanged(object sender, KeyExchangeArgs e, int port)
        {
            foreach (var keyExchangeAlg in e.KeyExchangeAlgorithms)
            {
                System.Diagnostics.Debug.WriteLine("Key exchange algorithm: {0}", keyExchangeAlg);
            }
        }

        void e_ServiceRegistered(object sender, SshService e, int port)
        {
            var session = (Session)sender;
            System.Diagnostics.Debug.WriteLine("Session {0} requesting {1}.",
                BitConverter.ToString(session.SessionId).Replace("-", ""), e.GetType().Name);
            clientVersion = session.ClientVersion;
            if (e is UserauthService)
            {
                var service = (UserauthService)e;
                service.Userauth += (ss, ee) => service_Userauth(ss, ee, port);
            }
            else if (e is ConnectionService)
            {
                var service = (ConnectionService)e;
                service.CommandOpened += (ss, ee) => service_CommandOpened(ss, ee, port);
                service.EnvReceived += (ss, ee) => service_EnvReceived(ss, ee, port);
                service.PtyReceived += (ss, ee) => service_PtyReceived(ss, ee, port);
                service.TcpForwardRequest += (ss, ee) => service_TcpForwardRequest(ss, ee, port);
            }
        }

        void service_TcpForwardRequest(object sender, TcpRequestArgs e, int port)
        {
            System.Diagnostics.Debug.WriteLine("Received a request to forward data to {0}:{1}", e.Host, e.Port);

            var allow = true;

            if (!allow)
                return;
        }

        void service_PtyReceived(object sender, PtyArgs e, int port)
        {
            System.Diagnostics.Debug.WriteLine("Request to create a PTY received for terminal type {0}", e.Terminal);

            if (channels.ContainsKey(GetChannelId(port, e.Channel.ServerChannelId)))
            {
                channels[GetChannelId(port, e.Channel.ServerChannelId)].SetType("terminal");
            }
            else
            {
                channels.Add(GetChannelId(port, e.Channel.ServerChannelId), new CommandHandler("terminal", _client));
            }

            e.Channel.EofReceived += (ss, ee) => channels[GetChannelId(port, e.Channel.ServerChannelId)].Print("Terminal received EOF");
            e.Channel.CloseReceived += (ss, ee) => channels[GetChannelId(port, e.Channel.ServerChannelId)].OnClose();
            e.Channel.DataReceived += (ss, ee) => channels[GetChannelId(port, e.Channel.ServerChannelId)].Print("Terminal received data");
        }

        void service_EnvReceived(object sender, EnvironmentArgs e, int port)
        {
            System.Diagnostics.Debug.WriteLine("Received environment variable {0}:{1}", e.Name, e.Value);
        }

        void service_Userauth(object sender, UserauthArgs e, int port)
        {
            System.Diagnostics.Debug.WriteLine("Client {0} fingerprint: {1}.", e.KeyAlgorithm, e.Fingerprint);

            e.Result = true;
        }

        void PrintOutput(string output)
        {
            System.Diagnostics.Debug.WriteLine("Foundd output: " + output);
        }

        void service_CommandOpened(object sender, CommandRequestedArgs e, int port)
        {
            System.Diagnostics.Debug.WriteLine($"Channel {e.Channel.ServerChannelId} runs {e.ShellType}: \"{e.CommandText}\".");

            if (!channels.ContainsKey(GetChannelId(port, e.Channel.ServerChannelId)))
            {
                if (e.CommandText.Split(' ').First() == "scp")
                {
                    channels.Add(GetChannelId(port, e.Channel.ServerChannelId), new SCPHandler("scp", _client));
                }
                else
                {
                    channels.Add(GetChannelId(port, e.Channel.ServerChannelId), new CommandHandler("", _client));
                }
            }


            if (e.CommandText != null)
            {
                if (e.CommandText.Split(' ').First() == "scp")
                {
                    channels[GetChannelId(port, e.Channel.ServerChannelId)].EofReceived += (ss, ee) => e.Channel.SendEof();
                    channels[GetChannelId(port, e.Channel.ServerChannelId)].CloseReceived += (ss, ee) => e.Channel.SendClose(ee);
                    channels[GetChannelId(port, e.Channel.ServerChannelId)].DataReceived += (ss, ee) => e.Channel.SendData(ee);
                    e.Channel.EofReceived += (ss, ee) => channels[GetChannelId(port, e.Channel.ServerChannelId)].OnEndOfFile();
                    e.Channel.CloseReceived += (ss, ee) => channels[GetChannelId(port, e.Channel.ServerChannelId)].OnClose();
                    e.Channel.DataReceived += (ss, ee) => channels[GetChannelId(port, e.Channel.ServerChannelId)].OnData(ee);
                    if (e.CommandText != null)
                    {
                        channels[GetChannelId(port, e.Channel.ServerChannelId)].OnData(Encoding.ASCII.GetBytes(e.CommandText));
                    }
                }
                else
                {
                    channels[GetChannelId(port, e.Channel.ServerChannelId)].EofReceived += (ss, ee) => e.Channel.SendEof();
                    channels[GetChannelId(port, e.Channel.ServerChannelId)].CloseReceived += (ss, ee) => e.Channel.SendClose(ee);
                    channels[GetChannelId(port, e.Channel.ServerChannelId)].DataReceived += (ss, ee) => e.Channel.SendData(ee);
                    e.Channel.EofReceived += (ss, ee) => channels[GetChannelId(port, e.Channel.ServerChannelId)].OnEndOfFile();
                    e.Channel.CloseReceived += (ss, ee) => channels[GetChannelId(port, e.Channel.ServerChannelId)].OnClose();
                    e.Channel.DataReceived += (ss, ee) => channels[GetChannelId(port, e.Channel.ServerChannelId)].OnData(ee);
                    if (e.CommandText != null)
                    {
                        channels[GetChannelId(port, e.Channel.ServerChannelId)].OnData(Encoding.ASCII.GetBytes(e.CommandText));
                    }

                }
            }
            else
            {
                channels[GetChannelId(port, e.Channel.ServerChannelId)].EofReceived += (ss, ee) => e.Channel.SendEof();
                channels[GetChannelId(port, e.Channel.ServerChannelId)].CloseReceived += (ss, ee) => e.Channel.SendClose(ee);
                channels[GetChannelId(port, e.Channel.ServerChannelId)].DataReceived += (ss, ee) => e.Channel.SendData(ee);
                e.Channel.EofReceived += (ss, ee) => channels[GetChannelId(port, e.Channel.ServerChannelId)].OnEndOfFile();
                e.Channel.CloseReceived += (ss, ee) => channels[GetChannelId(port, e.Channel.ServerChannelId)].OnClose();
                e.Channel.DataReceived += (ss, ee) => channels[GetChannelId(port, e.Channel.ServerChannelId)].OnData(ee);
            }

            e.Channel.CloseReceived += (ss, ee) => CloseChannel(port, e.Channel.ServerChannelId);
        }


        static void CloseChannel(int port, uint channel)
        {
            channels.Remove(GetChannelId(port, channel));
        }
    }
}
