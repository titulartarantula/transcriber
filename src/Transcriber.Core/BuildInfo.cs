using System.Reflection;

namespace Transcriber.Core;

/// <summary>An app's release version and the commit it was built from.</summary>
public sealed record BuildInfo(string Version, string? Commit)
{
    /// <summary>
    /// Reads the informational version the SDK stamps as "0.2.0+&lt;commit sha&gt;" when building from git.
    /// </summary>
    public static BuildInfo From(Assembly assembly)
    {
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3)
            ?? "0.0.0";
        return Parse(info);
    }

    internal static BuildInfo Parse(string informationalVersion)
    {
        int plus = informationalVersion.IndexOf('+');
        if (plus < 0) return new BuildInfo(informationalVersion, null);
        var sha = informationalVersion[(plus + 1)..];
        return new BuildInfo(informationalVersion[..plus], sha.Length == 0 ? null : sha[..Math.Min(7, sha.Length)]);
    }

    public override string ToString() => Commit is null ? Version : $"{Version} ({Commit})";
}

/// <summary>Which app is running, for notes' frontmatter. Set by each app at startup.</summary>
public static class ProductInfo
{
    public static string Client { get; set; } = "Transcriber";
}
