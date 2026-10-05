# Porting the React storefront to Blazor — conventions

Source of truth for design: the React app in `E:\Hamee\WorkingProcess\Nuraherbex\nuraherbex\src` (JSX + Tailwind).
Target: Razor components in `E:\Hamee\WorkingProcess\Nuraherbex\nuraherbex\blazor\src\Nuraherbex.UI` (a Razor Class Library
shared by the Blazor WebAssembly web app and the .NET MAUI Blazor Hybrid app). Everything is interactive client-side; there is no SSR/prerender.

## Fidelity rules (the user wants *exactly* these designs)
1. **Copy Tailwind class strings verbatim** (`className="..."` → `class="..."`), keep the same DOM structure, element order, ids, text, copy, emojis, and inline `style`.
   Dynamic classNames built with template strings/ternaries/`cn()`/`clsx()` become Razor expressions: `class="@($"base {(active ? "a" : "b")}")"`.
   Tailwind generates CSS by scanning `.razor` and `.cs` files, so keep complete class names as literals (never concatenate partial class names like `"bg-" + color`).
2. **Icons**: `lucide-react` → `<Icon Name="ShoppingBag" Size="18" StrokeWidth="2" Class="..." />` (see `Components/Ui/Icon.razor`; 80 icons are available in
   `LucideIcons.g.cs`; it already covers every icon the React source imports). `fill="currentColor"` → `Fill="currentColor"`. If you need an icon that is missing, append its
   lucide-react name (space separated) to `blazor/tools/extra-icons.txt`, then run from the repo root `node blazor/tools/gen-icons.cjs` (it regenerates the file from the React source + that list; safe to re-run; never edit the .g.cs by hand).
3. **Images/video**: use `Media.*` from `Nuraherbex.UI.Config` (Cloudflare R2 CDN URLs — same as the original). `siteConfig.*` → `SiteConfig.*`.
4. **Text content** comes from `ContentState.Content` (typed `SiteContent`, mirrors `siteContent.json`) wherever the React code used `useContent()`.
5. Do not redesign, "improve", or add features that the React page does not have — except where an API/Blazor adaptation is needed.
6. Animations: Tailwind keyframes/utilities and `globals.css` rules were already copied (`Styles/app.css`, `tailwind.config.js`). The IntersectionObserver reveal/scroll-progress logic
   lives in `wwwroot/js/nura.js` and is started by `StoreLayout`; you do **not** need to port `useScrollEffects`. Anything else that needs JS must go through `BrowserInterop`.

## Razor gotchas
* A literal `@` in markup/class strings must be `@@` (e.g. `@@container`, emails in text: `care@@nuraherbex.com`). Inside `@code`/attributes it is fine.
* Curly braces in markup text are fine, but inside `@code { }` strings/JSON must stay balanced. Use `@("{")` if you need an unbalanced brace in markup.
* `onClick={() => x}` → `@onclick="() => X()"`; `onChange` → `@onchange` (or `@bind`/`@bind:event="oninput"` for live typing); `onSubmit` + `e.preventDefault()` → `<form @onsubmit="HandleSubmit">` using `EditForm` is NOT required;
  plain `<form @onsubmit="Handler" @onsubmit:preventDefault>` works. `e.stopPropagation()` → `@onclick:stopPropagation`.
* `{cond && <X/>}` → `@if (cond) { <X/> }`; `.map()` → `@foreach`; `key=` is `@key="..."` (only needed on dynamic lists).
* `style={{ a: b }}` → `style="a: @b"`. Boolean attributes: `disabled="@(cond)"`.
* `dangerouslySetInnerHTML` → `@((MarkupString)html)`.
* Use `<PageTitle>` only if needed; meta tags are applied with `Browser.SetMetaAsync(RouteMeta.X)` in `OnAfterRenderAsync(firstRender)` (see below).
* After async work started from non-Blazor callbacks (timers, events from services), call `await InvokeAsync(StateHasChanged)`.
  Subscribe to `CartState.Changed` / `AuthState.Changed` / `ContentState.Changed` in `OnInitialized` and unsubscribe in `Dispose` (`@implements IDisposable`).
