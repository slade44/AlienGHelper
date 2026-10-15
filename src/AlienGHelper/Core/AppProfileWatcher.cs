using System.Diagnostics;

namespace AlienGHelper.Core;

/// <summary>App com janela aberta (para a lista "Running apps").</summary>
public sealed record RunningApp(string Label, string Exe);

/// <summary>
/// A cada 3 s confere quais processos estão abertos e ativa o primeiro perfil (na ordem da lista)
/// cujo executável estiver rodando. Sem nenhum aberto, desativa o perfil.
/// </summary>
public sealed class AppProfileWatcher : IDisposable
{
    private const int PeriodMs = 3000;

    private readonly AppController _controller;
    private readonly Timer _timer;
    private int _busy;

    /// <summary>Disparado (em background) quando o perfil ativo muda: nome do perfil ou null.</summary>
    public event Action<string?>? ActiveChanged;

    public AppProfileWatcher(AppController controller)
    {
        _controller = controller;
        _timer = new Timer(_ => Check(), null, 1000, PeriodMs);
    }

    /// <summary>Reavalia agora (ex.: logo depois de editar a lista de perfis).</summary>
    public void Refresh() => ThreadPool.QueueUserWorkItem(_ => Check());

    private void Check()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var settings = _controller.Settings;
            var profiles = settings.AppProfiles;   // a lista é trocada inteira, então ler a referência é seguro
            AppProfile? match = null;

            if (settings.AppProfilesEnabled && profiles.Count > 0)
            {
                var running = RunningProcessNames();
                match = profiles.FirstOrDefault(p => running.Contains(p.ProcessName));
            }

            var before = _controller.ActiveProfile;
            _controller.ActivateProfile(match);
            if (!ReferenceEquals(before, match) &&
                !string.Equals(before?.Exe, match?.Exe, StringComparison.OrdinalIgnoreCase))
                ActiveChanged?.Invoke(match?.Name);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Profile check failed: {ex}");
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private static HashSet<string> RunningProcessNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try { names.Add(p.ProcessName); }
            catch { }
            finally { p.Dispose(); }
        }
        return names;
    }

    /// <summary>Apps com janela abertos agora (para a lista "Running apps").</summary>
    public static List<RunningApp> RunningApps()
    {
        var result = new List<RunningApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int self = Environment.ProcessId;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == self || p.MainWindowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(p.MainWindowTitle)) continue;
                if (!seen.Add(p.ProcessName)) continue;
                string title = p.MainWindowTitle.Length > 50 ? p.MainWindowTitle[..50] + "…" : p.MainWindowTitle;
                result.Add(new RunningApp($"{title}  ({p.ProcessName}.exe)", p.ProcessName + ".exe"));
            }
            catch { }
            finally { p.Dispose(); }
        }
        return result.OrderBy(r => r.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public void Dispose() => _timer.Dispose();
}
