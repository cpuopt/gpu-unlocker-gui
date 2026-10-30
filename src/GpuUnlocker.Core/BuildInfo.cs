using System.Reflection;

namespace GpuUnlocker.Core;

public static class BuildInfo
{
    public const string ProductName = "GPU Unlocker GUI";
    public static string Version => typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
        .InformationalVersion.Split('+')[0];
    public static string WindowTitle => ProductName + " v" + Version;
    public static string Header => WindowTitle;
}
