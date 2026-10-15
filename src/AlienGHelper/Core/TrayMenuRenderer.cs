using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace TccG15.Core;

/// <summary>
/// Desenho do menu da bandeja no estilo do app: fundo do tema, destaque arredondado ao passar o mouse,
/// marca de seleção na cor de destaque e cantos arredondados do Windows 11.
/// </summary>
internal sealed class TrayMenuRenderer : Forms.ToolStripRenderer
{
    public const string HeaderTag = "header";
    public const string SectionTag = "section";

    public Color Background { get; private set; } = Color.FromArgb(0x2B, 0x2B, 0x2B);
    public Color Text { get; private set; } = Color.FromArgb(0xF3, 0xF3, 0xF3);
    private Color _muted = Color.FromArgb(0x9A, 0x9A, 0x9A);
    private Color _hover = Color.FromArgb(0x3A, 0x3A, 0x3A);
    private Color _separator = Color.FromArgb(0x40, 0x40, 0x40);
    private Color _border = Color.FromArgb(0x48, 0x48, 0x48);
    private Color _accent = Color.FromArgb(0xA6, 0xE0, 0x8E);

    public TrayMenuRenderer() => SetTheme(SystemIsDark(), _accent);

    public void SetTheme(bool dark, Color accent)
    {
        _accent = accent;
        if (dark)
        {
            Background = Color.FromArgb(0x2B, 0x2B, 0x2B);
            Text = Color.FromArgb(0xF3, 0xF3, 0xF3);
            _muted = Color.FromArgb(0x9A, 0x9A, 0x9A);
            _hover = Color.FromArgb(0x3A, 0x3A, 0x3A);
            _separator = Color.FromArgb(0x40, 0x40, 0x40);
            _border = Color.FromArgb(0x48, 0x48, 0x48);
        }
        else
        {
            Background = Color.FromArgb(0xF9, 0xF9, 0xF9);
            Text = Color.FromArgb(0x1A, 0x1A, 0x1A);
            _muted = Color.FromArgb(0x6E, 0x6E, 0x6E);
            _hover = Color.FromArgb(0xEA, 0xEA, 0xEA);
            _separator = Color.FromArgb(0xE1, 0xE1, 0xE1);
            _border = Color.FromArgb(0xD5, 0xD5, 0xD5);
        }
    }

    /// <summary>Tema do Windows (para quando a janela ainda não aplicou o tema do app).</summary>
    public static bool SystemIsDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v ? v == 0 : true;
        }
        catch { return true; }
    }

    // ---------- desenho ----------

    protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
    {
        using var b = new SolidBrush(Background);
        e.Graphics.FillRectangle(b, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(_border);
        var r = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        e.Graphics.DrawRectangle(pen, r);
    }

    protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2);
        using var path = Rounded(r, 5);
        using var b = new SolidBrush(_hover);
        g.FillPath(b, path);
    }

    protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
    {
        var tag = e.Item.Tag as string;
        Color color = tag is SectionTag or HeaderTag ? _muted : Text;
        if (!e.Item.Enabled) color = _muted;

        var rect = e.TextRectangle;
        if (e.Item is Forms.ToolStripMenuItem) rect.X = 28;   // espaço para a marca de seleção

        e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        Forms.TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, rect, color,
            Forms.TextFormatFlags.Left | Forms.TextFormatFlags.VerticalCenter | Forms.TextFormatFlags.EndEllipsis);

        // marca do modo atual: barrinha + ponto na cor de destaque
        if (e.Item is Forms.ToolStripMenuItem { Checked: true })
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int cy = e.Item.Height / 2;
            using var b = new SolidBrush(_accent);
            using var bar = Rounded(new Rectangle(5, cy - 8, 3, 16), 1);
            g.FillPath(b, bar);
            g.FillEllipse(b, 14, cy - 3, 6, 6);
        }
    }

    protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
    {
        using var pen = new Pen(_separator);
        int y = e.Item.Height / 2;
        e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
    }

    protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e) { }   // desenhado no texto

    protected override void OnRenderLabelBackground(Forms.ToolStripItemRenderEventArgs e) { }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        int d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // ---------- cantos arredondados (Windows 11) ----------

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void RoundCorners(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            int pref = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
        catch (DllNotFoundException) { }
    }
}
