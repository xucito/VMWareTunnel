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
using VMware.Vim;

namespace CloudOSTunnel.Clients
{
    public class VMWareClient
    {
        private readonly GuestOperationsManager guest;
        private readonly GuestFileManager fileManager;
        private GuestProcessManager processManager;
        private readonly string _serviceUrl = "";
        private readonly string _vCenterUsername = "";
        private readonly string _vCenterPassword = "";
        private ManagedObjectReference _vm;
        private NamePasswordAuthentication _executingCredentials;
        private VimClientImpl client;
        public readonly string HostName;
        public readonly string MoRef;
        private string _baseOutputPath = "/tmp/vmwaretunnel-";
        public string SessionId { get; set; }
        public string TempPath { get { return _baseOutputPath; } }
        public bool ExecuteAsRoot { get; set; }
        public string PrivateFileLocation;
        public string PublicKey;
        public string FullVMName { get; set; }
        public string EntryMessage { get; set; }
        public int SSHMessageCount = 0;
        public string GuestFamily { get; }
        public string GuestFullName { get; }

        public VMWareClient(string serviceUrl, string vcenterUsername, string vcenterPassword, string vmRootUsername, string vmRootPassword, string vmName, string moref)
        {
            client = new VimClientImpl();
            client.IgnoreServerCertificateErrors = true;
            client.Connect(serviceUrl);

            var session = client.Login(vcenterUsername, vcenterPassword);
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

            ExecuteAsRoot = true;

            SetupKey();
            EntryMessage = ExecuteCommand("sleep 0", out _, out _);
            EntryMessage = EntryMessage.Replace("Connection to 127.0.0.1 closed.", "");
            //EntryMessage = EntryMessage.Trim(new char[] { '\n', '\r' });
            SSHMessageCount = EntryMessage.Count();
            HostName = ExecuteCommand("hostname", out _, out _);
            var test = ExecuteCommand("sleep 0", out _, out _);
            //HostName = HostName.Substring(SSHMessageCount);
        }


        public VMWareClient(string serviceUrl, string vcenterUsername, string vcenterPassword, string vmUsername, string vmPassword, string moref)
        {

            client = new VimClientImpl();
            client.IgnoreServerCertificateErrors = true;
            client.Connect(serviceUrl);

            var session = client.Login(vcenterUsername, vcenterPassword);

            _vm = new ManagedObjectReference()
            {
                Value = moref,
                Type = "VirtualMachine"
            };

            var foundVM = (VirtualMachine)client.GetView(_vm, null);

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

            ExecuteAsRoot = vmUsername == null ? true : false;

            SetupKey();
            HostName = ExecuteCommand("hostname", out _, out _);
            EntryMessage = ExecuteCommand("sleep 0", out _, out _);
            MoRef = moref;
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

            fileManager.MakeDirectoryInGuest(_vm, _executingCredentials, _baseOutputPath, false);

            var pid = processManager.StartProgramInGuest(_vm, _executingCredentials, new GuestProgramSpec
            {
                ProgramPath = "/usr/bin/ssh-keygen",
                Arguments = "-t rsa -N \"\" -f " + _baseOutputPath + "/vmwaretunnelkey",
                WorkingDirectory = "/tmp"
            });

            AwaitProcess(pid);

            PublicKey = ReadFile(_vm, _executingCredentials, _baseOutputPath + "/vmwaretunnelkey.pub");

            pid = processManager.StartProgramInGuest(_vm, _executingCredentials, new GuestProgramSpec
            {
                ProgramPath = "/usr/bin/cat",
                Arguments = _baseOutputPath + "/vmwaretunnelkey.pub >> ~/.ssh/authorized_keys",
                WorkingDirectory = "/tmp"
            });

            AwaitProcess(pid);
            PrivateFileLocation = _baseOutputPath + "/vmwaretunnelkey";
        }

        public bool AwaitProcess(long pid)
        {

            GuestProcessInfo[] process;
            do
            {
                process = processManager.ListProcessesInGuest(_vm,
                _executingCredentials, new long[] { pid });
            }
            while (process.Count() != 1 || process[0].EndTime == null);

            return true;
        }

