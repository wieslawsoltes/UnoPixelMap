using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.UI.Xaml;
using SkiaSharp;

namespace PixelMap.Controls;

/// <summary>Adapts Uno 6.7.135's recorded browser visuals to their owning Skia context.</summary>
internal static class BrowserGpuContext
{
    private const string AssemblyName = "Uno.UI.Runtime.Skia.WebAssembly.Browser";
    private const string HostTypeName = "Uno.UI.Runtime.Skia.WebAssembly.Browser.WebAssemblyBrowserHost";
    private const string RendererTypeName = "Uno.UI.Runtime.Skia.BrowserRenderer";
    private const string GlRendererTypeName = "Uno.UI.Runtime.Skia.WebGlBrowserRenderer";
    private const BindingFlags PrivateMembers = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly PropertyInfo? ApplicationHost = typeof(Application).GetProperty("Host", PrivateMembers);
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.NonPublicFields)]
    private static readonly Type? HostType = Type.GetType(HostTypeName + ", " + AssemblyName);
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.NonPublicFields)]
    private static readonly Type? RendererType = Type.GetType(RendererTypeName + ", " + AssemblyName);
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.NonPublicFields | DynamicallyAccessedMemberTypes.PublicMethods)]
    private static readonly Type? GlRendererType = Type.GetType(GlRendererTypeName + ", " + AssemblyName);
    private static readonly FieldInfo? HostRenderer = HostType?.GetField("_renderer", PrivateMembers);
    private static readonly FieldInfo? RendererBackend = RendererType?.GetField("_renderer", PrivateMembers);
    private static readonly FieldInfo? GlContext = GlRendererType?.GetField("_context", PrivateMembers);
    private static readonly MethodInfo? SelectContext = GlRendererType?.GetMethod("MakeCurrent", BindingFlags.Instance | BindingFlags.Public, []);

    [DynamicDependency("Host", typeof(Application))]
    [DynamicDependency("_renderer", HostTypeName, AssemblyName)]
    [DynamicDependency("_renderer", RendererTypeName, AssemblyName)]
    [DynamicDependency("_context", GlRendererTypeName, AssemblyName)]
    [DynamicDependency("MakeCurrent", GlRendererTypeName, AssemblyName)]
    internal static bool TryGet(out GRContext? context, out object? owner)
    {
        context = null;
        owner = null;
        if (!OperatingSystem.IsBrowser() || Application.Current is not { } application ||
            HostType is null || RendererType is null || GlRendererType is null || SelectContext is null) return false;
        var host = ApplicationHost?.GetValue(application);
        if (host?.GetType() != HostType) return false;
        var renderer = HostRenderer?.GetValue(host);
        if (renderer?.GetType() != RendererType) return false;
        owner = RendererBackend?.GetValue(renderer);
        if (owner?.GetType() != GlRendererType) return false;
        context = GlContext?.GetValue(owner) as GRContext;
        return context is not null && context.Handle != IntPtr.Zero &&
            !context.IsAbandoned && context.Backend == GRBackend.OpenGL;
    }

    internal static void MakeCurrent(object owner) => SelectContext!.Invoke(owner, null);
}
