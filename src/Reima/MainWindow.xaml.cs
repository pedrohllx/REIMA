using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Reima.Models;
using Reima.Services;

namespace Reima;

public partial class MainWindow : Window
{
    sealed record UserSettings(string Mo2Root, string AnomalyRoot, string? BaseProfile, List<string> Selected, bool PreferModDb = false, bool UsePacks = true, string? PackDir = null);

    static readonly string AppDir = AppContext.BaseDirectory;
    static readonly string SettingsPath = Path.Combine(AppDir, "settings.json");
    static readonly string DownloadsDir = Path.Combine(AppDir, "downloads");
    static readonly string LogPath = Path.Combine(AppDir, "logs", "reima.log");

    readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    readonly Dictionary<string, ToggleButtonRef> _optionControls = [];
    Manifest? _manifest;
    CancellationTokenSource? _cts;

    sealed record ToggleButtonRef(System.Windows.Controls.Primitives.ToggleButton Control);

    public MainWindow()
    {
        InitializeComponent();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("REIMA-Installer/0.1");
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        Loaded += (_, _) => Init();
    }

    void Init()
    {
        try
        {
            _manifest = Manifest.Load(Path.Combine(AppDir, "manifest.json"));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Não consegui ler o manifest.json:\n" + ex.Message, "REIMA", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
            return;
        }
        SubTitle.Text = $"REIMA v{_manifest.Version} · perfil \"{_manifest.Profile}\"";
        Footer.Text = $"REIMA Windows installer v{typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}";

        var saved = LoadSettings();
        Mo2Box.Text = saved?.Mo2Root ?? @"D:\noSteam\Gamma Modpack\GAMMA";
        AnomalyBox.Text = saved?.AnomalyRoot ?? @"D:\noSteam\Gamma Modpack\Anomaly";
        BaseProfileBox.Text = saved?.BaseProfile ?? "";
        RootText.Text = Path.GetDirectoryName(AnomalyBox.Text.TrimEnd('\\')) is { } r &&
                        Path.GetDirectoryName(Mo2Box.Text.TrimEnd('\\')) == r ? r : "(caminhos separados)";
        PreferModDbCheck.IsChecked = saved?.PreferModDb ?? false;
        UsePacksCheck.IsChecked = saved?.UsePacks ?? true;
        PackDirBox.Text = saved?.PackDir ?? "";
        BuildOptions(saved?.Selected);
        BaseCheck.IsEnabled = _manifest.Base is not null;
        Mo2Box.TextChanged += (_, _) => UpdateDetect();
        AnomalyBox.TextChanged += (_, _) => UpdateDetect();
        UpdateDetect();
        _ = StartupChecksAsync();
    }

    async Task StartupChecksAsync()
    {
        await UpdateRecipeAsync();
        await CheckGammaAsync();
        await CheckOutdatedAsync();
        await CheckVersionsAsync();
    }

    // ---------- Outdated GAMMA mods (ModDB build id vs GAMMA list) ----------
    static readonly string UploadDir = Path.Combine(AppDir, "Subir pro Drive");
    List<OutdatedMod> _outdated = [];

    /// <summary>Recipe entries already updated locally but still without a Drive link.</summary>
    List<Asset> PendingLinks() =>
        _manifest!.Assets.Where(a => a.SourceVersion is not null && string.IsNullOrWhiteSpace(a.Url) && a.Sha256 is not null).ToList();

    async Task CheckOutdatedAsync()
    {
        try
        {
            _outdated = await new ModDbVersions(_http).FindOutdatedAsync(_manifest!, CancellationToken.None);
            var pending = PendingLinks().Count;
            if (_outdated.Count + pending == 0) { OutdatedBanner.Visibility = Visibility.Collapsed; AddLog("Mods do GAMMA: todos na versão atual."); return; }
            OutdatedBannerText.Text =
                (_outdated.Count > 0 ? $"{_outdated.Count} mod(s) do GAMMA estão desatualizados na sua receita. " : "") +
                (pending > 0 ? $"{pending} mod(s) atualizados aguardando o link do Drive." : "");
            OutdatedBanner.Visibility = Visibility.Visible;
        }
        catch (Exception ex) { AddLog("Checagem de mods desatualizados falhou: " + ex.Message); }
    }

