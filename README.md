# Nura Herbex — Blazor (Web + Mobile) with ASP.NET Core API

A .NET 10 rebuild of the Stamix storefront, pixel-matched to the React design in `../src`.

```
blazor/
├─ src/
│  ├─ Nuraherbex.Shared   DTOs + default site content (shared by API and clients)
│  ├─ Nuraherbex.Api      ASP.NET Core Web API: orders, PayU, Shiprocket, auth, admin, WhatsApp webhook (EF Core → SQL Server)
│  ├─ Nuraherbex.UI       Razor Class Library — every page/component, services, Tailwind CSS (shared by web + mobile)
│  ├─ Nuraherbex.Web      Blazor WebAssembly PWA host  → http://localhost:5200
│  └─ Nuraherbex.Maui     .NET MAUI Blazor Hybrid host (Android / iOS / macOS / Windows)
├─ tools/gen-icons.cjs    regenerates the lucide icon set used by <Icon/>
├─ tailwind.config.js     same design tokens as the React app
└─ build-ui.ps1           serialised compile check for the UI library
```

## 1. Run it locally (no database needed)

Development uses an **in-memory database** seeded with the 4 Stamix products, coupons (`STAMIX10`, `SAVE10`, `STAMIX20`, `WELCOME20`) and the demo batch `STX-2609-A17`.

```powershell
# terminal 1 — API on http://localhost:5118
dotnet run --project src/Nuraherbex.Api

# terminal 2 — web app on http://localhost:5200
dotnet run --project src/Nuraherbex.Web
```

* Admin portal: `/admin` — dev credentials are in `src/Nuraherbex.Api/appsettings.Development.json` (**dev only — override in production**).
* Payments run in **simulation mode** until `PayU:MerchantKey/MerchantSalt` are set; Shiprocket simulates until credentials are set; emails are skipped until `Email:ResendApiKey` is set.

### CSS (Tailwind)
The UI uses the same Tailwind classes as the React app. After changing markup, rebuild the stylesheet (uses the Tailwind CLI already in `../node_modules`):

```powershell
npm run css:build --prefix blazor     # or css:watch while developing
```

### Mobile app
**Requires Visual Studio 2026 (v18+) for the IDE** — Visual Studio 2022 (17.x) cannot target .NET 10 and shows errors such as
"The current Visual Studio version does not support targeting .NET 10.0", "target platform identifier android was not recognized"
and the trimming/NETSDK1195 messages. On this machine VS 2026 is installed alongside 17.14: open `Nuraherbex.sln` with it
(right-click the .sln → *Open with* → Visual Studio 2026).

Command line:
```powershell
dotnet build src/Nuraherbex.Maui -f net10.0-windows10.0.19041.0     # Windows
powershell -File build-android.ps1                                  # Android (explicit API level net10.0-android36.0)
```
The Android target is pinned to `net10.0-android36.0`; without a platform version NuGet fails with NU1012.
Debug builds call `http://10.0.2.2:5118` (Android emulator → host) or `http://localhost:5118`. Set the production URL in `MauiProgram.ProductionApi`.

## 2. SQL Server

`ConnectionStrings:Default` is stored in **user-secrets** (not in the repo). To switch the API from the in-memory demo to SQL Server:

```powershell
dotnet user-secrets set "Database:Provider" "SqlServer" --project src/Nuraherbex.Api
```

On startup the API calls `EnsureCreated()` and seeds products/coupons/batch if the tables are empty. If your login cannot create tables/databases, run `src/Nuraherbex.Api/Sql/01_schema.sql` once as a user with DDL rights (regenerate any time with `dotnet run --project src/Nuraherbex.Api -- --generate-sql <file>`).

> The `Nuraherbex` database must exist and the login (`shatech`) needs `db_owner` (or at least `db_ddladmin` + read/write) on it.

## 3. WhatsApp (Meta Cloud API)

Configuration (user-secrets / environment variables `WhatsApp__*`):

