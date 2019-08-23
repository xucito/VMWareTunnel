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
        private readonly GuestProcessManager processManager;
        private readonly string _serviceUrl = "";
        private readonly string _vCenterUsername = "";
        private readonly string _vCenterPassword = "";
        private ManagedObjectReference _vm;
        private string _vmRootUsername;
        private string _vmRootPassword;
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

        public VMWareClient(string serviceUrl, string vcenterUsername, string vcenterPassword, string vmRootUsername, string vmRootPassword, string vmName, string moref, string vmUsername = null, string vmPassord = null)
        {
            ServicePointManager
                .ServerCertificateValidationCallback +=
                (sender, cert, chain, sslPolicyErrors) => true;

            ServicePointManager.SecurityProtocol = SecurityProtocolType.Ssl3 | SecurityProtocolType.Tls | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12;

            client = new VimClientImpl();
            client.Connect(serviceUrl);

            var session = client.Login(vcenterUsername, vcenterPassword);
            NameValueCollection filter = new NameValueCollection();
            filter.Add("Name", String.Format("^{0}$", Regex.Escape(vmName)));
            var foundVM = client.FindEntityView(typeof(VirtualMachine), null, filter, null);
            FullVMName = ((VirtualMachine)foundVM).Name;
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

            if (vmUsername != null)
            {
                _executingCredentials = new NamePasswordAuthentication()
                {
                    Username = vmUsername,
                    Password = vmPassord
                };
            }
            else
            {
                _executingCredentials = new NamePasswordAuthentication()
                {
                    Username = vmRootUsername,
                    Password = vmRootPassword
                };
            }

            ExecuteAsRoot = vmUsername == null ? true : false;

            _vmRootUsername = vmRootUsername;
            _vmRootPassword = vmRootPassword;

            SetupKey();
            HostName = GetHostName();
        }


        public VMWareClient(string serviceUrl, string vcenterUsername, string vcenterPassword, string vmRootUsername, string vmRootPassword, string moref, string vmUsername = null, string vmPassord = null)
        {
            ServicePointManager
                .ServerCertificateValidationCallback +=
                (sender, cert, chain, sslPolicyErrors) => true;

            ServicePointManager.SecurityProtocol = SecurityProtocolType.Ssl3 | SecurityProtocolType.Tls | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12;


            client = new VimClientImpl();
            client.Connect(serviceUrl);

            var session = client.Login(vcenterUsername, vcenterPassword);

            _vm = new ManagedObjectReference()
            {
                Value = moref,
                Type = "VirtualMachine"
            };

            var foundVM = client.GetView(_vm, null);

            FullVMName = ((VirtualMachine)foundVM).Name;

            guest = (GuestOperationsManager)client.GetView(client.ServiceContent.GuestOperationsManager, null);
            fileManager = (GuestFileManager)client.GetView(guest.FileManager, null);
            processManager = (GuestProcessManager)client.GetView(guest.ProcessManager, null);

            SessionId = RandomString(6, true);
            _baseOutputPath = _baseOutputPath + SessionId;

            if (vmUsername != null)
            {
                _executingCredentials = new NamePasswordAuthentication()
                {
                    Username = vmUsername,
                    Password = vmPassord
                };
            }
            else
            {
                _executingCredentials = new NamePasswordAuthentication()
                {
                    Username = vmRootUsername,
                    Password = vmRootPassword
                };
            }

            ExecuteAsRoot = vmUsername == null ? true : false;

            _vmRootUsername = vmRootUsername;
            _vmRootPassword = vmRootPassword;

            SetupKey();
            HostName = GetHostName();
            MoRef = moref;
        }

        public string GetHostName()
        {
            return ExecuteCommand("hostname");
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

            var auth = new NamePasswordAuthentication()
            {
                Username = _vmRootUsername,
                Password = _vmRootPassword,
                InteractiveSession = false
            };

            fileManager.MakeDirectoryInGuest(_vm, auth, _baseOutputPath, false);

            var pid = processManager.StartProgramInGuest(_vm, auth, new GuestProgramSpec
            {
                ProgramPath = "/usr/bin/ssh-keygen",
                Arguments = "-t rsa -N \"\" -f " + _baseOutputPath + "/vmwaretunnelkey",
                WorkingDirectory = "/tmp"
            });

            AwaitProcess(pid);

            PublicKey = ReadFile(_vm, auth, _baseOutputPath + "/vmwaretunnelkey.pub");

            pid = processManager.StartProgramInGuest(_vm, auth, new GuestProgramSpec
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
            var auth = new NamePasswordAuthentication()
            {
                Username = _vmRootUsername,
                Password = _vmRootPassword,
                InteractiveSession = false
            };

            GuestProcessInfo[] process;
            do
            {
                process = processManager.ListProcessesInGuest(_vm,
                auth, new long[] { pid });
            }
            while (process.Count() != 1 || process[0].EndTime == null);

            return true;
        }

        public string ExecuteCommand(string command, string commandUniqueIdentifier = null)
        {
            if (commandUniqueIdentifier == null)
            {
                commandUniqueIdentifier = Guid.NewGuid().ToString();
            }
            System.Diagnostics.Debug.WriteLine("Executing command " + command);
            var outputFileName = "output-" + commandUniqueIdentifier;

            var auth = new NamePasswordAuthentication()
            {
                Username = _vmRootUsername,
                Password = _vmRootPassword,
                InteractiveSession = false
            };

            var sshCommand = command;
            long pid;
            var fullCommand = "/usr/bin/ssh" + " -i " + PrivateFileLocation + " -o StrictHostKeyChecking=no " + "127.0.0.1 \"(" + sshCommand.Replace("\"", "\\\"").Replace("\'", "\\\'") + ")\" &> " + _baseOutputPath + "/" + outputFileName;
            System.Diagnostics.Debug.WriteLine("Full command path:  " + fullCommand);

            //  if (command.Split(' ')[0] != "/bin/sh")
            // {
            pid = processManager.StartProgramInGuest(_vm, auth, new GuestProgramSpec
            {
                ProgramPath = "/usr/bin/ssh",
                Arguments = "-i " + PrivateFileLocation + " -o StrictHostKeyChecking=no -t -t " + "127.0.0.1 \"(" + sshCommand.Replace("\"", "\\\"") + ")\" &> " + _baseOutputPath + "/" + outputFileName,//"-i key root@localhost \"(" + sshCommand + ")\" &> test",
                WorkingDirectory = "/tmp"
            });

            Thread.Sleep(1000);

            AwaitProcess(pid);

            var files = fileManager.ListFilesInGuest(_vm, auth, _baseOutputPath, null, null, outputFileName);

            if (files.Files != null && files.Files.Count() == 1)
            {
                var output = ReadFile(_vm, auth, _baseOutputPath + "/" + outputFileName);

                if (output.Contains('=') && !output.Contains(" "))
                {
                    output = output.Split('=').Last();
                }

                fileManager.DeleteFileInGuest(_vm, auth, _baseOutputPath + "/" + outputFileName);

                return (output);
            }
            else
            {
                return "";
            }
        }

        private string ReadFile(ManagedObjectReference vm, NamePasswordAuthentication auth, string filePath)
        {
            var result = fileManager.InitiateFileTransferFromGuest(_vm, auth, filePath);

            HttpClient httpClient = new HttpClient();

            var sshOutput = httpClient.GetAsync(result.Url).GetAwaiter().GetResult();

            return sshOutput.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }

        public async Task<string> UploadFile(string filePath, byte[] file, string path)
        {
            System.Diagnostics.Debug.WriteLine("Uploading file to " + path);

            var auth = new NamePasswordAuthentication()
            {
                Username = _vmRootUsername,
                Password = _vmRootPassword,
                InteractiveSession = false
            };

            var result = fileManager.InitiateFileTransferToGuest(_vm, auth, path, new GuestFileAttributes() { }, file.LongLength, true);

            HttpClient httpClient = new HttpClient();
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

        public void Logout()
        {
            var auth = new NamePasswordAuthentication()
            {
                Username = _vmRootUsername,
                Password = _vmRootPassword,
                InteractiveSession = false
            };

            fileManager.DeleteDirectoryInGuest(_vm, auth, _baseOutputPath, true);
            client.Logout();
        }
    }
}
