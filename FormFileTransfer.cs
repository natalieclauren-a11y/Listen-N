using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Vf61Gui;

namespace Listen_N
{

    public partial class FormFileTransfer : Form
    {
        private List<Detector> detectors;


        public FormFileTransfer(List<Detector> detectors)
        {
            InitializeComponent();

            this.detectors = detectors;

            InitializeHandlers();

            PopulateIpList();

            buttonToggleChecks.Text = "Check All";
            buttonBrowseData.Click += buttonBrowseData_Click;
            buttonRefreshFiles.Click += buttonRefreshFiles_Click;

            radioButtonPC.Checked = true;

            buttonTransferSelected.Click += buttonTransferSelected_Click;
            buttonTransferAll.Click += buttonTransferAll_Click;

            listViewFileList.ItemChecked += (s, e) => UpdateTransferButtonState();

            string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            textBoxSavePathway.Text = desktopPath;

            progressBarSave.Minimum = 0;
            progressBarSave.Maximum = 100;
            progressBarSave.Value = 0;

            buttonEraseSelected.Click += buttonEraseSelected_Click;
            buttonEraseAll.Click += buttonEraseAll_Click;


        }

        private void InitializeHandlers()
        {
            listViewDetectorAddresses.SelectedIndexChanged += ListViewDetectorAddresses_SelectedIndexChanged;
            buttonToggleChecks.Click += buttonToggleChecks_Click;


        }
        private Detector? GetDetectorByIp(string ip)
        {
            return detectors.FirstOrDefault(d => d.IpAddress == ip);
        }
        private void TransferFile(string detectorIp, string filename)
        {
            var detector = GetDetectorByIp(detectorIp);
            if (detector == null || !detector.CheckState(InstrumentState.ONLINE))
            {
                MessageBox.Show($"Detector {detectorIp} is not online.");
                return;
            }

            if (radioButtonPC.Checked)
            {
                string saveDir = textBoxSavePathway.Text.Trim();
                if (!Directory.Exists(saveDir))
                {
                    MessageBox.Show("Selected save path does not exist.");
                    return;
                }

                detector.netSaveFilepath = saveDir;

                // Reset state
                detector.resetEventNetSaveFile.Reset();
                progressBarSave.Value = 0;

                // Hook up progress
                detector.EventUSBsaveFileProgress += (s, e) =>
                {
                    Invoke(() =>
                    {
                        progressBarSave.Value = Math.Clamp(detector.percentComplete, 0, 100);
                    });
                };

                detector.WriteAndWait($"NetSaveFile = {filename}");

                bool completed = detector.resetEventNetSaveFile.WaitOne(60000);
                if (!completed)
                    MessageBox.Show($"Transfer of {filename} timed out.");

                Invoke(() => progressBarSave.Value = 100); // Complete
                Task.Run(async () =>
                {
                    await Task.Delay(500);  // wait 0.5s
                    Invoke(() => progressBarSave.Value = 0);
                });

            }
            else if (radioButtonUSB.Checked)
            {
                detector.WriteAndWait($"USBsaveFile = {filename}");
                // No feedback for USB progress — you could add later via event hooks
            }
        }

        private void buttonTransferSelected_Click(object sender, EventArgs e)
        {
            bool anyChecked = false;

            foreach (ListViewItem item in listViewFileList.Items)
            {
                if (item.Checked)
                {
                    anyChecked = true;
                    string detectorIp = item.SubItems[0].Text;
                    string filename = item.SubItems[1].Text;
                    TransferFile(detectorIp, filename);
                }
            }

            if (!anyChecked)
            {
                MessageBox.Show("No files checked.");
            }
            else
            {
                MessageBox.Show("Checked file(s) transfer initiated.");
            }
        }

        private void buttonTransferAll_Click(object sender, EventArgs e)
        {
            if (listViewFileList.Items.Count == 0)
            {
                MessageBox.Show("No files to transfer.");
                return;
            }

            foreach (ListViewItem item in listViewFileList.Items)
            {
                string detectorIp = item.SubItems[0].Text;
                string filename = item.SubItems[1].Text;
                TransferFile(detectorIp, filename);
            }

            MessageBox.Show("All file transfers initiated.");
        }


        private void PopulateIpList()
        {
            listViewDetectorAddresses.Items.Clear();

            foreach (var detector in detectors)
            {
                var item = new ListViewItem(detector.IpAddress);
                listViewDetectorAddresses.Items.Add(item);
            }
        }

