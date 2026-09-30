using Projecao.Services;
using Tmds.DBus.Protocol;

namespace Projecao.Desktop;

/// <summary>
/// Impede o protetor de ecrã / desligar do monitor / suspensão ENQUANTO se projeta
/// (um projetor a apagar a meio do culto é o pior cenário).
///
/// Usa D-Bus da sessão, por ordem:
///   1. org.freedesktop.ScreenSaver.Inhibit  — KDE, XFCE, Cinnamon, MATE, LXQt…
///   2. org.gnome.SessionManager.Inhibit     — GNOME
/// A inibição dura enquanto a ligação D-Bus estiver aberta; se a app morrer,
/// o sistema liberta-a sozinho.
/// </summary>
public sealed class ScreenSaverInhibitor(ProjectionService projection, ILogger<ScreenSaverInhibitor> logger)
    : IHostedService, IAsyncDisposable
{
    private const string AppName = "I-LOUVORES";
    private const string Reason = "Projeção em curso";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private DBusConnection? _connection;
    private Func<Task>? _release;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        projection.OnStateChanged += OnStateChanged;
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        projection.OnStateChanged -= OnStateChanged;
        await SetInhibitedAsync(false);
    }

    private void OnStateChanged() => _ = SetInhibitedAsync(projection.State.IsProjecting);

    private async Task SetInhibitedAsync(bool inhibit)
    {
        await _gate.WaitAsync();
        try
        {
            if (inhibit == (_release is not null))
                return;

            if (inhibit)
            {
                _release = await TryFreedesktopAsync() ?? await TryGnomeAsync();
                if (_release is null)
                    logger.LogWarning("Não foi possível inibir o protetor de ecrã: desative-o manualmente durante o culto.");
            }
            else
            {
                var release = _release!;
                _release = null;
                await release();
                logger.LogInformation("Protetor de ecrã reativado");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha ao {Action} o protetor de ecrã", inhibit ? "inibir" : "libertar");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Func<Task>?> TryFreedesktopAsync()
    {
        try
        {
            var connection = await GetConnectionAsync();
            const string service = "org.freedesktop.ScreenSaver", path = "/org/freedesktop/ScreenSaver";

            var cookie = await connection.CallMethodAsync(
                Call(connection, service, path, service, "Inhibit", "ss", (ref MessageWriter w) => { w.WriteString(AppName); w.WriteString(Reason); }),
                (Message m, object? _) => m.GetBodyReader().ReadUInt32(), null);

            logger.LogInformation("Protetor de ecrã inibido (freedesktop, cookie {Cookie})", cookie);
            return () => connection.CallMethodAsync(Call(connection, service, path, service, "UnInhibit", "u", (ref MessageWriter w) => w.WriteUInt32(cookie)));
        }
        catch (Exception ex) when (ex is DBusErrorReplyException or DBusConnectionException)
        {
            ResetIfClosed(ex);
            logger.LogInformation(ex, "org.freedesktop.ScreenSaver indisponível");
            return null;
        }
    }

    private async Task<Func<Task>?> TryGnomeAsync()
    {
        try
        {
            var connection = await GetConnectionAsync();
            const string service = "org.gnome.SessionManager", path = "/org/gnome/SessionManager";
            const uint inhibitIdle = 8, inhibitSuspend = 4;

            var cookie = await connection.CallMethodAsync(
                Call(connection, service, path, service, "Inhibit", "susu", (ref MessageWriter w) =>
                {
                    w.WriteString(AppName);
                    w.WriteUInt32(0); // toplevel_xid (não aplicável em Wayland)
                    w.WriteString(Reason);
                    w.WriteUInt32(inhibitIdle | inhibitSuspend);
                }),
                (Message m, object? _) => m.GetBodyReader().ReadUInt32(), null);

            logger.LogInformation("Protetor de ecrã inibido (GNOME, cookie {Cookie})", cookie);
            return () => connection.CallMethodAsync(Call(connection, service, path, service, "Uninhibit", "u", (ref MessageWriter w) => w.WriteUInt32(cookie)));
        }
        catch (Exception ex) when (ex is DBusErrorReplyException or DBusConnectionException)
        {
            ResetIfClosed(ex);
            logger.LogInformation(ex, "org.gnome.SessionManager indisponível");
            return null;
        }
    }

    /// <summary>Ligação perdida (ex.: reinício do D-Bus): descarta-a para a próxima tentativa reconectar.</summary>
    private void ResetIfClosed(Exception ex)
    {
        if (ex is DBusConnectionException)
        {
            _connection?.Dispose();
            _connection = null;
        }
    }

    private async Task<DBusConnection> GetConnectionAsync()
    {
        if (_connection is not null)
            return _connection;

        var address = DBusAddress.Session ?? throw new DBusConnectFailedException("Sem sessão D-Bus (DBUS_SESSION_BUS_ADDRESS).");
        var connection = new DBusConnection(address);
        await connection.ConnectAsync();
        return _connection = connection;
    }

    /// <summary>
    /// O MessageWriter é uma struct: tem de ser passado por <c>ref</c>. Com Action&lt;MessageWriter&gt;
    /// o corpo seria escrito numa cópia, a mensagem sairia malformada e o barramento
    /// D-Bus desligaria a ligação ("Connection closed by peer").
    /// </summary>
    private delegate void BodyWriter(ref MessageWriter writer);

    private static MessageBuffer Call(DBusConnection connection, string service, string path, string iface, string member,
        string signature, BodyWriter body)
    {
        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(service, path, iface, member, signature, MessageFlags.None);
            body(ref writer);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await SetInhibitedAsync(false);
        _connection?.Dispose();
        _gate.Dispose();
    }
}
