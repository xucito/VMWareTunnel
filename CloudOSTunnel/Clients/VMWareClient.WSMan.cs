using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using VMware.Vim;
using CloudOSTunnel.Services.WSMan;
using System.Net.Http;
using System.Net;

namespace CloudOSTunnel.Clients
{
    public partial class VMWareClient : IDisposable, IWSManLogging<VMWareClient>
    {
        #region Constants
        //private const int GUEST_OPERATIONS_TIMEOUT_SECONDS = 60;
        //private const int GUEST_OPERATIONS_MAX_RETRIES = 3;
        //private const int GUEST_OPERATIONS_TASK_TIMEOUT_SECONDS = 3600;
        // Root path in Windows guest (to store temporal files)
        private const string windowsGuestRoot = @"C:\Users\Public\Documents";
        #endregion Constants

        public string VmUser
        {
            get { return _executingCredentials.Username; }
        }

        public string VmPassword
        {
            get { return _executingCredentials.Password; }
        }

        #region Logging
        public string Id
        {
            get
            {
                return string.Format("{0}:{1}", _serviceUrl, FullVMName);
            }
        }

        public ILogger<VMWareClient> Logger { get; private set; }

        public void LogInformation(string msg)
        {
            Logger.LogInformation(string.Format("{0} {1}", Id, msg));
        }

        public void LogWarning(string msg)
        {
            Logger.LogWarning(string.Format("{0} {1}", Id, msg));
        }

        public void LogDebug(string msg)
        {
            Logger.LogDebug(string.Format("{0} {1}", Id, msg));
        }

        public void LogError(string msg)
        {
            Logger.LogError(string.Format("{0} {1}", Id, msg));
        }
        #endregion Logging

        /// <summary>
        /// Upload file content from server to guest
        /// </summary>
        /// <param name="content"></param>
        /// <param name="serverPath"></param>
        /// <param name="guestPath"></param>
        /// <returns></returns>
        public async Task<string> UploadFile(string content, string serverPath, string guestPath)
        {
            LogInformation(string.Format("Saving content to {0}", serverPath));
            await File.WriteAllTextAsync(serverPath, content);
            await UploadFile(serverPath, guestPath);

            return serverPath;
        }

        /// <summary>
        /// Upload file from server to guest
        /// </summary>
        /// <param name="serverPath"></param>
        /// <param name="guestPath"></param>
        /// <returns></returns>
        public async Task<string> UploadFile(string serverPath, string guestPath)
        {
            LogInformation(string.Format("Uploading {0} to {1}", serverPath, guestPath));

            try
            {
                using (FileStream fs = new FileStream(serverPath, FileMode.Open, FileAccess.Read))
                {
                    var fileTransferRef = fileManager.InitiateFileTransferToGuest(_vm,
                        _executingCredentials, guestPath,
                        new GuestFileAttributes() { },
                        fs.Length, true);
                    using (var handler = new HttpClientHandler
                    {
                        ServerCertificateCustomValidationCallback = (sender, certificate, chain, sslPolicyErrors) => true
                    })
                    {
                        HttpClient httpClient = new HttpClient(handler);
                        httpClient.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
                        StreamContent streamContent = new StreamContent(fs, 8 * 1024 * 1024); // Use 8MB buffer
                        var uploadResponse = await httpClient.PutAsync(fileTransferRef, streamContent);
                        if (!uploadResponse.IsSuccessStatusCode)
                        {
                            var message = (await uploadResponse.Content.ReadAsStringAsync());
                            LogError("Failed to upload file with error " + message);
                            throw new Exception("Failed to upload file with error " + message);
                        }
                        return serverPath;
                    }
                }
            }
            finally
            {
                LogInformation(string.Format("Deleting {0}", serverPath));
                File.Delete(serverPath);
            }
        }

        /// <summary>
        /// Get prefix to file names
        /// </summary>
        /// <param name="commandId"></param>
        /// <returns></returns>
        private string GetWsmanFilePrefix(string commandId = null)
        {
            string vmPrefix = FullVMName.Trim().Split(" ")[0];
            if (commandId != null)
                return string.Format("{0}_{1}", vmPrefix, commandId);
            else
                return vmPrefix;
        }

