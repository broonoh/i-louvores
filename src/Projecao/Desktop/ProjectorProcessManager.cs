using System.Diagnostics;
using System.Globalization;
using Projecao.Models;
using Projecao.Services;

namespace Projecao.Desktop;

/// <summary>
/// Implementação Linux de <see cref="IProjectionWindowManager"/>: a janela do projetor
/// é um processo filho (o próprio executável com <c>--projetor</c>).
///
/// Porquê um processo à parte? No Linux o Photino corre cada janela extra num loop
/// GTK aninhado que bloqueia a thread de UI. Com um processo por janela isso não
/// acontece, e se o projetor falhar o painel continua a funcionar.
/// </summary>
public sealed class ProjectorProcessManager(
    ProjectionService projection,
    LocalAccess access,
    ServerInfo server,
    ILogger<ProjectorProcessManager> logger) : IProjectionWindowManager, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;

    public bool IsOpen => _process is { HasExited: false };

    public MonitorInfo? CurrentMonitor { get; private set; }

    public async Task OpenAsync(MonitorInfo monitor)
    {
        await _gate.WaitAsync();
        try
        {
            if (_process is { HasExited: false } running)
            {
                // Já aberto: só muda de monitor (sem recarregar a página).
                await running.StandardInput.WriteLineAsync(
                    string.Create(CultureInfo.InvariantCulture, $"monitor {monitor.X} {monitor.Y}"));
                await running.StandardInput.FlushAsync();
            }
            else
            {
                _process = Start(monitor);
            }

            CurrentMonitor = monitor;
            logger.LogInformation("Projetor em {Monitor}", monitor.Description);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CloseAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_process is not { HasExited: false } process)
                return;

            try
            {
                await process.StandardInput.WriteLineAsync("close");
                await process.StandardInput.FlushAsync();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or InvalidOperationException)
            {
                logger.LogWarning("Projetor não fechou a tempo; a terminar o processo");
                process.Kill(entireProcessTree: true);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private Process Start(MonitorInfo monitor)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath ?? throw new InvalidOperationException("Caminho do executável desconhecido."),
            RedirectStandardInput = true,
            UseShellExecute = false
        };

        // Se corremos via "dotnet Projecao.dll", o ProcessPath é o próprio dotnet.
        if (Path.GetFileNameWithoutExtension(psi.FileName) == "dotnet")
            psi.ArgumentList.Add(typeof(ProjectorProcessManager).Assembly.Location);

        psi.ArgumentList.Add(ProjectorHost.Flag);
        psi.ArgumentList.Add("--url");
        psi.ArgumentList.Add(access.WithToken($"{server.BaseUrl}/projetor"));
        psi.ArgumentList.Add("--x");
        psi.ArgumentList.Add(monitor.X.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--y");
        psi.ArgumentList.Add(monitor.Y.ToString(CultureInfo.InvariantCulture));

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar o processo do projetor.");
        process.EnableRaisingEvents = true;
        process.Exited += OnProcessExited;
        return process;
    }

    /// <summary>Fechado pelo operador (Alt+F4), por "close" ou por falha.</summary>
    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (sender is not Process exited || !ReferenceEquals(exited, _process))
            return; // processo antigo; já foi substituído

        var code = exited.ExitCode;
        if (code != 0)
            logger.LogError("Processo do projetor terminou com código {Code}", code);

        _process = null;
        CurrentMonitor = null;
        projection.StopProjection();
    }

    public void Dispose()
    {
        if (_process is { HasExited: false } process)
            process.Kill(entireProcessTree: true);
        _process?.Dispose();
        _gate.Dispose();
    }
}
