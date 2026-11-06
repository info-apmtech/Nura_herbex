# WhatsApp templates to submit to Meta

WhatsApp lets you send free text, photos, videos and PDFs only inside **24 hours of the customer's last message**.
After that, the only thing you can send is a template Meta has approved; when the customer replies, the window
reopens. The app already has every template below written, with the right variables, in
**Admin → Team & organisation → Messages & templates → WhatsApp templates**:

1. Press **Copy text** on a template.
2. In Meta Business Manager → WhatsApp Manager → Message templates → *Create template*, use **exactly** the name and
   category shown, language **English** (`en`, the same as `WhatsApp:TemplateLanguage`), no header or footer.
   Paste the text as the body. Use the example values shown under it when Meta asks for samples.
3. When Meta approves it, switch **Approved** on in the app and press *Save messages*.
   Only approved templates appear in the chat's **Send an approved template** box.

Category notes: *Utility* templates confirm or support something the customer already did or asked for;
*Marketing* templates promote. Meta may re-label a template; that is fine, the name stays the same.

## Templates (14)

| Name | Stage | Category | Body |
|---|---|---|---|
| `nura_lead_reconnect` | Leads | Marketing | Hello {{1}}, this is {{2}} from Nura Herbex 🌿 We would love to help you with {{3}}. Reply here and we will continue from where we left off. |
| `nura_info_ready` | Leads | Utility | Hello {{1}}, the details you asked about are ready. Reply YES and {{2}} from Nura Herbex will send them right away. *(quick-reply button: "Yes, send them")* |
| `nura_docs_ready` | Leads | Utility | Hello {{1}}, the usage guide and product video you asked for are ready. Reply here to receive them. *(quick-reply button: "Send me the files")* |
| `nura_callback_confirm` | Leads | Utility | Hello {{1}}, {{2}} from Nura Herbex will call you on {{3}}. Reply here if another time suits you better. |
| `nura_hot_nudge` | Interested | Marketing | Hello {{1}}, {{2}} here from Nura Herbex. Your {{3}} is still available. Reply here and I will help you place your order today. |
| `nura_offer` | Interested | Marketing | Hello {{1}}, we have a special offer on {{2}} this week: {{3}}. Reply here to know more. |
| `nura_followup_reminder` | Follow-up | Marketing | Hello {{1}}, just following up on our earlier chat about Stamix. Would you like to continue? Reply here any time and {{2}} will help you. |
| `customer_welcome` | Customers | Utility | Hello {{1}}, thank you for your order {{2}} with Nura Herbex 🌿 / I am {{3}} from our customer care team and I will look after you from here. Message us on this number any time for order updates or help with your product. |
| `nura_order_checkin` | Customers | Utility | Hello {{1}}, this is {{2}} from Nura Herbex care. Your order {{3}} is on its way. Reply here if you need anything. |
| `nura_usage_tips` | Customers | Utility | Hello {{1}}, a quick tip for your Stamix routine: {{2}}. Reply here if you have any questions. |
| `nura_feedback` | Customers | Marketing | Hello {{1}}, how are you finding Stamix? Reply here and tell us. We read every message. |
| `nura_reorder` | Customers | Marketing | Hello {{1}}, your Stamix pack will be finishing soon. Reply here and {{2}} will arrange your next order. |
| `nura_issue_followup` | Customers | Utility | Hello {{1}}, this is {{2}} following up on your concern about order {{3}}. Reply here and we will sort it out. |
| `nura_payment_reminder` | Customers | Utility | Hello {{1}}, your order {{2}} is waiting for a payment of ₹{{3}}. Reply here and we will help you complete it. |

The first variable is always the customer's first name, filled in by the app. Variables for the sender's name and the
order number are filled in too. Only the variables the sender must choose (a topic, an offer, a time, an amount) are
typed in the chat. `customer_welcome` is also sent automatically after a customer's first paid order; after Meta
approves it, set `WhatsApp:CustomerWelcomeTemplate = customer_welcome` on the server.

The two buttons ("Yes, send them", "Send me the files") are **Quick reply** buttons added when you create the template.
When the customer taps one, the window reopens and your team can send the PDF and video from **Files & videos**.

## Automatic order messages (already in the app, separate setting)

These use the names in `WhatsApp:OrderConfirmationTemplate`, `OrderShippedTemplate` and `OrderDeliveredTemplate`:

| Suggested name | Body |
|---|---|
| `order_confirmation` | Hello {{1}}, your order {{2}} for {{3}} is confirmed. We will message you when it ships. |
| `order_shipped` | Hello {{1}}, your order {{2}} has shipped with {{3}}. Tracking number: {{4}}. |
| `order_delivered` | Hello {{1}}, your order {{2}} has been delivered. We hope you enjoy it. Reply here if you need any help. |

## Sending files and videos

Files live in **Team & organisation → File library** (upload once, staff reuse them). In an open chat, **Files & videos**
offers each file as **Send file** (a real WhatsApp document, video or image) or **Send link**. WhatsApp delivers
documents up to 100 MB, videos up to 16 MB and images up to 5 MB as attachments; larger files go as a link.
The file must be reachable from the internet over https: set `Store:ApiPublicUrl` to your public API address
(already `https://nuraapi.apmiot.com`), and keep `WhatsApp:LibraryPath` outside the deploy folder so redeploys
do not delete uploads. IIS limits uploads to 30 MB by default; raise `maxAllowedContentLength` in `web.config`
(for example `104857600`) to upload larger files.

## Who gets which automatic message

| Who is writing | After hours | "hi" / "hello" | "track" |
|---|---|---|---|
| New or follow-up lead | Professional "we are away" note, no tracking | Professional greeting | Greeting, no tracking |
| Interested lead | Own wording ("thank you for your interest…") | Professional greeting | Greeting, no tracking |
| Customer, order on its way | Note + offer to send *track* | Greeting that mentions the order | Live order status |
| Customer, everything delivered | Plain "we are away" note | Plain greeting | "Your order was delivered…" |

Typing an order number still always returns that order's status, as before.
