using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web;
using VMware.Vim;

namespace VMWareHypervisorTunnel.Clients
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

            ServicePointManager
                .ServerCertificateValidationCallback +=
                (sender, cert, chain, sslPolicyErrors) => true;

            client = new VimClientImpl();
            client.Connect(serviceUrl);

            var session = client.Login(vcenterUsername, vcenterPassword);
            NameValueCollection filter = new NameValueCollection();
            filter.Add("Name", String.Format("^{0}$", Regex.Escape(vmName)));
            //filter.Add("name", vmName);
            var foundVM = client.FindEntityView(typeof(VirtualMachine), null, filter, null);
            FullVMName = ((VirtualMachine)foundVM).Name;
            if (foundVM != null)
            {
                if(moref != "" && moref != null)
                {
                    if(foundVM.MoRef.Value != moref)
                    {
                        client.Logout();
                        throw new Exception(vmName +" does not match moref " + moref);
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

            ServicePointManager
                .ServerCertificateValidationCallback +=
                (sender, cert, chain, sslPolicyErrors) => true;

            client = new VimClientImpl();
            client.Connect(serviceUrl);

            var session = client.Login(vcenterUsername, vcenterPassword);

            _vm = new ManagedObjectReference()
            {
                Value = moref,
                Type = "VirtualMachine"
            };

            var foundVM = client.GetView(_vm,null);

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
                WorkingDirectory = "/root"
            });
            
            AwaitProcess(pid);

            PublicKey = ReadFile(_vm, auth, _baseOutputPath + "/vmwaretunnelkey.pub");

            pid = processManager.StartProgramInGuest(_vm, auth, new GuestProgramSpec
            {
                ProgramPath = "/usr/bin/cat",
                Arguments =  _baseOutputPath + "/vmwaretunnelkey.pub >> ~/.ssh/authorized_keys",
                WorkingDirectory = "/root"
            });

            AwaitProcess(pid);


            PrivateFileLocation = _baseOutputPath + "/vmwaretunnelkey";
            /*
            if (files.Files != null && files.Files.Count() == 1)
            {
                var result = fileManager.InitiateFileTransferFromGuest(_vm, auth, _baseOutputPath + "/keyfile");

                HttpClient httpClient = new HttpClient();

                var sshOutput = httpClient.GetAsync(result.Url).GetAwaiter().GetResult();

                var keyfile = sshOutput.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                _keyFileLocation = _baseOutputPath + "/keyfile.pub";

                //fileManager.DeleteDirectoryInGuest(_vm, auth, _baseOutputPath, true);
                
            }
            else
            {
            }*/
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
            //Thread.Sleep(5000);

            return true;
        }

        public string ExecuteCommand(string command)
        {
            System.Diagnostics.Debug.WriteLine("Executing command " + command);

            var auth = new NamePasswordAuthentication()
            {
                Username = _vmRootUsername,
                Password = _vmRootPassword,
                InteractiveSession = false
            };

            //fileManager.MakeDirectoryInGuest(_vm, auth, _baseOutputPath, false);

            var sshCommand = command;

            long pid = processManager.StartProgramInGuest(_vm, auth, new GuestProgramSpec
            {
                ProgramPath = "/usr/bin/ssh",
                Arguments = "-i " + PrivateFileLocation + " -o StrictHostKeyChecking=no " +"127.0.0.1 \"(" + sshCommand + ")\" &> " + _baseOutputPath + "/output",//"-i key root@localhost \"(" + sshCommand + ")\" &> test",
                WorkingDirectory = "/root"
            });

            /*
            long pid = processManager.StartProgramInGuest(_vm, auth, new GuestProgramSpec
            {
                ProgramPath = "/usr/bin/sshpass",
                Arguments = "-p " + _executingCredentials.Password + " ssh -o StrictHostKeyChecking=no " + _executingCredentials.Username + "@0.0.0.0 \"(" + sshCommand + ")\" &> " + _baseOutputPath + "/output",//"-i key root@localhost \"(" + sshCommand + ")\" &> test",
                WorkingDirectory = "/root"
            });*/
            AwaitProcess(pid);

            var files = fileManager.ListFilesInGuest(_vm, auth, _baseOutputPath, null, null, "output");

            if (files.Files != null && files.Files.Count() == 1)
            {
                var output = ReadFile(_vm, auth, _baseOutputPath + "/output");

                if (output.Contains('=') && !output.Contains(" "))
                {
                    output = output.Split('=').Last();
                }

                //fileManager.DeleteDirectoryInGuest(_vm, auth, _baseOutputPath, true);

                fileManager.DeleteFileInGuest(_vm, auth, _baseOutputPath + "/output");

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

        public string UploadFile(string filePath, byte[] file, string path)
        {
            System.Diagnostics.Debug.WriteLine("Uploading file");

            var auth = new NamePasswordAuthentication()
            {
                Username = _vmRootUsername,
                Password = _vmRootPassword,
                InteractiveSession = false
            };

            var result = fileManager.InitiateFileTransferToGuest(_vm, auth, path, new GuestFileAttributes() { }, file.LongLength, true);

            HttpClient httpClient = new HttpClient();
            ByteArrayContent byteContent = new ByteArrayContent(file);
            var sshOutput = httpClient.PutAsync(result, byteContent).GetAwaiter().GetResult();
            return filePath;
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
            //fileManager.DeleteFileInGuest(_vm, auth, "/.ssh/authorized_keys/keyfile.pub");
            client.Logout();
        }
    }
}