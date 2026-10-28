using System;
using System.IO;
using System.Runtime.InteropServices;

namespace CleanBrowser {
    public static class TaskbarIdentity {
        public const string AppId = "CleanNetwork.Desktop";
        static readonly Guid PropertySet = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

        // Set before creating UI, so launches from another application cannot inherit its group.
        public static void ConfigureProcess() {
            Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(AppId));
        }

        public static void ConfigureWindow(IntPtr handle, string install, string title) {
            Guid iid = typeof(IPropertyStore).GUID;
            IPropertyStore store;
            Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(handle, ref iid, out store));
            try { Configure(store, install, title); }
            finally { Marshal.ReleaseComObject(store); }
        }

        public static void ConfigureShortcut(string path, string install, string title) {
            Guid iid = typeof(IPropertyStore).GUID;
            IPropertyStore store;
            Marshal.ThrowExceptionForHR(SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 2, ref iid, out store));
            try { Configure(store, install, title); store.Commit(); }
            finally { Marshal.ReleaseComObject(store); }
            SHChangeNotify(0x2000, 0x1005, path, IntPtr.Zero);
        }

        static void Configure(IPropertyStore store, string install, string title) {
            // Relaunch and pinning properties must precede AppUserModel.ID.
            Set(store, 9, new PropVariant { Type = 11 }); // VT_BOOL, false
            Set(store, 2, Text("\"" + Path.GetFullPath(Path.Combine(install, "CleanNetwork.exe")) + "\""));
            Set(store, 3, Text(Path.GetFullPath(Path.Combine(install, "assets", "app.ico")) + ",0"));
            Set(store, 4, Text(title));
            Set(store, 5, Text(AppId));
        }

        static PropVariant Text(string text) {
            return new PropVariant { Type = 31, Value = Marshal.StringToCoTaskMemUni(text) };
        }
        static void Set(IPropertyStore store, uint id, PropVariant value) {
            var key = new PropertyKey { Format = PropertySet, Id = id };
            try { store.SetValue(ref key, ref value); }
            finally { PropVariantClear(ref value); }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        internal struct PropertyKey { public Guid Format; public uint Id; }
        // Native PROPVARIANT is 16 bytes on x86 and 24 bytes on x64.
        [StructLayout(LayoutKind.Sequential)]
        internal struct PropVariant {
            public ushort Type, Reserved1, Reserved2, Reserved3;
            public IntPtr Value, Extra;
        }
        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IPropertyStore {
            void GetCount(out uint count);
            void GetAt(uint index, out PropertyKey key);
            void GetValue(ref PropertyKey key, out PropVariant value);
            void SetValue(ref PropertyKey key, ref PropVariant value);
            void Commit();
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
        [DllImport("shell32.dll")]
        internal static extern int SHGetPropertyStoreForWindow(IntPtr handle, ref Guid iid, out IPropertyStore store);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        internal static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr bind, uint flags, ref Guid iid, out IPropertyStore store);
        [DllImport("ole32.dll")]
        internal static extern int PropVariantClear(ref PropVariant value);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern void SHChangeNotify(uint change, uint flags, string item, IntPtr other);
    }
}
