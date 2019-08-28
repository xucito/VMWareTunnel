using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Xml;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using CloudOSTunnel.Clients;

namespace CloudOSTunnel.Services.WSMan
{
    public class CommandResult
    {
        public int exitCode;
        public string stdout;
        public string stderr;
        public bool hasOutput;
    }

    internal class WSManRuntime : IWSManLogging<WSManRuntime>
    {
        // Reference to the shared vCenter client
        private readonly VMWareClient client;

        // Command type (first command is invoke command, subsequent ones are Base64-encoded stdin commands)
        public enum CommandType { InvokeCommand, StdinCommand };

        // Used to record encoded payload begin time and end time
        private DateTime? payloadBeginTime, payloadEndTime;

        // Port number assigned by the server
        private readonly int port;
        // Command ID assigned for each command (used throughout the command lifetime)
        private readonly string commandId;
        // Logging prefix
        private readonly string loggingPrefix;

        #region Protocol Constants
        // Shell ID assigned by server (used throughout the protocol lifetime)
        // Note: Not used because there is no runtime shell created
        internal const string SHELL_ID = "00000000-0000-0000-0000-000000000000";
        // Command ID assigned by server (used throughout the protocol lifetime)
        // internal const string COMMAND_ID = "11111111-1111-1111-1111-111111111111";
        // Client IP (dummy ip to fill wsman protocol, likely not used at all)
        internal const string CLIENT_IP = "127.0.0.1";
        // Standard invoke command
        internal const string STANDARD_WRAPPER_COMMAND = "PowerShell -NoProfile -NonInteractive -ExecutionPolicy Unrestricted -EncodedCommand JgBjAGgAYwBwAC4AYwBvAG0AIAA2ADUAMAAwADEAIAA+ACAAJABuAHUAbABsAAoAJABlAHgAZQBjAF8AdwByAGEAcABwAGUAcgBfAHMAdAByACAAPQAgACQAaQBuAHAAdQB0ACAAfAAgAE8AdQB0AC0AUwB0AHIAaQBuAGcACgAkAHMAcABsAGkAdABfAHAAYQByAHQAcwAgAD0AIAAkAGUAeABlAGMAXwB3AHIAYQBwAHAAZQByAF8AcwB0AHIALgBTAHAAbABpAHQAKABAACgAIgBgADAAYAAwAGAAMABgADAAIgApACwAIAAyACwAIABbAFMAdAByAGkAbgBnAFMAcABsAGkAdABPAHAAdABpAG8AbgBzAF0AOgA6AFIAZQBtAG8AdgBlAEUAbQBwAHQAeQBFAG4AdAByAGkAZQBzACkACgBJAGYAIAAoAC0AbgBvAHQAIAAkAHMAcABsAGkAdABfAHAAYQByAHQAcwAuAEwAZQBuAGcAdABoACAALQBlAHEAIAAyACkAIAB7ACAAdABoAHIAbwB3ACAAIgBpAG4AdgBhAGwAaQBkACAAcABhAHkAbABvAGEAZAAiACAAfQAKAFMAZQB0AC0AVgBhAHIAaQBhAGIAbABlACAALQBOAGEAbQBlACAAagBzAG8AbgBfAHIAYQB3ACAALQBWAGEAbAB1AGUAIAAkAHMAcABsAGkAdABfAHAAYQByAHQAcwBbADEAXQAKACQAZQB4AGUAYwBfAHcAcgBhAHAAcABlAHIAIAA9ACAAWwBTAGMAcgBpAHAAdABCAGwAbwBjAGsAXQA6ADoAQwByAGUAYQB0AGUAKAAkAHMAcABsAGkAdABfAHAAYQByAHQAcwBbADAAXQApAAoAJgAkAGUAeABlAGMAXwB3AHIAYQBwAHAAZQByAA==";
        #endregion Protocol Constants

        #region Protocol Types and Attributes
        // Invoke command from Ansible
        private string command;
        // Record last command from Ansible
        private string lastCommand;
        // Indicate payload is Base64 encoded
        private bool payloadEncoded;
        // Payload file path
        private string payloadFile;
        // Payload writer
        private StreamWriter payloadWriter;
        #endregion Protocol Types and Attributes

