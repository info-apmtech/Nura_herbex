using Microsoft.Extensions.Logging;
using Nuraherbex.UI.Services;

namespace Nuraherbex.Maui;

public static class MauiProgram
{
	// Debug and Release both use the live API.
	private const string ProductionApi = "https://nuraapi.apmiot.com/";

	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
			});

		builder.Services.AddMauiBlazorWebView();
		builder.Services.AddNuraherbexUI(ResolveApiBase());

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}

	private static string ResolveApiBase() => ProductionApi;
}
