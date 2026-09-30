using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Photino.NET;
using Projecao.Components;
using Projecao.Data;
using Projecao.Services;

namespace Projecao.Desktop;

/// <summary>
/// Processo principal: servidor Blazor local + janela nativa do painel de controlo.
/// </summary>
internal static class DesktopHost
{
    public const string PanelTitle = "Projeção - Painel de Controle";

    public static int Run(string[] args)
    {
        var app = BuildServer(args);

        // Síncrono de propósito: a janela GTK a seguir tem de ficar na thread principal.
        app.StartAsync().GetAwaiter().GetResult();

        var baseUrl = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');
        app.Services.GetRequiredService<ServerInfo>().BaseUrl = baseUrl;

        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("I-LOUVORES");
        logger.LogInformation("Servidor local em {Url}", baseUrl);

        var access = app.Services.GetRequiredService<LocalAccess>();
#if DEBUG
        // Só em Debug: permite abrir o painel também num browser para diagnóstico.
        logger.LogInformation("URL do painel (debug): {Url}", access.WithToken($"{baseUrl}/"));
#endif
        var panel = new PhotinoWindow()
            .SetLogVerbosity(0)
            .SetTitle(PanelTitle)
            .SetUseOsDefaultSize(false)
            .SetSize(1400, 860)
            .SetMinSize(1100, 650)
            .Center()
#if DEBUG
            .SetDevToolsEnabled(true)
#else
            .SetDevToolsEnabled(false)
            .SetContextMenuEnabled(false)
#endif
            .Load(access.WithToken($"{baseUrl}/"));

        AppIcon.Apply(panel);
        app.Services.GetRequiredService<GtkDisplayService>().Attach(panel, PanelTitle);

        // SIGTERM/SIGINT (terminar sessão, desligar, Ctrl+C): o ASP.NET pára o servidor,
        // mas a janela GTK mantém a thread principal presa — fechamo-la explicitamente.
        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        using var stopping = lifetime.ApplicationStopping.Register(() => panel.Invoke(panel.Close));

        panel.WaitForClose();

        // Painel fechado: fecha o projetor e desliga o servidor.
        var projector = app.Services.GetRequiredService<ProjectorProcessManager>();
        projector.CloseAsync().GetAwaiter().GetResult();
        app.StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        return 0;
    }

    private static WebApplication BuildServer(string[] args)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory
        });

        // Serve wwwroot, CSS isolado e blazor.web.js também fora do "dotnet run".
        builder.WebHost.UseStaticWebAssets();

        // Só loopback, porta aleatória: nada fica exposto na rede da igreja.
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));

        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);

        var paths = new AppPaths();

        // Cópia de segurança importada na sessão anterior: aplica antes de abrir a base.
        Projecao.Services.Backup.BackupService.ApplyPendingRestore(paths);
        var services = builder.Services;
        services.AddSingleton(paths);
        services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite($"Data Source={paths.DatabasePath}"));

        services.AddSingleton<ProjectionService>();
        services.AddSingleton<MediaLibraryService>();
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<GalleryService>();
        services.AddSingleton<Projecao.Services.Import.SongImporter>();
        services.AddSingleton<Projecao.Services.Bible.BibleService>();
        services.AddSingleton<Projecao.Services.LegacyImport.LegacyMigrationService>();
        services.AddSingleton<Projecao.Services.Notices.NoticeService>();
        services.AddSingleton<Projecao.Services.Songs.SongService>();
        services.AddSingleton<Projecao.Services.Backup.BackupService>();
        services.AddSingleton<FontService>();
        services.AddSingleton<LocalAccess>();
        services.AddSingleton<ServerInfo>();
        services.AddSingleton<GtkDisplayService>();
        services.AddSingleton<IDisplayService>(sp => sp.GetRequiredService<GtkDisplayService>());
        services.AddSingleton<ProjectorProcessManager>();
        services.AddSingleton<IProjectionWindowManager>(sp => sp.GetRequiredService<ProjectorProcessManager>());
        services.AddHostedService<ScreenSaverInhibitor>();

        services.AddRazorComponents().AddInteractiveServerComponents();

        var app = builder.Build();

        using (var db = app.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext())
            DbInitializer.Initialize(db);

        // Bíblia Livre embutida (só na 1.ª execução; ~1 s).
        try
        {
            app.Services.GetRequiredService<Projecao.Services.Bible.BibleService>().EnsureBundledBibleAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("I-LOUVORES")
                .LogError(ex, "Falha a instalar a Bíblia Livre embutida");
        }

        // Leitura bíblica: o Próximo/Anterior passa sozinho para o capítulo vizinho.
        var bibleService = app.Services.GetRequiredService<Projecao.Services.Bible.BibleService>();
        app.Services.GetRequiredService<ProjectionService>().ReadingContinuation = bibleService.GetAdjacentChapter;

        // Fundos da Galeria escolhidos na última sessão.
        app.Services.GetRequiredService<GalleryService>().Initialize();

        // Aparência da projeção (aba Configuração) guardada da última sessão.
        app.Services.GetRequiredService<ProjectionService>().SetStyle(app.Services.GetRequiredService<SettingsStore>().ProjectionStyle);

        // Margem de segurança da TV (overscan) guardada da última sessão.
        app.Services.GetRequiredService<ProjectionService>()
            .SetSafeArea(app.Services.GetRequiredService<SettingsStore>().SafeAreaPercent);

        var access = app.Services.GetRequiredService<LocalAccess>();
        app.Use(access.InvokeAsync);

        // Documentos/I-LOUVORES/Galeria → /galeria/… (fundos de imagem e vídeo).
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(paths.GalleryFolder),
            RequestPath = MediaLibraryService.UrlPrefix
        });

        app.UseAntiforgery();
        app.MapStaticAssets();
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
        return app;
    }
}
