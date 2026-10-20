# Nura Herbex — WhatsApp CRM: Analysis & Phased Plan

Status: **analysis only — nothing implemented yet.** Phase 1 (WhatsApp lead capture) is next.

Flow: **Lead Generation → Lead Assignment → Customer Communication → Follow-up → Sales Conversion → Order Tracking → Customer Retention**

---

## 1. What already exists (reuse, don't rebuild)

| Area | Today | Gap for CRM |
|---|---|---|
| WhatsApp Cloud API (direct Meta) | `WhatsAppService`, `WhatsAppWebhookController` (`GET` verify, `POST` signed events, idempotent inbound by `wamid`), `SendTextAsync`, `SendTemplateAsync`, status updates (sent/delivered/read/failed) | Messages are stored flat in `WhatsAppMessages` keyed only by phone. No Lead, no conversation, no assignee, no opt-in, no 24h-window tracking, no media storage |
| Order notifications | Confirmed / Shipped / Delivered templates via WhatsApp + email | Not linked to a lead/customer timeline |
| Customers | `Customer` (storefront account: email, phone, address, password hash) | Not the same as a CRM *contact*; most WhatsApp enquirers will have no account |
| Orders | `Order` with `CustomerPhone`, Shadowfax tracking | Can be matched to a lead by normalized phone |
| Auth | JWT, roles `admin` and `customer`; single admin from config | No employees, no `sales` role |
| Admin UI | Blazor `AdminWhatsAppTab` (raw message list + send) | No lead board, inbox, follow-up queue, dashboards |
| Platform | .NET 10 API, EF Core, SQL Server, Blazor WASM + MAUI | Needs a background-job host for scheduled follow-ups (a `BackgroundService` pattern already exists in `ShadowfaxReconciliationWorker`) |

Conclusion: the webhook transport is done. Phase 1 is mostly **data model + lead creation + inbox UI** on top of it.

---

## 2. Decision: Meta Cloud API direct vs Interakt / WATI

Interakt and WATI are BSPs (resellers) that wrap the same WhatsApp Cloud API and add a shared inbox, broadcast and CRM UI, charging a monthly seat fee plus a message markup. You are building your own CRM, so their UI duplicates what you are building.

| | Direct Meta Cloud API (current) | Interakt / WATI |
|---|---|---|
| Cost | Meta per-conversation/message fee only | Monthly plan + per-seat + markup |
| Control & data | Full — own DB, own rules | Data lives in their system; sync via webhooks |
| Effort | You build inbox, templates UI, retries | Faster start; limited customisation, API rate/plan limits |
| Lock-in | None | Number migration needed to leave |

**Recommendation:** stay on **Meta Cloud API direct**, and put an `IWhatsAppProvider` abstraction behind it (`MetaCloud` now; `Interakt`/`Wati` adapters possible later because both expose webhooks + send-message/template APIs). Interakt/WATI docs are used as *feature references* (inbox, templates, tags, assignment, broadcasts).

Instagram / Facebook share the same Meta app: Messenger/IG DMs arrive via the same webhook mechanism (`object: instagram | page`), and Facebook/Instagram **Lead Ads** arrive via the `leadgen` webhook. So one Meta app + one signed webhook endpoint covers all three social sources.

---

## 3. Modules

### 3.1 User Management (Essential)
- New `Employee` entity: Id, Name, Email, Phone, PasswordHash, Role (`admin` | `sales`), IsActive, CreatedAt, LastLoginAt.
- Separate login per employee (`/api/staff/auth/login`) issuing JWT with role `admin` or `sales`. Existing config-based admin stays as the bootstrap/super admin.
- Admin: create / deactivate (soft-remove, keep history) / reset password / change role.
- Sales executive sees only **their** leads, follow-ups and conversations; admin sees everything.
- Deactivating an employee triggers lead reassignment (see 3.2).
- Audit trail: `ActivityLog` (who did what to which lead).

### 3.2 Lead Management (Essential)
**Lead sources:** `whatsapp`, `instagram`, `facebook`, `website`, `manual`.

