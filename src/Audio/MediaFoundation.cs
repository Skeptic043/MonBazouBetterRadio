// Adapted from skeptic043/Radio, revision 1aa3626c53ae2fb785d1bda7ef711382923a015e, src/Audio/MediaFoundation.cs.
// Copyright (c) 2026 skeptic043. MIT license; see licenses/SailwindRadio.txt.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace RadioEnhancements
{
    // Windows Media Foundation through P/Invoke and manual vtable calls. The Unity 2019.1 Mono
    // spike crashed on a [ComImport] interface out-parameter, so no runtime callable wrappers
    // exist here. Every pointer is released explicitly by its owner.
    internal static class MediaFoundation
    {
        private const int Version = 0x20070;
        private const int StartupNoSockets = 1;
        private const int MultiThreaded = 0;
        private const int EnumSync = 0x1, EnumLocal = 0x10, EnumSortAndFilter = 0x40;
        internal const int FirstAudioStream = unchecked((int)0xFFFFFFFD);
        internal const int AllStreams = unchecked((int)0xFFFFFFFE);
        internal const int MediaSource = unchecked((int)0xFFFFFFFF);
        internal const int ReaderError = 0x1, ReaderEndOfStream = 0x2, ReaderTypeChanged = 0x20;

        internal static readonly Guid MajorType = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        internal static readonly Guid Subtype = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        internal static readonly Guid Audio = new Guid("73647561-0000-0010-8000-00aa00389b71");
        internal static readonly Guid Float = new Guid("00000003-0000-0010-8000-00aa00389b71");
        internal static readonly Guid Aac = new Guid("00001610-0000-0010-8000-00aa00389b71");
        // The MP4 source reports Apple Lossless with the second identifier. Both are accepted.
        internal static readonly Guid AlacFourCc = new Guid("63616c61-0000-0010-8000-00aa00389b71");
        internal static readonly Guid Alac = new Guid("616c6163-767a-494d-b478-f29d25dc9037");
        internal static readonly Guid Channels = new Guid("37e48bf5-645e-4c5b-89de-ada9e29b696a");
        internal static readonly Guid SamplesPerSecond = new Guid("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
        internal static readonly Guid ChannelMask = new Guid("55fb5765-644a-4caf-8479-938983bb1588");
        internal static readonly Guid Duration = new Guid("6c990d33-bb8e-477a-8598-0d5d96fcd88a");
        internal static readonly Guid Protected = new Guid("00af2181-bdc2-423c-abca-f503593bc121");
        internal static readonly Guid AudioDecoders = new Guid("9ea73fb4-ef7a-4559-8d5d-719d8f0426c7");

        [DllImport("mfplat.dll", ExactSpelling = true)] internal static extern int MFStartup(int version, int flags);
        [DllImport("mfplat.dll", ExactSpelling = true)] internal static extern int MFShutdown();
        [DllImport("mfplat.dll", ExactSpelling = true)] internal static extern int MFCreateMediaType(out IntPtr type);
        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFTEnumEx(Guid category, int flags, IntPtr input, IntPtr output, out IntPtr activates, out int count);
        [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        internal static extern int MFCreateSourceReaderFromURL(string url, IntPtr attributes, out IntPtr reader);
        [DllImport("ole32.dll", ExactSpelling = true)] private static extern int CoInitializeEx(IntPtr reserved, int flags);
        [DllImport("ole32.dll", ExactSpelling = true)] private static extern void CoUninitialize();
        [DllImport("ole32.dll", ExactSpelling = true)] private static extern void CoTaskMemFree(IntPtr memory);
        [DllImport("ole32.dll", ExactSpelling = true)] internal static extern int PropVariantClear(IntPtr variant);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)] private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int ReleaseMethod(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SetStreamSelectionMethod(IntPtr self, int stream, int selected);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int GetNativeMediaTypeMethod(IntPtr self, int stream, int index, out IntPtr type);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int GetCurrentMediaTypeMethod(IntPtr self, int stream, out IntPtr type);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SetCurrentMediaTypeMethod(IntPtr self, int stream, IntPtr reserved, IntPtr type);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SetCurrentPositionMethod(IntPtr self, ref Guid format, IntPtr position);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int ReadSampleMethod(IntPtr self, int stream, int control, out int actual, out int flags, out long time, out IntPtr sample);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int GetPresentationAttributeMethod(IntPtr self, int stream, ref Guid key, IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int GetUInt32Method(IntPtr self, ref Guid key, out int value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int GetGuidMethod(IntPtr self, ref Guid key, out Guid value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SetGuidMethod(IntPtr self, ref Guid key, ref Guid value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int GetPointerMethod(IntPtr self, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int LockMethod(IntPtr self, out IntPtr data, out int maximum, out int current);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int UnlockMethod(IntPtr self);

        // Vtable slots. IUnknown owns 0 to 2 and IMFAttributes 3 to 32.
        internal const int SlotRelease = 2, SlotGetUInt32 = 7, SlotGetGuid = 10, SlotSetGuid = 24;
        internal const int SlotSetStreamSelection = 4, SlotGetNativeMediaType = 5, SlotGetCurrentMediaType = 6,
            SlotSetCurrentMediaType = 7, SlotSetCurrentPosition = 8, SlotReadSample = 9, SlotGetPresentationAttribute = 12;
        internal const int SlotConvertToContiguousBuffer = 41, SlotLock = 3, SlotUnlock = 4;

        private static class Delegates<T> where T : class
        {
            internal static readonly Dictionary<IntPtr, T> ByFunction = new Dictionary<IntPtr, T>();
        }

        internal static T Method<T>(IntPtr self, int slot) where T : class
        {
            IntPtr function = Marshal.ReadIntPtr(Marshal.ReadIntPtr(self), slot * IntPtr.Size);
            lock (Delegates<T>.ByFunction)
            {
                T method;
                if (!Delegates<T>.ByFunction.TryGetValue(function, out method))
                {
                    method = (T)(object)Marshal.GetDelegateForFunctionPointer(function, typeof(T));
                    Delegates<T>.ByFunction.Add(function, method);
                }
                return method;
            }
        }

        internal static void Release(ref IntPtr pointer)
        {
            IntPtr owned = pointer;
            pointer = IntPtr.Zero;
            if (owned != IntPtr.Zero) Method<ReleaseMethod>(owned, SlotRelease)(owned);
        }

        internal static int GetUInt32(IntPtr attributes, Guid key, int fallback)
        {
            int value;
            return Method<GetUInt32Method>(attributes, SlotGetUInt32)(attributes, ref key, out value) >= 0 ? value : fallback;
        }

        internal static bool GetGuid(IntPtr attributes, Guid key, out Guid value)
        {
            return Method<GetGuidMethod>(attributes, SlotGetGuid)(attributes, ref key, out value) >= 0;
        }

        // PROPVARIANT is 16 bytes on x86 and 24 on x64. The value union starts at offset 8 on both.
        internal static IntPtr NewVariant()
        {
            IntPtr variant = Marshal.AllocHGlobal(32);
            for (int i = 0; i < 32; i += 8) Marshal.WriteInt64(variant, i, 0);
            return variant;
        }

        // Joins the multithreaded apartment for one call. A single-threaded caller such as the
        // Unity main thread gets RPC_E_CHANGED_MODE and keeps its apartment, which MF also accepts.
        internal sealed class Apartment : IDisposable
        {
            private bool joined;
            internal Apartment() { joined = CoInitializeEx(IntPtr.Zero, MultiThreaded) >= 0; }
            public void Dispose()
            {
                if (!joined) return;
                joined = false;
                CoUninitialize();
            }
        }

        internal static bool WineDetected()
        {
            IntPtr ntdll = GetModuleHandle("ntdll.dll");
            return ntdll != IntPtr.Zero && GetProcAddress(ntdll, "wine_get_version") != IntPtr.Zero;
        }

        // True when Media Foundation starts and lists at least one local AAC decoder.
        internal static bool AacDecoderAvailable()
        {
            using (new Apartment())
            {
                if (MFStartup(Version, StartupNoSockets) < 0) return false;
                try { return DecoderCount(Aac) > 0; }
                finally { MFShutdown(); }
            }
        }

        internal static int DecoderCount(Guid subtype)
        {
            IntPtr info = Marshal.AllocHGlobal(32);
            try
            {
                Marshal.StructureToPtr(Audio, info, false);
                Marshal.StructureToPtr(subtype, new IntPtr(info.ToInt64() + 16), false);
                IntPtr list;
                int count;
                if (MFTEnumEx(AudioDecoders, EnumSync | EnumLocal | EnumSortAndFilter, info, IntPtr.Zero, out list, out count) < 0) return 0;
                for (int i = 0; i < count; i++)
                {
                    IntPtr activate = Marshal.ReadIntPtr(list, i * IntPtr.Size);
                    Release(ref activate);
                }
                if (list != IntPtr.Zero) CoTaskMemFree(list);
                return count;
            }
            finally { Marshal.FreeHGlobal(info); }
        }

        internal static void Startup()
        {
            int result = MFStartup(Version, StartupNoSockets);
            if (result < 0) throw new System.IO.IOException("Media Foundation could not start (error 0x" + result.ToString("X8") + ")");
        }
    }

    // One start-up decision for M4A and AAC. Under Wine or Proton Media Foundation is never
    // called, and no setting overrides that.
    internal static class MediaFoundationSupport
    {
        internal const string WineReason = "Proton or Wine detected";
        internal const string UnavailableReason = "Media Foundation is unavailable on this system";
        private static volatile bool enabled;
        private static volatile bool detected;
        private static readonly object detection = new object();
        private static volatile string reason = UnavailableReason;

        internal static bool Enabled { get { return enabled; } }
        internal static bool Detected { get { return detected; } }
        internal static string Reason { get { return enabled ? "" : reason; } }
        internal static string Summary
        {
            get { return enabled ? "AAC, M4A and MP4 audio playback is enabled through Media Foundation" : "AAC, M4A and MP4 audio playback is disabled. " + reason; }
        }

        // Probes run in order. Any probe exception disables the formats.
        internal static bool Decide(Func<bool> wine, Func<bool> aacDecoder, out string why)
        {
            why = UnavailableReason;
            try
            {
                if (wine()) { why = WineReason; return false; }
                if (!aacDecoder()) return false;
                why = "";
                return true;
            }
            catch (Exception) { return false; }
        }

        internal static void Detect() { Detect(MediaFoundation.WineDetected, MediaFoundation.AacDecoderAvailable); }

        // Called by the library scan worker before extension filtering, never by a Unity callback.
        internal static void EnsureDetected()
        {
            lock (detection)
            {
                if (detected) return;
                Detect();
                detected = true;
            }
        }

        internal static void Detect(Func<bool> wine, Func<bool> aacDecoder)
        {
            string why;
            bool decided = Decide(wine, aacDecoder, out why);
            reason = why;
            enabled = decided;
        }
    }
}
