using System.IO;
using System.Diagnostics;
using System.Text;

namespace AlienGHelper.Core;

/// <summary>
/// Inicia o app como administrador no logon via Agendador de Tarefas.
/// Gera o XML com "não parar na bateria" (essencial em notebook).
/// </summary>
public static class Autorun
{
    private const string TaskName = "AlienGHelper";
    /// <summary>Nome usado antes do "Alien G-Helper"; só serve para migrar.</summary>
    private const string LegacyTaskName = "TCC_G15";

    public static bool IsEnabled() => RunSchtasks($"/query /tn {TaskName}") == 0;

    /// <summary>
    /// Se a tarefa antiga (TCC_G15) aponta para este .exe, troca pela tarefa nova.
    /// Tarefas antigas de outro programa (ex.: a edição completa) ficam como estão.
    /// </summary>
    public static void MigrateLegacy()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || IsEnabled()) return;
            if (RunSchtasks($"/query /tn {LegacyTaskName} /xml", out string xml) != 0) return;
            if (xml.IndexOf(System.Security.SecurityElement.Escape(exe), StringComparison.OrdinalIgnoreCase) < 0
                && xml.IndexOf(exe, StringComparison.OrdinalIgnoreCase) < 0) return;
            if (Enable() == 0) RunSchtasks($"/delete /tn {LegacyTaskName} /f");
        }
        catch { }
    }

    public static int Disable() => RunSchtasks($"/delete /tn {TaskName} /f");

    public static int Enable()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return -100;

        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers>
                <LogonTrigger><Enabled>true</Enabled></LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Enabled>true</Enabled>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{System.Security.SecurityElement.Escape(exe)}</Command>
                  <Arguments>--minimized</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;

        string tmp = Path.Combine(Path.GetTempPath(), "alienghelper_task.xml");
        File.WriteAllText(tmp, xml, Encoding.Unicode); // schtasks espera UTF-16
        try
        {
            Disable();
            return RunSchtasks($"/create /xml \"{tmp}\" /tn {TaskName}");
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    private static int RunSchtasks(string args) => RunSchtasks(args, out _);

    private static int RunSchtasks(string args, out string output)
    {
        output = "";
        try
        {
            var psi = new ProcessStartInfo("schtasks", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            output = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode;
        }
        catch
        {
            return -1;
        }
    }
}
