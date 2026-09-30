using System.Text.Json;

namespace Projecao.Services;

/// <summary>
/// Preferências do utilizador num JSON (dados/config.json). Escrita atómica
/// (ficheiro temporário + rename) para não corromper com falhas de energia.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly object _gate = new();
    private readonly string _path;
    private Settings _settings;

    public SettingsStore(AppPaths paths)
    {
        _path = Path.Combine(paths.DataFolder, "config.json");
        _settings = Load(_path);
    }

    public MonitorPreference? PreferredMonitor
    {
        get { lock (_gate) return _settings.PreferredMonitor; }
        set
        {
            lock (_gate)
            {
                _settings = _settings with { PreferredMonitor = value };
                Save();
            }
        }
    }

    /// <summary>Margem de segurança (% de cada lado) para TVs com overscan.</summary>
    public double SafeAreaPercent
    {
        get { lock (_gate) return _settings.SafeAreaPercent; }
        set
        {
            lock (_gate)
            {
                _settings = _settings with { SafeAreaPercent = value };
                Save();
            }
        }
    }

    /// <summary>Fundo dos slides escolhido na Galeria (caminho relativo).</summary>
    public string? GalleryBackground
    {
        get { lock (_gate) return _settings.GalleryBackground; }
        set { lock (_gate) { _settings = _settings with { GalleryBackground = value }; Save(); } }
    }

    /// <summary>Fundo do 1.º slide escolhido na Galeria (caminho relativo).</summary>
    public string? GalleryFirstSlideBackground
    {
        get { lock (_gate) return _settings.GalleryFirstSlideBackground; }
        set { lock (_gate) { _settings = _settings with { GalleryFirstSlideBackground = value }; Save(); } }
    }

    /// <summary>Fundo das passagens bíblicas escolhido na Galeria (caminho relativo).</summary>
    public string? GalleryBibleBackground
    {
        get { lock (_gate) return _settings.GalleryBibleBackground; }
        set { lock (_gate) { _settings = _settings with { GalleryBibleBackground = value }; Save(); } }
    }

    /// <summary>Fundo do relógio/cronómetro (módulo Geral).</summary>
    public string? GalleryGeneralBackground
    {
        get { lock (_gate) return _settings.GalleryGeneralBackground; }
        set { lock (_gate) { _settings = _settings with { GalleryGeneralBackground = value }; Save(); } }
    }

    /// <summary>A Bíblia Livre embutida já foi instalada (não reinstalar se o utilizador a apagar).</summary>
    public bool BundledBibleInstalled
    {
        get { lock (_gate) return _settings.BundledBibleInstalled; }
        set { lock (_gate) { _settings = _settings with { BundledBibleInstalled = value }; Save(); } }
    }

    /// <summary>Aparência da projeção (aba Configuração).</summary>
    public Projecao.Models.Projection.ProjectionStyle ProjectionStyle
    {
        get { lock (_gate) return _settings.ProjectionStyle ?? Projecao.Models.Projection.ProjectionStyle.Default; }
        set { lock (_gate) { _settings = _settings with { ProjectionStyle = value }; Save(); } }
    }

    /// <summary>Tema do painel: "auto" (segue o sistema), "light" ou "dark".</summary>
    public string Theme
    {
        get { lock (_gate) return _settings.Theme is "light" or "dark" ? _settings.Theme : "auto"; }
        set { lock (_gate) { _settings = _settings with { Theme = value }; Save(); } }
    }

    /// <summary>Última versão da Bíblia escolhida no módulo Bíblia.</summary>
    public string? LastBibleVersion
    {
        get { lock (_gate) return _settings.LastBibleVersion; }
        set { lock (_gate) { _settings = _settings with { LastBibleVersion = value }; Save(); } }
    }

    private void Save()
    {
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_settings, Json));
        File.Move(temp, _path, overwrite: true);
    }

    private static Settings Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Json) ?? new Settings()
                : new Settings();
        }
        catch (JsonException)
        {
            return new Settings(); // ficheiro corrompido: recomeça com valores por omissão
        }
    }

    private sealed record Settings
    {
        public MonitorPreference? PreferredMonitor { get; init; }

        public double SafeAreaPercent { get; init; }

        public string? GalleryBackground { get; init; }

        public string? GalleryFirstSlideBackground { get; init; }

        public bool BundledBibleInstalled { get; init; }

        public string? GalleryBibleBackground { get; init; }

        public string? GalleryGeneralBackground { get; init; }

        public string? LastBibleVersion { get; init; }

        public string? Theme { get; init; }

        public Projecao.Models.Projection.ProjectionStyle? ProjectionStyle { get; init; }
    }
}
