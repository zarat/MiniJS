#pragma once
#include "Api.h"

#include <string>
#include <vector>
#include <functional>
#include <stdexcept>
#include <cstdint>
#include <cstring>

// ------------------------------------------------------------
// If your Api.h doesn't declare the extended API yet,
// these forward declarations are harmless (same signatures).
// ------------------------------------------------------------
extern "C" {
    MINIJS_API void* minijs_function_create_native(minijs_native_cb cb, void* userdata);
    MINIJS_API void   minijs_global_declare(void* it, const char* name, const minijs_value* v);

    MINIJS_API void* minijs_class_create(void* it, const char* name);
    MINIJS_API void   minijs_class_add_method(void* classHandle, const char* methodName, void* fnHandle);

    MINIJS_API void* minijs_array_create();
    MINIJS_API int32_t minijs_array_length(void* arrHandle);
    MINIJS_API void   minijs_array_get(void* arrHandle, int32_t index, minijs_value* out);
    MINIJS_API void   minijs_array_set(void* arrHandle, int32_t index, const minijs_value* v);
    MINIJS_API void   minijs_array_push(void* arrHandle, const minijs_value* v);

    MINIJS_API void* minijs_object_create();
    MINIJS_API int32_t minijs_object_has(void* objHandle, const char* key);
    MINIJS_API void   minijs_object_get(void* objHandle, const char* key, minijs_value* out);
    MINIJS_API void   minijs_object_set(void* objHandle, const char* key, const minijs_value* v);
    MINIJS_API char* minijs_object_keys(void* objHandle);
}

namespace minijspp {

    class Engine;
    class Object;
    class Array;
    class Function;
    class Class;

    class Value {
    public:
        enum class Kind : int32_t {
            Null = MINIJS_NULL,
            Number = MINIJS_NUMBER,
            Bool = MINIJS_BOOL,
            String = MINIJS_STRING,
            Array = MINIJS_ARRAY,
            Object = MINIJS_OBJECT,
            Function = MINIJS_FUNCTION,
            Class = MINIJS_CLASS,
            Task = MINIJS_TASK
        };

        Value() : _kind(Kind::Null), _num(0.0), _b(false), _h(nullptr) {}

        static Value Null() { return Value(); }
        static Value Number(double n) { Value v; v._kind = Kind::Number; v._num = n; return v; }
        static Value Bool(bool b) { Value v; v._kind = Kind::Bool; v._b = b; return v; }
        static Value String(std::string s) { Value v; v._kind = Kind::String; v._s = std::move(s); return v; }

        static Value Handle(Kind k, void* h, bool retain) {
            Value v;
            v._kind = k;
            v._h = h;
            if (retain && v._h) minijs_handle_retain(v._h);
            return v;
        }

        Kind kind() const { return _kind; }
        bool isHandleKind() const {
            return _kind == Kind::Array || _kind == Kind::Object || _kind == Kind::Function || _kind == Kind::Class || _kind == Kind::Task;
        }

        double toNumber(double def = 0.0) const {
            if (_kind == Kind::Number) return _num;
            if (_kind == Kind::Bool) return _b ? 1.0 : 0.0;
            return def;
        }

        bool toBool(bool def = false) const {
            if (_kind == Kind::Bool) return _b;
            if (_kind == Kind::Number) return _num != 0.0;
            return def;
        }

        const std::string& toStringRef() const { return _s; }

        void* handle() const { return _h; }

        // Transfer ownership to runtime (for "consumed handle" APIs)
        void* detachHandle() {
            void* h = _h;
            _h = nullptr;
            _kind = Kind::Null;
            _num = 0.0;
            _b = false;
            _s.clear();
            return h;
        }

        // Copy retains handle
        Value(const Value& o) : _kind(o._kind), _num(o._num), _b(o._b), _s(o._s), _h(o._h) {
            if (_h && isHandleKind()) minijs_handle_retain(_h);
        }

