using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Diagnostics;
using System.IO;

namespace mcserv
{
    public partial class Form1 : Form
    {
        private ServerManager serverManager;
        private string serversDir;
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
            this.button6.Click += ButtonStartCF_Click; // Start CF tunnel
            this.button7.Click += ButtonStopCF_Click; // Stop CF tunnel
            this.button8.Click += ButtonConsoleSend_Click; // Console send
            this.listBox1.SelectedIndexChanged += ListBox1_SelectedIndexChanged;
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
            richTextBox1.AppendText(prefix + e.Line + Environment.NewLine);
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

        private void ButtonStartCF_Click(object sender, EventArgs e)
        {
            // Placeholder: start Cloudflare tunnel for selected server (requires external cloudflared)
            int idx = listBox1.SelectedIndex;
            if (idx < 0) return;
            var si = serverManager.Servers[idx];
            MessageBox.Show($"Start CF tunnel stub for '{si.Name}'. Configure 'cloudflared' separately.");
        }

        private void ButtonStopCF_Click(object sender, EventArgs e)
        {
            int idx = listBox1.SelectedIndex;
            if (idx < 0) return;
            var si = serverManager.Servers[idx];
            MessageBox.Show($"Stop CF tunnel stub for '{si.Name}'.");
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
    }
}