* `setTimeout(() => ..., ms)` → `await Task.Delay(ms)` followed by `StateHasChanged()`; `setInterval` → `System.Threading.PeriodicTimer` or `System.Timers.Timer` (dispose it).
* Never block on JS interop during `OnInitialized`; do JS in `OnAfterRenderAsync`.
* Namespace imports are global (see `_Imports.razor`): `Nuraherbex.Shared.Models`, `Nuraherbex.UI.Services`, `Nuraherbex.UI.Config`, `Nuraherbex.UI.Components`, `Nuraherbex.UI.Components.Ui`, `Nuraherbex.UI.Components.Admin`, `Nuraherbex.UI.Layout`.
* Put component-private helpers in the `@code` block; if a file's `@code` exceeds ~400 lines move it to a code-behind `Foo.razor.cs` (`public partial class Foo`).
* Do **not** use scoped `.razor.css` files — all styling is Tailwind utility classes in markup.

## Routes (pages)
| React page | Razor file | `@page` routes |
|---|---|---|
| HomePage | `Pages/Home.razor` | `/` |
| ShopPage (preview view) | `Pages/Shop.razor` | `/shop`, `/store` |
| ShopPage (pdp view) | same component, parameterised | `/products/stamix`, `/product/stamix`, `/products`, `/product`, `/shop/stamix` |
| CheckoutPage | `Pages/Checkout.razor` | `/checkout` (query: `status`, `orderId`, `message`, `txnid`) |
| TrackOrderPage | `Pages/TrackOrder.razor` | `/track`, `/track-order` |
| VerifyProductPage | `Pages/VerifyProduct.razor` | `/trust-passport`, `/trust-passport/{BatchNo?}`, `/verify`, `/verify-product`, `/verify-batch` |
| FormulationPage | `Pages/Formulation.razor` | `/formulation`, `/ingredients`, `/the-formula` |
| AccountPage | `Pages/Account.razor` | `/account`, `/my-account`, `/profile` |
| AdminPortalPage | `Pages/Admin.razor` (+ `Components/Admin/*`) | `/admin` — uses `@layout AdminLayout` |

`NavigationManager.NavigateTo("/shop")` replaces React `onNavigate('/shop')` / `setCurrentPage('shop')`.
Legacy hash targets: `#privacy`/`#terms`/`#review` → `ModalState.Open("privacy"|"terms"|"review")`; `#label`/`#clinical-data`/`#supplement-facts` → `/formulation`; any other `#section` on the home page →
`NavigationManager.NavigateTo("/#section")` (StoreLayout scrolls to the id) — for links already on the home page call `Browser.ScrollToIdAsync("section")`.
Components that took `onNavigate`, `setCurrentPage`, `onOpenModal` props must **not** take them: inject `NavigationManager` and `ModalState` instead.

## Services you can inject (all in `Nuraherbex.UI.Services`, scoped)
* `ApiClient api` — every backend call (`GetProductsAsync`, `ValidateCartAsync`, `CreateOrderAsync`, `PayUInitiateAsync`, `PayUVerifyAsync`, `TrackOrderAsync`, `GetBatchesAsync`, `GetBatchAsync`, `LoginAsync` … `AdminXxxAsync`). Methods never throw; check `.Success` / `.Message`.
  The React code called `fetch('/api/...')` and Supabase directly — replace *all* of that with `ApiClient` (no localStorage "offline order/batch caches": the API is the source of truth).
