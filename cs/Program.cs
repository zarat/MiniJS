using System;
using System.IO;
using MiniJsHost;

internal static class Program
{

    private static JsValue HostAdd(JsValue[] args, JsValue thisVal)
    {
        double a = 0;
        double b = 0;

        if (args.Length > 0 && args[0].Type == Kind.Number) a = args[0].Number;
        if (args.Length > 1 && args[1].Type == Kind.Number) b = args[1].Number;

        return JsValue.FromNumber(a + b);
    }

    private static JsValue CounterCtor(JsValue[] args, JsValue thisVal)
    {
        // thisVal is an Object handle when called as constructor/method
        if (thisVal.Type != Kind.Object || thisVal.Handle == IntPtr.Zero)
            return JsValue.Null();

        double v = 0;
        if (args.Length > 0 && args[0].Type == Kind.Number) v = args[0].Number;

        // Wrap borrowed this-handle into JsObject (retain because wrapper owns)
        MiniJs js = _js;
        js.RetainHandle(thisVal.Handle);
        using (JsObject obj = new JsObject(js, thisVal.Handle, true))
        {
            obj.Set("x", JsValue.FromNumber(v));
        }

        return JsValue.Null();
    }

    private static JsValue CounterInc(JsValue[] args, JsValue thisVal)
    {
        if (thisVal.Type != Kind.Object || thisVal.Handle == IntPtr.Zero)
            return JsValue.Null();

        MiniJs js = _js;
        js.RetainHandle(thisVal.Handle);
        using (JsObject obj = new JsObject(js, thisVal.Handle, true))
        {
            JsValue cur = obj.Get("x");
            double x = 0;
            if (cur.Type == Kind.Number) x = cur.Number;
            x = x + 1;
            obj.Set("x", JsValue.FromNumber(x));
            return JsValue.FromNumber(x);
        }
    }

    private static MiniJs _js;

    public static int Main(string[] args)
    {

        if (args.Length < 1)
        {
            Console.WriteLine("Usage: MiniJS_Demo.exe <scriptfile>");
            return 1;
        }

        _js = new MiniJs();


        // 1) register global function
        _js.Register("hostAdd", HostAdd);

        // 2) create + register class
        using (JsClass counter = _js.CreateClass("Counter"))
        {
            counter.AddMethod("constructor", CounterCtor);
            counter.AddMethod("inc", CounterInc);
            counter.DeclareToGlobals(); // puts Counter into JS globals
        }

        // 3) show object/array usage (declare them into JS)
        using (JsArray arr = _js.CreateArray())
        {
            arr.Push(JsValue.FromNumber(1));
            arr.Push(JsValue.FromNumber(2));
            arr.Push(JsValue.FromString("hi"));
            _js.Declare("hostArr", arr); // transfers ownership to JS runtime
        }

        using (JsObject obj = _js.CreateObject())
        {
            obj.Set("a", JsValue.FromNumber(123));
            obj.Set("b", JsValue.FromString("text"));
            _js.Declare("hostObj", obj); // transfers ownership to JS runtime
        }

        string code = File.ReadAllText(args[0]);
        string last = _js.Run(code);
        Console.WriteLine("minijs_run returned: " + last);


        return 0;

    }

}
