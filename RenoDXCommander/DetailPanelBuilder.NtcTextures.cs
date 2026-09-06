// DetailPanelBuilder.NtcTextures.cs — NTC texture compression panel (Supreme RHI + NTC)

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RenoDXCommander.Models;
using RenoDXCommander.Services;
using RenoDXCommander.ViewModels;

namespace RenoDXCommander;

public partial class DetailPanelBuilder
{
    private CancellationTokenSource? _ntcScanCts;
    private NtcScanResult? _lastNtcScan;

    /// <summary>Populates the NTC texture compression panel for the given game card.</summary>
    internal void PopulateNtcPanel(GameCardViewModel card)
    {
        var panel = _window.NtcTexturesPanel;
        panel.Children.Clear();

        // ── BETA disclaimer ───────────────────────────────────────────────
        var betaBar = new InfoBar
        {
            Title = "⚠ VERSIÓN BETA",
            Message = "Esta función está en desarrollo. Siempre se hace backup automático antes de modificar archivos, pero úsala bajo tu propia responsabilidad. No se garantiza compatibilidad con todos los juegos.",
            Severity = InfoBarSeverity.Warning,
            IsOpen = true,
            IsClosable = false,
            Margin = new Microsoft.UI.Xaml.Thickness(0, 0, 0, 12),
        };
        panel.Children.Add(betaBar);

        // ── Section header ────────────────────────────────────────────────
        var header = new TextBlock
        {
            Text = "NTC — Compresión de Texturas",
            Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
            Margin = new Microsoft.UI.Xaml.Thickness(0, 0, 0, 4),
        };
        panel.Children.Add(header);

        var desc = new TextBlock
        {
            Text = "Comprime texturas DDS sin comprimir a formato BCn (BC7/BC5/BC4) para reducir el uso de VRAM.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            Margin = new Microsoft.UI.Xaml.Thickness(0, 0, 0, 10),
        };
        panel.Children.Add(desc);

        // ── texconv.exe path row ──────────────────────────────────────────
        var texconvRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Microsoft.UI.Xaml.Thickness(0, 0, 0, 6) };
        var texconvLabel = new TextBlock { Text = "texconv.exe:", VerticalAlignment = VerticalAlignment.Center, Width = 110 };
        var texconvBox = new TextBox
        {
            PlaceholderText = "Ruta a texconv.exe…",
            Width = 260,
            Text = _ntcService.TexconvPath ?? string.Empty,
        };
        texconvBox.TextChanged += (s, _) => _ntcService.TexconvPath = texconvBox.Text.Trim();

