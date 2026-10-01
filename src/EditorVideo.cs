// Editor de Vídeo: aplicación de escritorio.
// Un servidor local (solo accesible desde este equipo) que usa FFmpeg nativo
// y abre la interfaz (app.html) en una ventana propia de Edge/Chrome.
// Se compila con el csc.exe que trae Windows: ver build.ps1.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("Editor de Vídeo")]
[assembly: System.Reflection.AssemblyVersion("2.0.0.0")]

class Settings
{
    public string OutputDir;
    public bool AutoOpen = true;
    public List<string> ExtraDirs = new List<string>();
}

class Job
{
    public string Id, Kind, Output, State = "running", Error;
    public double Time;
    public long Size;
    public List<string> Log = new List<string>();
    public Process Proc;
    public bool Cancelled;
    public DateTime Started = DateTime.Now;
}

static class App
{
    static readonly string Dir = AppDomain.CurrentDomain.BaseDirectory;
    static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EditorVideo");
    static readonly string PortFile = Path.Combine(DataDir, "instancia.txt");
    static readonly string SettingsFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    static readonly object Lk = new object();
    static readonly Dictionary<string, Job> Jobs = new Dictionary<string, Job>();
    static readonly List<string> OpenQueue = new List<string>();
    static readonly Dictionary<string, byte[]> ThumbCache = new Dictionary<string, byte[]>();
    static readonly Queue<string> ThumbOrder = new Queue<string>();
    static readonly Semaphore ThumbSem = new Semaphore(4, 4);

    static string FFmpeg, FFprobe, FFversion = "", HwEncoders = "";
    static int Port;
    static string Token;
    static HttpListener Listener;
    static Settings Cfg;
    static DateTime LastBeat = DateTime.Now, ByeAt = DateTime.MinValue;
    static bool NoBrowser;

