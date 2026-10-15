using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace AlienGHelper.Core;

/// <summary>
/// Tecla G-Mode do notebook (VK 0x80 = F17). Usa uma janela "message-only" (NativeWindow do WinForms)
/// para receber WM_HOTKEY sem janela visível. Criar na thread da UI (o loop de mensagens do Avalonia
/// entrega as mensagens a ela).
/// </summary>
public sealed class HotKeyService : IDisposable
{
    public const uint GModeKey = 0x80;
    private const int WM_HOTKEY = 0x0312;
    private const int HotKeyId = 1;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private sealed class Sink : Forms.NativeWindow
    {
        public Action? Hit;
        protected override void WndProc(ref Forms.Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotKeyId) Hit?.Invoke();
            base.WndProc(ref m);
        }
    }

    private readonly Sink _sink = new();
    public bool Registered { get; }
    public event Action? Pressed;

    public HotKeyService(uint virtualKey)
    {
        _sink.CreateHandle(new Forms.CreateParams { Caption = "AlienGHelperHotKeySink", Parent = HWND_MESSAGE });
        _sink.Hit = () => Pressed?.Invoke();
        Registered = RegisterHotKey(_sink.Handle, HotKeyId, 0, virtualKey);
    }

    public void Dispose()
    {
        if (Registered) UnregisterHotKey(_sink.Handle, HotKeyId);
        _sink.DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
