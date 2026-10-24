#include "nova_http.h"
#include "nova_util.h"

#ifdef _WIN32
/* ================= Windows: WinHTTP ================= */
#include <windows.h>
#include <winhttp.h>
#pragma comment(lib, "winhttp.lib")

static int g_http_init = 0;
static HINTERNET g_session = NULL;

static int http_ensure_init(char** err) {
    if (g_http_init) return 0;
    g_session = WinHttpOpen(L"NovaLauncher/1.0", WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY,
                            WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
    if (!g_session) { if (err) *err = nv_strdup("WinHttpOpen failed"); return -1; }
    g_http_init = 1;
    return 0;
}

/* url: https://host:port/path?query ; 拆分 host/path，默认 443 */
static int parse_url(const char* url, wchar_t** host_out, int* port_out, wchar_t** path_out, int* is_https) {
    const char* p = url;
    *is_https = 0;
    if (nv_starts_with(p, "https://")) { *is_https = 1; p += 8; }
    else if (nv_starts_with(p, "http://")) { p += 7; }
    else return -1;
    const char* slash = strchr(p, '/');
    const char* host_end = slash ? slash : p + strlen(p);
    const char* colon = memchr(p, ':', (size_t)(host_end - p));
    int port = *is_https ? 443 : 80;
    char hostbuf[512];
    if (colon) {
        size_t hl = (size_t)(colon - p);
        if (hl >= sizeof(hostbuf)) return -1;
        memcpy(hostbuf, p, hl); hostbuf[hl] = 0;
        port = atoi(colon + 1);
        if (port <= 0) return -1;
    } else {
        size_t hl = (size_t)(host_end - p);
        if (hl >= sizeof(hostbuf)) return -1;
        memcpy(hostbuf, p, hl); hostbuf[hl] = 0;
    }
    /* host 可能是 [ipv6]，简化处理：去掉方括号 */
    size_t hl = strlen(hostbuf);
    if (hl > 2 && hostbuf[0] == '[' && hostbuf[hl-1] == ']') {
        memmove(hostbuf, hostbuf + 1, hl - 2);
        hostbuf[hl-2] = 0;
    }
    const char* path = slash ? slash : "/";
    int n = MultiByteToWideChar(CP_UTF8, 0, hostbuf, -1, NULL, 0);
    int m = MultiByteToWideChar(CP_UTF8, 0, path, -1, NULL, 0);
    if (n <= 0 || m <= 0) return -1;
    *host_out = (wchar_t*)nv_xmalloc((size_t)n * sizeof(wchar_t));
    *path_out = (wchar_t*)nv_xmalloc((size_t)m * sizeof(wchar_t));
    MultiByteToWideChar(CP_UTF8, 0, hostbuf, -1, *host_out, n);
    MultiByteToWideChar(CP_UTF8, 0, path, -1, *path_out, m);
    *port_out = port;
    return 0;
}

typedef struct { nv_buf* buf; } write_ctx;

static int http_do(const char* method, const char* url, const char* body,
                   const char* content_type, const char* extra_headers,
                   int timeout_ms, int64_t range_from, FILE* outfile,
                   nv_buf* outbuf, int* out_status, char** err) {
    if (http_ensure_init(err)) return -1;
    wchar_t* host = NULL; wchar_t* path = NULL; int port = 80, is_https = 0;
    if (parse_url(url, &host, &port, &path, &is_https)) {
        if (err) *err = nv_strdup("bad url");
        return -1;
    }
    HINTERNET conn = WinHttpConnect(g_session, host, (INTERNET_PORT)port, 0);
    if (!conn) { free(host); free(path); if (err) *err = nv_strdup("WinHttpConnect failed"); return -1; }
    const wchar_t* verb = (method && *method == 'P') ? L"POST" : L"GET";
    HINTERNET req = WinHttpOpenRequest(conn, verb, path, NULL, WINHTTP_NO_REFERER,
                                       WINHTTP_DEFAULT_ACCEPT_TYPES,
                                       is_https ? WINHTTP_FLAG_SECURE : 0);
    if (!req) { WinHttpCloseHandle(conn); free(host); free(path); if (err) *err = nv_strdup("WinHttpOpenRequest failed"); return -1; }
    DWORD policy = WINHTTP_OPTION_REDIRECT_POLICY_ALWAYS;
    WinHttpSetOption(req, WINHTTP_OPTION_REDIRECT_POLICY, &policy, sizeof(policy));
    DWORD timeout = timeout_ms > 0 ? (DWORD)timeout_ms : 30000;
    WinHttpSetTimeouts(req, timeout, timeout, timeout, timeout);

    /* 组装附加头 */
    nv_buf hdrs; nv_buf_init(&hdrs);
    if (content_type) { nv_buf_append(&hdrs, "Content-Type: "); nv_buf_append(&hdrs, content_type); nv_buf_append(&hdrs, "\r\n"); }
    if (extra_headers) nv_buf_append(&hdrs, extra_headers);
    if (range_from >= 0) {
        nv_buf_append(&hdrs, "Range: bytes=");
        nv_buf_append_int(&hdrs, (long long)range_from);
        nv_buf_append(&hdrs, "-\r\n");
    }
    wchar_t* hdrw = NULL;
    if (hdrs.len) {
        int n = MultiByteToWideChar(CP_UTF8, 0, hdrs.data, -1, NULL, 0);
        hdrw = (wchar_t*)nv_xmalloc((size_t)n * sizeof(wchar_t));
        MultiByteToWideChar(CP_UTF8, 0, hdrs.data, -1, hdrw, n);
    }
    BOOL ok = WinHttpSendRequest(req, hdrw, hdrw ? -1 : 0,
                                 (LPVOID)body, body ? (DWORD)strlen(body) : 0,
                                 body ? (DWORD)strlen(body) : 0, 0);
    if (!ok) {
        WinHttpCloseHandle(req); WinHttpCloseHandle(conn);
        free(host); free(path); free(hdrw); nv_buf_free(&hdrs);
        if (err) *err = nv_strdup("WinHttpSendRequest failed");
        return -1;
    }
    if (!WinHttpReceiveResponse(req, NULL)) {
        WinHttpCloseHandle(req); WinHttpCloseHandle(conn);
        free(host); free(path); free(hdrw); nv_buf_free(&hdrs);
        if (err) *err = nv_strdup("WinHttpReceiveResponse failed");
        return -1;
    }
    DWORD status = 0; DWORD sz = sizeof(status);
    WinHttpQueryHeaders(req, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
                        WINHTTP_HEADER_NAME_BY_INDEX, &status, &sz, WINHTTP_NO_HEADER_INDEX);
    if (out_status) *out_status = (int)status;
    int64_t total = 0;
    DWORD avail = 0;
    unsigned char buf[65536];
    BOOL keep = TRUE;
    while (keep && WinHttpQueryDataAvailable(req, &avail) && avail > 0) {
        DWORD rd = 0;
        if (!WinHttpReadData(req, buf, avail < sizeof(buf) ? avail : (DWORD)sizeof(buf), &rd)) break;
        if (rd > 0) {
            if (outfile) {
                if (fwrite(buf, 1, rd, outfile) != rd) { keep = FALSE; break; }
            } else if (outbuf) {
                nv_buf_append_n(outbuf, (const char*)buf, rd);
            }
            total += rd;
        }
        if (avail == 0) break;
    }
    (void)total;
    WinHttpCloseHandle(req); WinHttpCloseHandle(conn);
    free(host); free(path); free(hdrw); nv_buf_free(&hdrs);
    return 0;
}

char* nv_http_request(const char* method, const char* url, const char* body,
                      const char* content_type, const char* extra_headers,
                      int timeout_ms, char** err) {
    nv_buf out; nv_buf_init(&out);
    int status = 0;
    if (http_do(method, url, body, content_type, extra_headers, timeout_ms, -1,
                NULL, &out, &status, err)) { nv_buf_free(&out); return NULL; }
    if (status >= 400) {
        if (err) { nv_buf e; nv_buf_init(&e); nv_buf_append(&e, "HTTP "); nv_buf_append_int(&e, status); *err = nv_buf_detach(&e); }
        nv_buf_free(&out);
        return NULL;
    }
    return nv_buf_detach(&out);
}

int nv_http_download_to_file(const char* url, const char* dest,
                             int64_t range_from, const char* extra_headers,
                             int timeout_ms, int64_t* out_bytes, char** err) {
    /* 打开文件：range_from>0 追加模式 */
    FILE* f = fopen(dest, range_from > 0 ? "ab" : "wb");
    if (!f) { if (err) *err = nv_strdup("cannot open dest"); return -1; }
    int status = 0;
    int rc = http_do("GET", url, NULL, NULL, extra_headers, timeout_ms, range_from, f, NULL, &status, err);
    int64_t sz = nv_file_size(dest);
    fclose(f);
    if (out_bytes) *out_bytes = sz > 0 ? sz : 0;
    if (rc != 0) return -1;
    if (status == 404 || status == 403) return status;
    if (status >= 400) return status;
    return 200;
}

#else
/* ================= macOS/Linux: libcurl ================= */
#include <curl/curl.h>

typedef struct { nv_buf* buf; } curl_buf_ctx;
static size_t curl_write_buf(char* ptr, size_t size, size_t nmemb, void* userdata) {
    curl_buf_ctx* ctx = (curl_buf_ctx*)userdata;
    size_t n = size * nmemb;
    nv_buf_append_n(ctx->buf, ptr, n);
    return n;
}
typedef struct { FILE* f; } curl_file_ctx;
static size_t curl_write_file(char* ptr, size_t size, size_t nmemb, void* userdata) {
    curl_file_ctx* ctx = (curl_file_ctx*)userdata;
    return fwrite(ptr, size, nmemb, ctx->f);
}

static int curl_global_done = 0;

char* nv_http_request(const char* method, const char* url, const char* body,
                      const char* content_type, const char* extra_headers,
                      int timeout_ms, char** err) {
    if (!curl_global_done) { curl_global_init(CURL_GLOBAL_DEFAULT); curl_global_done = 1; }
    CURL* c = curl_easy_init();
    if (!c) { if (err) *err = nv_strdup("curl init failed"); return NULL; }
    nv_buf out; nv_buf_init(&out);
    curl_buf_ctx ctx = { &out };
    curl_easy_setopt(c, CURLOPT_URL, url);
    curl_easy_setopt(c, CURLOPT_WRITEFUNCTION, curl_write_buf);
    curl_easy_setopt(c, CURLOPT_WRITEDATA, &ctx);
    curl_easy_setopt(c, CURLOPT_FOLLOWLOCATION, 1L);
    curl_easy_setopt(c, CURLOPT_MAXREDIRS, 8L);
    curl_easy_setopt(c, CURLOPT_USERAGENT, "NovaLauncher/1.0");
    curl_easy_setopt(c, CURLOPT_TIMEOUT_MS, (long)(timeout_ms > 0 ? timeout_ms : 30000));
    curl_easy_setopt(c, CURLOPT_CONNECTTIMEOUT_MS, 15000L);
    curl_easy_setopt(c, CURLOPT_SSL_VERIFYPEER, 1L);
    curl_easy_setopt(c, CURLOPT_SSL_VERIFYHOST, 2L);
    curl_easy_setopt(c, CURLOPT_ACCEPT_ENCODING, "");
    if (method && *method == 'P') {
        curl_easy_setopt(c, CURLOPT_POST, 1L);
        curl_easy_setopt(c, CURLOPT_POSTFIELDS, body ? body : "");
    }
    struct curl_slist* headers = NULL;
    if (content_type) headers = curl_slist_append(headers, content_type);
    if (extra_headers) headers = curl_slist_append(headers, extra_headers);
    if (headers) curl_easy_setopt(c, CURLOPT_HTTPHEADER, headers);
    CURLcode rc = curl_easy_perform(c);
    if (rc != CURLE_OK) {
        if (err) *err = nv_strdup(curl_easy_strerror(rc));
        if (headers) curl_slist_free_all(headers);
        curl_easy_cleanup(c);
        nv_buf_free(&out);
        return NULL;
    }
    long status = 0;
    curl_easy_getinfo(c, CURLINFO_RESPONSE_CODE, &status);
    if (headers) curl_slist_free_all(headers);
    curl_easy_cleanup(c);
    if (status >= 400) {
        if (err) { nv_buf e; nv_buf_init(&e); nv_buf_append(&e, "HTTP "); nv_buf_append_int(&e, status); *err = nv_buf_detach(&e); }
        nv_buf_free(&out);
        return NULL;
    }
    return nv_buf_detach(&out);
}

int nv_http_download_to_file(const char* url, const char* dest,
                             int64_t range_from, const char* extra_headers,
                             int timeout_ms, int64_t* out_bytes, char** err) {
    if (!curl_global_done) { curl_global_init(CURL_GLOBAL_DEFAULT); curl_global_done = 1; }
    FILE* f = fopen(dest, range_from > 0 ? "ab" : "wb");
    if (!f) { if (err) *err = nv_strdup("cannot open dest"); return -1; }
    CURL* c = curl_easy_init();
    if (!c) { fclose(f); if (err) *err = nv_strdup("curl init failed"); return -1; }
    curl_file_ctx ctx = { f };
    curl_easy_setopt(c, CURLOPT_URL, url);
    curl_easy_setopt(c, CURLOPT_WRITEFUNCTION, curl_write_file);
    curl_easy_setopt(c, CURLOPT_WRITEDATA, &ctx);
    curl_easy_setopt(c, CURLOPT_FOLLOWLOCATION, 1L);
    curl_easy_setopt(c, CURLOPT_MAXREDIRS, 8L);
    curl_easy_setopt(c, CURLOPT_USERAGENT, "NovaLauncher/1.0");
    curl_easy_setopt(c, CURLOPT_TIMEOUT_MS, (long)(timeout_ms > 0 ? timeout_ms : 60000));
    curl_easy_setopt(c, CURLOPT_CONNECTTIMEOUT_MS, 15000L);
    struct curl_slist* hdrs = NULL;
    if (extra_headers) hdrs = curl_slist_append(hdrs, extra_headers);
    if (hdrs) curl_easy_setopt(c, CURLOPT_HTTPHEADER, hdrs);
    if (range_from > 0) curl_easy_setopt(c, CURLOPT_RESUME_FROM_LARGE, (curl_off_t)range_from);
    CURLcode rc = curl_easy_perform(c);
    long status = 0;
    curl_easy_getinfo(c, CURLINFO_RESPONSE_CODE, &status);
    if (rc != CURLE_OK && rc != CURLE_RANGE_ERROR) {
        if (err) *err = nv_strdup(curl_easy_strerror(rc));
    }
    if (hdrs) curl_slist_free_all(hdrs);
    curl_easy_cleanup(c);
    int64_t sz = nv_file_size(dest);
    fclose(f);
    if (out_bytes) *out_bytes = sz > 0 ? sz : 0;
    if (rc != CURLE_OK && rc != CURLE_RANGE_ERROR) return -1;
    if (status == 404 || status == 403) return (int)status;
    if (status >= 400) return (int)status;
    return 200;
}
#endif