        internal WSManRuntime(ILoggerFactory loggerFactory, VMWareClient client, int port, string commandId, string loggingPrefix)
        {
            this.Logger = loggerFactory.CreateLogger<WSManRuntime>();
            this.client = client;
            this.port = port;
            this.commandId = commandId;
            this.loggingPrefix = loggingPrefix;

            this.payloadBeginTime = this.payloadEndTime = null;
            this.command = this.lastCommand = null;
            this.payloadEncoded = false;
            this.payloadFile = null;
            this.payloadWriter = null;
        }

        #region Logging
        // ID for logging e.g. <prefix>:vc:vm:commandId
        public string Id
        {
            get
            {
                var clientId = client != null ? client.Id : "<client null>";
                return string.Format("{0}:{1}:{2}", loggingPrefix, clientId, commandId);
            }
        }

        public ILogger<WSManRuntime> Logger { get; private set; }

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

        #region Payload Writer
        private void CreatePayloadWriter(int port)
        {
            if(this.payloadFile != null)
            {
                throw new WSManException(
                    string.Format("Unable to create payload file {0} as it already exists", this.payloadFile));
            }
            this.payloadFile = Path.Join(Path.GetTempPath(), string.Format("{0}_{1}", port, commandId));
            this.payloadWriter = new StreamWriter(payloadFile, false);
            this.payloadWriter.AutoFlush = true;
        }

        private void DeletePayloadWriter()
        {
            if(this.payloadFile != null)
                System.IO.File.Delete(this.payloadFile);

            this.payloadFile = null;
            this.payloadWriter = null;
        }

        private async Task WritePayload(string payload)
        {
            await this.payloadWriter.WriteAsync(payload);
        }

        public void Dispose()
        {
            DeletePayloadWriter();
            command = null;
            lastCommand = null;
        }
        #endregion Payload Writer

        #region Protocol Implementation
        // Get the input value from an invoke command
        // PowerShell -NoProfile -NonInteractive -ExecutionPolicy Unrestricted -EncodedCommand 1>C:\1.txt 2>C:\2.txt
        public static string GetCommandInput(string command, string inputName)
        {
            string[] tmp = command.Split(" ", StringSplitOptions.RemoveEmptyEntries);
            int inputIndex = -1;
            for (int i = 0; i < tmp.Length - 1; i++)
            {
                if (tmp[i].ToUpper() == inputName.ToUpper())
                {
                    inputIndex = i;
                    break;
                }
            }

            if (inputIndex == -1)
            {
                throw new WSManException(string.Format("No input of name {0} found in {1}", inputName, command));
            }

            return tmp[inputIndex + 1];
        }

        /// <summary>
        /// Encode to UTF8 for response
        /// </summary>
        /// <param name="content">String to encoded to UTF8 format</param>
        /// <returns>Encoded command in UTF8</returns>
        private static string EncodeUTF8(string content)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
        }

