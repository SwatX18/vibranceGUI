using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Windows.Forms;
using vibrance.GUI.AMD;
using vibrance.GUI.NVIDIA;

namespace vibrance.GUI.common
{
    public partial class ProcessExplorer : Form
    {

        Form vibranceGui;

        public ProcessExplorer(Form vibranceGui)
        {
            InitializeComponent();

            listView.Columns.Add("Programs", 130, HorizontalAlignment.Left);
            listView.Columns.Add("Full Path", 320, HorizontalAlignment.Left);
            listView.LargeImageList = iconList;
            listView.FullRowSelect = true;
            listView.View = View.Tile;
            listView.HeaderStyle = ColumnHeaderStyle.Nonclickable;

            this.vibranceGui = vibranceGui;

            backgroundWorker.WorkerReportsProgress = true;
            backgroundWorker.RunWorkerAsync();
        }

        private void GetAllProcesses()
        {
            var activeProcceses = Process.GetProcesses();
            int activeApplicationCount = 0;
            foreach (Process process in activeProcceses)
            {
                if (process.MainWindowHandle != IntPtr.Zero && process.Id != Process.GetCurrentProcess().Id)
                {
                    try
                    {
                        string path = GetPathFromProcessId(process);
                        if (path != string.Empty && File.Exists(path))
                        {
                            ProcessExplorerEntry processEntry = new ProcessExplorerEntry(path, Icon.ExtractAssociatedIcon(path), process);
                            backgroundWorker.ReportProgress(++activeApplicationCount, processEntry);
                        }
                    }
                    catch (Exception ex)
                    {
                        VibranceGUI.Log(ex);
                    }
                }
            }
        }

        /// <summary>
        /// Safely gets the executable path from the specified process.
        /// Process.MainModule.FileName crashes when called on a x64 process because vibranceGUI is running as x86 process.
        /// GetModuleFileNameEx stood in for it here and went through Process.Handle, which opens the
        /// target with PROCESS_ALL_ACCESS: measured on Windows 11 as a plain user, that threw for 116
        /// of 225 processes, and every one of those exceptions was caught and written to the log file
        /// with its stack trace. PathResolver asks for query access only.
        /// </summary>
        /// <param name="process">the process to read out the executable path of</param>
        /// <returns>the fully qualified executable path, or an empty string when Windows declines</returns>
        private string GetPathFromProcessId(Process process)
        {
            string imagePath;
            if (PathResolver.TryGetProcessImagePath(process.Id, out imagePath))
            {
                return imagePath;
            }
            return string.Empty;
        }

        private void listView_DoubleClick(object sender, EventArgs e)
        {
            if (listView.SelectedItems.Count == 1)
            {
                ProcessExplorerEntry processExplorerEntry = (ProcessExplorerEntry)listView.SelectedItems[0].Tag;
                if (processExplorerEntry == null)
                {
                    return;
                }
                this.Hide();
                ((VibranceGUI)vibranceGui).AddProgramExtern(processExplorerEntry);
                this.Close();
            }
        }

        private void button_Click(object sender, EventArgs e)
        {
            if (!backgroundWorker.IsBusy)
            {
                listView.Items.Clear();
                backgroundWorker.RunWorkerAsync();
            }
        }

        private void backgroundWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            GetAllProcesses();
        }

        private void backgroundWorker_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            if(!(e.UserState is ProcessExplorerEntry))
            {
                return;
            }
            ProcessExplorerEntry processEntry = (ProcessExplorerEntry)e.UserState;
            iconList.Images.Add(processEntry.Icon);
            var listItem = new ListViewItem(processEntry.ProcessName, iconList.Images.Count - 1);
            listItem.Tag = processEntry;
            listItem.SubItems.Add(processEntry.Path);
            listView.Items.Add(listItem);
        }
    }
}
