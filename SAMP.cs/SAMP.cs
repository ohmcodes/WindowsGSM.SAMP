using System;
using System.IO;
using System.Diagnostics;
using System.Text;
using System.Net;
using System.Threading.Tasks;
using WindowsGSM.Functions;
using WindowsGSM.GameServer.Engine;
using WindowsGSM.GameServer.Query;
using System.Collections.Generic;
using System.Threading;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using System.Linq;
using Newtonsoft.Json.Linq;
using System.Security.Policy;
using System.ComponentModel;

namespace WindowsGSM.Plugins
{
    public class SAMP
    {
        // - Plugin Details
        public Plugin Plugin = new Plugin
        {
            name = "WindowsGSM.SAMP", // WindowsGSM.XXXX
            author = "ohmcodes",
            description = "WindowsGSM plugin for supporting SAMP Dedicated Server",
            version = "1.0.1",
            url = "https://github.com/ohmcodes/WindowsGSM.SAMP", // Github repository link (Best practice)
            color = "#FFBF00" // Color Hex
        };

        // - Standard Constructor and properties
        public SAMP(ServerConfig serverData) => _serverData = serverData;
        private readonly ServerConfig _serverData;
        public string Error, Notice;

        // - Game server Fixed variables
        public string StartPath => "samp-server.exe"; // Game server start path
        public string FullName = "SAMP Dedicated Server"; // Game server FullName
        public bool AllowsEmbedConsole = true;  // Does this server support output redirect?
        public int PortIncrements = 1; // This tells WindowsGSM how many ports should skip after installation
        public object QueryMethod = new A2S(); // Query method should be use on current server type. Accepted value: null or new A2S() or new FIVEM() or new UT3()

        // - Game server default values
        public string ServerName = "wgsm_samp_dedicated";
        public string Defaultmap = "San Andreas"; // Original (MapName)
        public string Maxplayers = "50"; // WGSM reads this as string but originally it is number or int (MaxPlayers)
        public string Port = "27015"; // WGSM reads this as string but originally it is number or int
        public string QueryPort = "27016"; // WGSM reads this as string but originally it is number or int (SteamQueryPort)
        public string Additional = string.Empty;


        private Dictionary<string, string> configData = new Dictionary<string, string>();


        // - Create a default cfg for the game server after installation
        public async void CreateServerCFG()
        {
            modifyConfigFile();
        }

        // - Start server function, return its Process to WindowsGSM
        public async Task<Process> Start()
        {
            string shipExePath = ServerPath.GetServersServerFiles(_serverData.ServerID, StartPath);
            if (!File.Exists(shipExePath))
            {
                Error = $"{Path.GetFileName(shipExePath)} not found ({shipExePath})";
                return null;
            }

            string param = string.Empty;

            param += $" {_serverData.ServerParam}";

            modifyConfigFile("rcon_password");

            Process p;
            if (!AllowsEmbedConsole)
            {
                p = new Process
                {
                    StartInfo =
                    {
                        WorkingDirectory = ServerPath.GetServersServerFiles(_serverData.ServerID),
                        FileName = ServerPath.GetServersServerFiles(_serverData.ServerID, StartPath),
                        Arguments = param,
                        WindowStyle = ProcessWindowStyle.Minimized,
                        UseShellExecute = false
                    },
                    EnableRaisingEvents = true
                };
                p.Start();
            }
            else
            {
                p = new Process
                {
                    StartInfo =
                    {
                        WorkingDirectory = ServerPath.GetServersServerFiles(_serverData.ServerID),
                        FileName = ServerPath.GetServersServerFiles(_serverData.ServerID, StartPath),
                        Arguments = param,
                        WindowStyle = ProcessWindowStyle.Minimized,
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    },
                    EnableRaisingEvents = true
                };
                var serverConsole = new ServerConsole(_serverData.ServerID);
                p.OutputDataReceived += serverConsole.AddOutput;
                p.ErrorDataReceived += serverConsole.AddOutput;
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
            }

            return p;
        }

