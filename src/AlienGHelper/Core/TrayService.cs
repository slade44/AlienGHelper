using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace TccG15.Core;

/// <summary>Ícone de bandeja que mostra GPU/CPU em tempo real (port de QGaugeTrayIcon).</summary>
public sealed class TrayService : IDisposable
{
    private static readonly System.Globalization.NumberFormatInfo Dots = new() { NumberGroupSeparator = "." };
    private static readonly (int Warn, int Crit) GpuLimits = (72, 85);
    private static readonly (int Warn, int Crit) CpuLimits = (85, 95);

    private readonly Forms.NotifyIcon _notify;
    private readonly Dictionary<ThermalMode, Forms.ToolStripMenuItem> _modeItems = new();
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.ToolStripLabel _header;
    private readonly TrayMenuRenderer _renderer = new();
    private Icon? _icon;

    public event Action<ThermalMode>? ModeRequested;
    public event Action? ShowRequested;
    public event Action? ExitRequested;

    public TrayService()
    {
        _menu = new Forms.ContextMenuStrip
        {
            Renderer = _renderer,
            ShowImageMargin = false,
            ShowCheckMargin = false,
            Padding = new Forms.Padding(4, 6, 4, 6),
            Font = new Font("Segoe UI", 9.5f),
        };

        // linha de status (temperaturas) no topo
        _header = new Forms.ToolStripLabel("GPU -- °C · CPU -- °C")
        {
            Font = new Font("Segoe UI Semibold", 9f),
            Padding = new Forms.Padding(8, 4, 8, 6),
            Tag = TrayMenuRenderer.HeaderTag,
        };
        _menu.Items.Add(_header);
        _menu.Items.Add(new Forms.ToolStripSeparator());

        _menu.Items.Add(new Forms.ToolStripLabel("MODE")
        {
            Font = new Font("Segoe UI Semibold", 8f),
            Padding = new Forms.Padding(8, 4, 8, 2),
            Tag = TrayMenuRenderer.SectionTag,
        });
        foreach (var m in new[] { ThermalMode.Quiet, ThermalMode.Balanced, ThermalMode.GMode, ThermalMode.Custom })
        {
            var mode = m;
            var item = MakeItem(ModeName(mode));
            item.Click += (_, _) => ModeRequested?.Invoke(mode);
            _modeItems[mode] = item;
            _menu.Items.Add(item);
        }
        _menu.Items.Add(new Forms.ToolStripSeparator());

        var show = MakeItem("Open Alien G-Helper");
        show.Font = new Font("Segoe UI Semibold", 9.5f);
        show.Click += (_, _) => ShowRequested?.Invoke();
        _menu.Items.Add(show);

        var exit = MakeItem("Exit");
        exit.Click += (_, _) => ExitRequested?.Invoke();
        _menu.Items.Add(exit);

        // cantos arredondados do Windows 11 no menu
        _menu.Opened += (_, _) => TrayMenuRenderer.RoundCorners(_menu.Handle);

        _notify = new Forms.NotifyIcon
        {
            ContextMenuStrip = _menu,
            Text = "Alien G-Helper",
            Visible = true,
        };
        _notify.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) ShowRequested?.Invoke();
        };
        UpdateIcon(null, null, false);
    }

    private static Forms.ToolStripMenuItem MakeItem(string text) => new(text)
    {
        Padding = new Forms.Padding(10, 0, 10, 0),
        AutoSize = false,
        Height = 32,
        Width = 230,
    };

    /// <summary>Cores do menu iguais às do app (tema claro/escuro e cor de destaque).</summary>
    public void ApplyTheme(bool dark, byte r, byte g, byte b)
    {
        _renderer.SetTheme(dark, Color.FromArgb(r, g, b));
        _menu.BackColor = _renderer.Background;
        _menu.ForeColor = _renderer.Text;
        foreach (Forms.ToolStripItem item in _menu.Items) item.ForeColor = _renderer.Text;
        _menu.Invalidate();
    }

    public static string ModeName(ThermalMode m) => m.DisplayName();

    public void Update(Snapshot s)
    {
        foreach (var (mode, item) in _modeItems) item.Checked = mode == s.Mode;
        _header.Text = $"GPU {s.GpuTemp?.ToString() ?? "--"} °C  ·  CPU {s.CpuTemp?.ToString() ?? "--"} °C" +
                       (s.ProfileName is null ? "" : $"  ·  {s.ProfileName}");
        UpdateIcon(s.GpuTemp, s.CpuTemp, s.Mode == ThermalMode.GMode);
        string tip = "Alien G-Helper\n" +
            $"GPU: {s.GpuTemp?.ToString() ?? "-"} °C  {s.GpuRpm?.ToString("#,0", Dots) ?? "-"} RPM\n" +
            $"CPU: {s.CpuTemp?.ToString() ?? "-"} °C  {s.CpuRpm?.ToString("#,0", Dots) ?? "-"} RPM\n" +
            $"Fan Control: {ModeName(s.Mode)}";
        _notify.Text = tip.Length > 127 ? tip[..127] : tip;
    }

    public void ShowBalloon(string title, string text, bool warning) =>
        _notify.ShowBalloonTip(4000, title, text, warning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);

    // ---------- desenho do ícone ----------

    /// <summary>Ícone da bandeja: o alienígena do app (Resources/app.ico), no tamanho pequeno do sistema.</summary>
    private void UpdateIcon(int? gpu, int? cpu, bool gMode)
    {
        if (_icon is not null) return;   // fixo; temperaturas, RPM e modo ficam na dica
        int size = Math.Max(16, Forms.SystemInformation.SmallIconSize.Width);
        using var stream = typeof(TrayService).Assembly.GetManifestResourceStream("TccG15.app.ico");
        if (stream is null) return;
        _icon = new Icon(stream, size, size);
        _notify.Icon = _icon;
    }

    public void Dispose()
    {
        _notify.Visible = false;
        _notify.Dispose();
        _icon?.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
