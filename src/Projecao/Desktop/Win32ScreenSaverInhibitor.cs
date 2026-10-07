using System.Runtime.InteropServices;
using Projecao.Services;

namespace Projecao.Desktop;

/// <summary>
/// Impede o protetor de ecrã / suspensão do Windows ENQUANTO se projeta, via
/// <c>SetThreadExecutionState</c> (kernel32). Equivalente Windows do <see cref="ScreenSaverInhibitor"/>
/// (que usa D-Bus, só disponível no Linux).
/// </summary>
public sealed class Win32ScreenSaverInhibitor(ProjectionService projection, ILogger<Win32ScreenSaverInhibitor> logger) : IHostedService
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        projection.OnStateChanged += OnStateChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        projection.OnStateChanged -= OnStateChanged;
        Release();
        return Task.CompletedTask;
    }

    private void OnStateChanged()
    {
        try
        {
            if (projection.State.IsProjecting)
            {
                SetThreadExecutionState(EsContinuous | EsSystemRequired | EsDisplayRequired);
                logger.LogInformation("Protetor de ecrã inibido");
            }
            else
            {
                Release();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha ao ajustar o protetor de ecrã");
        }
    }

    private void Release()
    {
        SetThreadExecutionState(EsContinuous);
        logger.LogInformation("Protetor de ecrã reativado");
    }

    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
}
