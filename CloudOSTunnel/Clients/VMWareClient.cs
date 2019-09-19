using System;
using System.IO;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Web;
using Microsoft.Extensions.Logging;
using VMware.Vim;
using CloudOSTunnel.Services.WSMan;
using CloudOSTunnel.Services;

namespace CloudOSTunnel.Clients
{
    public class VMWareClient : IDisposable
    {
        // Indicate VM is rebooting
        public bool IsRebooting { get; private set; }
        // Indicate VM is executing command
        public bool IsExecuting { get; private set; }

        #region vCenter Attributes
        // Maximum time for VM guest to stop services and start rebooting
        // - Note that after installed updates, it can take long to shutdown
        private const int GUEST_TIME_TO_SHUTDOWN_SECONDS = 1800;
        // Maximum time for VM guest to run a program
        // - Note that it can take long to install updates
        private const int GUEST_OPERATIONS_TASK_TIMEOUT_SECONDS = 7200;
        // Maximum time to wait for guest operations to be ready, note the below:
        // - VMware Tools takes time to load completely and guest operations may experience transient states e.g. up,down,up..
        // - After Windows patching, it can take long time to boot into operating system, hence a high timeout value
        private const int GUEST_OPERATIONS_TIMEOUT_SECONDS = 3600;
       
        private GuestOperationsManager _guestOperationsManager;
        private GuestOperationsManager GuestOperationsManager
        {
            get
            {
                if (_guestOperationsManager == null || (DateTime.Now - lastAuthenticatedCheck).TotalMinutes > 30)
                {
                    _guestOperationsManager = (GuestOperationsManager)client.GetView(_client.ServiceContent.GuestOperationsManager, null);
                }
                return _guestOperationsManager;
            }
        }

        private GuestFileManager _fileManager;
        private GuestFileManager FileManager
        {
            get
            {
                if (_fileManager == null || (DateTime.Now - lastAuthenticatedCheck).TotalMinutes > 30)
                {
                    _fileManager = (GuestFileManager)client.GetView(GuestOperationsManager.FileManager, null);
                }
                return _fileManager;
            }
        }

        private GuestProcessManager _processManager;
        private GuestProcessManager ProcessManager
        {
            get
            {
                if (_processManager == null || (DateTime.Now - lastAuthenticatedCheck).TotalMinutes > 30)
                {
                    _processManager = (GuestProcessManager)client.GetView(GuestOperationsManager.ProcessManager, null);
                }
                return _processManager;
            }
        }

        private readonly string _serviceUrl = "";
        private readonly string _vCenterUsername = "";
        private readonly string _vCenterPassword = "";
        private ManagedObjectReference _vm;
        private NamePasswordAuthentication _executingCredentials;
        private VimClientImpl _client { get; set; }
        private DateTime lastAuthenticatedCheck;
        private VimClientImpl client
        {
            get
            {
                //Only recheck the last authenticated every 30 minutes
                if ((DateTime.Now - lastAuthenticatedCheck).TotalMinutes < 30)
                {
                    return _client;
                }

                var testSession = (SessionManager)_client.GetView(_client.ServiceContent.SessionManager, null);
                var isActive = testSession.CurrentSession;

                if (isActive != null)
                {
                    return _client;
                }
                else
                {
                    InitialiseClient();
                    return _client;
                }
            }
        }
        public readonly string HostName;
        public readonly string MoRef;
        public string FullVMName { get; }
        public string GuestFamily { get; }
        public string GuestFullName { get; }
        #endregion vCenter Attributes

        #region Linux Guest Attributes
        public string EntryMessage { get; set; }
        private string _baseOutputPath = "/tmp/vmwaretunnel-";
        public int SSHMessageCount = 0;
        public string TempPath { get { return _baseOutputPath; } }
        public bool ExecuteAsRoot { get; set; }
        public string PrivateFileLocation;
        public string PublicKey;
        public string SessionId { get; }
        #endregion Linux Guest Attributes

        #region Windows Guest Attributes
        // Root path in Windows guest (to store temporal files)
        private const string windowsGuestRoot = @"C:\Users\Public\Documents";

        // Get Windows guest full path for a file name
        private string GetWindowsGuestPath(string filename)
        {
            return string.Format(@"{0}\{1}", windowsGuestRoot, filename);
        }

        internal string VmUser
        {
            get { return _executingCredentials.Username; }
        }

