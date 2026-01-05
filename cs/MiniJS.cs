using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace MiniJsHost
{

    public sealed class MiniJs : IDisposable
    {
        private IntPtr _it;
        private bool _disposed;

        // Keep native delegates alive (otherwise: crash sooner or later)
        private readonly List<GCHandle> _pinnedDelegates;

        public MiniJs()
        {
            _pinnedDelegates = new List<GCHandle>();

            _it = Native.minijs_create();
            if (_it == IntPtr.Zero)
            {
                throw new InvalidOperationException("minijs_create() returned NULL.");
            }
        }

        public delegate JsValue Callback(JsValue[] args, JsValue thisVal);

        public string Run(string code)
        {
            EnsureNotDisposed();

            if (code == null) code = "";

            IntPtr p = Native.minijs_run(_it, code);
            if (p == IntPtr.Zero) return "";

            try
            {
                string s = Marshal.PtrToStringUTF8(p);
                if (s == null) s = "";
                return s;
            }
            finally
            {
                Native.minijs_free(p);
            }
        }

        public void Register(string name, Callback cb)
        {
            EnsureNotDisposed();

            if (string.IsNullOrEmpty(name)) throw new ArgumentException("name");
            if (cb == null) throw new ArgumentNullException("cb");

            Native.minijs_native_cb tramp = MakeTrampoline(cb);

            // Pin delegate so it never gets collected
            _pinnedDelegates.Add(GCHandle.Alloc(tramp));

            Native.minijs_register(_it, name, tramp, IntPtr.Zero);
        }

        public JsFunction CreateFunction(Callback cb)
        {
            EnsureNotDisposed();

            if (cb == null) throw new ArgumentNullException("cb");

            Native.minijs_native_cb tramp = MakeTrampoline(cb);
            _pinnedDelegates.Add(GCHandle.Alloc(tramp));

            IntPtr h = Native.minijs_function_create_native(tramp, IntPtr.Zero);
            if (h == IntPtr.Zero) throw new InvalidOperationException("minijs_function_create_native returned NULL.");

            return new JsFunction(this, h, true);
        }

        public JsClass CreateClass(string name)
        {
            EnsureNotDisposed();

            if (name == null) name = "";

            IntPtr h = Native.minijs_class_create(_it, name);
            if (h == IntPtr.Zero) throw new InvalidOperationException("minijs_class_create returned NULL.");

            return new JsClass(this, h, name, true);
        }

        public JsArray CreateArray()
        {
            EnsureNotDisposed();

            IntPtr h = Native.minijs_array_create();
            if (h == IntPtr.Zero) throw new InvalidOperationException("minijs_array_create returned NULL.");

            return new JsArray(this, h, true);
        }

        public JsObject CreateObject()
        {
            EnsureNotDisposed();

            IntPtr h = Native.minijs_object_create();
            if (h == IntPtr.Zero) throw new InvalidOperationException("minijs_object_create returned NULL.");

            return new JsObject(this, h, true);
        }

        // Declare primitive / handle value into global scope.
        // NOTE: Handle kinds (Array/Object/Function/Class) are CONSUMED by the runtime.
        public void Declare(string name, JsValue v)
        {
            EnsureNotDisposed();
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("name");

            Native.minijs_value nv = v.ToNativeAllocatedForReturn();

            try
            {
                Native.minijs_global_declare(_it, name, ref nv);
            }
            finally
            {
                // If we allocated a string for nv.str, free it (runtime copies it)
                if (nv.kind == (int)Kind.String && nv.str != IntPtr.Zero)
                {
                    Native.minijs_free(nv.str);
                }
            }
        }

        // Convenience overloads: declare and transfer ownership
        public void Declare(string name, JsArray arr)
        {
            if (arr == null) throw new ArgumentNullException("arr");
            JsValue v = arr.DetachValue(); // ownership -> runtime
            Declare(name, v);
        }

        public void Declare(string name, JsObject obj)
        {
            if (obj == null) throw new ArgumentNullException("obj");
            JsValue v = obj.DetachValue(); // ownership -> runtime
            Declare(name, v);
        }

        public void Declare(string name, JsFunction fn)
        {
            if (fn == null) throw new ArgumentNullException("fn");
            JsValue v = fn.DetachValue(); // ownership -> runtime
            Declare(name, v);
        }

        public void Declare(string name, JsClass cls)
        {
            if (cls == null) throw new ArgumentNullException("cls");
            JsValue v = cls.DetachValue(); // ownership -> runtime
            Declare(name, v);
        }

        internal void RetainHandle(IntPtr h)
        {
            if (h == IntPtr.Zero) return;
            Native.minijs_handle_retain(h);
        }

        internal void ReleaseHandle(IntPtr h)
        {
            if (h == IntPtr.Zero) return;
            Native.minijs_handle_release(h);
        }

        private Native.minijs_native_cb MakeTrampoline(Callback cb)
        {
            return delegate (int argc, IntPtr argv, IntPtr thisValPtr, IntPtr userdata)
            {
                JsValue[] args = new JsValue[argc];

                int size = Marshal.SizeOf(typeof(Native.minijs_value));
                int i = 0;
                while (i < argc)
                {
                    Native.minijs_value nv = (Native.minijs_value)Marshal.PtrToStructure(
                        IntPtr.Add(argv, i * size),
                        typeof(Native.minijs_value)
                    );

                    args[i] = JsValue.FromNative(nv);
                    i++;
                }

                JsValue thisVal = JsValue.Null();
                if (thisValPtr != IntPtr.Zero)
                {
                    Native.minijs_value nt = (Native.minijs_value)Marshal.PtrToStructure(
                        thisValPtr,
                        typeof(Native.minijs_value)
                    );
                    thisVal = JsValue.FromNative(nt);
                }

                JsValue ret;
                try
                {
                    ret = cb(args, thisVal);
                }
                catch (Exception ex)
                {
                    ret = JsValue.FromString("Error: " + ex.Message);
                }

                // Return value rules:
                // - String: allocate with minijs_malloc (runtime copies and frees)
                // - Handle kinds: runtime CONSUMES handle (release after copying)
                return ret.ToNativeAllocatedForReturn();
            };
        }

        private void EnsureNotDisposed()
        {
            if (_disposed || _it == IntPtr.Zero)
            {
                throw new ObjectDisposedException("MiniJs");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Free pinned delegates
            int i = 0;
            while (i < _pinnedDelegates.Count)
            {
                GCHandle h = _pinnedDelegates[i];
                if (h.IsAllocated) h.Free();
                i++;
            }
            _pinnedDelegates.Clear();

            if (_it != IntPtr.Zero)
            {
                Native.minijs_destroy(_it);
                _it = IntPtr.Zero;
            }
        }
    }

    public enum Kind
    {
        Null = 0,
        Number = 1,
        Bool = 2,
        String = 3,
        Array = 4,
        Object = 5,
        Function = 6,
        Class = 7
    }

    public struct JsValue
    {
        public Kind Type;
        public double Number;
        public bool Bool;
        public string String;
        public IntPtr Handle;

        public static JsValue Null()
        {
            JsValue v = new JsValue();
            v.Type = Kind.Null;
            v.Number = 0;
            v.Bool = false;
            v.String = null;
            v.Handle = IntPtr.Zero;
            return v;
        }

        public static JsValue FromNumber(double n)
        {
            JsValue v = new JsValue();
            v.Type = Kind.Number;
            v.Number = n;
            v.Bool = false;
            v.String = null;
            v.Handle = IntPtr.Zero;
            return v;
        }

        public static JsValue FromBool(bool b)
        {
            JsValue v = new JsValue();
            v.Type = Kind.Bool;
            v.Number = 0;
            v.Bool = b;
            v.String = null;
            v.Handle = IntPtr.Zero;
            return v;
        }

        public static JsValue FromString(string s)
        {
            JsValue v = new JsValue();
            v.Type = Kind.String;
            v.Number = 0;
            v.Bool = false;
            v.String = s;
            v.Handle = IntPtr.Zero;
            return v;
        }

        public static JsValue FromHandle(Kind kind, IntPtr handle)
        {
            JsValue v = new JsValue();
            v.Type = kind;
            v.Number = 0;
            v.Bool = false;
            v.String = null;
            v.Handle = handle;
            return v;
        }

        internal static JsValue FromNative(Native.minijs_value nv)
        {
            Kind k = (Kind)nv.kind;

            if (k == Kind.Null) return Null();
            if (k == Kind.Number) return FromNumber(nv.num);
            if (k == Kind.Bool) return FromBool(nv.boolean != 0);
            if (k == Kind.String)
            {
                string s = "";
                if (nv.str != IntPtr.Zero)
                {
                    s = Marshal.PtrToStringUTF8(nv.str);
                    if (s == null) s = "";
                }
                return FromString(s);
            }

            // Handle kinds
            return FromHandle(k, nv.handle);
        }

        // IMPORTANT:
        // This is used when returning from callbacks OR declaring into runtime.
        // - String is allocated via minijs_malloc; runtime copies and frees it.
        // - Handle kinds are returned as-is; runtime consumes (releases after copying).
        internal Native.minijs_value ToNativeAllocatedForReturn()
        {
            Native.minijs_value nv = new Native.minijs_value();
            nv.kind = (int)Type;
            nv.num = Number;
            nv.boolean = Bool ? 1 : 0;
            nv.str = IntPtr.Zero;
            nv.handle = Handle;

            if (Type == Kind.String)
            {
                string s = String;
                if (s == null) s = "";
                nv.str = Native.AllocUtf8WithMinijsMalloc(s);
            }

            return nv;
        }

        // If you want to return a borrowed handle (arg or this) back to JS:
        // call minijs_handle_retain first, otherwise runtime will consume and free it.
        public JsValue Retain(MiniJsHost.MiniJs js)
        {
            if (js == null) throw new ArgumentNullException("js");
            if (Handle != IntPtr.Zero)
            {
                js.RetainHandle(Handle);
            }
            return this;
        }
    }

    public sealed class JsArray : IDisposable
    {
        private readonly MiniJs _js;
        public IntPtr Handle;
        private bool _owns;

        internal JsArray(MiniJs js, IntPtr handle, bool owns)
        {
            _js = js;
            Handle = handle;
            _owns = owns;
        }

        public int Length
        {
            get
            {
                Ensure();
                return Native.minijs_array_length(Handle);
            }
        }

        public JsValue Get(int index)
        {
            Ensure();

            Native.minijs_value outv = new Native.minijs_value();
            Native.minijs_array_get(Handle, index, ref outv);

            JsValue v = JsValue.FromNative(outv);

            // array_get allocates string with malloc => free after reading
            if ((Kind)outv.kind == Kind.String && outv.str != IntPtr.Zero)
            {
                Native.minijs_free(outv.str);
            }

            return v;
        }

        public void Set(int index, JsValue v)
        {
            Ensure();

            Native.minijs_value nv = v.ToNativeAllocatedForReturn();
            try
            {
                Native.minijs_array_set(Handle, index, ref nv);
            }
            finally
            {
                if ((Kind)nv.kind == Kind.String && nv.str != IntPtr.Zero)
                {
                    Native.minijs_free(nv.str);
                }
            }
        }

        public void Push(JsValue v)
        {
            Ensure();

            Native.minijs_value nv = v.ToNativeAllocatedForReturn();
            try
            {
                Native.minijs_array_push(Handle, ref nv);
            }
            finally
            {
                if ((Kind)nv.kind == Kind.String && nv.str != IntPtr.Zero)
                {
                    Native.minijs_free(nv.str);
                }
            }
        }

        public IntPtr Detach()
        {
            IntPtr h = Handle;
            Handle = IntPtr.Zero;
            _owns = false;
            return h;
        }

        public JsValue DetachValue()
        {
            IntPtr h = Detach();
            return JsValue.FromHandle(Kind.Array, h);
        }

        private void Ensure()
        {
            if (Handle == IntPtr.Zero) throw new ObjectDisposedException("JsArray");
        }

        public void Dispose()
        {
            if (_owns && Handle != IntPtr.Zero)
            {
                _js.ReleaseHandle(Handle);
                Handle = IntPtr.Zero;
            }
            _owns = false;
        }
    }

    public sealed class JsObject : IDisposable
    {
        private readonly MiniJs _js;
        public IntPtr Handle;
        private bool _owns;

        internal JsObject(MiniJs js, IntPtr handle, bool owns)
        {
            _js = js;
            Handle = handle;
            _owns = owns;
        }

        public JsValue Get(string key)
        {
            Ensure();
            if (key == null) key = "";

            Native.minijs_value outv = new Native.minijs_value();
            Native.minijs_object_get(Handle, key, ref outv);

            JsValue v = JsValue.FromNative(outv);

            // object_get allocates string with malloc => free after reading
            if ((Kind)outv.kind == Kind.String && outv.str != IntPtr.Zero)
            {
                Native.minijs_free(outv.str);
            }

            return v;
        }

        public void Set(string key, JsValue v)
        {
            Ensure();
            if (key == null) key = "";

            Native.minijs_value nv = v.ToNativeAllocatedForReturn();
            try
            {
                Native.minijs_object_set(Handle, key, ref nv);
            }
            finally
            {
                if ((Kind)nv.kind == Kind.String && nv.str != IntPtr.Zero)
                {
                    Native.minijs_free(nv.str);
                }
            }
        }

        public string[] Keys()
        {
            Ensure();

            IntPtr p = Native.minijs_object_keys(Handle);
            if (p == IntPtr.Zero) return new string[0];

            try
            {
                string json = Marshal.PtrToStringUTF8(p);
                if (json == null) json = "[]";

                // cheap parser for ["a","b"]
                // If you want proper: use System.Text.Json in your app.
                return SimpleJsonArrayToStrings(json);
            }
            finally
            {
                Native.minijs_free(p);
            }
        }

        private static string[] SimpleJsonArrayToStrings(string s)
        {
            // minimal, not bulletproof; enough for ["a","b"]
            s = s.Trim();
            if (s.Length < 2) return new string[0];
            if (s[0] != '[' || s[s.Length - 1] != ']') return new string[0];

            List<string> list = new List<string>();
            int i = 1;
            while (i < s.Length - 1)
            {
                while (i < s.Length - 1 && (s[i] == ' ' || s[i] == ',')) i++;
                if (i >= s.Length - 1) break;

                if (s[i] != '"') break;
                i++;

                StringBuilder sb = new StringBuilder();
                while (i < s.Length - 1)
                {
                    char c = s[i];
                    if (c == '"') { i++; break; }
                    if (c == '\\')
                    {
                        i++;
                        if (i >= s.Length - 1) break;
                        char e = s[i];
                        if (e == 'n') sb.Append('\n');
                        else if (e == 'r') sb.Append('\r');
                        else if (e == 't') sb.Append('\t');
                        else sb.Append(e);
                        i++;
                        continue;
                    }
                    sb.Append(c);
                    i++;
                }

                list.Add(sb.ToString());
            }

            return list.ToArray();
        }

        public IntPtr Detach()
        {
            IntPtr h = Handle;
            Handle = IntPtr.Zero;
            _owns = false;
            return h;
        }

        public JsValue DetachValue()
        {
            IntPtr h = Detach();
            return JsValue.FromHandle(Kind.Object, h);
        }

        private void Ensure()
        {
            if (Handle == IntPtr.Zero) throw new ObjectDisposedException("JsObject");
        }

        public void Dispose()
        {
            if (_owns && Handle != IntPtr.Zero)
            {
                _js.ReleaseHandle(Handle);
                Handle = IntPtr.Zero;
            }
            _owns = false;
        }
    }

    public sealed class JsFunction : IDisposable
    {
        private readonly MiniJs _js;
        public IntPtr Handle;
        private bool _owns;

        internal JsFunction(MiniJs js, IntPtr handle, bool owns)
        {
            _js = js;
            Handle = handle;
            _owns = owns;
        }

        public IntPtr Detach()
        {
            IntPtr h = Handle;
            Handle = IntPtr.Zero;
            _owns = false;
            return h;
        }

        public JsValue DetachValue()
        {
            IntPtr h = Detach();
            return JsValue.FromHandle(Kind.Function, h);
        }

        public void Dispose()
        {
            if (_owns && Handle != IntPtr.Zero)
            {
                _js.ReleaseHandle(Handle);
                Handle = IntPtr.Zero;
            }
            _owns = false;
        }
    }

    public sealed class JsClass : IDisposable
    {
        private readonly MiniJs _js;
        public IntPtr Handle;
        public string Name;
        private bool _owns;

        internal JsClass(MiniJs js, IntPtr handle, string name, bool owns)
        {
            _js = js;
            Handle = handle;
            Name = name;
            _owns = owns;
        }

        public void AddMethod(string methodName, MiniJs.Callback cb)
        {
            Ensure();

            if (string.IsNullOrEmpty(methodName)) throw new ArgumentException("methodName");
            if (cb == null) throw new ArgumentNullException("cb");

            JsFunction fn = _js.CreateFunction(cb);
            IntPtr fnHandle = fn.Detach(); // class_add_method consumes handle
            fn.Dispose();

            Native.minijs_class_add_method(Handle, methodName, fnHandle);
        }

        // Convenience: publish class to JS globals under its Name
        public void DeclareToGlobals()
        {
            Ensure();
            if (string.IsNullOrEmpty(Name)) throw new InvalidOperationException("Class Name is empty.");

            _js.Declare(Name, this); // detaches & transfers ownership
        }

        public IntPtr Detach()
        {
            IntPtr h = Handle;
            Handle = IntPtr.Zero;
            _owns = false;
            return h;
        }

        public JsValue DetachValue()
        {
            IntPtr h = Detach();
            return JsValue.FromHandle(Kind.Class, h);
        }

        private void Ensure()
        {
            if (Handle == IntPtr.Zero) throw new ObjectDisposedException("JsClass");
        }

        public void Dispose()
        {
            if (_owns && Handle != IntPtr.Zero)
            {
                _js.ReleaseHandle(Handle);
                Handle = IntPtr.Zero;
            }
            _owns = false;
        }
    }

    internal static class Native
    {
        private const string DllName = "minijs.dll";

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        internal struct minijs_value
        {
            public int kind;
            public double num;
            public int boolean;
            public IntPtr str;
            public IntPtr handle;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate minijs_value minijs_native_cb(int argc, IntPtr argv, IntPtr thisVal, IntPtr userdata);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr minijs_create();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void minijs_destroy(IntPtr it);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr minijs_run(IntPtr it, [MarshalAs(UnmanagedType.LPUTF8Str)] string code);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr minijs_malloc(UIntPtr n);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void minijs_free(IntPtr p);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void minijs_register(IntPtr it, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, minijs_native_cb cb, IntPtr userdata);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr minijs_function_create_native(minijs_native_cb cb, IntPtr userdata);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void minijs_global_declare(IntPtr it, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, ref minijs_value v);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr minijs_class_create(IntPtr it, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void minijs_class_add_method(IntPtr classHandle, [MarshalAs(UnmanagedType.LPUTF8Str)] string methodName, IntPtr fnHandle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr minijs_array_create();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int minijs_array_length(IntPtr arrHandle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void minijs_array_get(IntPtr arrHandle, int index, ref minijs_value outv);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void minijs_array_set(IntPtr arrHandle, int index, ref minijs_value v);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void minijs_array_push(IntPtr arrHandle, ref minijs_value v);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr minijs_object_create();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void minijs_object_get(IntPtr objHandle, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, ref minijs_value outv);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void minijs_object_set(IntPtr objHandle, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, ref minijs_value v);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr minijs_object_keys(IntPtr objHandle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void minijs_handle_retain(IntPtr h);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void minijs_handle_release(IntPtr h);

        internal static IntPtr AllocUtf8WithMinijsMalloc(string s)
        {
            if (s == null) s = "";

            byte[] bytes = Encoding.UTF8.GetBytes(s);
            IntPtr mem = minijs_malloc((UIntPtr)(bytes.Length + 1));
            if (mem == IntPtr.Zero) return IntPtr.Zero;

            Marshal.Copy(bytes, 0, mem, bytes.Length);
            Marshal.WriteByte(mem, bytes.Length, 0);
            return mem;
        }

    }

}
