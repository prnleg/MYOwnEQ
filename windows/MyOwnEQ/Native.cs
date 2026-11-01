using System;
using System.Runtime.InteropServices;

namespace MyOwnEQ;

/// <summary>P/Invoke surface of core/ffi (see core/ffi/include/myowneq.h).</summary>
internal static class Native
{
    private const string Lib = "myowneq_ffi";

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr meq_core_new([MarshalAs(UnmanagedType.LPUTF8Str)] string? dataDir,
                                             [MarshalAs(UnmanagedType.LPUTF8Str)] string machineId);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void meq_core_free(IntPtr core);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr meq_core_command(IntPtr core, [MarshalAs(UnmanagedType.LPUTF8Str)] string json);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void meq_string_free(IntPtr s);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint meq_core_spectrum(IntPtr core, [Out] float[] output, uint n);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr meq_core_make_audio(IntPtr core, double sampleRate);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr meq_discover(uint timeoutMs);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void meq_audio_free(IntPtr audio);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern unsafe void meq_audio_analyze(IntPtr audio, float* buf, uint frames, uint channels);

    /// <summary>Copy a Rust-owned string and free it.</summary>
    public static string TakeString(IntPtr p)
    {
        if (p == IntPtr.Zero) return "";
        try { return Marshal.PtrToStringUTF8(p) ?? ""; }
        finally { meq_string_free(p); }
    }
}
