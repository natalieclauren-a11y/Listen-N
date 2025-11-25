using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Policy;
using System.Text;
using System.Threading.Tasks;
using Vf61Gui;

namespace MultiPass
{
    public class TabPageMessages
    {
        public ColumnHeader? columnHeaderTime;
        public ColumnHeader? columnHeaderMessage;
        public ColumnHeader? columnHeaderFile;
        public ColumnHeader? columnHeaderSize;
        public ListView? listViewCommands;
        public ListView? listViewFiles;
        public ProgressBar? progressBar;
        public Button? buttonSave;
        public Button? buttonDeleteFiles;
        public Button? buttonHVWrite;
        public TextBox? textBoxMessages;
        public TextBox? textBoxHVRead;
        public NumericUpDown? numericUpDownHVWrite;
        public ContextMenuStrip? contextMenuClearListView;
        public ContextMenuStrip? contextMenuHVWriteDefault;
        public ToolStripMenuItem? toolStripMenuItemClear;
        public ToolStripMenuItem? toolStripMenuItemHVWriteDefault;
        public TabPage? tabPage;
        public Detector detector;
        // To detect redundant calls
        private bool _disposedValue;

        // Instantiate a SafeHandle instance.
        private SafeHandle? _safeHandle = new SafeFileHandle(IntPtr.Zero, true);

        public TabPageMessages(Detector detector)
        {
            this.detector = detector;
            InitializeComponent();
        }

        // Public implementation of Dispose pattern callable by consumers.
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        // Protected implementation of Dispose pattern.
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    _safeHandle?.Dispose();
                    _safeHandle = null;
                    listViewCommands.RetrieveVirtualItem -= listViewCommands_RetrieveVirtualItem;
                    listViewCommands.CacheVirtualItems -= listViewCommands_CacheVirtualItems;
                    listViewCommands.SearchForVirtualItem -= listViewCommands_SearchForVirtualItem;
                }