        Value& operator=(const Value& o) {
            if (this == &o) return *this;
            cleanup();
            _kind = o._kind;
            _num = o._num;
            _b = o._b;
            _s = o._s;
            _h = o._h;
            if (_h && isHandleKind()) minijs_handle_retain(_h);
            return *this;
        }

        // Move transfers handle
        Value(Value&& o) noexcept : _kind(o._kind), _num(o._num), _b(o._b), _s(std::move(o._s)), _h(o._h) {
            o._h = nullptr;
            o._kind = Kind::Null;
            o._num = 0.0;
            o._b = false;
        }

        Value& operator=(Value&& o) noexcept {
            if (this == &o) return *this;
            cleanup();
            _kind = o._kind;
            _num = o._num;
            _b = o._b;
            _s = std::move(o._s);
            _h = o._h;
            o._h = nullptr;
            o._kind = Kind::Null;
            o._num = 0.0;
            o._b = false;
            return *this;
        }

        ~Value() { cleanup(); }

        static Value fromNative(const minijs_value& nv, bool retainHandle) {
            Kind k = (Kind)nv.kind;
            switch (k) {
            case Kind::Null:   return Value::Null();
            case Kind::Number: return Value::Number(nv.num);
            case Kind::Bool:   return Value::Bool(nv.boolean != 0);
            case Kind::String: return Value::String(nv.str ? std::string(nv.str) : std::string());
            case Kind::Array:
            case Kind::Object:
            case Kind::Function:
            case Kind::Class:
            case Kind::Task:
                return Value::Handle(k, nv.handle, retainHandle);
            }
            return Value::Null();
        }

    private:
        void cleanup() {
            if (_h && isHandleKind()) {
                minijs_handle_release(_h);
            }
            _h = nullptr;
        }

        Kind _kind;
        double _num;
        bool _b;
        std::string _s;
        void* _h;
    };

    // ------------------------------------------------------------

    class Engine {
    public:
        using Callback = std::function<Value(const std::vector<Value>& args, const Value& thisVal)>;

        Engine() : _it(minijs_create()) {
            if (!_it) throw std::runtime_error("minijs_create() failed");
        }

        ~Engine() {
            for (Binding* b : _bindings) delete b;
            _bindings.clear();
            if (_it) {
                minijs_destroy(_it);
                _it = nullptr;
            }
        }

        Engine(const Engine&) = delete;
        Engine& operator=(const Engine&) = delete;

        void* raw() const { return _it; }

        std::string run(const std::string& code) {
            char* out = minijs_run(_it, code.c_str());
            if (!out) return std::string();
            std::string s(out);
            minijs_free(out);
            return s;
        }

        // ----------------------------
        // Register global native function: name(...)
        // ----------------------------
        void registerFunction(const std::string& name, Callback cb) {
            if (name.empty()) throw std::runtime_error("registerFunction: name empty");
            Binding* b = new Binding();
            b->engine = this;
            b->cb = std::move(cb);
            _bindings.push_back(b);
            minijs_register(_it, name.c_str(), &Engine::trampoline, b);
        }

        // ----------------------------
        // Create a Function handle (for class methods etc.)
        // ----------------------------
        Function createFunction(Callback cb);

        // ----------------------------
        // Create Class / Object / Array
        // ----------------------------
        Class createClass(const std::string& name);
        Object createObject();
        Array createArray();

        // ----------------------------
        // Declare value into global scope
        // - declareCopy keeps your Value alive
        // - declareMove transfers handle ownership to runtime
        // ----------------------------
        void declareCopy(const std::string& name, const Value& v) {
            minijs_value nv{};
            nv.kind = (int32_t)v.kind();
            nv.num = v.toNumber();
            nv.boolean = v.toBool() ? 1 : 0;
            nv.str = nullptr;
            nv.handle = nullptr;

            char* tmp = nullptr;
            nv = valueToNativeArg(v, &tmp);

            // For handles: global_declare consumes handle => pass a retained duplicate
            if (v.isHandleKind() && v.handle()) {
                // duplicate handle so runtime consumption doesn't kill caller's Value
                minijs_handle_retain(v.handle());
                nv.handle = v.handle();
            }

            minijs_global_declare(_it, name.c_str(), &nv);

            if (tmp) minijs_free(tmp);
            if (v.isHandleKind() && v.handle()) {
                // runtime consumed one retain; we added one retain; net: caller stays alive
            }
        }

