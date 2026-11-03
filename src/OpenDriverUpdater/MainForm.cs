using System.Diagnostics;
using OpenDriverUpdater.Models;
using OpenDriverUpdater.Services;

namespace OpenDriverUpdater;

/// <summary>
/// Ventana principal. Flujo:
/// 1) Al abrir, analiza todos los dispositivos y sus drivers.
/// 2) Compara cada uno con Windows Update, el Catálogo de Microsoft Update y la web de NVIDIA.
/// 3) Muestra versión instalada y última versión oficial; el usuario marca qué actualizar.
/// 4) Descarga + instala, y pregunta si reiniciar.
/// </summary>
public sealed class MainForm : Form
{
    private static readonly Color UpdateColor = Color.FromArgb(196, 82, 0);
    private static readonly Color OkColor = Color.FromArgb(24, 128, 56);
    private static readonly Color MutedColor = Color.FromArgb(110, 110, 110);

    private readonly Label _header = new()
    {
        Text = "Analizando tu PC…",
        Dock = DockStyle.Top,
        Height = 44,
        Font = new Font("Segoe UI Semibold", 13f),
        Padding = new Padding(12, 10, 12, 0),
    };

    private readonly ComboBox _filter = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 260,
        Margin = new Padding(0, 4, 12, 0),
    };

    private readonly Label _summary = new() { AutoSize = true, Margin = new Padding(0, 8, 0, 0), ForeColor = MutedColor };

    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        CheckBoxes = true,
        BorderStyle = BorderStyle.None,
        ShowItemToolTips = true,
    };

    private readonly CheckBox _restorePoint = new()
    {
        Text = "Crear punto de restauración",
        Checked = true,
        AutoSize = true,
        Margin = new Padding(0, 8, 16, 0),
    };

    private readonly Button _rescanButton = MakeButton("Buscar de nuevo");
    private readonly Button _recommendedButton = MakeButton("Marcar recomendados");
    private readonly Button _reportButton = MakeButton("Exportar informe");
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

    private readonly UpdateEngine _engine = new();
    private readonly HashSet<DeviceRow> _checked = new();
    private List<DeviceRow> _rows = new();
    private bool _busy;
    private bool _filling;

    private static readonly string[] FilterOptions =
    {
        "Mostrar: todos los drivers",
        "Mostrar: solo con actualización",
        "Mostrar: solo al día",
        "Mostrar: sin datos / integrados",
    };

    public MainForm()
    {
        Text = "OpenDriverUpdater";
        Width = 1280;
        Height = 720;
        MinimumSize = new Size(900, 480);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);

        _list.Columns.Add("Dispositivo", 290);
        _list.Columns.Add("Tipo", 95);
        _list.Columns.Add("Versión instalada", 150);
        _list.Columns.Add("Fecha", 82);
        _list.Columns.Add("Última versión oficial", 160);
        _list.Columns.Add("Fecha", 82);
        _list.Columns.Add("Fuente", 145);
        _list.Columns.Add("Estado", 240);

        _filter.Items.AddRange(FilterOptions);
        _filter.SelectedIndex = 0;

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38, Padding = new Padding(10, 2, 10, 2) };
        top.Controls.AddRange(new Control[] { _filter, _summary });

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(10, 10, 10, 6) };
        buttons.Controls.AddRange(new Control[] { _rescanButton, _recommendedButton, _reportButton, _restorePoint, _updateButton });

        var statusBar = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 30, ColumnCount = 2, Padding = new Padding(10, 2, 10, 4) };
        statusBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        statusBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statusBar.Controls.Add(_progress, 0, 0);
        statusBar.Controls.Add(_status, 1, 0);

        Controls.Add(_list);
        Controls.Add(top);
        Controls.Add(buttons);
        Controls.Add(statusBar);
        Controls.Add(_header);

        _rescanButton.Click += async (_, _) => await ScanAsync();
        _recommendedButton.Click += (_, _) => CheckRecommended();
        _reportButton.Click += (_, _) => ExportReport();
        _updateButton.Click += async (_, _) => await UpdateSelectedAsync();
        _filter.SelectedIndexChanged += (_, _) => FillList();
        _list.ItemCheck += OnItemCheck;
        _list.ItemChecked += OnItemChecked;
        _list.DoubleClick += (_, _) => ShowDetails();
        Shown += async (_, _) => await ScanAsync();
        FormClosing += OnFormClosing;
    }

    private static Button MakeButton(string text) => new() { Text = text, AutoSize = true, Padding = new Padding(8, 2, 8, 2) };

    // ───────────────────────── Escaneo ─────────────────────────

    private async Task ScanAsync()
    {
        if (_busy) return;
        SetBusy(true);
        _list.Items.Clear();
        _checked.Clear();
        _header.Text = "Analizando tu PC…";

        var progress = new Progress<ScanProgress>(p =>
        {
            SetStatus(p.Message);
            if (p.Total > 0)
            {
                _progress.Style = ProgressBarStyle.Continuous;
                _progress.Maximum = p.Total;
                _progress.Value = Math.Min(p.Done, p.Total);
                _header.Text = "Comparando con las versiones oficiales…";
            }
        });

        try
        {
            _rows = await _engine.ScanAsync(progress, CancellationToken.None);
            foreach (var r in _rows.Where(r => r.Recommended)) _checked.Add(r);
            FillList();

            int updates = _rows.Count(r => r.CanUpdate);
            _header.Text = updates == 0
                ? "✔ Todos tus drivers están al día"
                : $"Hay {updates} driver(s) con una versión oficial más reciente";

            var warnings = new List<string>();
            if (_engine.WindowsUpdateError is { } wu) warnings.Add("Windows Update no respondió: " + wu);
            if (_engine.CatalogErrors > 0) warnings.Add($"{_engine.CatalogErrors} consultas al catálogo fallaron (conexión)");
            if (_engine.NvidiaError is { } nv) warnings.Add("NVIDIA: " + nv);
            SetStatus(warnings.Count > 0
                ? "Aviso: " + string.Join(" · ", warnings)
                : "Marca los drivers que quieras actualizar. Doble clic en una fila para ver los detalles.");
        }
        catch (Exception ex)
        {
            _header.Text = "No se pudo completar el análisis";
            SetStatus(ex.Message);
            MessageBox.Show(this, "Error al analizar los drivers:\n\n" + ex.Message +
                                  "\n\nComprueba tu conexión a Internet.",
                "OpenDriverUpdater", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private IEnumerable<DeviceRow> FilteredRows() => _filter.SelectedIndex switch
    {
        1 => _rows.Where(r => r.CanUpdate),
        2 => _rows.Where(r => r.Status == DeviceStatus.UpToDate),
        3 => _rows.Where(r => r.Status is DeviceStatus.NotFound or DeviceStatus.NotChecked ||
                              (r.Status == DeviceStatus.MissingDriver && r.Latest is null)),
        _ => _rows,
    };

    private void FillList()
    {
        _filling = true;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var r in FilteredRows())
        {
            var d = r.Device;
            var l = r.Latest;

            string installed = d.HasDriver ? d.Version : "(sin driver)";
            if (d.HasDriver && NvidiaService.IsNvidiaGpu(d) && VersionUtil.NvidiaFromWindows(d.Version) is { } nv)
                installed += $"  ({nv})";

            var item = new ListViewItem(new[]
            {
                d.Name,
                d.DeviceClass,
                installed,
                d.Date?.ToString("dd/MM/yyyy") ?? "",
                l?.Version is { Length: > 0 } v ? v : l is null ? "—" : "(ver detalles)",
                l?.Date?.ToString("dd/MM/yyyy") ?? "",
                l?.SourceName ?? "",
                r.StatusText,
            })
            {
                Tag = r,
                Checked = _checked.Contains(r),
                UseItemStyleForSubItems = true,
                ToolTipText = l?.Title ?? string.Join("\n", d.HardwareIds.Take(3)),
            };

            if (r.CanUpdate)
            {
                item.ForeColor = UpdateColor;
                item.Font = new Font(_list.Font, FontStyle.Bold);
            }
            else if (r.Status == DeviceStatus.UpToDate) item.ForeColor = OkColor;
            else item.ForeColor = MutedColor;

            _list.Items.Add(item);
        }
        _list.EndUpdate();
        _filling = false;

        int total = _rows.Count;
        _summary.Text = total == 0 ? "" :
            $"{total} dispositivos · {_rows.Count(r => r.CanUpdate)} con actualización · " +
            $"{_rows.Count(r => r.Status == DeviceStatus.UpToDate)} al día · " +
            $"{_rows.Count(r => r.Status is DeviceStatus.NotFound or DeviceStatus.NotChecked)} sin datos";
        RefreshUpdateButton();
    }

    private void OnItemCheck(object? sender, ItemCheckEventArgs e)
    {
        if (_filling) return;
        var row = (DeviceRow)_list.Items[e.Index].Tag!;
        if (e.NewValue == CheckState.Checked && (!row.CanUpdate || _busy))
            e.NewValue = CheckState.Unchecked; // solo se pueden marcar los que tienen versión más nueva
    }

    private void OnItemChecked(object? sender, ItemCheckedEventArgs e)
    {
        if (_filling) return;
        var row = (DeviceRow)e.Item.Tag!;
        if (e.Item.Checked) _checked.Add(row); else _checked.Remove(row);
        RefreshUpdateButton();
    }

    private void CheckRecommended()
    {
        _checked.Clear();
        foreach (var r in _rows.Where(r => r.Recommended)) _checked.Add(r);
        FillList();
    }

    private void ShowDetails()
    {
        if (_list.SelectedItems.Count == 0) return;
        var r = (DeviceRow)_list.SelectedItems[0].Tag!;
        var d = r.Device;

        var text = $"{d.Name}\n\n" +
                   $"Estado: {r.StatusText}\n\n" +
                   $"INSTALADO\n  Versión: {(d.HasDriver ? d.Version : "(ninguno)")}\n  Fecha: {d.Date:dd/MM/yyyy}\n  Proveedor: {d.Provider}\n  INF: {d.InfName}\n\n";

        if (r.Latest is { } l)
        {
            text += $"ÚLTIMA VERSIÓN OFICIAL\n  {l.Title}\n  Versión: {l.Version}\n  Fecha: {l.Date:dd/MM/yyyy}\n" +
                    $"  Fuente: {l.SourceName}\n  Coincidencia: {(l.Match == MatchQuality.Exact ? "exacta (tu modelo)" : "genérica (puede no ser para tu modelo exacto)")}\n" +
                    $"  ID buscado: {l.MatchedId}\n\n";
        }

        var others = r.AllCandidates.Where(c => c != r.Latest)
            .OrderByDescending(c => VersionUtil.Parse(c.Version) ?? new Version(0, 0)).Take(5).ToList();
        if (others.Count > 0)
            text += "OTRAS VERSIONES ENCONTRADAS\n" + string.Join("\n", others.Select(c => $"  {c.Version} · {c.SourceName} · {c.Title}")) + "\n\n";

        text += "HARDWARE IDs\n  " + string.Join("\n  ", d.HardwareIds.Take(6));

        if (r.Latest?.InfoUrl is { } url)
        {
            text += "\n\n¿Abrir la página oficial de este driver?";
            if (MessageBox.Show(this, text, "Detalles del driver", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                OpenUrl(url);
        }
        else
        {
            MessageBox.Show(this, text, "Detalles del driver", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void ExportReport()
    {
        if (_rows.Count == 0) return;
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            $"OpenDriverUpdater-informe-{DateTime.Now:yyyyMMdd-HHmm}.txt");
        try
        {
            File.WriteAllText(path, _engine.BuildReport(_rows));
            SetStatus("Informe guardado en el escritorio: " + Path.GetFileName(path));
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "No se pudo guardar el informe:\n" + ex.Message, "OpenDriverUpdater",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { /* sin navegador */ }
    }

    // ───────────────────────── Actualización ─────────────────────────

    private async Task UpdateSelectedAsync()
    {
        if (_busy) return;
        var selected = _rows.Where(r => _checked.Contains(r) && r.CanUpdate).ToList();
        if (selected.Count == 0) return;

        var lines = string.Join("\n", selected.Take(12).Select(r =>
            $"• {r.Device.Name}: {(r.Device.HasDriver ? r.Device.Version : "sin driver")} → {r.Latest!.Version} ({r.Latest.SourceName})"));
        if (selected.Count > 12) lines += $"\n• … y {selected.Count - 12} más";
        if (selected.Any(r => r.Latest!.Source == DriverSource.Nvidia))
            lines += "\n\nEl driver de NVIDIA se instala con el asistente oficial de NVIDIA: sigue sus pasos cuando aparezca.";

        var confirm = MessageBox.Show(this,
            $"Se van a descargar e instalar {selected.Count} driver(s):\n\n{lines}\n\n" +
            "Durante la instalación la pantalla puede parpadear o perderse la red unos segundos. ¿Continuar?",
            "Confirmar actualización", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes) return;

        SetBusy(true);
        _header.Text = "Actualizando drivers…";
        var results = new List<InstallResult>();
        try
        {
            if (_restorePoint.Checked)
            {
                SetStatus("Creando punto de restauración…");
                if (!await Task.Run(() => SystemActions.TryCreateRestorePoint("Antes de OpenDriverUpdater")))
                    SetStatus("No se pudo crear el punto de restauración (puede estar desactivado o ya creado hoy). Continuando…");
            }

            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Maximum = selected.Count;
            _progress.Value = 0;
            var stepProgress = new Progress<string>(SetStatus);

            for (int i = 0; i < selected.Count; i++)
            {
                _header.Text = $"Actualizando drivers… ({i + 1}/{selected.Count})";
                var outcome = await _engine.InstallAsync(selected[i], stepProgress, CancellationToken.None);
                results.Add(new InstallResult(selected[i], outcome));
                _progress.Value = i + 1;
            }

            ShowSummary(results);

            bool reboot = results.Any(r => r.Outcome.RebootRequired) || WindowsUpdateService.IsRebootPending();
            bool anyOk = results.Any(r => r.Outcome.Ok);
            if (anyOk)
            {
                var msg = reboot
                    ? "Algunos drivers necesitan reiniciar el equipo para terminar de aplicarse.\n\n¿Quieres reiniciar ahora?"
                    : "Actualización terminada. No es obligatorio reiniciar, pero se recomienda.\n\n¿Quieres reiniciar ahora?";
                if (MessageBox.Show(this, msg, reboot ? "Reinicio necesario" : "Actualización terminada",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
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

        await ScanAsync(); // refrescar las versiones instaladas
    }

    private void ShowSummary(List<InstallResult> results)
    {
        int ok = results.Count(r => r.Outcome.Ok);
        int failed = results.Count - ok;

        var lines = results.Select(r =>
            (r.Outcome.Ok ? "✔ " : r.Outcome.Status == InstallStatus.NotApplied ? "• " : "✖ ") +
            r.Row.Device.Name + " — " + r.Outcome.Message);

        MessageBox.Show(this,
            $"Instalados: {ok}    Sin instalar: {failed}\n\n" + string.Join("\n", lines),
            "Resumen", MessageBoxButtons.OK,
            failed == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);

        SetStatus($"Terminado: {ok} instalado(s), {failed} sin instalar.");
    }

    // ───────────────────────── Utilidades UI ─────────────────────────

    private void RefreshUpdateButton()
    {
        int n = _rows.Count(r => _checked.Contains(r) && r.CanUpdate);
        _updateButton.Enabled = !_busy && n > 0;
        _updateButton.Text = n > 0 ? $"Actualizar seleccionados ({n})" : "Actualizar seleccionados";
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _rescanButton.Enabled = !busy;
        _recommendedButton.Enabled = !busy;
        _reportButton.Enabled = !busy;
        _restorePoint.Enabled = !busy;
        _filter.Enabled = !busy;
        _progress.Visible = busy;
        if (busy) _progress.Style = ProgressBarStyle.Marquee;
        UseWaitCursor = busy;
        RefreshUpdateButton();
    }

    private void SetStatus(string text) => _status.Text = text;

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