        internal string VmPassword
        {
            get { return _executingCredentials.Password; }
        }

        #endregion Windows Guest Attributes

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

        #region Guest Info
        public string GetGuestOs()
        {
            if (GuestFamily.Contains("Windows"))
            {
                return GuestFullName;
            }

            string cmd;

            if(GuestFullName.Contains("CentOS"))
            {
                // CentOS Linux release 7.6.1810 (Core)
                cmd = "cat /etc/centos-release";
            }
            else if(GuestFullName.Contains("Red Hat Enterprise Linux"))
            {
                // Red Hat Enterprise Linux Server release 7.4 (Maipo)
                cmd = "cat /etc/redhat-release";
            }
            else if(GuestFullName.Contains("SUSE"))
            {
                // SUSE Linux Enterprise Server 11 (x86_64)
                // VERSION = 11
                // PATCHLEVEL = 4
                cmd = "cat /etc/SuSE-release";
            }
            else if(GuestFullName.Contains("Ubuntu"))
            {
                // Description:    Ubuntu 18.04.3 LTS
                cmd = "lsb_release -d";
            }
            else
            {
                throw new CloudOSTunnelException(string.Format("Unsupported guest OS {0}", GuestFullName));
            }

            return ExecuteLinuxCommand(cmd, out _, out _);
        }

        #endregion Guest Info
        
        // Create a client using VM name
        public VMWareClient(ILoggerFactory loggerFactory, string serviceUrl, string vcenterUsername, string vcenterPassword, 
            string vmUsername, string vmPassword, string vmName, string moref)
        {
            this.IsRebooting = false;
            this.IsExecuting = false;
            this.Logger = loggerFactory.CreateLogger<VMWareClient>();

            _serviceUrl = serviceUrl;
            _vCenterUsername = vcenterUsername;
            _vCenterPassword = vcenterPassword;
            InitialiseClient();

            NameValueCollection filter = new NameValueCollection();
            filter.Add("Name", String.Format("^{0}$", Regex.Escape(vmName)));
            var foundVM = (VirtualMachine)client.FindEntityView(typeof(VirtualMachine), null, filter, null);
            GuestFamily = foundVM.Guest.GuestFamily;
            GuestFullName = foundVM.Guest.GuestFullName;
            FullVMName = foundVM.Name;

            if (foundVM != null)
            {
                if (moref != "" && moref != null)
                {
                    if (foundVM.MoRef.Value != moref)
                    {
                        client.Logout();
                        throw new Exception(vmName + " does not match moref " + moref);
                    }
                    else
                    {
                        _vm = new ManagedObjectReference()
                        {
                            Value = moref,
                            Type = "VirtualMachine"
                        };
                    }
                }
                else
                {
                    _vm = foundVM.MoRef;
                }

                MoRef = foundVM.MoRef.Value;
            }
            else
            {
                client.Logout();
                throw new Exception(vmName + " not found.");
            }

            _executingCredentials = new NamePasswordAuthentication()
            {
                Username = vmUsername,
                Password = vmPassword
            };

            if (!GuestFamily.ToLower().Contains("window"))
            {
                SessionId = RandomString(6, true);
                _baseOutputPath = _baseOutputPath + SessionId;

                ExecuteAsRoot = true;

                SetupLinuxKey();
                EntryMessage = ExecuteLinuxCommand("sleep 0", out _, out _);
                EntryMessage = EntryMessage.Replace("Connection to 127.0.0.1 closed.", "");
                //EntryMessage = EntryMessage.Trim(new char[] { '\n', '\r' });
                SSHMessageCount = EntryMessage.Count();
                HostName = ExecuteLinuxCommand("hostname", out _, out _);
                var test = ExecuteLinuxCommand("sleep 0", out _, out _);
                //HostName = HostName.Substring(SSHMessageCount);
            }
            else
            {
                HostName = ExecuteWindowsCommand("hostname").stdout.Trim();
            }
        }

