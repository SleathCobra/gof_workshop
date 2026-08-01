using Avalonia;
using Avalonia.Browser;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace Gof2Workshop.Browser;

internal sealed partial class Program
{
    [SupportedOSPlatform("browser")]
    private static async Task Main(string[] args)
    {
        // .NET 10 requires ES module imports to be registered before the first
        // source-generated JSImport stub is invoked. Keeping both modules here
        // also gives startup one explicit, auditable browser-interop boundary.
        // The runtime loader is rooted in /_framework; module identifiers must
        // match the attributes, while URLs step back to the application root.
        await JSHost.ImportAsync("./workshopStorage.js", "../workshopStorage.js");
        await JSHost.ImportAsync("./workshopWebGl.js", "../workshopWebGl.js");
        await BuildAvaloniaApp().StartBrowserAppAsync("out");
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>();
}
