#pragma once
#include <stdint.h>
#include <stddef.h>

#if defined(_WIN32) && defined(MINIJS_BUILD_DLL)
#define MINIJS_API __declspec(dllexport)
#else
#define MINIJS_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

    // ----------------------------
    // malloc/free helpers
    // ----------------------------
    MINIJS_API void* minijs_malloc(size_t n);
    MINIJS_API void  minijs_free(void* p);

    // ----------------------------
    // Interpreter lifecycle (opaque handle)
    // ----------------------------
    MINIJS_API void* minijs_create();
    MINIJS_API void  minijs_destroy(void* it);

    // Runs code; returns newly allocated UTF-8 string of last value's toString().
    // Caller must free via minijs_free().
    MINIJS_API char* minijs_run(void* it, const char* code);

    // ----------------------------
    // Value transport (ABI-stable)
    // ----------------------------
    enum minijs_kind : int32_t {
        MINIJS_NULL = 0,
        MINIJS_NUMBER = 1,
        MINIJS_BOOL = 2,
        MINIJS_STRING = 3,
        MINIJS_ARRAY = 4,
        MINIJS_OBJECT = 5,
        MINIJS_FUNCTION = 6,
        MINIJS_CLASS = 7,
        MINIJS_TASK = 8
    };

#pragma pack(push, 8)
    typedef struct minijs_value {
        int32_t kind;       // minijs_kind
        double  num;        // number payload
        int32_t boolean;    // bool payload (0/1)
        const char* str;    // UTF-8 string payload
        void* handle;     // opaque handle for Array/Object/Function/Class/Task
    } minijs_value;
#pragma pack(pop)

    // ----------------------------
    // Handles (retain/release)
    // ----------------------------
    MINIJS_API void minijs_handle_retain(void* h);
    MINIJS_API void minijs_handle_release(void* h);

    // ----------------------------
    // Native callbacks
    // ----------------------------
    typedef minijs_value(*minijs_native_cb)(
        int argc,
        const minijs_value* argv,
        const minijs_value* thisVal,
        void* userdata
        );

    // Register native global function: name(...).
    MINIJS_API void  minijs_register(void* it, const char* name, minijs_native_cb cb, void* userdata);

    // Create native function as handle (for methods, storing in objects, etc.)
    MINIJS_API void* minijs_function_create_native(minijs_native_cb cb, void* userdata);

    // Declare any value into global scope.
    // - Consumes HANDLE kinds (releases handle after copying into runtime).
    // - Does NOT free strings (caller keeps ownership of v->str).
    MINIJS_API void  minijs_global_declare(void* it, const char* name, const minijs_value* v);

    // ----------------------------
    // Class API (register classes + methods)
    // ----------------------------
    MINIJS_API void* minijs_class_create(void* it, const char* name);
    // Adds/overwrites instance method. Use methodName="constructor" for ctor.
    // fnHandle is CONSUMED by this call.
    MINIJS_API void  minijs_class_add_method(void* classHandle, const char* methodName, void* fnHandle);

    // ----------------------------
    // Array API
    // ----------------------------
    MINIJS_API void* minijs_array_create();
    MINIJS_API int32_t minijs_array_length(void* arrHandle);
    MINIJS_API void    minijs_array_get(void* arrHandle, int32_t index, minijs_value* out); // out.str must be freed via minijs_free
    MINIJS_API void    minijs_array_set(void* arrHandle, int32_t index, const minijs_value* v);
    MINIJS_API void    minijs_array_push(void* arrHandle, const minijs_value* v);

    // ----------------------------
    // Object API
    // ----------------------------
    MINIJS_API void* minijs_object_create();
    MINIJS_API int32_t minijs_object_has(void* objHandle, const char* key);
    MINIJS_API void    minijs_object_get(void* objHandle, const char* key, minijs_value* out); // out.str must be freed via minijs_free
    MINIJS_API void    minijs_object_set(void* objHandle, const char* key, const minijs_value* v);

    // Returns JSON array string: ["a","b"] (free via minijs_free)
    MINIJS_API char* minijs_object_keys(void* objHandle);

#ifdef __cplusplus
}
#endif