        /// <summary>
        /// Await process and output exit code
        /// </summary>
        /// <param name="pid"></param>
        /// <param name="exitCode"></param>
        /// <returns></returns>
        public bool AwaitProcess(long pid, out int exitCode)
        {
            GuestProcessInfo[] process;
            do
            {
                process = processManager.ListProcessesInGuest(_vm, _executingCredentials, new long[] { pid });
                // Reduce number of calls to vCenter
                System.Threading.Thread.Sleep(500);
            } while (process.Count() != 1 || process[0].EndTime == null);

            exitCode = process[0].ExitCode.Value;

            return true;
        }

        /// <summary>
        /// Invoke a command in windows guest
        /// </summary>
        /// <param name="command"></param>
        /// <param name="wait"></param>
        /// <param name="stdoutPathGuest"></param>
        /// <param name="stderrPathGuest"></param>
        /// <returns></returns>
        private CommandResult InvokeWindowsCommand(string command, bool wait, 
            string stdoutPathGuest = null, string stderrPathGuest = null)
        {
            processManager = (GuestProcessManager)client.GetView(guest.ProcessManager, null);

            long pid = processManager.StartProgramInGuest(_vm, _executingCredentials, new GuestProgramSpec
            {
                ProgramPath = @"cmd.exe",
                Arguments = "/C " + command,
                WorkingDirectory = windowsGuestRoot
            });

            int exitCode;
            bool hasOutput = false;
            string stdout, stderr;

            stdout = stderr = null;

            if (wait)
            {
                AwaitProcess(pid, out exitCode);
                if (stdoutPathGuest != null)
                {
                    LogInformation("Getting output and error from guest");
                    stdout = ReadFile(_vm, _executingCredentials, stdoutPathGuest);
                    LogInformation(string.Format("Obtained guest stdout: {0}", stdout));
                    hasOutput = true;
                }
                if (stderrPathGuest != null)
                {
                    stderr = ReadFile(_vm, _executingCredentials, stderrPathGuest);
                    LogInformation(string.Format("Obtained guest stderr: {0}", stderr));
                    hasOutput = true;
                }
            }
            else
            {
                // Assume success if no wait
                exitCode = 0;
                stdout = stderr = null;
                hasOutput = false;
            }

            return new CommandResult
            {
                exitCode = exitCode,
                stdout = stdout,
                stderr = stderr,
                hasOutput = hasOutput
            };
        }


        /// <summary>
        /// Formulate the full powershell command line
        /// </summary>
        /// <param name="psCommand"></param>
        /// <param name="stdoutPath"></param>
        /// <param name="stderrPath"></param>
        /// <returns></returns>
        private string GetFullWindowsCommand(string psCommand, string stdoutPath = null, string stderrPath = null)
        {
            string fullCommand = string.Format(@"PowerShell -NoProfile -NonInteractive -ExecutionPolicy Unrestricted -Command ""& {{{0}}}""", psCommand);
            if(stdoutPath != null)
            {
                fullCommand += " 1>" + stdoutPath;
            }
            if(stderrPath != null)
            {
                fullCommand += " 2>" + stderrPath;
            }
            return fullCommand;
        }

        /// <summary>
        /// Delete files in Windows guest
        /// </summary>
        /// <param name="guestPaths"></param>
        /// <returns></returns>
        private CommandResult DeleteWindowsGuestFiles(string[] guestPaths)
        {
            if (guestPaths == null || guestPaths.Length == 0)
                throw new WSManException("Windows guest files to clean up must be specified");

            string path = string.Join(",", guestPaths);
            string fullCommand = GetFullWindowsCommand("Remove-Item -Path " + path + " -Confirm:$false");
            LogInformation(string.Format("Deleting files in Windows guest {0}", path));
            return InvokeWindowsCommand(fullCommand, true);
        }

