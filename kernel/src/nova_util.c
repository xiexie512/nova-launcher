#include "nova_util.h"
#include "nova_platform.h"
#include <sys/stat.h>
#include <errno.h>
#include <time.h>

void* nv_xmalloc(size_t n) { void* p = malloc(n ? n : 1); if (!p) abort(); return p; }
void* nv_xcalloc(size_t n, size_t sz) { void* p = calloc(n ? n : 1, sz ? sz : 1); if (!p) abort(); return p; }
void* nv_xrealloc(void* p, size_t n) { void* q = realloc(p, n ? n : 1); if (!q) abort(); return q; }

char* nv_strdup(const char* s) {
    if (!s) return NULL;
    size_t n = strlen(s);
    char* r = (char*)nv_xmalloc(n + 1);
    memcpy(r, s, n + 1);
    return r;
}
char* nv_strndup(const char* s, size_t n) {
    char* r = (char*)nv_xmalloc(n + 1);
    memcpy(r, s, n);
    r[n] = 0;
    return r;
}
int nv_starts_with(const char* s, const char* p) {
    size_t n = strlen(p);
    return strncmp(s, p, n) == 0;
}
int nv_ends_with(const char* s, const char* p) {
    size_t ls = strlen(s), lp = strlen(p);
    if (lp > ls) return 0;
    return memcmp(s + ls - lp, p, lp) == 0;
}

void nv_buf_init(nv_buf* b) { b->data = NULL; b->len = 0; b->cap = 0; }
void nv_buf_free(nv_buf* b) { free(b->data); b->data = NULL; b->len = b->cap = 0; }
static void nv_buf_reserve(nv_buf* b, size_t extra) {
    if (b->len + extra + 1 > b->cap) {
        size_t nc = b->cap ? b->cap : 64;
        while (nc < b->len + extra + 1) nc *= 2;
        b->data = (char*)nv_xrealloc(b->data, nc);
        b->cap = nc;
    }
}
void nv_buf_append_n(nv_buf* b, const char* s, size_t n) {
    nv_buf_reserve(b, n);
    memcpy(b->data + b->len, s, n);
    b->len += n;
    b->data[b->len] = 0;
}
void nv_buf_append(nv_buf* b, const char* s) { nv_buf_append_n(b, s, strlen(s)); }
void nv_buf_append_char(nv_buf* b, char c) { nv_buf_append_n(b, &c, 1); }
void nv_buf_append_int(nv_buf* b, long long v) {
    char tmp[32];
    snprintf(tmp, sizeof(tmp), "%lld", v);
    nv_buf_append(b, tmp);
}
char* nv_buf_detach(nv_buf* b) {
    if (!b->data) { b->data = (char*)nv_xmalloc(1); b->data[0] = 0; b->len = 0; b->cap = 1; }
    char* r = b->data;
    b->data = NULL; b->len = b->cap = 0;
    return r;
}

char* nv_path_join(const char* a, const char* b) {
    nv_buf s; nv_buf_init(&s);
    nv_buf_append(&s, a);
    size_t la = strlen(a);
    if (la == 0) { nv_buf_append(&s, b); return nv_buf_detach(&s); }
    if (a[la-1] != '/' && a[la-1] != '\\') nv_buf_append_char(&s, '/');
    if (b && *b == '/') b++;
    nv_buf_append(&s, b);
    return nv_buf_detach(&s);
}

int nv_mkdirs(const char* path) {
    if (!path || !*path) return -1;
    char tmp[4096];
    size_t n = strlen(path);
    if (n >= sizeof(tmp)) return -1;
    memcpy(tmp, path, n + 1);
    /* 去掉结尾分隔符 */
    size_t end = n;
    while (end > 1 && (tmp[end-1] == '/' || tmp[end-1] == '\\')) tmp[--end] = 0;
    for (size_t i = 1; i < end; i++) {
        if (tmp[i] == '/' || tmp[i] == '\\') {
            tmp[i] = 0;
            if (tmp[0]) { if (nv_mkdir(tmp) != 0 && errno != EEXIST) return -1; }
            tmp[i] = '/';
        }
    }
    if (tmp[0] && nv_mkdir(tmp) != 0 && errno != EEXIST) return -1;
    return 0;
}

int nv_file_exists(const char* path) {
    struct stat st;
    return stat(path, &st) == 0;
}
int64_t nv_file_size(const char* path) {
    struct stat st;
    if (stat(path, &st) != 0) return -1;
    return (int64_t)st.st_size;
}

char* nv_read_file(const char* path, size_t* out_len) {
    FILE* f = fopen(path, "rb");
    if (!f) return NULL;
    fseek(f, 0, SEEK_END);
    long sz = ftell(f);
    fseek(f, 0, SEEK_SET);
    if (sz < 0) { fclose(f); return NULL; }
    char* d = (char*)nv_xmalloc((size_t)sz + 1);
    size_t rd = fread(d, 1, (size_t)sz, f);
    fclose(f);
    d[rd] = 0;
    if (out_len) *out_len = rd;
    return d;
}

int nv_write_file(const char* path, const char* data, size_t len) {
    FILE* f = fopen(path, "wb");
    if (!f) return -1;
    size_t w = fwrite(data, 1, len, f);
    fclose(f);
    return w == len ? 0 : -1;
}

char* nv_random_uuid(void) {
    unsigned char b[16];
    FILE* f = fopen("/dev/urandom", "rb");
    if (f) { if (fread(b, 1, 16, f) != 16) { fclose(f); goto fallback; } fclose(f); }
    else {
fallback:
        for (int i = 0; i < 16; i++) b[i] = (unsigned char)(rand() ^ (int)time(0) ^ (i * 37));
    }
    b[6] = (unsigned char)((b[6] & 0x0F) | 0x40);
    b[8] = (unsigned char)((b[8] & 0x3F) | 0x80);
    char* s = (char*)nv_xmalloc(37);
    snprintf(s, 37, "%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x",
             b[0], b[1], b[2], b[3], b[4], b[5], b[6], b[7], b[8], b[9], b[10], b[11], b[12], b[13], b[14], b[15]);
    return s;
}
