#include "nova_json.h"
#include "nova_util.h"

static nv* nv_new(nv_type t) {
    nv* v = (nv*)nv_xcalloc(1, sizeof(nv));
    v->t = t;
    return v;
}

void nv_free(nv* v) {
    if (!v) return;
    if (v->t == N_ARR) {
        for (int i = 0; i < v->count; i++) nv_free(v->items[i]);
        free(v->items);
    } else if (v->t == N_OBJ) {
        for (int i = 0; i < v->count; i++) { free(v->obj[i].k); nv_free(v->obj[i].v); }
        free(v->obj);
    }
    free(v->str);
    free(v);
}

static void skip_ws(const char** p) {
    while (**p == ' ' || **p == '\t' || **p == '\n' || **p == '\r') (*p)++;
}

static void append_utf8(nv_buf* b, unsigned cp) {
    if (cp < 0x80) nv_buf_append_char(b, (char)cp);
    else if (cp < 0x800) {
        nv_buf_append_char(b, (char)(0xC0 | (cp >> 6)));
        nv_buf_append_char(b, (char)(0x80 | (cp & 0x3F)));
    } else if (cp < 0x10000) {
        nv_buf_append_char(b, (char)(0xE0 | (cp >> 12)));
        nv_buf_append_char(b, (char)(0x80 | ((cp >> 6) & 0x3F)));
        nv_buf_append_char(b, (char)(0x80 | (cp & 0x3F)));
    } else {
        nv_buf_append_char(b, (char)(0xF0 | (cp >> 18)));
        nv_buf_append_char(b, (char)(0x80 | ((cp >> 12) & 0x3F)));
        nv_buf_append_char(b, (char)(0x80 | ((cp >> 6) & 0x3F)));
        nv_buf_append_char(b, (char)(0x80 | (cp & 0x3F)));
    }
}

static int hex4(const char* s) {
    int v = 0;
    for (int i = 0; i < 4; i++) {
        char c = s[i];
        v <<= 4;
        if (c >= '0' && c <= '9') v |= c - '0';
        else if (c >= 'a' && c <= 'f') v |= c - 'a' + 10;
        else if (c >= 'A' && c <= 'F') v |= c - 'A' + 10;
        else return -1;
    }
    return v;
}

static nv* parse_value(const char** p);

static nv* parse_string(const char** p) {
    (*p)++; /* " */
    nv_buf b; nv_buf_init(&b);
    while (**p && **p != '"') {
        char c = **p;
        if (c == '\\') {
            (*p)++;
            char e = **p;
            switch (e) {
                case 'n': nv_buf_append_char(&b, '\n'); break;
                case 't': nv_buf_append_char(&b, '\t'); break;
                case 'r': nv_buf_append_char(&b, '\r'); break;
                case 'b': nv_buf_append_char(&b, '\b'); break;
                case 'f': nv_buf_append_char(&b, '\f'); break;
                case '/': nv_buf_append_char(&b, '/'); break;
                case '\\': nv_buf_append_char(&b, '\\'); break;
                case '"': nv_buf_append_char(&b, '"'); break;
                case 'u': {
                    int cp = hex4(*p + 1);
                    if (cp < 0) { nv_buf_free(&b); return NULL; }
                    (*p) += 4;
                    if (cp >= 0xD800 && cp <= 0xDBFF && (*p)[0] == '\\' && (*p)[1] == 'u') {
                        int lo = hex4(*p + 3);
                        if (lo >= 0xDC00 && lo <= 0xDFFF) {
                            cp = 0x10000 + ((cp - 0xD800) << 10) + (lo - 0xDC00);
                            (*p) += 6;
                        }
                    }
                    append_utf8(&b, (unsigned)cp);
                    break;
                }
                default: nv_buf_free(&b); return NULL;
            }
            (*p)++;
        } else {
            nv_buf_append_char(&b, c);
            (*p)++;
        }
    }
    if (**p != '"') { nv_buf_free(&b); return NULL; }
    (*p)++;
    nv* v = nv_new(N_STR);
    v->str = nv_buf_detach(&b);
    return v;
}

static nv* parse_number(const char** p) {
    const char* s = *p;
    while ((**p >= '0' && **p <= '9') || **p == '-' || **p == '+' ||
           **p == '.' || **p == 'e' || **p == 'E') (*p)++;
    char* tmp = nv_strndup(s, (size_t)(*p - s));
    nv* v = nv_new(N_NUM);
    v->num = atof(tmp);
    free(tmp);
    return v;
}

