using System.Reflection;

namespace Scratcharr;

public static class AppInfo
{
    /// <summary>The version from the project file, without the commit hash the SDK appends.</summary>
    public static readonly string Version =
        (typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
         ?? "0.0.0").Split('+')[0];
}
