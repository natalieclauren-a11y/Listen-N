namespace Listen_N
{
    partial class FormFileTransfer
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            listViewDetectorAddresses = new ListView();
            ListviewDetectorDataName = new ColumnHeader();
            listViewFileList = new ListView();
            columnHeaderDetectorAddress = new ColumnHeader();
            columnHeaderFileName = new ColumnHeader();
            columnHeaderFileSize = new ColumnHeader();
            buttonRefreshFiles = new Button();
            labelDataDirectory = new Label();
            textBoxSavePathway = new TextBox();
            buttonBrowseData = new Button();
            groupBoxSaveProgress = new GroupBox();
            progressBarSave = new ProgressBar();
            labelSaveProgress = new Label();
            groupBox1 = new GroupBox();
            buttonEraseAll = new Button();
            buttonEraseSelected = new Button();
            groupBoxTransferSelected = new GroupBox();
            richTextBoxTransferMessage = new RichTextBox();
            buttonTransferAll = new Button();
            buttonTransferSelected = new Button();
            radioButtonUSB = new RadioButton();
            radioButtonPC = new RadioButton();
            buttonToggleChecks = new Button();
            groupBoxSaveProgress.SuspendLayout();
            groupBox1.SuspendLayout();
            groupBoxTransferSelected.SuspendLayout();
            SuspendLayout();
            // 
            // listViewDetectorAddresses
            // 
            listViewDetectorAddresses.Columns.AddRange(new ColumnHeader[] { ListviewDetectorDataName });
            listViewDetectorAddresses.FullRowSelect = true;
            listViewDetectorAddresses.Location = new Point(12, 12);
            listViewDetectorAddresses.Name = "listViewDetectorAddresses";
            listViewDetectorAddresses.Size = new Size(255, 97);
            listViewDetectorAddresses.TabIndex = 0;
            listViewDetectorAddresses.UseCompatibleStateImageBehavior = false;
            listViewDetectorAddresses.View = View.Details;
            // 
            // ListviewDetectorDataName
            // 
            ListviewDetectorDataName.Text = "Detector Address";
            ListviewDetectorDataName.Width = 250;
            // 
            // listViewFileList
            // 
            listViewFileList.CheckBoxes = true;
            listViewFileList.Columns.AddRange(new ColumnHeader[] { columnHeaderDetectorAddress, columnHeaderFileName, columnHeaderFileSize });
            listViewFileList.FullRowSelect = true;
            listViewFileList.Location = new Point(12, 144);
            listViewFileList.Name = "listViewFileList";
            listViewFileList.Size = new Size(461, 294);
            listViewFileList.TabIndex = 1;
            listViewFileList.UseCompatibleStateImageBehavior = false;
            listViewFileList.View = View.Details;
            // 
            // columnHeaderDetectorAddress
            // 
            columnHeaderDetectorAddress.Text = "Detector";
            columnHeaderDetectorAddress.Width = 175;
            // 
            // columnHeaderFileName
            // 
            columnHeaderFileName.Text = "File Name";
            columnHeaderFileName.Width = 175;
            // 
            // columnHeaderFileSize
            // 
            columnHeaderFileSize.Text = "File Size (bytes)";
            columnHeaderFileSize.Width = 100;
            // 
            // buttonRefreshFiles
            // 
            buttonRefreshFiles.Location = new Point(490, 57);
            buttonRefreshFiles.Name = "buttonRefreshFiles";
            buttonRefreshFiles.Size = new Size(100, 32);
            buttonRefreshFiles.TabIndex = 2;
            buttonRefreshFiles.Text = "Refresh Files";
            buttonRefreshFiles.UseVisualStyleBackColor = true;
            // 
            // labelDataDirectory
            // 
            labelDataDirectory.AutoSize = true;
            labelDataDirectory.Location = new Point(273, 12);
            labelDataDirectory.Name = "labelDataDirectory";
            labelDataDirectory.Size = new Size(73, 15);
            labelDataDirectory.TabIndex = 3;
            labelDataDirectory.Text = "Save Data To";
            // 
            // textBoxSavePathway
            // 
            textBoxSavePathway.Location = new Point(352, 9);
            textBoxSavePathway.Name = "textBoxSavePathway";
            textBoxSavePathway.Size = new Size(251, 23);
            textBoxSavePathway.TabIndex = 4;
            // 
            // buttonBrowseData
            // 
            buttonBrowseData.Location = new Point(609, 9);
            buttonBrowseData.Name = "buttonBrowseData";
            buttonBrowseData.Size = new Size(75, 23);
            buttonBrowseData.TabIndex = 5;
            buttonBrowseData.Text = "Browse";
            buttonBrowseData.UseVisualStyleBackColor = true;
            // 
            // groupBoxSaveProgress
            // 
            groupBoxSaveProgress.Controls.Add(progressBarSave);
            groupBoxSaveProgress.Controls.Add(labelSaveProgress);
            groupBoxSaveProgress.Location = new Point(273, 38);
            groupBoxSaveProgress.Name = "groupBoxSaveProgress";
            groupBoxSaveProgress.Size = new Size(200, 71);
            groupBoxSaveProgress.TabIndex = 6;
            groupBoxSaveProgress.TabStop = false;
            // 
            // progressBarSave
            // 
            progressBarSave.Location = new Point(6, 37);
            progressBarSave.Name = "progressBarSave";
            progressBarSave.Size = new Size(188, 23);
            progressBarSave.TabIndex = 7;
            // 
            // labelSaveProgress
            // 
            labelSaveProgress.AutoSize = true;
            labelSaveProgress.Location = new Point(6, 19);
            labelSaveProgress.Name = "labelSaveProgress";
            labelSaveProgress.Size = new Size(79, 15);
            labelSaveProgress.TabIndex = 8;
            labelSaveProgress.Text = "Save Progress";
            // 
            // groupBox1
            // 
            groupBox1.Controls.Add(buttonEraseAll);
            groupBox1.Controls.Add(buttonEraseSelected);
            groupBox1.Location = new Point(479, 364);
            groupBox1.Name = "groupBox1";
            groupBox1.Size = new Size(215, 74);
            groupBox1.TabIndex = 7;
            groupBox1.TabStop = false;
            // 
            // buttonEraseAll
            // 
            buttonEraseAll.Location = new Point(117, 22);
            buttonEraseAll.Name = "buttonEraseAll";
            buttonEraseAll.Size = new Size(88, 33);
            buttonEraseAll.TabIndex = 8;
            buttonEraseAll.Text = "Erase All";
            buttonEraseAll.UseVisualStyleBackColor = true;
            // 
            // buttonEraseSelected
            // 
            buttonEraseSelected.Location = new Point(6, 22);
            buttonEraseSelected.Name = "buttonEraseSelected";
            buttonEraseSelected.Size = new Size(100, 33);
            buttonEraseSelected.TabIndex = 8;
            buttonEraseSelected.Text = "Erase Selected";
            buttonEraseSelected.UseVisualStyleBackColor = true;
            // 
            // groupBoxTransferSelected
            // 
            groupBoxTransferSelected.Controls.Add(richTextBoxTransferMessage);
            groupBoxTransferSelected.Controls.Add(buttonTransferAll);
            groupBoxTransferSelected.Controls.Add(buttonTransferSelected);
            groupBoxTransferSelected.Controls.Add(radioButtonUSB);
            groupBoxTransferSelected.Controls.Add(radioButtonPC);
            groupBoxTransferSelected.Location = new Point(479, 127);
            groupBoxTransferSelected.Name = "groupBoxTransferSelected";
            groupBoxTransferSelected.Size = new Size(215, 231);
            groupBoxTransferSelected.TabIndex = 8;
            groupBoxTransferSelected.TabStop = false;
            groupBoxTransferSelected.Text = "Transfer Files";
            // 
            // richTextBoxTransferMessage
            // 
            richTextBoxTransferMessage.Location = new Point(11, 169);
            richTextBoxTransferMessage.Name = "richTextBoxTransferMessage";
            richTextBoxTransferMessage.ReadOnly = true;
            richTextBoxTransferMessage.Size = new Size(194, 42);
            richTextBoxTransferMessage.TabIndex = 9;
            richTextBoxTransferMessage.Text = "";
            // 
            // buttonTransferAll
            // 
            buttonTransferAll.Location = new Point(40, 127);
            buttonTransferAll.Name = "buttonTransferAll";
            buttonTransferAll.Size = new Size(131, 36);
            buttonTransferAll.TabIndex = 10;
            buttonTransferAll.Text = "Transfer All";
            buttonTransferAll.UseVisualStyleBackColor = true;
            // 
            // buttonTransferSelected
            // 
            buttonTransferSelected.Location = new Point(40, 85);
            buttonTransferSelected.Name = "buttonTransferSelected";
            buttonTransferSelected.Size = new Size(131, 36);
            buttonTransferSelected.TabIndex = 9;
            buttonTransferSelected.Text = "Transfer Selected";
            buttonTransferSelected.UseVisualStyleBackColor = true;
            // 
            // radioButtonUSB
            // 
            radioButtonUSB.AutoSize = true;
            radioButtonUSB.Location = new Point(11, 47);
            radioButtonUSB.Name = "radioButtonUSB";
            radioButtonUSB.Size = new Size(122, 19);
            radioButtonUSB.TabIndex = 9;
            radioButtonUSB.TabStop = true;
            radioButtonUSB.Text = "To Instrument USB";
            radioButtonUSB.UseVisualStyleBackColor = true;
            // 
            // radioButtonPC
            // 
            radioButtonPC.AutoSize = true;
            radioButtonPC.Location = new Point(12, 22);
            radioButtonPC.Name = "radioButtonPC";
            radioButtonPC.Size = new Size(77, 19);
            radioButtonPC.TabIndex = 0;
            radioButtonPC.TabStop = true;
            radioButtonPC.Text = "To this PC";
            radioButtonPC.UseVisualStyleBackColor = true;
            // 
            // buttonToggleChecks
            // 
            buttonToggleChecks.Location = new Point(12, 115);
            buttonToggleChecks.Name = "buttonToggleChecks";
            buttonToggleChecks.Size = new Size(75, 23);
            buttonToggleChecks.TabIndex = 9;
            buttonToggleChecks.Text = "Check All";
            buttonToggleChecks.UseVisualStyleBackColor = true;
            // 
            // FormFileTransfer
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(705, 450);
            Controls.Add(buttonToggleChecks);
            Controls.Add(groupBoxTransferSelected);
            Controls.Add(groupBox1);
            Controls.Add(groupBoxSaveProgress);
            Controls.Add(buttonBrowseData);
            Controls.Add(textBoxSavePathway);
            Controls.Add(labelDataDirectory);
            Controls.Add(buttonRefreshFiles);
            Controls.Add(listViewFileList);
            Controls.Add(listViewDetectorAddresses);
            Name = "FormFileTransfer";
            Text = "File Transfer";
            Load += FormFileTransfer_Load;
            groupBoxSaveProgress.ResumeLayout(false);
            groupBoxSaveProgress.PerformLayout();
            groupBox1.ResumeLayout(false);
            groupBoxTransferSelected.ResumeLayout(false);
            groupBoxTransferSelected.PerformLayout();
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private ListView listViewDetectorAddresses;
        private ColumnHeader ListviewDetectorDataName;
        private ListView listViewFileList;
        private ColumnHeader columnHeaderDetectorAddress;
        private ColumnHeader columnHeaderFileName;
        private ColumnHeader columnHeaderFileSize;
        private Button buttonRefreshFiles;
        private Label labelDataDirectory;
        private TextBox textBoxSavePathway;
        private Button buttonBrowseData;
        private GroupBox groupBoxSaveProgress;
        private ProgressBar progressBarSave;
        private Label labelSaveProgress;
        private GroupBox groupBox1;
        private Button buttonEraseAll;
        private Button buttonEraseSelected;
        private GroupBox groupBoxTransferSelected;
        private Button buttonTransferAll;
        private Button buttonTransferSelected;
        private RadioButton radioButtonUSB;
        private RadioButton radioButtonPC;
        private RichTextBox richTextBoxTransferMessage;
        private Button buttonToggleChecks;
    }
}