        // - Stop server function
        public async Task Stop(Process p)
        {
            await Task.Run(() =>
            {
                ServerConsole.SetMainWindow(p.MainWindowHandle);
                ServerConsole.SendWaitToMainWindow("^c");
            });
        }
        // - Install server function
        public async Task<Process> Install()
        {
            string downloadLink = await GetDownloadLink();
            string tarName = Path.GetFileName(new Uri(downloadLink).LocalPath);
            string tarPath = ServerPath.GetServersServerFiles(_serverData.ServerID,tarName);

            using (WebClient webClient = new WebClient())
            {
                try { await webClient.DownloadFileTaskAsync(downloadLink, tarPath); }
                catch
                {
                    Error = $"Fail to download {tarName}";
                    return null;
                }
            }

            // Extract zip
            if (!await FileManagement.ExtractZip(tarPath, Directory.GetParent(tarPath).FullName))
            {
                Error = $"Fail to extract {tarName}";
                return null;
            }

            // Delete zip
            await FileManagement.DeleteAsync(tarPath);

            return null;
        }

        // - Update server function
        public async Task<Process> Update(bool validate = false, string custum = null)
        {
            string directoryPath = ServerPath.GetServersServerFiles(_serverData.ServerID);
            string tmpConfigFile = ServerPath.GetServersServerFiles(_serverData.ServerID, @"tmp");
            string fileName = "server.cfg";
            string copySource = Path.Combine(directoryPath, fileName);
            string copyDestination = Path.Combine(tmpConfigFile, fileName);
            string restoreSource = Path.Combine(tmpConfigFile, fileName);
            // backup server.cfg
            Error = await AsyncBackupRestoreConfigFile(copySource, copyDestination, true, false);

            if (validate==true)
            {
                // reinstall
                DownloadExtract();
            }
            else
            {
                // Check if version is the same

                if(GetLocalBuild() == await GetRemoteBuild())
                {
                    Error = "Skipping Update Same Version.";
                }
                else
                {
                    // reinstall
                    DownloadExtract();

                    // TODO Add config comparison if there's a change
                }
            }

            // restore server.cfg
            Error = await AsyncBackupRestoreConfigFile(restoreSource, Path.Combine(directoryPath, fileName), true, false);

            return null;
        }
        public bool IsInstallValid()
        {
            return File.Exists(Functions.ServerPath.GetServersServerFiles(_serverData.ServerID, StartPath));
        }

        public bool IsImportValid(string path)
        {
            string exePath = Path.Combine(path, "PackageInfo.bin");
            Error = $"Invalid Path! Fail to find {Path.GetFileName(exePath)}";
            return File.Exists(exePath);
        }

        public string GetLocalBuild()
        {
            string exePath = ServerPath.GetServersServerFiles(_serverData.ServerID, StartPath);
            if (!File.Exists(exePath))
            {
                Error = $"{StartPath} is missing.";
                return string.Empty;
            }

            return FileVersionInfo.GetVersionInfo(exePath).ProductVersion;
        }

