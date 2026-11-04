using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

/// <summary>A real GLES 3 context for opt-in Linux shader and framebuffer checks.</summary>
internal sealed class HeadlessGlContext : IDisposable
{
    private const int None = 0x3038;
    private nint display;
    private nint surface;
    private nint context;
    private bool disposed;

    internal GL Gl { get; }
    internal string Description { get; }

    internal HeadlessGlContext(int width, int height)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The optional native GPU checks require Linux with libEGL.so.1.");
        try
        {
            display = Egl.GetPlatformDisplay(0x31DD, 0, 0); // EGL_PLATFORM_SURFACELESS_MESA
            Require(display != 0, "eglGetPlatformDisplay");
            Require(Egl.Initialize(display, out int major, out int minor), "eglInitialize");
            Require(Egl.BindApi(0x30A0), "eglBindAPI"); // EGL_OPENGL_ES_API
            int[] attributes =
            [
                0x3033, 1, // EGL_SURFACE_TYPE, EGL_PBUFFER_BIT
                0x3040, 0x40, // EGL_RENDERABLE_TYPE, EGL_OPENGL_ES3_BIT
                0x3024, 8, 0x3023, 8, 0x3022, 8, 0x3021, 8,
                0x3025, 24, 0x3026, 8, None
            ];
            Require(Egl.ChooseConfig(display, attributes, out nint config, 1, out int count) && count == 1,
                "eglChooseConfig");
            surface = Egl.CreatePbufferSurface(display, config, [0x3057, width, 0x3056, height, None]);
            Require(surface != 0, "eglCreatePbufferSurface");
            context = Egl.CreateContext(display, config, 0, [0x3098, 3, None]);
            Require(context != 0, "eglCreateContext");
            Require(Egl.MakeCurrent(display, surface, surface, context), "eglMakeCurrent");
            Gl = GL.GetApi(Egl.GetProcAddress);
            Description = $"EGL {major}.{minor}, {Gl.GetStringS(StringName.Version)}, " +
                $"{Gl.GetStringS(StringName.Vendor)}, {Gl.GetStringS(StringName.Renderer)}";
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Gl?.Dispose();
        if (display == 0) return;
        Egl.MakeCurrent(display, 0, 0, 0);
        if (context != 0) Egl.DestroyContext(display, context);
        if (surface != 0) Egl.DestroySurface(display, surface);
        Egl.Terminate(display);
        context = surface = display = 0;
    }

    private static void Require(bool success, string operation)
    {
        if (!success)
            throw new InvalidOperationException($"{operation} failed with EGL error 0x{Egl.GetError():X4}.");
    }

    private static class Egl
    {
        private const string Library = "libEGL.so.1";

        [DllImport(Library, EntryPoint = "eglGetPlatformDisplay")]
        internal static extern nint GetPlatformDisplay(uint platform, nint nativeDisplay, nint attributes);

        [DllImport(Library, EntryPoint = "eglInitialize")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Initialize(nint display, out int major, out int minor);

        [DllImport(Library, EntryPoint = "eglBindAPI")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool BindApi(uint api);

        [DllImport(Library, EntryPoint = "eglChooseConfig")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ChooseConfig(nint display, int[] attributes, out nint config, int size, out int count);

        [DllImport(Library, EntryPoint = "eglCreatePbufferSurface")]
        internal static extern nint CreatePbufferSurface(nint display, nint config, int[] attributes);

        [DllImport(Library, EntryPoint = "eglCreateContext")]
        internal static extern nint CreateContext(nint display, nint config, nint share, int[] attributes);

        [DllImport(Library, EntryPoint = "eglMakeCurrent")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool MakeCurrent(nint display, nint draw, nint read, nint context);

        [DllImport(Library, EntryPoint = "eglGetProcAddress", CharSet = CharSet.Ansi)]
        internal static extern nint GetProcAddress(string name);

        [DllImport(Library, EntryPoint = "eglGetError")]
        internal static extern int GetError();

        [DllImport(Library, EntryPoint = "eglDestroyContext")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyContext(nint display, nint context);

        [DllImport(Library, EntryPoint = "eglDestroySurface")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroySurface(nint display, nint surface);

        [DllImport(Library, EntryPoint = "eglTerminate")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Terminate(nint display);
    }
}
