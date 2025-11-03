using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace mcserv
{
    public partial class Form1 : Form
    {
        private ServerManager serverManager;
        private string serversDir;
        
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern Int32 SendMessage(IntPtr hWnd, int msg, int wParam, string lParam);
        private const int EM_SETCUEBANNER = 0x1501;

        public Form1()
        {
            InitializeComponent();
            // initialize server manager
            serversDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "servers");
            serverManager = new ServerManager(serversDir);

            this.FormClosing += MainForm_FormClosing;

            // wire up UI events
            this.Load += Form1_Load;
            this.button1.Click += ButtonCreate_Click; // Create
            this.button2.Click += ButtonDelete_Click; // Delete
            this.button3.Click += ButtonStop_Click; // Stop
            this.button4.Click += ButtonStart_Click; // Start
            this.button5.Click += ButtonRestart_Click; // Restart
            this.button6.Click += ButtonStartCF_Click; // Start ngrok tunnel
            this.button7.Click += ButtonStopCF_Click; // Stop ngrok tunnel
            this.button8.Click += ButtonConsoleSend_Click; // Console send
            this.button9.Click += ButtonDownloadCF_Click; // Console send
            this.button10.Click += ButtonAddToken_Click; // Add Token
            //this.button11.Click += ButtonKillServerAndNgrok_Click;

            SetCueBanner(this.textBox1, "Enter Server Name...");
            SetCueBanner(this.textBox3, "Enter server.jar URL...");
            SetCueBanner(this.textBox4, "Enter authtoken...");
            SetCueBanner(this.textBox2, "/help");
            SetCueBanner(this.textBox5, "Console output...");

            this.listBox1.SelectedIndexChanged += ListBox1_SelectedIndexChanged;
            // allow pressing Enter in the console input to send the command
            this.textBox2.KeyDown += TextBox2_KeyDown;
        }

        private void TextBox2_KeyDown(object sender, KeyEventArgs e)
        {
            // If Enter is pressed (without Shift), send the command and prevent the ding/newline
            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                e.SuppressKeyPress = true;
                // call the same logic as the Send button
                ButtonConsoleSend_Click(sender, EventArgs.Empty);
            }
        }

        private void SetCueBanner(TextBox box, string text)
        {
            SendMessage(box.Handle, EM_SETCUEBANNER, 0, text);
        }

        private void ButtonAddToken_Click(object sender, EventArgs e)
        {
            try
            {
                // Read the ngrok authtoken from the dedicated TextBox (textBox4)
                var token = textBox4.Text?.Trim();
                if (string.IsNullOrWhiteSpace(token))
                {
                    // user cancelled or didn't enter anything
                    return;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = "ngrok",
                    Arguments = $"authtoken {token}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var p = Process.Start(psi))
                {
                    if (p == null)
                    {
                        MessageBox.Show("Failed to start ngrok. Make sure ngrok is installed and on PATH.");
                        return;
                    }

                    // Read both streams
                    var stdout = p.StandardOutput.ReadToEnd();
                    var stderr = p.StandardError.ReadToEnd();
                    p.WaitForExit(5000);

                    if (!string.IsNullOrWhiteSpace(stderr))
                    {
                        MessageBox.Show("ngrok reported an error:\n" + stderr);
                    }
                    else
                    {
                        MessageBox.Show("ngrok authtoken set successfully." + (string.IsNullOrWhiteSpace(stdout) ? string.Empty : "\n\n" + stdout));
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to add ngrok authtoken: " + ex.Message);
            }
        }

        private void ButtonDownloadCF_Click(object sender, EventArgs e)
        {
            System.Diagnostics.Process.Start("https://ngrok.com/download");
        }

        private async void Form1_Load(object sender, EventArgs e)
        {
            try
            {
                Directory.CreateDirectory(serversDir);
                serverManager.Load();
                serverManager.DiscoverServers();
                serverManager.Save();
                serverManager.ServerOutput += ServerManager_ServerOutput;
                RefreshServerList();
                // initialize WebView2 help tab if available
                try
                {
                    if (this.webView21 != null)
                    {
                        await this.webView21.EnsureCoreWebView2Async(null);
                        try
                        {
                            this.webView21.CoreWebView2.Navigate("https://github.com/mschiller890/mcserv/wiki");
                        }
                        catch
                        {
                            // fallback: set Source if CoreWebView2 navigation fails
                            try { this.webView21.Source = new Uri("https://github.com/mschiller890/mcserv/wiki"); } catch { }
                        }
                    }
                }
                catch { /* non-fatal if WebView2 not available */ }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to initialize server manager: " + ex.Message);
            }
        }

        private void RefreshServerList(string selectName = null)
        {
            var names = serverManager.Servers.Select(s => s.Name).ToList();
            var previous = listBox1.SelectedItem as string;
            listBox1.DataSource = null;
            listBox1.DataSource = names;
            // try to restore previous selection or select provided name
            if (!string.IsNullOrEmpty(selectName))
            {
                var idx = names.IndexOf(selectName);
                if (idx >= 0) listBox1.SelectedIndex = idx;
            }
            else if (previous != null)
            {
                var idx = names.IndexOf(previous);
                if (idx >= 0) listBox1.SelectedIndex = idx;
            }
        }

        private void ListBox1_SelectedIndexChanged(object sender, EventArgs e)
        {
            // when selection changes, clear console view and optionally show buffered output
            int idx = listBox1.SelectedIndex;
            if (idx >= 0 && idx < serverManager.Servers.Count)
            {
                var si = serverManager.Servers[idx];
                richTextBox1.Clear();
                if (!string.IsNullOrEmpty(si.ConsoleBuffer))
                    AppendTextAndScroll(richTextBox1, si.ConsoleBuffer + Environment.NewLine);
                // load server.properties into DataGridView for editing
                LoadServerPropertiesIntoGrid(si);
            }
            else
            {
                // no selection: clear properties grid
                ClearPropertiesGrid();
            }
        }

        // internal representation of a properties file line
        private class PropertyLine
        {
            public enum LineType { Comment, Blank, Setting }
            public LineType Type { get; set; }
            public string RawText { get; set; }
            public string Key { get; set; }
            public string Value { get; set; }
        }

        private Guid currentPropsServerId = Guid.Empty;
        private List<PropertyLine> currentProps = null;
        private bool suppressGridEvents = false;

        private void ClearPropertiesGrid()
        {
            suppressGridEvents = true;
            try
            {
                dataGridView1.Columns.Clear();
                dataGridView1.Rows.Clear();
            }
            finally { suppressGridEvents = false; }
        }

        private void EnsureGridColumns()
        {
            if (dataGridView1.Columns.Count == 0)
            {
                var colKey = new DataGridViewTextBoxColumn { Name = "Key", HeaderText = "Key", ReadOnly = true, Width = 300 };
                var colValue = new DataGridViewTextBoxColumn { Name = "Value", HeaderText = "Value", ReadOnly = false, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill };
                dataGridView1.Columns.Add(colKey);
                dataGridView1.Columns.Add(colValue);
            }
        }

        private void LoadServerPropertiesIntoGrid(ServerInstance si)
        {
            try
            {
                var path = Path.Combine(si.FolderPath ?? string.Empty, "server.properties");
                List<PropertyLine> lines = new List<PropertyLine>();
                if (File.Exists(path))
                {
                    var raw = File.ReadAllLines(path);
                    foreach (var r in raw)
                    {
                        if (string.IsNullOrWhiteSpace(r))
                        {
                            lines.Add(new PropertyLine { Type = PropertyLine.LineType.Blank, RawText = r });
                        }
                        else if (r.TrimStart().StartsWith("#"))
                        {
                            lines.Add(new PropertyLine { Type = PropertyLine.LineType.Comment, RawText = r });
                        }
                        else
                        {
                            var idx = r.IndexOf('=');
                            if (idx >= 0)
                            {
                                var k = r.Substring(0, idx).Trim();
                                var v = r.Substring(idx + 1);
                                lines.Add(new PropertyLine { Type = PropertyLine.LineType.Setting, RawText = r, Key = k, Value = v });
                            }
                            else
                            {
                                // treat as raw/comment if it doesn't contain '='
                                lines.Add(new PropertyLine { Type = PropertyLine.LineType.Comment, RawText = r });
                            }
                        }
                    }
                }
                else
                {
                    // file doesn't exist: start with empty list
                    lines = new List<PropertyLine>();
                }

                // populate grid with setting lines
                suppressGridEvents = true;
                try
                {
                    EnsureGridColumns();
                    dataGridView1.Rows.Clear();
                    foreach (var pl in lines)
                    {
                        if (pl.Type == PropertyLine.LineType.Setting)
                        {
                            dataGridView1.Rows.Add(pl.Key, pl.Value);
                        }
                    }
                }
                finally { suppressGridEvents = false; }

                currentProps = lines;
                currentPropsServerId = si.Id;

                // wire events (ensure only once)
                dataGridView1.CellValueChanged -= DataGridView1_CellValueChanged;
                dataGridView1.CellValueChanged += DataGridView1_CellValueChanged;
                dataGridView1.CurrentCellDirtyStateChanged -= DataGridView1_CurrentCellDirtyStateChanged;
                dataGridView1.CurrentCellDirtyStateChanged += DataGridView1_CurrentCellDirtyStateChanged;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to load server.properties: " + ex.Message);
            }
        }

        private void DataGridView1_CurrentCellDirtyStateChanged(object sender, EventArgs e)
        {
            if (dataGridView1.IsCurrentCellDirty)
            {
                // commit immediately so CellValueChanged fires
                dataGridView1.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        }

        private void DataGridView1_CellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (suppressGridEvents) return;
            try
            {
                if (e.RowIndex < 0 || e.ColumnIndex != 1) return; // only handle Value column
                if (currentProps == null) return;

                var key = dataGridView1.Rows[e.RowIndex].Cells[0].Value?.ToString();
                var newVal = dataGridView1.Rows[e.RowIndex].Cells[1].Value?.ToString() ?? string.Empty;
                if (string.IsNullOrEmpty(key)) return;

                // update first matching setting in currentProps
                var found = false;
                for (int i = 0; i < currentProps.Count; i++)
                {
                    var pl = currentProps[i];
                    if (pl.Type == PropertyLine.LineType.Setting && string.Equals(pl.Key, key, StringComparison.Ordinal))
                    {
                        pl.Value = newVal;
                        pl.RawText = pl.Key + "=" + pl.Value;
                        currentProps[i] = pl;
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    // not found in original file: append at end
                    var pl = new PropertyLine { Type = PropertyLine.LineType.Setting, Key = key, Value = newVal, RawText = key + "=" + newVal };
                    currentProps.Add(pl);
                }

                // save back to file for current server
                SaveCurrentPropertiesToFile();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to save property change: " + ex.Message);
            }
        }

        private void SaveCurrentPropertiesToFile()
        {
            try
            {
                if (currentProps == null || currentPropsServerId == Guid.Empty) return;
                var si = serverManager.Servers.FirstOrDefault(s => s.Id == currentPropsServerId);
                if (si == null) return;
                var path = Path.Combine(si.FolderPath ?? string.Empty, "server.properties");

                var outLines = new List<string>();
                foreach (var pl in currentProps)
                {
                    if (pl.Type == PropertyLine.LineType.Setting)
                    {
                        outLines.Add(pl.Key + "=" + (pl.Value ?? string.Empty));
                    }
                    else
                    {
                        outLines.Add(pl.RawText ?? string.Empty);
                    }
                }

                File.WriteAllLines(path, outLines);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to save server.properties: " + ex.Message);
            }
        }

        private void AppendTextAndScroll(RichTextBox box, string text)
        {
            if (box == null) return;
            try
            {
                box.AppendText(text ?? string.Empty);
                // move caret to end and scroll
                box.SelectionStart = box.TextLength;
                box.ScrollToCaret();
            }
            catch { /* ignore UI race errors */ }
        }

        private void ServerManager_ServerOutput(object sender, ServerOutputEventArgs e)
        {
            // Ensure UI thread
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => ServerManager_ServerOutput(sender, e)));
                return;
            }

            // Find server name for this output (if known)
            var si = serverManager.Servers.FirstOrDefault(s => s.Id == e.ServerId);
            var prefix = si != null ? $"[{si.Name}] " : string.Empty;
            // Route ngrok output to richTextBox2 (separate console)
            if (e.Line != null && e.Line.StartsWith("[ngrok]", StringComparison.OrdinalIgnoreCase))
            {
                if (richTextBox2 != null)
                    AppendTextAndScroll(richTextBox2, prefix + e.Line + Environment.NewLine);
                else
                    AppendTextAndScroll(richTextBox1, prefix + e.Line + Environment.NewLine);
            }
            else if (e.Line != null && e.Line.StartsWith("<ngrok", StringComparison.OrdinalIgnoreCase))
            {
                if (richTextBox2 != null)
                    AppendTextAndScroll(richTextBox2, prefix + e.Line + Environment.NewLine);
                else
                    AppendTextAndScroll(richTextBox1, prefix + e.Line + Environment.NewLine);
            }
            else
            {
                AppendTextAndScroll(richTextBox1, prefix + e.Line + Environment.NewLine);
            }
        }

        private async void ButtonCreate_Click(object sender, EventArgs e)
        {
            var name = textBox1.Text?.Trim();
            var url = textBox3.Text?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                MessageBox.Show("Please enter a server name in the Management box.");
                return;
            }

            try
            {
                await serverManager.CreateServerAsync(name, url);
                RefreshServerList(name);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to create server: " + ex.Message);
            }
        }

        private void ButtonDelete_Click(object sender, EventArgs e)
        {
            int idx = listBox1.SelectedIndex;
            if (idx < 0) return;
            var si = serverManager.Servers[idx];
            var ok = MessageBox.Show($"Delete server '{si.Name}' and its files?","Confirm",MessageBoxButtons.YesNo) == DialogResult.Yes;
            if (!ok) return;
            try
            {
                serverManager.DeleteServer(si.Name);
                RefreshServerList();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to delete server: " + ex.Message);
            }
        }

        private async void ButtonStart_Click(object sender, EventArgs e)
        {
            int idx = listBox1.SelectedIndex;
            if (idx < 0) return;
            var si = serverManager.Servers[idx];
            try
            {
                await serverManager.StartServerAsync(si.Name);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to start server: " + ex.Message);
            }
        }

        private async void ButtonStop_Click(object sender, EventArgs e)
        {
            int idx = listBox1.SelectedIndex;
            if (idx < 0) return;
            var si = serverManager.Servers[idx];
            try
            {
                await serverManager.StopServerAsync(si.Name);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to stop server: " + ex.Message);
            }
        }

        private async void ButtonRestart_Click(object sender, EventArgs e)
        {
            int idx = listBox1.SelectedIndex;
            if (idx < 0) return;
            var si = serverManager.Servers[idx];
            try
            {
                await serverManager.RestartServerAsync(si.Name);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to restart server: " + ex.Message);
            }
        }

    private async void ButtonStartCF_Click(object sender, EventArgs e)
        {
            int idx = listBox1.SelectedIndex;
            if (idx < 0) return;
            var si = serverManager.Servers[idx];
            try
            {
                // start ngrok tunnel to make server public
                if (richTextBox1 != null)
                    AppendTextAndScroll(richTextBox1, $"[{si.Name}] <starting ngrok...>" + Environment.NewLine);
                await serverManager.StartNgrokTunnelAsync(si.Name);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to start ngrok tunnel: " + ex.Message);
            }
        }

        private async void ButtonStopCF_Click(object sender, EventArgs e)
        {
            int idx = listBox1.SelectedIndex;
            if (idx < 0) return;
            var si = serverManager.Servers[idx];
            try
            {
                if (richTextBox1 != null)
                    AppendTextAndScroll(richTextBox1, $"[{si.Name}] <stopping ngrok...>" + Environment.NewLine);
                await serverManager.StopNgrokTunnelAsync(si.Name);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to stop ngrok tunnel: " + ex.Message);
            }
        }

        private void ButtonConsoleSend_Click(object sender, EventArgs e)
        {
            int idx = listBox1.SelectedIndex;
            if (idx < 0) return;
            var si = serverManager.Servers[idx];
            var cmd = textBox2.Text?.Trim();
            if (string.IsNullOrEmpty(cmd)) return;
            serverManager.SendCommand(si.Name, cmd);
            textBox2.Clear();
        }

        private static bool IsInSubnet(IPAddress address, IPAddress network, int prefixLength)
        {
            var addrBytes = address.GetAddressBytes();
            var netBytes = network.GetAddressBytes();
            if (addrBytes.Length != netBytes.Length) return false;

            int byteCount = prefixLength / 8;
            int bitCount = prefixLength % 8;

            for (int i = 0; i < byteCount; i++)
            {
                if (addrBytes[i] != netBytes[i]) return false;
            }
            if (bitCount > 0)
            {
                int mask = (byte)~(0xFF >> bitCount);
                if ((addrBytes[byteCount] & mask) != (netBytes[byteCount] & mask)) return false;
            }
            return true;
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Environment.Exit(0);
        }

        private void button11_Click(object sender, EventArgs e)
        {
            ButtonStop_Click(sender, e);
            ButtonStopCF_Click(sender, e);
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            ButtonStopCF_Click(sender, e);
            ButtonStop_Click(sender, e);
        }

        public void SetStatus()
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => SetStatus()));
                return;
            }

            if (richTextBox1.Lines.Length >= 2)
                textBox5.Text = richTextBox1.Lines[richTextBox1.Lines.Length - 2];
            else
                textBox5.Text = "";

            if (richTextBox2.Lines.Length >= 2)
                textBox6.Text = richTextBox2.Lines[richTextBox2.Lines.Length - 2];
            else
                textBox6.Text = "";
        }
    }
}
