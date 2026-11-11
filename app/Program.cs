using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
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
            core.Navigate("https://" + Host + "/index.html");
        }

        static void Open(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }
    }
}