        public string ExecuteCommand(string command, out bool isComplete, out long pid, string commandUniqueIdentifier = null)
        {
            if (commandUniqueIdentifier == null)
            {
                commandUniqueIdentifier = Guid.NewGuid().ToString();
            }
            System.Diagnostics.Debug.WriteLine("Executing command " + command);
            var outputFileName = "output-" + commandUniqueIdentifier;

            var sshCommand = command;

            processManager = (GuestProcessManager)client.GetView(guest.ProcessManager, null);
            //  if (command.Split(' ')[0] != "/bin/sh")
            // {

            var fullCommand = "/usr/bin/ssh" + " -i " + PrivateFileLocation + " -o StrictHostKeyChecking=no  -t -t  " + "127.0.0.1 \"(" + sshCommand.Replace("\"", "\\\"").Replace("\'", "\\\'") + ")\" &> " + _baseOutputPath + "/" + outputFileName;

            System.Diagnostics.Debug.WriteLine("Full command path:  " + fullCommand);
            pid = processManager.StartProgramInGuest(_vm, _executingCredentials, new GuestProgramSpec
            {
                ProgramPath = "/usr/bin/ssh",
                Arguments = "-i " + PrivateFileLocation + " -o StrictHostKeyChecking=no -t -t " + "127.0.0.1 \"(" + sshCommand.Replace("\"", "\\\"") + ")\" > " + _baseOutputPath + "/" + outputFileName + " 2>&1",//"-i key root@localhost \"(" + sshCommand + ")\" &> test",
                WorkingDirectory = "/tmp"
            });

            // Thread.Sleep(1000);

            isComplete = AwaitProcess(pid);

            var files = fileManager.ListFilesInGuest(_vm, _executingCredentials, _baseOutputPath, null, null, outputFileName);

            if (files.Files != null && files.Files.Count() == 1)
            {
                var output = ReadFile(_vm, _executingCredentials, _baseOutputPath + "/" + outputFileName);

                if (output.Contains('=') && !output.Contains(" "))
                {
                    output = output.Split('=').Last();
                }

                //fileManager.DeleteDirectoryInGuest(_vm, auth, _baseOutputPath, true);

                fileManager.DeleteFileInGuest(_vm, _executingCredentials, _baseOutputPath + "/" + outputFileName);

                return (output.Trim(new char[] { '\n', '\r' }).Substring(SSHMessageCount).Replace("Connection to 127.0.0.1 closed.", ""));
            }
            else
            {
                return "";
            }
        }

        private string ReadFile(ManagedObjectReference vm, NamePasswordAuthentication auth, string filePath)
        {

            var result = fileManager.InitiateFileTransferFromGuest(_vm, auth, filePath);

            using (var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (sender, certificate, chain, sslPolicyErrors) => true
            })
            {
                HttpClient httpClient = new HttpClient(handler);

                var sshOutput = httpClient.GetAsync(result.Url).GetAwaiter().GetResult();

                return sshOutput.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
        }

        public async Task<string> UploadFile(string filePath, byte[] file, string path)
        {
            System.Diagnostics.Debug.WriteLine("Uploading file to " + path);

            var result = fileManager.InitiateFileTransferToGuest(_vm, _executingCredentials, path, new GuestFileAttributes() { }, file.LongLength, true);
            using (var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (sender, certificate, chain, sslPolicyErrors) => true
            })
            {
                HttpClient httpClient = new HttpClient(handler);
                ByteArrayContent byteContent = new ByteArrayContent(file);
                var sshOutput = await httpClient.PutAsync(result, byteContent);
                if (sshOutput.IsSuccessStatusCode)
                {
                    return filePath;
                }
                else
                {
                    var message = (await sshOutput.Content.ReadAsStringAsync());
                    System.Diagnostics.Debug.WriteLine("Failed to upload with error " + message);
                    throw new Exception("Failed to upload file to " + message);
                }
            }
        }

        public void Logout()
        {
            try
            {

                fileManager.DeleteDirectoryInGuest(_vm, _executingCredentials, _baseOutputPath, true);
                client.Logout();
            }
            catch (Exception e)
            {
                Console.WriteLine("Failed to end session correctly, check " + _vm.Value + " at directory " + _baseOutputPath);
            }
        }
    }
}
