using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AlienGHelper.Core;

namespace AlienGHelper.Views;

public partial class ProfilesPage : UserControl
{
    private AppController _controller = null!;
    private AppProfileWatcher _watcher = null!;
    private string? _selectedExe;       // perfil em edição (pelo executável)
    private bool _gpuTab = true;
    private bool _loading = true;

    public ProfilesPage()
    {
        InitializeComponent();
        Editor.CurveChanged += OnCurveChanged;
        EnabledToggle.OnCheckedChanged(Enabled_Changed);
    }

    public void Initialize(AppController controller, AppProfileWatcher watcher)
    {
        _controller = controller;
        _watcher = watcher;
        EnabledToggle.IsChecked = controller.Settings.AppProfilesEnabled;
        _watcher.ActiveChanged += _ => Dispatcher.UIThread.Post(() => { UpdateStatus(); BuildList(); });
        _loading = false;

        RefreshRunning();
        BuildList();
        UpdateStatus();
        PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty && IsVisible) { RefreshRunning(); UpdateStatus(); BuildList(); } };
    }

    /// <summary>Redesenha o que usa a cor de destaque (chamado quando a aparência muda).</summary>
    public void RefreshTheme()
    {
        if (_loading) return;
        BuildList();
        UpdateEditor();
    }

    // ---------- dados ----------

    private List<AppProfile> Profiles => _controller.Settings.AppProfiles;

    private AppProfile? Selected =>
        _selectedExe is null ? null
            : Profiles.FirstOrDefault(p => string.Equals(p.Exe, _selectedExe, StringComparison.OrdinalIgnoreCase));

    /// <summary>A lista é sempre trocada por uma nova (o salvamento e o vigia leem em outras threads).</summary>
    private void SaveProfiles(IEnumerable<AppProfile> list)
    {
        _controller.Settings.AppProfiles = list.Take(AppProfile.MaxProfiles).ToList();
        _watcher.Refresh();
        BuildList();
        UpdateEditor();
    }

    private void Replace(AppProfile updated) =>
        SaveProfiles(Profiles.Select(p =>
            string.Equals(p.Exe, updated.Exe, StringComparison.OrdinalIgnoreCase) ? updated : p));

    private void AddProfile(string exe)
    {
        var p = new AppProfile
        {
            Exe = exe,
            Mode = ThermalMode.Custom,
            CpuCurve = _controller.Settings.CpuCurve.Clone(),
            GpuCurve = _controller.Settings.GpuCurve.Clone(),
        }.Sanitized();
        if (p.Exe.Length == 0) return;

        if (Profiles.Any(x => string.Equals(x.Exe, p.Exe, StringComparison.OrdinalIgnoreCase)))
        {
            _selectedExe = p.Exe;   // já existe: só seleciona
            BuildList();
            UpdateEditor();
            return;
        }
        if (Profiles.Count >= AppProfile.MaxProfiles)
        {
            _ = Dialogs.ShowAsync(TopLevel.GetTopLevel(this) as Window, "App Profiles",
                $"You can have up to {AppProfile.MaxProfiles} profiles.");
            return;
        }
        _selectedExe = p.Exe;
        SaveProfiles(Profiles.Append(p));
    }

    // ---------- UI ----------

    private void RefreshRunning()
    {
        var apps = AppProfileWatcher.RunningApps();
        RunningBox.ItemsSource = apps;
        if (apps.Count > 0 && RunningBox.SelectedIndex < 0) RunningBox.SelectedIndex = 0;
    }

    private void UpdateStatus()
    {
        var active = _controller.ActiveProfile;
        bool on = _controller.Settings.AppProfilesEnabled;
        StatusDot.Fill = active is not null ? AppTheme.AccentBrush
                       : new SolidColorBrush(on ? Color.FromRgb(0x80, 0x80, 0x80) : Color.FromRgb(0x60, 0x60, 0x60));
        StatusText.Text = !on ? "Profiles are turned off"
                        : active is null ? "No profile active"
                        : $"Active: {active.Name} · {active.Mode.DisplayName()}";
    }

    private void BuildList()
    {
        ProfileList.Children.Clear();
        var active = _controller.ActiveProfile;
        EmptyText.IsVisible = Profiles.Count == 0;
        if (Selected is null) _selectedExe = null;

        // o editor fica dentro da lista, logo abaixo do perfil selecionado (sai do lugar original na 1ª vez)
        if (EditorCard.Parent is Panel owner) owner.Children.Remove(EditorCard);

        foreach (var p in Profiles)
        {
            bool sel = string.Equals(p.Exe, _selectedExe, StringComparison.OrdinalIgnoreCase);
            bool running = active is not null && string.Equals(active.Exe, p.Exe, StringComparison.OrdinalIgnoreCase);
            ProfileList.Children.Add(MakeRow(p, sel, running));
            if (sel) ProfileList.Children.Add(EditorCard);
        }
        EditorCard.IsVisible = Selected is not null;
    }

    private Border MakeRow(AppProfile p, bool selected, bool running)
    {
        var name = new TextBlock { Text = p.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var exe = new TextBlock { Text = p.Exe, FontSize = 11, Margin = new Thickness(0, 2, 0, 0) };
        exe.Classes.Add("tertiary");
        var info = new StackPanel();
        info.Children.Add(name);
        info.Children.Add(exe);

        var mode = new Border
        {
            Margin = new Thickness(10, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = p.Mode.DisplayName(), FontSize = 11, FontWeight = FontWeight.SemiBold },
        };
        mode.Classes.Add("badge");

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var chevron = new TextBlock
        {
            Text = selected ? "\uE70E" : "\uE70D",   // ChevronUp / ChevronDown
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 11,
            Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        chevron.Classes.Add("tertiary");
        if (running)
        {
            var live = new TextBlock
            {
                Text = "● running", FontSize = 11, FontWeight = FontWeight.SemiBold,
                Foreground = AppTheme.AccentBrush, VerticalAlignment = VerticalAlignment.Center,
            };
            right.Children.Add(live);
        }
        right.Children.Add(mode);

        var delete = new Button
        {
            Content = new TextBlock { Text = "\uE74D", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 13 },
            Width = 32, Height = 30, Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
        };
        delete.Classes.Add("subtle");
        ToolTip.SetTip(delete, "Delete profile");
        delete.Click += (_, e) =>
        {
            e.Handled = true;
            if (string.Equals(_selectedExe, p.Exe, StringComparison.OrdinalIgnoreCase)) _selectedExe = null;
            SaveProfiles(Profiles.Where(x => !ReferenceEquals(x, p)));
        };
        right.Children.Add(delete);
        right.Children.Add(chevron);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(info);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);

        var row = new Border
        {
            Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(14, 10, 8, 10),
            CornerRadius = new CornerRadius(8), Child = grid,
        };
        row.Classes.Add("item");
        row.Classes.Set("active", selected);
        row.PointerReleased += (_, e) =>
        {
            if (e.Handled) return;
            // clicar no perfil aberto recolhe; clicar em outro abre aquele
            _selectedExe = selected ? null : p.Exe;
            BuildList();
            UpdateEditor();
        };
        ToolTip.SetTip(row, selected ? "Click to collapse" : "Click to edit");
        return row;
    }

    private void UpdateEditor()
    {
        var p = Selected;
        EditorCard.IsVisible = p is not null;
        if (p is null) return;

        if (!NameBox.IsKeyboardFocusWithin) NameBox.Text = p.Name;
        ExeText.Text = p.Exe;
        ModeQuiet.SetActive(p.Mode == ThermalMode.Quiet);
        ModeAuto.SetActive(p.Mode == ThermalMode.Balanced);
        ModeG.SetActive(p.Mode == ThermalMode.GMode);
        ModeManual.SetActive(p.Mode == ThermalMode.Custom);
        ModeHint.Text = p.Mode switch
        {
            ThermalMode.Custom => "Fans follow this profile's own curves while the app is running.",
            ThermalMode.GMode => "Turbo (G Mode): fans and power at maximum while the app is running.",
            ThermalMode.Quiet => "Silent: Dell's quiet profile while the app is running.",
            _ => "Dell's automatic fan control while the app is running.",
        };

        PowNone.SetActive(p.PowerPreset is null);
        PowEco.SetActive(p.PowerPreset == "Eco");
        PowBalanced.SetActive(p.PowerPreset == "Balanced");
        PowPerformance.SetActive(p.PowerPreset == "Performance");

        CurvePanel.IsVisible = p.Mode == ThermalMode.Custom;
        TabGpu.SetActive(_gpuTab);
        TabCpu.SetActive(!_gpuTab);
        Editor.SetCurve(_gpuTab ? p.GpuCurve : p.CpuCurve);
        Editor.InvalidateVisual();
    }

    // ---------- eventos ----------

    private void Enabled_Changed()
    {
        if (_loading) return;
        _controller.Settings.AppProfilesEnabled = EnabledToggle.IsChecked == true;
        _watcher.Refresh();
        UpdateStatus();
    }

    private void RefreshRunning_Click(object? sender, RoutedEventArgs e) => RefreshRunning();

    private void AddRunning_Click(object? sender, RoutedEventArgs e)
    {
        if (RunningBox.SelectedItem is RunningApp app) AddProfile(app.Exe);
    }

    private async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Pick the game or app executable",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Programs") { Patterns = new[] { "*.exe" } } },
        });
        if (files.Count > 0) AddProfile(Path.GetFileName(files[0].Name));
    }

    private void NameBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitName(); TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus(); e.Handled = true; }
    }

    private void NameBox_LostFocus(object? sender, RoutedEventArgs e) => CommitName();

    private void CommitName()
    {
        if (Selected is not { } p) return;
        string name = (NameBox.Text ?? "").Trim();
        if (name.Length == 0 || name == p.Name) { NameBox.Text = p.Name; return; }
        Replace(p.With(x => x.Name = name));
    }

    private void Mode_Click(object? sender, PointerReleasedEventArgs e)
    {
        if (Selected is not { } p || sender is not Control { Tag: string tag }) return;
        var mode = tag switch
        {
            "Quiet" => ThermalMode.Quiet,
            "GMode" => ThermalMode.GMode,
            "Custom" => ThermalMode.Custom,
            _ => ThermalMode.Balanced,
        };
        if (mode != p.Mode) Replace(p.With(x => x.Mode = mode));
    }

    private void Power_Click(object? sender, PointerReleasedEventArgs e)
    {
        if (Selected is not { } p || sender is not Control { Tag: string tag }) return;
        string? preset = tag.Length == 0 ? null : tag;
        if (preset != p.PowerPreset) Replace(p.With(x => x.PowerPreset = preset));
    }

    private void Tab_Click(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Control { Tag: string tag })
        {
            _gpuTab = tag == "Gpu";
            UpdateEditor();
        }
    }

    private void CopyMain_Click(object? sender, RoutedEventArgs e)
    {
        if (Selected is not { } p) return;
        var s = _controller.Settings;
        Replace(p.With(x => { x.CpuCurve = s.CpuCurve.Clone(); x.GpuCurve = s.GpuCurve.Clone(); }));
    }

    private void OnCurveChanged(FanCurve curve)
    {
        if (Selected is not { } p) return;
        Replace(p.With(x => { if (_gpuTab) x.GpuCurve = curve; else x.CpuCurve = curve; }));
    }
}
