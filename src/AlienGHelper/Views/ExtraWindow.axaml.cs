using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AlienGHelper.Core;

namespace AlienGHelper.Views;

/// <summary>
/// Janela "Extra" no estilo do G-Helper: aparência, fail-safe e outras preferências à esquerda,
/// perfis por aplicativo à direita.
/// </summary>
public partial class ExtraWindow : Window
{
    private static readonly IBrush Green = new SolidColorBrush(Color.FromRgb(0x34, 0xA8, 0x53));
    private static readonly IBrush Yellow = new SolidColorBrush(Color.FromRgb(0xF1, 0xC2, 0x32));
    private static readonly IBrush Red = new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36));
    private static readonly IBrush Grey = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));

    private static readonly Dictionary<string, string> StyleHints = new()
    {
        ["Mica"] = "Subtle tint of your wallpaper (Windows 11 default)",
        ["Flat"] = "Solid colors, no transparency (closest to G-Helper)",
    };

    private readonly AppHost _host = null!;
    private readonly AppController _controller = null!;
    private bool _loading = true;

    public ProfilesPage ProfilesView => ProfilesPageView;

    /// <summary>Só para o designer do Avalonia.</summary>
    public ExtraWindow() => InitializeComponent();

    public ExtraWindow(AppHost host)
    {
        InitializeComponent();
        _host = host;
        _controller = host.Controller;
        var s = _controller.Settings;

        // aparência salva: vale para todas as janelas (registradas no AppTheme)
        AppTheme.Apply(this, s.Appearance);

        foreach (var t in AppearanceSettings.Themes) ThemeBox.Items.Add(t);
        foreach (var st in AppearanceSettings.Styles) StyleBox.Items.Add(st);
        ThemeBox.SelectionChanged += (_, _) =>
        {
            if (!_loading && ThemeBox.SelectedItem is string t && t != Appearance.Theme)
                SetAppearance(a => { var n = Copy(a); n.Theme = t; return n; });
        };
        StyleBox.SelectionChanged += (_, _) =>
        {
            if (!_loading && StyleBox.SelectedItem is string st && st != Appearance.Style)
                SetAppearance(a => { var n = Copy(a); n.Style = st; return n; });
        };

        for (int v = 50; v <= 90; v++) GpuLimitBox.Items.Add(v);
        for (int v = 50; v <= 100; v++) CpuLimitBox.Items.Add(v);
        GpuLimitBox.SelectedItem = s.GpuThresholdTemp;
        CpuLimitBox.SelectedItem = s.CpuThresholdTemp;
        FailSafeToggle.IsChecked = s.FailSafeOn;
        MinimizeToggle.IsChecked = s.MinimizeOnClose;

        FailSafeToggle.OnCheckedChanged(FailSafe_Changed);
        MinimizeToggle.OnCheckedChanged(() =>
        {
            if (!_loading) _controller.Settings.MinimizeOnClose = MinimizeToggle.IsChecked == true;
        });
        AutorunToggle.OnCheckedChanged(Autorun_Changed);

        UpdateAppearanceControls();
        AppTheme.Changed += () => { if (IsVisible) UpdateAppearanceControls(); };
        Opened += (_, _) => { UpdateAppearanceControls(); RefreshAutorun(); };

        ProfilesPageView.Initialize(_controller, host.Profiles);
        _controller.Updated += snap => Dispatcher.UIThread.Post(() => ApplySnapshot(snap));

        _loading = false;
        RefreshAutorun();
    }

    // ---------- fail-safe ----------

    private void ApplySnapshot(Snapshot s)
    {
        // verde = ativo, cinza = off, amarelo = já disparou, vermelho = disparado agora
        IBrush color = s.FailSafeEnabled ? Green : Grey;
        string msg = s.FailSafeEnabled ? "Normal" : "Off";
        if (s.LastHighTemp is DateTimeOffset ts)
        {
            color = Yellow;
            msg = $"Last high temp at {ts.LocalDateTime:HH:mm:ss}";
            if (s.FailSafeTripped) { color = Red; msg = "Tripped – Turbo active"; }
        }
        FailSafeDot.Fill = color;
        FailSafeStatus.Text = msg;
    }

    private void FailSafe_SelectionChanged(object? sender, SelectionChangedEventArgs e) => FailSafe_Changed();

    private void FailSafe_Changed()
    {
        if (_loading || GpuLimitBox?.SelectedItem is not int gpu || CpuLimitBox?.SelectedItem is not int cpu) return;
        bool on = FailSafeToggle.IsChecked == true;
        Task.Run(() => _controller.ConfigureFailSafe(on, gpu, cpu));
    }

    // ---------- autorun ----------

    private void RefreshAutorun()
    {
        Task.Run(Autorun.IsEnabled).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            bool was = _loading;
            _loading = true;
            AutorunToggle.IsChecked = t.Result;
            _loading = was;
        }), TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    private void Autorun_Changed()
    {
        if (_loading) return;
        bool want = AutorunToggle.IsChecked == true;
        Task.Run(() => want ? Autorun.Enable() : Autorun.Disable()).ContinueWith(t => Dispatcher.UIThread.Post(async () =>
        {
            if (t.Result == 0) return;
            await Dialogs.ShowAsync(this, "Error", $"Failed to {(want ? "enable" : "disable")} autorun. Error={t.Result}");
            _loading = true;
            AutorunToggle.IsChecked = !want;
            _loading = false;
        }));
    }

    // ---------- restaurar padrões ----------

    private async void ResetAll_Click(object? sender, RoutedEventArgs e)
    {
        if (!await Dialogs.ConfirmAsync(this, "Restore Defaults", "Do you want to reset all settings to default?")) return;

        await Task.Run(_controller.ResetSettings);
        _loading = true;
        var s = _controller.Settings;
        GpuLimitBox.SelectedItem = s.GpuThresholdTemp;
        CpuLimitBox.SelectedItem = s.CpuThresholdTemp;
        FailSafeToggle.IsChecked = s.FailSafeOn;
        MinimizeToggle.IsChecked = s.MinimizeOnClose;
        _loading = false;
        _host.FansIfOpen?.ReloadCurves();
        AppTheme.Apply(this, s.Appearance);
        UpdateAppearanceControls();
    }

    // ---------- aparência ----------

    private AppearanceSettings Appearance => _controller.Settings.Appearance;

    private void SetAppearance(Func<AppearanceSettings, AppearanceSettings> change)
    {
        // objeto novo: o salvamento roda em outra thread
        var next = change(Appearance).Sanitized();
        _controller.Settings.Appearance = next;
        AppTheme.Apply(this, next);
        UpdateAppearanceControls();
    }

    private static AppearanceSettings Copy(AppearanceSettings a) =>
        new() { Theme = a.Theme, Style = a.Style, Accent = a.Accent, FlatBackground = a.FlatBackground, StudioVariant = a.StudioVariant };

    private void UpdateAppearanceControls()
    {
        bool was = _loading;
        _loading = true;
        var a = Appearance;
        ThemeBox.SelectedItem = a.Theme;
        StyleBox.SelectedItem = a.Style;
        StyleHint.Text = StyleHints.TryGetValue(a.Style, out var h) ? h : "";
        AccentHex.Text = a.Accent;
        BuildAccentSwatches();
        _loading = was;
    }

    private void BuildAccentSwatches()
    {
        AccentSwatches.Children.Clear();
        foreach (var hex in AppTheme.AccentPresets)
        {
            AppTheme.TryParseHex(hex, out var c);
            var sq = MakeSwatch(c, hex, string.Equals(hex, Appearance.Accent, StringComparison.OrdinalIgnoreCase));
            sq.PointerReleased += (_, _) => SetAppearance(a => { var n = Copy(a); n.Accent = hex; return n; });
            AccentSwatches.Children.Add(sq);
        }
    }

    /// <summary>Quadrado de cor como os do G-Helper; o escolhido ganha borda clara.</summary>
    private static Border MakeSwatch(Color c, string tip, bool selected)
    {
        var b = new Border
        {
            Width = 22, Height = 22,
            Margin = new Thickness(0, 0, 3, 0),
            Background = new SolidColorBrush(c),
            BorderThickness = new Thickness(selected ? 2 : 1),
            Cursor = new Cursor(StandardCursorType.Hand),
            BorderBrush = selected ? null : new SolidColorBrush(Color.FromArgb(90, 128, 128, 128)),
        };
        if (selected) b.Bind(Border.BorderBrushProperty, b.GetResourceObservable("TextPrimaryBrush"));
        ToolTip.SetTip(b, tip);
        return b;
    }

    private void AccentHex_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitAccentHex(); e.Handled = true; }
    }

    private void AccentHex_LostFocus(object? sender, RoutedEventArgs e) => CommitAccentHex();

    private void CommitAccentHex()
    {
        if (_loading) return;
        string text = (AccentHex.Text ?? "").Trim();
        if (!text.StartsWith('#')) text = "#" + text;
        if (AppTheme.TryParseHex(text, out var c) && !string.Equals(text, Appearance.Accent, StringComparison.OrdinalIgnoreCase))
            SetAppearance(a => { var n = Copy(a); n.Accent = AppTheme.Hex(c); return n; });
        else AccentHex.Text = Appearance.Accent;
    }
}