        public async Task<string> GetRemoteBuild()
        {
            try
            {
                using (WebClient webClient = new WebClient())
                {
                    string html = await webClient.DownloadStringTaskAsync("https://sa-mp.mp/downloads/");
                    string stringPattern = @"<a\s+[^>]*href=""(?<url>[^""]*svr[^""]*win32[^""]*)""[^>]*>";

                    MatchCollection matches = Regex.Matches(html, stringPattern);

                    foreach (Match match in matches)
                    {
                        GroupCollection groups = match.Groups;
                        string hrefValue = groups["url"].Value;

                        // Extract the version from the link
                        string fileName = Path.GetFileNameWithoutExtension(new Uri(hrefValue).LocalPath);

                        // Use a regular expression to extract "037"
                        string pattern = @"samp(\d+)_";
                        Match versionMatch = Regex.Match(fileName, pattern);

                        if (versionMatch.Success)
                        {
                            string extractedVersion = versionMatch.Groups[1].Value;
                            // Format the version as "0.0.0.0"
                            string formattedVersion = FormatVersion(extractedVersion);

                            return formattedVersion;
                        }
                        else
                        {
                            Console.WriteLine("Version not found in the input string.");
                        }
                    }
                }

                Error = "Fail to get remote build";
                return string.Empty;
            }
            catch
            {
                Error = "Fail to get remote build";
                return string.Empty;
            }
        }
        private async Task<string> AsyncBackupRestoreConfigFile(string source, string destination, bool overwrite, bool deleteSource)
        {
            return await Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(Path.GetDirectoryName(destination)))
                        Directory.CreateDirectory(Path.GetDirectoryName(destination));

                    File.Copy(source, destination, overwrite);