static nv* parse_array(const char** p) {
    (*p)++; /* [ */
    nv* v = nv_new(N_ARR);
    skip_ws(p);
    if (**p == ']') { (*p)++; return v; }
    while (1) {
        skip_ws(p);
        nv* item = parse_value(p);
        if (!item) { nv_free(v); return NULL; }
        v->items = (nv**)nv_xrealloc(v->items, sizeof(nv*) * (size_t)(v->count + 1));
        v->items[v->count++] = item;
        skip_ws(p);
        if (**p == ',') { (*p)++; continue; }
        if (**p == ']') { (*p)++; return v; }
        nv_free(v);
        return NULL;
    }
}

static nv* parse_object(const char** p) {
    (*p)++; /* { */
    nv* v = nv_new(N_OBJ);
    skip_ws(p);
    if (**p == '}') { (*p)++; return v; }
    while (1) {
        skip_ws(p);
        if (**p != '"') { nv_free(v); return NULL; }
        nv* k = parse_string(p);
        if (!k) { nv_free(v); return NULL; }
        skip_ws(p);
        if (**p != ':') { nv_free(k); nv_free(v); return NULL; }
        (*p)++;
        skip_ws(p);
        nv* val = parse_value(p);
        if (!val) { nv_free(k); nv_free(v); return NULL; }
        v->obj = (nv_obj_entry*)nv_xrealloc(v->obj, sizeof(nv_obj_entry) * (size_t)(v->count + 1));
        v->obj[v->count].k = k->str; k->str = NULL;
        v->obj[v->count].v = val;
        v->count++;
        nv_free(k);
        skip_ws(p);
        if (**p == ',') { (*p)++; continue; }
        if (**p == '}') { (*p)++; return v; }
        nv_free(v);
        return NULL;
    }
}

static nv* parse_value(const char** p) {
    skip_ws(p);
    char c = **p;
    if (c == '{') return parse_object(p);
    if (c == '[') return parse_array(p);
    if (c == '"') return parse_string(p);
    if (c == 't') { if (strncmp(*p, "true", 4) == 0) { *p += 4; nv* v = nv_new(N_BOOL); v->bl = 1; return v; } return NULL; }
    if (c == 'f') { if (strncmp(*p, "false", 5) == 0) { *p += 5; nv* v = nv_new(N_BOOL); v->bl = 0; return v; } return NULL; }
    if (c == 'n') { if (strncmp(*p, "null", 4) == 0) { *p += 4; return nv_new(N_NULL); } return NULL; }
    if (c == '-' || (c >= '0' && c <= '9')) return parse_number(p);
    return NULL;
}

nv* nv_parse(const char* text) {
    if (!text) return NULL;
    const char* p = text;
    nv* v = parse_value(&p);
    if (!v) return NULL;
    skip_ws(&p);
    if (*p) { nv_free(v); return NULL; }
    return v;
}

static nv* find_key(const nv* v, const char* key) {
    if (!v || v->t != N_OBJ) return NULL;
    for (int i = 0; i < v->count; i++)
        if (strcmp(v->obj[i].k, key) == 0) return v->obj[i].v;
    return NULL;
}

nv* nv_get(const nv* v, const char* path) {
    if (!v || !path) return NULL;
    const nv* cur = v;
    const char* p = path;
    nv_buf seg; nv_buf_init(&seg);
    while (*p) {
        nv_buf_free(&seg); nv_buf_init(&seg);
        while (*p && *p != '.' && *p != '[') { nv_buf_append_char(&seg, *p); p++; }
        if (seg.len == 0) { nv_buf_free(&seg); return NULL; }
        cur = find_key(cur, seg.data);
        if (!cur) { nv_buf_free(&seg); return NULL; }
        while (*p == '[') {
            p++;
            const char* num = p;
            while (*p >= '0' && *p <= '9') p++;
            if (*p != ']') { nv_buf_free(&seg); return NULL; }
            int idx = atoi(num);
            if (!cur || cur->t != N_ARR || idx < 0 || idx >= cur->count) { nv_buf_free(&seg); return NULL; }
            cur = cur->items[idx];
            p++;
        }
        if (*p == '.') p++;
    }
    nv_buf_free(&seg);
    return (nv*)cur;
}

const char* nv_str(const nv* v) { return (v && v->t == N_STR) ? v->str : NULL; }
double nv_num(const nv* v) { return (v && v->t == N_NUM) ? v->num : 0.0; }
int nv_bool(const nv* v) { return (v && v->t == N_BOOL) ? v->bl : 0; }
int nv_arr_count(const nv* v) { return (v && v->t == N_ARR) ? v->count : 0; }
nv* nv_arr_at(const nv* v, int i) { return (v && v->t == N_ARR && i >= 0 && i < v->count) ? v->items[i] : NULL; }

const char* nv_str_at(const nv* root, const char* path) { return nv_str(nv_get(root, path)); }
double nv_num_at(const nv* root, const char* path) { return nv_num(nv_get(root, path)); }