| Key | Purpose |
|---|---|
| `WhatsApp:VerifyToken` | Any secret string you choose; you type the same value into Meta's "Verify token" box. |
| `WhatsApp:AppSecret` | Meta App Secret — used to validate `X-Hub-Signature-256` on every event. **Required outside Development.** |
| `WhatsApp:AccessToken` | Permanent system-user token (outbound messages). |
| `WhatsApp:PhoneNumberId` | The WhatsApp Business phone-number id. |
| `WhatsApp:OrderConfirmationTemplate` / `OrderShippedTemplate` / `OrderDeliveredTemplate` | Optional approved template names (see below). |

**Webhook in Meta:** App Dashboard → WhatsApp → Configuration → Webhook
* Callback URL: `https://<your-api-host>/api/webhooks/whatsapp`
* Verify token: the value of `WhatsApp:VerifyToken`
* Subscribe to the **messages** field.

Behaviour
* `GET /api/webhooks/whatsapp` — verification handshake: returns the `hub.challenge` as plain text (200) when `hub.mode=subscribe` and the token matches (constant-time compare), otherwise **403**.
* `POST /api/webhooks/whatsapp` — validates `X-Hub-Signature-256` (HMAC-SHA256 of the raw body with the App Secret; invalid/missing → **401**), stores inbound messages and delivery statuses (idempotent on Meta retries) and always answers 200 for a valid payload.
* Auto-reply: a customer can send `track` or an order id like `NH-123456` and get the live order status.
* Order notifications: confirmation, shipped and delivered messages are sent automatically. Outside the 24-hour customer-service window WhatsApp only delivers **approved templates** — create templates whose body variables are `{{1}}=first name, {{2}}=order id, {{3}}=amount` (confirmation), `{{1}} name {{2}} order {{3}} courier {{4}} AWB` (shipped) and `{{1}} name {{2}} order` (delivered) and put their names in the settings above.
* Admin → **WhatsApp** tab shows status, the message log and lets you send a message.

Local testing of the webhook: expose the API with a tunnel (e.g. `cloudflared tunnel --url http://localhost:5118`) and use the tunnel URL as the Callback URL.

## 4. API surface (summary)

Public: `GET /api/products`, `POST /api/orders/validate`, `POST /api/orders`, `GET /api/orders/{id}`, `GET /api/orders/track?query=`, `POST /api/payments/payu-initiate|payu-response|verify`,
`POST /api/auth/register|login|send-otp|verify-otp`, `GET /api/auth/check-user|me|my-orders`, `PUT /api/auth/me`, `GET /api/trust-batches[/{batchNo}]`, `GET /api/content/site|formulation-ingredients|formulation-metrics`, `GET/POST /api/reviews`,
`POST /api/webhooks/shiprocket/webhook`, `GET|POST /api/webhooks/whatsapp`.
Admin (JWT, role `admin`): `POST /api/admin/login`, `GET /api/admin/verify|orders|customers|batches|products`, `POST /api/admin/orders[/{id}/retry-shiprocket|deliver|send-email]`, `PUT/DELETE /api/admin/batches`, `PUT/DELETE /api/admin/content`,
`PUT /api/admin/formulation/ingredients|metrics`, `GET /api/admin/whatsapp/status|messages`, `POST /api/admin/whatsapp/send`.

## 5. Production checklist
* Set `Jwt:Secret` (32+ chars), `Admin:Email/Password`, `Store:FrontendUrl`, `Store:ApiPublicUrl`, `Cors:Origins` (the web app origin) via environment/secrets — the values in `appsettings.Development.json` are for local use only.
* PayU: set `PayU:MerchantKey/MerchantSalt/Env=live`; the PayU `surl/furl` is `<ApiPublicUrl>/api/payments/payu-response`, which redirects the browser back to `<FrontendUrl>/checkout`.
* Shiprocket: set credentials and, in Shiprocket's panel, the webhook URL `<ApiPublicUrl>/api/webhooks/shiprocket/webhook` with the token from `Shiprocket:WebhookToken` sent as `x-api-key`.
