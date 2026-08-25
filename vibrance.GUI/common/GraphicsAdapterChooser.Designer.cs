namespace vibrance.GUI.common
{
    partial class GraphicsAdapterChooser
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
            this.labelHeadline = new System.Windows.Forms.Label();
            this.labelExplanation = new System.Windows.Forms.Label();
            this.listViewAdapters = new System.Windows.Forms.ListView();
            this.checkBoxRemember = new System.Windows.Forms.CheckBox();
            this.labelDdu = new System.Windows.Forms.Label();
            this.linkLabelDdu = new System.Windows.Forms.LinkLabel();
            this.buttonUse = new System.Windows.Forms.Button();
            this.buttonCancel = new System.Windows.Forms.Button();
            this.SuspendLayout();
            //
            // labelHeadline
            //
            this.labelHeadline.AutoSize = true;
            this.labelHeadline.Font = new System.Drawing.Font("Microsoft Sans Serif", 9.75F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.labelHeadline.Location = new System.Drawing.Point(18, 18);
            this.labelHeadline.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.labelHeadline.Name = "labelHeadline";
            this.labelHeadline.Size = new System.Drawing.Size(0, 33);
            this.labelHeadline.TabIndex = 0;
            this.labelHeadline.Text = "Which graphics card should vibranceGUI control?";
            //
            // labelExplanation
            //
            this.labelExplanation.Location = new System.Drawing.Point(18, 60);
            this.labelExplanation.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.labelExplanation.Name = "labelExplanation";
            this.labelExplanation.Size = new System.Drawing.Size(858, 66);
            this.labelExplanation.TabIndex = 1;
            this.labelExplanation.Text = "An NVIDIA and an AMD driver are both installed here, and vibranceGUI could not wor" +
    "k out on its own which card is driving your screen. Pick the one whose colour set" +
    "tings it should change.";
            //
            // listViewAdapters
            //
            this.listViewAdapters.FullRowSelect = true;
            this.listViewAdapters.HeaderStyle = System.Windows.Forms.ColumnHeaderStyle.Nonclickable;
            this.listViewAdapters.HideSelection = false;
            this.listViewAdapters.Location = new System.Drawing.Point(18, 135);
            this.listViewAdapters.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.listViewAdapters.MultiSelect = false;
            this.listViewAdapters.Name = "listViewAdapters";
            this.listViewAdapters.Size = new System.Drawing.Size(858, 240);
            this.listViewAdapters.TabIndex = 2;
            this.listViewAdapters.UseCompatibleStateImageBehavior = false;
            this.listViewAdapters.View = System.Windows.Forms.View.Details;
            this.listViewAdapters.SelectedIndexChanged += new System.EventHandler(this.listViewAdapters_SelectedIndexChanged);
            this.listViewAdapters.DoubleClick += new System.EventHandler(this.listViewAdapters_DoubleClick);
            //
            // checkBoxRemember
            //
            this.checkBoxRemember.AutoSize = true;
            this.checkBoxRemember.Checked = true;
            this.checkBoxRemember.CheckState = System.Windows.Forms.CheckState.Checked;
            this.checkBoxRemember.Location = new System.Drawing.Point(18, 390);
            this.checkBoxRemember.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.checkBoxRemember.Name = "checkBoxRemember";
            this.checkBoxRemember.Size = new System.Drawing.Size(0, 29);
            this.checkBoxRemember.TabIndex = 3;
            this.checkBoxRemember.Text = "Remember my choice and stop asking";
            this.checkBoxRemember.UseVisualStyleBackColor = true;
            //
            // labelDdu
            //
            this.labelDdu.AutoSize = true;
            this.labelDdu.ForeColor = System.Drawing.SystemColors.GrayText;
            this.labelDdu.Location = new System.Drawing.Point(18, 432);
            this.labelDdu.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.labelDdu.Name = "labelDdu";
            this.labelDdu.Size = new System.Drawing.Size(0, 25);
            this.labelDdu.TabIndex = 4;
            this.labelDdu.Text = "Swapped graphics cards and never removed the old driver? Only then is it worth cle" +
    "aning up with";
            //
            // linkLabelDdu
            //
            this.linkLabelDdu.AutoSize = true;
            this.linkLabelDdu.Location = new System.Drawing.Point(18, 462);
            this.linkLabelDdu.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.linkLabelDdu.Name = "linkLabelDdu";
            this.linkLabelDdu.Size = new System.Drawing.Size(0, 25);
            this.linkLabelDdu.TabIndex = 5;
            this.linkLabelDdu.TabStop = true;
            this.linkLabelDdu.Text = "Display Driver Uninstaller (guru3d.com)";
            this.linkLabelDdu.LinkClicked += new System.Windows.Forms.LinkLabelLinkClickedEventHandler(this.linkLabelDdu_LinkClicked);
            //
            // buttonUse
            //
            this.buttonUse.Location = new System.Drawing.Point(540, 504);
            this.buttonUse.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.buttonUse.Name = "buttonUse";
            this.buttonUse.Size = new System.Drawing.Size(220, 44);
            this.buttonUse.TabIndex = 6;
            this.buttonUse.Text = "Use this graphics card";
            this.buttonUse.UseVisualStyleBackColor = true;
            this.buttonUse.Click += new System.EventHandler(this.buttonUse_Click);
            //
            // buttonCancel
            //
            this.buttonCancel.Location = new System.Drawing.Point(770, 504);
            this.buttonCancel.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.buttonCancel.Name = "buttonCancel";
            this.buttonCancel.Size = new System.Drawing.Size(106, 44);
            this.buttonCancel.TabIndex = 7;
            this.buttonCancel.Text = "Cancel";
            this.buttonCancel.UseVisualStyleBackColor = true;
            this.buttonCancel.Click += new System.EventHandler(this.buttonCancel_Click);
            //
            // GraphicsAdapterChooser
            //
            this.AcceptButton = this.buttonUse;
            this.AutoScaleDimensions = new System.Drawing.SizeF(144F, 144F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Dpi;
            this.CancelButton = this.buttonCancel;
            this.ClientSize = new System.Drawing.Size(894, 566);
            this.Controls.Add(this.buttonCancel);
            this.Controls.Add(this.buttonUse);
            this.Controls.Add(this.linkLabelDdu);
            this.Controls.Add(this.labelDdu);
            this.Controls.Add(this.checkBoxRemember);
            this.Controls.Add(this.listViewAdapters);
            this.Controls.Add(this.labelExplanation);
            this.Controls.Add(this.labelHeadline);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle;
            this.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "GraphicsAdapterChooser";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "vibranceGUI - select your graphics card";
            this.Load += new System.EventHandler(this.GraphicsAdapterChooser_Load);
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.Label labelHeadline;
        private System.Windows.Forms.Label labelExplanation;
        private System.Windows.Forms.ListView listViewAdapters;
        private System.Windows.Forms.CheckBox checkBoxRemember;
        private System.Windows.Forms.Label labelDdu;
        private System.Windows.Forms.LinkLabel linkLabelDdu;
        private System.Windows.Forms.Button buttonUse;
        private System.Windows.Forms.Button buttonCancel;
    }
}
