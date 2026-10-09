# Shadowfax forward shipping

The store-to-customer integration uses the existing order tables and Blazor/MAUI shared UI. Legacy Shiprocket shipments retain their existing provider. The database field names `ShiprocketAwb` and related fields remain for compatibility and contain the selected provider's identifiers.

## Workflow and API routes

1. `POST /api/orders/place` (also `/api/orders`) calculates prices on the server and saves a pending order. Online orders also have a payment-attempt snapshot. COD orders reserve stock immediately. Neither requests pickup.
2. Initiate PayU using `POST /api/payments/payu-initiate`. Confirm using the existing PayU callback or `POST /api/orders/{id}/confirm` with the complete signed PayU response. `udf1` must match the route. Never send an invented payment-success flag. Reverse hash, merchant, transaction association and exact amount are checked. Simulation is restricted to Development.
3. An administrator marks the parcel packed through `POST /api/shipping/{id}/ready`. Optional body: `{"weightKg":0.4,"lengthCm":10,"breadthCm":10,"heightCm":14}`. New orders have catalog-derived estimates; measure the actual package in the admin drawer before confirming. Existing orders without dimensions require these values.
4. `POST /api/shipping/{id}/book` requires admin JWT, valid payment/COD state, and packing readiness. Checks delivery, pickup and return serviceability for the configured tier. Stores a stable client reference before submission and the AWB on success. Concurrent shipment operations serialize using SQL Server session application locks. Repeating booking returns the saved shipment; timeout retries reuse the same reference. No automatic booking retries create pickups in the background.
5. `GET /api/shipping/{id}/track` requires the owning customer's JWT or admin JWT. Existing storefront tracking remains supported. `GET /api/shipping/serviceability/{pincode}` is public and rate limited; a null result means unavailable, not serviceable.
6. `POST /api/shipping/{id}/label` creates an HTTPS PDF label. `POST /api/shipping/{id}/cancel?reason=...` cancels an active shipment; response code 304 means queued and does not mark it cancelled. These require admin JWT. Existing `/api/admin/orders/{id}/retry-shipment`, `shipping-label`, and `cancel-shipment` routes remain.
7. `POST /api/webhooks/shadowfax` accepts authenticated callbacks. Unknown AWBs/references cannot update orders. Duplicate events are ignored; delayed events cannot regress status or change a delivered/cancelled order's display or COD payment state. A worker reconciles the least recently checked 50 active AWBs every five minutes because Shadowfax callbacks are not retried. Status notifications retain the application's existing best-effort background behavior.

Cancelled, returned and delivered orders are not rebooked through the normal book endpoint. Return pickups and pickup OTPs belong to separate Shadowfax products and are not enabled by this forward integration. Shipments with product value >= INR 50,000 are blocked until a workflow for e-way bill and seller GST details is added.

## Configuration

Set server-side secrets in deployment environment variables or a secret store. Do not place them in web/MAUI settings or commit them. Environment and command-line settings override appsettings.Local.json.

```text
Shadowfax__Env=production
Shadowfax__Token=<production token>
Shadowfax__WebhookToken=<random secret of at least 32 characters>
Shadowfax__OrderType=marketplace
Shadowfax__ServiceTier=Regular
Shadowfax__Pickup__Name=<registered pickup name>
Shadowfax__Pickup__Contact=<phone>
Shadowfax__Pickup__AddressLine1=<street>
Shadowfax__Pickup__City=<city>
Shadowfax__Pickup__State=<state>
Shadowfax__Pickup__Pincode=<six digits>
Shadowfax__Pickup__UniqueCode=<registered seller code>
```

For staging use `Shadowfax__Env=staging` and `Shadowfax__StagingToken`. Confirm marketplace/warehouse and Regular/Surface provisioning with Shadowfax; the application never silently switches tier. The return address defaults to pickup; override `Shadowfax__Return__...` if required. Production configuration validates token, pickup address and webhook secret on startup. Use a durable SQL Server database in production.

Register an HTTPS callback URL and the agreed Authorization header (`Token <WebhookToken>` or the exact secret) with your Shadowfax account manager. Callback templates are account-specific; arrange this shape:

```json
{
  "awb_number": "{awb_number}",
  "client_order_id": "{client_order_id}",
  "status_id": "{status_id}",
  "status": "{status}",
  "last_updated": "{last_updated}",
  "current_location": "{current_location}",
  "remarks": "{remarks}"
}
```

Timestamps with offsets are parsed as supplied; offset-free callback timestamps are interpreted as IST. A missing configured webhook secret returns 503. A wrong secret returns 401. No callback authentication scheme is assumed beyond the header explicitly registered for your account.

## Database rollout

The `AddShadowfaxFulfillment` migration adds parcel dimensions, packing/booking/tracking timestamps, saved booking references, and filtered unique indexes for Shadowfax AWBs and client references. It preserves existing orders. `Sql/02_shadowfax_migrations.sql` is a generated idempotent script for the complete migration chain; review it before use. No database migration has been applied as part of implementation.

Check existing duplicate AWBs before applying the unique index:

```sql
SELECT ShiprocketAwb, COUNT(*) AS DuplicateCount
FROM orders WHERE Courier = 'Shadowfax' AND ShiprocketAwb IS NOT NULL
GROUP BY ShiprocketAwb HAVING COUNT(*) > 1;
```

Back up the database, resolve any duplicates using actual courier records, and deploy the migration before starting the updated binaries. Prefer `Database__AutoCreate=false` in production and apply the reviewed script through your deployment process. The design-time EF factory deliberately uses a design-only database; `dotnet ef database update` must receive an explicit `--connection` for your intended database. Grant the runtime database principal permission to acquire/release session application locks.

## Verification and remaining live checks

```powershell
dotnet run --project tools/ShadowfaxChecks -c Release
dotnet run --project tools/AdminAuthChecks -c Release
dotnet build src/Nuraherbex.Web -c Release
```

Offline checks use fake HTTP responses and an isolated in-memory database. They do not create real shipments, test SQL Server locking across processes, or validate account provisioning. Before live rollout, exercise the migration against a staging SQL Server and test prepaid/COD booking, retry after an ambiguous timeout, label printing, authenticated callbacks, queued cancellation, and tracking reconciliation using your Shadowfax staging account. The currently running application must be restarted/deployed after migration to use these changes.

The existing public `/api/orders/{id}` and search-based tracking routes still return the legacy order DTO. Review their guest-access/privacy policy before exposing the whole application publicly. Notifications remain best effort rather than a durable outbox.

Official references checked: [Orders](https://developer.shadowfax.in/docs/api/forward-logistics/orders), [serviceability tiers](https://developer.shadowfax.in/docs/reference/serviceability), [callbacks](https://developer.shadowfax.in/docs/forward-logistics/callbacks), [tracking](https://developer.shadowfax.in/docs/forward-logistics/tracking), [labels](https://developer.shadowfax.in/docs/api/forward-logistics/label-and-pod).