# Lacre · API

Backend de **Lacre**, plataforma de invitaciones digitales con QR para bodas, XV años y más:
confirmación de asistencia, plano de mesas, escáner para hostess y pagos en MXN.

El frontend vive en [lacre-web](https://github.com/josuehmtz/lacre-web).

## Stack

- .NET 10 (LTS) · ASP.NET Core Minimal APIs, organizado por funcionalidad (`Features/`)
- EF Core + PostgreSQL (Neon) · ASP.NET Identity (cookie httpOnly) e inicio de sesión con Google
- Stripe Checkout (tarjeta y OXXO) · Google Cloud Run

```
Invitations.Api/
├── Domain/      Entidades y reglas de negocio (pases, confirmación, mesas, pagos, hostess)
├── Data/        AppDbContext + migraciones
└── Features/    Endpoints por funcionalidad
Invitations.Tests/  Pruebas unitarias de las reglas (xUnit)
```

## Desarrollo local

```powershell
# Base de datos: Neon (rama dev) o Postgres local con Docker
dotnet user-secrets set "ConnectionStrings:Default" '<cadena de Neon o local>' --project Invitations.Api
docker compose up -d   # opcional: Postgres local que coincide con appsettings.Development.json

dotnet run --project Invitations.Api   # http://localhost:5080 (aplica migraciones al iniciar)
dotnet test
```

Secretos opcionales en desarrollo (user-secrets):

| Clave | Para qué |
|---|---|
| `Stripe:SecretKey` | Pagos de prueba (`sk_test_...`) |
| `Stripe:WebhookSecret` | Webhook con la [Stripe CLI](https://docs.stripe.com/stripe-cli) |
| `Authentication:Google:ClientId` / `ClientSecret` | Inicio de sesión con Google |

Nueva migración: `dotnet tool restore; dotnet ef migrations add Nombre --project Invitations.Api --output-dir Data/Migrations`

## Despliegue (Cloud Run)

```powershell
gcloud run deploy invitations-api --source . --region us-east1 --quiet
```

Variables y secretos del servicio (Secret Manager):

| Variable | Origen |
|---|---|
| `ConnectionStrings__Default` | secreto `db-connection` (Neon, rama production) |
| `Stripe__SecretKey` / `Stripe__WebhookSecret` | secretos `stripe-secret-key` / `stripe-webhook-secret` |
| `Authentication__Google__ClientId` / `ClientSecret` | secretos `google-client-id` / `google-client-secret` |
| `App__PublicUrl` | URL del frontend (regresos de Stripe y Google) |
| `Database__MigrateOnStartup` | `true` |
| `Admin__Emails` | correos de administradores de Lacre, separados por coma (panel de salones con convenio) |

Webhook de Stripe: `https://<api>/api/stripe/webhook` con los eventos `checkout.session.completed`,
`checkout.session.async_payment_succeeded`, `checkout.session.async_payment_failed` y `checkout.session.expired`.

## Candados de costo (Google Cloud)

- Cloud Run con `--max-instances 1`.
- Presupuesto `lacre-tope` ($20 MXN/mes) → Pub/Sub `billing-alerts` → función `stop-billing`
  ([infra/stop-billing](infra/stop-billing)), que desvincula la facturación al llegar al tope.
- Limpieza automática de imágenes viejas en Artifact Registry.