                _disposedValue = true;
            }
        }

        public TabPage? TabPage
        {
            get { return tabPage; }
        }


        private void InitializeComponent()
        {
            // 
            // columnHeaderTime
            // 
            columnHeaderTime = new ColumnHeader();
            columnHeaderTime.Text = "Time";
            columnHeaderTime.Width = 200;
            // 
            // columnHeaderMessage
            // 
            columnHeaderMessage = new ColumnHeader();
            columnHeaderMessage.Text = "Message";
            columnHeaderMessage.Width = 900;
            //
            // columnHeaderFile
            //
            columnHeaderFile = new ColumnHeader();
            columnHeaderFile.Text = "Filename";
            columnHeaderFile.Width = 400;
            //
            // columnHeaderSize
            //
            columnHeaderSize = new ColumnHeader();
            columnHeaderSize.Text = "File Size";
            columnHeaderSize.Width = 200;
            //
            //toolStripMenuItemClear
            //
            toolStripMenuItemClear = new ToolStripMenuItem();
            toolStripMenuItemClear.Name = "toolStripMenuItemClear";
            toolStripMenuItemClear.Text = "Clear";
            toolStripMenuItemClear.Size = new Size(180, 20);
            toolStripMenuItemClear.Click += new EventHandler(toolStripMenuItemClear_Click);
            // 
            // contextMenuClearListView
            // 
            contextMenuClearListView = new ContextMenuStrip();
            contextMenuClearListView.Items.AddRange(new ToolStripItem[] { toolStripMenuItemClear });
            contextMenuClearListView.Name = "contextMenuStripListViewAddresses";
            contextMenuClearListView.Size = new Size(180, 70);
            //
            // toolStripMenuItemHVWriteDefault
            //
            toolStripMenuItemHVWriteDefault = new ToolStripMenuItem();
            toolStripMenuItemHVWriteDefault.Name = "toolStripMenuItemHVWriteDefault";
            toolStripMenuItemHVWriteDefault.Text = "Default Value";
            toolStripMenuItemHVWriteDefault.Size = new Size(180, 20);
            toolStripMenuItemHVWriteDefault.Click += new EventHandler(toolStripMenuItemHVWriteDefault_Click);
            //
            // contextMenuHVWriteDefault
            //
            contextMenuHVWriteDefault = new ContextMenuStrip();
            contextMenuHVWriteDefault.Items.AddRange(new ToolStripItem[] { toolStripMenuItemHVWriteDefault });
            contextMenuHVWriteDefault.Name = "contextMenuHVWriteDefault";
            contextMenuHVWriteDefault.Size = new Size(180, 70);
            // 
            // listViewCommands
            // 
            listViewCommands = new ListView();
            listViewCommands.Columns.AddRange(new ColumnHeader[] { columnHeaderTime, columnHeaderMessage });
            //listViewCommands.Dock = DockStyle.Fill;
            //listViewCommands.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            listViewCommands.FullRowSelect = true;
            listViewCommands.Location = new Point(0, 0);
            listViewCommands.Name = "listViewCommands";
            listViewCommands.Size = new Size(400, 200);
            listViewCommands.TabIndex = 0;
            listViewCommands.UseCompatibleStateImageBehavior = false;
            listViewCommands.View = View.Details;
            listViewCommands.Sorting = SortOrder.Ascending;
            listViewCommands.ContextMenuStrip = contextMenuClearListView;
            listViewCommands.RetrieveVirtualItem += new RetrieveVirtualItemEventHandler(listViewCommands_RetrieveVirtualItem);
            listViewCommands.CacheVirtualItems += new CacheVirtualItemsEventHandler(listViewCommands_CacheVirtualItems);
            listViewCommands.SearchForVirtualItem += new SearchForVirtualItemEventHandler(listViewCommands_SearchForVirtualItem);
            listViewCommands.VirtualMode = false;
            listViewCommands.Tag = new List<ListViewItem>();    // Use this to store virtual items
            //
            //listViewFiles
            //
            listViewFiles = new ListView();
            listViewFiles.Columns.AddRange(new ColumnHeader[] { columnHeaderFile, columnHeaderSize });
            //listViewFiles.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            listViewFiles.FullRowSelect = true;
            listViewFiles.Location = new Point(0, 200);
            listViewFiles.Name = "listViewFiles";
            listViewFiles.Size = new Size(400, 200);
            listViewFiles.TabIndex = 0;
            listViewFiles.UseCompatibleStateImageBehavior = false;
            listViewFiles.View = View.Details;
            //listViewFiles.Sorting = SortOrder.Ascending;
            listViewFiles.ContextMenuStrip = contextMenuClearListView;
            //
            // buttonSave
            // 
            buttonSave = new Button();
            buttonSave.Text = "Save File(s)";
            buttonSave.Size = new Size(100, 30);
            buttonSave.Name = "buttonSave";
            buttonSave.Click += new EventHandler(buttonSaveFiles_Click);
            //
            // buttonDeleteFiles
            //
            buttonDeleteFiles = new Button();
            buttonDeleteFiles.Text = "Delete Files";
            buttonDeleteFiles.Size = new Size(100, 30);
            buttonDeleteFiles.Name = "buttonDeleteFiles";
            buttonDeleteFiles.Click += new EventHandler(buttonDeleteFiles_Click);
            //
            // buttonHVWrite
            //
            buttonHVWrite = new Button();
            buttonHVWrite.Text = "HV Write";
            buttonHVWrite.Size = new Size(100, 30);
            buttonHVWrite.Name = "buttonHVWrite";
            buttonHVWrite.Click += new EventHandler(buttonHVWrite_Click);
            //
            // textBoxMessages
            //
            textBoxMessages = new TextBox();
            textBoxMessages.Text = "";
            textBoxMessages.Name = "textBoxMessages";
            //
            //
            //
            textBoxHVRead = new TextBox();
            textBoxHVRead.Text = "";
            textBoxHVRead.Name = "textBoxHVRead";
            textBoxHVRead.Size = new Size(100, 30);
            //
            // numericUpDownHVWrite
            //
            numericUpDownHVWrite = new NumericUpDown();
            numericUpDownHVWrite.Name = "numericUpDownHVWrite";
            numericUpDownHVWrite.Size = new Size(100, 30);
            numericUpDownHVWrite.Minimum = 0.0m; 
            numericUpDownHVWrite.Maximum = 2000.0m;
            numericUpDownHVWrite.ContextMenuStrip = contextMenuHVWriteDefault;
            //
            // progressBar
            //
            progressBar = new ProgressBar();
            progressBar.Name = "progressBar";
            progressBar.Size = new Size(100, 10);


            //
            // tabPage
            // 
            tabPage = new TabPage();
            tabPage.Name = detector.IpAddress;
            tabPage.Size = new Size(400, 400);
            tabPage.TabIndex = 0;
            tabPage.Text = detector.IpAddress;
            tabPage.UseVisualStyleBackColor = true;
            tabPage.Controls.Add(listViewCommands);
            tabPage.Controls.Add(listViewFiles);
            tabPage.Controls.Add(buttonSave);
            tabPage.Controls.Add(buttonDeleteFiles);
            tabPage.Controls.Add(buttonHVWrite);
            tabPage.Controls.Add(numericUpDownHVWrite);
            tabPage.Controls.Add(textBoxHVRead);
            tabPage.Controls.Add(textBoxMessages);
            tabPage.Controls.Add(progressBar);
            tabPage.Tag = detector;
            tabPage.Resize += new EventHandler(tabPage_Resize);
        }


        public static ListView? GetListViewControl(TabPage tabPage, string name)
        {
            Control[]? controls = tabPage.Controls.Find(name, false).ToArray();
            if(controls != null && controls.Length > 0)
            {
                return (ListView)controls[0];
            }
            else
            {
                return null;
            }
        }

        public static Button? GetButtonControl(TabPage tabPage, string name)
        {
            Control[]? controls = tabPage.Controls.Find(name, false).ToArray();
            if(controls != null && controls.Length > 0)
            {
                return (Button?)controls[0];
            }
            else
            {
                return null;
            }
        }
        public static TextBox? GetTextBoxControl(TabPage tabPage, string name)
        {
            Control[]? controls = tabPage.Controls.Find(name, false).ToArray();
            if (controls != null && controls.Length > 0)
            {
                return (TextBox?)controls[0];
            }
            else
            {
                return null;
            }
        }
        public static ProgressBar? GetProgressBarControl(TabPage tabPage, string name)
        {
            Control[]? controls = tabPage.Controls.Find(name, false).ToArray();
            if (controls != null && controls.Length > 0)
            {
                return (ProgressBar?)controls[0];
            }
            else
            {
                return null;
            }
        }

        public static NumericUpDown? GetNumericUpDownControl(TabPage tabPage, string name)
        {
            Control[]? controls = tabPage.Controls.Find(name, false).ToArray();
            if (controls != null && controls.Length > 0)
            {
                return (NumericUpDown?)controls[0];
            }
            else
            {
                return null;
            }
        }

        public static void EnableDisableButtons(TabPage tabPage, bool enabled)
        {
            List<Control> listControls = tabPage.GetAllControls(typeof(System.Windows.Forms.Button));
            foreach (Control control in listControls)
            {
                control.Enabled = enabled;
            }
        }

        public static void tabPage_Resize(object sender, EventArgs e)
        {
            TabPage tabPage = (TabPage)sender;
            ListView? listViewCommands = GetListViewControl(tabPage, "listViewCommands");
            ListView? listViewFiles = GetListViewControl(tabPage, "listViewFiles");
            Button? buttonSave = GetButtonControl(tabPage, "buttonSave");
            Button? buttonDelete = GetButtonControl(tabPage, "buttonDeleteFiles");
            Button? buttonHVWrite = GetButtonControl(tabPage, "buttonHVWrite");
            TextBox? textBoxMessages = GetTextBoxControl(tabPage, "textBoxMessages");
            TextBox? textBoxHVRead = GetTextBoxControl(tabPage, "textBoxHVRead");
            NumericUpDown? numericUpDownHVWrite = GetNumericUpDownControl(tabPage, "numericUpDownHVWrite");
            ProgressBar? progressBar = GetProgressBarControl(tabPage, "progressBar");

            if ((listViewCommands == null) 
                || (listViewFiles == null) 
                || (buttonSave == null)
                || (buttonDelete == null)
                || (buttonHVWrite == null)
                || (textBoxMessages == null)
                || (textBoxHVRead == null)
                || (numericUpDownHVWrite == null)
                || (progressBar == null))
            {
                return;
            }

            listViewCommands.SuspendLayout();
            listViewFiles.SuspendLayout();
            buttonSave.SuspendLayout();
            buttonDelete.SuspendLayout();
            buttonHVWrite.SuspendLayout();
            textBoxMessages.SuspendLayout();
            textBoxHVRead.SuspendLayout();
            numericUpDownHVWrite.SuspendLayout();
            progressBar.SuspendLayout();

            int buttonPadding = 5;
            int progressBarHeight = 10;

            listViewCommands.Size = new Size(tabPage.Width - buttonSave.Width - 2 * buttonPadding, tabPage.Height / 2);
            listViewCommands.Location = new Point(0, 0);

            listViewFiles.Size = new Size(listViewCommands.Width, tabPage.Height / 2 - progressBarHeight);
            listViewFiles.Location = new Point(0, listViewCommands.Bottom);

            buttonSave.Location = new Point(listViewFiles.Right + buttonPadding, buttonPadding);
            buttonDelete.Location = new Point(buttonSave.Left, buttonSave.Bottom + buttonPadding);
            buttonHVWrite.Location = new Point(buttonSave.Left, buttonDelete.Bottom + buttonPadding);
            textBoxHVRead.Location = new Point(buttonSave.Left, buttonHVWrite.Bottom + buttonPadding);
            numericUpDownHVWrite.Location = new Point(buttonSave.Left, textBoxHVRead.Bottom + buttonPadding);

            progressBar.Size = new Size(listViewFiles.Width, progressBarHeight);
            progressBar.Location = new Point(0, listViewFiles.Bottom);

            //textBoxMessages.Size = new Size

            listViewCommands.ResumeLayout();
            listViewFiles.ResumeLayout();
            buttonSave.ResumeLayout();
            buttonDelete.ResumeLayout();
            buttonHVWrite.ResumeLayout();
            textBoxMessages.ResumeLayout();
            textBoxHVRead.ResumeLayout();
            numericUpDownHVWrite.ResumeLayout();
            progressBar.ResumeLayout();

        }

        public static async void buttonSaveFiles_Click(object sender, EventArgs e)
        {
            TabPage? tabPage = (TabPage?)((Button)sender).Parent;
            if(tabPage == null)
            {
                return;
            }
            Detector? detector = (tabPage.Tag == null) ? null : (Detector)tabPage.Tag;

            ListView? listViewFiles = GetListViewControl(tabPage, "listViewFiles");
            if(listViewFiles == null) { return; }

            if(listViewFiles.SelectedItems.Count == 0 ) { return; }

            ProgressBar? progressBar = GetProgressBarControl(tabPage, "progressBar");
            progressBar.Value = 0;
            progressBar.Maximum = listViewFiles.SelectedItems.Count;
            
            int[] indices = listViewFiles.SelectedIndices.Cast<int>().ToArray();
            Cursor.Current = Cursors.WaitCursor;
            foreach (int index in indices)
            {
                string filename = listViewFiles.Items[index].SubItems[0].Text;
                progressBar.Value = index;

                detector?.resetEventNetSaveFile.Reset();
                detector?.Write("NetSaveFile = " + filename);
                Thread.Sleep(100); // Give detector some time to open binaryWriter
                if (detector?.binaryWriter != null)
                {
                    detector?.resetEventNetSaveFile.WaitOne();
                }

            }
            progressBar.Value = progressBar.Maximum;
            Cursor.Current = Cursors.Default;
        }

 


        public static void buttonDeleteFiles_Click(object sender, EventArgs e)
        {
            TabPage? tabPage = (TabPage?)((Button)sender).Parent;
            if (tabPage == null)
            {
                return;
            }
            Detector? detector = (tabPage.Tag == null) ? null : (Detector)tabPage.Tag;
            DialogResult result = MessageBox.Show("Delete All Files?", "Delete All Files", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (result == DialogResult.Yes)
            {
                detector?.WriteAndWait("LFSformat");
            }
        }

        public static void buttonHVWrite_Click(object sender, EventArgs e)
        {
            TabPage? tabPage = (TabPage?)((Button)sender).Parent;
            if (tabPage == null)
            {
                return;
            }
            Detector? detector = (tabPage.Tag == null) ? null : ((Detector)tabPage.Tag);
            NumericUpDown numericUpDownHVset = GetNumericUpDownControl(tabPage, "numericUpDownHVWrite");
            detector.WriteAndWait("HVset = " + numericUpDownHVset.Value.ToString());
            detector.WriteAndWait("HVread");
        }

        public static void toolStripMenuItemHVWriteDefault_Click(object sender, EventArgs e)
        {
            ToolStripMenuItem toolStripMenuItem = (ToolStripMenuItem)sender;
            if (toolStripMenuItem == null)
            {
                return;
            }
            ContextMenuStrip? contextMenuStrip = (ContextMenuStrip?)(toolStripMenuItem.GetCurrentParent());
            if (contextMenuStrip?.SourceControl is NumericUpDown)
            {
                NumericUpDown numericUpDown = (NumericUpDown)contextMenuStrip.SourceControl;
                if(numericUpDown.Name.Equals("numericUpDownHVWrite"))
                {
                    numericUpDown.Value = 1680;
                }
            }
        }
        public static void toolStripMenuItemClear_Click(object sender, EventArgs e)
        {
            ToolStripMenuItem toolStripMenuItem = (ToolStripMenuItem)sender;
            if(toolStripMenuItem == null)
            {
                return;
            }
            ContextMenuStrip? contextMenuStrip = (ContextMenuStrip?)(toolStripMenuItem.GetCurrentParent());
            ListView? listView = (ListView?)contextMenuStrip?.SourceControl;

            if (listView == null)
            {
                return;
            }
            listView.SuspendLayout();
            listView.Items.Clear();
            listView.ResumeLayout();
        }

        public static void listViewCommands_RetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e)
        {
            ListView listView = (ListView)sender;
            if(sender == null)
            {
                return;
            }
        }
        public static void listViewCommands_CacheVirtualItems(object sender, CacheVirtualItemsEventArgs e)
        {

        }
        public static void listViewCommands_SearchForVirtualItem(object sender, SearchForVirtualItemEventArgs e)
        {

        }

    }
}
