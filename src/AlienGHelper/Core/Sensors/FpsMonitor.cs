using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace TccG15.Core.Sensors;

/// <summary>Leitura do contador de FPS para a UI.</summary>
public sealed record FpsSnapshot(
    string Process, double Fps, double FrameTimeMs, double? Low1Fps, double[] RecentFrameTimes);

/// <summary>
/// Contador de FPS / frametime pelo mesmo caminho do PresentMon: uma sessão ETW em tempo real
/// ouvindo os eventos "Present" do DXGI (DirectX 10/11/12) e do Direct3D 9. Cada Present de um
/// processo é um quadro; o intervalo entre dois Presents é o frametime.
/// Precisa de administrador (o app já roda assim). Vulkan/OpenGL sem DXGI não aparecem.
/// O jogo escolhido é o da janela em primeiro plano; se for este app, fica o último jogo visto.
/// </summary>
public sealed class FpsMonitor : IDisposable
{
    private const string SessionName = "TccG15-FPS";
    private static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3D9Provider = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    private const int DxgiPresentStart = 42;
    private const int D3D9PresentStart = 1;

    private const double KeepMs = 5000;     // histórico por processo
    private const double ActiveMs = 2000;   // sem Present por mais que isso = processo parado

    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        "dwm", "explorer", "TccG15", "ApplicationFrameHost", "SearchHost", "StartMenuExperienceHost",
        "ShellExperienceHost", "TextInputHost", "msedgewebview2", "steamwebhelper", "Discord",
    };

    private sealed class Track
    {
        public readonly Queue<double> Times = new();   // timestamps dos Presents (ms, relógio do ETW)
        public double LastMs;
    }

    private readonly object _gate = new();
    private readonly Dictionary<int, Track> _tracks = new();
    private readonly Dictionary<int, string> _names = new();
    private readonly Stopwatch _clock = new();
    private double _clockOffset = double.NaN;     // relógio do ETW − Stopwatch
    private TraceEventSession? _session;
    private Thread? _thread;
    private int _lastGame;

    public bool IsRunning => _session is not null;
    public string? Error { get; private set; }

    public bool Start()
    {
        if (_session is not null) return true;
        Error = null;
        try
        {
            if (!(TraceEventSession.IsElevated() ?? false))
            {
                Error = "The FPS monitor needs administrator rights.";
                return false;
            }
            // uma sessão com o mesmo nome (de um crash anterior) é fechada e recriada
            // buffers pequenos: só eventos de Present (o padrão do TraceEvent reserva dezenas de MB)
            var s = new TraceEventSession(SessionName) { StopOnDispose = true, BufferSizeMB = 4 };
            s.EnableProvider(DxgiProvider, TraceEventLevel.Informational);
            s.EnableProvider(D3D9Provider, TraceEventLevel.Informational);
            s.Source.AllEvents += OnEvent;
            _session = s;
            _clock.Restart();
            _clockOffset = double.NaN;
            _thread = new Thread(() =>
            {
                try { s.Source.Process(); }
                catch (Exception ex) { Error = ex.Message; }
            }) { IsBackground = true, Name = "FPS monitor (ETW)" };
            _thread.Start();
            return true;
        }
        catch (Exception ex)
        {
            Error = $"Could not start the FPS monitor: {ex.Message}";
            Stop();
            return false;
        }
    }

    public void Stop()
    {
        var s = _session;
        _session = null;
        try { s?.Dispose(); } catch { }
        _thread = null;
        lock (_gate) { _tracks.Clear(); _lastGame = 0; }
    }

    private void OnEvent(TraceEvent e)
    {
        int id = (int)e.ID;
        bool present = (id == DxgiPresentStart && e.ProviderGuid == DxgiProvider)
                    || (id == D3D9PresentStart && e.ProviderGuid == D3D9Provider);
        if (!present) return;

        double t = e.TimeStampRelativeMSec;
        lock (_gate)
        {
            if (double.IsNaN(_clockOffset)) _clockOffset = t - _clock.Elapsed.TotalMilliseconds;
            if (!_tracks.TryGetValue(e.ProcessID, out var tr)) _tracks[e.ProcessID] = tr = new Track();
            tr.Times.Enqueue(t);
            tr.LastMs = t;
            while (tr.Times.Count > 0 && t - tr.Times.Peek() > KeepMs) tr.Times.Dequeue();
        }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int pid);

    /// <summary>Estado atual; null = nenhum jogo apresentando quadros agora.</summary>
    public FpsSnapshot? Read()
    {
        if (_session is null) return null;
        GetWindowThreadProcessId(GetForegroundWindow(), out int fg);

        lock (_gate)
        {
            if (double.IsNaN(_clockOffset)) return null;
            double now = _clock.Elapsed.TotalMilliseconds + _clockOffset;

            // descarta processos parados
            foreach (var pid in _tracks.Where(kv => now - kv.Value.LastMs > KeepMs).Select(kv => kv.Key).ToList())
                _tracks.Remove(pid);

            bool Active(int pid) => _tracks.TryGetValue(pid, out var tr) && now - tr.LastMs < ActiveMs
                                    && tr.Times.Count >= 10 && !Ignored.Contains(NameOf(pid));

            int target = Active(fg) ? fg
                : Active(_lastGame) ? _lastGame
                : _tracks.Keys.Where(Active).OrderByDescending(p => _tracks[p].Times.Count).FirstOrDefault();
            if (target == 0) return null;
            _lastGame = target;

            var times = _tracks[target].Times.ToArray();
            var ft = new double[times.Length - 1];
            for (int i = 1; i < times.Length; i++) ft[i - 1] = times[i] - times[i - 1];
            if (ft.Length < 2) return null;

            // FPS do último segundo; 1% low sobre os 5 s guardados
            double last = times[^1];
            int inSecond = times.Count(x => last - x <= 1000);
            double span = Math.Max(1, last - times.First(x => last - x <= 1000));
            double fps = inSecond > 1 ? (inSecond - 1) * 1000.0 / span : 0;
            double avgFt = ft.Skip(Math.Max(0, ft.Length - Math.Max(1, inSecond - 1))).Average();

            double? low1 = null;
            if (ft.Length >= 100)
            {
                var sorted = ft.OrderByDescending(x => x).ToArray();
                int n = Math.Max(1, sorted.Length / 100);
                low1 = 1000.0 / sorted.Take(n).Average();
            }

            var recent = ft.Skip(Math.Max(0, ft.Length - 240)).ToArray();
            return new FpsSnapshot(NameOf(target), fps, avgFt, low1, recent);
        }
    }

    private string NameOf(int pid)
    {
        if (pid == 0) return "";
        if (_names.TryGetValue(pid, out var n)) return n;
        try { n = Process.GetProcessById(pid).ProcessName; }
        catch { n = $"PID {pid}"; }
        _names[pid] = n;
        return n;
    }

    public void Dispose() => Stop();
}
