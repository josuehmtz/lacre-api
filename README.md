# Lacre · API

Backend de **Lacre**, plataforma de invitaciones digitales con QR para bodas, XV años y más:
confirmación de asistencia, plano de mesas, escáner para hostess y pagos en MXN.

El frontend vive en [lacre-web](https://github.com/josuehmtz/lacre-web).

## Stack

- .NET 10 (LTS) · ASP.NET Core Minimal APIs, organizado por funcionalidad (`Features/`)
- EF Core + PostgreSQL (Neon) · ASP.NET Identity (cookie httpOnly) e inicio de sesión con Google
- Stripe Checkout (tarjeta y OXXO) · Google Cloud Run
- Fotos de las invitaciones en Cloudflare R2 (el navegador sube directo con URL prefirmada)

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
| `R2:AccountId`, `R2:Bucket`, `R2:AccessKeyId`, `R2:SecretAccessKey`, `R2:PublicBaseUrl` | Subida de fotos (ver abajo). Sin ellas, la API responde 503 al subir fotos y lo demás funciona igual |

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
| `R2__AccountId` / `R2__Bucket` / `R2__PublicBaseUrl` | Valores normales (no son secretos) |
| `R2__AccessKeyId` / `R2__SecretAccessKey` | secretos `r2-access-key-id` / `r2-secret-access-key` |
| `Admin__Emails` | correos de administradores de Lacre, separados por coma (panel de salones con convenio) |

Webhook de Stripe: `https://<api>/api/stripe/webhook` con los eventos `checkout.session.completed`,
`checkout.session.async_payment_succeeded`, `checkout.session.async_payment_failed` y `checkout.session.expired`.

## Fotos (Cloudflare R2)

Plan gratis de R2: 10 GB guardados, 1M escrituras y 10M lecturas al mes, **sin costo por tráfico de salida**.
Cada foto se reduce en el navegador (WebP, lado mayor de 1600 px, ≈50–400 KB) antes de subirse.

Flujo: el front pide `POST /api/events/{id}/photos` → la API valida (evento pagado, JPG/PNG/WebP, ≤ 3 MB,
≤ 30 archivos por evento) y firma una URL `PUT` válida 10 min con el **tipo y tamaño exactos** →
el navegador sube directo a R2 (sin pasar por Cloud Run) → el diseño guarda solo la llave
(`events/<evento>/<guid>.webp`). Al guardar el diseño se borran las fotos que ya no usa (con más de 1 h de antigüedad).

Configuración (una sola vez, en el panel de Cloudflare):

1. **R2 → Create bucket**: `lacre-fotos` (ubicación automática).
2. **Settings → Public access → R2.dev subdomain → Allow**. Copia la URL `https://pub-….r2.dev` → `R2__PublicBaseUrl`.
   El subdominio r2.dev tiene límite de velocidad; para producción conecta un dominio propio (requiere el dominio en Cloudflare).
3. **Settings → CORS policy**:
   ```json
   [{ "AllowedOrigins": ["https://lacremx.netlify.app", "http://localhost:5173"],
      "AllowedMethods": ["PUT", "GET"], "AllowedHeaders": ["content-type"], "MaxAgeSeconds": 3600 }]
   ```
4. **R2 → Manage API tokens → Create API token**: permiso *Object Read & Write*, solo el bucket `lacre-fotos`.
   Copia el Access Key ID y el Secret Access Key directo a Secret Manager (`r2-access-key-id`, `r2-secret-access-key`);
   el Account ID aparece en la misma pantalla → `R2__AccountId`.

```powershell
gcloud run services update invitations-api --region us-east1 `
  --update-env-vars "R2__AccountId=<account-id>,R2__Bucket=lacre-fotos,R2__PublicBaseUrl=https://pub-xxxx.r2.dev" `
  --update-secrets "R2__AccessKeyId=r2-access-key-id:latest,R2__SecretAccessKey=r2-secret-access-key:latest"
```

La cuenta de servicio de Cloud Run necesita `roles/secretmanager.secretAccessor` en los dos secretos nuevos
(igual que en los existentes). Ojo: el plan gratis de Secret Manager incluye 6 versiones activas; con estos dos
podrías pasar de ese número (cuesta ≈ US$0.06 al mes por versión extra). Destruye versiones viejas que ya no uses.

## Candados de costo (Google Cloud)

- Cloud Run con `--max-instances 1`.
- Presupuesto `lacre-tope` ($20 MXN/mes) → Pub/Sub `billing-alerts` → función `stop-billing`
  ([infra/stop-billing](infra/stop-billing)), que desvincula la facturación al llegar al tope.
- Limpieza automática de imágenes viejas en Artifact Registry.
