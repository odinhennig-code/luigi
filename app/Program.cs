using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Collections.Generic;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
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
        static readonly string Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        static readonly string BaseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Luigi");
        string updateUrl, updateVersion;
        readonly WebView2 web;
        readonly bool dark;

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public MainForm()
        {
            dark = IsDarkMode();
            Text = "Luigi";
            Width = 1280;
            Height = 820;
            MinimumSize = new Size(420, 560);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = dark ? Color.FromArgb(19, 19, 20) : Color.White;
            web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
            Controls.Add(web);
            Load += async (s, e) => await Start();
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

        async Task Start()
        {
            string baseDir = BaseDir;
            string appDir = Path.Combine(baseDir, "app");
            Directory.CreateDirectory(appDir);
            using (var res = typeof(MainForm).Assembly.GetManifestResourceStream("index.html"))
            using (var file = File.Create(Path.Combine(appDir, "index.html")))
                res.CopyTo(file);

            try
            {
                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(baseDir, "WebView"));
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
                if (!e.Uri.StartsWith("https://" + Host + "/", StringComparison.OrdinalIgnoreCase))
                {
                    e.Cancel = true;
                    Open(e.Uri);
                }
            };
            try { await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache | CoreWebView2BrowsingDataKinds.CacheStorage); } catch { }
            await core.AddScriptToExecuteOnDocumentCreatedAsync("window.LUIGI_VERSION=" + JsonSerializer.Serialize(Version) + ";");
            core.WebMessageReceived += OnMessage;
            core.NavigationCompleted += async (s, e) => await CheckUpdate();
            core.Navigate("https://" + Host + "/index.html?v=" + Version + "-" + DateTime.UtcNow.Ticks);
        }

        // ---------- Aufträge vom Chat auf dem PC ausführen ----------
        async void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string id = "", typ = "", ort = "", name = "", url = "", inhalt = "", neu = "";
            try
            {
                using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
                var r = doc.RootElement;
                string Get(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
                id = Get("id"); typ = Get("typ"); ort = Get("ort"); name = Get("name"); url = Get("url"); inhalt = Get("inhalt"); neu = Get("neu");
            }
            catch { return; }

            if (typ == "update_installieren") { await InstallUpdate(); return; }

            (bool ok, string msg) result;
            try { result = await Task.Run(() => Run(typ, ort, name, url, inhalt, neu)); }
            catch (Exception ex) { result = (false, "Fehler: " + ex.Message); }

            var json = JsonSerializer.Serialize(new { id, ok = result.ok, msg = result.msg });
            try { web.CoreWebView2.PostWebMessageAsJson(json); } catch { }
        }

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
            string baseDir = BaseFolder(ort);
            var invalid = Path.GetInvalidFileNameChars();
            var parts = (name ?? "").Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => new string(p.Where(c => !invalid.Contains(c)).ToArray()).Trim().TrimEnd('.'))
                .Where(p => p.Length > 0 && p != "." && p != "..").ToArray();
            string full = Path.GetFullPath(Path.Combine(new[] { baseDir }.Concat(parts).ToArray()));
            if (!full.StartsWith(Path.GetFullPath(baseDir), StringComparison.OrdinalIgnoreCase)) throw new Exception("Dieser Ort ist nicht erlaubt.");
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

        static (bool, string) FindProgram(string name)
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
                return (true, $"**{name}** ist geöffnet.");
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
            if (best == null) return (false, $"Ich habe kein Programm namens **{name}** gefunden.");
            Process.Start(new ProcessStartInfo(best) { UseShellExecute = true });
            return (true, $"**{Path.GetFileNameWithoutExtension(best)}** ist geöffnet.");
        }

        static (bool, string) Run(string typ, string ort, string name, string url, string inhalt, string neu)
        {
            if (typ == "webseite_oeffnen")
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http")) return (false, "Diese Adresse kann ich nicht öffnen.");
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                return (true, $"**{uri.Host}** ist geöffnet.");
            }
            if (typ == "programm_oeffnen")
            {
                if (string.IsNullOrWhiteSpace(name)) return (false, "Welches Programm denn?");
                return FindProgram(name);
            }
            string path = Target(ort, name, out string shown);
            switch (typ)
            {
                case "datei_erstellen":
                {
                    if (string.IsNullOrWhiteSpace(name)) return (false, "Die Datei braucht einen Namen.");
                    var ext = Path.GetExtension(path).ToLowerInvariant();
                    if (ext == "") { path += ".txt"; shown += ".txt"; ext = ".txt"; }
                    if (Blocked.Contains(ext)) return (false, $"Dateien mit **{ext}** lege ich aus Sicherheitsgründen nicht an.");
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    string final = path; int n = 2;
                    while (File.Exists(final)) final = Path.Combine(Path.GetDirectoryName(path), $"{Path.GetFileNameWithoutExtension(path)} ({n++}){ext}");
                    File.WriteAllText(final, inhalt ?? "", new UTF8Encoding(false));
                    return (true, $"Die Datei **{Path.GetFileName(final)}** ist angelegt.");
                }

                case "umbenennen":
                {
                    var invalid = Path.GetInvalidFileNameChars();
                    var clean = new string((neu ?? "").Where(c => !invalid.Contains(c)).ToArray()).Trim().TrimEnd('.');
                    if (clean.Length == 0) return (false, "Der neue Name fehlt.");
                    var dest = Path.Combine(Path.GetDirectoryName(path), clean);
                    if (File.Exists(path))
                    {
                        if (Blocked.Contains(Path.GetExtension(dest).ToLowerInvariant()) && !Blocked.Contains(Path.GetExtension(path).ToLowerInvariant())) return (false, "Diese Endung vergebe ich nicht.");
                        if (File.Exists(dest) || Directory.Exists(dest)) return (false, $"**{clean}** gibt es dort schon.");
                        File.Move(path, dest);
                    }
                    else if (Directory.Exists(path))
                    {
                        if (File.Exists(dest) || Directory.Exists(dest)) return (false, $"**{clean}** gibt es dort schon.");
                        Directory.Move(path, dest);
                    }
                    else return (false, $"**{shown}** gibt es nicht.");
                    return (true, $"Heißt jetzt **{clean}**.");
                }

                case "ordner_anlegen":
                    if (string.IsNullOrWhiteSpace(name)) return (false, "Der Ordner braucht einen Namen.");
                    if (Directory.Exists(path)) return (true, $"Den Ordner **{shown}** gibt es schon.");
                    Directory.CreateDirectory(path);
                    return (true, $"Der Ordner **{shown}** ist angelegt.");

                case "ordner_oeffnen":
                    if (!Directory.Exists(path)) return (false, $"**{shown}** gibt es nicht.");
                    Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
                    return (true, $"**{shown}** ist geöffnet.");

                case "dateien_auflisten":
                {
                    if (!Directory.Exists(path)) return (false, $"**{shown}** gibt es nicht.");
                    var dirs = Directory.GetDirectories(path).Select(Path.GetFileName).Where(n => !n.StartsWith(".")).OrderBy(n => n).ToList();
                    var files = Directory.GetFiles(path).Select(Path.GetFileName).Where(n => !n.StartsWith(".") && !n.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)).OrderBy(n => n).ToList();
                    if (dirs.Count + files.Count == 0) return (true, $"**{shown}** ist leer.");
                    var sb = new StringBuilder($"In **{shown}**: {dirs.Count} Ordner, {files.Count} Dateien\n");
                    foreach (var d in dirs.Take(20)) sb.Append("- ").Append(d).Append(" (Ordner)\n");
                    foreach (var f in files.Take(30)) sb.Append("- ").Append(f).Append('\n');
                    if (dirs.Count > 20 || files.Count > 30) sb.Append("- … und noch mehr\n");
                    return (true, sb.ToString().TrimEnd());
                }

                case "aufraeumen":
                {
                    if (!Directory.Exists(path)) return (false, $"**{shown}** gibt es nicht.");
                    var moved = new Dictionary<string, int>();
                    foreach (var file in Directory.GetFiles(path))
                    {
                        var fn = Path.GetFileName(file);
                        var ext = Path.GetExtension(file).ToLowerInvariant();
                        if (fn.StartsWith(".") || fn.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) || ext == ".lnk" || ext == ".url" || ext == ".tmp" || ext == ".crdownload" || ext == ".part") continue;
                        if ((File.GetAttributes(file) & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                        var group = Groups.FirstOrDefault(g => g.Value.Contains(ext)).Key ?? "Sonstiges";
                        var destDir = Path.Combine(path, group);
                        Directory.CreateDirectory(destDir);
                        var dest = Path.Combine(destDir, fn);
                        int n = 2;
                        while (File.Exists(dest)) dest = Path.Combine(destDir, $"{Path.GetFileNameWithoutExtension(fn)} ({n++}){ext}");
                        try { File.Move(file, dest); moved[group] = moved.TryGetValue(group, out var c) ? c + 1 : 1; } catch { }
                    }
                    if (moved.Count == 0) return (true, $"In **{shown}** gab es nichts zum Aufräumen.");
                    var sb = new StringBuilder($"**{moved.Values.Sum()} Dateien** einsortiert:\n");
                    foreach (var kv in moved.OrderByDescending(k => k.Value)) sb.Append($"- {kv.Key}: {kv.Value}\n");
                    return (true, sb.ToString().TrimEnd());
                }
            }
            return (false, "Diesen Auftrag kenne ich noch nicht.");
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
            h.DefaultRequestHeaders.UserAgent.ParseAdd("Luigi/" + Version);
            return h;
        }

        bool checkedUpdate;
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
                var name = doc.RootElement.GetProperty("name").GetString() ?? "";
                var m = System.Text.RegularExpressions.Regex.Match(name, @"(\d+\.\d+\.\d+)");
                if (!m.Success) return;
                if (new System.Version(m.Groups[1].Value) <= new System.Version(Version)) return;
                foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
                    if (a.GetProperty("name").GetString() == "Luigi.exe") updateUrl = a.GetProperty("browser_download_url").GetString();
                if (updateUrl == null) return;
                updateVersion = m.Groups[1].Value;
                web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "update", version = updateVersion }));
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
                    "@echo off\r\n" +
                    "set n=0\r\n" +
                    ":warte\r\n" +
                    "timeout /t 1 /nobreak >nul\r\n" +
                    $"copy /y \"{newExe}\" \"{current}\" >nul 2>nul\r\n" +
                    "if not errorlevel 1 goto fertig\r\n" +
                    "set /a n+=1\r\n" +
                    "if %n% lss 30 goto warte\r\n" +
                    ":fertig\r\n" +
                    $"start \"\" \"{current}\"\r\n" +
                    "del \"%~f0\"\r\n", new UTF8Encoding(false));
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
