// DropSense — Services/ServiceHelper.cs
namespace DropSense.Services;

/// <summary>
/// Service-locator escape hatch for views that are instantiated directly by
/// XAML (e.g. child ContentViews embedded in a page) rather than resolved
/// through Shell's DI pipeline. Only use this where constructor injection
/// isn't reachable — prefer DI everywhere else.
/// </summary>
public static class ServiceHelper
{
    public static T GetRequiredService<T>() where T : notnull =>
        IPlatformApplication.Current!.Services.GetRequiredService<T>();
}