        // Create a client using VM moref
        public VMWareClient(ILoggerFactory loggerFactory, string serviceUrl, string vcenterUsername, string vcenterPassword, 
            string vmUsername, string vmPassword, string moref)
        {
            this.IsRebooting = false;
            this.IsExecuting = false;
            this.Logger = loggerFactory.CreateLogger<VMWareClient>();

            _serviceUrl = serviceUrl;
            _vCenterUsername = vcenterUsername;
            _vCenterPassword = vcenterPassword;
            InitialiseClient();

            _vm = new ManagedObjectReference()
            {
                Value = moref,
                Type = "VirtualMachine"
            };

            var foundVM = (VirtualMachine)client.GetView(_vm, null);

            MoRef = moref;
            FullVMName = foundVM.Name;
            GuestFamily = foundVM.Guest.GuestFamily;
            GuestFullName = foundVM.Guest.GuestFullName;

            SessionId = RandomString(6, true);
            _baseOutputPath = _baseOutputPath + SessionId;

            _executingCredentials = new NamePasswordAuthentication()
            {
                Username = vmUsername,
                Password = vmPassword
            };

            if (!GuestFamily.ToLower().Contains("window"))
            {
                SessionId = RandomString(6, true);
                _baseOutputPath = _baseOutputPath + SessionId;

                ExecuteAsRoot = vmUsername == null ? true : false;

                SetupLinuxKey();
                HostName = ExecuteLinuxCommand("hostname", out _, out _);
                EntryMessage = ExecuteLinuxCommand("sleep 0", out _, out _);
            }
            else
            {
                HostName = ExecuteWindowsCommand("hostname").stdout.Trim();
            }
        }

        /// <summary>
        /// Initialise client
        /// </summary>
        public void InitialiseClient()
        {
            _client = new VimClientImpl();
            _client.IgnoreServerCertificateErrors = true;
            _client.Connect(_serviceUrl);

            var session = _client.Login(_vCenterUsername, _vCenterPassword);
            lastAuthenticatedCheck = DateTime.Now;
        }

        /// <summary>
        /// Await guest process completion
        /// </summary>
        /// <param name="pid">Process ID to await</param>
        /// <param name="exitCode">Exit code of process</param>
        /// <param name="timeoutSeconds">Timeout in seconds</param>
        /// <returns>True if process exited, otherwise false</returns>
        public bool AwaitProcess(long pid, out int? exitCode, int timeoutSeconds = GUEST_OPERATIONS_TASK_TIMEOUT_SECONDS)
        {
            GuestProcessInfo[] process = null;
            Stopwatch stopWatch = new Stopwatch();
            stopWatch.Start();

            do
            {
                // Reduce number of calls to vCenter
                Thread.Sleep(1000);

                if (stopWatch.Elapsed.TotalSeconds > timeoutSeconds)
                {
                    exitCode = null;
                    return false;
                }
                try
                {
                    // "The operation is not allowed in the current state" will be thrown on occassion
                    process = ProcessManager.ListProcessesInGuest(_vm, _executingCredentials, new long[] { pid });
                }
                catch(VimException ex)
                {
                    if(ex.Message.Contains("The operation is not allowed in the current state"))
                    {
                        LogWarning("Encountered operation not allowed while awaiting process, ignore and retry");
                        continue;
                    }
                    else
                    {
                        throw ex;
                    }
                }
            } while (process.Count() != 1 || process[0].EndTime == null);

            // If the process was started using StartProgramInGuest then the process exit code 
            // will be available if queried within 5 minutes after it completes. 
            exitCode = process[0].ExitCode.Value;

            return true;
        }

        /// <summary>
        /// Await guest to shutdown
        /// </summary>
        private void AwaitGuestShutdown()
        {
            bool shutdown;
            Stopwatch stopWatch = new Stopwatch();
            stopWatch.Start();

            LogInformation("Awaiting guest shutdown");

            do
            {
                var vm = (VirtualMachine)client.GetView(_vm, null);
                if (stopWatch.Elapsed.TotalSeconds > GUEST_TIME_TO_SHUTDOWN_SECONDS)
                {
                    var msg = string.Format("Awaiting guest shutdown timed out with ToolsRunningStatus {0}", vm.Guest.ToolsRunningStatus);
                    LogError(msg);
                    throw new CloudOSTunnelException(msg);
                }

                shutdown = vm.Guest.ToolsRunningStatus == "guestToolsNotRunning"; 
            } while (!shutdown);
        }

