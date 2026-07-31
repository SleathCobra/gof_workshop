using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace Gof2Workshop.Browser;

[SupportedOSPlatform("browser")]
internal static partial class BrowserLocalStorage
{
    [JSImport("getSetting", "./workshopStorage.js")]
    internal static partial string? GetSetting(string key);

    [JSImport("setSetting", "./workshopStorage.js")]
    internal static partial void SetSetting(string key, string value);

    [JSImport("clearWorkshopData", "./workshopStorage.js")]
    internal static partial void ClearWorkshopData();

    [JSImport("getWorkshopStorageBytes", "./workshopStorage.js")]
    internal static partial int GetWorkshopStorageBytes();
}