        /// <summary>
        /// Execute simple Windows command without WSMan
        /// </summary>
        /// <param name="command"></param>
        /// <returns></returns>
        public CommandResult ExecuteWindowsCommand(string command)
        {
            string vmPrefix = GetWsmanFilePrefix();
            string stdoutPathGuest = Path.Join(windowsGuestRoot, vmPrefix + "_stdout.txt");
            string stderrPathGuest = Path.Join(windowsGuestRoot, vmPrefix + "_stderr.txt");

            // To support script block, -Command must be in the format of: -Command "& {command}"
            string fullCommand = GetFullWindowsCommand(command, stdoutPathGuest, stderrPathGuest);

            LogInformation(fullCommand);

            // Invoke command and get result
            var result = InvokeWindowsCommand(fullCommand, true, stdoutPathGuest, stderrPathGuest);

            // Delete temp files
            DeleteWindowsGuestFiles(new string[] { stdoutPathGuest, stderrPathGuest });

            return result;
        }

        /// <summary>
        /// Execute Windows command received from WSMan protocol
        /// </summary>
        /// <param name="commandId">Command id to uniquely identify a command in WSMan protocol</param>
        /// <param name="command">Command to run</param>
        /// <param name="payloadPathOnServer">Path to the payload on server side</param>
        /// <param name="base64Payload">Indicate whether payload is Base64 encoded</param>
        /// <param name="hasOutput">Output whether it will produce output</param>
        /// <returns></returns>
        public async Task<CommandResult> ExecuteWindowsCommand(int port, string commandId, string command,
            string payloadPathOnServer, bool base64Payload)
        {
            // Check whether decoded command contains reboot code
            bool IsRebootCommand(string decodedCommand)
            {
                // win_reboot uses code below:
                // Set-StrictMode -Version Latest
                // shutdown /r /t 2 /c "Reboot initiated by Ansible"
                // If(-not $?) { If(Get - Variable LASTEXITCODE - ErrorAction SilentlyContinue) { exit $LASTEXITCODE } Else { exit 1 } }
                // Note: Windows can be rebooted by shutdown or Restart-Computer
                string[] lines = decodedCommand.Split('\n');
                string shutdownLine = lines.FirstOrDefault(x => x.StartsWith("shutdown ", StringComparison.OrdinalIgnoreCase));
                string restartLine = lines.FirstOrDefault(x => x.StartsWith("Restart-Computer", StringComparison.OrdinalIgnoreCase));
                return shutdownLine != null || restartLine != null;
            }

            string invoker;
            bool isReboot = false;

            string[] filesToDelete;

            string filePrefix = GetWsmanFilePrefix(commandId);

            string stdoutPathGuest = Path.Join(windowsGuestRoot, filePrefix + "_stdout.txt");
            string stderrPathGuest = Path.Join(windowsGuestRoot, filePrefix + "_stderr.txt");
            string payloadPathGuest = Path.Join(windowsGuestRoot, filePrefix + "_payload.txt");
            string cmdPathGuest = Path.Join(windowsGuestRoot, filePrefix + "_command.ps1");

            string cmdPathServer = Path.Join(Path.GetTempPath(), port + "_command.ps1");

            // There are 4 cases of known command formats so far:
            // 1) No payload, command starts with PowerShell and uses -EncodedCommand
            //    - Directly invoke as is
            // 2) No payload, command is clear-text command e.g. "Get-WmiObject..."
            //    - Invoke with PowerShell .. -Command { <command> }
            // 3) With payload, command starts with PowerShell
            //    - Copy payload to guest as file
            //    - Pipe payload to command using "PowerShell -NoProfile .. -Command {Get-Content <payload file> | <command>}"
            // 4) With payload, command is "begin {} process {} end {}"
            //    - Copy both command and payload to guest as files
            //    - Pipe payload to command using "PowerShell -NoProfile .. -Command {Get-Content <payload file> | <command file>}"
            // Note: all cases must redirect output and error to files
            if (payloadPathOnServer == null)
            {
                // When there is no payload
                if (command.StartsWith("PowerShell", StringComparison.OrdinalIgnoreCase))
                {
                    // Further examine encoded commands when needed
                    // win_reboot that contains Restart-Computer/shutdown must run async because it will lose contact with guest agent
                    string innerEncodedCommand = WSManRuntime.GetCommandInput(command, "-EncodedCommand");
                    string innerDecodedCommand = WSManRuntime.DecodeCommand(innerEncodedCommand, WSManRuntime.CommandType.InvokeCommand);

                    LogInformation("InnerDecodedCommand: " + innerDecodedCommand);

                    isReboot = IsRebootCommand(innerDecodedCommand);
                    if (isReboot)
                    {
                        LogInformation("InnerDecodedCommand contains reboot");
                    }

                    // Directly invoke because it starts with PowerShell..
                    invoker = string.Format("{0} 1>{1} 2>{2}", command, stdoutPathGuest, stderrPathGuest);
                    filesToDelete = new string[] { stdoutPathGuest, stderrPathGuest };
                }
                else
                {
                    // If not Base64 encoded, it is a simple command that should be invoked using "PowerShell -Command {}"
                    if (!base64Payload)
                    {
                        // Single command e.g. (Get-WmiObject -ClassName Win32_OperatingSystem).LastBootUpTime
                        invoker = GetFullWindowsCommand(command, stdoutPathGuest, stderrPathGuest);
                        filesToDelete = new string[] { stdoutPathGuest, stderrPathGuest };
                    }
                    else
                    {
                        throw new WSManException(string.Format("Unrecognised command format: {0}", command));
                    }
                }
            }
            else
            {
                // When there is payload
                if (command.StartsWith("PowerShell", StringComparison.OrdinalIgnoreCase))
                {
                    // When command is "PowerShell ..."
                    // Copy payload to guest
                    await UploadFile(payloadPathOnServer, payloadPathGuest);

                    // Command: PowerShell -NoProfile -NonInteractive -ExecutionPolicy Unrestricted -EncodedCommand XXX
                    // Note: -NoProfile must be used in the out-most PowerShell
                    string psCommand = string.Format("Get-Content {0} | {1}", payloadPathGuest, command);
                    invoker = GetFullWindowsCommand(psCommand, stdoutPathGuest, stderrPathGuest);
                    filesToDelete = new string[] { payloadPathGuest, stdoutPathGuest, stderrPathGuest };
                }
                else if (command.StartsWith("begin", StringComparison.OrdinalIgnoreCase))
                {
                    // When command is "begin { } process { } end { }" which expects pipeline input
                    // Payload is Base64 encoded
                    // Note: This is used by win_file to copy files
                    if (base64Payload)
                    {
                        // Copy command script to guest
                        await UploadFile(command, cmdPathServer, cmdPathGuest);
                        // Copy payload file to guest
                        await UploadFile(payloadPathOnServer, payloadPathGuest);
                        // Call command script file, then pipe payload into it
                        // Note: -NoProfile must be used in the out-most PowerShell
                        string psCommand = string.Format("Get-Content {0} | {1}", payloadPathGuest, cmdPathGuest);
                        invoker = GetFullWindowsCommand(psCommand, stdoutPathGuest, stderrPathGuest);
                        filesToDelete = new string[] { payloadPathGuest, cmdPathGuest, stdoutPathGuest, stderrPathGuest };
                    }
                    else
                    {
                        throw new WSManException(string.Format("Unrecognised command format: {0}", command));
                    }
                }
                else
                {
                    throw new WSManException(string.Format("Unrecognised command format: {0}", command));
                }
            }

            LogInformation(invoker);

            // Invoke and get output
            CommandResult result;
            if (isReboot)
            {
                // Reboot must not wait because guest agent will lose contact. This expects 0 exit code
                result = InvokeWindowsCommand(invoker, false);
            }
            else
            {
                // Invoke command and wait for completion
                result = InvokeWindowsCommand(invoker, true, stdoutPathGuest, stderrPathGuest);
                // Delete temp files
                DeleteWindowsGuestFiles(filesToDelete);
            }

            return result;
        }

        public void Dispose()
        {
            // Minimally erase credential
            _executingCredentials.Username = null;
            _executingCredentials.Password = null;
            _executingCredentials = null;
        }
    }
}