    void OutdatedDetails_Click(object sender, RoutedEventArgs e) { BuildOutdatedList(); Show(OutdatedView); }

    async void RecheckOutdated_Click(object sender, RoutedEventArgs e)
    {
        _manifest = Manifest.Load(ManifestPath);
        await CheckOutdatedAsync();
        BuildOutdatedList();
    }

    void OpenUpload_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(UploadDir);
        Process.Start(new ProcessStartInfo(UploadDir) { UseShellExecute = true });
    }

    void BuildOutdatedList()
    {
        OutdatedList.Children.Clear();
        var pending = PendingLinks();
        OutdatedStatus.Text = $"{_outdated.Count} desatualizado(s) · {pending.Count} aguardando link do Drive · " +
                              "depois de colar os links, suba também o manifest.json da pasta do launcher no Drive (é a receita publicada).";

        Border Card(params UIElement[] children)
        {
            var sp = new StackPanel();
            foreach (var c in children) sp.Children.Add(c);
            return new Border
            {
                Background = (System.Windows.Media.Brush)FindResource("Panel"), BorderBrush = (System.Windows.Media.Brush)FindResource("Line"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 6),
                Child = sp,
            };
        }

        foreach (var o in _outdated)
        {
            var btn = new Button { Content = "Atualizar este mod", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
            var status = new TextBlock { Style = (Style)FindResource("Muted") };
            btn.Click += async (_, _) =>
            {
                btn.IsEnabled = false;
                status.Text = "Página aberta no navegador. Clique em baixar; o REIMA pega o arquivo da sua pasta Downloads...";
                _cts = new CancellationTokenSource();
                try
                {
                    var prog = new Progress<StepProgress>(p => status.Text = p.Title + " · " + p.Detail);
                    var got = await Task.Run(() => new ModUpdater(ManifestPath, DownloadsDir, UploadDir).UpdateAsync(o, prog, _cts.Token));
                    status.Text = $"Pronto: {Path.GetFileName(got)} está em \"Subir pro Drive\". Suba e cole o link abaixo.";
                    _manifest = Manifest.Load(ManifestPath);
                    _outdated.Remove(o);
                    BuildOutdatedList();
                }
                catch (Exception ex) { status.Text = "Erro: " + ex.Message; btn.IsEnabled = true; }
            };
            OutdatedList.Children.Add(Card(
                new TextBlock { Text = o.Name, FontWeight = FontWeights.SemiBold },
                new TextBlock { Text = o.Versions, Style = (Style)FindResource("Muted") },
                new TextBlock { Text = o.Latest.Page, Style = (Style)FindResource("Muted") },
                btn, status));
        }

        foreach (var a in pending)
        {
            var box = new TextBox { Margin = new Thickness(0, 6, 0, 0) };
            var save = new Button { Content = "Salvar link do Drive", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
            var status = new TextBlock { Style = (Style)FindResource("Muted") };
            save.Click += (_, _) =>
            {
                if (!box.Text.Contains("drive.google.com") && !box.Text.StartsWith("http")) { status.Text = "Cole um link válido (ex.: https://drive.google.com/file/d/.../view)."; return; }
                new ModUpdater(ManifestPath, DownloadsDir, UploadDir).SetLink(a.Id, box.Text);
                _manifest = Manifest.Load(ManifestPath);
                AddLog($"Link do Drive salvo: {a.Name}");
                BuildOutdatedList();
                _ = CheckOutdatedAsync();
            };
            OutdatedList.Children.Add(Card(
                new TextBlock { Text = a.Name, FontWeight = FontWeights.SemiBold },
                new TextBlock { Text = $"Arquivo: {a.File}  ·  versão ModDB #{a.SourceVersion}  ·  aguardando link do Drive", Style = (Style)FindResource("Muted") },
                box, save, status));
        }
        if (_outdated.Count + pending.Count == 0)
            OutdatedList.Children.Add(new TextBlock { Text = "Tudo em dia.", Style = (Style)FindResource("Muted") });
    }

    /// <summary>Downloads the published recipe (manifestUrl) and replaces the local one when it changed.</summary>
    async Task UpdateRecipeAsync()
    {
        if (string.IsNullOrWhiteSpace(_manifest?.ManifestUrl)) return;
        try
        {
            var text = await _http.GetStringAsync(Downloader.Normalize(_manifest.ManifestUrl));
            var remote = Manifest.Parse(text); // validates before replacing anything
            if (text == await File.ReadAllTextAsync(ManifestPath)) { AddLog($"Receita em dia (v{remote.Version})."); return; }
            File.Copy(ManifestPath, ManifestPath + ".bak", true);
            await File.WriteAllTextAsync(ManifestPath, text);
            AddLog($"Receita atualizada: v{_manifest.Version} → v{remote.Version}");
            _manifest = remote;
            SubTitle.Text = $"REIMA v{_manifest.Version} · perfil \"{_manifest.Profile}\"";
        }
        catch (Exception ex)
        {
            AddLog("Não consegui baixar a receita publicada (usando a local): " + ex.Message);
        }
    }

    /// <summary>Version summary like GAMMA's launcher: installed / outdated / missing, without downloading anything.</summary>
    async Task CheckVersionsAsync()
    {
        if (_manifest is null || !File.Exists(Path.Combine(Mo2Box.Text.Trim(), "ModOrganizer.exe"))) return;
        try
        {
            VersionText.Text = "Checando versões dos mods...";
            var inst = new Installer(_manifest, BuildSettings(), new Downloader(_http), new Extractor("7z")) { GitHub = new GitHubSource(_http) };
            inst.Log += AddLog;
            var list = await Task.Run(() => inst.CheckAsync(CancellationToken.None));
            int ok = list.Count(x => x.Status == Installer.Status.UpToDate);
            var old = list.Where(x => x.Status == Installer.Status.Outdated).ToList();
            int missing = list.Count(x => x.Status == Installer.Status.NotInstalled);
            VersionText.Text = $"Mods: {list.Count} na receita  ·  {ok} na versão mais nova  ·  {old.Count} com atualização  ·  {missing} não instalados";
            foreach (var o in old) AddLog($"Atualização disponível: {o.Asset.Name} ({o.Installed} → {o.Latest})");
        }
        catch (Exception ex)
        {
            VersionText.Text = "";
            AddLog("Checagem de versões falhou: " + ex.Message);
        }
    }

    // ---------- GAMMA official updates ----------
    GammaUpdates? _gamma;
    GammaReport? _gammaReport;
    static readonly string ManifestPath = Path.Combine(AppDir, "manifest.json");

    async Task CheckGammaAsync()
    {
        try
        {
            _gamma = new GammaUpdates(_http, Path.Combine(AppDir, "gamma_state.json"));
            var gammaRoot = Directory.Exists(Mo2Box.Text) ? Mo2Box.Text.Trim() : null;
            _gammaReport = await _gamma.CheckAsync(_manifest!.Assets.Select(a => a.Name), gammaRoot, CancellationToken.None);
            if (_gammaReport is null) { AddLog("GAMMA oficial: sem mudanças desde a última checagem."); return; }
            if (!_gammaReport.Any)
            {
                _gamma.Accept(); // repo changed but not the mod list (patches, docs)
                AddLog($"GAMMA oficial mudou ({_gammaReport.Message}), mas a lista de mods é a mesma.");
                return;
            }
            var r = _gammaReport;
            GammaBannerText.Text =
                $"O GAMMA oficial foi atualizado em {r.Date.LocalDateTime:dd/MM/yyyy}: {r.Added.Count} mod(s) novo(s), " +
                $"{r.Updated.Count} atualizado(s), {r.Removed.Count} removido(s)" +
                (r.AffectsReima > 0 ? $" — {r.AffectsReima} afeta(m) o REIMA." : ". Nada do que o REIMA usa mudou.");
            GammaBanner.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            AddLog("Não consegui checar atualizações do GAMMA: " + ex.Message);
        }
    }

    void GammaDetails_Click(object sender, RoutedEventArgs e)
    {
        if (_gammaReport is not { } r) return;
        GammaHeader.Text = $"Versão de {r.Date.LocalDateTime:dd/MM/yyyy HH:mm} · \"{r.Message}\" · github.com/Grokitach/Stalker_GAMMA";
        GammaList.Items.Clear();
        void Section(string title, List<GammaChange> items, Func<GammaChange, string> line)
        {
            if (items.Count == 0) return;
            GammaList.Items.Add(new TextBlock { Text = $"{title} ({items.Count})", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 2) });
            foreach (var c in items.OrderByDescending(c => c.InReima))
                GammaList.Items.Add(new TextBlock { Text = (c.InReima ? "★ " : "   ") + line(c), TextWrapping = TextWrapping.Wrap });
        }
        Section("Atualizados", r.Updated, c => c.Mod.Display + (c.Old?.Url != c.Mod.Url ? "  (novo arquivo no ModDB)" : "  (pastas de instalação mudaram)"));
        Section("Novos no GAMMA", r.Added, c => c.Mod.Display + "  ·  " + c.Mod.Page);
        Section("Removidos do GAMMA", r.Removed, c => c.Mod.Display + (c.InReima ? "  (ainda está no REIMA: decida se mantém)" : ""));
        GammaList.Items.Add(new TextBlock
        {
            Text = "★ = está na receita do REIMA. \"Atualizar a receita\" troca o link e as pastas dos mods atualizados; " +
                   "na próxima instalação eles são baixados de novo. Mods novos e removidos não mudam sozinhos: a posição no perfil é decisão sua.",
            Style = (Style)FindResource("Muted"), Margin = new Thickness(0, 12, 0, 0),
        });
        GammaApplyBtn.IsEnabled = r.Updated.Any(c => c.InReima);
        Show(GammaView);
    }

    void GammaApply_Click(object sender, RoutedEventArgs e)
    {
        if (_gammaReport is null || _gamma is null) return;
        try
        {
            var n = GammaUpdates.ApplyToManifest(ManifestPath, _gammaReport);
            _gamma.Accept();
            _manifest = Manifest.Load(ManifestPath);
            GammaBanner.Visibility = Visibility.Collapsed;
            AddLog($"Receita atualizada com {n} mod(s) do GAMMA. Backup: manifest.json.bak_gamma_update");
            MessageBox.Show(this, $"{n} mod(s) atualizados na receita. Clique em Instalar / Atualizar para baixar as versões novas.",
                "REIMA", MessageBoxButton.OK, MessageBoxImage.Information);
            Show(ChoiceView);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Não consegui atualizar a receita: " + ex.Message, "REIMA", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    void GammaIgnore_Click(object sender, RoutedEventArgs e)
    {
        _gamma?.Accept();
        GammaBanner.Visibility = Visibility.Collapsed;
        Show(ChoiceView);
    }

    void UpdateDetect()
    {
        bool anomaly = File.Exists(Path.Combine(AnomalyBox.Text.Trim(), "AnomalyLauncher.exe"));
        bool mo2 = File.Exists(Path.Combine(Mo2Box.Text.Trim(), "ModOrganizer.exe"));
        DetectText.Text = $"Anomaly: {(anomaly ? "encontrado" : "não encontrado")}   ·   Mod Organizer 2: {(mo2 ? "encontrado" : "não encontrado")}";
        if (!anomaly || !mo2) BaseCheck.IsChecked = true;
    }

    void BuildOptions(List<string>? saved)
    {
        OptionsHost.Children.Clear();
        _optionControls.Clear();
        foreach (var g in _manifest!.OptionGroups)
        {
            OptionsHost.Children.Add(new TextBlock { Text = g.Label, Style = (Style)FindResource("H2") });
            foreach (var o in g.Options)
            {
                System.Windows.Controls.Primitives.ToggleButton c = g.Exclusive
                    ? new RadioButton { GroupName = g.Id }
                    : new CheckBox();
                c.IsChecked = saved?.Contains(o.Id) ?? o.Default;
                c.Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = o.Label },
                        new TextBlock { Text = o.Description, Style = (Style)FindResource("Muted") },
                    },
                };
                OptionsHost.Children.Add(c);
                _optionControls[o.Id] = new(c);
            }
        }
    }

    HashSet<string> Selected() => _optionControls.Where(kv => kv.Value.Control.IsChecked == true).Select(kv => kv.Key).ToHashSet();

    void Show(FrameworkElement view)
    {
        foreach (var v in new FrameworkElement[] { ChoiceView, ProgressView, MissingView, FinishView, GammaView, OutdatedView })
            v.Visibility = v == view ? Visibility.Visible : Visibility.Collapsed;
    }

    async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_manifest is null) return;
        bool doBase = BaseCheck.IsChecked == true && _manifest.Base is not null;
        bool doMods = ModsCheck.IsChecked == true;
        if (!doBase && !doMods) return;
        if (string.IsNullOrWhiteSpace(Mo2Box.Text) || string.IsNullOrWhiteSpace(AnomalyBox.Text))
        {
            MessageBox.Show(this, "Escolha as pastas do MO2 e do Anomaly.", "REIMA", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!doBase && !File.Exists(Path.Combine(Mo2Box.Text, "ModOrganizer.exe")))
        {
            MessageBox.Show(this, "O MO2 não foi encontrado nessa pasta. Marque \"Baixar e configurar o jogo base\".", "REIMA",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (Process.GetProcessesByName("ModOrganizer").Length > 0)
        {
            MessageBox.Show(this, "Feche o Mod Organizer 2 antes de instalar (ele sobrescreve o perfil ao fechar).", "REIMA",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        SaveSettings();

        var settings = BuildSettings();
        var sevenZip = Extractor.Find(settings.Mo2Root);
        if (sevenZip is null)
        {
            MessageBox.Show(this, "7-Zip não encontrado. Instale o 7-Zip ou coloque 7z.exe em tools\\ ao lado do launcher.", "REIMA",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var installer = new Installer(_manifest, settings, new Downloader(_http), new Extractor(sevenZip)) { GitHub = new GitHubSource(_http) };
        installer.Log += AddLog;

        Show(ProgressView);
        LogList.Items.Clear();
        CancelBtn.IsEnabled = true;
        _cts = new CancellationTokenSource();
        var progress = new Progress<StepProgress>(p =>
        {
            StepTitle.Text = p.Title;
            StepDetail.Text = p.Detail;
            OverallBar.Value = p.Overall;
            CurrentBar.IsIndeterminate = p.Current is null;
            if (p.Current is double c) CurrentBar.Value = c;
        });

        try
        {
            if (doBase)
            {
                var aria = Aria2.Find();
                var setup = new BaseSetup(_manifest.Base!, settings, new Downloader(_http), new Extractor(sevenZip),
                    aria is null ? null : new Aria2(aria), _manifest.Profile);
                setup.Log += AddLog;
                await Task.Run(() => setup.RunAsync(progress, _cts.Token));
            }
            if (doMods)
                await Task.Run(() => installer.RunAsync(progress, _cts.Token));
            FinishTitle.Text = doMods ? "Pronto para jogar" : "Jogo base pronto";
            FinishDetail.Text = doMods
                ? $"Abra o MO2, selecione o perfil \"{_manifest.Profile}\" e rode o Anomaly por ele."
                : "Anomaly e Mod Organizer 2 instalados e configurados. Volte e marque \"Instalar os mods\" quando quiser.";
            Show(FinishView);
            UpdateDetect();
        }
        catch (MissingFilesException mf)
        {
            Directory.CreateDirectory(DownloadsDir);
            MissingList.ItemsSource = mf.Missing;
            Show(MissingView);
        }
        catch (OperationCanceledException)
        {
            AddLog("Cancelado.");
            FinishTitle.Text = "Instalação cancelada";
            FinishDetail.Text = "Nada foi perdido: na próxima vez o download continua de onde parou.";
            Show(FinishView);
        }
        catch (Exception ex)
        {
            AddLog("ERRO: " + ex);
            FinishTitle.Text = "Erro na instalação";
            FinishDetail.Text = ex.Message + "\n\nDetalhes no log.";
            Show(FinishView);
        }
        finally
        {
            CancelBtn.IsEnabled = false;
        }
    }

    InstallSettings BuildSettings() => new()
    {
        Mo2Root = Mo2Box.Text.Trim(),
        AnomalyRoot = AnomalyBox.Text.Trim(),
        DownloadsDir = DownloadsDir,
        BaseProfile = string.IsNullOrWhiteSpace(BaseProfileBox.Text) ? null : BaseProfileBox.Text.Trim(),
        Selected = Selected(),
        PreferModDb = PreferModDbCheck.IsChecked == true,
        UsePacks = UsePacksCheck.IsChecked == true,
        PackDir = string.IsNullOrWhiteSpace(PackDirBox.Text) ? null : PackDirBox.Text.Trim(),
        // MO2\downloads + an optional folder with old GAMMA archives: found there = no download
        CacheDirs = new[] { Path.Combine(Mo2Box.Text.Trim(), "downloads"), CacheBox.Text.Trim() }
            .Where(d => d.Length > 0 && Directory.Exists(d)).ToList(),
    };

    void AddLog(string line)
    {
        var stamped = $"[{DateTime.Now:HH:mm:ss}] {line}";
        try { File.AppendAllText(LogPath, stamped + Environment.NewLine); } catch { }
        Dispatcher.BeginInvoke(() =>
        {
            LogList.Items.Add(stamped);
            LogList.ScrollIntoView(stamped);
        });
    }

    void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CancelBtn.IsEnabled = false;
        _cts?.Cancel();
    }

    void Back_Click(object sender, RoutedEventArgs e) => Show(ChoiceView);

    void Launch_Click(object sender, RoutedEventArgs e)
    {
        var exe = Path.Combine(Mo2Box.Text, "ModOrganizer.exe");
        if (!File.Exists(exe)) { MessageBox.Show(this, "ModOrganizer.exe não encontrado.", "REIMA"); return; }
        var args = _manifest is null ? "" : $"-p \"{_manifest.Profile}\"";
        Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true, WorkingDirectory = Mo2Box.Text });
    }

    void OpenDownloads_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(DownloadsDir);
        Process.Start(new ProcessStartInfo(DownloadsDir) { UseShellExecute = true });
    }

    void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(LogPath)) Process.Start(new ProcessStartInfo(LogPath) { UseShellExecute = true });
    }

    /// <summary>One install root: &lt;root&gt;\Anomaly and &lt;root&gt;\MO2.</summary>
    void BrowseRoot_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Pasta de instalação do REIMA (ex.: D:\\Games\\REIMA)" };
        if (dlg.ShowDialog(this) != true) return;
        var root = dlg.FolderName;
        AnomalyBox.Text = Path.Combine(root, "Anomaly");
        Mo2Box.Text = Path.Combine(root, "MO2");
        RootText.Text = root;
    }

    void BrowseMo2_Click(object sender, RoutedEventArgs e) => Browse(Mo2Box);
    void BrowseCache_Click(object sender, RoutedEventArgs e) => Browse(CacheBox);
    void BrowsePacks_Click(object sender, RoutedEventArgs e) => Browse(PackDirBox);
    void BrowseAnomaly_Click(object sender, RoutedEventArgs e) => Browse(AnomalyBox);

    void Browse(TextBox box)
    {
        var dlg = new OpenFolderDialog { InitialDirectory = Directory.Exists(box.Text) ? box.Text : null };
        if (dlg.ShowDialog(this) == true) box.Text = dlg.FolderName;
    }

    static UserSettings? LoadSettings()
    {
        try { return File.Exists(SettingsPath) ? JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(SettingsPath)) : null; }
        catch { return null; }
    }

    void SaveSettings()
    {
        var st = new UserSettings(Mo2Box.Text.Trim(), AnomalyBox.Text.Trim(), BaseProfileBox.Text.Trim(), [.. Selected()], PreferModDbCheck.IsChecked == true, UsePacksCheck.IsChecked == true, PackDirBox.Text.Trim());
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(st, new JsonSerializerOptions { WriteIndented = true }));
    }
}
