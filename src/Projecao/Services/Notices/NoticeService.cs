using Ganss.Xss;
using Microsoft.EntityFrameworkCore;
using Projecao.Data;
using Projecao.Models;

namespace Projecao.Services.Notices;

/// <summary>
/// Avisos: HTML formatado (editor da aba Avisos), guardados na base como avisos ou modelos.
/// Todo o HTML é sanitizado (lista branca de tags e estilos) antes de ser gravado ou projetado:
/// texto colado de páginas web/e-mails não pode injetar scripts no projetor.
/// </summary>
public sealed class NoticeService(IDbContextFactory<AppDbContext> dbFactory)
{
    private static readonly HtmlSanitizer Sanitizer = CreateSanitizer();

    private static HtmlSanitizer CreateSanitizer()
    {
        var s = new HtmlSanitizer();
        s.AllowedTags.Clear();
        foreach (var tag in new[] { "div", "p", "br", "span", "b", "strong", "i", "em", "u", "s", "strike", "font", "sup", "sub" })
            s.AllowedTags.Add(tag);

        s.AllowedAttributes.Clear();
        s.AllowedAttributes.Add("style");
        s.AllowedAttributes.Add("color"); // <font color> (execCommand sem CSS)
        s.AllowedAttributes.Add("face");

        s.AllowedCssProperties.Clear();
        foreach (var css in new[] { "color", "background-color", "font-size", "font-family", "font-weight", "font-style", "text-decoration", "text-align" })
            s.AllowedCssProperties.Add(css);

        s.AllowedSchemes.Clear(); // sem URLs (nem url() em CSS)
        s.AllowedAtRules.Clear();
        return s;
    }

    public static string Sanitize(string? html) => string.IsNullOrWhiteSpace(html) ? string.Empty : Sanitizer.Sanitize(html);

    public async Task<IReadOnlyList<Notice>> ListAsync(bool templates, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Notices.AsNoTracking().Where(n => n.IsTemplate == templates)
            .OrderByDescending(n => n.UpdatedAt).ToListAsync(ct);
    }

    public async Task<Notice?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Notices.AsNoTracking().FirstOrDefaultAsync(n => n.Id == id, ct);
    }

    /// <summary>Grava (cria ou atualiza pelo título + tipo). Devolve o aviso gravado.</summary>
    public async Task<Notice> SaveAsync(string title, string html, string? backColor, bool isTemplate, int? id = null, CancellationToken ct = default)
    {
        title = title.Trim();
        if (title.Length is 0 or > 200) throw new ArgumentException("O nome deve ter entre 1 e 200 caracteres.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var notice = id is { } existingId
            ? await db.Notices.FirstOrDefaultAsync(n => n.Id == existingId, ct)
            : await db.Notices.FirstOrDefaultAsync(n => n.Title == title && n.IsTemplate == isTemplate, ct);

        if (notice is null)
        {
            notice = new Notice { Title = title, IsTemplate = isTemplate };
            db.Notices.Add(notice);
        }

        notice.Title = title;
        notice.Content = Sanitize(html);
        notice.BackColor = CssSafe.Color(backColor);
        notice.IsTemplate = isTemplate;
        await db.SaveChangesAsync(ct);
        return notice;
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Notices.Where(n => n.Id == id).ExecuteDeleteAsync(ct);
    }
}
