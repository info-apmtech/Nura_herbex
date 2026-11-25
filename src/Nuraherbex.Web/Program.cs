using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Nuraherbex.UI.Services;
using Nuraherbex.Web;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// API address & Project Name come from wwwroot/appsettings.json (and appsettings.{Environment}.json).
var apiBase = builder.Configuration["ApiBaseUrl"] ?? "https://nuraapi.apmiot.com/";
var projectName = builder.Configuration["ProjectName"] ?? "NeuraHerbex";
builder.Services.AddNuraherbexUI(apiBase, projectName);

await builder.Build().RunAsync();
