using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Luigi
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    public class MainForm : Form
    {
        const string Host = "luigi.local";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        static readonly string Ver = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        static readonly string BaseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Luigi");
        static readonly string StateFile = Path.Combine(BaseDir, "fenster.json");
        readonly WebView2 web;
        readonly bool dark;
        string updateUrl;
        bool checkedUpdate;
        NotifyIcon tray;

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public MainForm()
        {
            dark = IsDarkMode();
            Text = "Luigi";
            Width = 1280; Height = 820;
            MinimumSize = new Size(420, 560);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = dark ? Color.FromArgb(19, 19, 20) : Color.White;
            LoadState();
            web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
            Controls.Add(web);
            Load += async (s, e) => await Start();
            FormClosing += (s, e) => { SaveState(); try { if (tray != null) { tray.Visible = false; tray.Dispose(); } } catch { } };
        }

        void LoadState()
        {
            try
            {
                if (!File.Exists(StateFile)) return;
                using var doc = JsonDocument.Parse(File.ReadAllText(StateFile));
                var r = doc.RootElement;
                var rect = new Rectangle(r.GetProperty("x").GetInt32(), r.GetProperty("y").GetInt32(), r.GetProperty("w").GetInt32(), r.GetProperty("h").GetInt32());
                if (rect.Width >= 420 && rect.Height >= 560 && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(rect)))
                {
                    StartPosition = FormStartPosition.Manual;
                    Bounds = rect;
                }
                if (r.GetProperty("max").GetBoolean()) WindowState = FormWindowState.Maximized;
                TopMost = r.GetProperty("oben").GetBoolean();
            }
            catch { }
        }

        void SaveState()
        {
            try
            {
                Directory.CreateDirectory(BaseDir);
                var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                File.WriteAllText(StateFile, JsonSerializer.Serialize(new { x = b.X, y = b.Y, w = b.Width, h = b.Height, max = WindowState == FormWindowState.Maximized, oben = TopMost }));
            }
            catch { }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int on = dark ? 1 : 0;
            try { DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)); } catch { }
        }

        static bool IsDarkMode()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch { return false; }
        }

        static bool AutostartOn()
        {
            try { using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue("Luigi") != null; } catch { return false; }
        }

        static void SetAutostart(bool on)
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true);
                if (k == null) return;
                if (on) k.SetValue("Luigi", "\"" + Environment.ProcessPath + "\""); else k.DeleteValue("Luigi", false);
            }
            catch { }
        }

        async Task Start()
        {
            string appDir = Path.Combine(BaseDir, "app");
            Directory.CreateDirectory(appDir);
            using (var res = typeof(MainForm).Assembly.GetManifestResourceStream("index.html"))
            using (var file = File.Create(Path.Combine(appDir, "index.html")))
                res.CopyTo(file);

            try
            {
                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(BaseDir, "WebView"));
                await web.EnsureCoreWebView2Async(env);
            }
            catch (WebView2RuntimeNotFoundException)
            {
                MessageBox.Show("Luigi braucht Microsoft Edge WebView2. Das ist bei Windows 10 und 11 meistens schon da. Ich öffne jetzt die Download-Seite von Microsoft.", "Luigi");
                Open("https://developer.microsoft.com/microsoft-edge/webview2/");
                Close();
                return;
            }

            var core = web.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping(Host, appDir, CoreWebView2HostResourceAccessKind.Allow);
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.NewWindowRequested += (s, e) => { e.Handled = true; Open(e.Uri); };
            core.NavigationStarting += (s, e) =>
            {
                if (!e.Uri.StartsWith("https://" + Host + "/", StringComparison.OrdinalIgnoreCase)) { e.Cancel = true; Open(e.Uri); }
            };
            try { await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache | CoreWebView2BrowsingDataKinds.CacheStorage); } catch { }
            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                "window.LUIGI_VERSION=" + JsonSerializer.Serialize(Ver) + ";window.LUIGI_APP=" + JsonSerializer.Serialize(new { oben = TopMost, autostart = AutostartOn() }) + ";");
            core.WebMessageReceived += OnMessage;
            core.NavigationCompleted += async (s, e) => await CheckUpdate();
            core.Navigate("https://" + Host + "/index.html?v=" + Ver + "-" + DateTime.UtcNow.Ticks);
        }

        void Reply(string id, bool ok, string msg, string data = null)
        {
            try { web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { id, ok, msg, data })); } catch { }
        }

        // ---------- Aufträge vom Chat ----------
        async void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            var a = new Dictionary<string, string>();
            try
            {
                using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
                foreach (var p in doc.RootElement.EnumerateObject())
                    a[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.ToString();
            }
            catch { return; }
            string G(string k) => a.TryGetValue(k, out var v) ? v ?? "" : "";
            string id = G("id"), typ = G("typ");
            bool an = G("an").ToLowerInvariant() == "true";

            switch (typ)
            {
                case "update_installieren": await InstallUpdate(); return;
                case "fenster_oben": TopMost = an; SaveState(); return;
                case "autostart": SetAutostart(an); return;
                case "hinweis":
                    try
                    {
                        if (tray == null) tray = new NotifyIcon { Icon = SystemIcons.Information, Text = "Luigi", Visible = true };
                        tray.ShowBalloonTip(8000, "Luigi erinnert dich", G("text"), ToolTipIcon.Info);
                    }
                    catch { }
                    return;
                case "zwischenablage":
                    try { Clipboard.SetText(G("inhalt")); Reply(id, true, "Liegt in der Zwischenablage."); }
                    catch { Reply(id, false, "Die Zwischenablage war gerade gesperrt."); }
                    return;
            }

            (bool ok, string msg, string data) r;
            try { r = await Task.Run(() => Run(typ, G)); }
            catch (UnauthorizedAccessException) { r = (false, "Darauf habe ich keinen Zugriff.", null); }
            catch (IOException ex) { r = (false, "Das ging nicht: " + ex.Message, null); }
            catch (Exception ex) { r = (false, "Fehler: " + ex.Message, null); }
            Reply(id, r.ok, r.msg, r.data);
        }

        static (bool, string, string) Ok(string m, string d = null) => (true, m, d);
        static (bool, string, string) Bad(string m) => (false, m, null);

        static string BaseFolder(string ort)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            switch (ort)
            {
                case "Dokumente": return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                case "Downloads": return Path.Combine(home, "Downloads");
                case "Bilder": return Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                case "Musik": return Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
                case "Videos": return Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                default: return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            }
        }

        static string Target(string ort, string name, out string shown)
        {
            string baseDir = Path.GetFullPath(BaseFolder(ort));
            var invalid = Path.GetInvalidFileNameChars();
            var parts = (name ?? "").Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => new string(p.Where(c => !invalid.Contains(c)).ToArray()).Trim().TrimEnd('.'))
                .Where(p => p.Length > 0 && p != "." && p != "..").ToArray();
            string full = Path.GetFullPath(Path.Combine(new[] { baseDir }.Concat(parts).ToArray()));
            if (full != baseDir && !full.StartsWith(baseDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("Dieser Ort ist nicht erlaubt.");
            shown = string.Join(" › ", new[] { string.IsNullOrEmpty(ort) ? "Desktop" : ort }.Concat(parts));
            return full;
        }

        static readonly Dictionary<string, string[]> Groups = new Dictionary<string, string[]>
        {
            { "Bilder", new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".heic", ".svg", ".ico" } },
            { "Videos", new[] { ".mp4", ".mov", ".mkv", ".avi", ".webm", ".wmv" } },
            { "Musik", new[] { ".mp3", ".wav", ".ogg", ".flac", ".m4a", ".aac" } },
            { "Dokumente", new[] { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".odt", ".csv", ".rtf", ".md" } },
            { "Programme", new[] { ".exe", ".msi", ".appx", ".msix" } },
            { "Archive", new[] { ".zip", ".rar", ".7z", ".tar", ".gz" } },
        };
        static readonly string[] Blocked = { ".exe", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".jse", ".wsf", ".lnk", ".url", ".reg", ".msi", ".scr", ".com", ".dll", ".sys", ".hta", ".cpl" };

        static bool Hidden(string fn) => fn.StartsWith(".") || fn.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase);
        static string Size(long b) => b < 1024 ? b + " B" : b < 1048576 ? (b / 1024) + " KB" : (b / 1048576.0).ToString("0.#") + " MB";

        static string Free(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path), stem = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
            for (int n = 2; ; n++)
            {
                string c = Path.Combine(dir, $"{stem} ({n}){ext}");
                if (!File.Exists(c) && !Directory.Exists(c)) return c;
            }
        }

        static void CopyDir(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), false);
            foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
        }

        static string Fetch(string url)
        {
            using var h = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(15) };
            h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
            h.DefaultRequestHeaders.AcceptLanguage.ParseAdd("de-DE,de;q=0.9,en;q=0.5");
            return h.GetStringAsync(url).GetAwaiter().GetResult();
        }

        static string Strip(string html)
        {
            html = Regex.Replace(html, @"<(script|style|noscript|svg|head)[\s\S]*?</\1>", " ", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<(br|/p|/div|/li|/h\d|/tr)[^>]*>", "\n", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, "<[^>]+>", " ");
            html = WebUtility.HtmlDecode(html);
            html = Regex.Replace(html, @"[ \t\r\f]+", " ");
            html = Regex.Replace(html, @"\s*\n\s*", "\n");
            return html.Trim();
        }

        static string Cut(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + " …";

        static (bool, string, string) FindProgram(string name)
        {
            var builtin = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "rechner", "calc.exe" }, { "taschenrechner", "calc.exe" }, { "calculator", "calc.exe" },
                { "editor", "notepad.exe" }, { "notepad", "notepad.exe" }, { "paint", "mspaint.exe" },
                { "explorer", "explorer.exe" }, { "datei-explorer", "explorer.exe" }, { "dateiexplorer", "explorer.exe" },
                { "task-manager", "taskmgr.exe" }, { "taskmanager", "taskmgr.exe" }, { "einstellungen", "ms-settings:" },
                { "eingabeaufforderung", "cmd.exe" }, { "cmd", "cmd.exe" }, { "terminal", "wt.exe" },
                { "snipping tool", "ms-screenclip:" }, { "kamera", "microsoft.windows.camera:" }, { "uhr", "ms-clock:" }
            };
            if (builtin.TryGetValue(name.Trim(), out var cmd))
            {
                Process.Start(new ProcessStartInfo(cmd) { UseShellExecute = true });
                return Ok($"**{name}** ist geöffnet.");
            }
            var dirs = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
            };
            var opt = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            string q = name.Trim().ToLowerInvariant(), best = null; int bestScore = 99;
            foreach (var d in dirs.Where(Directory.Exists))
                foreach (var f in Directory.EnumerateFiles(d, "*.*", opt))
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext != ".lnk" && ext != ".url" && ext != ".appref-ms") continue;
                    var n = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                    if (n.Contains("uninstall") || n.Contains("deinstall") || n.Contains("hilfe") || n.Contains("help")) continue;
                    int score = n == q ? 0 : n.StartsWith(q) ? 1 : n.Contains(q) ? 2 : 99;
                    if (score < bestScore || (score == bestScore && best != null && n.Length < Path.GetFileNameWithoutExtension(best).Length)) { bestScore = score; best = f; }
                }
            if (best == null) return Bad($"Ich habe kein Programm namens **{name}** gefunden.");
            Process.Start(new ProcessStartInfo(best) { UseShellExecute = true });
            return Ok($"**{Path.GetFileNameWithoutExtension(best)}** ist geöffnet.");
        }

        static (bool, string, string) Run(string typ, Func<string, string> G)
        {
            string ort = G("ort"), name = G("name");

            if (typ == "webseite_oeffnen" || typ == "webseite_lesen")
            {
                if (!Uri.TryCreate(G("url"), UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http")) return Bad("Diese Adresse kann ich nicht öffnen.");
                if (typ == "webseite_oeffnen")
                {
                    Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                    return Ok($"**{uri.Host}** ist geöffnet.");
                }
                var text = Strip(Fetch(uri.AbsoluteUri));
                if (text.Length < 40) return Bad("Auf der Seite stand kein lesbarer Text.");
                return Ok($"**{uri.Host}** gelesen ({text.Length} Zeichen).", Cut(text, 9000));
            }
            if (typ == "websuche")
            {
                if (string.IsNullOrWhiteSpace(name)) return Bad("Wonach soll ich suchen?");
                var html = Fetch("https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(name));
                var links = Regex.Matches(html, @"<a[^>]*class=""result__a""[^>]*href=""([^""]+)""[^>]*>([\s\S]*?)</a>");
                var snips = Regex.Matches(html, @"class=""result__snippet""[^>]*>([\s\S]*?)</a>");
                var sb = new StringBuilder(); var shown = new StringBuilder();
                for (int i = 0; i < links.Count && i < 6; i++)
                {
                    string href = WebUtility.HtmlDecode(links[i].Groups[1].Value);
                    var m = Regex.Match(href, @"uddg=([^&]+)");
                    if (m.Success) href = Uri.UnescapeDataString(m.Groups[1].Value);
                    string title = Strip(links[i].Groups[2].Value), snip = i < snips.Count ? Strip(snips[i].Groups[1].Value) : "";
                    sb.Append($"{i + 1}. {title}\n{href}\n{snip}\n\n");
                    shown.Append($"- {title}\n");
                }
                if (sb.Length == 0)
                {
                    var plain = Strip(html);
                    if (plain.Length < 80) return Bad("Die Suche hat nichts geliefert.");
                    return Ok("Suche erledigt.", Cut(plain, 4000));
                }
                return Ok(shown.ToString().TrimEnd(), sb.ToString());
            }
            if (typ == "programm_oeffnen")
            {
                if (string.IsNullOrWhiteSpace(name)) return Bad("Welches Programm denn?");
                return FindProgram(name);
            }
            if (typ == "pc_info")
            {
                var sb = new StringBuilder();
                sb.Append($"- Uhrzeit: {DateTime.Now:dddd, d. MMMM yyyy, HH:mm}\n- Windows: {Environment.OSVersion.Version}\n");
                try
                {
                    var d = new DriveInfo(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
                    sb.Append($"- Speicher {d.Name}: {d.AvailableFreeSpace / 1073741824} GB frei von {d.TotalSize / 1073741824} GB\n");
                }
                catch { }
                try
                {
                    var p = SystemInformation.PowerStatus;
                    if (p.BatteryChargeStatus != BatteryChargeStatus.NoSystemBattery) sb.Append($"- Akku: {(int)(p.BatteryLifePercent * 100)} %{(p.PowerLineStatus == PowerLineStatus.Online ? " (lädt)" : "")}\n");
                }
                catch { }
                var s = sb.ToString().TrimEnd();
                return Ok(s, s);
            }

            string path = Target(ort, name, out string shown0);
            string baseDir = Path.GetFullPath(BaseFolder(ort));
            bool isBase = path == baseDir;
            switch (typ)
            {
                case "ordner_anlegen":
                    if (isBase) return Bad("Der Ordner braucht einen Namen.");
                    if (Directory.Exists(path)) return Ok($"Den Ordner **{shown0}** gibt es schon.");
                    Directory.CreateDirectory(path);
                    return Ok($"Der Ordner **{shown0}** ist angelegt.");

                case "ordner_oeffnen":
                    if (!Directory.Exists(path)) return Bad($"**{shown0}** gibt es nicht.");
                    Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
                    return Ok($"**{shown0}** ist geöffnet.");

                case "dateien_auflisten":
                {
                    if (!Directory.Exists(path)) return Bad($"**{shown0}** gibt es nicht.");
                    var dirs = Directory.GetDirectories(path).Select(Path.GetFileName).Where(n => !Hidden(n)).OrderBy(n => n).ToList();
                    var files = new DirectoryInfo(path).GetFiles().Where(f => !Hidden(f.Name) && (f.Attributes & FileAttributes.Hidden) == 0).OrderBy(f => f.Name).ToList();
                    if (dirs.Count + files.Count == 0) return Ok($"**{shown0}** ist leer.", "leer");
                    var data = new StringBuilder();
                    foreach (var d in dirs.Take(150)) data.Append("[Ordner] ").Append(d).Append('\n');
                    foreach (var f in files.Take(250)) data.Append(f.Name).Append("  (").Append(Size(f.Length)).Append(", ").Append(f.LastWriteTime.ToString("dd.MM.yyyy")).Append(")\n");
                    return Ok($"In **{shown0}**: {dirs.Count} Ordner, {files.Count} Dateien", data.ToString());
                }

                case "suchen":
                {
                    if (string.IsNullOrWhiteSpace(name)) return Bad("Wonach soll ich suchen?");
                    string q = name.Trim();
                    var opt = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 8 };
                    var hits = Directory.EnumerateFileSystemEntries(baseDir, "*", opt)
                        .Where(f => Path.GetFileName(f).Contains(q, StringComparison.OrdinalIgnoreCase)).Take(40)
                        .Select(f => Path.GetRelativePath(baseDir, f).Replace('\\', '/')).ToList();
                    if (hits.Count == 0) return Ok($"Nichts mit „{q}“ in **{ort}** gefunden.", "keine Treffer");
                    return Ok($"**{hits.Count} Treffer** für „{q}“:\n" + string.Join("\n", hits.Take(12).Select(h => "- " + h)), string.Join("\n", hits));
                }

                case "datei_lesen":
                {
                    if (!File.Exists(path)) return Bad($"Die Datei **{shown0}** gibt es nicht.");
                    var fi = new FileInfo(path);
                    if (fi.Length > 400000) return Bad("Die Datei ist zu groß zum Lesen.");
                    var bytes = File.ReadAllBytes(path);
                    if (bytes.Take(4000).Any(b => b == 0)) return Bad("Das ist keine Textdatei, die kann ich nicht lesen.");
                    var text = Encoding.UTF8.GetString(bytes);
                    return Ok($"**{Path.GetFileName(path)}** gelesen ({Size(fi.Length)}).", Cut(text, 12000));
                }

                case "datei_erstellen":
                case "datei_anhaengen":
                {
                    if (isBase) return Bad("Die Datei braucht einen Namen.");
                    var ext = Path.GetExtension(path).ToLowerInvariant();
                    if (ext == "") { path += ".txt"; ext = ".txt"; }
                    if (Blocked.Contains(ext)) return Bad($"Dateien mit **{ext}** schreibe ich aus Sicherheitsgründen nicht.");
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    if (typ == "datei_anhaengen")
                    {
                        File.AppendAllText(path, (File.Exists(path) && new FileInfo(path).Length > 0 ? "\n" : "") + G("inhalt"), new UTF8Encoding(false));
                        return Ok($"In **{Path.GetFileName(path)}** geschrieben.");
                    }
                    string final = Free(path);
                    File.WriteAllText(final, G("inhalt"), new UTF8Encoding(false));
                    return Ok($"Die Datei **{Path.GetFileName(final)}** ist angelegt.");
                }

                case "datei_oeffnen":
                    if (!File.Exists(path)) return Bad($"Die Datei **{shown0}** gibt es nicht.");
                    if (Blocked.Contains(Path.GetExtension(path).ToLowerInvariant())) return Bad("Programme und Skripte starte ich so nicht.");
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                    return Ok($"**{Path.GetFileName(path)}** ist geöffnet.");

                case "umbenennen":
                {
                    if (isBase) return Bad("Was soll ich umbenennen?");
                    var invalid = Path.GetInvalidFileNameChars();
                    var clean = new string(G("neu").Where(c => !invalid.Contains(c)).ToArray()).Trim().TrimEnd('.');
                    if (clean.Length == 0) return Bad("Der neue Name fehlt.");
                    bool isFile = File.Exists(path);
                    if (!isFile && !Directory.Exists(path)) return Bad($"**{shown0}** gibt es nicht.");
                    if (isFile && Path.GetExtension(clean) == "") clean += Path.GetExtension(path);
                    var dest = Path.Combine(Path.GetDirectoryName(path), clean);
                    if (isFile && Blocked.Contains(Path.GetExtension(dest).ToLowerInvariant()) && !Blocked.Contains(Path.GetExtension(path).ToLowerInvariant())) return Bad("Diese Endung vergebe ich nicht.");
                    if (File.Exists(dest) || Directory.Exists(dest)) return Bad($"**{clean}** gibt es dort schon.");
                    if (isFile) File.Move(path, dest); else Directory.Move(path, dest);
                    return Ok($"Heißt jetzt **{clean}**.");
                }

                case "verschieben":
                case "kopieren":
                {
                    if (isBase) return Bad("Was genau meinst du?");
                    bool isFile = File.Exists(path);
                    if (!isFile && !Directory.Exists(path)) return Bad($"**{shown0}** gibt es nicht.");
                    string destDir = Target(G("ziel_ort"), G("ziel"), out string shownDest);
                    if (!isFile && (destDir + Path.DirectorySeparatorChar).StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return Bad("Einen Ordner kann ich nicht in sich selbst legen.");
                    Directory.CreateDirectory(destDir);
                    string dest = Free(Path.Combine(destDir, Path.GetFileName(path)));
                    if (typ == "kopieren") { if (isFile) File.Copy(path, dest); else CopyDir(path, dest); return Ok($"Kopie liegt in **{shownDest}**."); }
                    if (isFile) File.Move(path, dest);
                    else
                    {
                        try { Directory.Move(path, dest); }
                        catch (IOException) { CopyDir(path, dest); Directory.Delete(path, true); }
                    }
                    return Ok($"Liegt jetzt in **{shownDest}**.");
                }

                case "loeschen":
                    if (isBase) return Bad("Ganze Hauptordner lösche ich nicht.");
                    if (File.Exists(path)) Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    else if (Directory.Exists(path)) Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    else return Bad($"**{shown0}** gibt es nicht.");
                    return Ok($"**{shown0}** liegt im Papierkorb.");

                case "zippen":
                {
                    if (isBase) return Bad("Was soll ich packen?");
                    string zip = Free(Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path) + ".zip"));
                    if (Directory.Exists(path)) ZipFile.CreateFromDirectory(path, zip);
                    else if (File.Exists(path))
                    {
                        using var z = ZipFile.Open(zip, ZipArchiveMode.Create);
                        z.CreateEntryFromFile(path, Path.GetFileName(path));
                    }
                    else return Bad($"**{shown0}** gibt es nicht.");
                    return Ok($"**{Path.GetFileName(zip)}** ist fertig.");
                }

                case "aufraeumen":
                {
                    if (!Directory.Exists(path)) return Bad($"**{shown0}** gibt es nicht.");
                    var moved = new Dictionary<string, int>();
                    foreach (var file in Directory.GetFiles(path))
                    {
                        var fn = Path.GetFileName(file);
                        var ext = Path.GetExtension(file).ToLowerInvariant();
                        if (Hidden(fn) || ext == ".lnk" || ext == ".url" || ext == ".tmp" || ext == ".crdownload" || ext == ".part") continue;
                        if ((File.GetAttributes(file) & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                        var group = Groups.FirstOrDefault(g => g.Value.Contains(ext)).Key ?? "Sonstiges";
                        var destDir = Path.Combine(path, group);
                        Directory.CreateDirectory(destDir);
                        try { File.Move(file, Free(Path.Combine(destDir, fn))); moved[group] = moved.TryGetValue(group, out var c) ? c + 1 : 1; } catch { }
                    }
                    if (moved.Count == 0) return Ok($"In **{shown0}** gab es nichts zum Aufräumen.");
                    var sb = new StringBuilder($"**{moved.Values.Sum()} Dateien** einsortiert:\n");
                    foreach (var kv in moved.OrderByDescending(k => k.Value)) sb.Append($"- {kv.Key}: {kv.Value}\n");
                    return Ok(sb.ToString().TrimEnd());
                }
            }
            return Bad("Diesen Auftrag kenne ich noch nicht.");
        }

        // ---------- Automatische Updates über GitHub ----------
        static string Repo()
        {
            try
            {
                using var res = typeof(MainForm).Assembly.GetManifestResourceStream("repo.txt");
                if (res == null) return null;
                using var r = new StreamReader(res);
                return r.ReadToEnd().Trim();
            }
            catch { return null; }
        }

        static HttpClient Http()
        {
            var h = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            h.DefaultRequestHeaders.UserAgent.ParseAdd("Luigi/" + Ver);
            return h;
        }

        async Task CheckUpdate()
        {
            if (checkedUpdate) return;
            checkedUpdate = true;
            string repo = Repo();
            if (string.IsNullOrEmpty(repo)) return;
            try
            {
                using var http = Http();
                var json = await http.GetStringAsync($"https://api.github.com/repos/{repo}/releases/latest");
                using var doc = JsonDocument.Parse(json);
                var m = Regex.Match(doc.RootElement.GetProperty("name").GetString() ?? "", @"(\d+\.\d+\.\d+)");
                if (!m.Success || new Version(m.Groups[1].Value) <= new Version(Ver)) return;
                foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
                    if (a.GetProperty("name").GetString() == "Luigi.exe") updateUrl = a.GetProperty("browser_download_url").GetString();
                if (updateUrl == null) return;
                web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "update", version = m.Groups[1].Value }));
            }
            catch { }
        }

        async Task InstallUpdate()
        {
            try
            {
                if (updateUrl == null) throw new Exception();
                string dir = Path.Combine(BaseDir, "update");
                Directory.CreateDirectory(dir);
                string newExe = Path.Combine(dir, "Luigi.exe");
                using (var http = Http())
                using (var stream = await http.GetStreamAsync(updateUrl))
                using (var file = File.Create(newExe))
                    await stream.CopyToAsync(file);

                string current = Environment.ProcessPath;
                string script = Path.Combine(dir, "update.cmd");
                File.WriteAllText(script,
                    "@echo off\r\nset n=0\r\n:warte\r\ntimeout /t 1 /nobreak >nul\r\n" +
                    $"copy /y \"{newExe}\" \"{current}\" >nul 2>nul\r\n" +
                    "if not errorlevel 1 goto fertig\r\nset /a n+=1\r\nif %n% lss 30 goto warte\r\n:fertig\r\n" +
                    $"start \"\" \"{current}\"\r\ndel \"%~f0\"\r\n", new UTF8Encoding(false));
                Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + script + "\"") { CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, UseShellExecute = false });
                Application.Exit();
            }
            catch
            {
                try { web.CoreWebView2.PostWebMessageAsJson("{\"type\":\"update_fail\"}"); } catch { }
            }
        }

        static void Open(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }
    }
}
