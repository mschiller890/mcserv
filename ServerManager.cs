using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace mcserv
{
    public class ServerOutputEventArgs : EventArgs
    {
        public Guid ServerId { get; set; }
        public string Line { get; set; }
    }

    public class ServerManager
    {
        private readonly string baseDir;
        private readonly string dataFile;
        private readonly HttpClient http = new HttpClient();

        public List<ServerInstance> Servers { get; private set; } = new List<ServerInstance>();

        public event EventHandler<ServerOutputEventArgs> ServerOutput;

        public ServerManager(string baseDirectory)
        {
            baseDir = baseDirectory ?? throw new ArgumentNullException(nameof(baseDirectory));
            dataFile = Path.Combine(baseDir, "servers.json");
        }

        public void Load()
        {
            if (!File.Exists(dataFile))
            {
                Servers = new List<ServerInstance>();
                return;
            }

            var json = File.ReadAllText(dataFile);
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var list = JsonSerializer.Deserialize<List<ServerInstance>>(json, opts);
            Servers = list ?? new List<ServerInstance>();
        }

        /// <summary>
        /// Discover existing server folders under the base directory and add any that are not present in the saved list.
        /// This allows users who manually dropped a server.jar into a folder to have it show up in the UI.
        /// </summary>
        public void DiscoverServers()
        {
            try
            {
                if (!Directory.Exists(baseDir)) return;
                var dirs = Directory.GetDirectories(baseDir);
                foreach (var dir in dirs)
                {
                    // skip the data file path if baseDir points to app folder
                    if (string.Equals(dir, baseDir, StringComparison.OrdinalIgnoreCase)) continue;
                    // If there is already an instance for this folder, skip
                    if (Servers.Any(s => string.Equals(Path.GetFullPath(s.FolderPath), Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase)))
                        continue;

                    // Create a default instance for the discovered folder
                    var name = Path.GetFileName(dir);
                    var instance = new ServerInstance
                    {
                        Id = Guid.NewGuid(),
                        Name = name,
                        FolderPath = dir,
                        JarUrl = null
                    };
                    Servers.Add(instance);
                    OnOutput(instance, $"<discovered server folder: {dir}>");
                }
            }
            catch (Exception ex)
            {
                // log discovery failure
                var dummy = new ServerInstance { Id = Guid.NewGuid(), Name = "(discovery)", FolderPath = baseDir };
                OnOutput(dummy, $"<discovery failed: {ex.Message}>");
            }
        }

        public void Save()
        {
            var opts = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(Servers, opts);
            File.WriteAllText(dataFile, json);
        }

        public async Task CreateServerAsync(string name, string jarUrl)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name required");
            var safe = MakeSafeName(name);
            var folder = Path.Combine(baseDir, safe);
            int suffix = 1;
            var original = folder;
            while (Directory.Exists(folder))
            {
                folder = original + "_" + suffix++;
            }
            Directory.CreateDirectory(folder);

            var instance = new ServerInstance
            {
                Id = Guid.NewGuid(),
                Name = name,
                FolderPath = folder,
                JarUrl = jarUrl
            };
            // log creation
            OnOutput(instance, $"<created server folder: {folder}>");

            // add to list early so UI can show download progress prefixed with name
            Servers.Add(instance);
            try
            {
                Save();
                OnOutput(instance, "<server metadata saved>");
            }
            catch (Exception ex)
            {
                OnOutput(instance, $"<failed to save metadata: {ex.Message}>");
                throw;
            }

            // optionally download jar (report progress)
            if (!string.IsNullOrWhiteSpace(jarUrl))
            {
                var dest = Path.Combine(folder, "server.jar");
                try
                {
                    OnOutput(instance, $"<downloading server.jar from {jarUrl}>");
                    await DownloadToFileAsync(jarUrl, dest, instance);
                    OnOutput(instance, "<download complete>");
                }
                catch (Exception ex)
                {
                    OnOutput(instance, $"<download failed: {ex.Message}>");
                    throw;
                }
            }
        }

        public void DeleteServer(string name)
        {
            var si = Servers.FirstOrDefault(s => s.Name == name);
            if (si == null) throw new InvalidOperationException("Server not found");

            // try stop
            if (si.IsRunning)
            {
                try { StopServerAsync(si.Name).Wait(5000); } catch { }
            }

            if (Directory.Exists(si.FolderPath))
                Directory.Delete(si.FolderPath, true);

            Servers.Remove(si);
            Save();
        }

        public async Task StartServerAsync(string name)
        {
            var si = Servers.FirstOrDefault(s => s.Name == name);
            if (si == null) throw new InvalidOperationException("Server not found");

            if (!IsJavaInstalled())
                throw new InvalidOperationException("Java not found in PATH. Please install Java and ensure 'java' is available.");

            if (si.IsRunning) return;

            var jar = Path.Combine(si.FolderPath, "server.jar");
            if (!File.Exists(jar))
            {
                // try to find any jar in the folder (support spigot/paper jar names)
                var jars = Directory.GetFiles(si.FolderPath, "*.jar");
                if (jars.Length > 0)
                {
                    jar = jars[0];
                    OnOutput(si, $"<using detected jar: {Path.GetFileName(jar)}>");
                }
                else
                {
                    throw new FileNotFoundException("server.jar not found in server folder. Provide a valid URL or place a jar in the folder.");
                }
            }

            var psi = new ProcessStartInfo
            {
                FileName = "java",
                Arguments = $"-Xms512M -Xmx1024M -jar \"{Path.GetFileName(jar)}\" nogui",
                WorkingDirectory = si.FolderPath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (s, e) => { if (e.Data != null) OnOutput(si, e.Data); };
            p.ErrorDataReceived += (s, e) => { if (e.Data != null) OnOutput(si, e.Data); };
            p.Exited += (s, e) => { si.IsRunning = false; OnOutput(si, "<process exited>"); };

            if (!p.Start()) throw new InvalidOperationException("Failed to start java process");

            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            si.Process = p;
            si.IsRunning = true;
            OnOutput(si, "<process started>");
        }

        public async Task StopServerAsync(string name)
        {
            var si = Servers.FirstOrDefault(s => s.Name == name);
            if (si == null) throw new InvalidOperationException("Server not found");
            if (!si.IsRunning || si.Process == null) return;

            try
            {
                // attempt graceful stop
                try
                {
                    if (!si.Process.HasExited)
                    {
                        si.Process.StandardInput.WriteLine("stop");
                        // wait a bit
                        if (!si.Process.WaitForExit(10000))
                        {
                            try { si.Process.Kill(); } catch { }
                        }
                    }
                }
                catch
                {
                    try { si.Process.Kill(); } catch { }
                }
            }
            finally
            {
                si.IsRunning = false;
                si.Process = null;
                OnOutput(si, "<process stopped>");
            }
        }

        public async Task RestartServerAsync(string name)
        {
            await StopServerAsync(name);
            await StartServerAsync(name);
        }

        public void SendCommand(string name, string command)
        {
            var si = Servers.FirstOrDefault(s => s.Name == name);
            if (si == null) throw new InvalidOperationException("Server not found");
            if (!si.IsRunning || si.Process == null)
            {
                MessageBox.Show("You need to run the server before sending a command.");
                return;
            }

            try
            {
                si.Process.StandardInput.WriteLine(command);
                OnOutput(si, "> " + command);
            }
            catch (Exception ex)
            {
                OnOutput(si, "<failed to send command: " + ex.Message + ">");
            }
        }

        private void OnOutput(ServerInstance si, string line)
        {
            // buffer
            si.ConsoleBuffer = (si.ConsoleBuffer ?? string.Empty) + line + Environment.NewLine;
            ServerOutput?.Invoke(this, new ServerOutputEventArgs { ServerId = si.Id, Line = line });
        }

        private async Task DownloadToFileAsync(string url, string destPath, ServerInstance si)
        {
            using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength;
                using (var src = await resp.Content.ReadAsStreamAsync())
                using (var dst = File.Create(destPath))
                {
                    var buffer = new byte[81920];
                    long totalRead = 0;
                    int read;
                    int lastPct = -1;
                    var lastReport = DateTime.MinValue;
                    while ((read = await src.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await dst.WriteAsync(buffer, 0, read);
                        totalRead += read;

                        if (total.HasValue)
                        {
                            var pct = (int)((totalRead * 100) / total.Value);
                            // throttle to 1% changes
                            if (pct != lastPct)
                            {
                                lastPct = pct;
                                OnOutput(si, $"<download {pct}% ({totalRead}/{total} bytes)>");
                            }
                        }
                        else
                        {
                            // unknown total length: report every ~250ms
                            if ((DateTime.Now - lastReport).TotalMilliseconds > 250)
                            {
                                lastReport = DateTime.Now;
                                OnOutput(si, $"<download {totalRead} bytes written>");
                            }
                        }
                    }
                }
            }
        }

        private static string MakeSafeName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }

        private bool IsJavaInstalled()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "java",
                    Arguments = "-version",
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return false;
                    if (!p.WaitForExit(2000))
                    {
                        try { p.Kill(); } catch { }
                        return false;
                    }
                    return p.ExitCode == 0 || p.ExitCode == 1 || p.ExitCode == 2; // java -version sometimes returns 0; tolerate others as "present"
                }
            }
            catch
            {
                return false;
            }
        }
    }

    public class ServerInstance
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string FolderPath { get; set; }
        public string JarUrl { get; set; }

        [JsonIgnore]
        public Process Process { get; set; }

        [JsonIgnore]
        public bool IsRunning { get; set; }

        [JsonIgnore]
        public string ConsoleBuffer { get; set; }
    }
}
