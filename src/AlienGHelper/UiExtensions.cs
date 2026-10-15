using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace AlienGHelper;

/// <summary>Atalhos para ligar eventos por código (evita disparos durante o InitializeComponent).</summary>
public static class UiExtensions
{
    /// <summary>Chamado quando o valor do slider muda.</summary>
    public static void OnValueChanged(this RangeBase slider, Action action) =>
        slider.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) action(); };

    /// <summary>Chamado quando um CheckBox / ToggleSwitch é marcado ou desmarcado.</summary>
    public static void OnCheckedChanged(this ToggleButton toggle, Action action) =>
        toggle.PropertyChanged += (_, e) => { if (e.Property == ToggleButton.IsCheckedProperty) action(); };

    /// <summary>Marca/desmarca o "segmento ativo" de um seletor (Border.seg).</summary>
    public static void SetActive(this StyledElement e, bool active) => e.Classes.Set("active", active);
}