**Entity `Lead`:** Id, Name, Phone (E.164 normalized, unique index), Email?, Source, SourceDetail (campaign / ad / form / page), Status, AssignedToEmployeeId?, ConsentStatus, FirstMessage, Tags, Notes, CustomerId? (link when they buy), CreatedAt, LastContactAt, ConvertedAt?.

**Status flow:** `New → Interested → Follow-up → Converted` or `Not Interested` (reopenable by admin). Every change is written to `LeadStatusHistory`.

**Capture rules**
- WhatsApp: first inbound message from an unknown number → create Lead (`New`, source `whatsapp`, name from WhatsApp profile). Known number → append to existing lead, never create a second.
- Instagram/Facebook: DM webhook or Lead-Ad `leadgen` event → Lead (phone required; if only IG/FB id is available, store `ExternalId` and mark phone missing).
- Website: contact/enquiry form and "Order on WhatsApp" button post to `POST /api/leads/website` (rate-limited, honeypot/captcha-light); optionally abandoned-checkout phone capture.
- **Duplicate prevention:** normalize phone (strip spaces, `+`, leading 0, apply `DefaultCountryCode` 91) → unique key. Secondary match on email. Duplicate arrival from a different source is merged: keep the oldest lead, record the extra source in `LeadSourceEvents`, and notify the assignee.
- **Assignment:** manual by admin, or auto rules — round-robin across active sales executives (default), optionally weighted/by source. Unassigned leads go to an admin "Unassigned" queue.
- **Transfer:** admin or current owner moves a lead to another employee with a reason; history logged and the new owner notified.

### 3.3 WhatsApp Management (Essential)
- **Conversation** entity per lead/phone: LastInboundAt, LastOutboundAt, UnreadCount, **ServiceWindowExpiresAt** (= last inbound + 24h).
- **Inbox UI:** list of conversations (filter: mine / unassigned / unread), chat thread, delivery ticks from status webhooks.
- **Send rules (Meta policy):** free-form text only inside the 24h window; outside it, only **approved templates**. The UI must enforce this and show the countdown.
- **Template registry:** local table mirroring approved Meta templates (name, language, category `utility | marketing | authentication`, variables, status). Needed because follow-ups are mostly templates.
- **Opt-in / consent:** `ConsentStatus` (`unknown | opted_in | opted_out`), `ConsentAt`, `ConsentSource`. Inbound message from the customer counts as opt-in for service replies; marketing/follow-up templates require explicit opt-in (website checkbox, or a WhatsApp reply to a consent prompt). Keywords `STOP` / `UNSUBSCRIBE` set `opted_out` and halt all automation immediately.
- **Media:** download inbound media via Graph API (media ids expire), store in blob/disk, keep reference.
- Reliability: webhook returns 200 fast and enqueues processing; outbound sends retry with backoff; failed statuses surface on the lead.

### 3.4 Follow-up Management (Essential)
**Entity `FollowUp`:** Id, LeadId, AssignedToEmployeeId, DueAt, Type (`call | whatsapp | note`), Status (`pending | done | skipped | overdue`), Outcome, CreatedBy, CompletedAt.

- Sales exec schedules follow-ups from a lead; a lead set to *Follow-up* requires a due date.
- **Reminders:** in-app notification + (optional) WhatsApp/email/push to the exec at due time; overdue items escalate to admin after a configurable delay.
- Views: Today, Overdue, Upcoming, Completed; per-employee counts.

**Automated 15-day journey** (starts when a lead becomes *Converted* / first order is placed; customer must be `opted_in`):

| Day | Step | Channel | Notes |
|---|---|---|---|
| 1 | Welcome & usage guidance | WhatsApp template | Utility |
| 3 | Usage check | WhatsApp template | Quick-reply buttons (Using / Need help) |
| 7 | Experience check | WhatsApp template | Reply with rating / feedback |
| 10 | Human call | **Task** for sales exec (no message) | Auto-created FollowUp type `call` |
| 12 | Reorder reminder | WhatsApp template | Marketing category, needs opt-in |
| 14–15 | Reorder follow-up | WhatsApp template + task if no reply | Ends journey |

