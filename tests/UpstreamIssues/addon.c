#include <node_api.h>

// Deliberately leave napi_* unresolved, as Linux Node-API addons normally do.
static napi_value throw_type_error(napi_env env, napi_callback_info info) {
    napi_throw_type_error(env, "ISSUE509", "native type error");
    return NULL;
}

static napi_value initialize(napi_env env, napi_value exports) {
    napi_value answer;
    if (napi_create_int32(env, 42, &answer) != napi_ok) return NULL;
    if (napi_set_named_property(env, exports, "answer", answer) != napi_ok) return NULL;
    napi_value callback;
    if (napi_create_function(env, "throwTypeError", NAPI_AUTO_LENGTH, throw_type_error, NULL, &callback) != napi_ok) return NULL;
    if (napi_set_named_property(env, exports, "throwTypeError", callback) != napi_ok) return NULL;
    return exports;
}

NAPI_MODULE(issue_probe, initialize)
