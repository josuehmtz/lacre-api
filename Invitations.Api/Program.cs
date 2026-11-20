using System.Text.Json.Serialization;
using Invitations.Api.Data;
using Invitations.Api.Domain;
using Invitations.Api.Features.Auth;
using Invitations.Api.Features.Billing;
using Invitations.Api.Features.Common;
using Invitations.Api.Features.Events;
using Invitations.Api.Features.Invitations;
using Invitations.Api.Features.Photos;
using Invitations.Api.Features.Plans;
using Invitations.Api.Features.Public;
using Invitations.Api.Features.Seating;
using Invitations.Api.Features.Staff;
using Invitations.Api.Features.Templates;
using Invitations.Api.Features.Venues;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Cloud Run / Render indican el puerto con la variable PORT.
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrEmpty(port))
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
// Reloj inyectable: las reglas de fechas (confirmación, inicio del evento) se pueden probar.
builder.Services.AddSingleton(TimeProvider.System);
// Valida automáticamente los atributos [Required], [Range], etc. de los requests (.NET 10).
builder.Services.AddValidation();
// Enums como texto en JSON ("Wedding" en vez de 0): más legible para el front.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Límite de peticiones por IP para los endpoints públicos (links de invitación).
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(PublicInvitationEndpoints.RateLimitPolicy, ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1) }));
});

// Pagos (Stripe). Las llaves vienen de user-secrets o variables de entorno (Stripe__SecretKey, Stripe__WebhookSecret).
builder.Services.Configure<StripeOptions>(builder.Configuration.GetSection("Stripe"));
builder.Services.AddSingleton<IPaymentGateway, StripePaymentGateway>();

// Fotos en Cloudflare R2 (R2__AccountId, R2__Bucket, R2__AccessKeyId, R2__SecretAccessKey, R2__PublicBaseUrl).
// Sin configurar, la API sigue funcionando y la subida de fotos responde 503.
builder.Services.Configure<R2Options>(builder.Configuration.GetSection("R2"));
builder.Services.AddHttpClient<IPhotoStorage, R2PhotoStorage>(c => c.Timeout = TimeSpan.FromSeconds(15));

builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseNpgsql(ConnectionStrings.Normalize(builder.Configuration.GetConnectionString("Default"))));

// Identity con cookie httpOnly. El front llama a /api/* a través del proxy de Netlify
// (o de Vite en desarrollo), así que la cookie es del mismo sitio.
builder.Services.AddAuthorizationBuilder()
    // Las hostess usan su propia sesión, limitada a un evento (ver Features/Staff).
    .AddPolicy(StaffAuth.Policy, p => p
        .AddAuthenticationSchemes(StaffAuth.Scheme)
        .RequireClaim(StaffAuth.AccessClaim))
    .AddPolicy(AdminAuth.Policy, p => AdminAuth.Configure(p, builder.Configuration));
builder.Services
    .AddIdentityApiEndpoints<AppUser>(o =>
    {
        o.User.RequireUniqueEmail = true;
        o.Password.RequiredLength = 8;
        o.Password.RequireNonAlphanumeric = false;
    })
    .AddRoles<IdentityRole<Guid>>()
    .AddErrorDescriber<SpanishIdentityErrors>()
    .AddEntityFrameworkStores<AppDbContext>();

builder.Services.ConfigureApplicationCookie(o =>
{
    o.Cookie.Name = "inv_session";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    o.ExpireTimeSpan = TimeSpan.FromDays(14);
    o.SlidingExpiration = true;
});

var authentication = builder.Services.AddAuthentication();

// Inicio de sesión con Google (solo si hay credenciales: user-secrets en desarrollo, Secret Manager en producción).
var google = builder.Configuration.GetSection("Authentication:Google");
if (!string.IsNullOrWhiteSpace(google["ClientId"]) && !string.IsNullOrWhiteSpace(google["ClientSecret"]))
{
    authentication.AddGoogle(GoogleAuth.Scheme, o =>
    {
        o.ClientId = google["ClientId"]!.Trim();
        o.ClientSecret = google["ClientSecret"]!.Trim();
        // Bajo /api para que pase por el proxy de Netlify (y de Vite) y la cookie quede en el dominio del sitio.
        o.CallbackPath = "/api/auth/signin-google";
        o.SignInScheme = IdentityConstants.ExternalScheme;
    });
}

authentication
    .AddCookie(StaffAuth.Scheme, o =>
    {
        o.Cookie.Name = "inv_staff";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        // Es una API: responder 401/403 en vez de redirigir a una página de login.
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
    });

// Llaves de cifrado de cookies en la base (ver AppDbContext.DataProtectionKeys).
builder.Services.AddDataProtection()
    .PersistKeysToDbContext<AppDbContext>()
    .SetApplicationName("invitations");

// Detrás de proxies (Netlify, Cloud Run) para conocer el esquema/IP reales.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

app.UseForwardedHeaders();

// La API se usa detrás del proxy de Netlify (o de Vite): las URLs que genera (p. ej. el regreso de Google)
// deben usar el dominio público del sitio, no el de Cloud Run.
if (Uri.TryCreate(app.Configuration["App:PublicUrl"], UriKind.Absolute, out var publicUrl))
{
    var publicHost = PublicUrls.HostFor(publicUrl);
    app.Use((ctx, next) =>
    {
        ctx.Request.Scheme = publicUrl.Scheme;
        ctx.Request.Host = publicHost;
        return next(ctx);
    });
}

// Errores no controlados y 404/401 vacíos se devuelven como ProblemDetails (JSON estándar).
app.UseExceptionHandler(new ExceptionHandlerOptions
{
    // JSON mal formado o tipos inválidos son error del cliente (400), no del servidor (500).
    StatusCodeSelector = ex => ex is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status500InternalServerError,
});
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

// Migraciones al iniciar: siempre en desarrollo, y en producción si Database__MigrateOnStartup=true.
// EF Core toma un bloqueo en la base, así que es seguro aunque arranquen varias instancias a la vez.
if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();
}

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

var api = app.MapGroup("/api");
api.MapGet("/health", () => Results.Ok(new { status = "ok" }));
api.MapAuthEndpoints();
api.MapPlanEndpoints();
api.MapEventEndpoints();
api.MapInvitationEndpoints();
api.MapTemplateEndpoints();
api.MapPhotoEndpoints();
api.MapPublicInvitationEndpoints();
api.MapSeatingEndpoints();
api.MapBillingEndpoints();
api.MapStaffAccessEndpoints();
api.MapStaffEndpoints();
api.MapVenueEndpoints();

app.Run();
