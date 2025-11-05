using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net;
using System.Net.Sockets;
using System.Xml;
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

        /// <summary>
        /// Start an ngrok tunnel for the named server using the server's TunnelCommand (or a sensible default).
        /// Captures stdout/stderr and emits ServerOutput events.
        /// </summary>
        public async Task StartNgrokTunnelAsync(string name)
        {
            var si = Servers.FirstOrDefault(s => s.Name == name);
            if (si == null) throw new InvalidOperationException("Server not found");

            if (si.IsTunnelRunning)
            {
                OnOutput(si, "<ngrok already running>");
                return;
            }

            // Default tunnel command: expose TCP port 25565 of the server folder
            var cmd = si.TunnelCommand;
            if (string.IsNullOrWhiteSpace(cmd))
            {
                // use tcp tunnel to localhost:25565 (common for Minecraft)
                // default to ngrok's tcp mode
                cmd = "ngrok tcp 25565";
            }

            // split executable and args simply
            var parts = SplitCommand(cmd);
            var exe = parts.Item1;
            var args = parts.Item2;

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                WorkingDirectory = si.FolderPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            try
            {
                var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                p.OutputDataReceived += (s, e) => { if (e.Data != null) OnOutput(si, "[ngrok] " + e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) OnOutput(si, "[ngrok] " + e.Data); };
                p.Exited += (s, e) => { si.IsTunnelRunning = false; OnOutput(si, "<ngrok exited>"); };

                if (!p.Start()) throw new InvalidOperationException("Failed to start ngrok process");
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                si.NgrokProcess = p;
                si.IsTunnelRunning = true;
                OnOutput(si, $"<ngrok started: {cmd} (pid {p.Id}) at {DateTime.Now:O}>");
                // Try to query ngrok's local web API (default: 127.0.0.1:4040) to obtain the public tunnel URL
                try
                {
                    var apiUrl = "http://127.0.0.1:4040/api/tunnels";
                    string publicUrl = null;
                    // poll a few times for ngrok to register the tunnel
                    for (int attempt = 0; attempt < 10 && string.IsNullOrEmpty(publicUrl); attempt++)
                    {
                        try
                        {
                            var resp = await http.GetAsync(apiUrl);
                            if (resp.IsSuccessStatusCode)
                            {
                                var body = await resp.Content.ReadAsStringAsync();
                                using (var doc = JsonDocument.Parse(body))
                                {
                                    if (doc.RootElement.TryGetProperty("tunnels", out var tunnels) && tunnels.ValueKind == JsonValueKind.Array)
                                    {
                                        foreach (var t in tunnels.EnumerateArray())
                                        {
                                            if (t.TryGetProperty("public_url", out var pu) && pu.ValueKind == JsonValueKind.String)
                                            {
                                                var url = pu.GetString();
                                                if (!string.IsNullOrWhiteSpace(url))
                                                {
                                                    publicUrl = url;
                                                    break;
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        catch { /* ignore transient errors while ngrok starts */ }

                        if (string.IsNullOrEmpty(publicUrl))
                            await Task.Delay(500);
                    }

                    if (!string.IsNullOrEmpty(publicUrl))
                    {
                        var changed = !string.Equals(si.LastNgrokPublicUrl, publicUrl, StringComparison.OrdinalIgnoreCase);
                        OnOutput(si, $"<ngrok tunnel: {publicUrl}>" + (changed ? " <changed>" : " <unchanged>"));
                        si.LastNgrokPublicUrl = publicUrl;
                    }
                    else
                    {
                        OnOutput(si, "<ngrok: public URL not available yet>");
                    }
                }
                catch { /* non-fatal */ }
            }
            catch (Exception ex)
            {
                OnOutput(si, $"<ngrok failed to start: {ex.Message}>");
                throw;
            }
        }

        public async Task StopNgrokTunnelAsync(string name)
        {
            var si = Servers.FirstOrDefault(s => s.Name == name);
            if (si == null) throw new InvalidOperationException("Server not found");
            if (!si.IsTunnelRunning || si.NgrokProcess == null) return;

            try
            {
                if (!si.NgrokProcess.HasExited)
                {
                    try { si.NgrokProcess.Kill(); } catch (Exception ex) { OnOutput(si, $"<failed to kill ngrok: {ex.Message}>"); }
                }
            }
            finally
            {
                si.IsTunnelRunning = false;
                si.NgrokProcess = null;
                OnOutput(si, "<ngrok stopped>");
            }
        }

        /// <summary>
        /// Start UPnP port forwarding (AddPortMapping) so the server becomes publicly reachable.
        /// </summary>
        //public async Task StartPortForwardingAsync(string name, int internalPort = 25565, int externalPort = 0)
        //{
        //    var si = Servers.FirstOrDefault(s => s.Name == name);
        //    if (si == null) throw new InvalidOperationException("Server not found");

        //    if (si.IsPortForwarded)
        //    {
        //        OnOutput(si, "<portforward already running>");
        //        return;
        //    }

        //    if (externalPort == 0) externalPort = internalPort;

        //    // SSDP discovery for WANIPConnection service
        //    string location = null;
        //    try
        //    {
        //        using (var udp = new UdpClient())
        //        {
        //            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        //            udp.Client.ReceiveTimeout = 2000;
        //            var msg =
        //                "M-SEARCH * HTTP/1.1\r\n" +
        //                "HOST:239.255.255.250:1900\r\n" +
        //                "MAN:\"ssdp:discover\"\r\n" +
        //                "MX:2\r\n" +
        //                "ST:urn:schemas-upnp-org:service:WANIPConnection:1\r\n" +
        //                "\r\n";
        //            var data = Encoding.UTF8.GetBytes(msg);
        //            var ep = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
        //            OnOutput(si, "<portforward: sending SSDP discovery to 239.255.255.250:1900>");
        //            await udp.SendAsync(data, data.Length, ep);

        //            var start = DateTime.UtcNow;
        //            while ((DateTime.UtcNow - start).TotalMilliseconds < 2000)
        //            {
        //                try
        //                {
        //                    var res = await udp.ReceiveAsync();
        //                    var resp = Encoding.UTF8.GetString(res.Buffer);
        //                    // log a short snippet of the response for debugging
        //                    var snippet = resp.Length > 512 ? resp.Substring(0, 512) + "..." : resp;
        //                    OnOutput(si, "<portforward: SSDP response> " + snippet);
        //                    foreach (var line in resp.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
        //                    {
        //                        if (line.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase))
        //                        {
        //                            location = line.Substring(9).Trim();
        //                            OnOutput(si, "<portforward: discovered LOCATION=" + location + ">");
        //                            break;
        //                        }
        //                    }
        //                    if (!string.IsNullOrEmpty(location)) break;
        //                }
        //                catch (SocketException se)
        //                {
        //                    OnOutput(si, "<portforward: SSDP receive error: " + se.Message + ">");
        //                    break;
        //                }
        //            }
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        OnOutput(si, $"<portforward discovery failed: {ex.Message}>");
        //    }

        //    if (string.IsNullOrEmpty(location))
        //    {
        //        OnOutput(si, "<portforward: no IGD found>");
        //        return;
        //    }

        //    try
        //    {
        //        var desc = await http.GetStringAsync(location);
        //        var shortDesc = desc.Length > 1024 ? desc.Substring(0, 1024) + "..." : desc;
        //        OnOutput(si, "<portforward: device description> " + shortDesc);
        //        var xd = new XmlDocument();
        //        xd.LoadXml(desc);

        //        XmlNode serviceNode = null;
        //        foreach (XmlNode sn in xd.GetElementsByTagName("service"))
        //        {
        //            var st = sn.SelectSingleNode("serviceType");
        //            if (st != null && st.InnerText.Contains("WANIPConnection"))
        //            {
        //                serviceNode = sn;
        //                break;
        //            }
        //        }

        //        if (serviceNode == null)
        //        {
        //            OnOutput(si, "<portforward: WANIPConnection service not found>");
        //            return;
        //        }

        //        var controlUrlNode = serviceNode.SelectSingleNode("controlURL");
        //        var serviceTypeNode = serviceNode.SelectSingleNode("serviceType");
        //        if (controlUrlNode == null || serviceTypeNode == null)
        //        {
        //            OnOutput(si, "<portforward: invalid service description>");
        //            return;
        //        }

        //        var controlUrl = controlUrlNode.InnerText.Trim();
        //        var serviceType = serviceTypeNode.InnerText.Trim();
        //        OnOutput(si, $"<portforward: serviceType={serviceType} controlURL={controlUrl}>");
        //        var baseUri = new Uri(location);
        //        var controlUri = controlUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? new Uri(controlUrl) : new Uri(baseUri, controlUrl);

        //        var localIp = GetLocalIPAddress();
        //        if (localIp == null)
        //        {
        //            OnOutput(si, "<portforward: could not determine local IP>");
        //            return;
        //        }

        //        var addSoap = "<?xml version=\"1.0\"?>\r\n" +
        //                      "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">\r\n" +
        //                      " <s:Body>\r\n" +
        //                      $"  <u:AddPortMapping xmlns:u=\"{serviceType}\">\r\n" +
        //                      "    <NewRemoteHost></NewRemoteHost>\r\n" +
        //                      $"    <NewExternalPort>{externalPort}</NewExternalPort>\r\n" +
        //                      "    <NewProtocol>TCP</NewProtocol>\r\n" +
        //                      $"    <NewInternalPort>{internalPort}</NewInternalPort>\r\n" +
        //                      $"    <NewInternalClient>{localIp}</NewInternalClient>\r\n" +
        //                      "    <NewEnabled>1</NewEnabled>\r\n" +
        //                      "    <NewPortMappingDescription>mcserv</NewPortMappingDescription>\r\n" +
        //                      "    <NewLeaseDuration>0</NewLeaseDuration>\r\n" +
        //                      "  </u:AddPortMapping>\r\n" +
        //                      " </s:Body>\r\n" +
        //                      "</s:Envelope>\r\n";

        //        var req = new HttpRequestMessage(HttpMethod.Post, controlUri);
        //        req.Content = new StringContent(addSoap, Encoding.UTF8, "text/xml");
        //        req.Headers.Add("SOAPACTION", "\"" + serviceType + "#AddPortMapping\"");

        //        OnOutput(si, "<portforward: sending AddPortMapping SOAP request>");
        //        var resp = await http.SendAsync(req);
        //        var respBody = await resp.Content.ReadAsStringAsync();
        //        OnOutput(si, "<portforward: AddPortMapping response status=" + resp.StatusCode + ">" + (respBody.Length > 1024 ? respBody.Substring(0, 1024) + "..." : respBody));
        //        if (!resp.IsSuccessStatusCode)
        //        {
        //            OnOutput(si, $"<portforward add failed: {resp.StatusCode}>");
        //            return;
        //        }

        //        // Query external IP
        //        var getSoap = "<?xml version=\"1.0\"?>\r\n" +
        //                      "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">\r\n" +
        //                      " <s:Body>\r\n" +
        //                      $"  <u:GetExternalIPAddress xmlns:u=\"{serviceType}\"/>\r\n" +
        //                      " </s:Body>\r\n" +
        //                      "</s:Envelope>\r\n";

        //        var getReq = new HttpRequestMessage(HttpMethod.Post, controlUri);
        //        getReq.Content = new StringContent(getSoap, Encoding.UTF8, "text/xml");
        //        getReq.Headers.Add("SOAPACTION", "\"" + serviceType + "#GetExternalIPAddress\"");

        //        string externalIp = null;
        //        try
        //        {
        //            OnOutput(si, "<portforward: sending GetExternalIPAddress SOAP request>");
        //            var getResp = await http.SendAsync(getReq);
        //            var getBody = await getResp.Content.ReadAsStringAsync();
        //            OnOutput(si, "<portforward: GetExternalIPAddress response status=" + getResp.StatusCode + ">" + (getBody.Length > 1024 ? getBody.Substring(0, 1024) + "..." : getBody));
        //            if (getResp.IsSuccessStatusCode)
        //            {
        //                var xd2 = new XmlDocument();
        //                xd2.LoadXml(getBody);
        //                var elems = xd2.GetElementsByTagName("NewExternalIPAddress");
        //                if (elems.Count > 0) externalIp = elems[0].InnerText.Trim();
        //            }
        //        }
        //        catch (Exception ex)
        //        {
        //            OnOutput(si, "<portforward: GetExternalIPAddress failed: " + ex.Message + ">");
        //        }

        //        si.IsPortForwarded = true;
        //        si.PortForwardExternalPort = externalPort;
        //        si.PortForwardExternalIp = externalIp;
        //        si.PortMappingControlUrl = controlUri.ToString();
        //        si.PortMappingServiceType = serviceType;

        //        OnOutput(si, $"<portforward created: {externalIp ?? "?"}:{externalPort}>");
        //    }
        //    catch (Exception ex)
        //    {
        //        OnOutput(si, $"<portforward failed: {ex.Message}>");
        //    }
        //}

        //public async Task StopPortForwardingAsync(string name)
        //{
        //    var si = Servers.FirstOrDefault(s => s.Name == name);
        //    if (si == null) throw new InvalidOperationException("Server not found");
        //    if (!si.IsPortForwarded) return;

        //    try
        //    {
        //        if (string.IsNullOrEmpty(si.PortMappingControlUrl) || string.IsNullOrEmpty(si.PortMappingServiceType))
        //        {
        //            OnOutput(si, "<portforward: no mapping info to remove>");
        //            si.IsPortForwarded = false;
        //            return;
        //        }

        //        var controlUri = new Uri(si.PortMappingControlUrl);
        //        var serviceType = si.PortMappingServiceType;
        //        var externalPort = si.PortForwardExternalPort;

        //        var delSoap = "<?xml version=\"1.0\"?>\r\n" +
        //                      "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">\r\n" +
        //                      " <s:Body>\r\n" +
        //                      $"  <u:DeletePortMapping xmlns:u=\"{serviceType}\">\r\n" +
        //                      "    <NewRemoteHost></NewRemoteHost>\r\n" +
        //                      $"    <NewExternalPort>{externalPort}</NewExternalPort>\r\n" +
        //                      "    <NewProtocol>TCP</NewProtocol>\r\n" +
        //                      "  </u:DeletePortMapping>\r\n" +
        //                      " </s:Body>\r\n" +
        //                      "</s:Envelope>\r\n";

        //        var req = new HttpRequestMessage(HttpMethod.Post, controlUri);
        //        req.Content = new StringContent(delSoap, Encoding.UTF8, "text/xml");
        //        req.Headers.Add("SOAPACTION", "\"" + serviceType + "#DeletePortMapping\"");

        //        var resp = await http.SendAsync(req);
        //        if (!resp.IsSuccessStatusCode)
        //        {
        //            var body = await resp.Content.ReadAsStringAsync();
        //            OnOutput(si, $"<portforward delete failed: {resp.StatusCode} {body}>");
        //        }
        //        else
        //        {
        //            OnOutput(si, "<portforward stopped>");
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        OnOutput(si, $"<portforward stop failed: {ex.Message}>");
        //    }
        //    finally
        //    {
        //        si.IsPortForwarded = false;
        //        si.PortForwardExternalIp = null;
        //        si.PortForwardExternalPort = 0;
        //        si.PortMappingControlUrl = null;
        //        si.PortMappingServiceType = null;
        //    }
        //}

        private static Tuple<string, string> SplitCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return Tuple.Create(string.Empty, string.Empty);
            // naive split: first token is exe (handles quoted paths)
            command = command.Trim();
            if (command.StartsWith("\"") || command.StartsWith("\'"))
            {
                var quote = command[0];
                var end = command.IndexOf(quote, 1);
                if (end > 0)
                {
                    var exe = command.Substring(1, end - 1);
                    var args = command.Substring(end + 1).Trim();
                    return Tuple.Create(exe, args);
                }
            }
            var idx = command.IndexOf(' ');
            if (idx < 0) return Tuple.Create(command, string.Empty);
            var first = command.Substring(0, idx);
            var rest = command.Substring(idx + 1).Trim();
            return Tuple.Create(first, rest);
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

            if (Application.OpenForms.Count > 0 && Application.OpenForms[0] is Form1 mainForm1)
            {
                mainForm1.SetStatus();
            }

            if (line.Contains("[ServerMain/INFO]: You need to agree to the EULA in order to run the server."))
            {
                // Find the main UI thread (Form) and invoke on it if needed
                if (Application.OpenForms.Count > 0)
                {
                    var mainForm = Application.OpenForms[0];
                    if (mainForm.InvokeRequired)
                    {
                        mainForm.Invoke((Action)(() =>
                        {
                            MessageBox.Show(
                                mainForm,
                                "You need to agree to the EULA before starting your server.",
                                "EULA Required",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Warning
                            );
                        }));
                    }
                    else
                    {
                        MessageBox.Show(
                            mainForm,
                            "You need to agree to the EULA before starting your server.",
                            "EULA Required",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning
                        );
                    }
                }
                else
                {
                    // Fallback if no forms are open
                    MessageBox.Show(
                        "You need to agree to the EULA before starting your server.",
                        "EULA Required",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning
                    );
                }
            }
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

                        double totalReadMB = totalRead / (1024.0 * 1024.0);
                        if (total.HasValue)
                        {
                            double totalMB = total.Value / (1024.0 * 1024.0);
                            var pct = (int)((totalRead * 100) / total.Value);
                            // throttle to 1% changes
                            if (pct != lastPct)
                            {
                                lastPct = pct;
                                OnOutput(si, $"<download {pct}% ({totalReadMB:F2}/{totalMB:F2} MB)>");
                            }
                        }
                        else
                        {
                            // unknown total length: report every ~250ms
                            if ((DateTime.Now - lastReport).TotalMilliseconds > 250)
                            {
                                lastReport = DateTime.Now;
                                OnOutput(si, $"<download {totalReadMB:F2} MB written>");
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

        //private string GetLocalIPAddress()
        //{
        //    try
        //    {
        //        // prefer non-loopback IPv4 from DNS
        //        var host = Dns.GetHostEntry(Dns.GetHostName());
        //        foreach (var ip in host.AddressList)
        //        {
        //            if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
        //                return ip.ToString();
        //        }

        //        // fallback: open UDP socket to public IP and read local endpoint
        //        using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        //        {
        //            s.Connect("8.8.8.8", 53);
        //            var ep = s.LocalEndPoint as IPEndPoint;
        //            return ep?.Address.ToString();
        //        }
        //    }
        //    catch
        //    {
        //        return null;
        //    }
        //}

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
        public string TunnelCommand { get; set; }

        [JsonIgnore]
        public Process Process { get; set; }

        [JsonIgnore]
        public bool IsRunning { get; set; }

        [JsonIgnore]
        public Process NgrokProcess { get; set; }

        //[JsonIgnore]
        //public bool IsPortForwarded { get; set; }

        //[JsonIgnore]
        //public int PortForwardExternalPort { get; set; }

        //[JsonIgnore]
        //public string PortForwardExternalIp { get; set; }

        //[JsonIgnore]
        //public string PortMappingControlUrl { get; set; }

        //[JsonIgnore]
        //public string PortMappingServiceType { get; set; }

        [JsonIgnore]
        public string LastNgrokPublicUrl { get; set; }

        [JsonIgnore]
        public bool IsTunnelRunning { get; set; }

        [JsonIgnore]
        public string ConsoleBuffer { get; set; }
    }
}
