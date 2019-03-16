using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
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
        private string _baseOutputPath = "/root/vmwaretunnel-";
        public string SessionId { get; set; }
        public string TempPath { get { return _baseOutputPath; } }


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

            guest = (GuestOperationsManager)client.GetView(client.ServiceContent.GuestOperationsManager, null);
            fileManager = (GuestFileManager)client.GetView(guest.FileManager, null);
            processManager = (GuestProcessManager)client.GetView(guest.ProcessManager, null);

            SessionId = RandomString(6,true);
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

            _vmRootUsername = vmRootUsername;
            _vmRootPassword = vmRootPassword;

            _vm = new ManagedObjectReference()
            {
                Value = moref,
                Type = "VirtualMachine"
            };

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

        public string ExecuteCommand(string command)
        {
            System.Diagnostics.Debug.WriteLine("Executing command " + command);

            var auth = new NamePasswordAuthentication()
            {
                Username = _vmRootUsername,
                Password = _vmRootPassword,
                InteractiveSession = false
            };

            fileManager.MakeDirectoryInGuest(_vm, auth, _baseOutputPath, false);
            /*
            try
            {
                //var tempDirectory = fileManager.CreateTemporaryDirectoryInGuest(_vm, auth, null, null, _baseOutputPath);
                
                var existingfiles = fileManager.ListFilesInGuest(_vm, auth, _baseOutputPath, null, null, "output");
                if (existingfiles.Files != null && existingfiles.Files.Count() == 1)
                {
                    fileManager.DeleteFileInGuest(_vm, auth, _baseOutputPath + "/output");
                }
                throw Exception()
            }
            catch (Exception e)
            {
                fileManager.MakeDirectoryInGuest(_vm, auth, _baseOutputPath, false);
                System.Diagnostics.Debug.WriteLine(e.Message);
            }*/

            var sshCommand = command;

            var pid = processManager.StartProgramInGuest(_vm, auth, new GuestProgramSpec
            {
                ProgramPath = "/usr/bin/sshpass",//"/usr/bin/ssh",
                //Use & to also pipe errors
                Arguments = "-p " + _executingCredentials.Password + " ssh -o StrictHostKeyChecking=no " + _executingCredentials.Username + "@0.0.0.0 \"(" + sshCommand + ")\" &> " + _baseOutputPath + "/output",//"-i key root@localhost \"(" + sshCommand + ")\" &> test",
                WorkingDirectory = "/root"
            });

            GuestProcessInfo[] process;
            do
            {
                process = processManager.ListProcessesInGuest(_vm,
                auth, new long[] { pid });
            }
            while (process.Count() != 1 && process[0].EndTime == null);
            Thread.Sleep(3000);
            var files = fileManager.ListFilesInGuest(_vm, auth, _baseOutputPath, null, null, "output");

            if (files.Files != null && files.Files.Count() == 1)
            {
                var result = fileManager.InitiateFileTransferFromGuest(_vm, auth, _baseOutputPath  + "/output" );

                HttpClient httpClient = new HttpClient();

                var sshOutput = httpClient.GetAsync(result.Url).GetAwaiter().GetResult();

                var output = sshOutput.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                if (output.Contains('=') && !output.Contains(" "))
                {
                    output = output.Split('=').Last();
                }

                //Delete temp file path
                //fileManager.DeleteFileInGuest(_vm, auth, _baseOutputPath + "/output");
                
                fileManager.DeleteDirectoryInGuest(_vm, auth, _baseOutputPath, true);

                return (output);
            }
            else
            {
                return "";
            }
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
            client.Logout();
        }
    }
}