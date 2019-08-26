using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CloudOSTunnel.Services.WSMan
{
    public class VcClient : IDisposable, IWSManLogging
    {
        // PowerShell object to run commands
        private PowerShell ps;
        private readonly object psLock = new object();
        // Logging objects
        private ILogger<VcClient> logger;

        #region Constants
        private const int GUEST_OPERATIONS_TIMEOUT_SECONDS = 60;
        private const int GUEST_OPERATIONS_MAX_RETRIES = 3;
        private const int GUEST_OPERATIONS_TASK_TIMEOUT_SECONDS = 3600;
        private readonly string vcServer;
        private readonly string vmId, vmName, vmUser;
        private readonly string scriptType;
        private readonly bool debug;

        // Root path in guest (to store temporal files)
        private const string guestRoot = @"C:\Users\Public\Documents";
        #endregion Constants

        public VcClient(ILoggerFactory loggerFactory, string vcServer, string vcUser, string vcPassword,
            string vmId, string vmName, string vmUser, string vmPassword,
            string scriptType, bool debug)
        {
            this.logger = loggerFactory.CreateLogger<VcClient>();
            this.vcServer = vcServer;
            this.vmName = vmName;
            this.vmUser = vmUser;
            this.scriptType = scriptType;
            this.debug = debug;

            // Fix VM ID if required
            if (vmId != null && !vmId.StartsWith("VirtualMachine-"))
            {
                this.vmId = "VirtualMachine-" + vmId;
            }
            else
            {
                this.vmId = vmId;
            }

            // Create a powershell runspace
            ps = PowerShell.Create(RunspaceMode.NewRunspace);

            // Set vSphere variables
            ps.Runspace.SessionStateProxy.SetVariable("vcServer", vcServer);
            ps.Runspace.SessionStateProxy.SetVariable("vcUser", vcUser);
            ps.Runspace.SessionStateProxy.SetVariable("vcPassword", vcPassword);
            ps.Runspace.SessionStateProxy.SetVariable("vmId", vmId);
            ps.Runspace.SessionStateProxy.SetVariable("vmName", vmName);
            ps.Runspace.SessionStateProxy.SetVariable("vmUser", vmUser);
            ps.Runspace.SessionStateProxy.SetVariable("vmPassword", vmPassword);
            ps.Runspace.SessionStateProxy.SetVariable("scriptType", scriptType);
            ps.Runspace.SessionStateProxy.SetVariable("guestOperationsTimeoutSeconds", GUEST_OPERATIONS_TIMEOUT_SECONDS);
        }

        /// <summary>
        /// Wait for async task to complete
        /// </summary>
        /// <param name="result">Task result</param>
        /// <returns>Task state</returns>
        private string WaitTask(out string result, int timeout = GUEST_OPERATIONS_TASK_TIMEOUT_SECONDS)
        {
            string taskState;

            var startTime = DateTime.Now;

            lock (psLock)
            {
                // Pull task state: Error, Queued, Running, Success
                do
                {
                    Clear();

                    var script = @"
$taskState = $task.State.ToString()
$result = $task.Result.ScriptOutput
if($result) {
    $result = $result.Trim()
}
";
                    ps.AddScript(script);
                    ps.Invoke();
                    if (ps.Streams.Error.Count > 0)
                    {
                        throw ps.Streams.Error[0].Exception;
                    }

                    taskState = (string)ps.Runspace.SessionStateProxy.GetVariable("taskState");
                    System.Threading.Thread.Sleep(1000);
                    if (DateTime.Now.Subtract(startTime).TotalSeconds > GUEST_OPERATIONS_TASK_TIMEOUT_SECONDS)
                    {
                        throw new WinRM2VMWException("Task for script timed out");
                    }
                } while ((new string[] { "Queued", "Running" }).Contains(taskState));

                LogInformation(string.Format("Task completed in {0:0} seconds with {1}",
                    DateTime.Now.Subtract(startTime).TotalSeconds, taskState));

                result = (string)ps.Runspace.SessionStateProxy.GetVariable("result");
            }

            return taskState;
        }

        /// <summary>
        /// Safe execute code by first waiting for guest operations ready
        /// </summary>
        /// <param name="script">Script to execute</param>
        private void SafeExecute(string script)
        {
            string waitScript = @"
Function Wait-GuestOperations() {
    $startTime = Get-Date
    do {
        $vm = Get-VM -Id $vm.Id
        if( ((Get-Date) - $startTime).TotalSeconds -gt $guestOperationsTimeoutSeconds) {
            throw 'wait for guest operations timed out'
        }
        Start-Sleep -Seconds 1
        $ready = $vm.Guest.ExtensionData.GuestOperationsReady
        $ready = $ready -and $vm.Guest.ExtensionData.GuestStateChangeSupported
        $ready = $ready -and ($vm.Guest.ExtensionData.ToolsRunningStatus -eq 'guestToolsRunning')
    } while (!$ready)
}

Wait-GuestOperations
";

            // Scenario 1: If "operation not allowed" is caused by Ansible initiating repetitive commands, 
            //   - It implies that the coordination with Ansible is already broken.
            //   - Retry does not really help in this case.
            //   - It will eventually turn to be a failure and cause auto shutdown.
            // Scenario 2 (not applicable any more):
            // Guest operations fails with these, retry may fix it. Likely caused by VM reboot. 
            //   - "operation not allowed
            //   - "Copy-VMGuestFile		A specified parameter was not correct"
            for (var i = 0; i <= GUEST_OPERATIONS_MAX_RETRIES; i++)
            {
                if (i > 0)
                {
                    LogWarning(string.Format("Retry #{0} after 10 seconds", i));
                    System.Threading.Thread.Sleep(10000);
                }

                Clear();

                lock (psLock)
                {
                    ps.AddScript(waitScript + script);
                    // Occasionally it throws: 
                    // 1) 7/10/2019 4:06:00 PM	Invoke-VMScript		The operation is not allowed in the current state.
                    // 2) 7/22/2019 2:10:11 PM	Copy-VMGuestFile		A specified parameter was not correct: 
                    //   Detailed error: WARNING: The guest OS for the virtual machine 'win-2016-06-IWDm' is unknown. The operation may fail.
                    //   Potential cause: VM is too busy or VMware Tools is not ready completely.
                    ps.Invoke();
                    if (ps.Streams.Error.Count > 0)
                    {
                        var ex = ps.Streams.Error[0].Exception;
                        var msg = ex.ToString();
                        if (msg.Contains("The operation is not allowed in the current state"))
                            LogWarning("Encountered operation not allowed while running: " + script);
                        //else if(msg.Contains("Copy-VMGuestFile") && msg.Contains("A specified parameter was not correct"))
                        //    LogWarning("Encountered specified parameter not correct during file copy: " + script);
                        else
                            throw ex;
                    }
                    else
                    {
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Connect to vCenter
        /// </summary>
        public void Connect()
        {
            string script = "";
            // Note: Set-ExecutionPolicy is not supported in Linux. Only run it for Windows.
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                script += @"
Set-ExecutionPolicy Unrestricted -Force | Out-Null
";
            }

            script += @"
Set-PowerCLIConfiguration -Scope User -WebOperationTimeoutSeconds -1 -ParticipateInCEIP:$false -InvalidCertificateAction Ignore -DefaultVIServerMode Single -Confirm:$false | Out-Null
$vcPasswordSecure = ConvertTo-SecureString $vcPassword -AsPlainText -Force
$vcCred = New-Object System.Management.Automation.PSCredential ($vcUser, $vcPasswordSecure)
$server = Connect-VIServer -Server $vcServer -Credential $vcCred
if(!$?) {
    exit
}
if($vmName) {
    $vm = Get-VM -Name $vmName
}
if(!$vm -and $vmId) {
    $vm = Get-VM -Id $vmId
}
if(($vm | Measure-Object).Count -gt 1) {
    if($vmName) {
        throw ('Multiple VMs of name {0} found' -f $vmName)
    } elseif($vmId) {
        throw ('Multiple VMs of id {0} found' -f $vmId)
    }
}
$vmPasswordSecure = ConvertTo-SecureString $vmPassword -AsPlainText -Force
$vmCred = New-Object System.Management.Automation.PSCredential ($vmUser, $vmPasswordSecure)
";

            // On occasion, it throws 7/24/2019 8:25:20 AM	Connect-VIServer		No such host is known
            // This is due to VPN connectivity and DNS server issue
            LogInformation("Connecting..");
            Clear();

            lock (psLock)
            {
                ps.AddScript(script);
                ps.Invoke();
                if (ps.Streams.Error.Count > 0)
                {
                    throw ps.Streams.Error[0].Exception;
                }
            }

            // Clear user and password to improve security
            ps.Runspace.SessionStateProxy.SetVariable("vcUser", null);
            ps.Runspace.SessionStateProxy.SetVariable("vcPassword", null);
            ps.Runspace.SessionStateProxy.SetVariable("vcPasswordSecure", null);
            ps.Runspace.SessionStateProxy.SetVariable("vmUser", null);
            ps.Runspace.SessionStateProxy.SetVariable("vmPassword", null);
            ps.Runspace.SessionStateProxy.SetVariable("vmPasswordSecure", null);
        }

        /// <summary>
        /// Validate guest identity by VM name
        /// </summary>
        /// <param name="vcServer">vCenter name to match</param>
        /// <param name="vmName">VM name to match</param>
        /// <returns></returns>
        public bool ValidateGuestIdentityByName(string vcServer, string vmName)
        {
            return vcServer == this.vcServer && vmName == this.vmName;
        }

        /// <summary>
        /// Validate guest identity by VM ID
        /// </summary>
        /// <param name="vcServer">vCenter name to match</param>
        /// <param name="vmId">VM ID to match</param>
        /// <returns></returns>
        public bool ValidateGuestIdentityById(string vcServer, string vmId)
        {
            return vcServer == this.vcServer && vmId == this.vmId;
        }

        /// <summary>
        /// Validate guest credential by logging in and retrieve hostname
        /// </summary>
        /// <param name="vmUser"></param>
        /// <param name="vmPassword"></param>
        /// <param name="hostname"></param>
        /// <returns></returns>
        public bool ValidateGuestCredential(string vmUser, string vmPassword, out string hostname)
        {
            ps.Runspace.SessionStateProxy.SetVariable("scriptText", "hostname");

            string script = @"
$task = $vm | Invoke-VMScript -GuestCredential $vmCred -ScriptType $scriptType -ScriptText $scriptText -RunAsync
";
            bool valid;
            try
            {
                SafeExecute(script);
                WaitTask(out hostname);
                valid = true;
            }
            catch
            {
                valid = false;
                hostname = null;
            }

            return valid;
        }

        #region Logging
        public string Id
        {
            get
            {
                return string.Format("{0}:{1}", vcServer, vmName != null ? vmName : vmId);
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

        /// <summary>
        /// Clear powershell streams and commands
        /// Note: This must be called before exeucting any code.
        /// Otherwise, past commands will get executed again and past errors will affect error handling.
        /// </summary>
        private void Clear()
        {
            ps.Streams.ClearStreams();
            ps.Commands.Clear();
        }

        /// <summary>
        /// Copy file content from server to guest
        /// </summary>
        /// <param name="content">File content to copy to guest</param>
        /// <param name="serverPath">File path on server as source</param>
        /// <param name="guestPath">File path on guest as destination</param>
        private void CopyToGuest(string content, string serverPath, string guestPath)
        {
            LogInformation(string.Format("Saving content to {0}", serverPath));
            File.WriteAllText(serverPath, content);

            CopyToGuest(serverPath, guestPath);
        }

        /// <summary>
        /// Copy a file from server to guest
        /// </summary>
        /// <param name="serverPath">File path on server as source</param>
        /// <param name="guestPath">File path on guest as destination</param>
        private void CopyToGuest(string serverPath, string guestPath)
        {
            string script = string.Format("$null = $vm | Copy-VMGuestFile -GuestCredential $vmCred -Source {0} -Destination {1} -LocalToGuest -Force",
                serverPath, guestPath);

            LogInformation(string.Format("Copying {0} to {1}", serverPath, guestPath));
            SafeExecute(script);
        }

        /// <summary>
        /// Check whether decoded command contains reboot code
        /// </summary>
        /// <param name="decodedCommand">Decoded command to check</param>
        /// <returns></returns>
        private bool IsRebootCommand(string decodedCommand)
        {
            // win_reboot uses code below:
            // Set-StrictMode -Version Latest
            // shutdown /r /t 2 /c "Reboot initiated by Ansible"
            // If(-not $?) { If(Get - Variable LASTEXITCODE - ErrorAction SilentlyContinue) { exit $LASTEXITCODE } Else { exit 1 } }
            // Note: Windows can be rebooted by shutdown or Restart-Computer
            string[] lines = decodedCommand.Split('\n');
            string shutdownLine = lines.FirstOrDefault(x => x.StartsWith("shutdown ", StringComparison.OrdinalIgnoreCase));
            string restartLine = lines.FirstOrDefault(x => x.StartsWith("Restart-Computer", StringComparison.OrdinalIgnoreCase));
            bool isReboot = shutdownLine != null || restartLine != null;

            return isReboot;
        }

        /// <summary>
        /// Get prefix to file names
        /// </summary>
        /// <param name="commandId"></param>
        /// <returns></returns>
        private string GetFilePrefix(string commandId)
        {
            string vmPrefix = vmName != null ? vmName.Trim().Split(" ")[0] : this.vmId;
            return string.Format("{0}_{1}", vmPrefix, commandId);
        }

        /// <summary>
        /// Execute ansible script in VM
        /// </summary>
        /// <param name="command">Command to run</param>
        /// <param name="payloadPathOnServer">Path to the payload on server side</param>
        /// <param name="base64Payload">Indicate whether payload is Base64 encoded</param>
        /// <param name="hasOutput">Indicate whether it will produce output</param>
        /// <returns></returns>
        public int ExecuteAnsibleScript(string commandId, string command,
            string payloadPathOnServer, bool base64Payload, out bool hasOutput)
        {
            int returnCode;
            string invoker = null;
            bool isReboot = false;

            string filePrefix = GetFilePrefix(commandId);

            string stdoutPathGuest = Path.Join(guestRoot, filePrefix + "_stdout.txt");
            string stderrPathGuest = Path.Join(guestRoot, filePrefix + "_stderr.txt");
            string payloadPathGuest = Path.Join(guestRoot, filePrefix + "_payload.txt");
            string cmdPathGuest = Path.Join(guestRoot, filePrefix + "_command.ps1");
            string invokerPathGuest = Path.Join(guestRoot, filePrefix + "_invoker.ps1");

            string cmdPathServer = Path.Join(Path.GetTempPath(), filePrefix + "_command.ps1");
            string invokerPathServer = Path.Join(Path.GetTempPath(), filePrefix + "_invoker.ps1");

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
                    string innerEncodedCommand = WsmanRuntime.GetCommandInput(command, "-EncodedCommand");
                    string innerDecodedCommand = WsmanRuntime.DecodeCommand(innerEncodedCommand, WsmanRuntime.CommandType.InvokeCommand);

                    LogInformation("InnerDecodedCommand: " + innerDecodedCommand);

                    isReboot = IsRebootCommand(innerDecodedCommand);
                    if (isReboot)
                    {
                        LogInformation("InnerDecodedCommand contains reboot");
                    }

                    // Directly invoke
                    invoker = string.Format("{0} 1>{1} 2>{2}", command, stdoutPathGuest, stderrPathGuest);
                }
                else
                {
                    // If not Base64 encoded, it is a simple command that should be invoked using "PowerShell -Command {}"
                    if (!base64Payload)
                    {
                        // Single command e.g. (Get-WmiObject -ClassName Win32_OperatingSystem).LastBootUpTime
                        invoker = string.Format("PowerShell -NoProfile -NonInteractive -ExecutionPolicy Unrestricted -Command {{{0}}} 1>{1} 2>{2}",
                            command, stdoutPathGuest, stderrPathGuest);
                    }
                    else
                    {
                        throw new WinRM2VMWException(string.Format("Unrecognised command format: {0}", command));
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
                    CopyToGuest(payloadPathOnServer, payloadPathGuest);

                    // Command: PowerShell -NoProfile -NonInteractive -ExecutionPolicy Unrestricted -EncodedCommand XXX
                    // Note: -NoProfile must be used in the out-most PowerShell
                    invoker = string.Format("PowerShell -NoProfile -NonInteractive -ExecutionPolicy Unrestricted -Command {{ Get-Content {0} | {1} 1>{2} 2>{3} }}",
                        payloadPathGuest, command, stdoutPathGuest, stderrPathGuest);
                }
                else if (command.StartsWith("begin", StringComparison.OrdinalIgnoreCase))
                {
                    // When command is "begin { } process { } end { }" which expects pipeline input
                    // Payload is Base64 encoded
                    // Note: This is used by win_file to copy files
                    if (base64Payload)
                    {
                        // Copy command script to guest
                        CopyToGuest(command, cmdPathServer, cmdPathGuest);
                        // Copy payload file to guest
                        CopyToGuest(payloadPathOnServer, payloadPathGuest);
                        // Call command script file, then pipe payload into it
                        // Note: -NoProfile must be used in the out-most PowerShell
                        invoker = string.Format("PowerShell -NoProfile -NonInteractive -ExecutionPolicy Unrestricted -Command {{ Get-Content {0} | {1} 1>{2} 2>{3} }}",
                            payloadPathGuest, cmdPathGuest, stdoutPathGuest, stderrPathGuest);
                    }
                    else
                    {
                        throw new WinRM2VMWException(string.Format("Unrecognised command format: {0}", command));
                    }
                }
                else
                {
                    throw new WinRM2VMWException(string.Format("Unrecognised command format: {0}", command));
                }
            }

            // Save invoker for debug mode only
            if (debug)
            {
                CopyToGuest(invoker, invokerPathServer, invokerPathGuest);
            }

            // No output if reboot
            hasOutput = !isReboot;

            LogInformation(invoker);

            ps.Runspace.SessionStateProxy.SetVariable("scriptText", invoker);

            // Invoke script asynchronously
            string script = @"
$task = $vm | Invoke-VMScript -GuestCredential $vmCred -ScriptType $scriptType -ScriptText $scriptText -RunAsync
";
            // Wait for guest operations then execute
            SafeExecute(script);

            // Wait for task
            if (isReboot)
            {
                // Reboot does not need to wait, and the task state can be either Error or Success
                returnCode = 0;
            }
            else
            {
                string result;
                var taskState = WaitTask(out result);
                returnCode = (taskState == "Success") ? 0 : 1;
            }

            return returnCode;
        }

        /// <summary>
        /// Get ansible script output and error
        /// </summary>
        /// <param name="stdout">Standard output stream</param>
        /// <param name="stderr">Standard error stream</param>
        public void GetAnsibleScriptOutput(string commandId, out string stdout, out string stderr)
        {
            string filePrefix = GetFilePrefix(commandId);
            string stdoutPathGuest = Path.Join(guestRoot, filePrefix + "_stdout.txt");
            string stderrPathGuest = Path.Join(guestRoot, filePrefix + "_stderr.txt");
            string cmdPathGuest = Path.Join(guestRoot, filePrefix + "_command.ps1");
            string payloadPathGuest = Path.Join(guestRoot, filePrefix + "_payload.txt");
            string invokerPathGuest = Path.Join(guestRoot, filePrefix + "_invoker.ps1");

            string stdoutPathServer = Path.Join(Path.GetTempPath(), filePrefix + "_stdout.txt");
            string stderrPathServer = Path.Join(Path.GetTempPath(), filePrefix + "_stderr.txt");

            // Delete old outputs on server
            if (File.Exists(stdoutPathServer))
                File.Delete(stdoutPathServer);
            if (File.Exists(stderrPathServer))
                File.Delete(stderrPathServer);

            // Copy outputs from guest to server then remove used files
            string destinationPathServer = Path.GetTempPath();
            ps.Runspace.SessionStateProxy.SetVariable("destinationPathServer", destinationPathServer);
            ps.Runspace.SessionStateProxy.SetVariable("stdoutPathGuest", stdoutPathGuest);
            ps.Runspace.SessionStateProxy.SetVariable("stderrPathGuest", stderrPathGuest);

            string script = @"
$null = $vm | Copy-VMGuestFile -GuestCredential $vmCred -Source $stdoutPathGuest,$stderrPathGuest -Destination $destinationPathServer -GuestToLocal -Force
";
            if (!debug)
            {
                string scriptText = string.Format("del {0} & del {1} & del {2} & del {3} & del {4}",
                    cmdPathGuest, payloadPathGuest, stdoutPathGuest, stderrPathGuest, invokerPathGuest);
                ps.Runspace.SessionStateProxy.SetVariable("scriptText", scriptText);
                script += @"
$task = $vm | Invoke-VMScript -ScriptText $scriptText -GuestCredential $vmCred -ScriptType Bat -RunAsync
";
            }

            // Wait for guest operations then execute
            SafeExecute(script);

            if (!debug)
            {
                string result;
                var taskState = WaitTask(out result, 60);
            }

            // Read stdout and stderr from copied files
            stdout = File.Exists(stdoutPathServer) ? File.ReadAllText(stdoutPathServer) : null;
            stderr = File.Exists(stderrPathServer) ? File.ReadAllText(stderrPathServer) : null;
        }

        /// <summary>
        /// Dispose resources
        /// </summary>
        public void Dispose()
        {
            lock (psLock)
            {
                // Dispose PS but do not disconnect from vCenter
                // Because it will disconnect other VcClient using the same credential
                ps.Dispose();
            }
        }
    }
}
