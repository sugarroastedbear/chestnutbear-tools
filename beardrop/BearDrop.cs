// BearDrop — 局域网手机↔电脑传文件（手机免装 App，扫码即用）
// 单文件 C#，必须保持 C# 5 兼容（不用字符串内插、?.、nameof、=> 成员、不用 async/await）。
// 界面文字一律用 L.T("中文", "English")。
// 架构：本机 HTTP 服务（TcpListener 手写），界面是它 serve 的网页（web/app.html + web/qrcode.js，内嵌为资源）。
//   电脑端：程序启动后用默认浏览器打开 http://127.0.0.1:<port>/ （只对本机开放），页面显示二维码。
//   手机端：扫码打开 http://<局域网IP>:<port>/m?k=<token> ，同一套响应式页面传文件。
//   上传：手机端一个文件一个 POST /up?k=token，文件名在 X-Filename（URL 编码），body 就是原始字节 —— C# 无需解析 multipart。
//   下载：电脑端用「添加文件」把磁盘文件加入分享列表，手机 GET /list、/dl/<id> 下载。
// 只做文件传输，不执行任何命令。只在局域网、普通 HTTP、随机一次性 token。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace BearDrop
{
    static class L
    {
        public static bool Zh;
        public static string T(string zh, string en) { return Zh ? zh : en; }
        static string Dir() { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BearDrop"); }
        static string CfgPath() { return Path.Combine(Dir(), "lang.txt"); }
        public static void Load()
        {
            try { string p = CfgPath(); if (File.Exists(p)) { Zh = File.ReadAllText(p).Trim() == "zh"; return; } }
            catch { }
            try { Zh = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh"; } catch { Zh = false; }
        }
        public static void Save()
        {
            try { Directory.CreateDirectory(Dir()); File.WriteAllText(CfgPath(), Zh ? "zh" : "en"); } catch { }
        }
    }

    class SharedFile
    {
        public string Id;
        public string Name;
        public string Path;
        public long Size;
    }

    class Server
    {
        public const int FirstPort = 18650, LastPort = 18659;
        TcpListener listener;
        volatile bool running;
        public int Port { get; private set; }
        public string Token { get; private set; }
        public string ReceivedDir;
        public bool Running { get { return running; } }

        readonly List<SharedFile> shared = new List<SharedFile>();
        readonly object gate = new object();

        byte[] appHtml, qrJs;

        public Server()
        {
            Token = MakeToken(12);
            appHtml = ReadResource("app.html");
            qrJs = ReadResource("qrcode.js");
            ReceivedDir = DefaultReceivedDir();
        }

        static string DefaultReceivedDir()
        {
            string baseDir;
            try
            {
                string prof = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string dl = Path.Combine(prof, "Downloads");
                baseDir = Directory.Exists(dl) ? dl : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            }
            catch { baseDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); }
            string d = Path.Combine(baseDir, "BearDrop");
            try { Directory.CreateDirectory(d); } catch { }
            return d;
        }

        static byte[] ReadResource(string name)
        {
            var asm = Assembly.GetExecutingAssembly();
            using (var s = asm.GetManifestResourceStream(name))
            {
                if (s == null) return new byte[0];
                using (var ms = new MemoryStream()) { s.CopyTo(ms); return ms.ToArray(); }
            }
        }

        static string MakeToken(int n)
        {
            const string al = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            var rnd = new Random(unchecked(Environment.TickCount * 31 + Guid.NewGuid().GetHashCode()));
            var sb = new StringBuilder();
            for (int i = 0; i < n; i++) sb.Append(al[rnd.Next(al.Length)]);
            return sb.ToString();
        }

        public void Start()
        {
            if (running) return;
            for (int p = FirstPort; p <= LastPort && listener == null; p++)
            {
                try { var l = new TcpListener(IPAddress.Any, p); l.Start(); listener = l; Port = p; }
                catch (SocketException) { }
            }
            if (listener == null)
            {
                listener = new TcpListener(IPAddress.Any, 0);
                listener.Start();
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            running = true;
            var l2 = listener;
            new Thread(delegate () { AcceptLoop(l2); }) { IsBackground = true, Name = "BearDrop" }.Start();
        }

        public void Stop()
        {
            running = false;
            if (listener != null) { try { listener.Stop(); } catch { } listener = null; }
        }

        void AcceptLoop(TcpListener l)
        {
            while (running)
            {
                TcpClient c;
                try { c = l.AcceptTcpClient(); }
                catch { break; }
                var cc = c;
                new Thread(delegate () { try { Handle(cc); } catch { } }) { IsBackground = true }.Start();
            }
        }

        // ---- 分享列表 ----
        public void AddShared(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return;
                lock (gate)
                {
                    foreach (var s in shared) if (string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase)) return;
                    shared.Add(new SharedFile { Id = MakeToken(10), Name = fi.Name, Path = path, Size = fi.Length });
                }
            }
            catch { }
        }
        public void ClearShared() { lock (gate) { shared.Clear(); } }
        public List<SharedFile> SharedSnapshot() { lock (gate) { return new List<SharedFile>(shared); } }

        // ---- 本机局域网 IP ----
        public static List<string> LocalIPs()
        {
            var pref = new List<string>();
            var other = new List<string>();
            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                foreach (var a in host.AddressList)
                {
                    if (a.AddressFamily != AddressFamily.InterNetwork) continue;
                    string s = a.ToString();
                    if (s.StartsWith("127.") || s.StartsWith("169.254.")) continue;
                    if (s.StartsWith("192.168.") || s.StartsWith("10.") || s.StartsWith("172.")) pref.Add(s);
                    else other.Add(s);
                }
            }
            catch { }
            pref.AddRange(other);
            if (pref.Count == 0) pref.Add("127.0.0.1");
            return pref;
        }
        public string BestHost() { return LocalIPs()[0]; }
        public string MobileUrl() { return "http://" + BestHost() + ":" + Port + "/m?k=" + Token; }

        // ---- HTTP ----
        void Handle(TcpClient client)
        {
            client.ReceiveTimeout = 120000; client.SendTimeout = 600000;
            using (var ns = client.GetStream())
            using (var bs = new BufferedStream(ns))
            {
                string reqline = ReadLine(bs);
                if (reqline == null) return;
                var parts = reqline.Split(' ');
                if (parts.Length < 2) return;
                string method = parts[0];
                string rawurl = parts[1];
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string line;
                while ((line = ReadLine(bs)) != null && line.Length > 0)
                {
                    int ci = line.IndexOf(':');
                    if (ci > 0) headers[line.Substring(0, ci).Trim()] = line.Substring(ci + 1).Trim();
                }

                string path = rawurl; string query = "";
                int qi = rawurl.IndexOf('?');
                if (qi >= 0) { path = rawurl.Substring(0, qi); query = rawurl.Substring(qi + 1); }
                var q = ParseQuery(query);

                bool isLocal = IsLoopback(client);

                if (method == "GET" && (path == "/" || path == "/index.html"))
                {
                    if (!isLocal) { WriteStatus(bs, 403, "Forbidden"); return; }
                    ServePage(bs, "host");
                    return;
                }
                if (method == "GET" && path == "/m")
                {
                    if (!CheckToken(q)) { WriteStatus(bs, 403, "Forbidden"); return; }
                    ServePage(bs, "mobile");
                    return;
                }
                if (method == "GET" && path == "/qrcode.js")
                {
                    WriteBytes(bs, 200, "application/javascript; charset=utf-8", qrJs, null);
                    return;
                }
                if (method == "GET" && path == "/list")
                {
                    if (!CheckToken(q)) { WriteStatus(bs, 403, "Forbidden"); return; }
                    WriteBytes(bs, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(ListJson()), null);
                    return;
                }
                if (method == "GET" && path.StartsWith("/dl/"))
                {
                    if (!CheckToken(q)) { WriteStatus(bs, 403, "Forbidden"); return; }
                    ServeDownload(bs, path.Substring(4));
                    return;
                }
                if (method == "POST" && path == "/up")
                {
                    if (!CheckToken(q)) { WriteStatus(bs, 403, "Forbidden"); return; }
                    ReceiveUpload(bs, headers);
                    return;
                }
                WriteStatus(bs, 404, "Not Found");
            }
        }

        bool CheckToken(Dictionary<string, string> q)
        {
            string k; return q.TryGetValue("k", out k) && k == Token;
        }

        static bool IsLoopback(TcpClient c)
        {
            try
            {
                var ep = c.Client.RemoteEndPoint as IPEndPoint;
                if (ep == null) return false;
                return IPAddress.IsLoopback(ep.Address);
            }
            catch { return false; }
        }

        void ServePage(BufferedStream bs, string role)
        {
            string html = Encoding.UTF8.GetString(appHtml);
            string cfg = "<script>window.BD={role:\"" + role + "\",token:\"" + Token + "\",mobileUrl:\"" + MobileUrl() + "\"};</script>";
            html = html.Replace("<!--BD_INJECT-->", cfg);
            WriteBytes(bs, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html), null);
        }

        string ListJson()
        {
            var sb = new StringBuilder();
            sb.Append('[');
            var snap = SharedSnapshot();
            for (int i = 0; i < snap.Count; i++)
            {
                var s = snap[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"id\":\"").Append(s.Id).Append("\",\"name\":").Append(JsonStr(s.Name))
                  .Append(",\"size\":").Append(s.Size).Append('}');
            }
            sb.Append(']');
            return sb.ToString();
        }

        void ServeDownload(BufferedStream bs, string id)
        {
            SharedFile f = null;
            lock (gate) { foreach (var s in shared) if (s.Id == id) { f = s; break; } }
            if (f == null || !File.Exists(f.Path)) { WriteStatus(bs, 404, "Not Found"); return; }
            FileStream fs = null;
            try { fs = new FileStream(f.Path, FileMode.Open, FileAccess.Read, FileShare.Read); }
            catch { WriteStatus(bs, 404, "Not Found"); return; }
            using (fs)
            {
                long len = fs.Length;
                string asciiName = ToAscii(f.Name);
                var extra = new List<string>();
                extra.Add("Content-Disposition: attachment; filename=\"" + asciiName + "\"; filename*=UTF-8''" + Rfc5987(f.Name));
                WriteHead(bs, 200, "application/octet-stream", len, extra);
                var buf = new byte[65536];
                int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0) bs.Write(buf, 0, n);
                bs.Flush();
            }
        }

        void ReceiveUpload(BufferedStream bs, Dictionary<string, string> headers)
        {
            string fn = "file.bin";
            string xf;
            if (headers.TryGetValue("X-Filename", out xf) && xf.Length > 0) fn = UrlDecode(xf);
            fn = SafeName(fn);
            long len = 0;
            string cl;
            if (headers.TryGetValue("Content-Length", out cl)) long.TryParse(cl, out len);

            string dest = UniquePath(Path.Combine(ReceivedDir, fn));
            bool ok = true;
            try
            {
                Directory.CreateDirectory(ReceivedDir);
                using (var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var buf = new byte[65536];
                    long remain = len;
                    while (remain > 0)
                    {
                        int want = (int)Math.Min(remain, buf.Length);
                        int n = bs.Read(buf, 0, want);
                        if (n <= 0) break;
                        fs.Write(buf, 0, n);
                        remain -= n;
                    }
                }
            }
            catch { ok = false; try { File.Delete(dest); } catch { } }
            if (ok && Received != null) Received(dest);
            WriteBytes(bs, ok ? 200 : 500, "application/json; charset=utf-8",
                Encoding.UTF8.GetBytes(ok ? "{\"ok\":true}" : "{\"ok\":false}"), null);
        }

        public event Action<string> Received;

        // ---- 工具 ----
        static string SafeName(string n)
        {
            n = n.Replace("\\", "/");
            int slash = n.LastIndexOf('/');
            if (slash >= 0) n = n.Substring(slash + 1);
            foreach (char c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
            n = n.Trim();
            if (n.Length == 0) n = "file.bin";
            if (n.Length > 180) n = n.Substring(0, 180);
            return n;
        }
        static string UniquePath(string p)
        {
            if (!File.Exists(p)) return p;
            string dir = Path.GetDirectoryName(p);
            string stem = Path.GetFileNameWithoutExtension(p);
            string ext = Path.GetExtension(p);
            for (int i = 1; i < 10000; i++)
            {
                string cand = Path.Combine(dir, stem + " (" + i + ")" + ext);
                if (!File.Exists(cand)) return cand;
            }
            return p;
        }
        static string JsonStr(string s)
        {
            var sb = new StringBuilder();
            sb.Append('"');
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') { sb.Append('\\').Append(c); }
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            sb.Append('"');
            return sb.ToString();
        }
        static string ToAscii(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s) sb.Append(c < 128 && c != '"' && c != '\\' ? c : '_');
            string r = sb.ToString().Trim();
            return r.Length == 0 ? "download" : r;
        }
        static string Rfc5987(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            var sb = new StringBuilder();
            foreach (byte b in bytes)
            {
                char c = (char)b;
                bool unreserved = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '.' || c == '_' || c == '~';
                if (unreserved) sb.Append(c);
                else sb.Append('%').Append(b.ToString("X2"));
            }
            return sb.ToString();
        }
        static Dictionary<string, string> ParseQuery(string q)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(q)) return d;
            foreach (var pair in q.Split('&'))
            {
                if (pair.Length == 0) continue;
                int eq = pair.IndexOf('=');
                string key = eq >= 0 ? pair.Substring(0, eq) : pair;
                string val = eq >= 0 ? pair.Substring(eq + 1) : "";
                d[UrlDecode(key)] = UrlDecode(val);
            }
            return d;
        }
        static string UrlDecode(string s)
        {
            try { return Uri.UnescapeDataString(s.Replace('+', ' ')); } catch { return s; }
        }

        // ---- 极简 HTTP 读写 ----
        static string ReadLine(Stream s)
        {
            var sb = new StringBuilder();
            int b;
            bool any = false;
            while ((b = s.ReadByte()) != -1)
            {
                any = true;
                if (b == '\n') break;
                if (b != '\r') sb.Append((char)b);
            }
            if (!any) return null;
            return sb.ToString();
        }
        static void WriteHead(Stream s, int code, string ctype, long len, List<string> extra)
        {
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(code).Append(' ').Append(Reason(code)).Append("\r\n");
            sb.Append("Content-Type: ").Append(ctype).Append("\r\n");
            if (len >= 0) sb.Append("Content-Length: ").Append(len).Append("\r\n");
            sb.Append("Cache-Control: no-store\r\n");
            sb.Append("Connection: close\r\n");
            if (extra != null) foreach (var h in extra) sb.Append(h).Append("\r\n");
            sb.Append("\r\n");
            var head = Encoding.UTF8.GetBytes(sb.ToString());
            s.Write(head, 0, head.Length);
        }
        static void WriteBytes(Stream s, int code, string ctype, byte[] body, List<string> extra)
        {
            WriteHead(s, code, ctype, body.Length, extra);
            s.Write(body, 0, body.Length);
            s.Flush();
        }
        static void WriteStatus(Stream s, int code, string msg)
        {
            WriteBytes(s, code, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(msg), null);
        }
        static string Reason(int c)
        {
            if (c == 200) return "OK";
            if (c == 403) return "Forbidden";
            if (c == 404) return "Not Found";
            if (c == 500) return "Internal Server Error";
            return "OK";
        }
    }

    class MainForm : Form
    {
        readonly Server server = new Server();
        Label status, urlLabel, recvLabel;
        ListBox shareList;
        Button addBtn, openFolderBtn, openPageBtn, clearBtn, langBtn;

        public MainForm()
        {
            Text = "BearDrop";
            StartPosition = FormStartPosition.CenterScreen;
            Width = 520; Height = 440;
            MinimumSize = new Size(460, 400);
            Font = new Font("Segoe UI", 9.5f);
            try { Icon = SystemIcons.Application; } catch { }

            server.Received += delegate (string p) { BeginInvoke(new Action(delegate { OnReceived(p); })); };

            langBtn = new Button(); langBtn.SetBounds(Width - 90, 10, 60, 26); langBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            langBtn.Click += delegate { L.Zh = !L.Zh; L.Save(); ApplyText(); };
            Controls.Add(langBtn);

            status = new Label(); status.SetBounds(16, 16, 380, 24); status.Font = new Font(Font, FontStyle.Bold);
            Controls.Add(status);

            urlLabel = new Label(); urlLabel.SetBounds(16, 44, 480, 22); urlLabel.ForeColor = Color.DimGray;
            urlLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(urlLabel);

            openPageBtn = new Button(); openPageBtn.SetBounds(16, 76, 150, 34);
            openPageBtn.Click += delegate { OpenPage(); };
            Controls.Add(openPageBtn);

            addBtn = new Button(); addBtn.SetBounds(176, 76, 150, 34);
            addBtn.Click += delegate { AddFiles(); };
            Controls.Add(addBtn);

            openFolderBtn = new Button(); openFolderBtn.SetBounds(336, 76, 150, 34);
            openFolderBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            openFolderBtn.Click += delegate { try { System.Diagnostics.Process.Start(server.ReceivedDir); } catch { } };
            Controls.Add(openFolderBtn);

            recvLabel = new Label(); recvLabel.SetBounds(16, 120, 480, 20); recvLabel.ForeColor = Color.DimGray;
            recvLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(recvLabel);

            shareList = new ListBox(); shareList.SetBounds(16, 148, 470, 200);
            shareList.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            shareList.AllowDrop = true;
            shareList.DragEnter += delegate (object s, DragEventArgs e) { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            shareList.DragDrop += delegate (object s, DragEventArgs e)
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                foreach (var f in files) if (File.Exists(f)) server.AddShared(f);
                RefreshShare();
            };
            Controls.Add(shareList);

            clearBtn = new Button(); clearBtn.SetBounds(16, 356, 120, 30);
            clearBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            clearBtn.Click += delegate { server.ClearShared(); RefreshShare(); };
            Controls.Add(clearBtn);

            Load += delegate { OnStart(); };
        }

        void OnStart()
        {
            try { server.Start(); }
            catch (Exception ex)
            {
                MessageBox.Show(this, L.T("无法启动服务：", "Could not start the service: ") + ex.Message, "BearDrop");
            }
            ApplyText();
            RefreshShare();
            FirstRunFirewallNote();
            OpenPage();
        }

        void ApplyText()
        {
            langBtn.Text = L.Zh ? "EN" : "中";
            status.Text = L.T("正在运行 — 已在浏览器打开页面", "Running — page opened in your browser");
            urlLabel.Text = L.T("手机扫码或打开：", "On your phone, scan the QR or open: ") + server.MobileUrl();
            openPageBtn.Text = L.T("重新打开页面", "Open page");
            addBtn.Text = L.T("添加文件（给手机下载）", "Add files to share");
            openFolderBtn.Text = L.T("打开接收文件夹", "Received folder");
            clearBtn.Text = L.T("清空分享", "Clear");
            recvLabel.Text = L.T("手机传来的文件保存到：", "Files from your phone are saved to: ") + server.ReceivedDir;
        }

        void OpenPage()
        {
            try { System.Diagnostics.Process.Start("http://127.0.0.1:" + server.Port + "/"); } catch { }
        }

        void AddFiles()
        {
            using (var ofd = new OpenFileDialog())
            {
                ofd.Multiselect = true;
                ofd.Title = L.T("选择要分享给手机的文件", "Choose files to share with your phone");
                if (ofd.ShowDialog(this) == DialogResult.OK)
                {
                    foreach (var f in ofd.FileNames) server.AddShared(f);
                    RefreshShare();
                }
            }
        }

        void RefreshShare()
        {
            shareList.BeginUpdate();
            shareList.Items.Clear();
            var snap = server.SharedSnapshot();
            if (snap.Count == 0)
                shareList.Items.Add(L.T("（还没有分享文件。点「添加文件」或把文件拖到这里，手机即可下载）",
                                        "(No shared files yet. Click “Add files” or drop files here for your phone to download.)"));
            else
                foreach (var s in snap) shareList.Items.Add(s.Name + "   (" + FmtSize(s.Size) + ")");
            shareList.EndUpdate();
        }

        void OnReceived(string path)
        {
            try { status.Text = L.T("已收到：", "Received: ") + Path.GetFileName(path); } catch { }
        }

        void FirstRunFirewallNote()
        {
            string flag = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BearDrop", "fw.ok");
            try { if (File.Exists(flag)) return; } catch { }
            MessageBox.Show(this,
                L.T("BearDrop 需要在局域网里和你的手机通信。\n\nWindows 第一次可能会弹出防火墙提示，请勾选「专用网络」并点「允许访问」。\n\n只在你信任的 Wi‑Fi（如家里）使用。",
                    "BearDrop talks to your phone over the local network.\n\nWindows may ask about the firewall the first time — please tick “Private networks” and click “Allow access”.\n\nUse it only on a Wi‑Fi you trust, such as at home."),
                "BearDrop");
            try { File.WriteAllText(flag, "1"); } catch { }
        }

        static string FmtSize(long n)
        {
            if (n < 1024) return n + " B";
            if (n < 1048576) return (n / 1024.0).ToString("0.0") + " KB";
            if (n < 1073741824) return (n / 1048576.0).ToString("0.0") + " MB";
            return (n / 1073741824.0).ToString("0.00") + " GB";
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try { server.Stop(); } catch { }
            base.OnFormClosing(e);
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            L.Load();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