        /// <summary>
        /// Decode command depending on command type
        /// Note: The first command is encoded using Unicode. Subsequent commands (stdin) are in UTF8.
        /// </summary>
        /// <param name="encodedCommand">Base64 encoded command to decode</param>
        /// <param name="commandType">Command type</param>
        /// <returns>Decoded command</returns>
        public static string DecodeCommand(string encodedCommand, CommandType commandType)
        {
            if (commandType == CommandType.InvokeCommand)
            {
                return Encoding.Unicode.GetString(Convert.FromBase64String(encodedCommand));
            }
            else
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(encodedCommand));
            }
        }

        /// <summary>
        /// Wsman protocol action 1 - Create shell
        /// </summary>
        /// <param name="xml">XML request</param>
        /// <returns>Wsman protocol response</returns>
        public static string HandleCreateShellAction(XmlDocument xml, string vmUser, IWSManLogging<WSManServer> logger)
        {
            // Action 1: Create shell http://schemas.xmlsoap.org/ws/2004/09/transfer/Create
            // Request: <env:Header><w:OperationTimeout>PT9999S</w:OperationTimeout><a:To>http://windows-host:5985/wsman</a:To><w:OptionSet><w:Option Name="WINRS_NOPROFILE">FALSE</w:Option><w:Option Name="WINRS_CODEPAGE">65001</w:Option></w:OptionSet><w:MaxEnvelopeSize mustUnderstand="true">153600</w:MaxEnvelopeSize><w:ResourceURI mustUnderstand="true">http://schemas.microsoft.com/wbem/wsman/1/windows/shell/cmd</w:ResourceURI><a:Action mustUnderstand="true">http://schemas.xmlsoap.org/ws/2004/09/transfer/Create</a:Action><p:DataLocale mustUnderstand="false" xml:lang="en-US"></p:DataLocale><a:ReplyTo><a:Address mustUnderstand="true">http://schemas.xmlsoap.org/ws/2004/08/addressing/role/anonymous</a:Address></a:ReplyTo><a:MessageID>uuid:a9b8312c-2d01-4757-9ef2-9751ddaafd43</a:MessageID><w:Locale mustUnderstand="false" xml:lang="en-US"></w:Locale></env:Header><env:Body><rsp:Shell><rsp:InputStreams>stdin</rsp:InputStreams><rsp:OutputStreams>stdout stderr</rsp:OutputStreams></rsp:Shell></env:Body>

            string requestMessageId = xml.GetElementsByTagName("a:MessageID").Item(0).InnerText;
            string address = xml.GetElementsByTagName("a:To").Item(0).InnerText;

            logger.LogInformation(string.Format("Received create shell action from {0}", address));
            string responseMessageId = "uuid:" + Guid.NewGuid();
            string body = @"<s:Envelope xml:lang=""en-US"" xmlns:s=""http://www.w3.org/2003/05/soap-envelope"" xmlns:a=""http://schemas.xmlsoap.org/ws/2004/08/addressing"" xmlns:x=""http://schemas.xmlsoap.org/ws/2004/09/transfer"" xmlns:w=""http://schemas.dmtf.org/wbem/wsman/1/wsman.xsd"" xmlns:rsp=""http://schemas.microsoft.com/wbem/wsman/1/windows/shell"" xmlns:p=""http://schemas.microsoft.com/wbem/wsman/1/wsman.xsd"">"
                + "<s:Header>"
                + "<a:Action>http://schemas.xmlsoap.org/ws/2004/09/transfer/CreateResponse</a:Action>"
                + "<a:MessageID>" + responseMessageId + @"</a:MessageID>"
                + "<a:To>http://schemas.xmlsoap.org/ws/2004/08/addressing/role/anonymous</a:To>"
                + "<a:RelatesTo>" + requestMessageId + @"</a:RelatesTo>"
                + "</s:Header>"
                + "<s:Body>"
                + "<x:ResourceCreated>"
                + "<a:Address>" + address + "</a:Address>"
                + "<a:ReferenceParameters>"
                + "<w:ResourceURI>http://schemas.microsoft.com/wbem/wsman/1/windows/shell/cmd</w:ResourceURI>"
                + "<w:SelectorSet>"
                + @"<w:Selector Name=""ShellId"">" + SHELL_ID + "</w:Selector>"
                + "</w:SelectorSet>"
                + "</a:ReferenceParameters>"
                + "</x:ResourceCreated>"
                + @"<rsp:Shell xmlns:rsp=""http://schemas.microsoft.com/wbem/wsman/1/windows/shell"">"
                + "<rsp:ShellId>" + SHELL_ID + "</rsp:ShellId>"
                + "<rsp:ResourceUri>http://schemas.microsoft.com/wbem/wsman/1/windows/shell/cmd</rsp:ResourceUri>"
                + "<rsp:Owner>" + vmUser + "</rsp:Owner>"
                + "<rsp:ClientIP>" + CLIENT_IP + "</rsp:ClientIP>"
                + "<rsp:IdleTimeOut>PT7200.000S</rsp:IdleTimeOut>"
                + "<rsp:InputStreams>stdin</rsp:InputStreams>"
                + "<rsp:OutputStreams>stdout stderr</rsp:OutputStreams>"
                + "<rsp:ShellRunTime>P0DT0H0M0S</rsp:ShellRunTime>"
                + "<rsp:ShellInactivity>P0DT0H0M0S</rsp:ShellInactivity>"
                + "</rsp:Shell>"
                + "</s:Body>"
                + "</s:Envelope>";

            return body;
        }

        /// <summary>
        /// Wsman protocol action 2 - Execute command
        /// </summary>
        /// <param name="xml">XML request</param>
        /// <returns>Wsman protocol response</returns>
        public string HandleExecuteCommandAction(XmlDocument xml)
        {
            // Action 2: Execute command http://schemas.microsoft.com/wbem/wsman/1/windows/shell/Command
            // Request: <env:Header><w:OptionSet><w:Option Name="WINRS_CONSOLEMODE_STDIN">FALSE</w:Option><w:Option Name="WINRS_SKIP_CMD_SHELL">FALSE</w:Option></w:OptionSet><w:MaxEnvelopeSize mustUnderstand="true">153600</w:MaxEnvelopeSize><a:Action mustUnderstand="true">http://schemas.microsoft.com/wbem/wsman/1/windows/shell/Command</a:Action><w:Locale mustUnderstand="false" xml:lang="en-US"></w:Locale><a:MessageID>uuid:39acd81e-32ed-4c23-a103-56bafa5e4e8c</a:MessageID><w:OperationTimeout>PT9999S</w:OperationTimeout><a:ReplyTo><a:Address mustUnderstand="true">http://schemas.xmlsoap.org/ws/2004/08/addressing/role/anonymous</a:Address></a:ReplyTo><w:SelectorSet><w:Selector Name="ShellId">BB6E9B83-A0BC-4577-B121-ED61CA46EED6</w:Selector></w:SelectorSet><w:ResourceURI mustUnderstand="true">http://schemas.microsoft.com/wbem/wsman/1/windows/shell/cmd</w:ResourceURI><a:To>http://windows-host:5985/wsman</a:To><p:DataLocale mustUnderstand="false" xml:lang="en-US"></p:DataLocale></env:Header><env:Body><rsp:CommandLine><rsp:Arguments>-NoProfile -NonInteractive -ExecutionPolicy Unrestricted -EncodedCommand <encodedCommand></rsp:Arguments><rsp:Command>PowerShell</rsp:Command></rsp:CommandLine></env:Body>
            string requestMessageId = xml.GetElementsByTagName("a:MessageID").Item(0).InnerText;
            string address = xml.GetElementsByTagName("a:To").Item(0).InnerText;

            // string command = xml.GetElementsByTagName("rsp:Command").Item(0).InnerText;
            string arguments = xml.GetElementsByTagName("rsp:Arguments").Item(0).InnerText;
            // PowerShell -NoProfile -NonInteractive -ExecutionPolicy Unrestricted -EncodedCommand XXX
            // Where XXX after decoding may incluldes these possibilities: 
            // 1) PowerShell -NoProfile -NonInteractive -ExecutionPolicy Unrestricted -EncodedCommand XXX
            // 2) begin {} process {} end {}
            // 3) YYY (encoded payload)
            // 4) ZZZ (clear-text payload)
            string encodedCommand = GetCommandInput(arguments, "-EncodedCommand");

            string decodedCommand = DecodeCommand(encodedCommand, CommandType.InvokeCommand);

            LogInformation(string.Format("Received decoded command {0} chars", decodedCommand.Length));
            LogInformation("Received decoded command: " + decodedCommand);

            command = decodedCommand;

            // Failfast on receiving duplicate commands
            // This happens when win_reboot does not specify connect_timeout long enough to tolerate guest operations overhead
            // Then win_reboot will send many duplicate commands which are impossible to handle timely
            // In this case, terminate the server to avoid further problems
            // Resolution: User must shut down tunnel then create a new one
            if (command == lastCommand && command != STANDARD_WRAPPER_COMMAND)
            {
                string msg = string.Format("Duplicate command received: {0}", command);
                LogWarning(msg);
                LogWarning("Terminating server to avoid issues");
                throw new WSManException(msg);
            }

            // Remember last command
            lastCommand = command;

            // Reset encoded payload times
            payloadBeginTime = payloadEndTime = null;

            string responseMessageId = "uuid:" + Guid.NewGuid();
            string body = @"<s:Envelope xml:lang=""en-US"" xmlns:s=""http://www.w3.org/2003/05/soap-envelope"" xmlns:a=""http://schemas.xmlsoap.org/ws/2004/08/addressing"" xmlns:x=""http://schemas.xmlsoap.org/ws/2004/09/transfer"" xmlns:w=""http://schemas.dmtf.org/wbem/wsman/1/wsman.xsd"" xmlns:rsp=""http://schemas.microsoft.com/wbem/wsman/1/windows/shell"" xmlns:p=""http://schemas.microsoft.com/wbem/wsman/1/wsman.xsd"">"
                + "<s:Header>"
                + "<a:Action>http://schemas.microsoft.com/wbem/wsman/1/windows/shell/CommandResponse</a:Action>"
                + "<a:MessageID>" + responseMessageId + "</a:MessageID>"
                + "<a:To>http://schemas.xmlsoap.org/ws/2004/08/addressing/role/anonymous</a:To>"
                + "<a:RelatesTo>" + requestMessageId + "</a:RelatesTo>"
                + "</s:Header>"
                + "<s:Body>"
                + "<rsp:CommandResponse>"
                + "<rsp:CommandId>" + commandId + "</rsp:CommandId>"
                + "</rsp:CommandResponse>"
                + "</s:Body>"
                + "</s:Envelope>";

            return body;
        }

        /// <summary>
        /// Wsman protocol action 3 (optional) - Send input
        /// </summary>
        /// <param name="xml">XML request</param>
        /// <returns>Wsman protocol response</returns>
        public async Task<string> HandleSendInputAction(XmlDocument xml, int port)
        {
            // Action 3: Send Input http://schemas.microsoft.com/wbem/wsman/1/windows/shell/Send
            // Request: <env:Header><w:OperationTimeout>PT9999S</w:OperationTimeout><a:To>http://windows-host:5985/wsman</a:To><w:SelectorSet><w:Selector Name="ShellId">BB6E9B83-A0BC-4577-B121-ED61CA46EED6</w:Selector></w:SelectorSet><w:MaxEnvelopeSize mustUnderstand="true">153600</w:MaxEnvelopeSize><w:ResourceURI mustUnderstand="true">http://schemas.microsoft.com/wbem/wsman/1/windows/shell/cmd</w:ResourceURI><a:Action mustUnderstand="true">http://schemas.microsoft.com/wbem/wsman/1/windows/shell/Send</a:Action><p:DataLocale mustUnderstand="false" xml:lang="en-US"></p:DataLocale><a:ReplyTo><a:Address mustUnderstand="true">http://schemas.xmlsoap.org/ws/2004/08/addressing/role/anonymous</a:Address></a:ReplyTo><a:MessageID>uuid:33ab78de-f9d8-4af8-9705-21773e9f5a3a</a:MessageID><w:Locale mustUnderstand="false" xml:lang="en-US"></w:Locale></env:Header><env:Body><rsp:Send><rsp:Stream End="true" Name="stdin" CommandId="0832BFA4-BD87-47EE-BA48-80D2B67724D1"><encodedCommand></rsp:Stream></rsp:Send></env:Body>
            string requestMessageId = xml.GetElementsByTagName("a:MessageID").Item(0).InnerText;
            string address = xml.GetElementsByTagName("a:To").Item(0).InnerText;

            string encodedCommand = xml.GetElementsByTagName("rsp:Stream").Item(0).InnerText;

            string stdinCommand = DecodeCommand(encodedCommand, CommandType.StdinCommand);

            LogInformation(string.Format("Received stdin command {0} chars", stdinCommand.Length));
            //LogDebug(stdinCommand);

            // Payload is encoded in base64 if it has no space in first 32 chars
            payloadEncoded = !stdinCommand.Take(32).Contains(' ');

            // Create a payload writer if not exists or closed
            if (payloadWriter == null || payloadWriter.BaseStream == null)
            {
                CreatePayloadWriter(port);
                payloadBeginTime = DateTime.Now;
                LogInformation(string.Format("Payload started {0}",
                    payloadBeginTime.Value.ToString("yyyy/MM/dd HH:mm:ss")));
            }

            // Write stdin command to disk
            await WritePayload(stdinCommand);

            string responseMessageId = "uuid:" + Guid.NewGuid();
            string body = @"<s:Envelope xml:lang=""en-US"" xmlns:s=""http://www.w3.org/2003/05/soap-envelope"" xmlns:a=""http://schemas.xmlsoap.org/ws/2004/08/addressing"" xmlns:x=""http://schemas.xmlsoap.org/ws/2004/09/transfer"" xmlns:w=""http://schemas.dmtf.org/wbem/wsman/1/wsman.xsd"" xmlns:rsp=""http://schemas.microsoft.com/wbem/wsman/1/windows/shell"" xmlns:p=""http://schemas.microsoft.com/wbem/wsman/1/wsman.xsd"">"
                + "<s:Header>"
                + "<a:Action>http://schemas.microsoft.com/wbem/wsman/1/windows/shell/SendResponse</a:Action>"
                + "<a:MessageID>" + responseMessageId + "</a:MessageID>"
                + "<a:To>http://schemas.xmlsoap.org/ws/2004/08/addressing/role/anonymous</a:To>"
                + "<a:RelatesTo>" + requestMessageId + "</a:RelatesTo>"
                + "</s:Header>"
                + "<s:Body>"
                + "<rsp:SendResponse/>"
                + "</s:Body>"
                + "</s:Envelope>";

            return body;
        }

        /// <summary>
        /// Wsman protocol action 4 - Receive
        /// </summary>
        /// <param name="xml">XML request</param>
        /// <returns>Wsman protocol response</returns>
        public async Task<string> HandleReceiveAction(XmlDocument xml, string commandId)
        {
            // Action 4: Receive http://schemas.microsoft.com/wbem/wsman/1/windows/shell/Receive
            // Request: <env:Header><w:OperationTimeout>PT9999S</w:OperationTimeout><a:To>http://windows-host:5985/wsman</a:To><w:SelectorSet><w:Selector Name="ShellId">BB6E9B83-A0BC-4577-B121-ED61CA46EED6</w:Selector></w:SelectorSet><w:MaxEnvelopeSize mustUnderstand="true">153600</w:MaxEnvelopeSize><w:ResourceURI mustUnderstand="true">http://schemas.microsoft.com/wbem/wsman/1/windows/shell/cmd</w:ResourceURI><a:Action mustUnderstand="true">http://schemas.microsoft.com/wbem/wsman/1/windows/shell/Receive</a:Action><p:DataLocale mustUnderstand="false" xml:lang="en-US"></p:DataLocale><a:ReplyTo><a:Address mustUnderstand="true">http://schemas.xmlsoap.org/ws/2004/08/addressing/role/anonymous</a:Address></a:ReplyTo><a:MessageID>uuid:32380b0b-03b3-43a4-b51d-766ee2275403</a:MessageID><w:Locale mustUnderstand="false" xml:lang="en-US"></w:Locale></env:Header><env:Body><rsp:Receive><rsp:DesiredStream CommandId="0832BFA4-BD87-47EE-BA48-80D2B67724D1">stdout stderr</rsp:DesiredStream></rsp:Receive></env:Body>
            string requestMessageId = xml.GetElementsByTagName("a:MessageID").Item(0).InnerText;

            LogInformation("Received receive action, start execution");
            // Close payload file before executing
            if (payloadWriter != null)
            {
                payloadWriter.Close();

                payloadEndTime = DateTime.Now;
                LogInformation(string.Format("Payload ended {0}", payloadEndTime.Value.ToString("yyyy/MM/dd HH:mm:ss")));
                LogInformation(string.Format("Payload took {0:0} seconds", (payloadEndTime - payloadBeginTime).Value.TotalSeconds));
            }

            // Execute commands
            try
            {
                CommandResult result = await client.ExecuteWindowsCommand(port, commandId, command,
                    payloadFile, payloadEncoded);

                string encodedStdout = "";
                string encodedStderr = "";

                if(result.hasOutput)
                {
                    // Encode output to Base64 UTF8
                    encodedStdout = result.stdout != null ? EncodeUTF8(result.stdout) : "";
                    encodedStderr = result.stderr != null ? EncodeUTF8(result.stderr) : "";
                }
                else
                {
                    encodedStdout = "";
                    encodedStderr = "";
                }

                string responseMessageId = "uuid:" + Guid.NewGuid();

                string body = @"<s:Envelope xml:lang=""en-US"" xmlns:s=""http://www.w3.org/2003/05/soap-envelope"" xmlns:a=""http://schemas.xmlsoap.org/ws/2004/08/addressing"" xmlns:w=""http://schemas.dmtf.org/wbem/wsman/1/wsman.xsd"" xmlns:rsp=""http://schemas.microsoft.com/wbem/wsman/1/windows/shell"" xmlns:p=""http://schemas.microsoft.com/wbem/wsman/1/wsman.xsd"">"
                    + "<s:Header>"
                    + "<a:Action>http://schemas.microsoft.com/wbem/wsman/1/windows/shell/ReceiveResponse</a:Action>"
                    + "<a:MessageID>" + responseMessageId + "</a:MessageID>"
                    + "<a:To>http://schemas.xmlsoap.org/ws/2004/08/addressing/role/anonymous</a:To>"
                    + "<a:RelatesTo>" + requestMessageId + "</a:RelatesTo>"
                    + "</s:Header>"
                    + "<s:Body>"
                    + "<rsp:ReceiveResponse>"
                    + @"<rsp:Stream Name=""stdout"" CommandId=""" + commandId + @""">" + encodedStdout + "</rsp:Stream>"
                    + @"<rsp:Stream Name=""stderr"" CommandId=""" + commandId + @""">" + encodedStderr + "</rsp:Stream>"
                    + @"<rsp:Stream Name=""stdout"" CommandId=""" + commandId + @""" End=""true""></rsp:Stream>"
                    + @"<rsp:Stream Name=""stderr"" CommandId=""" + commandId + @""" End=""true""></rsp:Stream>"
                    + @"<rsp:CommandState CommandId=""" + commandId + @""" State=""http://schemas.microsoft.com/wbem/wsman/1/windows/shell/CommandState/Done"">"
                    + @"<rsp:ExitCode>" + result.exitCode + "</rsp:ExitCode>"
                    + @"</rsp:CommandState>"
                    + "</rsp:ReceiveResponse>"
                    + "</s:Body>"
                    + "</s:Envelope>";

                return body;
            }
            finally
            {
                command = null;
                DeletePayloadWriter();
            }
        }

        /// <summary>
        /// Wsman protocol control action - Signal terminate
        /// </summary>
        /// <param name="xml">XML request</param>
        /// <returns>Wsman protocol response</returns>
        public string HandleSignalAction(XmlDocument xml)
        {
            // Action 5: Signal http://schemas.microsoft.com/wbem/wsman/1/windows/shell/Signal
            // Request: <env:Header><w:OperationTimeout>PT9999S</w:OperationTimeout><a:To>http://windows-host:5985/wsman</a:To><w:SelectorSet><w:Selector Name="ShellId">BB6E9B83-A0BC-4577-B121-ED61CA46EED6</w:Selector></w:SelectorSet><w:MaxEnvelopeSize mustUnderstand="true">153600</w:MaxEnvelopeSize><w:ResourceURI mustUnderstand="true">http://schemas.microsoft.com/wbem/wsman/1/windows/shell/cmd</w:ResourceURI><a:Action mustUnderstand="true">http://schemas.microsoft.com/wbem/wsman/1/windows/shell/Signal</a:Action><p:DataLocale mustUnderstand="false" xml:lang="en-US"></p:DataLocale><a:ReplyTo><a:Address mustUnderstand="true">http://schemas.xmlsoap.org/ws/2004/08/addressing/role/anonymous</a:Address></a:ReplyTo><a:MessageID>uuid:f9d8466f-e99c-4519-9a61-5d832ad5de94</a:MessageID><w:Locale mustUnderstand="false" xml:lang="en-US"></w:Locale></env:Header><env:Body><rsp:Signal CommandId="0832BFA4-BD87-47EE-BA48-80D2B67724D1"><rsp:Code>http://schemas.microsoft.com/wbem/wsman/1/windows/shell/signal/terminate</rsp:Code></rsp:Signal></env:Body>
            string requestMessageId = xml.GetElementsByTagName("a:MessageID").Item(0).InnerText;
            string signalCode = xml.GetElementsByTagName("rsp:Code").Item(0).InnerText;

            LogInformation("Received signal terminate");

            string responseMessageId = "uuid:" + Guid.NewGuid();

            string body = @"<s:Envelope xml:lang=""en-US"" xmlns:s=""http://www.w3.org/2003/05/soap-envelope"" xmlns:a=""http://schemas.xmlsoap.org/ws/2004/08/addressing"" xmlns:x=""http://schemas.xmlsoap.org/ws/2004/09/transfer"" xmlns:w=""http://schemas.dmtf.org/wbem/wsman/1/wsman.xsd"" xmlns:rsp=""http://schemas.microsoft.com/wbem/wsman/1/windows/shell"" xmlns:p=""http://schemas.microsoft.com/wbem/wsman/1/wsman.xsd"">"
                    + "<s:Header>"
                    + "<a:Action>http://schemas.microsoft.com/wbem/wsman/1/windows/shell/SignalResponse</a:Action>"
                    + "<a:MessageID>" + responseMessageId + "</a:MessageID>"
                    + "<a:To>http://schemas.xmlsoap.org/ws/2004/08/addressing/role/anonymous</a:To>"
                    + "<a:RelatesTo>" + requestMessageId + "</a:RelatesTo>"
                    + "</s:Header>"
                    + "<s:Body>"
                    + "<rsp:SignalResponse/>"
                    + "</s:Body>"
                    + "</s:Envelope>";

            if (signalCode == "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/signal/terminate")
            {
                return body;
            }
            else if (signalCode == "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/signal/ctrl_c")
            {
                throw new NotSupportedException(string.Format("Not supported signal code {0}", signalCode));
            }
            else if (signalCode == "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/signal/ctrl_break")
            {
                throw new NotSupportedException(string.Format("Not supported signal code {0}", signalCode));
            }
            else if (signalCode == "powershell/signal/ctrl_c")
            {
                throw new NotSupportedException(string.Format("Not supported signal code {0}", signalCode));
            }
            else
            {
                throw new NotSupportedException(string.Format("Not supported signal code {0}", signalCode));
            }
        }

        /// <summary>
        /// Wsman protocol control action - Delete shell
        /// </summary>
        /// <param name="xml">XML request</param>
        /// <returns>Wsman protocol response</returns>
        public static string HandleDeleteShellAction(XmlDocument xml, IWSManLogging<WSManServer> logger)
        {
            // Action 6: Delete http://schemas.xmlsoap.org/ws/2004/09/transfer/Delete
            // Request: <env:Header><w:OperationTimeout>PT9999S</w:OperationTimeout><a:To>http://windows-host:5985/wsman</a:To><w:SelectorSet><w:Selector Name="ShellId">BB6E9B83-A0BC-4577-B121-ED61CA46EED6</w:Selector></w:SelectorSet><w:MaxEnvelopeSize mustUnderstand="true">153600</w:MaxEnvelopeSize><w:ResourceURI mustUnderstand="true">http://schemas.microsoft.com/wbem/wsman/1/windows/shell/cmd</w:ResourceURI><a:Action mustUnderstand="true">http://schemas.xmlsoap.org/ws/2004/09/transfer/Delete</a:Action><p:DataLocale mustUnderstand="false" xml:lang="en-US"></p:DataLocale><a:ReplyTo><a:Address mustUnderstand="true">http://schemas.xmlsoap.org/ws/2004/08/addressing/role/anonymous</a:Address></a:ReplyTo><a:MessageID>uuid:d48b5f63-9ed8-4007-b086-cef303e813ac</a:MessageID><w:Locale mustUnderstand="false" xml:lang="en-US"></w:Locale></env:Header><env:Body></env:Body>
            string requestMessageId = xml.GetElementsByTagName("a:MessageID").Item(0).InnerText;

            logger.LogInformation("Received delete shell action");

            string responseMessageId = "uuid:" + Guid.NewGuid();

            string body = @"<s:Envelope xml:lang=""en-US"" xmlns:s=""http://www.w3.org/2003/05/soap-envelope"" xmlns:a=""http://schemas.xmlsoap.org/ws/2004/08/addressing"" xmlns:w=""http://schemas.dmtf.org/wbem/wsman/1/wsman.xsd"" xmlns:p=""http://schemas.microsoft.com/wbem/wsman/1/wsman.xsd"">"
                + "<s:Header>"
                + "<a:Action>http://schemas.xmlsoap.org/ws/2004/09/transfer/DeleteResponse</a:Action>"
                + "<a:MessageID>" + responseMessageId + "</a:MessageID>"
                + "<a:To>http://schemas.xmlsoap.org/ws/2004/08/addressing/role/anonymous</a:To>"
                + "<a:RelatesTo>" + requestMessageId + "</a:RelatesTo>"
                + "</s:Header>"
                + "<s:Body>"
                + "</s:Body>"
                + "</s:Envelope>";

            return body;
        }
        #endregion Protocol Implementation
    }
}