    [STAThread]
    static void Main(string[] args)
    {
        Directory.CreateDirectory(DataDir);
        string fileArg = args.FirstOrDefault(a => !a.StartsWith("--") && File.Exists(a));
        NoBrowser = args.Contains("--no-browser");
        int fixedPort = 0;
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--port") int.TryParse(args[i + 1], out fixedPort);

        bool created;
        var mutex = new Mutex(true, "EditorVideo_instancia_unica", out created);
        if (!created)
        {
            // Ya está abierta: le pasamos el archivo y salimos
            if (ForwardToRunning(fileArg)) return;
        }

        Token = Guid.NewGuid().ToString("N");
        FFmpeg = FindTool("ffmpeg.exe");
        FFprobe = FindTool("ffprobe.exe");
        if (FFmpeg != null) { FFversion = RunCapture(FFmpeg, "-hide_banner -version", 5000).Split('\n')[0].Trim(); DetectHw(); }
        Cfg = LoadSettings();

        if (!StartListener(fixedPort))
        {
            MessageBox.Show("No se pudo iniciar el servidor local del editor.", "Editor de Vídeo", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        File.WriteAllText(PortFile, Port + "|" + Token);
        Log("inicio en puerto " + Port + " · " + (FFversion.Length > 0 ? FFversion.Substring(0, Math.Min(40, FFversion.Length)) : "SIN FFMPEG") + " · GPU: " + (HwEncoders.Length > 0 ? HwEncoders : "no"));
        if (fileArg != null) lock (Lk) OpenQueue.Add(fileArg);

        var t = new Thread(ListenLoop) { IsBackground = true };
        t.Start();
        if (!NoBrowser) LaunchWindow();

        // Se cierra cuando se cierra la ventana (o si deja de dar señales mucho tiempo)
        while (true)
        {
            Thread.Sleep(1000);
            if (NoBrowser) continue;
            double sinceBeat = (DateTime.Now - LastBeat).TotalSeconds;
            bool bye = ByeAt != DateTime.MinValue && sinceBeat > 4 && (DateTime.Now - ByeAt).TotalSeconds > 4;
            if (bye || sinceBeat > 600) { Log(bye ? "ventana cerrada: salgo" : "sin señales de la ventana: salgo"); break; }
        }
        lock (Lk) foreach (var j in Jobs.Values) KillJob(j);
        try { File.Delete(PortFile); } catch { }
        GC.KeepAlive(mutex);
    }

    // ---------------- arranque ----------------

    static bool ForwardToRunning(string file)
    {
        try
        {
            var parts = File.ReadAllText(PortFile).Split('|');
            string url = "http://localhost:" + parts[0] + "/api/open?k=" + parts[1] + (file != null ? "&path=" + Uri.EscapeDataString(file) : "");
            using (var wc = new WebClient()) wc.UploadString(url, "");
            return true;
        }
        catch { return false; }
    }

    static string FindTool(string exe)
    {
        foreach (var p in new[] { Path.Combine(Dir, "ffmpeg", exe), Path.Combine(Dir, "ffmpeg", "bin", exe), Path.Combine(Dir, exe) })
            if (File.Exists(p)) return p;
        foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try { var p = Path.Combine(d.Trim(), exe); if (File.Exists(p)) return p; } catch { }
        }
        return null;
    }

    // Comprueba qué codificadores por GPU funcionan de verdad en este equipo
    static void DetectHw()
    {
        var found = new List<string>();
        foreach (var enc in new[] { "h264_nvenc", "h264_qsv", "h264_amf" })
        {
            string o = RunCapture(FFmpeg, "-hide_banner -loglevel error -f lavfi -i color=black:s=320x240:d=0.2 -c:v " + enc + " -f null -", 8000);
            if (o.Trim().Length == 0 && LastExit == 0) found.Add(enc);
        }
        HwEncoders = string.Join(",", found);
    }

    static readonly string LogFile = Path.Combine(DataDir, "registro.txt");
    static void Log(string m)
    {
        try
        {
            if (File.Exists(LogFile) && new FileInfo(LogFile).Length > 512 * 1024) File.Delete(LogFile);
            File.AppendAllText(LogFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + m + Environment.NewLine);
        }
        catch { }
    }

    static int LastExit;
    static string RunCapture(string exe, string args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using (var p = Process.Start(psi))
            {
                var err = p.StandardError.ReadToEndAsync();
                string o = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } LastExit = -1; return "timeout"; }
                LastExit = p.ExitCode;
                return o + err.Result;
            }
        }
        catch (Exception ex) { LastExit = -1; return ex.Message; }
    }

    static bool StartListener(int fixedPort)
    {
        var ports = fixedPort > 0 ? new[] { fixedPort } : Enumerable.Range(47810, 30).ToArray();
        foreach (int p in ports)
        {
            try
            {
                var l = new HttpListener();
                l.Prefixes.Add("http://localhost:" + p + "/");
                l.Start();
                Listener = l; Port = p;
                return true;
            }
            catch { }
        }
        return false;
    }

    static void LaunchWindow()
    {
        string url = "http://localhost:" + Port + "/";
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string la = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var browsers = new[] {
            Path.Combine(pf86, @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(pf, @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(pf, @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(pf86, @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(la, @"Google\Chrome\Application\chrome.exe")
        };
        string profile = Path.Combine(DataDir, "ventana");
        foreach (var b in browsers)
        {
            if (!File.Exists(b)) continue;
            try
            {
                Process.Start(b, "--app=" + url + " --user-data-dir=" + Q(profile) +
                    " --no-first-run --no-default-browser-check --disable-features=Translate --window-size=1500,920");
                return;
            }
            catch { }
        }
        try { Process.Start(url); } catch { }
    }

    static Settings LoadSettings()
    {
        Settings s = null;
        try { if (File.Exists(SettingsFile)) s = Json.Deserialize<Settings>(File.ReadAllText(SettingsFile)); } catch { }
        if (s == null) s = new Settings();
        if (s.ExtraDirs == null) s.ExtraDirs = new List<string>();
        if (string.IsNullOrEmpty(s.OutputDir))
            s.OutputDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Editados");
        return s;
    }
    static void SaveSettings() { try { File.WriteAllText(SettingsFile, Json.Serialize(Cfg)); } catch { } }

    // ---------------- HTTP ----------------

    static void ListenLoop()
    {
        while (Listener.IsListening)
        {
            HttpListenerContext c;
            try { c = Listener.GetContext(); } catch { break; }
            ThreadPool.QueueUserWorkItem(_ => Handle(c));
        }
    }

    static void Handle(HttpListenerContext c)
    {
        try
        {
            var req = c.Request;
            string host = req.Headers["Host"] ?? "";
            // Evita que otras webs abiertas en el navegador usen el servidor
            if (host != "localhost:" + Port && host != "127.0.0.1:" + Port) { Send(c, 403, "text/plain", "Prohibido"); return; }
            string path = req.Url.AbsolutePath;
            var q = req.QueryString;
            if (path == "/" || path == "/index.html")
            {
                string html = File.ReadAllText(Path.Combine(Dir, "app.html"), Encoding.UTF8).Replace("__TOKEN__", Token);
                Send(c, 200, "text/html; charset=utf-8", html);
                return;
            }
            if (q["k"] != Token) { Send(c, 403, "text/plain", "Token no válido"); return; }
            LastBeat = DateTime.Now;

            switch (path)
            {
                case "/api/state": SendJson(c, State()); return;
                case "/api/events": SendJson(c, Events()); return;
                case "/api/bye": ByeAt = DateTime.Now; Log("aviso de cierre de la ventana"); SendJson(c, true); return;
                case "/api/open":
                    {
                        string p = q["path"];
                        lock (Lk) if (!string.IsNullOrEmpty(p)) OpenQueue.Add(p);
                        // si la ventana estaba cerrada, la volvemos a abrir
                        if (!NoBrowser && (DateTime.Now - LastBeatFromUi).TotalSeconds > 5) LaunchWindow();
                        SendJson(c, true); return;
                    }
                case "/api/recordings": SendJson(c, Recordings()); return;
                case "/api/probe": SendRaw(c, "application/json", RunCapture(FFprobe, "-v error -print_format json -show_format -show_streams " + Q(CheckFile(q["path"])), 30000)); return;
                case "/api/thumb": Thumb(c, CheckFile(q["path"]), Num(q["t"], 0), (int)Num(q["h"], 90)); return;
                case "/media": ServeFile(c, CheckFile(q["path"])); return;
                case "/api/pick": SendJson(c, new { path = Pick(q["kind"]) }); return;
                case "/api/pickdir":
                    {
                        string d = PickDir();
                        if (d != null) { Cfg.OutputDir = d; SaveSettings(); }
                        SendJson(c, new { path = d }); return;
                    }
                case "/api/settings":
                    {
                        var body = Json.Deserialize<Dictionary<string, object>>(ReadBody(req));
                        if (body.ContainsKey("autoOpen")) Cfg.AutoOpen = Convert.ToBoolean(body["autoOpen"]);
                        if (body.ContainsKey("outputDir") && body["outputDir"] is string) Cfg.OutputDir = (string)body["outputDir"];
                        SaveSettings(); SendJson(c, State()); return;
                    }
                case "/api/upload": SendJson(c, new { path = Upload(req, q["name"]) }); return;
                case "/api/run": SendJson(c, StartJob(Json.Deserialize<Dictionary<string, object>>(ReadBody(req)))); return;
                case "/api/job": SendJson(c, JobInfo(q["id"])); return;
                case "/api/cancel":
                    {
                        Job j; lock (Lk) Jobs.TryGetValue(q["id"] ?? "", out j);
                        if (j != null) { j.Cancelled = true; KillJob(j); }
                        SendJson(c, true); return;
                    }
                case "/api/reveal": Process.Start("explorer.exe", "/select," + Q(CheckFile(q["path"]))); SendJson(c, true); return;
                case "/api/openfolder": Directory.CreateDirectory(Cfg.OutputDir); Process.Start("explorer.exe", Q(Cfg.OutputDir)); SendJson(c, true); return;
                case "/api/play": Process.Start(CheckFile(q["path"])); SendJson(c, true); return;
                case "/api/copyfile": CopyFileToClipboard(CheckFile(q["path"])); SendJson(c, true); return;
                default: Send(c, 404, "text/plain", "No encontrado"); return;
            }
        }
        catch (Exception ex)
        {
            try { SendJson(c, new { error = ex.Message }, 500); } catch { }
        }
    }

    static DateTime LastBeatFromUi = DateTime.MinValue;

    static object State()
    {
        return new
        {
            ffmpeg = FFmpeg != null && FFprobe != null,
            version = FFversion,
            hw = HwEncoders,
            outputDir = Cfg.OutputDir,
            autoOpen = Cfg.AutoOpen,
            recDirs = RecDirs()
        };
    }

    static object Events()
    {
        LastBeatFromUi = DateTime.Now;
        if (ByeAt != DateTime.MinValue && (DateTime.Now - ByeAt).TotalSeconds > 2) ByeAt = DateTime.MinValue;
        List<string> open;
        lock (Lk) { open = new List<string>(OpenQueue); OpenQueue.Clear(); }
        return new { open = open };
    }

    static string CheckFile(string p)
    {
        if (string.IsNullOrEmpty(p) || !File.Exists(p)) throw new FileNotFoundException("No se encuentra el archivo: " + p);
        return Path.GetFullPath(p);
    }

    static double Num(string s, double def)
    {
        double v;
        return double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) ? v : def;
    }

    static string ReadBody(HttpListenerRequest req)
    {
        using (var r = new StreamReader(req.InputStream, Encoding.UTF8)) return r.ReadToEnd();
    }

    static void Send(HttpListenerContext c, int status, string type, string text)
    {
        var b = Encoding.UTF8.GetBytes(text);
        c.Response.StatusCode = status;
        c.Response.ContentType = type;
        c.Response.AddHeader("Cache-Control", "no-store");
        c.Response.ContentLength64 = b.Length;
        try { c.Response.OutputStream.Write(b, 0, b.Length); c.Response.Close(); } catch { }
    }
    static void SendRaw(HttpListenerContext c, string type, string text) { Send(c, 200, type, text); }
    static void SendJson(HttpListenerContext c, object o, int status = 200) { Send(c, status, "application/json; charset=utf-8", Json.Serialize(o)); }

    // ---------------- grabaciones ----------------

    static List<string> RecDirs()
    {
        string v = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        var l = new List<string>();
        foreach (var d in new[] { Path.Combine(v, "Captures"), Path.Combine(v, "Capturas"), Path.Combine(v, "Screen Recordings"), Path.Combine(v, "Grabaciones de pantalla") })
            if (Directory.Exists(d)) l.Add(d);
        // carpeta de la Game Bar configurada en el registro (si la has cambiado)
        try
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders"))
            {
                var cap = k == null ? null : k.GetValue("{EDC0FE71-98D8-4F4A-B920-C8DC133CB165}") as string;
                if (cap != null) { cap = Environment.ExpandEnvironmentVariables(cap); if (Directory.Exists(cap) && !l.Contains(cap)) l.Insert(0, cap); }
            }
        }
        catch { }
        foreach (var d in Cfg.ExtraDirs) if (Directory.Exists(d) && !l.Contains(d)) l.Add(d);
        return l;
    }

    static object Recordings()
    {
        var exts = new HashSet<string>(new[] { ".mp4", ".mkv", ".mov", ".webm", ".avi" }, StringComparer.OrdinalIgnoreCase);
        var files = new List<FileInfo>();
        foreach (var d in RecDirs())
        {
            try { files.AddRange(new DirectoryInfo(d).GetFiles().Where(f => exts.Contains(f.Extension))); } catch { }
        }
        var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return files.OrderByDescending(f => f.LastWriteTimeUtc).Take(80).Select(f => new
        {
            path = f.FullName,
            name = f.Name,
            size = f.Length,
            mtime = (long)(f.LastWriteTimeUtc - epoch).TotalMilliseconds,
            recording = (DateTime.UtcNow - f.LastWriteTimeUtc).TotalMinutes < 30 && IsLocked(f.FullName)
        }).ToList();
    }

    // La Game Bar mantiene el archivo bloqueado mientras graba
    static bool IsLocked(string p)
    {
        try { using (new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read)) return false; }
        catch (IOException) { return true; }
        catch { return false; }
    }

    // ---------------- miniaturas y vídeo ----------------

    static void Thumb(HttpListenerContext c, string p, double t, int h)
    {
        h = Math.Max(32, Math.Min(h, 1080));
        string key = p + "|" + File.GetLastWriteTimeUtc(p).Ticks + "|" + t.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "|" + h;
        byte[] data;
        lock (Lk) ThumbCache.TryGetValue(key, out data);
        if (data == null)
        {
            ThumbSem.WaitOne();
            try
            {
                string ts = t.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
                var psi = new ProcessStartInfo(FFmpeg, "-hide_banner -loglevel error -ss " + ts + " -i " + Q(p) +
                    " -frames:v 1 -vf scale=-2:" + h + " -q:v " + (h > 300 ? 3 : 6) + " -f image2pipe -c:v mjpeg pipe:1")
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                using (var proc = Process.Start(psi))
                using (var ms = new MemoryStream())
                {
                    proc.StandardError.ReadToEndAsync();
                    proc.StandardOutput.BaseStream.CopyTo(ms);
                    proc.WaitForExit(15000);
                    data = ms.ToArray();
                }
            }
            finally { ThumbSem.Release(); }
            if (data.Length > 0) lock (Lk)
                {
                    ThumbCache[key] = data; ThumbOrder.Enqueue(key);
                    while (ThumbOrder.Count > 600) ThumbCache.Remove(ThumbOrder.Dequeue());
                }
        }
        if (data.Length == 0) { Send(c, 404, "text/plain", "sin fotograma"); return; }
        c.Response.ContentType = "image/jpeg";
        c.Response.AddHeader("Cache-Control", "max-age=3600");
        c.Response.ContentLength64 = data.Length;
        try { c.Response.OutputStream.Write(data, 0, data.Length); c.Response.Close(); } catch { }
    }

    static string Mime(string p)
    {
        switch (Path.GetExtension(p).ToLowerInvariant())
        {
            case ".mp4": case ".m4v": case ".mov": return "video/mp4";
            case ".webm": return "video/webm";
            case ".mkv": return "video/x-matroska";
            case ".mp3": return "audio/mpeg";
            case ".m4a": case ".aac": return "audio/mp4";
            case ".wav": return "audio/wav";
            case ".ogg": case ".opus": return "audio/ogg";
            case ".flac": return "audio/flac";
            case ".gif": return "image/gif";
            default: return "application/octet-stream";
        }
    }

    // Sirve archivos con soporte de "Range" para poder saltar a cualquier punto del vídeo
    static void ServeFile(HttpListenerContext c, string p)
    {
        var res = c.Response;
        try
        {
            using (var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long len = fs.Length, start = 0, end = len - 1;
                string range = c.Request.Headers["Range"];
                res.AddHeader("Accept-Ranges", "bytes");
                res.ContentType = Mime(p);
                if (range != null && range.StartsWith("bytes="))
                {
                    var r = range.Substring(6).Split(',')[0].Split('-');
                    if (r[0].Length == 0) { start = Math.Max(0, len - long.Parse(r[1])); }
                    else { start = long.Parse(r[0]); if (r.Length > 1 && r[1].Length > 0) end = Math.Min(long.Parse(r[1]), len - 1); }
                    if (start >= len) { res.StatusCode = 416; res.AddHeader("Content-Range", "bytes */" + len); res.Close(); return; }
                    res.StatusCode = 206;
                    res.AddHeader("Content-Range", "bytes " + start + "-" + end + "/" + len);
                }
                long count = end - start + 1;
                res.ContentLength64 = count;
                fs.Seek(start, SeekOrigin.Begin);
                var buf = new byte[262144];
                while (count > 0)
                {
                    int n = fs.Read(buf, 0, (int)Math.Min(buf.Length, count));
                    if (n <= 0) break;
                    res.OutputStream.Write(buf, 0, n);
                    count -= n;
                }
                res.Close();
            }
        }
        catch (HttpListenerException) { } // el navegador cortó la descarga al saltar: normal
        catch (IOException) { }
    }

    static string Upload(HttpListenerRequest req, string name)
    {
        string dir = Path.Combine(DataDir, "importados");
        Directory.CreateDirectory(dir);
        string safe = Regex.Replace(Path.GetFileName(name ?? "archivo"), "[\\\\/:*?\"<>|]", "_");
        string dest = Unique(Path.Combine(dir, safe));
        using (var fs = File.Create(dest)) req.InputStream.CopyTo(fs, 1 << 20);
        return dest;
    }

    // ---------------- diálogos de Windows ----------------

    static T OnSta<T>(Func<T> f)
    {
        T result = default(T);
        Exception err = null;
        var th = new Thread(() => { try { result = f(); } catch (Exception e) { err = e; } });
        th.SetApartmentState(ApartmentState.STA);
        th.Start(); th.Join();
        if (err != null) throw err;
        return result;
    }

    static Form TopOwner()
    {
        var f = new Form { TopMost = true, ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None, Size = new Size(1, 1), StartPosition = FormStartPosition.CenterScreen, Opacity = 0 };
        f.Show(); f.Activate();
        return f;
    }

    static string Pick(string kind)
    {
        return OnSta(() =>
        {
            using (var owner = TopOwner())
            using (var d = new OpenFileDialog())
            {
                if (kind == "audio")
                {
                    d.Title = "Elige el audio";
                    d.Filter = "Audio|*.mp3;*.wav;*.m4a;*.aac;*.ogg;*.opus;*.flac;*.wma|Todos los archivos|*.*";
                }
                else
                {
                    d.Title = "Abrir vídeo";
                    d.Filter = "Vídeos|*.mp4;*.mkv;*.mov;*.webm;*.avi;*.m4v;*.wmv;*.flv;*.ts;*.mts;*.m2ts;*.3gp|Todos los archivos|*.*";
                    var rd = RecDirs();
                    if (rd.Count > 0) d.InitialDirectory = rd[0];
                }
                return d.ShowDialog(owner) == DialogResult.OK ? d.FileName : null;
            }
        });
    }

    static string PickDir()
    {
        return OnSta(() =>
        {
            using (var owner = TopOwner())
            using (var d = new FolderBrowserDialog { Description = "Carpeta donde se guardan los vídeos editados", SelectedPath = Cfg.OutputDir, ShowNewFolderButton = true })
                return d.ShowDialog(owner) == DialogResult.OK ? d.SelectedPath : null;
        });
    }

    // Copia el archivo al portapapeles para pegarlo en Explorer, Teams, Slack, WhatsApp…
    static void CopyFileToClipboard(string p)
    {
        OnSta(() =>
        {
            var col = new System.Collections.Specialized.StringCollection();
            col.Add(p);
            Clipboard.SetFileDropList(col);
            return true;
        });
    }

    // ---------------- trabajos de FFmpeg ----------------

    static object StartJob(Dictionary<string, object> body)
    {
        if (FFmpeg == null) throw new Exception("FFmpeg no está instalado");
        string kind = body.ContainsKey("kind") ? (string)body["kind"] : "export";
        string input = CheckFile((string)body["input"]);
        string audio = body.ContainsKey("audio") && body["audio"] != null ? CheckFile((string)body["audio"]) : null;
        string ext = Regex.Replace((string)body["ext"], "[^a-z0-9]", "");
        string output;
        if (kind == "proxy")
        {
            string dir = Path.Combine(DataDir, "vistas_previas");
            Directory.CreateDirectory(dir);
            CleanOld(dir, 20);
            var fi = new FileInfo(input);
            string hash = ((uint)(fi.FullName + fi.Length + fi.LastWriteTimeUtc.Ticks).GetHashCode()).ToString("x8");
            output = Path.Combine(dir, hash + "." + ext);
            if (File.Exists(output) && new FileInfo(output).Length > 0)
            {
                var done = new Job { Id = Guid.NewGuid().ToString("N"), Kind = kind, Output = output, State = "done", Size = new FileInfo(output).Length };
                lock (Lk) Jobs[done.Id] = done;
                return new { id = done.Id, output = output };
            }
        }
        else
        {
            Directory.CreateDirectory(Cfg.OutputDir);
            string name = Regex.Replace((string)body["name"] ?? "video_editado", "[\\\\/:*?\"<>|]", "_").Trim();
            if (name.Length == 0) name = "video_editado";
            output = Unique(Path.Combine(Cfg.OutputDir, name + "." + ext));
        }
        string tmpOut = output + ".parcial." + ext;

        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-progress", "pipe:1", "-nostats" };
        foreach (var a in (System.Collections.IEnumerable)body["args"])
        {
            string s = (string)a;
            if (s == "{IN}") s = input;
            else if (s == "{AUD}") s = audio;
            else if (s == "{OUT}") s = tmpOut;
            args.Add(s);
        }
        // el formato se fija explícitamente porque el archivo temporal tiene otra extensión
        int outIdx = args.LastIndexOf(tmpOut);
        string muxer = ext == "mp4" ? "mp4" : ext == "webm" ? "webm" : ext == "gif" ? "gif" : ext == "mp3" ? "mp3" : ext;
        args.Insert(outIdx, "-f"); args.Insert(outIdx + 1, muxer);

        var job = new Job { Id = Guid.NewGuid().ToString("N"), Kind = kind, Output = output };
        var psi = new ProcessStartInfo(FFmpeg, string.Join(" ", args.Select(Q)))
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8, StandardOutputEncoding = Encoding.UTF8
        };
        job.Log.Add("$ ffmpeg " + psi.Arguments);
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (s, e) =>
        {
            if (e.Data == null) return;
            if (e.Data.StartsWith("out_time_us=") || e.Data.StartsWith("out_time_ms="))
            {
                long us;
                if (long.TryParse(e.Data.Substring(12), out us) && us > 0) job.Time = us / 1e6;
            }
        };
        p.ErrorDataReceived += (s, e) =>
        {
            if (e.Data == null) return;
            lock (job.Log) { job.Log.Add(e.Data); if (job.Log.Count > 300) job.Log.RemoveAt(1); }
        };
        p.Exited += (s, e) =>
        {
            Thread.Sleep(200); // deja terminar de leer la salida
            int code = -1;
            try { code = p.ExitCode; } catch { }
            if (job.Cancelled) { job.State = "cancelled"; TryDelete(tmpOut); return; }
            if (code == 0 && File.Exists(tmpOut) && new FileInfo(tmpOut).Length > 0)
            {
                try
                {
                    if (File.Exists(output)) File.Delete(output);
                    File.Move(tmpOut, output);
                    job.Size = new FileInfo(output).Length;
                    job.State = "done";
                }
                catch (Exception ex) { job.State = "error"; job.Error = "No se pudo guardar el archivo: " + ex.Message; }
            }
            else
            {
                TryDelete(tmpOut);
                job.State = "error";
                job.Error = "FFmpeg terminó con código " + code;
                lock (job.Log) Log("error en " + job.Kind + " (código " + code + "): " + string.Join(" / ", job.Log.Skip(Math.Max(0, job.Log.Count - 4))));
            }
        };
        p.Start();
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { } // el PC sigue respondiendo mientras exporta
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        job.Proc = p;
        lock (Lk) Jobs[job.Id] = job;
        return new { id = job.Id, output = output };
    }

    static object JobInfo(string id)
    {
        Job j;
        lock (Lk) Jobs.TryGetValue(id ?? "", out j);
        if (j == null) return new { state = "unknown" };
        List<string> tail;
        lock (j.Log) tail = j.Log.Skip(Math.Max(0, j.Log.Count - 40)).ToList();
        return new { state = j.State, time = j.Time, output = j.Output, size = j.Size, error = j.Error, log = tail, elapsed = (DateTime.Now - j.Started).TotalSeconds };
    }

    static void KillJob(Job j)
    {
        try { if (j.Proc != null && !j.Proc.HasExited) j.Proc.Kill(); } catch { }
    }

    // ---------------- utilidades ----------------

    static string Unique(string p)
    {
        if (!File.Exists(p)) return p;
        string dir = Path.GetDirectoryName(p), name = Path.GetFileNameWithoutExtension(p), ext = Path.GetExtension(p);
        for (int i = 2; ; i++)
        {
            string c = Path.Combine(dir, name + " (" + i + ")" + ext);
            if (!File.Exists(c)) return c;
        }
    }

    static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }

    static void CleanOld(string dir, int keep)
    {
        try { foreach (var f in new DirectoryInfo(dir).GetFiles().OrderByDescending(f => f.LastWriteTimeUtc).Skip(keep)) f.Delete(); } catch { }
    }

    // Entrecomillado de argumentos según las reglas de Windows
    static string Q(string a)
    {
        if (a == null) a = "";
        if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
        var sb = new StringBuilder("\"");
        int bs = 0;
        foreach (char ch in a)
        {
            if (ch == '\\') { bs++; continue; }
            if (ch == '"') { sb.Append('\\', bs * 2 + 1); sb.Append('"'); bs = 0; continue; }
            sb.Append('\\', bs); bs = 0; sb.Append(ch);
        }
        sb.Append('\\', bs * 2);
        sb.Append('"');
        return sb.ToString();
    }
}