Engine: `JourneyDefinition` / `JourneyStep` (editable offsets and template names) and `JourneyEnrollment` + `ScheduledMessage` rows processed by a `BackgroundService` every minute.
Guards: skip if opted-out; stop the journey if the customer replies "not interested", reorders, or the lead is closed; if a customer reply arrives, pause automation and notify the owner; respect quiet hours (e.g. 9 am–8 pm IST); dedupe per (enrollment, step). Day offsets count from the delivery date when available (Shadowfax delivered webhook) rather than the order date.

### 3.5 Sales Management (Essential)
- Convert lead → **Sale**: either linked to an existing storefront `Order` (matched by phone) or a manual order created by the exec for WhatsApp-assisted sales (product, qty, price, COD/prepaid, address) that flows into the existing order/shipping pipeline.
- Optional payment-link sending (PayU) inside the chat.
- Conversion sets Lead = `Converted`, stamps `ConvertedAt`, attributes revenue to the employee and source, and enrolls the 15-day journey.
- Order tracking: reuse existing Shadowfax tracking; order status changes appear on the lead timeline and trigger the existing WhatsApp notifications.

### 3.6 Customer Management (Essential)
- `Customer` (existing) gains a CRM view: contact details, address, all conversations, orders, lead history, tags, lifetime value, order count, last order date, next reorder date.
- Link Lead ↔ Customer by phone (and email). A Customer record is created/linked at first order.
- Retention: reorder-due list (from product consumption days), repeat-customer tag, inactive 30/60/90-day segments, opt-in-only broadcasts.

### 3.7 Website Integration (Essential)
- Enquiry / "Contact us" / callback form → `POST /api/leads/website`.
- Floating "Chat on WhatsApp" click-to-chat link with a pre-filled message and UTM code (e.g. `NH-WEB-xxxx`) so the first WhatsApp message can be attributed back to the web source/campaign.
- Checkout capture: tie web orders to leads by phone; consent checkbox for WhatsApp updates stored as `ConsentStatus`.
- Meta Click-to-WhatsApp ads: the webhook `referral` object carries ad/campaign ids → stored in `SourceDetail`.

### 3.8 Basic Dashboard & Reports (Essential)
- Dashboard: new leads (today/week), by source, by status funnel, unassigned count, follow-ups due/overdue, conversions and conversion rate, revenue by employee, response time (first reply), journey performance (reorder rate).
- Reports: lead source performance, employee performance, follow-up compliance, lost-reason breakdown; CSV export.
- Sales exec sees personal dashboard only.

---

## 4. Data model (new tables)

```
Employee 1—* Lead (AssignedTo)
Lead 1—1 Conversation 1—* WhatsAppMessage (existing; add ConversationId, LeadId, SenderEmployeeId, TemplateName, MediaUrl)
Lead 1—* FollowUp
Lead 1—* LeadStatusHistory / LeadAssignmentHistory / LeadSourceEvent
Lead *—1 Customer (nullable)   Customer 1—* Order (existing)
Lead 1—* JourneyEnrollment 1—* ScheduledMessage
JourneyDefinition 1—* JourneyStep
WhatsAppTemplate, ActivityLog, Notification, AutomationSetting
```

Indexes: `Lead.Phone` unique, `Lead(AssignedTo, Status)`, `FollowUp(DueAt, Status)`, `ScheduledMessage(DueAt, Status)`, `WhatsAppMessage.WaMessageId`.
EF Core migrations (existing `Data/Migrations`), one migration per phase.

---

## 5. Key API surface (summary)

| Group | Endpoints |
|---|---|
| Staff auth | `POST /api/staff/auth/login`, `GET /me` |
| Employees (admin) | `GET/POST/PUT/DELETE /api/admin/employees` |
| Leads | `GET /api/leads` (filters), `GET /{id}`, `POST` (manual), `PUT /{id}/status`, `PUT /{id}/assign`, `POST /{id}/transfer`, `POST /{id}/notes` |
| Conversations | `GET /api/conversations`, `GET /{id}/messages`, `POST /{id}/send` (text or template), `POST /{id}/read` |
| Follow-ups | `GET /api/followups?scope=today|overdue`, `POST`, `PUT /{id}/complete` |
| Journeys | `GET/PUT /api/admin/journeys` |
| Public | `POST /api/leads/website`, existing `/api/webhooks/whatsapp` (extended with IG/FB) |
| Reports | `GET /api/reports/dashboard`, `/leads`, `/employees` |