        /// <summary>
        /// Await guest operations to be ready for any change
        /// </summary>
        private void AwaitGuestOperations(int timeoutSeconds = GUEST_OPERATIONS_TIMEOUT_SECONDS)
        {
            bool ready;
            Stopwatch stopWatch = new Stopwatch();
            stopWatch.Start();

            LogInformation("Awaiting guest operations");

            do
            {
                var vm = (VirtualMachine)client.GetView(_vm, null);
                if(stopWatch.Elapsed.TotalSeconds > GUEST_OPERATIONS_TIMEOUT_SECONDS)
                {
                    throw new CloudOSTunnelException("Awaiting guest operations timed out");
                }
                var supportRebootState = vm.Guest.GuestStateChangeSupported;
                var readyState = vm.Guest.GuestOperationsReady;

                ready = supportRebootState.HasValue && supportRebootState.Value;
                ready = ready && readyState.HasValue && readyState.Value;
                ready = ready && vm.Guest.ToolsRunningStatus == "guestToolsRunning";
            } while (!ready);
        }

        #region File Operation
        /// <summary>
        /// Check whether a file exists in a folder in guest
        /// </summary>
        /// <param name="folderPath"></param>
        /// <param name="fileName"></param>
        /// <returns></returns>
        public bool FileExist(string folderPath, string fileName)
        {
            var result = FileManager.ListFilesInGuest(_vm, _executingCredentials, folderPath, null, 200, fileName);

            if (result == null)
            {
                return false;
            }

            foreach (var file in result.Files)
            {
                LogInformation(file.Path);
            }

            return result.Files.Where(gfe => gfe.Path == fileName).Count() > 0;
        }

        /// <summary>
        /// Read file content from guest
        /// </summary>
        /// <param name="vm"></param>
        /// <param name="auth"></param>
        /// <param name="guestPath"></param>
        /// <returns></returns>
        private string ReadFile(ManagedObjectReference vm, NamePasswordAuthentication auth, string guestPath)
        {
            var result = FileManager.InitiateFileTransferFromGuest(_vm, auth, guestPath);

            using (var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (sender, certificate, chain, sslPolicyErrors) => true
            })
            {
                HttpClient httpClient = new HttpClient(handler);

                var fileTransferOutput = httpClient.GetAsync(result.Url).GetAwaiter().GetResult();

                return fileTransferOutput.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
        }

        /// <summary>
        /// Upload text content from server to a file in guest
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
                    var fileTransferRef = FileManager.InitiateFileTransferToGuest(_vm,
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
        /// Upload array of bytes to a file in guest
        /// </summary>
        /// <param name="serverPath"></param>
        /// <param name="file"></param>
        /// <param name="guestPath"></param>
        /// <returns></returns>
        public async Task<string> UploadFile(string serverPath, byte[] file)
        {
            System.Diagnostics.Debug.WriteLine("Uploading file to " + serverPath);

            var result = FileManager.InitiateFileTransferToGuest(_vm, _executingCredentials, serverPath, new GuestFileAttributes() { }, file.LongLength, true);
            using (var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (sender, certificate, chain, sslPolicyErrors) => true
            })
            {
                HttpClient httpClient = new HttpClient(handler);
                httpClient.Timeout = System.Threading.Timeout.InfiniteTimeSpan;

                ByteArrayContent byteContent = new ByteArrayContent(file);
                var sshOutput = await httpClient.PutAsync(result, byteContent);
                if (sshOutput.IsSuccessStatusCode)
                {
                    return serverPath;
                }
                else
                {
                    var message = (await sshOutput.Content.ReadAsStringAsync());
                    System.Diagnostics.Debug.WriteLine("Failed to upload with error " + message);
                    throw new Exception("Failed to upload file to " + message);
                }
            }
        }
        #endregion File Operation

        #region Linux Guest
        /// <summary>
        /// Generate a random string
        /// </summary>
        /// <param name="size"></param>
        /// <param name="lowerCase"></param>
        /// <returns></returns>
        public string RandomString(int size, bool lowerCase)
        {
            StringBuilder builder = new StringBuilder();
            Random random = new Random();
            char ch;
            for (int i = 0; i < size; i++)
            {
                ch = Convert.ToChar(Convert.ToInt32(Math.Floor(26 * random.NextDouble() + 65)));
                builder.Append(ch);
            }
            if (lowerCase)
                return builder.ToString().ToLower();
            return builder.ToString();
        }