        void declareMove(const std::string& name, Value&& v) {
            minijs_value nv{};
            char* tmp = nullptr;
            nv = valueToNativeArg(v, &tmp);

            // transfer handle ownership to runtime
            if (v.isHandleKind() && v.handle()) {
                nv.handle = v.detachHandle();
            }

            minijs_global_declare(_it, name.c_str(), &nv);
            if (tmp) minijs_free(tmp);
        }

        // ------------------------------------------------------------
        // IMPORTANT: this is PUBLIC (your original error was "private")
        // Used by Object/Array set/push.
        // If string needs temporary allocation, outTempStr is set.
        // ------------------------------------------------------------
        minijs_value valueToNativeArg(const Value& v, char** outTempStr) {
            if (outTempStr) *outTempStr = nullptr;

            minijs_value nv{};
            nv.kind = (int32_t)v.kind();
            nv.num = 0.0;
            nv.boolean = 0;
            nv.str = nullptr;
            nv.handle = nullptr;

            switch (v.kind()) {
            case Value::Kind::Null:
                nv.kind = MINIJS_NULL;
                return nv;

            case Value::Kind::Number:
                nv.kind = MINIJS_NUMBER;
                nv.num = v.toNumber();
                return nv;

            case Value::Kind::Bool:
                nv.kind = MINIJS_BOOL;
                nv.boolean = v.toBool() ? 1 : 0;
                return nv;

            case Value::Kind::String:
                nv.kind = MINIJS_STRING;
                nv.str = v.toStringRef().c_str();
                return nv;

            case Value::Kind::Array:
            case Value::Kind::Object:
            case Value::Kind::Function:
            case Value::Kind::Class:
            case Value::Kind::Task:
                nv.kind = (int32_t)v.kind();
                nv.handle = v.handle();
                return nv;
            }
            return nv;
        }

    private:
        struct Binding {
            Engine* engine;
            Callback cb;
        };

        static char* allocUtf8WithMinijsMalloc(const std::string& s) {
            void* mem = minijs_malloc(s.size() + 1);
            if (!mem) return nullptr;
            std::memcpy(mem, s.c_str(), s.size() + 1);
            return (char*)mem;
        }

        static minijs_value trampoline(int argc, const minijs_value* argv, const minijs_value* thisVal, void* userdata) {
            Binding* b = (Binding*)userdata;
            if (!b || !b->engine) {
                minijs_value r{};
                r.kind = MINIJS_NULL;
                return r;
            }

            try {
                std::vector<Value> args;
                args.reserve((size_t)argc);
                for (int i = 0; i < argc; i++) {
                    // retain handle args so our Value destructor can safely release
                    args.push_back(Value::fromNative(argv[i], /*retainHandle=*/true));
                }

                Value tv = Value::Null();
                if (thisVal) tv = Value::fromNative(*thisVal, /*retainHandle=*/true);

                Value ret = b->cb(args, tv);

                // Convert return:
                // - primitives: direct
                // - string: MUST be allocated via minijs_malloc (runtime frees)
                // - handles: CONSUMED by runtime => detach so we don't double-release
                minijs_value out{};
                out.kind = (int32_t)ret.kind();
                out.num = ret.toNumber();
                out.boolean = ret.toBool() ? 1 : 0;
                out.str = nullptr;
                out.handle = nullptr;

                if (ret.kind() == Value::Kind::String) {
                    std::string s = ret.toStringRef();
                    out.kind = MINIJS_STRING;
                    out.str = allocUtf8WithMinijsMalloc(s);
                    return out;
                }

                if (ret.isHandleKind() && ret.handle()) {
                    out.kind = (int32_t)ret.kind();
                    out.handle = ret.detachHandle(); // transfer ownership
                    return out;
                }

                // number/bool/null
                return out;
            }
            catch (const std::exception& e) {
                minijs_value out{};
                out.kind = MINIJS_STRING;
                std::string msg = std::string("Error: ") + e.what();
                out.str = allocUtf8WithMinijsMalloc(msg);
                return out;
            }
            catch (...) {
                minijs_value out{};
                out.kind = MINIJS_STRING;
                std::string msg = "Error: unknown native exception";
                out.str = allocUtf8WithMinijsMalloc(msg);
                return out;
            }
        }

