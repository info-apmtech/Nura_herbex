using Microsoft.Extensions.Logging;
using Nuraherbex.UI.Services;

namespace Nuraherbex.Maui;

public static class MauiProgram
{
	// Production API address. Update before shipping to the stores.
	private const string ProductionApi = "https://api.nuraherbex.com/";

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

	private static string ResolveApiBase()
	{
#if DEBUG
		// The Android emulator reaches the host machine through 10.0.2.2, not localhost.
		return DeviceInfo.Platform == DevicePlatform.Android ? "http://10.0.2.2:5118/" : "http://localhost:5118/";
#else
		return ProductionApi;
#endif
	}
}
