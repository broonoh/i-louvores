using System.Text.RegularExpressions;
using Projecao.Models;

namespace Projecao.Services;

/// <summary>
/// Validação de valores vindos da base antes de irem para atributos <c>style</c>.
/// O Blazor já codifica HTML, mas não impede injeção de CSS (ex.: "red; background:url(...)").
/// </summary>
public static partial class CssSafe
{
    public static string? Color(string? value) =>
        value is not null && ColorRegex().IsMatch(value) ? value : null;

    public static string? FontFamily(string? value) =>
        value is not null && FontRegex().IsMatch(value) ? $"'{value}'" : null;

    public static string TextAlign(TextAlignment align) => align switch
    {
        TextAlignment.Left => "left",
        TextAlignment.Right => "right",
        TextAlignment.Justify => "justify",
        _ => "center"
    };

    // #RGB, #RRGGBB, #RRGGBBAA ou nome simples (white, black…).
    [GeneratedRegex(@"^(#[0-9a-fA-F]{3,8}|[a-zA-Z]{3,20})$")]
    private static partial Regex ColorRegex();

    [GeneratedRegex(@"^[\p{L}\p{N} \-]{1,60}$")]
    private static partial Regex FontRegex();
}
