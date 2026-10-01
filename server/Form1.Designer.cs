using System;
using System.ComponentModel;
using System.Windows.Forms;

namespace server
{
    partial class Form1
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private IContainer components = null;

        // --- CHỈ GIỮ LẠI CÁC CONTROL CẦN THIẾT CHO SERVER ---
        private ListBox lstClients;
        private ListBox lstLog;
        private Label lblClients;
        private Label lblLog;




        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        /// <summary>
        /// Initialize UI controls (Server)
        /// </summary>
        private void InitializeComponent()
        {
            this.components = new Container();

            this.lstClients = new ListBox();
            this.lstLog = new ListBox();
            this.lblClients = new Label();
            this.lblLog = new Label();


            this.SuspendLayout();

            // Form
            this.Text = "UDP Airdrop Mini - Server";
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Width = 700;
            this.Height = 520; // Giữ nguyên kích thước cửa sổ
            this.AutoScaleMode = AutoScaleMode.Font;
            this.MinimumSize = new System.Drawing.Size(400, 300);

            // lblClients
            this.lblClients.Text = "Connected clients:";
            this.lblClients.Left = 10;
            this.lblClients.Top = 10;
            this.lblClients.Width = 200;

            // lstClients
            this.lstClients.Name = "lstClients";
            this.lstClients.Left = 10;
            this.lstClients.Top = 30;
            this.lstClients.Width = 220;
            // --- SỬA LỖI: Tăng chiều cao để lấp đầy form ---
            this.lstClients.Height = 440;
            this.lstClients.Anchor = (AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Bottom);


            // lblLog
            this.lblLog.Text = "Log / Transfers:";
            this.lblLog.Left = 240;
            this.lblLog.Top = 10;
            this.lblLog.Width = 200;

            // lstLog
            this.lstLog.Name = "lstLog";
            this.lstLog.Left = 240;
            this.lstLog.Top = 30;
            // --- SỬA LỖI: Tăng chiều rộng và chiều cao để lấp đầy form ---
            this.lstLog.Width = 440;
            this.lstLog.Height = 440;
            this.lstLog.Anchor = (AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Bottom | AnchorStyles.Right);
            this.lstLog.HorizontalScrollbar = true; // Thêm thanh cuộn ngang

            // --- ĐÃ XÓA CÁC CONTROL GỬI FILE KHỎI SERVER ---

            // Add controls to form
            this.Controls.Add(this.lblClients);
            this.Controls.Add(this.lstClients);
            this.Controls.Add(this.lblLog);
            this.Controls.Add(this.lstLog);



            this.ResumeLayout(false);
            this.PerformLayout();
        }
    }
}