                    if (deleteSource)
                        Directory.Delete(source, true);
                }
                catch (Exception ex)
                {
                    return $"Error {ex.Message}";
                }
                return null;
            });
        }

        public async Task<string> GetDownloadLink()
        {
            // Sample download link https://gta-multiplayer.cz/downloads/samp037_svr_R2-2-1_win32.zip
            try
            {
                using (WebClient webClient = new WebClient())
                {
                    string userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/58.0.3029.110 Safari/537.3";
                    webClient.Headers.Add(HttpRequestHeader.UserAgent, userAgent);
                    string html = await webClient.DownloadStringTaskAsync("https://sa-mp.mp/create-server/");
                    string stringPattern = @"<a\s+[^>]*href=""(?<url>[^""]*svr[^""]*win32[^""]*)""[^>]*>";

                    MatchCollection matches = Regex.Matches(html, stringPattern);

                    foreach (Match match in matches)
                    {
                        // TODO Create download link cache to fix the issue being block when requesting too many
                        GroupCollection groups = match.Groups;
                        return groups["url"].Value;
                    }
                }

                Error = "Fail to get download link";
                return string.Empty;
            }
            catch
            {
                Error = "Fail to get download link";
                return string.Empty;
            }
        }

        private void modifyConfigFile(string exclude="")
        {
            // Define the path to the configuration file
            string filePath = Path.Combine(ServerPath.GetServersServerFiles(_serverData.ServerID), "server.cfg");

            // Read all lines from the CFG file
            List<string> lines = File.ReadAllLines(filePath).ToList();

            // Define the keys and values you want to modify
            Dictionary<string, string> modifications = new Dictionary<string, string>
            {
                {"rcon_password", _serverData.GetRCONPassword().ToString()},
                {"maxplayers", _serverData.ServerMaxPlayer},
                {"port", int.Parse(_serverData.ServerPort).ToString()},
                {"hostname", _serverData.ServerName},
                {"announce", "1"},
                {"chatlogging", "1"}
            };

            // Modify the specified keys in the list of lines
            foreach (var modification in modifications)
            {
                string key = modification.Key;
                string newValue = modification.Value;
                Console.WriteLine(key);
                for (int i = 0; i < lines.Count; i++)
                {
                    string[] parts = lines[i].Split(new[] { ' ' }, 2);
                    Console.WriteLine(parts[0]);
                    Console.WriteLine(parts[1]);
                    if (parts.Length == 2 && parts[0] == key && key!= exclude)
                    {
                        Console.WriteLine(lines[i]);
                        // Update the value for the specified key
                        lines[i] = $"{key} {newValue}";
                        Console.WriteLine(lines[i]);
                        break;
                    }
                }
            }

            // Save the modified lines back to the CFG file
            File.WriteAllLines(filePath, lines);
        }

        private string FormatVersion(string version)
        {
            
            // Ensure the version has at least one digit
            string paddedVersion = string.IsNullOrEmpty(version) ? "0" : version;

            // Pad the version with zeros or trim if there are more than four digits
            paddedVersion = (paddedVersion + "0000").Substring(0, 4);

            // Format the version as "0,X,Y,0"
            string formattedVersion = $"{paddedVersion[0]}, {paddedVersion[1]}, {paddedVersion[2]}, {paddedVersion[3]}";

            return formattedVersion;
        }

        private async void DownloadExtract()
        {
            Console.WriteLine("Downloading");
            string downloadLink = await GetDownloadLink();

            string userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/58.0.3029.110 Safari/537.3";
            string fileName = Path.GetFileName(new Uri(downloadLink).LocalPath);
            string serverPath = ServerPath.GetServersServerFiles(_serverData.ServerID, fileName);

            WebClient webClient = new WebClient();
            webClient.Headers.Add(HttpRequestHeader.UserAgent, userAgent);
            webClient.DownloadFileCompleted += WebClient_DownloadFileCompleted; //extract when completed

            try
            {
                webClient.DownloadFileAsync(new Uri(downloadLink), serverPath);
                Thread.Sleep(5000);
            }
            catch (WebException ex)
            {
                // Handle exceptions
                Error = $"Error: {ex.Message}";
            }
        }

        private async void WebClient_DownloadFileCompleted(object sender, AsyncCompletedEventArgs e)
        {
            Console.WriteLine("Download is done");
            string downloadLink = await GetDownloadLink();

            if (downloadLink == null) return;
          
            string fileName = Path.GetFileName(new Uri(downloadLink).LocalPath);
            string serverPath = ServerPath.GetServersServerFiles(_serverData.ServerID,"tmp2");
            string fullFilePath = Path.Combine(serverPath, fileName);

            if(!Directory.Exists(serverPath))
                Directory.CreateDirectory(serverPath);

            if (!File.Exists(fullFilePath))
            {
                Console.WriteLine($"File is missing {fullFilePath}");
                return;
            };

            // Extract
            if (!await FileManagement.ExtractZip(fullFilePath, serverPath))
            {
                Console.WriteLine($"Fail to extract {fullFilePath}");
            }
            else
            {
                // TODO Copy directory and replace
                Console.WriteLine("Extract is done");

                // Copy each file from the source to the destination
                if (await CopyFiles(serverPath, ServerPath.GetServersServerFiles(_serverData.ServerID)))
                {
                    Console.WriteLine($"Attempting to delete tmp2");
                    // Delete tmp2 (Config files)
                    Directory.Delete(serverPath, true);
                    Console.WriteLine($"Deleted");
                }

                // Delete zip
                await FileManagement.DeleteAsync(fullFilePath);
            }
        }

        private async Task<bool> CopyFiles(string sourceDirectory, string destinationDirectory)
        {
            Console.WriteLine("CopyFiles() CALLED");
            // Get all files in the source directory and its subdirectories
            string[] filesToCopy = Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories);

            return await Task.Run(() => {
                foreach (string sourceFilePath in filesToCopy)
                {
                    // Construct the destination path by replacing the source directory with the destination directory
                    string relativePath = sourceFilePath.Substring(sourceDirectory.Length + 1);
                    string destinationFilePath = Path.Combine(destinationDirectory, relativePath);

                    if (!File.Exists(destinationFilePath))
                    {
                        Console.WriteLine($"Not Exist: !!!!!!!!!!! {destinationFilePath}");
                        return false;
                    }

                    try
                    {
                        // Ensure the directory structure exists in the destination path
                        Directory.CreateDirectory(Path.GetDirectoryName(destinationFilePath));

                        // Copy the file to the destination path, overwriting if it already exists
                        File.Copy(sourceFilePath, destinationFilePath, overwrite: true);

                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"CopyFiles() Error : {ex.Message}");
                        return false;
                    }
                }
                Console.WriteLine($"Done CopyFiles() LOOP");
                return true;
            });
        }
    }
}