        private void RefreshFileListForSelectedDetector()
        {
            if (listViewDetectorAddresses.SelectedItems.Count == 0)
                return;

            string selectedIp = listViewDetectorAddresses.SelectedItems[0].Text;

            var selectedDetector = detectors.FirstOrDefault(d => d.IpAddress == selectedIp);
            if (selectedDetector == null)
            {
                MessageBox.Show("Detector not found.");
                return;
            }

            if (!selectedDetector.CheckState(InstrumentState.ONLINE))
            {
                MessageBox.Show("Detector is not online.");
                return;
            }

            selectedDetector.WriteAndWait("LFSlist");

            listViewFileList.Items.Clear();

            foreach (var file in selectedDetector.fileList)
            {
                var item = new ListViewItem(selectedDetector.IpAddress);
                item.SubItems.Add(file.name);
                item.SubItems.Add(file.size.ToString());
                item.Checked = false;
                listViewFileList.Items.Add(item);
            }
            buttonToggleChecks.Text = "Check All";   // reset button label
            UpdateTransferButtonState();             // disable transfer buttons

        }

        private void ListViewDetectorAddresses_SelectedIndexChanged(object sender, EventArgs e)
        {
            RefreshFileListForSelectedDetector();

        }

        private void FormFileTransfer_Load(object sender, EventArgs e)
        {
            buttonToggleChecks.Text = "Check All";


        }

        private void buttonBrowseData_Click(object sender, EventArgs e)
        {
            using (var folderDialog = new FolderBrowserDialog())
            {
                folderDialog.Description = "Select folder to save transferred data";
                folderDialog.ShowNewFolderButton = true;

                if (folderDialog.ShowDialog() == DialogResult.OK)
                {
                    textBoxSavePathway.Text = folderDialog.SelectedPath;
                }
            }
        }
        private void buttonRefreshFiles_Click(object sender, EventArgs e)
        {
            RefreshFileListForSelectedDetector();
        }
        private void UpdateTransferButtonState()
        {
            bool anyChecked = listViewFileList.Items
                .Cast<ListViewItem>()
                .Any(item => item.Checked);

            buttonTransferSelected.Enabled = anyChecked;
        }

        private void buttonToggleChecks_Click(object sender, EventArgs e)
        {
            bool checkAll = listViewFileList.Items
                .Cast<ListViewItem>()
                .Any(item => !item.Checked);

            foreach (ListViewItem item in listViewFileList.Items)
            {
                item.Checked = checkAll;
            }

            // Optionally update button text
            buttonToggleChecks.Text = checkAll ? "Uncheck All" : "Check All";
        }

        private void TransferSavedFiles_Click(object? sender, EventArgs e)
        {
            var form = new FormFileTransfer(detectors);
            form.Show();
        }
        private void EraseFile(string detectorIp, string filename)
        {
            var detector = GetDetectorByIp(detectorIp);
            if (detector == null || !detector.CheckState(InstrumentState.ONLINE))
            {
                AppendLog($"[ERROR] Detector {detectorIp} is not online.");
                return;
            }

            if (string.IsNullOrWhiteSpace(filename))
            {
                AppendLog($"[ERROR] Invalid filename for detector {detectorIp}.");
                return;
            }

            // Erase specific file (not all!)
            detector.WriteAndWait($"LFSformat = {filename}");
            AppendLog($"Deleted '{filename}' from detector {detectorIp}.");
        }

        private void AppendLog(string message)
        {
            if (InvokeRequired)
            {
                Invoke(() => AppendLog(message));
                return;
            }

            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            richTextBoxTransferMessage.AppendText($"[{timestamp}] {message}{Environment.NewLine}");

            // At start of form or before transfer session
            richTextBoxTransferMessage.Clear();

            // Auto-scroll to bottom
            richTextBoxTransferMessage.SelectionStart = richTextBoxTransferMessage.Text.Length;
            richTextBoxTransferMessage.ScrollToCaret();

        }

        private void buttonEraseSelected_Click(object sender, EventArgs e)
        {
            bool anyChecked = false;

            foreach (ListViewItem item in listViewFileList.Items)
            {
                if (item.Checked)
                {
                    anyChecked = true;
                    string detectorIp = item.SubItems[0].Text;
                    string filename = item.SubItems[1].Text;
                    EraseFile(detectorIp, filename);
                }
            }

            if (!anyChecked)
            {
                AppendLog("No files were checked for erasure.");
                return;
            }

            RefreshFileListForSelectedDetector();
            AppendLog("Selected file(s) erased.");
        }

        private void buttonEraseAll_Click(object sender, EventArgs e)
        {
            AppendLog("[WARNING] Erase All triggered.");

            var distinctIps = listViewFileList.Items
                .Cast<ListViewItem>()
                .Select(item => item.SubItems[0].Text)
                .Distinct();

            foreach (string ip in distinctIps)
            {
                var detector = GetDetectorByIp(ip);
                if (detector != null && detector.CheckState(InstrumentState.ONLINE))
                {
                    detector.WriteAndWait("LFSformat");  // This deletes all
                    AppendLog($"All files erased from detector {ip}");
                }
            }

            RefreshFileListForSelectedDetector();
        }
    }
}