* `CartState cart` — `Items`, `ItemCount`, `Subtotal`, `FreeShippingProgress`, `IsCartOpen`, `AddToCartAsync(CartItem, qty)`, `UpdateQuantityAsync(id, delta)`, `RemoveItemAsync`, `ClearAsync`, `SetOpen(bool)`, `ToLines()`; event `Changed`.
  `CartItem { Id, VariantId, Name, Price, Image, ServingsText, Quantity, IsSubscription }`. Product ids are the API ids: `stamix-single`, `stamix-duo`, `stamix-trio`, `stamix-collector` (see `GET api/products`).
* `AuthState auth` — customer session: `Customer` (CustomerDto), `IsAuthenticated`, `IsLoading`, `LoginAsync`, `RegisterAsync`, `SendOtpAsync`, `VerifyOtpAsync`, `UpdateProfileAsync`, `SignOutAsync`, `AdoptAsync(AuthResponse?)`; event `Changed`.
* `AdminSession admin` — `IsAuthenticated`, `Email`, `InitAsync()`, `LoginAsync(email, pw)`, `SignOutAsync()`.
* `ContentState content` — `Content` (SiteContent), `SaveAsync(draft)`, `ResetToDefaultsAsync()`, `ExportJson()`; event `Changed`.
* `ModalState modal` — `Active` ("review" | "privacy" | "terms" | null), `Open(type)`, `Close()`.
* `BrowserInterop browser` — `SetMetaAsync(RouteMeta.Home)`, `ScrollToTopAsync()`, `ScrollToIdAsync(id)`, `CopyToClipboardAsync`, `PostFormAsync(url, fields)` (PayU), `OpenUrlAsync`, `DownloadTextAsync`, `ReadFileAsDataUrlAsync(inputId)`, `ConfirmAsync(msg)`, local/session storage helpers.
* Shared DTOs are in `Nuraherbex.Shared.Models` (see `src/Nuraherbex.Shared/Models/CommerceModels.cs` and `SiteContent.cs`). **Do not edit Shared or Services** — if you need a change (new DTO field, API method), keep a note in your final report instead
  (small additive helpers inside your own files are fine).

React snake_case fields map to the DTO PascalCase properties: `order.customer_name` → `order.CustomerName`, `order.shiprocket_awb` → `ShiprocketAwb`, `order.delivery_tracking_events` → `DeliveryTrackingEvents`,
`order.items[i].qty/quantity` → `Items[i].Quantity`, `shipping_address.addressLine1` → `ShippingAddress.AddressLine1`, `trust_batches.quality_checks` → `TrustBatchDto.QualityChecks`, etc.
`ProductDto` replaces the product catalog objects (`Price`, `CompareAtPrice`, `StockQuantity`, `ImageUrl`).

## Page lifecycle template
```razor
@page "/shop"
@implements IDisposable
@inject NavigationManager Nav
@inject CartState Cart
@inject BrowserInterop Browser

@code {
    protected override void OnInitialized() { Cart.Changed += OnChanged; }
    protected override async Task OnAfterRenderAsync(bool firstRender) { if (firstRender) await Browser.SetMetaAsync(RouteMeta.Shop); }
    private void OnChanged() => _ = InvokeAsync(StateHasChanged);
    public void Dispose() => Cart.Changed -= OnChanged;
}
```

## Building / verifying
* Compile-check the shared UI library with the serialised helper (a global mutex queues concurrent callers; other agents' unfinished files may produce errors — only fix errors in *your* files):
  `powershell -NoProfile -File E:\Hamee\WorkingProcess\Nuraherbex\nuraherbex\blazor\build-ui.ps1 -Match "YourFileNameFragment|AnotherFragment"`
* Never run `dotnet build`/`dotnet run` directly on the solution or on the UI project, never touch `obj/` or `bin/`, never run `npm`/Tailwind builds — the lead does CSS generation and visual verification.
* Only create/modify files that belong to your assignment. Components shared by several pages are owned by exactly one agent (listed in your assignment); use the public parameter contract described there.
* When done, reply with: files created, public component contracts (parameters), anything you could not port 1:1 and why, any missing API/DTO fields you needed.