        var browseBtn = new Button { Content = "Buscar…" };
        browseBtn.Click += async (s, _) =>
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".exe");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_window));
            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                texconvBox.Text = file.Path;
                _ntcService.TexconvPath = file.Path;
            }
        };
        texconvRow.Children.Add(texconvLabel);
        texconvRow.Children.Add(texconvBox);
        texconvRow.Children.Add(browseBtn);
        panel.Children.Add(texconvRow);

        if (!_ntcService.IsTexconvAvailable)
        {
            var warn = new InfoBar
            {
                Title = "texconv.exe no encontrado",
                Message = "Descarga Microsoft DirectXTex (texconv.exe) y configura la ruta arriba.",
                Severity = InfoBarSeverity.Warning,
                IsOpen = true,
                IsClosable = false,
                Margin = new Microsoft.UI.Xaml.Thickness(0, 0, 0, 8),
            };
            panel.Children.Add(warn);
        }

        // ── Game folder display ───────────────────────────────────────────
        var folderRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Microsoft.UI.Xaml.Thickness(0, 0, 0, 10) };
        var folderLabel = new TextBlock { Text = "Carpeta:", VerticalAlignment = VerticalAlignment.Center, Width = 110 };
        var folderText = new TextBlock
        {
            Text = card.InstallPath ?? "(sin ruta de instalación)",
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 320,
            Opacity = 0.8,
        };
        folderRow.Children.Add(folderLabel);
        folderRow.Children.Add(folderText);
        panel.Children.Add(folderRow);

        // ── Scan / Compress / Restore buttons ─────────────────────────────
        var progress = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed, Margin = new Microsoft.UI.Xaml.Thickness(0, 6, 0, 6) };
        var statusText = new TextBlock { Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
        var scanResultsBlock = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Microsoft.UI.Xaml.Thickness(0, 6, 0, 0) };

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Microsoft.UI.Xaml.Thickness(0, 0, 0, 6) };

        var scanBtn = new Button
        {
            Content = "Escanear texturas",
            IsEnabled = !string.IsNullOrEmpty(card.InstallPath),
        };
        var compressBtn = new Button
        {
            Content = "Comprimir todo",
            IsEnabled = false,
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        var restoreBtn = new Button
        {
            Content = "Restaurar originales",
            IsEnabled = !string.IsNullOrEmpty(card.InstallPath) && Directory.Exists(_ntcService.GetBackupFolder(card.InstallPath ?? "")),
        };

        btnRow.Children.Add(scanBtn);
        btnRow.Children.Add(compressBtn);
        btnRow.Children.Add(restoreBtn);

        // Scan
        scanBtn.Click += async (s, _) =>
        {
            var folder = card.InstallPath;
            if (string.IsNullOrEmpty(folder)) return;

            _ntcScanCts?.Cancel();
            _ntcScanCts = new CancellationTokenSource();
            var ct = _ntcScanCts.Token;

            scanBtn.IsEnabled = false;
            compressBtn.IsEnabled = false;
            progress.Visibility = Visibility.Visible;
            statusText.Text = "Escaneando texturas DDS…";
            scanResultsBlock.Text = string.Empty;

            try
            {
                _lastNtcScan = await _ntcService.ScanGameAsync(folder, ct);
                var r = _lastNtcScan;
                statusText.Text = string.Empty;
                scanResultsBlock.Text =
                    $"Texturas DDS encontradas: {r.Textures.Count}\n" +
                    $"Sin comprimir: {r.UncompressedCount}  ({FormatMb(r.UncompressedVramBytes)} MB VRAM)\n" +
                    $"Total VRAM estimada: {FormatMb(r.TotalVramBytes)} MB";
                compressBtn.IsEnabled = r.UncompressedCount > 0 && _ntcService.IsTexconvAvailable;
            }
            catch (OperationCanceledException) { statusText.Text = "Escaneo cancelado."; }
            catch (Exception ex) { statusText.Text = $"Error al escanear: {ex.Message}"; }
            finally
            {
                progress.Visibility = Visibility.Collapsed;
                scanBtn.IsEnabled = true;
            }
        };

        // Compress
        compressBtn.Click += async (s, _) =>
        {
            if (_lastNtcScan == null || string.IsNullOrEmpty(card.InstallPath)) return;

            compressBtn.IsEnabled = false;
            scanBtn.IsEnabled = false;
            restoreBtn.IsEnabled = false;
            progress.Visibility = Visibility.Visible;

            var progressReporter = new Progress<NtcProgress>(p =>
            {
                statusText.Text = $"{p.Message} ({p.Done}/{p.Total})";
            });

            try
            {
                var result = await _ntcService.CompressAsync(_lastNtcScan, card.InstallPath, progressReporter);
                statusText.Text = string.Empty;
                scanResultsBlock.Text =
                    $"Comprimidas: {result.Compressed}  |  Fallos: {result.Failed}\n" +
                    $"VRAM liberada aprox.: {FormatMb(result.VramSavedBytes)} MB";
                restoreBtn.IsEnabled = true;
                _lastNtcScan = null;
                compressBtn.IsEnabled = false;
            }
            catch (Exception ex) { statusText.Text = $"Error: {ex.Message}"; compressBtn.IsEnabled = true; }
            finally
            {
                progress.Visibility = Visibility.Collapsed;
                scanBtn.IsEnabled = true;
                restoreBtn.IsEnabled = true;
            }
        };

        // Restore
        restoreBtn.Click += async (s, _) =>
        {
            if (string.IsNullOrEmpty(card.InstallPath)) return;
            restoreBtn.IsEnabled = false;
            progress.Visibility = Visibility.Visible;
            statusText.Text = "Restaurando originales…";
            try
            {
                var count = await _ntcService.RestoreBackupsAsync(card.InstallPath);
                statusText.Text = $"Restauradas {count} texturas.";
                scanResultsBlock.Text = string.Empty;
                _lastNtcScan = null;
                compressBtn.IsEnabled = false;
            }
            catch (Exception ex) { statusText.Text = $"Error al restaurar: {ex.Message}"; restoreBtn.IsEnabled = true; }
            finally { progress.Visibility = Visibility.Collapsed; }
        };

        panel.Children.Add(btnRow);
        panel.Children.Add(progress);
        panel.Children.Add(statusText);
        panel.Children.Add(scanResultsBlock);

        // ── Bethesda Archives section ─────────────────────────────────────
        AddBethesdaSection(panel, card);
    }

    // ── Bethesda BSA / BA2 support ────────────────────────────────────────

    private BethesdaScanResult? _lastBethesdaScan;
    private CancellationTokenSource? _bethesdaScanCts;

    private void AddBethesdaSection(StackPanel panel, GameCardViewModel card)
    {
        var sep = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Height = 1, Fill = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
            Margin = new Microsoft.UI.Xaml.Thickness(0, 12, 0, 12),
        };
        panel.Children.Add(sep);

        panel.Children.Add(new TextBlock
        {
            Text = "Archivos Bethesda (BSA / BA2)",
            Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
            Margin = new Microsoft.UI.Xaml.Thickness(0, 0, 0, 4),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Extrae, comprime y reempaqueta texturas de archivos BSA (Skyrim) y BA2 (Fallout 4, Starfield).",
            TextWrapping = TextWrapping.Wrap, Opacity = 0.7,
            Margin = new Microsoft.UI.Xaml.Thickness(0, 0, 0, 10),
        });

        var bProgress    = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed, Margin = new Microsoft.UI.Xaml.Thickness(0, 6, 0, 6) };
        var bStatus      = new TextBlock { Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
        var bResults     = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Microsoft.UI.Xaml.Thickness(0, 6, 0, 0) };

        var bBtnRow  = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Microsoft.UI.Xaml.Thickness(0, 0, 0, 6) };
        var bScanBtn = new Button { Content = "Escanear archivos", IsEnabled = !string.IsNullOrEmpty(card.InstallPath) };
        var bCompBtn = new Button { Content = "Comprimir todo", IsEnabled = false, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var bRestBtn = new Button
        {
            Content = "Restaurar originales",
            IsEnabled = !string.IsNullOrEmpty(card.InstallPath)
                && Directory.Exists(_bethesdaService.GetBackupFolder(card.InstallPath ?? "")),
        };

        bBtnRow.Children.Add(bScanBtn);
        bBtnRow.Children.Add(bCompBtn);
        bBtnRow.Children.Add(bRestBtn);

        // Sync texconv path to bethesdaService whenever it changes
        _bethesdaService.TexconvPath = _ntcService.TexconvPath;

        bScanBtn.Click += async (s, _) =>
        {
            var folder = card.InstallPath;
            if (string.IsNullOrEmpty(folder)) return;

            _bethesdaService.TexconvPath = _ntcService.TexconvPath;
            _bethesdaScanCts?.Cancel();
            _bethesdaScanCts = new CancellationTokenSource();
            var ct = _bethesdaScanCts.Token;

            bScanBtn.IsEnabled = false; bCompBtn.IsEnabled = false;
            bProgress.Visibility = Visibility.Visible;
            bStatus.Text = "Escaneando archivos BSA/BA2…";
            bResults.Text = string.Empty;

            try
            {
                _lastBethesdaScan = await _bethesdaService.ScanGameAsync(folder, ct);
                var r = _lastBethesdaScan;
                bStatus.Text = string.Empty;
                if (r.Archives.Count == 0)
                {
                    bResults.Text = "No se encontraron archivos BSA/BA2 con texturas en esta carpeta.";
                }
                else
                {
                    var lines = r.Archives.Select(a =>
                        $"• {Path.GetFileName(a.Path)}  [{a.Type}]  ~{a.TextureCount} texturas").ToList();
                    lines.Add($"\nTotal: {r.TotalTextures} texturas  (~{FormatMb(r.TotalUnpackedBytes)} MB VRAM estimada)");
                    bResults.Text = string.Join("\n", lines);
                    bCompBtn.IsEnabled = _ntcService.IsTexconvAvailable;
                    if (!_ntcService.IsTexconvAvailable)
                        bStatus.Text = "Configura texconv.exe arriba para habilitar la compresión.";
                }
            }
            catch (OperationCanceledException) { bStatus.Text = "Escaneo cancelado."; }
            catch (Exception ex) { bStatus.Text = $"Error: {ex.Message}"; }
            finally { bProgress.Visibility = Visibility.Collapsed; bScanBtn.IsEnabled = true; }
        };

        bCompBtn.Click += async (s, _) =>
        {
            if (_lastBethesdaScan == null || string.IsNullOrEmpty(card.InstallPath)) return;
            _bethesdaService.TexconvPath = _ntcService.TexconvPath;

            bCompBtn.IsEnabled = false; bScanBtn.IsEnabled = false; bRestBtn.IsEnabled = false;
            bProgress.Visibility = Visibility.Visible;

            var rep = new Progress<NtcProgress>(p => bStatus.Text = $"{p.Message} ({p.Done}/{p.Total})");
            try
            {
                var result = await _bethesdaService.CompressArchivesAsync(
                    _lastBethesdaScan, card.InstallPath, rep);
                bStatus.Text = string.Empty;
                bResults.Text = $"Archivos procesados: {result.Processed}  |  Fallos: {result.Failed}\n"
                              + $"VRAM liberada aprox.: {FormatMb(result.VramSavedBytes)} MB";
                bRestBtn.IsEnabled = true;
                _lastBethesdaScan = null;
                bCompBtn.IsEnabled = false;
            }
            catch (Exception ex) { bStatus.Text = $"Error: {ex.Message}"; bCompBtn.IsEnabled = true; }
            finally { bProgress.Visibility = Visibility.Collapsed; bScanBtn.IsEnabled = true; bRestBtn.IsEnabled = true; }
        };

        bRestBtn.Click += async (s, _) =>
        {
            if (string.IsNullOrEmpty(card.InstallPath)) return;
            bRestBtn.IsEnabled = false; bProgress.Visibility = Visibility.Visible;
            bStatus.Text = "Restaurando archivos originales…";
            try
            {
                var count = await _bethesdaService.RestoreBackupsAsync(card.InstallPath);
                bStatus.Text = $"Restaurados {count} archivo(s).";
                bResults.Text = string.Empty;
                _lastBethesdaScan = null; bCompBtn.IsEnabled = false;
            }
            catch (Exception ex) { bStatus.Text = $"Error: {ex.Message}"; bRestBtn.IsEnabled = true; }
            finally { bProgress.Visibility = Visibility.Collapsed; }
        };

        panel.Children.Add(bBtnRow);
        panel.Children.Add(bProgress);
        panel.Children.Add(bStatus);
        panel.Children.Add(bResults);
    }

    private static string FormatMb(long bytes) => (bytes / 1_048_576.0).ToString("F1");
}
