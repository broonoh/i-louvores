using System.Security.Cryptography;
using System.Text;

namespace Projecao.Desktop;

/// <summary>
/// O servidor escuta só em 127.0.0.1, mas qualquer programa/página do mesmo PC
/// poderia ligar-se a ele. Exigimos um token aleatório (gerado a cada arranque):
/// entregue às nossas janelas no URL (?t=…) e depois guardado num cookie HttpOnly.
/// </summary>
public sealed class LocalAccess
{
    public const string QueryKey = "t";
    private const string CookieName = "projecao_auth";

    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    public string WithToken(string url) => $"{url}{(url.Contains('?') ? '&' : '?')}{QueryKey}={Token}";

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (context.Request.Query.TryGetValue(QueryKey, out var fromQuery) && Matches(fromQuery))
        {
            context.Response.Cookies.Append(CookieName, Token, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Path = "/"
            });
            await next(context);
            return;
        }

        if (context.Request.Cookies.TryGetValue(CookieName, out var fromCookie) && Matches(fromCookie))
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
    }

    private bool Matches(string? candidate) =>
        candidate is not null &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(candidate), Encoding.ASCII.GetBytes(Token));
}
