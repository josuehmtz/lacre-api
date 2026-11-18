using System.Security.Claims;
using Invitations.Api.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;

namespace Invitations.Api.Features.Auth;

public static class GoogleAuth
{
    public const string Scheme = "Google";
}

public static class AuthEndpoints
{
    public record ProvidersDto(bool Google);

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder api)
    {
        var auth = api.MapGroup("/auth").WithTags("Auth");

        // register, login, manage/info, etc. vienen de Identity.
        auth.MapIdentityApi<AppUser>();

        // Identity no incluye logout para cookies.
        auth.MapPost("/logout", async (SignInManager<AppUser> signIn) =>
        {
            await signIn.SignOutAsync();
            return Results.NoContent();
        }).RequireAuthorization();

        // El front muestra el botón de Google solo si está configurado.
        auth.MapGet("/providers", async (IAuthenticationSchemeProvider schemes) =>
            Results.Ok(new ProvidersDto(await schemes.GetSchemeAsync(GoogleAuth.Scheme) is not null)));

        // 1) El navegador entra aquí (navegación completa, no fetch) y se le redirige a Google.
        auth.MapGet("/google", async (string? returnUrl, SignInManager<AppUser> signIn, IAuthenticationSchemeProvider schemes) =>
        {
            if (await schemes.GetSchemeAsync(GoogleAuth.Scheme) is null) return Results.NotFound();

            var complete = $"/api/auth/google/complete?returnUrl={Uri.EscapeDataString(SafeReturnUrl(returnUrl))}";
            var props = signIn.ConfigureExternalAuthenticationProperties(GoogleAuth.Scheme, complete);
            return Results.Challenge(props, [GoogleAuth.Scheme]);
        });

        // 2) Google regresa a /api/auth/signin-google (lo procesa el handler) y de ahí aquí.
        auth.MapGet("/google/complete", async (string? returnUrl, HttpContext http,
            SignInManager<AppUser> signIn, UserManager<AppUser> users, ILoggerFactory logs) =>
        {
            var target = SafeReturnUrl(returnUrl);
            var info = await signIn.GetExternalLoginInfoAsync();
            if (info is null) return Results.LocalRedirect("/login?error=google");

            // Ya había entrado antes con Google.
            var existing = await signIn.ExternalLoginSignInAsync(info.LoginProvider, info.ProviderKey,
                isPersistent: true, bypassTwoFactor: true);
            if (existing.Succeeded) return Results.LocalRedirect(target);
            if (existing.IsLockedOut) return Results.LocalRedirect("/login?error=bloqueado");

            var email = info.Principal.FindFirstValue(ClaimTypes.Email);
            if (string.IsNullOrWhiteSpace(email)) return Results.LocalRedirect("/login?error=google");

            // Si ya tenía cuenta con correo y contraseña, se le vincula Google (Google verificó el correo).
            var user = await users.FindByEmailAsync(email);
            if (user is null)
            {
                user = new AppUser
                {
                    UserName = email,
                    Email = email,
                    EmailConfirmed = true,
                    FullName = info.Principal.FindFirstValue(ClaimTypes.Name) ?? "",
                };
                var created = await users.CreateAsync(user);
                if (!created.Succeeded)
                {
                    logs.CreateLogger("Auth").LogWarning("No se pudo crear la cuenta de Google: {Errors}",
                        string.Join("; ", created.Errors.Select(e => e.Code)));
                    return Results.LocalRedirect("/login?error=google");
                }
            }

            var linked = await users.AddLoginAsync(user, info);
            if (!linked.Succeeded) return Results.LocalRedirect("/login?error=google");

            await signIn.SignInAsync(user, isPersistent: true, info.LoginProvider);
            await http.SignOutAsync(IdentityConstants.ExternalScheme);
            return Results.LocalRedirect(target);
        });

        return api;
    }

    /// <summary>Solo rutas internas del sitio (evita redirigir a sitios ajenos después del login).</summary>
    public static string SafeReturnUrl(string? url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//") && !url.StartsWith("/\\")
            ? url
            : "/app";
}