Real-time inbox: SignalR hub (new message, assignment, reminder) with polling fallback for the MAUI app.

---

## 6. Cross-cutting concerns

- **Security:** webhook signature (exists); role-based authorization on every CRM endpoint; sales exec row-level scoping; secrets (Meta tokens) via user-secrets/env, not `appsettings.json`; encrypt access tokens at rest if stored in DB.
- **Compliance:** explicit opt-in, STOP handling, template category correctness (marketing vs utility), data-deletion request support (India DPDP Act).
- **Meta limits:** 24h window, template approval lead time (build/submit templates early), messaging tier limits per phone number, quality rating — poor opt-in practice lowers it.
- **Reliability:** idempotent webhook processing (exists), outbound retry queue, dead-letter list visible to admin, timezone handling (store UTC, schedule in IST).
- **Phone normalization:** single shared helper (already `NormalizePhone`) used by leads, orders and customers.

---

## 7. Phased delivery

| Phase | Scope | Result |
|---|---|---|
| **1 — WhatsApp lead capture** | `Lead`, `Conversation`, link existing `WhatsAppMessage`; auto-create/dedupe lead on inbound; `referral` (Click-to-WhatsApp ad) capture; consent flags + STOP; admin Leads list + conversation view in existing admin; migration | Every WhatsApp enquiry becomes a unique lead with full chat history |
| 2 — Users & assignment | `Employee`, staff login, roles, round-robin/manual assign, transfer, status workflow + history, sales-exec scoped views | Team can work leads |
| 3 — Inbox & templates | Full inbox UI, 24h-window enforcement, template registry/sending, media, SignalR | Customer communication in-app |
| 4 — Follow-ups | Follow-up tasks, reminders, overdue escalation | No lead goes cold |
| 5 — Sales & customers | Lead→order conversion, customer 360 view, order timeline | Sales conversion + tracking |
| 6 — 15-day automation | Journey engine, scheduled messages, guards, quiet hours | Retention/reorder automation |
| 7 — Website + Instagram/Facebook | Web form/click-to-chat attribution, IG/FB DMs and Lead Ads | All sources captured |
| 8 — Dashboard & reports | KPIs, funnels, employee/source reports, CSV | Management visibility |

### Phase 1 detail (to confirm before coding)
1. Migration: `Leads`, `Conversations`, `LeadSourceEvents`; add `LeadId`/`ConversationId`/`ReferralJson` to `WhatsAppMessages`.
2. Phone normalization + unique index; `LeadService.UpsertFromWhatsAppAsync` called inside `HandleInboundMessageAsync` (same transaction; idempotent on `wamid`).
3. Parse `referral` (ad id, headline, source url) and store on `SourceDetail`.
4. Consent: inbound = service-window opt-in; STOP/UNSUBSCRIBE → `opted_out`; marketing opt-in recorded separately.
5. Admin endpoints: `GET /api/admin/leads`, `GET /api/admin/leads/{id}` (with messages), `PUT status`, notes.
6. Admin UI: Leads tab (table + filters + detail drawer with chat) replacing the raw message list.
7. Move AppSecret/AccessToken out of tracked config; verify end to end with ngrok/Dev tunnel + Meta test number; add `tools/` script that posts signed sample payloads.
8. Decide whether the existing `AutoReply` (order-status bot) stays; recommendation: keep it but skip when a human has replied in the last N minutes.

## 8. Open questions
1. Confirm **Meta Cloud API direct** (recommended) vs Interakt/WATI as the sending provider — do you already have a verified WhatsApp Business number and Meta app?
2. Same WhatsApp number for the CRM and the existing order notifications?
3. Assignment: round-robin, or manual by admin initially?
4. How many sales executives, and is the CRM web-only or also in the MAUI app?
5. Are the 15-day messages already written/approved as Meta templates (English/Hindi/regional)?
6. Instagram/Facebook: Lead Ads, DMs, or both?
7. Data retention period for chats and media?