        void* _it;
        std::vector<Binding*> _bindings;
    };

    // ------------------------------------------------------------

    class Object {
    public:
        Object() : _v(Value::Null()) {}
        explicit Object(Value v) : _v(std::move(v)) {
            if (_v.kind() != Value::Kind::Object) throw std::runtime_error("Object: Value is not an object");
        }

        void* handle() const { return _v.handle(); }

        bool has(const std::string& key) const {
            if (!handle()) return false;
            return minijs_object_has(handle(), key.c_str()) != 0;
        }

        Value get(const std::string& key) const {
            if (!handle()) return Value::Null();
            minijs_value out{};
            minijs_object_get(handle(), key.c_str(), &out);

            // object_get may return malloc'ed string => free after copy
            Value v = Value::fromNative(out, /*retainHandle=*/false);
            if ((Value::Kind)out.kind == Value::Kind::String && out.str) {
                minijs_free((void*)out.str);
            }
            return v;
        }

        void set(Engine& e, const std::string& key, const Value& v) {
            if (!handle()) throw std::runtime_error("Object::set on null handle");
            char* tmp = nullptr;
            minijs_value nv = e.valueToNativeArg(v, &tmp);
            minijs_object_set(handle(), key.c_str(), &nv);
            if (tmp) minijs_free(tmp);
        }

        std::vector<std::string> keys() const {
            std::vector<std::string> res;
            if (!handle()) return res;

            char* json = minijs_object_keys(handle());
            if (!json) return res;

            // Very small parser for ["a","b"] (enough for keys())
            // Not a general JSON parser.
            std::string s(json);
            minijs_free(json);

            size_t i = 0;
            auto skipws = [&]() {
                while (i < s.size() && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
                };

            skipws();
            if (i >= s.size() || s[i] != '[') return res;
            i++;
            skipws();

            while (i < s.size()) {
                skipws();
                if (i < s.size() && s[i] == ']') break;

                if (i >= s.size() || s[i] != '"') break;
                i++;

                std::string cur;
                while (i < s.size()) {
                    char c = s[i++];
                    if (c == '"') break;
                    if (c == '\\' && i < s.size()) {
                        char e = s[i++];
                        switch (e) {
                        case '\\': cur.push_back('\\'); break;
                        case '"':  cur.push_back('"'); break;
                        case 'n':  cur.push_back('\n'); break;
                        case 'r':  cur.push_back('\r'); break;
                        case 't':  cur.push_back('\t'); break;
                        default:   cur.push_back(e); break;
                        }
                    }
                    else {
                        cur.push_back(c);
                    }
                }
                res.push_back(cur);

                skipws();
                if (i < s.size() && s[i] == ',') { i++; continue; }
                skipws();
                if (i < s.size() && s[i] == ']') break;
            }
            return res;
        }

    private:
        Value _v;
    };

    // ------------------------------------------------------------

    class Array {
    public:
        Array() : _v(Value::Null()) {}
        explicit Array(Value v) : _v(std::move(v)) {
            if (_v.kind() != Value::Kind::Array) throw std::runtime_error("Array: Value is not an array");
        }

