using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Microsoft.Extensions.Logging;
using VMware.Vim;
using CloudOSTunnel.Services.WSMan;

namespace CloudOSTunnel.Clients
{
    public partial class VMWareClient : IDisposable, IWSManLogging<VMWareClient>
    {
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
                    RefreshClient();
                    return _client;
                }
            }
        }
        public readonly string HostName;
        public readonly string MoRef;
        #endregion vCenter Attributes

        #region Guest Attributes
        public readonly string HostName;
        public string GuestFamily { get; }
        public string GuestFullName { get; }
        #endregion Guest Attributes

        #region Linux Variables
        public string EntryMessage { get; set; }
        private string _baseOutputPath = "/tmp/vmwaretunnel-";
        public int SSHMessageCount = 0;
        public string TempPath { get { return _baseOutputPath; } }
        public bool ExecuteAsRoot { get; set; }
        public string PrivateFileLocation;
        public string PublicKey;
        #endregion Linux Variables

        public VMWareClient(ILoggerFactory loggerFactory, string serviceUrl, string vcenterUsername, string vcenterPassword, 
            string vmRootUsername, string vmRootPassword, string vmName, string moref)
        {
            _serviceUrl = serviceUrl;
            _vCenterUsername = vcenterUsername;
            _vCenterPassword = vcenterPassword;
            RefreshClient();

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

            guest = (GuestOperationsManager)client.GetView(client.ServiceContent.GuestOperationsManager, null);
            fileManager = (GuestFileManager)client.GetView(guest.FileManager, null);
            processManager = (GuestProcessManager)client.GetView(guest.ProcessManager, null);

            SessionId = RandomString(6, true);
            _baseOutputPath = _baseOutputPath + SessionId;

            _executingCredentials = new NamePasswordAuthentication()
            {
                Username = vmRootUsername,
                Password = vmRootPassword
            };

            if (!GuestFamily.ToLower().Contains("window"))
            {
                SessionId = RandomString(6, true);
                _baseOutputPath = _baseOutputPath + SessionId;

                ExecuteAsRoot = true;

                SetupKey();
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

        public VMWareClient(ILoggerFactory loggerFactory, string serviceUrl, string vcenterUsername, string vcenterPassword, 
            string vmUsername, string vmPassword, string moref)
        {
            this.Logger = loggerFactory.CreateLogger<VMWareClient>();
            this._serviceUrl = serviceUrl;

            _serviceUrl = serviceUrl;
            _vCenterUsername = vcenterUsername;
            _vCenterPassword = vcenterPassword;
            RefreshClient();

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

            guest = (GuestOperationsManager)client.GetView(client.ServiceContent.GuestOperationsManager, null);
            fileManager = (GuestFileManager)client.GetView(guest.FileManager, null);
            processManager = (GuestProcessManager)client.GetView(guest.ProcessManager, null);

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

                SetupKey();
                HostName = ExecuteLinuxCommand("hostname", out _, out _);
                EntryMessage = ExecuteLinuxCommand("sleep 0", out _, out _);
            }
            else
            {
                HostName = ExecuteWindowsCommand("hostname").stdout.Trim();
            }
        }

        public void RefreshClient()
        {
            _client = new VimClientImpl();
            _client.IgnoreServerCertificateErrors = true;
            _client.Connect(_serviceUrl);

            var session = _client.Login(_vCenterUsername, _vCenterPassword);
            lastAuthenticatedCheck = DateTime.Now;
        }

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

        public void SetupKey()
        {
            System.Diagnostics.Debug.WriteLine("Setting up key");

            FileManager.MakeDirectoryInGuest(_vm, _executingCredentials, _baseOutputPath, false);

            var pid = ProcessManager.StartProgramInGuest(_vm, _executingCredentials, new GuestProgramSpec
            {
                ProgramPath = "/usr/bin/ssh-keygen",
                Arguments = "-t rsa -N \"\" -f " + _baseOutputPath + "/vmwaretunnelkey",
                WorkingDirectory = "/tmp"
            });

            AwaitProcess(pid);

            PublicKey = ReadFile(_vm, _executingCredentials, _baseOutputPath + "/vmwaretunnelkey.pub");

            pid = ProcessManager.StartProgramInGuest(_vm, _executingCredentials, new GuestProgramSpec
            {
                ProgramPath = DoesFileExist("/bin", "cat") ? "/bin/cat" : "/usr/bin/cat",
                Arguments = _baseOutputPath + "/vmwaretunnelkey.pub >> ~/.ssh/authorized_keys",
                WorkingDirectory = "/tmp"
            });

            AwaitProcess(pid);
            PrivateFileLocation = _baseOutputPath + "/vmwaretunnelkey";
        }

        public bool DoesFileExist(string folderPath, string fileName)
        {
            var result = FileManager.ListFilesInGuest(_vm, _executingCredentials, folderPath, null, 200, fileName);

            if(result == null)
            {
                return false;
            }

            foreach (var file in result.Files)
            {
                Console.WriteLine(file.Path);
            }

            return result.Files.Where(gfe => gfe.Path == fileName).Count() > 0;
        }

        public bool AwaitProcess(long pid)
        {

            GuestProcessInfo[] process;
            do
            {
                process = ProcessManager.ListProcessesInGuest(_vm,
                _executingCredentials, new long[] { pid });
            }
            while (process.Count() != 1 || process[0].EndTime == null);

            return true;
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

            isComplete = AwaitProcess(pid);

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

        private string ReadFile(ManagedObjectReference vm, NamePasswordAuthentication auth, string guestPath)
        {

            var result = FileManager.InitiateFileTransferFromGuest(_vm, auth, filePath);

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

        public async Task<string> UploadFile(string serverPath, byte[] file, string guestPath)
        {
            System.Diagnostics.Debug.WriteLine("Uploading file to " + guestPath);

            var result = FileManager.InitiateFileTransferToGuest(_vm, _executingCredentials, path, new GuestFileAttributes() { }, file.LongLength, true);
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

        public void CleanUpLinuxTempFiles()
        {
            FileManager.DeleteDirectoryInGuest(_vm, _executingCredentials, _baseOutputPath, true);
        }

        public void Logout()
        {
            try
            {
                client.Logout();
            }
            catch (Exception e)
            {
                Console.WriteLine("Failed to end session correctly, check " + _vm.Value + " at directory " + _baseOutputPath);
            }
        }
    }
}
