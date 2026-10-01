using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;


namespace server
{
    public partial class Form1 : Form
    {
        private const int PORT = 22333; // UDP Port
        private const int TCP_PORT = 22334; // TCP Port
        private const int CLIENT_TTL_SECONDS = 120;

        private readonly object _lock = new object();
        private readonly Dictionary<string, ClientInfo> _clients = new Dictionary<string, ClientInfo>(StringComparer.OrdinalIgnoreCase);

        private UdpClient _udp;
        private CancellationTokenSource _cts;
        private Task _serverTask;
        private Task _cleanupTask;
        private System.Net.Sockets.TcpListener _tcpListener;
        private CancellationTokenSource _tcpCts;
        public Form1()
        {
            InitializeComponent();

            this.Load += Form1_Load;
            this.FormClosing += Form1_FormClosing;
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            StartServer();
            StartTcpListener();
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            StopServer();
            StopTcpListener();
        }

        private void StartServer()
        {
            _cts = new CancellationTokenSource();

            _serverTask = Task.Run(() =>
            {
                try
                {
                    _udp = new UdpClient(PORT);
                    Log($"Server listening on UDP port {PORT}");

                    var remote = new IPEndPoint(IPAddress.Any, 0);

                    while (!_cts.IsCancellationRequested)
                    {
                        byte[] req;
                        try
                        {
                            req = _udp.Receive(ref remote); // blocking
                        }
                        catch (ObjectDisposedException) { break; }
                        catch (SocketException) { break; }
                        catch (Exception ex)
                        {
                            Log($"Receive error: {ex.Message}");
                            continue;
                        }

                        string raw = Encoding.UTF8.GetString(req);
                        Log($"Recv from {remote} raw=\"{raw}\"");

                        string resp;
                        try
                        {
                            resp = HandleMessage(raw, remote);
                        }
                        catch (Exception ex)
                        {
                            Log($"HandleMessage error: {ex.Message}");
                            resp = "ERR;Internal";
                        }

                        // Chỉ gửi phản hồi nếu HandleMessage trả về một chuỗi
                        // (HandlePunchRequest sẽ trả về null)
                        if (!string.IsNullOrEmpty(resp))
                        {
                            try
                            {
                                byte[] outBytes = Encoding.UTF8.GetBytes(resp);
                                _udp.Send(outBytes, outBytes.Length, remote);
                                Log($"Sent to {remote} -> \"{resp}\"");
                            }
                            catch (Exception ex)
                            {
                                Log($"Send error to {remote}: {ex.Message}");
                            }
                        }
                    }
                }
                finally
                {
                    try { _udp?.Close(); }
                    catch { }
                }
            }, _cts.Token);

            // Tác vụ dọn dẹp client cũ
            _cleanupTask = Task.Run(() =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    Thread.Sleep(30 * 1000);
                    DateTime now = DateTime.UtcNow;
                    var removed = new List<string>();
                    lock (_lock)
                    {
                        foreach (var kv in _clients)
                        {
                            if ((now - kv.Value.LastSeen).TotalSeconds > CLIENT_TTL_SECONDS)
                                removed.Add(kv.Key);
                        }
                        foreach (var k in removed)
                        {
                            _clients.Remove(k);
                            Log($"Removed stale client '{k}'");
                        }
                    }
                    if (removed.Count > 0) UpdateClientsList();
                }
            }, _cts.Token);
        }

        private void StopServer()
        {
            try
            {
                _cts?.Cancel();
                try { _udp?.Close(); } catch { }
                _serverTask?.Wait(1000);
                _cleanupTask?.Wait(500);
            }
            catch { /* ignore */ }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                _udp = null;
            }
        }

        // --- Xử lý tin nhắn UDP ---
        private string HandleMessage(string msg, IPEndPoint sender)
        {
            if (string.IsNullOrWhiteSpace(msg)) return "ERR;Empty";

            string s = msg.Trim();
            string[] parts = s.Split(new[] { ';' }, 3);
            string cmd = parts[0].ToUpperInvariant();

            switch (cmd)
            {
                case "HELLO": // Client gửi HELLO qua UDP để đăng ký EndPoint
                    if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[1])) return "ERR;NameMissing";
                    RegisterClient(parts[1].Trim(), sender);
                    return "OK";

                case "KEEPALIVE": // Client cũ có thể gửi KEEPALIVE qua UDP
                    if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[1])) return "ERR;NameMissing";
                    UpdateClient(parts[1].Trim(), sender);
                    return "OK";

                case "LIST":
                    return GetClientList();

                case "PUNCH":
                    {
                        string target = parts.Length >= 2 ? parts[1].Trim() : "";
                        string fromName = parts.Length == 3 ? parts[2].Trim() : "";

                        if (string.IsNullOrWhiteSpace(target)) return "ERR;TargetMissing";
                        if (string.IsNullOrWhiteSpace(fromName)) return "ERR;FromMissing";

                        // Gọi hàm PUNCH (hàm này sẽ tự gửi và trả về null)
                        return HandlePunchRequest(target, fromName, sender);
                    }

                case "DIR":
                    string path = s.Length > 4 ? s.Substring(4) : "";
                    return GetDirInfo(path);

                default:
                    return "ERR;UnknownCommand";
            }
        }

        private void RegisterClient(string name, IPEndPoint ep)
        {
            lock (_lock)
            {
                if (_clients.TryGetValue(name, out var info))
                {
                    info.EndPoint = ep;
                    info.LastSeen = DateTime.UtcNow;
                    Log($"Updated client '{name}' -> {ep}");
                }
                else
                {
                    _clients[name] = new ClientInfo { Name = name, EndPoint = ep, LastSeen = DateTime.UtcNow };
                    Log($"Registered client '{name}' -> {ep}");
                }

                // Xóa các entry trùng lặp (tùy chọn)
                var dup = new List<string>();
                foreach (var kv in _clients)
                {
                    if (!kv.Key.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                        kv.Value.EndPoint.Equals(ep))
                        dup.Add(kv.Key);
                }
                foreach (var k in dup) _clients.Remove(k);
            }

            UpdateClientsList();
        }

        private void UpdateClient(string name, IPEndPoint ep)
        {
            lock (_lock)
            {
                if (_clients.TryGetValue(name, out var info))
                {
                    // Chỉ log nếu EndPoint thay đổi (tránh spam log)
                    if (!info.EndPoint.Equals(ep))
                    {
                        Log($"UDP Keepalive (EP Changed) '{name}': {info.EndPoint} -> {ep}");
                        info.EndPoint = ep;
                    }
                    info.LastSeen = DateTime.UtcNow;
                }
                else
                {
                    RegisterClient(name, ep);
                }
            }
            UpdateClientsList();
        }

        private string FindNameByEndpoint(IPEndPoint ep)
        {
            lock (_lock)
            {
                foreach (var kv in _clients)
                {
                    if (kv.Value.EndPoint.Equals(ep)) return kv.Key;
                }
            }
            return null;
        }

        private string GetClientList()
        {
            lock (_lock)
            {
                var sb = new StringBuilder();
                sb.Append("LIST");
                foreach (var kv in _clients)
                {
                    sb.Append(';').Append(kv.Key);
                }
                sb.Append('#');
                return sb.ToString();
            }
        }

        // --- HÀM PUNCH ĐÃ SỬA LỖI ---
        private string HandlePunchRequest(string targetName, string fromName, IPEndPoint requesterEp)
        {
            ClientInfo target = null;
            ClientInfo from = null;
            lock (_lock)
            {
                // ---- SỬA LỖI QUAN TRỌNG (Stale State) ----
                // Cập nhật EndPoint của người yêu cầu (requester) ngay lập tức
                if (_clients.TryGetValue(fromName, out from))
                {
                    from.EndPoint = requesterEp;
                    from.LastSeen = DateTime.UtcNow;
                }
                // ---- KẾT THÚC SỬA LỖI ----

                _clients.TryGetValue(targetName, out target);
            }

            if (target == null)
            {
                Log($"PUNCH: target '{targetName}' not found (from '{fromName}')");
                return "ERR;TargetNotFound";
            }

            Log($"PUNCH request: '{fromName}' ({requesterEp}) -> '{targetName}' ({target.EndPoint})");

            try
            {
                // Gửi thông tin PEER cho người yêu cầu
                string toRequester = $"PEER;{target.Name};{target.EndPoint.Address};{target.EndPoint.Port}";
                var b1 = Encoding.UTF8.GetBytes(toRequester);
                _udp.Send(b1, b1.Length, requesterEp);
                Log($"Sent to {requesterEp} (Requester) -> \"{toRequester}\"");

                // Gửi thông tin PEER cho mục tiêu
                string advertisedFromName = from != null ? from.Name : fromName;
                string toTarget = $"PEER;{advertisedFromName};{requesterEp.Address};{requesterEp.Port}";
                var b2 = Encoding.UTF8.GetBytes(toTarget);
                _udp.Send(b2, b2.Length, target.EndPoint);
                Log($"Sent to {target.EndPoint} (Target) -> \"{toTarget}\"");


                Log($"Introduced {fromName} <-> {targetName}");

                // ---- SỬA LỖI QUAN TRỌNG (Response) ----
                // Trả về null để vòng lặp chính không gửi thêm tin "OK"
                return null;
            }
            catch (Exception ex)
            {
                Log($"Error during punch: {ex.Message}");
                return "ERR;Internal";
            }
        }

        // -- Tiện ích thư mục --
        private static string NormalizePath(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";
            string p = input.Trim().Trim('"', '\'').Replace('/', '\\');
            try { p = Environment.ExpandEnvironmentVariables(p); } catch { }
            try { p = Path.GetFullPath(p); } catch { return ""; }
            return p.TrimEnd();
        }

        private static string GetDirInfo(string path)
        {
            string normalized = NormalizePath(path);
            if (string.IsNullOrWhiteSpace(normalized) || !Directory.Exists(normalized))
            {
                return "ERR;InvalidPath";
            }
            var dir = new DirectoryInfo(normalized);
            DirectoryInfo[] dirs;
            FileInfo[] files;
            try { dirs = dir.GetDirectories(); } catch { dirs = Array.Empty<DirectoryInfo>(); }
            try { files = dir.GetFiles(); } catch { files = Array.Empty<FileInfo>(); }
            int n = dirs.Length + files.Length;
            var sb = new StringBuilder();
            sb.Append('$').Append(n);
            foreach (var d in dirs)
                sb.Append(';').Append(Uri.EscapeDataString(d.Name)).Append(";0");
            foreach (var f in files)
                sb.Append(';').Append(Uri.EscapeDataString(f.Name)).Append(";1");
            sb.Append('#');
            return sb.ToString();
        }

        // -- UI helpers --
        private void Log(string text)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {text}";
            if (lstLog.InvokeRequired)
            {
                lstLog.BeginInvoke(new Action(() =>
                {
                    lstLog.Items.Add(line);
                    TrimListBox(lstLog);
                    lstLog.TopIndex = lstLog.Items.Count - 1;
                }));
            }
            else
            {
                lstLog.Items.Add(line);
                TrimListBox(lstLog);
                lstLog.TopIndex = lstLog.Items.Count - 1;
            }
        }

        private void UpdateClientsList()
        {
            if (lstClients.InvokeRequired)
            {
                lstClients.BeginInvoke(new Action(() =>
                {
                    lstClients.Items.Clear();
                    lock (_lock)
                    {
                        foreach (var kv in _clients)
                            lstClients.Items.Add($"{kv.Key} -> {kv.Value.EndPoint}");
                    }
                }));
            }
            else
            {
                lstClients.Items.Clear();
                lock (_lock)
                {
                    foreach (var kv in _clients)
                        lstClients.Items.Add($"{kv.Key} -> {kv.Value.EndPoint}");
                }
            }
        }

        private void TrimListBox(ListBox lb, int max = 2000)
        {
            while (lb.Items.Count > max) lb.Items.RemoveAt(0);
        }

        // --- TCP Listener ---
        private void StartTcpListener()
        {
            try
            {
                _tcpCts = new CancellationTokenSource();
                _tcpListener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, TCP_PORT);
                _tcpListener.Start();
                Log($"TCP listener started on port {TCP_PORT}");

                Task.Run(async () =>
                {
                    while (!_tcpCts.IsCancellationRequested)
                    {
                        try
                        {
                            var client = await _tcpListener.AcceptTcpClientAsync().ConfigureAwait(false);
                            Log($"TCP client connected from {client.Client.RemoteEndPoint}");
                            // Bắt đầu một tác vụ để đọc KEEPALIVE từ client
                            _ = Task.Run(() => HandleTcpClient(client));
                        }
                        catch (ObjectDisposedException) { break; }
                        catch (Exception ex) { Log("TCP accept error: " + ex.Message); }
                    }
                }, _tcpCts.Token);
            }
            catch (Exception ex)
            {
                Log("StartTcpListener error: " + ex.Message);
            }
        }

        private async Task HandleTcpClient(TcpClient client)
        {
            string clientName = "unknown";
            try
            {
                using (client)
                using (var ns = client.GetStream())
                using (var sr = new StreamReader(ns, Encoding.UTF8))
                {
                    while (client.Connected)
                    {
                        string line = await sr.ReadLineAsync().ConfigureAwait(false);
                        if (line == null) break;
                        Log("TCP RX: " + line);

                        // Xử lý REGISTER và KEEPALIVE từ Client
                        if (line.StartsWith("REGISTER;"))
                        {
                            clientName = line.Split(';')[1];
                            Log($"TCP client registered as '{clientName}'");
                        }
                        else if (line.StartsWith("KEEPALIVE;"))
                        {
                            // Client đã được đăng ký, chỉ cần log
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_tcpCts.IsCancellationRequested)
                    Log("HandleTcpClient error: " + ex.Message);
            }
            finally
            {
                Log($"TCP client '{clientName}' disconnected");
            }
        }

        private void StopTcpListener()
        {
            try
            {
                _tcpCts?.Cancel();
                _tcpListener?.Stop();
            }
            catch { }
        }

        // -- Class lưu trữ thông tin Client --
        private class ClientInfo
        {
            public string Name;
            public IPEndPoint EndPoint; // Đây là EndPoint UDP
            public DateTime LastSeen;
        }
    }
}