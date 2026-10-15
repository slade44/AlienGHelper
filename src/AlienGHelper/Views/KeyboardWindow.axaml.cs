using Avalonia.Controls;

namespace AlienGHelper.Views;

/// <summary>Janela do teclado (como a do ROG Azoth no G-Helper). A lógica fica na <see cref="KeyboardPage"/>.</summary>
public partial class KeyboardWindow : Window
{
    public KeyboardPage Page => PageView;

    /// <summary>Só para o designer do Avalonia.</summary>
    public KeyboardWindow() => InitializeComponent();

    public KeyboardWindow(AppHost host)
    {
        InitializeComponent();
        AppTheme.Register(this);
        // inicializa já no começo: aplica ao iniciar, grava na memória do teclado e reaplica ao acordar
        PageView.Initialize(host.Controller.Settings, host.Lighting);
    }
}
