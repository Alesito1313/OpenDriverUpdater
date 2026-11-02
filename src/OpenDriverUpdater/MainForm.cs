using OpenDriverUpdater.Models;
using OpenDriverUpdater.Services;

namespace OpenDriverUpdater;

/// <summary>
/// Ventana principal. Flujo:
/// 1) Al abrir, escanea los drivers instalados.
/// 2) Busca versiones más nuevas en Windows Update.
/// 3) El usuario marca cuáles quiere actualizar.
/// 4) Descarga + instala, y pregunta si reiniciar.
/// </summary>
public sealed class MainForm : Form
{
    private readonly Label _header = new()
    {
        Text = "Buscando drivers…",
        Dock = DockStyle.Top,
        Height = 44,
        Font = new Font("Segoe UI Semibold", 13f),
        Padding = new Padding(12, 10, 12, 0),
    };

    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly ListView _updatesList = CreateList(checkBoxes: true);
    private readonly ListView _installedList = CreateList(checkBoxes: false);

    private readonly CheckBox _restorePoint = new()
    {
        Text = "Crear punto de restauración antes de instalar",
        Checked = true,
        AutoSize = true,
        Margin = new Padding(0, 8, 16, 0),
    };

    private readonly Button _rescanButton = new() { Text = "Buscar de nuevo", AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
    private readonly Button _selectAllButton = new() { Text = "Seleccionar todo", AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
    private readonly Button _updateButton = new()
    {
        Text = "Actualizar seleccionados",
        AutoSize = true,
        Padding = new Padding(12, 4, 12, 4),
        Font = new Font("Segoe UI Semibold", 10f),
        Enabled = false,
    };

    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Style = ProgressBarStyle.Marquee };
    private readonly Label _status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };

    private WindowsUpdateService? _wu;
    private List<InstalledDriver> _installed = new();
    private List<DriverUpdate> _available = new();
    private bool _busy;

    public MainForm()
    {
        Text = "OpenDriverUpdater";
        Width = 1050;
        Height = 640;
        MinimumSize = new Size(760, 420);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);

        // Columnas
        _updatesList.Columns.Add("Dispositivo", 300);
        _updatesList.Columns.Add("Fabricante", 130);
        _updatesList.Columns.Add("Tipo", 110);
        _updatesList.Columns.Add("Versión actual", 130);
        _updatesList.Columns.Add("Versión nueva", 130);
        _updatesList.Columns.Add("Fecha nueva", 95);
        _updatesList.Columns.Add("Tamaño", 80);

        _installedList.Columns.Add("Dispositivo", 340);
        _installedList.Columns.Add("Tipo", 120);
        _installedList.Columns.Add("Fabricante", 160);
        _installedList.Columns.Add("Versión", 140);
        _installedList.Columns.Add("Fecha", 95);
        _installedList.Columns.Add("INF", 110);

        var updatesTab = new TabPage("Actualizaciones disponibles");
        updatesTab.Controls.Add(_updatesList);
        var installedTab = new TabPage("Drivers instalados");
        installedTab.Controls.Add(_installedList);
        _tabs.TabPages.Add(updatesTab);
        _tabs.TabPages.Add(installedTab);

