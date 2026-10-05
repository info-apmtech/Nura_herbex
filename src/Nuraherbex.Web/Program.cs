using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Nuraherbex.UI.Services;
using Nuraherbex.Web;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// API address comes from wwwroot/appsettings.json (and appsettings.{Environment}.json).
var apiBase = builder.Configuration["ApiBaseUrl"] ?? "http://localhost:5118/";
builder.Services.AddNuraherbexUI(apiBase);

await builder.Build().RunAsync();