        /// <summary>
        /// Setup a key for Linux guest authentication
        /// </summary>
        public void SetupLinuxKey()
        {
            System.Diagnostics.Debug.WriteLine("Setting up key");

            FileManager.MakeDirectoryInGuest(_vm, _executingCredentials, _baseOutputPath, false);

            var pid = ProcessManager.StartProgramInGuest(_vm, _executingCredentials, new GuestProgramSpec
            {
                ProgramPath = "/usr/bin/ssh-keygen",
                Arguments = "-t rsa -N \"\" -f " + _baseOutputPath + "/vmwaretunnelkey",
                WorkingDirectory = "/tmp"
            });

            if(!AwaitProcess(pid, out _))
            {
                Console.WriteLine("Failed to wait till command ended.");
            }

            PublicKey = ReadFile(_vm, _executingCredentials, _baseOutputPath + "/vmwaretunnelkey.pub");

            pid = ProcessManager.StartProgramInGuest(_vm, _executingCredentials, new GuestProgramSpec
            {
                ProgramPath = FileExist("/bin", "cat") ? "/bin/cat" : "/usr/bin/cat",
                Arguments = _baseOutputPath + "/vmwaretunnelkey.pub >> ~/.ssh/authorized_keys",
                WorkingDirectory = "/tmp"
            });

            if (!AwaitProcess(pid, out _))
            {
                Console.WriteLine("Failed to wait till command ended.");
            }
            PrivateFileLocation = _baseOutputPath + "/vmwaretunnelkey";
        }

        public string ExecuteLinuxCommand(string command, out bool isComplete, out long pid, string commandUniqueIdentifier = null)
        {
            if (commandUniqueIdentifier == null)
            {
                commandUniqueIdentifier = Guid.NewGuid().ToString();
            }
            System.Diagnostics.Debug.WriteLine("Executing command " + command);
            var outputFileName = "output-" + commandUniqueIdentifier;

            var sshCommand = command;

            var fullCommand = "/usr/bin/ssh" + " -i " + PrivateFileLocation + " -o StrictHostKeyChecking=no  -t -t  " + "127.0.0.1 \"(" + sshCommand.Replace("\"", "\\\"").Replace("\'", "\\\'") + ")\" &> " + _baseOutputPath + "/" + outputFileName;

            System.Diagnostics.Debug.WriteLine("Full command path:  " + fullCommand);
            pid = ProcessManager.StartProgramInGuest(_vm, _executingCredentials, new GuestProgramSpec
            {
                ProgramPath = "/usr/bin/ssh",
                Arguments = "-i " + PrivateFileLocation + " -o StrictHostKeyChecking=no -t -t " + "127.0.0.1 \"(" + sshCommand.Replace("\"", "\\\"") + ")\" > " + _baseOutputPath + "/" + outputFileName + " 2>&1",//"-i key root@localhost \"(" + sshCommand + ")\" &> test",
                WorkingDirectory = "/tmp"
            });

            // Thread.Sleep(1000);

            isComplete = AwaitProcess(pid, out _);

            var files = FileManager.ListFilesInGuest(_vm, _executingCredentials, _baseOutputPath, null, null, outputFileName);

            if (files.Files != null && files.Files.Count() == 1)
            {
                var output = ReadFile(_vm, _executingCredentials, _baseOutputPath + "/" + outputFileName);

                if (output.Contains('=') && !output.Contains(" "))
                {
                    output = output.Split('=').Last();
                }

                //fileManager.DeleteDirectoryInGuest(_vm, auth, _baseOutputPath, true);

                FileManager.DeleteFileInGuest(_vm, _executingCredentials, _baseOutputPath + "/" + outputFileName);

                return (output.Trim(new char[] { '\n', '\r' }).Substring(SSHMessageCount).Replace("Connection to 127.0.0.1 closed.", ""));
            }
            else
            {
                return "";
            }
        }

        public void CleanUpLinuxTempFiles()
        {
            FileManager.DeleteDirectoryInGuest(_vm, _executingCredentials, _baseOutputPath, true);
        }
        #endregion Linux Guest

        #region Windows Guest
        /// <summary>
        /// Get prefix to be used for WSMan files
        /// </summary>
        /// <param name="commandId">Command ID of WSMan protocol</param>
        /// <returns></returns>
        private string GetWsmanFilePrefix(string commandId = null)
        {
            string vmPrefix = FullVMName.Trim().Split(" ")[0];
            return commandId != null ? string.Format("{0}_{1}", vmPrefix, commandId) : vmPrefix;
        }

