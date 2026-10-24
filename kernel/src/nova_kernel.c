#include "nova_kernel.h"
#include "nova_util.h"
#include "nova_http.h"

static char g_last_error[512] = "";

const char* nova_version(void) { return nv_strdup(NOVA_KERNEL_VERSION); }
void nova_free_string(char* s) { free(s); }

const char* nova_last_error(void) { return g_last_error; }

static void set_last_error(const char* msg) {
    if (!msg) return;
    snprintf(g_last_error, sizeof(g_last_error), "%s", msg);
}

char* nova_http_get(const char* url, int timeout_ms, char** err) {
    char* r = nv_http_request("GET", url, NULL, NULL, NULL, timeout_ms, err);
    if (!r && err && *err) set_last_error(*err);
    return r;
}

char* nova_http_post(const char* url, const char* body, const char* content_type,
                     int timeout_ms, char** err) {
    char* r = nv_http_request("POST", url, body, content_type, NULL, timeout_ms, err);
    if (!r && err && *err) set_last_error(*err);
    return r;
}

char* nova_http_get_ex(const char* url, const char* extra_headers,
                       int timeout_ms, char** err) {
    char* r = nv_http_request("GET", url, NULL, NULL, extra_headers, timeout_ms, err);
    if (!r && err && *err) set_last_error(*err);
    return r;
}

char* nova_http_post_ex(const char* url, const char* body,
                        const char* content_type, const char* extra_headers,
                        int timeout_ms, char** err) {
    char* r = nv_http_request("POST", url, body, content_type, extra_headers, timeout_ms, err);
    if (!r && err && *err) set_last_error(*err);
    return r;
}
