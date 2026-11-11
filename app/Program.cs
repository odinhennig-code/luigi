using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Collections.Generic;
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
            string baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Luigi");
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
            core.WebMessageReceived += OnMessage;
            core.Navigate("https://" + Host + "/index.html");
        }

        // ---------- Aufträge vom Chat auf dem PC ausführen ----------
        async void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string id = "", typ = "", ort = "", name = "";
            try
            {
                using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
                var r = doc.RootElement;
                string Get(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
                id = Get("id"); typ = Get("typ"); ort = Get("ort"); name = Get("name");
            }
            catch { return; }

            (bool ok, string msg) result;
            try { result = await Task.Run(() => Run(typ, ort, name)); }
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

        static (bool, string) Run(string typ, string ort, string name)
        {
            string path = Target(ort, name, out string shown);
            switch (typ)
            {
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

        static void Open(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }
    }
}
