using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;
using OpenDriverUpdater.Models;
using OpenDriverUpdater.Services;

namespace OpenDriverUpdater;

/// <summary>
/// Ventana principal: aloja la interfaz (HTML/CSS/JS en la carpeta ui/, embebida en el .exe)
/// dentro de WebView2 y hace de puente con el motor de drivers en C#.
/// </summary>
public sealed class MainForm : Form
{
    private const string HostName = "app.opendriverupdater.example";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly UpdateEngine _engine = new();
    private List<DeviceRow> _rows = new();
    private bool _busy;
    private bool _started;

    public MainForm()
    {
        Text = "OpenDriverUpdater";
        Width = 1200;
        Height = 800;
        MinimumSize = new Size(720, 520);
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { /* sin icono */ }

        ApplyTheme();
        Controls.Add(_web);

        Load += async (_, _) => await InitWebViewAsync();
        FormClosing += OnFormClosing;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.ApplyWindowStyle(Handle, IsDarkTheme(), BackColor);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        base.Dispose(disposing);
    }

    // ───────────────────────── Tema ─────────────────────────

    private static bool IsDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    /// <summary>El fondo de la ventana y la barra de título coinciden con el de la interfaz (sin destellos).</summary>
    private void ApplyTheme()
    {
        BackColor = IsDarkTheme() ? Color.FromArgb(28, 28, 30) : Color.FromArgb(245, 245, 247);
        _web.DefaultBackgroundColor = BackColor;
        if (IsHandleCreated) Native.ApplyWindowStyle(Handle, IsDarkTheme(), BackColor);
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General && !IsDisposed)
            BeginInvoke(new Action(ApplyTheme));
    }

    // ───────────────────────── WebView2 ─────────────────────────

    private async Task InitWebViewAsync()
    {
        try
        {
            var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenDriverUpdater");
            var uiDir = ExtractUi(dataDir);

            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(dataDir, "WebView2"));
            await _web.EnsureCoreWebView2Async(env);

            var core = _web.CoreWebView2;
            var s = core.Settings;
            s.AreDefaultContextMenusEnabled = false;
            s.IsStatusBarEnabled = false;
            s.IsZoomControlEnabled = false;
            s.AreBrowserAcceleratorKeysEnabled = false;
            s.IsGeneralAutofillEnabled = false;
            s.IsPasswordAutosaveEnabled = false;
#if !DEBUG
            s.AreDevToolsEnabled = false;
#endif
            core.SetVirtualHostNameToFolderMapping(HostName, uiDir, CoreWebView2HostResourceAccessKind.Deny);
            core.WebMessageReceived += OnWebMessage;
            core.NewWindowRequested += (_, e) => { e.Handled = true; OpenUrl(e.Uri); };
            core.NavigationStarting += (_, e) =>
            {
                // La interfaz nunca navega fuera de sí misma; los enlaces se abren en el navegador del usuario
                if (!e.Uri.StartsWith($"https://{HostName}/", StringComparison.OrdinalIgnoreCase))
                {
                    e.Cancel = true;
                    OpenUrl(e.Uri);
                }
            };

            core.Navigate($"https://{HostName}/index.html");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            var r = MessageBox.Show(this,
                "Falta el componente Microsoft Edge WebView2, necesario para mostrar la interfaz.\n\n" +
                "Viene incluido en Windows 11 y en Windows 10 actualizado. ¿Abrir la página de descarga oficial de Microsoft?",
                "OpenDriverUpdater", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r == DialogResult.Yes) OpenUrl("https://developer.microsoft.com/microsoft-edge/webview2/");
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "No se pudo iniciar la interfaz:\n\n" + ex.Message, "OpenDriverUpdater",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    /// <summary>Copia la interfaz embebida en el .exe a una carpeta local para servirla.</summary>
    private static string ExtractUi(string dataDir)
    {
        var asm = Assembly.GetExecutingAssembly();
        var version = asm.GetName().Version?.ToString() ?? "0";
        var dir = Path.Combine(dataDir, "ui", version);
        Directory.CreateDirectory(dir);
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("ui/", StringComparison.Ordinal)))
        {
            using var src = asm.GetManifestResourceStream(name)!;
            using var dst = File.Create(Path.Combine(dir, name["ui/".Length..]));
            src.CopyTo(dst);
        }
        return dir;
    }

    private void Post(object message)
    {
        if (IsDisposed || _web.CoreWebView2 is null) return;
        if (InvokeRequired) { BeginInvoke(new Action(() => Post(message))); return; }
        _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, Json));
    }

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var msg = doc.RootElement;
            switch (msg.GetProperty("type").GetString())
            {
                case "ready":
                    if (!_started) { _started = true; await ScanAsync(); }
                    else if (_rows.Count > 0) PostScanResult(); // la página se recargó
                    break;
                case "scan":
                    await ScanAsync();
                    break;
                case "install":
                    var ids = msg.GetProperty("ids").EnumerateArray().Select(x => x.GetInt32()).ToList();
                    bool restorePoint = msg.TryGetProperty("restorePoint", out var rp) && rp.GetBoolean();
                    await InstallAsync(ids, restorePoint);
                    break;
                case "export":
                    Export();
                    break;
                case "openUrl":
                    OpenUrl(msg.GetProperty("url").GetString());
                    break;
                case "restart":
                    SystemActions.RestartNow();
                    break;
            }
        }
        catch (Exception ex)
        {
            Post(new { type = "error", message = ex.Message });
        }
    }

    // ───────────────────────── Análisis ─────────────────────────

    private async Task ScanAsync()
    {
        if (_busy) return;
        _busy = true;
        Post(new { type = "scanStarted" });
        var progress = new Progress<ScanProgress>(p =>
            Post(new { type = "scanProgress", phase = p.Phase, message = p.Message, done = p.Done, total = p.Total }));
        try
        {
            _rows = await _engine.ScanAsync(progress, CancellationToken.None);
            PostScanResult();
        }
        catch (Exception ex)
        {
            Post(new { type = "scanError", message = ex.Message + " Comprueba tu conexión a Internet." });
        }
        finally
        {
            _busy = false;
        }
    }

    private void PostScanResult()
    {
        var warnings = new List<string>();
        if (_engine.WindowsUpdateError is { } wu) warnings.Add("Windows Update no respondió: " + wu);
        if (_engine.CatalogErrors > 0) warnings.Add($"{_engine.CatalogErrors} consultas al Catálogo de Microsoft fallaron (conexión)");
        if (_engine.NvidiaError is { } nv) warnings.Add("NVIDIA: " + nv);

        Post(new { type = "scanResult", rows = _rows.Select((r, i) => ToDto(r, i)).ToList(), warnings });
    }

    private static object ToDto(DeviceRow r, int id)
    {
        var d = r.Device;
        var l = r.Latest;
        return new
        {
            id,
            name = d.Name,
            deviceClass = d.DeviceClass,
            provider = d.Provider,
            hasDriver = d.HasDriver,
            installedVersion = d.Version,
            installedDate = d.Date?.ToString("dd/MM/yyyy") ?? "",
            nvidiaVersion = d.HasDriver && NvidiaService.IsNvidiaGpu(d) ? VersionUtil.NvidiaFromWindows(d.Version) ?? "" : "",
            infName = d.InfName,
            hardwareIds = d.HardwareIds.Take(6).ToList(),
            latestVersion = l?.Version ?? "",
            latestDate = l?.Date?.ToString("dd/MM/yyyy") ?? "",
            latestTitle = l?.Title ?? "",
            source = l?.SourceName ?? "",
            sourceKey = l?.Source switch
            {
                DriverSource.Nvidia => "nvidia",
                DriverSource.WindowsUpdate => "wu",
                DriverSource.MicrosoftCatalog => "catalog",
                _ => "",
            },
            match = l is null ? "" : l.Match == MatchQuality.Exact ? "exact" : "generic",
            infoUrl = l?.InfoUrl ?? "",
            sizeBytes = l?.SizeBytes ?? 0,
            status = r.Status switch
            {
                DeviceStatus.UpdateAvailable => "update",
                DeviceStatus.MissingDriver => "missing",
                DeviceStatus.VendorDriverAvailable => "vendor",
                DeviceStatus.UpToDate => "ok",
                DeviceStatus.NotFound => "none",
                _ => "builtin",
            },
            statusText = r.StatusText,
            canUpdate = r.CanUpdate,
            recommended = r.Recommended,
            others = r.AllCandidates
                .Where(c => c != l)
                .OrderByDescending(c => VersionUtil.Parse(c.Version) ?? new Version(0, 0))
                .Take(5)
                .Select(c => new { version = c.Version, source = c.SourceName, title = c.Title })
                .ToList(),
        };
    }

    // ───────────────────────── Instalación ─────────────────────────

    private async Task InstallAsync(List<int> ids, bool restorePoint)
    {
        if (_busy) return;
        _busy = true;
        int ok = 0, failed = 0;
        bool reboot = false;
        try
        {
            if (restorePoint)
            {
                Post(new { type = "installStatus", message = "Creando punto de restauración…" });
                if (!await Task.Run(() => SystemActions.TryCreateRestorePoint("Antes de OpenDriverUpdater")))
                    Post(new { type = "installStatus", message = "No se pudo crear el punto de restauración (puede estar desactivado). Continuando…" });
            }

            var valid = ids.Where(i => i >= 0 && i < _rows.Count && _rows[i].CanUpdate).ToList();
            for (int index = 0; index < valid.Count; index++)
            {
                int id = valid[index];
                int current = index;
                var progress = new Progress<string>(m =>
                    Post(new { type = "installProgress", id, index = current, total = valid.Count, message = m }));
                Post(new { type = "installProgress", id, index = current, total = valid.Count, message = "Preparando…" });

                var outcome = await _engine.InstallAsync(_rows[id], progress, CancellationToken.None);
                if (outcome.Ok) ok++; else failed++;
                reboot |= outcome.RebootRequired;

                Post(new
                {
                    type = "installItem",
                    id,
                    ok = outcome.Ok,
                    status = outcome.Status == InstallStatus.NotApplied ? "notApplied" : outcome.Ok ? "succeeded" : "failed",
                    message = outcome.Message,
                });
            }
            reboot |= WindowsUpdateService.IsRebootPending();
        }
        catch (Exception ex)
        {
            Post(new { type = "error", message = "Error durante la actualización: " + ex.Message });
        }
        finally
        {
            _busy = false;
            Post(new { type = "installDone", reboot, okCount = ok, failCount = failed });
        }
    }

    // ───────────────────────── Otras acciones ─────────────────────────

    private void Export()
    {
        if (_rows.Count == 0) return;
        var fileName = $"OpenDriverUpdater-informe-{DateTime.Now:yyyyMMdd-HHmm}.txt";
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), fileName);
        try
        {
            File.WriteAllText(path, _engine.BuildReport(_rows));
            Post(new { type = "exported", fileName });
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Post(new { type = "error", message = "No se pudo guardar el informe: " + ex.Message });
        }
    }

    private static void OpenUrl(string? url)
    {
        if (url is null || !(url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
            return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { /* sin navegador */ }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_busy && e.CloseReason == CloseReason.UserClosing)
        {
            var r = MessageBox.Show(this,
                "Hay una operación en curso. Cerrar durante una instalación de drivers no es recomendable.\n\n¿Cerrar de todos modos?",
                "OpenDriverUpdater", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) e.Cancel = true;
        }
    }
}
