using System;
using System.Collections.Generic;
using System.Reflection;

namespace CodeFusion;

/// <summary>
/// The CodeFusion packages use each other's internals, so an app must run them all at one version: a mix fails at the
/// first call that crosses, with a missing method or field. Their packages depend on each other at exactly their own
/// version, but NuGet lets an app's direct reference to another version win, with only an NU1608 warning. So each package
/// but Core also checks, as it loads (a module initializer the shared build adds, src/Directory.Build.props), that the
/// CodeFusion assemblies it uses are its version, and fails saying which to update:
/// <list type="bullet">
/// <item>A newer Core, Data or Http than the package: an <see cref="InvalidOperationException"/> naming both versions,
/// inside the <see cref="TypeInitializationException"/> the runtime wraps a module initializer's in.</item>
/// <item>A Core older than this check: no <c>PackageVersions</c> to call, so a <see cref="MissingMethodException"/> for
/// <c>CodeFusion.PackageVersions.Check</c>, wrapped the same way. It names the method, not the versions.</item>
/// <item>A mix that breaks a type the app's code loads first (an interface member that moved): a
/// <see cref="TypeLoadException"/> from the app's code, before the package's initializer runs.</item>
/// </list>
/// In the last two the message doesn't say "versions", but the fix is the same: every CodeFusion package at one release.
/// </summary>
internal static class PackageVersions
{
    /// <summary>Throws if one of <paramref name="others"/>, the CodeFusion assemblies <paramref name="self"/> uses (each
    /// named by a type of its own, so nothing is looked up by name and trimming keeps them), loaded at another version.</summary>
    public static void Check(Assembly self, params ReadOnlySpan<Assembly> others)
    {
        ArgumentNullException.ThrowIfNull(self);
        var loaded = new List<(string Name, string Version)>(others.Length);
        foreach (var a in others) loaded.Add((a.GetName().Name ?? "", VersionOf(a)));
        if (Mismatch(self.GetName().Name ?? "", VersionOf(self), loaded) is { } why) throw new InvalidOperationException(why);
    }

    /// <summary>Why <paramref name="self"/> at <paramref name="version"/> can't run with <paramref name="loaded"/>, or
    /// null when every one is at the same version.</summary>
    internal static string? Mismatch(string self, string version, IEnumerable<(string Name, string Version)> loaded)
    {
        foreach (var (name, other) in loaded)
            if (!string.Equals(other, version, StringComparison.Ordinal))
                return $"{self} {version} is running with {name} {other}. The CodeFusion packages share internals and must all be " +
                    $"the same version: update {name} to {version}, or every CodeFusion package to the same release.";
        return null;
    }

    /// <summary>The package version an assembly was built as: its informational version without the commit after '+'.</summary>
    static string VersionOf(Assembly a)
    {
        string v = a.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? a.GetName().Version?.ToString() ?? "";
        int plus = v.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? v : v[..plus];
    }
}