        /// <summary>
        /// Invoke a powershell command in windows guest
        /// </summary>
        /// <param name="fullCommand">Full command to invoke</param>
        /// <param name="isReboot">Indicate the command is to reboot</param>
        /// <param name="stdoutPathGuest">Path to redirect stdout in guest</param>
        /// <param name="stderrPathGuest">Path to redirect stderr in guest</param>
        /// <returns></returns>
        private CommandResult InvokeWindowsCommand(string fullCommand, bool isReboot,
            string stdoutPathGuest = null, string stderrPathGuest = null)
        {
            if(stdoutPathGuest == null && stderrPathGuest != null)
            {
                throw new CloudOSTunnelException("Both stdout and stderr need to be specified or neither.");
            }
            if (stdoutPathGuest != null && stderrPathGuest == null)
            {
                throw new CloudOSTunnelException("Both stdout and stderr need to be specified or neither.");
            }

            bool hasOutput;
            long pid = -1;
            int? exitCode;
            string stdout, stderr;

            try
            {
                if (isReboot)
                {
                    AwaitGuestOperations();
                    try
                    {
                        // Indicate command execution started
                        this.IsExecuting = true;
                        pid = ProcessManager.StartProgramInGuest(_vm, _executingCredentials, new GuestProgramSpec
                        {
                            ProgramPath = @"cmd.exe",
                            Arguments = "/C " + fullCommand,
                            WorkingDirectory = windowsGuestRoot
                        });
                    }
                    catch (VimException ex)
                    {
                        if (ex.Message.Contains("The guest operations agent could not be contacted"))
                        {
                            // Reboot can causes agent not contactable as VMware Tools shutdown too quickly
                            // - This is expected behavior
                            LogWarning("The guest operations agent could not be contacted after initiated reboot. No action required.");
                        }
                        else
                        {
                            // Throw other unexpected exceptions
                            throw new CloudOSTunnelException(string.Format("{0} {1}", ex.Message, ex.StackTrace));
                        }
                    }
                    // Indicate reboot has started
                    this.IsRebooting = true;
                    LogInformation("Reboot has started");
                    // Wait for guest to stop VMware Tools and shutdown
                    AwaitGuestShutdown();
                    LogInformation("Guest is down");
                    // Wait for guest operations to come back after reboot
                    AwaitGuestOperations();
                    LogInformation("Guest is up");
                    // Indicate reboot has completed
                    this.IsRebooting = false;
                    LogInformation("Reboot has completed"); 
                }
                else
                {
                    AwaitGuestOperations();
                    // Indicate command execution started
                    this.IsExecuting = true;
                    pid = ProcessManager.StartProgramInGuest(_vm, _executingCredentials, new GuestProgramSpec
                    {
                        ProgramPath = @"cmd.exe",
                        Arguments = "/C " + fullCommand,
                        WorkingDirectory = windowsGuestRoot
                    });
                }

                if (isReboot)
                {
                    // Assume success for reboot
                    exitCode = 0;
                    stdout = stderr = null;
                    hasOutput = false;
                }
                else
                {
                    hasOutput = true;
                    stdout = stderr = null;
                    if (AwaitProcess(pid, out exitCode))
                    {
                        if (stdoutPathGuest != null && stderrPathGuest != null)
                        {
                            LogInformation("Getting output and error from guest");
                            stdout = ReadFile(_vm, _executingCredentials, stdoutPathGuest);
                            LogInformation(string.Format("Obtained guest stdout: {0}", stdout));
                            stderr = ReadFile(_vm, _executingCredentials, stderrPathGuest);
                            LogInformation(string.Format("Obtained guest stderr: {0}", stderr));
                        }
                    }
                    else
                    {
                        throw new CloudOSTunnelException(string.Format("Guest process {0} timed out", pid));
                    }
                }
            }
            finally
            {
                // Must clear flags before exit
                this.IsExecuting = false;
                this.IsRebooting = false;
            }

            return new CommandResult
            {
                exitCode = exitCode.Value,
                stdout = stdout,
                stderr = stderr,
                hasOutput = hasOutput
            };
        }

