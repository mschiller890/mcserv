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
// using Microsoft.VisualBasic; // not required when using a dedicated TextBox for token input

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

            // wire up UI events
            this.Load += Form1_Load;
            this.button1.Click += ButtonCreate_Click; // Create
            this.button2.Click += ButtonDelete_Click; // Delete
            this.button4.Click += ButtonStart_Click; // Start
            this.button3.Click += ButtonStop_Click; // Stop
            this.button5.Click += ButtonRestart_Click; // Restart
            this.button6.Click += ButtonStartCF_Click; // Start ngrok tunnel
            this.button7.Click += ButtonStopCF_Click; // Stop ngrok tunnel
            this.button8.Click += ButtonConsoleSend_Click; // Console send
            this.button9.Click += ButtonDownloadCF_Click; // Console send
            this.button10.Click += ButtonAddToken_Click; // Add Token

            SetCueBanner(this.textBox1, "Enter Server Name...");
            SetCueBanner(this.textBox3, "Enter server.jar URL...");
            SetCueBanner(this.textBox4, "Enter authtoken...");
            SetCueBanner(this.textBox2, "/help");

            this.listBox1.SelectedIndexChanged += ListBox1_SelectedIndexChanged;
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
                    richTextBox1.AppendText(si.ConsoleBuffer + Environment.NewLine);
            }
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
                    richTextBox2.AppendText(prefix + e.Line + Environment.NewLine);
                else
                    richTextBox1.AppendText(prefix + e.Line + Environment.NewLine);
            }
            else if (e.Line != null && e.Line.StartsWith("<ngrok", StringComparison.OrdinalIgnoreCase))
            {
                if (richTextBox2 != null)
                    richTextBox2.AppendText(prefix + e.Line + Environment.NewLine);
                else
                    richTextBox1.AppendText(prefix + e.Line + Environment.NewLine);
            }
            else
            {
                richTextBox1.AppendText(prefix + e.Line + Environment.NewLine);
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
                    richTextBox1.AppendText($"[{si.Name}] <starting ngrok...>" + Environment.NewLine);
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
                    richTextBox1.AppendText($"[{si.Name}] <stopping ngrok...>" + Environment.NewLine);
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
    }
}
