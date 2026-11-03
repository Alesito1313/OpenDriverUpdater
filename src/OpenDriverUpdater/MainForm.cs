using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;
using OpenDriverUpdater.Core;

namespace OpenDriverUpdater
{
    /// <summary>
    /// La ventana. La interfaz es HTML (carpeta ui/, va dentro del exe) dentro de WebView2,
    /// y se habla con ella por mensajes JSON.
    /// </summary>
    public class MainForm : Form
    {
        const string Host = "app.opendriverupdater.example";

        readonly WebView2 web = new WebView2 { Dock = DockStyle.Fill };
        readonly Engine engine = new Engine();
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        List<Row> rows = new List<Row>();
        bool busy, started;
        readonly List<string> jsErrors = new List<string>();

        public MainForm()
        {
            Lang.Load();
            Text = "OpenDriverUpdater";
            Size = new Size(1200, 800);
            MinimumSize = new Size(720, 520);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            ApplyTheme();
            Controls.Add(web);

            Load += async (s, e) => await StartWebView();
            if (Program.SelfTestFile != null) StartSelfTestTimeout();
            FormClosing += OnClosing;
            SystemEvents.UserPreferenceChanged += OnThemeChanged;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.StyleWindow(Handle, IsDark(), BackColor);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) SystemEvents.UserPreferenceChanged -= OnThemeChanged;
            base.Dispose(disposing);
        }

        // tema claro/oscuro: el fondo de la ventana es el mismo que el de la web para que no haya destello blanco

        static bool IsDark()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch { return false; }
        }

        void ApplyTheme()
        {
            BackColor = IsDark() ? Color.FromArgb(28, 28, 30) : Color.FromArgb(245, 245, 247);
            web.DefaultBackgroundColor = BackColor;
            if (IsHandleCreated) Native.StyleWindow(Handle, IsDark(), BackColor);
        }

        void OnThemeChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category == UserPreferenceCategory.General && !IsDisposed) BeginInvoke(new Action(ApplyTheme));
        }

        async Task StartWebView()
        {
            try
            {
                var bin = Path.Combine(Program.DataDir, "bin", Program.Version);
                Program.ExtractResource("lib/WebView2Loader.dll", Path.Combine(bin, "WebView2Loader.dll"));
                CoreWebView2Environment.SetLoaderDllFolderPath(bin);

                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Program.DataDir, "WebView2"));
                await web.EnsureCoreWebView2Async(env);

                var core = web.CoreWebView2;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                core.Settings.IsGeneralAutofillEnabled = false;
                core.Settings.IsPasswordAutosaveEnabled = false;
#if !DEBUG
                core.Settings.AreDevToolsEnabled = false;