        // Barra inferior: botones
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 52,
            Padding = new Padding(10, 10, 10, 6),
            FlowDirection = FlowDirection.LeftToRight,
        };
        buttons.Controls.AddRange(new Control[] { _rescanButton, _selectAllButton, _restorePoint, _updateButton });

        // Barra de estado: progreso + texto
        var statusBar = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 30,
            ColumnCount = 2,
            Padding = new Padding(10, 2, 10, 4),
        };
        statusBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        statusBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statusBar.Controls.Add(_progress, 0, 0);
        statusBar.Controls.Add(_status, 1, 0);

        Controls.Add(_tabs);
        Controls.Add(buttons);
        Controls.Add(statusBar);
        Controls.Add(_header);

        _rescanButton.Click += async (_, _) => await ScanAsync();
        _selectAllButton.Click += (_, _) => ToggleSelectAll();
        _updateButton.Click += async (_, _) => await UpdateSelectedAsync();
        _updatesList.ItemChecked += (_, _) => RefreshUpdateButton();
        Shown += async (_, _) => await ScanAsync();
        FormClosing += OnFormClosing;
    }

    private static ListView CreateList(bool checkBoxes) => new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        GridLines = false,
        CheckBoxes = checkBoxes,
        BorderStyle = BorderStyle.None,
    };

    // ───────────────────────── Escaneo ─────────────────────────

    private async Task ScanAsync()
    {
        if (_busy) return;
        SetBusy(true);
        _updatesList.Items.Clear();
        _installedList.Items.Clear();

        try
        {
            SetStatus("Analizando los componentes del equipo…");
            _header.Text = "Analizando tu PC…";
            _installed = await Task.Run(DriverScanner.ScanInstalledDrivers);
            FillInstalledList();

            SetStatus($"Se encontraron {_installed.Count} drivers. Consultando Windows Update (puede tardar unos minutos)…");
            _header.Text = "Buscando versiones más recientes…";
            _available = await Task.Run(() =>
            {
                _wu ??= new WindowsUpdateService();
                var found = _wu.SearchDriverUpdates();
                DriverScanner.MatchUpdatesToInstalled(found, _installed);
                return found;
            });
            FillUpdatesList();

            if (_available.Count == 0)
            {
                _header.Text = "✔ Todos tus drivers están al día";
                SetStatus($"{_installed.Count} drivers analizados. No hay actualizaciones disponibles.");
            }
            else
            {
                _header.Text = $"Hay {_available.Count} actualización(es) de drivers disponibles";
                SetStatus("Marca los drivers que quieras actualizar y pulsa «Actualizar seleccionados».");
            }
        }
        catch (Exception ex)
        {
            _header.Text = "No se pudo completar la búsqueda";
            SetStatus(ex.Message);
            MessageBox.Show(this,
                "Error al buscar actualizaciones:\n\n" + ex.Message +
                "\n\nComprueba tu conexión a Internet y que el servicio «Windows Update» no esté deshabilitado.",
                "OpenDriverUpdater", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void FillInstalledList()
    {
        _installedList.BeginUpdate();
        foreach (var d in _installed)
        {
            _installedList.Items.Add(new ListViewItem(new[]
            {
                d.DeviceName, d.DeviceClass, d.Manufacturer, d.Version,
                d.Date?.ToString("dd/MM/yyyy") ?? "", d.InfName,
            }));
        }
        _installedList.EndUpdate();
        _tabs.TabPages[1].Text = $"Drivers instalados ({_installed.Count})";
    }

    private void FillUpdatesList()
    {
        _updatesList.BeginUpdate();
        foreach (var u in _available)
        {
            var name = u.Current?.DeviceName ?? (string.IsNullOrEmpty(u.Model) ? u.Title : u.Model);
            var item = new ListViewItem(new[]
            {
                name,
                string.IsNullOrEmpty(u.Manufacturer) ? u.Provider : u.Manufacturer,
                u.DriverClass,
                u.Current?.Version ?? "—",
                string.IsNullOrEmpty(u.NewVersion) ? "—" : u.NewVersion,
                u.NewDate?.ToString("dd/MM/yyyy") ?? "",
                FormatSize(u.SizeBytes),
            })
            {
                Tag = u,
                Checked = true, // por defecto, todo marcado; el usuario decide
                ToolTipText = u.Title,
            };
            _updatesList.Items.Add(item);
        }
        _updatesList.ShowItemToolTips = true;
        _updatesList.EndUpdate();
        _tabs.TabPages[0].Text = $"Actualizaciones disponibles ({_available.Count})";
        RefreshUpdateButton();
    }

    // ───────────────────────── Actualización ─────────────────────────

    private async Task UpdateSelectedAsync()
    {
        if (_busy || _wu is null) return;

        var selected = _updatesList.CheckedItems.Cast<ListViewItem>()
            .Select(i => (DriverUpdate)i.Tag!)
            .ToList();
        if (selected.Count == 0) return;

        var confirm = MessageBox.Show(this,
            $"Se van a descargar e instalar {selected.Count} driver(s).\n\n" +
            "Durante la instalación la pantalla puede parpadear o perderse la conexión de red unos segundos. ¿Continuar?",
            "Confirmar actualización", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes) return;

        SetBusy(true);
        _header.Text = "Actualizando drivers…";
        try
        {
            if (_restorePoint.Checked)
            {
                SetStatus("Creando punto de restauración…");
                bool ok = await Task.Run(() => SystemActions.TryCreateRestorePoint("Antes de OpenDriverUpdater"));
                if (!ok)
                    SetStatus("No se pudo crear el punto de restauración (puede estar desactivado o ya creado hoy). Continuando…");
            }

            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = 0;

            var progress = new Progress<(int Done, int Total, string Message)>(p =>
            {
                _progress.Maximum = Math.Max(1, p.Total);
                _progress.Value = Math.Min(p.Done, _progress.Maximum);
                if (!string.IsNullOrEmpty(p.Message)) SetStatus(p.Message);
            });

            var (results, reboot) = await Task.Run(() =>
                _wu.DownloadAndInstall(selected, progress, CancellationToken.None));

            ShowSummary(results);

            if (reboot)
            {
                var answer = MessageBox.Show(this,
                    "Algunos drivers necesitan reiniciar el equipo para terminar de aplicarse.\n\n¿Quieres reiniciar ahora?",
                    "Reinicio necesario", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer == DialogResult.Yes)
                {
                    SystemActions.RestartNow();
                    return;
                }
            }
            else
            {
                var answer = MessageBox.Show(this,
                    "Actualización terminada. No es obligatorio reiniciar, pero se recomienda.\n\n¿Quieres reiniciar ahora?",
                    "Actualización terminada", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (answer == DialogResult.Yes)
                {
                    SystemActions.RestartNow();
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Error durante la actualización:\n\n" + ex.Message,
                "OpenDriverUpdater", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }

        await ScanAsync(); // refrescar el estado tras instalar
    }

    private void ShowSummary(List<InstallResult> results)
    {
        int ok = results.Count(r => r.Status is InstallStatus.Succeeded or InstallStatus.SucceededWithErrors);
        int failed = results.Count - ok;

        var lines = results.Select(r =>
            (r.Status is InstallStatus.Succeeded or InstallStatus.SucceededWithErrors ? "✔ " : "✖ ") +
            r.Update.Title + " — " + r.Message);

        MessageBox.Show(this,
            $"Instalados: {ok}    Con error: {failed}\n\n" + string.Join("\n", lines),
            "Resumen", MessageBoxButtons.OK,
            failed == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);

        SetStatus($"Terminado: {ok} instalado(s), {failed} con error.");
    }

    // ───────────────────────── Utilidades UI ─────────────────────────

    private void ToggleSelectAll()
    {
        bool allChecked = _updatesList.Items.Cast<ListViewItem>().All(i => i.Checked);
        foreach (ListViewItem item in _updatesList.Items) item.Checked = !allChecked;
        _selectAllButton.Text = allChecked ? "Seleccionar todo" : "Quitar selección";
    }

    private void RefreshUpdateButton()
    {
        int n = _updatesList.CheckedItems.Count;
        _updateButton.Enabled = !_busy && n > 0;
        _updateButton.Text = n > 0 ? $"Actualizar seleccionados ({n})" : "Actualizar seleccionados";
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _rescanButton.Enabled = !busy;
        _selectAllButton.Enabled = !busy;
        _restorePoint.Enabled = !busy;
        _updatesList.Enabled = !busy;
        _progress.Visible = busy;
        if (busy) _progress.Style = ProgressBarStyle.Marquee;
        UseWaitCursor = busy;
        RefreshUpdateButton();
    }

    private void SetStatus(string text) => _status.Text = text;

    private static string FormatSize(decimal bytes)
    {
        if (bytes <= 0) return "";
        double mb = (double)bytes / (1024 * 1024);
        return mb >= 1 ? $"{mb:0.#} MB" : $"{(double)bytes / 1024:0} KB";
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_busy && e.CloseReason == CloseReason.UserClosing)
        {
            var r = MessageBox.Show(this,
                "Hay una operación en curso. Cerrar ahora durante una instalación de drivers no es recomendable.\n\n¿Cerrar de todos modos?",
                "OpenDriverUpdater", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) e.Cancel = true;
        }
    }
}
