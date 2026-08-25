namespace vibrance.GUI.common
{
    partial class GameFinder
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
            this.components = new System.ComponentModel.Container();
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(GameFinder));
            this.listViewGames = new System.Windows.Forms.ListView();
            this.iconList = new System.Windows.Forms.ImageList(this.components);
            this.buttonRescan = new System.Windows.Forms.Button();
            this.buttonSelectAll = new System.Windows.Forms.Button();
            this.buttonSelectNone = new System.Windows.Forms.Button();
            this.buttonAddSelected = new System.Windows.Forms.Button();
            this.buttonCancel = new System.Windows.Forms.Button();
            this.labelProgress = new System.Windows.Forms.Label();
            this.labelCounts = new System.Windows.Forms.Label();
            this.labelSkipped = new System.Windows.Forms.Label();
            this.labelNote = new System.Windows.Forms.Label();
            this.backgroundWorker = new System.ComponentModel.BackgroundWorker();
            this.SuspendLayout();
            //
            // listViewGames
            //
            this.listViewGames.CheckBoxes = true;
            this.listViewGames.FullRowSelect = true;
            this.listViewGames.HeaderStyle = System.Windows.Forms.ColumnHeaderStyle.Nonclickable;
            this.listViewGames.HideSelection = false;
            this.listViewGames.Location = new System.Drawing.Point(18, 72);
            this.listViewGames.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.listViewGames.Name = "listViewGames";
            this.listViewGames.ShowItemToolTips = true;
            this.listViewGames.Size = new System.Drawing.Size(1434, 561);
            this.listViewGames.SmallImageList = this.iconList;
            this.listViewGames.TabIndex = 1;
            this.listViewGames.UseCompatibleStateImageBehavior = false;
            this.listViewGames.View = System.Windows.Forms.View.Details;
            this.listViewGames.ItemCheck += new System.Windows.Forms.ItemCheckEventHandler(this.listViewGames_ItemCheck);
            this.listViewGames.ItemChecked += new System.Windows.Forms.ItemCheckedEventHandler(this.listViewGames_ItemChecked);
            //
            // iconList
            //
            this.iconList.ColorDepth = System.Windows.Forms.ColorDepth.Depth32Bit;
            this.iconList.ImageSize = new System.Drawing.Size(16, 16);
            this.iconList.TransparentColor = System.Drawing.Color.Transparent;
            //
            // buttonRescan
            //
            this.buttonRescan.Location = new System.Drawing.Point(18, 18);
            this.buttonRescan.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.buttonRescan.Name = "buttonRescan";
            this.buttonRescan.Size = new System.Drawing.Size(165, 42);
            this.buttonRescan.TabIndex = 0;
            this.buttonRescan.Text = "Rescan";
            this.buttonRescan.UseVisualStyleBackColor = true;
            this.buttonRescan.Click += new System.EventHandler(this.buttonRescan_Click);
            //
            // buttonSelectAll
            //
            this.buttonSelectAll.Location = new System.Drawing.Point(18, 645);
            this.buttonSelectAll.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.buttonSelectAll.Name = "buttonSelectAll";
            this.buttonSelectAll.Size = new System.Drawing.Size(150, 42);
            this.buttonSelectAll.TabIndex = 2;
            this.buttonSelectAll.Text = "Select all";
            this.buttonSelectAll.UseVisualStyleBackColor = true;
            this.buttonSelectAll.Click += new System.EventHandler(this.buttonSelectAll_Click);
            //
            // buttonSelectNone
            //
            this.buttonSelectNone.Location = new System.Drawing.Point(177, 645);
            this.buttonSelectNone.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.buttonSelectNone.Name = "buttonSelectNone";
            this.buttonSelectNone.Size = new System.Drawing.Size(150, 42);
            this.buttonSelectNone.TabIndex = 3;
            this.buttonSelectNone.Text = "Select none";
            this.buttonSelectNone.UseVisualStyleBackColor = true;
            this.buttonSelectNone.Click += new System.EventHandler(this.buttonSelectNone_Click);
            //
            // buttonAddSelected
            //
            this.buttonAddSelected.Location = new System.Drawing.Point(1062, 771);
            this.buttonAddSelected.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.buttonAddSelected.Name = "buttonAddSelected";
            this.buttonAddSelected.Size = new System.Drawing.Size(255, 48);
            this.buttonAddSelected.TabIndex = 4;
            this.buttonAddSelected.Text = "Add selected (0)";
            this.buttonAddSelected.UseVisualStyleBackColor = true;
            this.buttonAddSelected.Click += new System.EventHandler(this.buttonAddSelected_Click);
            //
            // buttonCancel
            //
            this.buttonCancel.Location = new System.Drawing.Point(1329, 771);
            this.buttonCancel.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.buttonCancel.Name = "buttonCancel";
            this.buttonCancel.Size = new System.Drawing.Size(123, 48);
            this.buttonCancel.TabIndex = 5;
            this.buttonCancel.Text = "Cancel";
            this.buttonCancel.UseVisualStyleBackColor = true;
            this.buttonCancel.Click += new System.EventHandler(this.buttonCancel_Click);
            //
            // labelProgress
            //
            this.labelProgress.AutoEllipsis = true;
            this.labelProgress.Location = new System.Drawing.Point(198, 27);
            this.labelProgress.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.labelProgress.Name = "labelProgress";
            this.labelProgress.Size = new System.Drawing.Size(1254, 33);
            this.labelProgress.TabIndex = 6;
            this.labelProgress.Text = "Scanning your game libraries...";
            //
            // labelCounts
            //
            this.labelCounts.AutoSize = true;
            this.labelCounts.Location = new System.Drawing.Point(342, 654);
            this.labelCounts.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.labelCounts.Name = "labelCounts";
            this.labelCounts.Size = new System.Drawing.Size(0, 25);
            this.labelCounts.TabIndex = 7;
            //
            // labelSkipped
            //
            this.labelSkipped.AutoSize = true;
            this.labelSkipped.ForeColor = System.Drawing.SystemColors.GrayText;
            this.labelSkipped.Location = new System.Drawing.Point(18, 702);
            this.labelSkipped.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.labelSkipped.Name = "labelSkipped";
            this.labelSkipped.Size = new System.Drawing.Size(0, 25);
            this.labelSkipped.TabIndex = 8;
            //
            // labelNote
            //
            this.labelNote.AutoSize = true;
            this.labelNote.Font = new System.Drawing.Font("Microsoft Sans Serif", 8.25F, System.Drawing.FontStyle.Italic, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.labelNote.ForeColor = System.Drawing.SystemColors.GrayText;
            this.labelNote.Location = new System.Drawing.Point(18, 738);
            this.labelNote.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.labelNote.Name = "labelNote";
            this.labelNote.Size = new System.Drawing.Size(0, 25);
            this.labelNote.TabIndex = 9;
            this.labelNote.Text = "Executables are a best guess - you can change one from a game\'s settings.";
            //
            // backgroundWorker
            //
            this.backgroundWorker.WorkerReportsProgress = true;
            this.backgroundWorker.WorkerSupportsCancellation = true;
            this.backgroundWorker.DoWork += new System.ComponentModel.DoWorkEventHandler(this.backgroundWorker_DoWork);
            this.backgroundWorker.ProgressChanged += new System.ComponentModel.ProgressChangedEventHandler(this.backgroundWorker_ProgressChanged);
            this.backgroundWorker.RunWorkerCompleted += new System.ComponentModel.RunWorkerCompletedEventHandler(this.backgroundWorker_RunWorkerCompleted);
            //
            // GameFinder
            //
            this.AcceptButton = this.buttonAddSelected;
            this.AutoScaleDimensions = new System.Drawing.SizeF(144F, 144F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Dpi;
            this.CancelButton = this.buttonCancel;
            this.ClientSize = new System.Drawing.Size(1470, 840);
            this.Controls.Add(this.labelNote);
            this.Controls.Add(this.labelSkipped);
            this.Controls.Add(this.labelCounts);
            this.Controls.Add(this.labelProgress);
            this.Controls.Add(this.buttonCancel);
            this.Controls.Add(this.buttonAddSelected);
            this.Controls.Add(this.buttonSelectNone);
            this.Controls.Add(this.buttonSelectAll);
            this.Controls.Add(this.buttonRescan);
            this.Controls.Add(this.listViewGames);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle;
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
            this.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "GameFinder";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "vibranceGUI Game Finder";
            this.FormClosing += new System.Windows.Forms.FormClosingEventHandler(this.GameFinder_FormClosing);
            this.Load += new System.EventHandler(this.GameFinder_Load);
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.ListView listViewGames;
        private System.Windows.Forms.ImageList iconList;
        private System.Windows.Forms.Button buttonRescan;
        private System.Windows.Forms.Button buttonSelectAll;
        private System.Windows.Forms.Button buttonSelectNone;
        private System.Windows.Forms.Button buttonAddSelected;
        private System.Windows.Forms.Button buttonCancel;
        private System.Windows.Forms.Label labelProgress;
        private System.Windows.Forms.Label labelCounts;
        private System.Windows.Forms.Label labelSkipped;
        private System.Windows.Forms.Label labelNote;
        private System.ComponentModel.BackgroundWorker backgroundWorker;
    }
}