#endif
                core.SetVirtualHostNameToFolderMapping(Host, ExtractUi(), CoreWebView2HostResourceAccessKind.Deny);
                core.WebMessageReceived += OnMessage;
                core.NewWindowRequested += (s, e) => { e.Handled = true; OpenUrl(e.Uri); };
                core.NavigationStarting += (s, e) =>
                {
                    // la interfaz no navega a ningun sitio; los enlaces van al navegador normal
                    if (!e.Uri.StartsWith("https://" + Host + "/", StringComparison.OrdinalIgnoreCase))
                    {
                        e.Cancel = true;
                        OpenUrl(e.Uri);
                    }
                };
                core.Navigate("https://" + Host + "/index.html");
            }
            catch (Exception ex) when (Program.SelfTestFile != null)
            {
                WriteSelfTest(false, 0, ex.ToString());
            }
            catch (WebView2RuntimeNotFoundException)
            {
                if (MessageBox.Show(this, Lang.Get("noWebView"), "OpenDriverUpdater", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                    OpenUrl("https://developer.microsoft.com/microsoft-edge/webview2/");
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, Lang.Get("uiFailed", ex.Message), "OpenDriverUpdater", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
        }

        static string ExtractUi()
        {
            var dir = Path.Combine(Program.DataDir, "ui", Program.Version);
            foreach (var name in Assembly.GetExecutingAssembly().GetManifestResourceNames().Where(n => n.StartsWith("ui/")))
            {
                var path = Path.Combine(dir, name.Substring(3));
                if (File.Exists(path)) File.Delete(path); // por si se edito a mano
                Program.ExtractResource(name, path);
            }
            return dir;
        }

        void Send(object msg)
        {
            if (IsDisposed || web.CoreWebView2 == null) return;
            if (InvokeRequired) { BeginInvoke(new Action(() => Send(msg))); return; }
            web.CoreWebView2.PostWebMessageAsJson(json.Serialize(msg));
        }

        async void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var msg = json.Deserialize<Dictionary<string, object>>(e.WebMessageAsJson);
                switch (msg["type"] as string)
                {
                    case "ready":
                        Send(new { type = "init", lang = Lang.Current, version = Program.Version });
                        if (!started) { started = true; await Scan(); }
                        else if (rows.Count > 0) SendResult(); // se recargo la pagina
                        break;
                    case "lang":
                        Lang.Set(msg["lang"] as string);
                        break;
                    case "scan":
                        await Scan();
                        break;
                    case "install":
                        // JavaScriptSerializer devuelve los arrays como ArrayList
                        var ids = ((System.Collections.IEnumerable)msg["ids"]).Cast<object>().Select(Convert.ToInt32).ToList();
                        await Install(ids, msg.TryGetValue("restorePoint", out var rp) && rp is bool b && b);
                        break;
                    case "export":
                        Export();
                        break;
                    case "openUrl":
                        OpenUrl(msg["url"] as string);
                        break;
                    case "restart":
                        SystemTools.Restart();
                        break;
                    case "jsError":
                        jsErrors.Add(msg["message"] as string);
                        break;
                    case "rendered":
                        if (Program.SelfTestFile != null) FinishSelfTest(Convert.ToInt32(msg["count"]));
                        break;
                }
            }
            catch (Exception ex)
            {
                Send(new { type = "error", message = ex.Message });
            }
        }

        async Task Scan()
        {
            if (busy) return;
            busy = true;
            Send(new { type = "scanStarted" });
            var progress = new Progress<ScanProgress>(p =>
                Send(new { type = "scanProgress", phase = p.Phase, device = p.Device ?? "", done = p.Done, total = p.Total }));
            try
            {
                rows = await engine.Scan(progress, CancellationToken.None);
                SendResult();
            }
            catch (Exception ex)
            {
                if (Program.SelfTestFile != null) { WriteSelfTest(false, 0, ex.ToString()); return; }
                Send(new { type = "scanError", message = Lang.Get("scanFailed", ex.Message) });
            }
            finally
            {
                busy = false;
            }
        }

        void SendResult()
        {
            Send(new { type = "scanResult", rows = rows.Select(ToJson).ToList(), warnings = engine.Warnings() });
        }

        static object ToJson(Row r, int id)
        {
            var d = r.Device;
            var l = r.Latest;
            return new
            {
                id,
                name = d.Name,
                deviceClass = d.Class,
                provider = d.Provider,
                hasDriver = d.HasDriver,
                installedVersion = d.Version,
                installedDate = d.Date?.ToString("dd/MM/yyyy") ?? "",
                nvidiaVersion = d.HasDriver && Nvidia.IsNvidiaGpu(d) ? Versions.NvidiaFromWindows(d.Version) ?? "" : "",
                infName = d.Inf,
                hardwareIds = d.HardwareIds.Take(6).ToList(),
                latestVersion = l?.Version ?? "",
                latestDate = l?.Date?.ToString("dd/MM/yyyy") ?? "",
                latestTitle = l?.Title ?? "",
                sourceKey = l?.SourceKey ?? "",
                match = l == null ? "" : l.Match == MatchKind.Exact ? "exact" : "generic",
                infoUrl = l?.InfoUrl ?? "",
                sizeBytes = l?.Size ?? 0,
                status = r.StatusKey,
                canUpdate = r.CanUpdate,
                recommended = r.Recommended,
                others = r.Candidates.Where(c => c != l)
                    .OrderByDescending(c => Versions.Parse(c.Version) ?? new Version(0, 0))
                    .Take(5)
                    .Select(c => new { version = c.Version, sourceKey = c.SourceKey, title = c.Title })
                    .ToList(),
            };
        }

        async Task Install(List<int> ids, bool restorePoint)
        {
            if (busy) return;
            busy = true;
            int ok = 0, failed = 0;
            bool reboot = false;
            try
            {
                if (restorePoint)
                {
                    Send(new { type = "installStatus", message = Lang.Get("restorePoint") });
                    if (!await Task.Run(() => SystemTools.CreateRestorePoint("OpenDriverUpdater")))
                        Send(new { type = "installStatus", message = Lang.Get("restorePointFailed") });
                }

                var todo = ids.Where(i => i >= 0 && i < rows.Count && rows[i].CanUpdate).ToList();
                for (int n = 0; n < todo.Count; n++)
                {
                    int id = todo[n], index = n;
                    var progress = new Progress<string>(m => Send(new { type = "installProgress", id, index, total = todo.Count, message = m }));
                    Send(new { type = "installProgress", id, index, total = todo.Count, message = Lang.Get("preparing") });

                    var result = await engine.Install(rows[id], progress, CancellationToken.None);
                    if (result.Ok) ok++; else failed++;
                    reboot |= result.Reboot;

                    Send(new
                    {
                        type = "installItem",
                        id,
                        ok = result.Ok,
                        status = result.State == InstallState.NotApplied ? "notApplied" : result.Ok ? "succeeded" : "failed",
                        message = result.Message,
                    });
                }
                reboot |= WindowsUpdate.RebootPending();
            }
            catch (Exception ex)
            {
                Send(new { type = "error", message = Lang.Get("installError", ex.Message) });
            }
            finally
            {
                busy = false;
                Send(new { type = "installDone", reboot, okCount = ok, failCount = failed });
            }
        }

        void Export()
        {
            if (rows.Count == 0) return;
            var file = "OpenDriverUpdater-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".txt";
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), file);
            try
            {
                File.WriteAllText(path, engine.Report(rows));
                Send(new { type = "exported", fileName = file });
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Send(new { type = "error", message = Lang.Get("exportFailed", ex.Message) });
            }
        }

        static void OpenUrl(string url)
        {
            if (url == null || !(url.StartsWith("https://") || url.StartsWith("http://"))) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        // prueba automatica

        void StartSelfTestTimeout()
        {
            var timer = new System.Windows.Forms.Timer { Interval = 6 * 60 * 1000 };
            timer.Tick += (s, e) => { timer.Stop(); WriteSelfTest(false, 0, "timeout"); };
            timer.Start();
        }

        void FinishSelfTest(int renderedRows)
        {
            bool ok = rows.Count > 0 && renderedRows > 0 && jsErrors.Count == 0;
            WriteSelfTest(ok, renderedRows, null);
        }

        void WriteSelfTest(bool ok, int renderedRows, string error)
        {
            var result = new Dictionary<string, object>
            {
                ["ok"] = ok,
                ["error"] = error,
                ["devices"] = rows.Count,
                ["renderedRows"] = renderedRows,
                ["updates"] = rows.Count(r => r.CanUpdate),
                ["upToDate"] = rows.Count(r => r.Status == Status.UpToDate),
                ["withCatalogResults"] = rows.Count(r => r.Candidates.Any(c => c.Source == Source.Catalog)),
                ["windowsUpdateError"] = engine.WuError,
                ["catalogErrors"] = engine.CatalogErrors,
                ["jsErrors"] = jsErrors,
                ["sample"] = rows.Take(15).Select(r => r.Device.Name + " | " + r.Device.Version + " -> " + (r.Latest?.Version ?? "-") + " | " + r.StatusKey).ToList(),
            };
            File.WriteAllText(Program.SelfTestFile, json.Serialize(result));
            busy = false;
            Close();
        }

        void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (busy && e.CloseReason == CloseReason.UserClosing &&
                MessageBox.Show(this, Lang.Get("busyClose"), "OpenDriverUpdater", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                e.Cancel = true;
        }
    }
}