        void* handle() const { return _v.handle(); }

        int32_t length() const {
            if (!handle()) return 0;
            return minijs_array_length(handle());
        }

        Value get(int32_t index) const {
            if (!handle()) return Value::Null();
            minijs_value out{};
            minijs_array_get(handle(), index, &out);

            Value v = Value::fromNative(out, /*retainHandle=*/false);
            if ((Value::Kind)out.kind == Value::Kind::String && out.str) {
                minijs_free((void*)out.str);
            }
            return v;
        }

        void set(Engine& e, int32_t index, const Value& v) {
            if (!handle()) throw std::runtime_error("Array::set on null handle");
            char* tmp = nullptr;
            minijs_value nv = e.valueToNativeArg(v, &tmp);
            minijs_array_set(handle(), index, &nv);
            if (tmp) minijs_free(tmp);
        }

        void push(Engine& e, const Value& v) {
            if (!handle()) throw std::runtime_error("Array::push on null handle");
            char* tmp = nullptr;
            minijs_value nv = e.valueToNativeArg(v, &tmp);
            minijs_array_push(handle(), &nv);
            if (tmp) minijs_free(tmp);
        }

    private:
        Value _v;
    };

    // ------------------------------------------------------------

    class Function {
    public:
        Function() : _v(Value::Null()) {}
        explicit Function(Value v) : _v(std::move(v)) {
            if (_v.kind() != Value::Kind::Function) throw std::runtime_error("Function: Value is not a function");
        }

        void* handle() const { return _v.handle(); }

        // Transfer ownership to runtime (consumed)
        void* detachHandle() { return _v.detachHandle(); }

    private:
        Value _v;
    };

    class Class {
    public:
        Class() : _v(Value::Null()) {}
        explicit Class(Value v) : _v(std::move(v)) {
            if (_v.kind() != Value::Kind::Class) throw std::runtime_error("Class: Value is not a class");
        }

        void* handle() const { return _v.handle(); }

        // Add method: consumes fn handle (runtime will release it)
        void addMethod(const std::string& methodName, Function&& fn) {
            if (!handle()) throw std::runtime_error("Class::addMethod on null handle");
            void* h = fn.detachHandle();
            minijs_class_add_method(handle(), methodName.c_str(), h);
        }

        // Transfer ownership to runtime (consumed)
        Value toValueMove() { return std::move(_v); }

    private:
        Value _v;
    };

    // ------------------------------------------------------------
    // Engine helpers (need class definitions above)
    // ------------------------------------------------------------

    inline Function Engine::createFunction(Callback cb) {
        Binding* b = new Binding();
        b->engine = this;
        b->cb = std::move(cb);
        _bindings.push_back(b);

        void* h = minijs_function_create_native(&Engine::trampoline, b);
        if (!h) throw std::runtime_error("minijs_function_create_native failed");

        Value v = Value::Handle(Value::Kind::Function, h, /*retain=*/false);
        return Function(std::move(v));
    }

    inline Class Engine::createClass(const std::string& name) {
        void* h = minijs_class_create(_it, name.c_str());
        if (!h) throw std::runtime_error("minijs_class_create failed");
        Value v = Value::Handle(Value::Kind::Class, h, /*retain=*/false);
        return Class(std::move(v));
    }

    inline Object Engine::createObject() {
        void* h = minijs_object_create();
        if (!h) throw std::runtime_error("minijs_object_create failed");
        Value v = Value::Handle(Value::Kind::Object, h, /*retain=*/false);
        return Object(std::move(v));
    }

    inline Array Engine::createArray() {
        void* h = minijs_array_create();
        if (!h) throw std::runtime_error("minijs_array_create failed");
        Value v = Value::Handle(Value::Kind::Array, h, /*retain=*/false);
        return Array(std::move(v));
    }

} // namespace minijspp
