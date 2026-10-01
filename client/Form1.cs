using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Client
{
    public partial class Form1 : Form
    {
        // --- 1. BIẾN CONTROL GIAO DIỆN ---
        private TextBox txtServerIp;
        private TextBox txtMyId;
        private Button btnConnect;
        private GroupBox grpSend;
        private TextBox txtTargetId;
        private Button btnBrowse;
        private TextBox txtFilePath;
        private Button btnSendFile;
        private Button btnTamDung;
        private Button btnHuy;
        private ProgressBar progressBar;
        private Label lblPercent;
        private ListBox lstLog;


        // --- 2. BIẾN LOGIC ---
        private TcpClient tcpClient;
        private UdpClient udpPeer;
        private IPEndPoint introEndpoint;
        private CancellationTokenSource _cts;
        private Task keepAliveTask;
        private Task udpReceiveTask;

        private ManualResetEventSlim pauseEvent = new ManualResetEventSlim(true);
        private ConcurrentDictionary<string, TaskCompletionSource<IPEndPoint>> _peerWaiters = new ConcurrentDictionary<string, TaskCompletionSource<IPEndPoint>>();
        private ConcurrentDictionary<string, TaskCompletionSource<int>> ackWaiters = new ConcurrentDictionary<string, TaskCompletionSource<int>>();

        // --- BIẾN ĐỂ NHẬN FILE ---
        private class ReceivingFileState
        {
            public string TransferId { get; set; }
            public string FileName { get; set; }
            public long TotalSize { get; set; }
            public long ReceivedBytes { get; set; }
            public int ExpectedSeq { get; set; }
            public FileStream FileStream { get; set; }
            public IPEndPoint SenderEndPoint { get; set; }
        }

        private ConcurrentDictionary<string, ReceivingFileState> _receivingTransfers = new ConcurrentDictionary<string, ReceivingFileState>();


        // --- 3. KHỞI TẠO FORM (GỌI BuildUI) ---
        public Form1()
        {
            BuildUI();
            this.FormClosing += Form1_FormClosing;
            this.Load += (s, e) => txtMyId.Text = Environment.MachineName;
        }

        /// <summary>
        /// XÂY DỰNG GIAO DIỆN BẰNG CODE
        /// </summary>
        void BuildUI()
        {
            this.Text = "UDP Airdrop Mini - Client";
            this.Width = 700;
            this.Height = 520;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.MinimumSize = new System.Drawing.Size(700, 520);
            this.AutoScaleMode = AutoScaleMode.Font;

            int y = 10;

            // Dòng 1: Server, MyId, Connect
            Label lbl = new Label() { Text = "Server IP:", Left = 10, Top = y + 4, AutoSize = true };
            this.Controls.Add(lbl);
            txtServerIp = new TextBox() { Left = 80, Top = y, Width = 120, Text = "127.0.0.1" };
            this.Controls.Add(txtServerIp);

            Label l2 = new Label() { Text = "MyId:", Left = 210, Top = y + 4, AutoSize = true };
            this.Controls.Add(l2);
            txtMyId = new TextBox() { Left = 250, Top = y, Width = 120 };
            this.Controls.Add(txtMyId);

            btnConnect = new Button() { Text = "Connect", Left = 380, Top = y - 1, Width = 90, Height = 25 };
            btnConnect.Click += btnConnect_Click;
            this.Controls.Add(btnConnect);

            y += 40; // y = 50

            // --- GroupBox Gửi File ---
            grpSend = new GroupBox()
            {
                Text = "Send File",
                Left = 10,
                Top = y,
                Width = 665,
                Height = 110,
                Enabled = false
            };
            this.Controls.Add(grpSend);

            int gy = 20; // Tọa độ Y bên trong GroupBox

            // Dòng 2: TargetId, Browse, FilePath (bên trong GroupBox)
            Label l3 = new Label() { Text = "TargetId:", Left = 10, Top = gy + 4, AutoSize = true };
            grpSend.Controls.Add(l3);
            txtTargetId = new TextBox() { Left = 70, Top = gy, Width = 120 };
            grpSend.Controls.Add(txtTargetId);

            btnBrowse = new Button() { Text = "Browse...", Left = 200, Top = gy - 1, Width = 90, Height = 25 };
            btnBrowse.Click += btnBrowse_Click;
            grpSend.Controls.Add(btnBrowse);

            txtFilePath = new TextBox() { Left = 300, Top = gy, Width = 350, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            grpSend.Controls.Add(txtFilePath);

            gy += 40; // gy = 60

            // Dòng 3: Send, Pause, Cancel, Progress (bên trong GroupBox)
            btnSendFile = new Button() { Text = "Send File", Left = 10, Top = gy, Width = 100, Height = 25 };
            btnSendFile.Click += btnSendFile_Click;
            grpSend.Controls.Add(btnSendFile);

            btnTamDung = new Button() { Text = "Tạm dừng", Left = 120, Top = gy, Width = 100, Height = 25, Enabled = false };
            btnTamDung.Click += btnTamDung_Click;
            grpSend.Controls.Add(btnTamDung);

            btnHuy = new Button() { Text = "Hủy", Left = 230, Top = gy, Width = 100, Height = 25, Enabled = false };
            btnHuy.Click += btnHuy_Click;
            grpSend.Controls.Add(btnHuy);

            progressBar = new ProgressBar() { Left = 340, Top = gy, Width = 270, Height = 23, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            grpSend.Controls.Add(progressBar);

            lblPercent = new Label() { Text = "0%", Left = 615, Top = gy + 4, Width = 45, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            grpSend.Controls.Add(lblPercent);

            y += 120; // y = 170

            // Dòng 4: Log
            Label lLog = new Label() { Text = "Log / Received files:", Left = 10, Top = y, AutoSize = true };
            this.Controls.Add(lLog);

            y += 20; // y = 190

            lstLog = new ListBox()
            {
                Left = 10,
                Top = y,
                Width = 665,
                Height = 270,
                HorizontalScrollbar = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };
            this.Controls.Add(lstLog);
        }

        // --- 4. TOÀN BỘ CODE LOGIC ---

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            try
            {
                _cts?.Cancel();
                tcpClient?.Close();
                udpPeer?.Close();
            }
            catch { }
        }

        private void UI(Action action)
        {
            if (this.InvokeRequired)
                this.BeginInvoke(action);
            else
                action();
        }

        private void Log(string msg)
        {
            UI(() =>
            {
                string line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
                lstLog.Items.Add(line);
                if (lstLog.Items.Count > 1000) lstLog.Items.RemoveAt(0);
                lstLog.TopIndex = lstLog.Items.Count - 1;
            });
        }

        private async void btnConnect_Click(object sender, EventArgs e)
        {
            if (tcpClient != null || udpPeer != null)
            {
                Log("Already connected. Disconnecting first...");
                Disconnect();
                await Task.Delay(500);
            }

            string serverIp = txtServerIp.Text.Trim();
            string myId = txtMyId.Text.Trim();
            if (string.IsNullOrEmpty(serverIp) || string.IsNullOrEmpty(myId))
            {
                MessageBox.Show("Server IP and MyId cannot be empty.");
                return;
            }

            try
            {
                introEndpoint = new IPEndPoint(IPAddress.Parse(serverIp), 22333);

                udpPeer = new UdpClient();
                Log($"UDP peer socket bound to {udpPeer.Client.LocalEndPoint}");

                tcpClient = new TcpClient();
                await tcpClient.ConnectAsync(serverIp, 22334);
                Log($"TCP connected to {tcpClient.Client.RemoteEndPoint}");

                _cts = new CancellationTokenSource();

                string hello = $"HELLO;{myId}";
                var helloB = Encoding.UTF8.GetBytes(hello);
                await udpPeer.SendAsync(helloB, helloB.Length, introEndpoint);
                Log($"Sent HELLO to introducer {introEndpoint}");

                udpReceiveTask = Task.Run(() => UdpReceiveLoop(_cts.Token), _cts.Token);

                keepAliveTask = Task.Run(async () =>
                {
                    try
                    {
                        using (var stream = tcpClient.GetStream())
                        using (var sw = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true })
                        {
                            await sw.WriteLineAsync($"REGISTER;{myId}");
                            Log($"TCP connected & registered as '{myId}'");

                            while (!_cts.Token.IsCancellationRequested)
                            {
                                await Task.Delay(30000, _cts.Token);
                                await sw.WriteLineAsync($"KEEPALIVE;{myId}");
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        Log("KeepAlive task error: " + ex.Message);
                    }
                }, _cts.Token);

                btnConnect.Text = "Disconnect";
                grpSend.Enabled = true;
            }
            catch (Exception ex)
            {
                Log($"Connection failed: {ex.Message}");
                Disconnect();
            }
        }

        private void Disconnect()
        {
            try
            {
                _cts?.Cancel();
                tcpClient?.Close();
                tcpClient = null;
                udpPeer?.Close();
                udpPeer = null;
                _peerWaiters.Clear();
                ackWaiters.Clear();
            }
            catch { }

            Log("Disconnected");
            btnConnect.Text = "Connect";
            grpSend.Enabled = false;
            ResetSendUi();
        }

        private void btnBrowse_Click(object sender, EventArgs e)
        {
            using (var ofd = new OpenFileDialog())
            {
                ofd.Filter = "All files (*.*)|*.*";
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    txtFilePath.Text = ofd.FileName;
                }
            }
        }

        private void btnSendFile_Click(object sender, EventArgs e)
        {
            string targetId = txtTargetId.Text.Trim();
            string filePath = txtFilePath.Text.Trim();

            if (string.IsNullOrEmpty(targetId) || string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                MessageBox.Show("Target ID and a valid File Path are required.");
                return;
            }

            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            pauseEvent.Set();

            btnSendFile.Enabled = false;
            btnTamDung.Enabled = true;
            btnHuy.Enabled = true;
            btnTamDung.Text = "Tạm dừng";
            progressBar.Value = 0;
            lblPercent.Text = "0%";

            Task.Run(() => SendFileP2PWorkflow(targetId, filePath, _cts.Token), _cts.Token);
        }

        private void btnTamDung_Click(object sender, EventArgs e)
        {
            if (pauseEvent.IsSet)
            {
                pauseEvent.Reset();
                btnTamDung.Text = "Tiếp tục";
                Log("Send Paused");
            }
            else
            {
                pauseEvent.Set();
                btnTamDung.Text = "Tạm dừng";
                Log("Send Resumed");
            }
        }

        private void btnHuy_Click(object sender, EventArgs e)
        {
            _cts?.Cancel();
            Log("Send Cancelled");
        }

        private void ResetSendUi()
        {
            UI(() =>
            {
                btnSendFile.Enabled = true;
                btnTamDung.Enabled = false;
                btnHuy.Enabled = false;
                btnTamDung.Text = "Tạm dừng";
                progressBar.Value = 0;
                lblPercent.Text = "0%";
            });
            pauseEvent.Set();
        }


        private async Task UdpReceiveLoop(CancellationToken token)
        {
            Log("UDP receive loop started");
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var result = await udpPeer.ReceiveAsync().ConfigureAwait(false);
                    string msgText = "";
                    byte[] buffer = result.Buffer;
                    int headerLength = -1;

                    // Tách header cho tin nhắn DATA
                    if (buffer.Length > 4 && buffer[0] == 'D' && buffer[1] == 'A') // "DATA"
                    {
                        for (int i = 0; i < buffer.Length; i++)
                        {
                            if (buffer[i] == ';')
                            {
                                for (int j = i + 1; j < buffer.Length; j++)
                                {
                                    if (buffer[j] == ';')
                                    {
                                        for (int k = j + 1; k < buffer.Length; k++)
                                        {
                                            if (buffer[k] == ';')
                                            {
                                                headerLength = k + 1;
                                                msgText = Encoding.UTF8.GetString(buffer, 0, headerLength);
                                                break;
                                            }
                                        }
                                        break;
                                    }
                                }
                                break;
                            }
                        }
                        if (headerLength == -1) msgText = "DATA;[malformed]";
                    }
                    else
                    {
                        msgText = Encoding.UTF8.GetString(buffer);
                    }

                    if (string.IsNullOrEmpty(msgText)) continue;

                    string[] parts = msgText.Split(';');
                    string cmd = parts[0].ToUpperInvariant();

                    if (cmd != "DATA")
                    {
                        Log($"Recv UDP from {result.RemoteEndPoint}: {msgText}");
                    }


                    switch (cmd)
                    {
                        case "PEER":
                            if (parts.Length >= 4)
                            {
                                string peerName = parts[1];
                                if (_peerWaiters.TryRemove(peerName, out var tcs))
                                {
                                    try
                                    {
                                        var peerEp = new IPEndPoint(IPAddress.Parse(parts[2]), int.Parse(parts[3]));
                                        tcs.TrySetResult(peerEp);
                                    }
                                    catch (Exception ex) { tcs.TrySetException(ex); }
                                }
                            }
                            break;

                        case "ACK":
                            if (parts.Length >= 3)
                            {
                                string ackKey = $"{parts[1]}:{parts[2]}";
                                if (ackWaiters.TryRemove(ackKey, out var tcs))
                                {
                                    tcs.TrySetResult(0);
                                }
                            }
                            break;

                        case "START":
                            if (parts.Length >= 5)
                            {
                                try
                                {
                                    string transferId = parts[1];
                                    string fileName = Path.GetFileName(parts[2]);
                                    long fileSize = long.Parse(parts[3]);
                                    string fromId = parts[4];

                                    if (_receivingTransfers.TryRemove(transferId, out var oldState))
                                    {
                                        oldState.FileStream?.Close();
                                        Log($"Removed old transfer {transferId}");
                                    }

                                    string savePath = Path.Combine(
                                        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                                        $"[Airdrop] {fileName}");

                                    var newState = new ReceivingFileState
                                    {
                                        TransferId = transferId,
                                        FileName = savePath,
                                        TotalSize = fileSize,
                                        ReceivedBytes = 0,
                                        ExpectedSeq = 0,
                                        SenderEndPoint = result.RemoteEndPoint,
                                        FileStream = new FileStream(savePath, FileMode.Create, FileAccess.Write)
                                    };

                                    if (_receivingTransfers.TryAdd(transferId, newState))
                                    {
                                        Log($"Receiving file '{fileName}' ({fileSize} bytes) from {fromId} @ {result.RemoteEndPoint}. Saving to {savePath}");
                                        // **FIX 2 (ĐÃ XÓA):** Không còn khóa UI
                                        // UI(() => grpSend.Enabled = false); 
                                    }
                                    else
                                    {
                                        newState.FileStream.Close();
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Log($"Failed to process START: {ex.Message}");
                                }
                            }
                            break;

                        case "DATA":
                            if (parts.Length >= 4 && headerLength != -1)
                            {
                                string transferId = parts[1];
                                int seq = int.Parse(parts[2]);

                                if (_receivingTransfers.TryGetValue(transferId, out var state))
                                {
                                    string ack = $"ACK;{transferId};{seq}";
                                    byte[] ackB = Encoding.UTF8.GetBytes(ack);
                                    await udpPeer.SendAsync(ackB, ackB.Length, result.RemoteEndPoint).ConfigureAwait(false);

                                    if (seq == state.ExpectedSeq)
                                    {
                                        int dataLength = buffer.Length - headerLength;
                                        state.FileStream.Write(buffer, headerLength, dataLength);

                                        // **FIX 3 (ĐÃ THÊM):** Khắc phục lỗi file rỗng
                                        state.FileStream.Flush(); // Ghi (flush) dữ liệu xuống đĩa ngay lập tức

                                        state.ReceivedBytes += dataLength;
                                        state.ExpectedSeq++;

                                        if (state.ReceivedBytes >= state.TotalSize && state.TotalSize > 0)
                                        {
                                            state.FileStream.Close();
                                            _receivingTransfers.TryRemove(transferId, out _);
                                            Log($"File receive complete: {state.FileName}");

                                            // **FIX 2 (ĐÃ XÓA):** Không còn khóa UI
                                            // UI(() => grpSend.Enabled = true);

                                            UI(() => MessageBox.Show($"File '{state.FileName}' received and saved to Desktop.", "Transfer Complete", MessageBoxButtons.OK, MessageBoxIcon.Information));
                                        }
                                    }
                                }
                            }
                            break;
                    }
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { break; }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Log($"UdpReceiveLoop error: {ex.Message}");
                    await Task.Delay(1000);
                }
            }
            Log("UDP receive loop stopped");

            foreach (var state in _receivingTransfers.Values)
            {
                state.FileStream?.Close();
            }
            _receivingTransfers.Clear();
        }


        // --- HÀM GỬI FILE ---
        private async Task SendFileP2PWorkflow(string targetId, string filePath, CancellationToken token)
        {
            string myId = txtMyId.Text.Trim();
            string transferId = Guid.NewGuid().ToString("N");
            try
            {
                // **FIX 1 (ĐÃ SỬA):** Khắc phục lỗi "No PEER" khi gửi nhiều lần
                var peerTcs = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
                _peerWaiters[targetId] = peerTcs;

                Log($"Waiting briefly (200ms) to ensure server processed HELLO...");
                await Task.Delay(200, token);

                var punchMsg = Encoding.UTF8.GetBytes($"PUNCH;{targetId};{myId}");
                await udpPeer.SendAsync(punchMsg, punchMsg.Length, introEndpoint).ConfigureAwait(false);
                Log($"PUNCH request sent to introducer for target '{targetId}'");

                var completed = await Task.WhenAny(peerTcs.Task, Task.Delay(TimeSpan.FromSeconds(10), token)).ConfigureAwait(false);
                if (completed != peerTcs.Task || peerTcs.Task.IsCanceled || peerTcs.Task.IsFaulted)
                {
                    _peerWaiters.TryRemove(targetId, out _);
                    Log("No PEER info received from introducer. Aborting.");
                    ResetSendUi();
                    return;
                }

                var peerEp = peerTcs.Task.Result;
                Log($"PEER obtained: {peerEp}");

                Log($"Hole punching {peerEp} ...");
                for (int i = 0; i < 5; i++)
                {
                    await udpPeer.SendAsync(new byte[] { 0 }, 1, peerEp).ConfigureAwait(false);
                    await Task.Delay(100).ConfigureAwait(false);
                }

                FileInfo fi = new FileInfo(filePath);
                string fileName = fi.Name;
                long fileSize = fi.Length;
                string start = $"START;{transferId};{fileName};{fileSize};{myId}";
                var startB = Encoding.UTF8.GetBytes(start);
                for (int i = 0; i < 3; i++)
                {
                    await udpPeer.SendAsync(startB, startB.Length, peerEp).ConfigureAwait(false);
                    await Task.Delay(100).ConfigureAwait(false);
                }
                Log($"START sent for {fileName} ({fileSize}) transferId={transferId}");

                int chunkSize = 1200; // Bạn có thể tăng giá trị này (ví dụ: 32000)
                long sent = 0;
                int seq = 0;
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                {
                    byte[] buffer = new byte[chunkSize];
                    while (sent < fileSize)
                    {
                        token.ThrowIfCancellationRequested();
                        pauseEvent.Wait(token);

                        int toRead = fs.Read(buffer, 0, buffer.Length);
                        if (toRead <= 0) break;

                        string hdr = $"DATA;{transferId};{seq};";
                        byte[] hdrB = Encoding.UTF8.GetBytes(hdr);
                        byte[] datagram = new byte[hdrB.Length + toRead];
                        Buffer.BlockCopy(hdrB, 0, datagram, 0, hdrB.Length);
                        Buffer.BlockCopy(buffer, 0, datagram, hdrB.Length, toRead);

                        var tcs = new TaskCompletionSource<int>();
                        ackWaiters.TryAdd($"{transferId}:{seq}", tcs);

                        int retries = 0;
                        bool acked = false;
                        while (retries < 8 && !acked)
                        {
                            await udpPeer.SendAsync(datagram, datagram.Length, peerEp).ConfigureAwait(false);
                            var delayTask = Task.Delay(3000, token);
                            var completedTask = await Task.WhenAny(tcs.Task, delayTask).ConfigureAwait(false);
                            if (completedTask == tcs.Task)
                            {
                                acked = true;
                                break;
                            }
                            retries++;
                            Log($"No ACK for seq={seq} retry {retries}");
                        }

                        ackWaiters.TryRemove($"{transferId}:{seq}", out _);

                        if (!acked)
                        {
                            throw new IOException($"Failed to receive ACK for seq={seq}. Aborting.");
                        }

                        sent += toRead;
                        seq++;

                        // --- CẬP NHẬT THANH TIẾN TRÌNH % ---
                        int percent = fileSize == 0 ? 100 : (int)(sent * 100L / fileSize);
                        percent = Math.Min(Math.Max(percent, 0), 100);
                        UI(() =>
                        {
                            progressBar.Value = percent;
                            lblPercent.Text = $"{percent}%";
                        });
                        // ------------------------------------
                    }
                }

                Log($"Send complete: {fileName} ({fileSize} bytes) to {targetId} via {peerEp}");
            }
            catch (OperationCanceledException)
            {
                Log("Send cancelled by user");
            }
            catch (Exception ex)
            {
                Log("SendFileP2PWorkflow error: " + ex.Message);
            }
            finally
            {
                ResetSendUi();
            }
        }
    }
}