        /// <summary>
        /// Wrap powershell command to formulate a full command
        /// </summary>
        /// <param name="psCommand"></param>
        /// <param name="stdoutPath"></param>
        /// <param name="stderrPath"></param>
        /// <returns></returns>
        private string WrapWindowsCommand(string psCommand, string stdoutPath = null, string stderrPath = null)
        {
            // To support script block, -Command must be quoted: -Command "& {command}"
            string fullCommand = string.Format(@"PowerShell -NoProfile -NonInteractive -ExecutionPolicy Unrestricted -Command ""& {{{0}}}""", psCommand);
            if (stdoutPath != null)
            {
                fullCommand += " 1>" + stdoutPath;
            }
            if (stderrPath != null)
            {
                fullCommand += " 2>" + stderrPath;
            }
            return fullCommand;
        }

        /// <summary>
        /// Delete files in Windows guest
        /// </summary>
        /// <param name="guestPaths">Guest files to delete (does not support folder)</param>
        /// <returns></returns>
        private CommandResult DeleteWindowsGuestFiles(string[] guestPaths)
        {
            if (guestPaths == null || guestPaths.Length == 0)
                throw new WSManException("Windows guest files to delete must be specified");

            string path = string.Join(",", guestPaths);
            string fullCommand = WrapWindowsCommand("Remove-Item -Path " + path + " -Confirm:$false");
            LogInformation(string.Format("Deleting files in Windows guest {0}", path));
            return InvokeWindowsCommand(fullCommand, false);
        }

        /// <summary>
        /// Execute simple Windows command without the use of WSMan
        /// </summary>
        /// <param name="command">Powershell command to execute</param>
        /// <returns></returns>
        public CommandResult ExecuteWindowsCommand(string command)
        {
            string vmPrefix = GetWsmanFilePrefix();
            
            string stdoutPathGuest = GetWindowsGuestPath(vmPrefix + "_stdout.txt");
            string stderrPathGuest = GetWindowsGuestPath(vmPrefix + "_stderr.txt");

            string fullCommand = WrapWindowsCommand(command, stdoutPathGuest, stderrPathGuest);

            LogInformation(fullCommand);

            // Invoke command and get result
            var result = InvokeWindowsCommand(fullCommand, false, stdoutPathGuest, stderrPathGuest);

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

            string[] filesToDelete = null;

            string filePrefix = GetWsmanFilePrefix(commandId);

            string stdoutPathGuest = GetWindowsGuestPath(filePrefix + "_stdout.txt");
            string stderrPathGuest = GetWindowsGuestPath(filePrefix + "_stderr.txt");
            string payloadPathGuest = GetWindowsGuestPath(filePrefix + "_payload.txt");
            string cmdPathGuest = GetWindowsGuestPath(filePrefix + "_command.ps1");

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
                        invoker = command;
                    }
                    else
                    {
                        // Directly invoke because it starts with PowerShell..
                        invoker = string.Format("{0} 1>{1} 2>{2}", command, stdoutPathGuest, stderrPathGuest);
                        filesToDelete = new string[] { stdoutPathGuest, stderrPathGuest };
                    }
                }
                else
                {
                    // If not Base64 encoded, it is a simple command that should be invoked using "PowerShell -Command {}"
                    if (!base64Payload)
                    {
                        // Single command e.g. (Get-WmiObject -ClassName Win32_OperatingSystem).LastBootUpTime
                        invoker = WrapWindowsCommand(command, stdoutPathGuest, stderrPathGuest);
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
                    invoker = WrapWindowsCommand(psCommand, stdoutPathGuest, stderrPathGuest);
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
                        invoker = WrapWindowsCommand(psCommand, stdoutPathGuest, stderrPathGuest);
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
                result = InvokeWindowsCommand(invoker, isReboot);
            }
            else
            {
                // Invoke command and wait for completion
                result = InvokeWindowsCommand(invoker, isReboot, stdoutPathGuest, stderrPathGuest);
            }

            if(filesToDelete != null)
            {
                // Delete temp files
                DeleteWindowsGuestFiles(filesToDelete);
            }

            return result;
        }

        #endregion Windows Guest

        /// <summary>
        /// Log out from vCenter
        /// </summary>
        public void Logout()
        {
            try
            {
                client.Logout();
            }
            catch (Exception e)
            {
                LogError("Failed to end session correctly, check " + _vm.Value + " at directory " + _baseOutputPath);
            }
        }

        /// <summary>
        /// Erase critical data
        /// </summary>
        public void Dispose()
        {
            // Minimally erase credential
            _executingCredentials.Username = null;
            _executingCredentials.Password = null;
            _executingCredentials = null;
        }
    }
}
