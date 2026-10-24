#include "nova_kernel.h"
#include "nova_util.h"
#include "nova_json.h"
#include "nova_platform.h"
#include <string.h>

/* ---------------- 平台信息 ---------------- */
#ifdef _WIN32
  #define OS_NAME "windows"
  #ifdef _M_ARM64
    #define OS_ARCH "arm64"
  #else
    #define OS_ARCH "x86_64"
  #endif
  #define CLASS_SEP ";"
#elif defined(__APPLE__)
  #define OS_NAME "osx"
  #ifdef __aarch64__
    #define OS_ARCH "arm64"
  #else
    #define OS_ARCH "x86_64"
  #endif
  #define CLASS_SEP ":"
#else
  #define OS_NAME "linux"
  #ifdef __aarch64__
    #define OS_ARCH "arm64"
  #else
    #define OS_ARCH "x86_64"
  #endif
  #define CLASS_SEP ":"
#endif

/* ---------------- 字符串列表 ---------------- */
typedef struct { char** items; int count; int cap; } strlist;
static void sl_add(strlist* l, const char* s) {
    if (l->count >= l->cap) {
        l->cap = l->cap ? l->cap * 2 : 16;
        l->items = (char**)nv_xrealloc(l->items, sizeof(char*) * (size_t)l->cap);
    }
    l->items[l->count++] = nv_strdup(s);
}
static void sl_free(strlist* l) {
    for (int i = 0; i < l->count; i++) free(l->items[i]);
    free(l->items);
    memset(l, 0, sizeof(*l));
}

static void jstr(nv_buf* b, const char* s) {
    nv_buf_append_char(b, '"');
    if (s) for (const char* p = s; *p; p++) {
        switch (*p) {
            case '"': nv_buf_append(b, "\\\""); break;
            case '\\': nv_buf_append(b, "\\\\"); break;
            case '\n': nv_buf_append(b, "\\n"); break;
            case '\r': nv_buf_append(b, "\\r"); break;
            case '\t': nv_buf_append(b, "\\t"); break;
            default: nv_buf_append_char(b, *p);
        }
    }
    nv_buf_append_char(b, '"');
}

/* 库名 -> 相对路径 */
static char* lib_path_from_name(const char* name, const char* classifier) {
    const char* c1 = strchr(name, ':');
    if (!c1) return NULL;
    const char* c2 = strchr(c1 + 1, ':');
    if (!c2) return NULL;
    const char* c3 = strchr(c2 + 1, ':');
    size_t glen = (size_t)(c1 - name);
    size_t alen = (size_t)(c2 - c1 - 1);
    size_t vlen = (size_t)((c3 ? c3 : name + strlen(name)) - c2 - 1);
    nv_buf b; nv_buf_init(&b);
    for (size_t i = 0; i < glen; i++) {
        if (name[i] == '.') nv_buf_append_char(&b, '/');
        else nv_buf_append_char(&b, name[i]);
    }
    nv_buf_append_char(&b, '/');
    nv_buf_append_n(&b, c1 + 1, alen);
    nv_buf_append_char(&b, '/');
    nv_buf_append_n(&b, c2 + 1, vlen);
    nv_buf_append_char(&b, '/');
    nv_buf_append_n(&b, c1 + 1, alen);
    nv_buf_append_char(&b, '-');
    nv_buf_append_n(&b, c2 + 1, vlen);
    if (classifier && *classifier) {
        nv_buf_append_char(&b, '-');
        nv_buf_append(&b, classifier);
    }
    nv_buf_append(&b, ".jar");
    return nv_buf_detach(&b);
}

/* 平台规则匹配 */
static int rules_match(const nv* rules, int has_custom_res) {
    if (!rules || rules->t != N_ARR) return 1;
    int allowed = 0, matched_any = 0;
    for (int i = 0; i < rules->count; i++) {
        nv* rule = nv_arr_at(rules, i);
        if (!rule || rule->t != N_OBJ) continue;
        const char* action = nv_str_at(rule, "action");
        int is_allow = action && strcmp(action, "allow") == 0;
        nv* os = nv_get(rule, "os");
        nv* features = nv_get(rule, "features");
        int match = 1;
        if (os && os->t == N_OBJ) {
            const char* oname = nv_str_at(os, "name");
            const char* oarch = nv_str_at(os, "arch");
            const char* oversion = nv_str_at(os, "version");
            if (oname && strcmp(oname, OS_NAME) != 0) match = 0;
            if (oarch && strcmp(oarch, OS_ARCH) != 0) match = 0;
            if (oversion) match = 0;
        }
        if (features && features->t == N_OBJ) {
            for (int f = 0; f < features->count; f++) {
                const char* key = features->obj[f].k;
                int want = nv_bool(features->obj[f].v);
                int got = 0;
                if (strcmp(key, "has_custom_resolution") == 0) got = has_custom_res;
                if (strcmp(key, "is_demo_user") == 0) got = 0;
                if (strcmp(key, "has_quick_plays_support") == 0) got = 0;
                if (got != want) match = 0;
            }
        }
        if (match) { matched_any = 1; allowed = is_allow ? 1 : 0; }
    }
    return matched_any ? allowed : 0;
}

/* 占位符替换 */
static void subst(nv_buf* out, const char* arg, const char* const* keys, const char* const* vals, int nkv) {
    const char* p = arg;
    while (*p) {
        const char* dollar = strstr(p, "${");
        if (!dollar) { nv_buf_append(out, p); break; }
        nv_buf_append_n(out, p, (size_t)(dollar - p));
        const char* close = strchr(dollar + 2, '}');
        if (!close) { nv_buf_append(out, p); break; }
        size_t klen = (size_t)(close - dollar - 2);
        const char* val = NULL;
        for (int i = 0; i < nkv; i++)
            if (strlen(keys[i]) == klen && strncmp(keys[i], dollar + 2, klen) == 0) { val = vals[i]; break; }
        if (val) nv_buf_append(out, val);
        else nv_buf_append_n(out, dollar, klen + 3);
        p = close + 1;
    }
}

/* 空白拆分（legacy minecraftArguments） */
static void split_ws(strlist* l, const char* s) {
    const char* p = s;
    while (*p) {
        while (*p == ' ' || *p == '\t' || *p == '\n' || *p == '\r') p++;
        if (!*p) break;
        const char* start = p;
        while (*p && *p != ' ' && *p != '\t' && *p != '\n' && *p != '\r') p++;
        sl_add(l, nv_strndup(start, (size_t)(p - start)));
    }
}

char* nova_build_launch(const char* req_json, char** err) {
    if (!req_json) { if (err) *err = nv_strdup("req_json is null"); return NULL; }
    nv* req = nv_parse(req_json);
    if (!req) { if (err) *err = nv_strdup("req_json invalid"); return NULL; }
    nv* vj = nv_get(req, "versionJson");
    if (!vj || vj->t != N_OBJ) { nv_free(req); if (err) *err = nv_strdup("versionJson missing"); return NULL; }

    const char* game_dir   = nv_str_at(req, "gameDir");
    const char* libs_dir   = nv_str_at(req, "librariesDir");
    const char* assets_dir = nv_str_at(req, "assetsDir");
    const char* natives    = nv_str_at(req, "nativesDir");
    const char* client_jar = nv_str_at(req, "clientJar");
    const char* java       = nv_str_at(req, "java");
    const char* username   = nv_str_at(req, "username");
    const char* uuid       = nv_str_at(req, "uuid");
    const char* token      = nv_str_at(req, "accessToken");
    const char* user_type  = nv_str_at(req, "userType");
    const char* ver_type   = nv_str_at(req, "versionType");
    const char* ver_name   = nv_str_at(req, "versionName");
    const char* log_cfg    = nv_str_at(req, "logConfigPath");
    const char* asset_index= nv_str_at(req, "assetIndexName");
    int mem = (int)nv_num_at(req, "memoryMb");
    int width = (int)nv_num_at(req, "width");
    int height = (int)nv_num_at(req, "height");
    if (!game_dir || !libs_dir || !assets_dir || !natives || !client_jar || !java ||
        !username || !uuid || !token) {
        nv_free(req);
        if (err) *err = nv_strdup("req_json missing required fields");
        return NULL;
    }
    if (mem <= 0) mem = 2048;
    if (!ver_name) ver_name = nv_str_at(vj, "id");
    if (!ver_name) ver_name = "unknown";
    if (!asset_index) asset_index = "legacy";

    /* classpath */
    nv_buf cp; nv_buf_init(&cp);
    nv* libs = nv_get(vj, "libraries");
    if (libs && libs->t == N_ARR) {
        for (int i = 0; i < libs->count; i++) {
            nv* lib = nv_arr_at(libs, i);
            if (!lib || lib->t != N_OBJ) continue;
            const char* name = nv_str_at(lib, "name");
            if (!name) continue;
            nv* artifact = nv_get(lib, "downloads.artifact");
            char* rel = NULL;
            if (artifact && artifact->t == N_OBJ) {
                const char* apath = nv_str_at(artifact, "path");
                if (apath) rel = nv_strdup(apath);
                else {
                    const char* aurl = nv_str_at(artifact, "url");
                    if (aurl) {
                        const char* q = strstr(aurl, "://");
                        const char* p2 = q ? q + 3 : aurl;
                        const char* slash = p2 ? strchr(p2, '/') : NULL;
                        if (slash) rel = nv_strdup(slash + 1);
                    }
                }
            } else if (nv_get(lib, "downloads.classifiers")) {
                /* 仅有 natives classifier 的库不进 classpath */
                continue;
            }
            if (!rel) rel = lib_path_from_name(name, NULL);
            if (!rel) continue;
            if (cp.len) nv_buf_append(&cp, CLASS_SEP);
            nv_buf_append(&cp, libs_dir);
            nv_buf_append_char(&cp, '/');
            nv_buf_append(&cp, rel);
            free(rel);
        }
    }
    if (cp.len) nv_buf_append(&cp, CLASS_SEP);
    nv_buf_append(&cp, client_jar);
    char* cp_s = nv_buf_detach(&cp);

    /* 占位符表 */
    char wbuf[16], hbuf[16];
    snprintf(wbuf, sizeof(wbuf), "%d", width);
    snprintf(hbuf, sizeof(hbuf), "%d", height);
    const char* const gk[] = { "auth_player_name", "auth_session", "auth_access_token",
                               "auth_uuid", "version_name", "game_directory", "assets_root",
                               "assets_index_name", "user_type", "version_type",
                               "resolution_width", "resolution_height", "user_properties",
                               "profile_name", "clientid" };
    const char* gv[] = { username, token, token, uuid, ver_name, game_dir, assets_dir,
                         asset_index, user_type ? user_type : "legacy", ver_type ? ver_type : "release",
                         wbuf, hbuf, "{}", username, "0" };

    /* JVM 参数列表 */
    strlist jvm; memset(&jvm, 0, sizeof(jvm));
    nv* jvm_args = nv_get(vj, "arguments.jvm");
    if (jvm_args && jvm_args->t == N_ARR) {
        for (int i = 0; i < jvm_args->count; i++) {
            nv* item = nv_arr_at(jvm_args, i);
            if (!item) continue;
            if (item->t == N_STR) {
                nv_buf tmp; nv_buf_init(&tmp);
                subst(&tmp, item->str, gk, gv, 15);
                sl_add(&jvm, tmp.data);
                nv_buf_free(&tmp);
            } else if (item->t == N_OBJ) {
                if (rules_match(nv_get(item, "rules"), width > 0)) {
                    nv* val = nv_get(item, "value");
                    if (val && val->t == N_ARR) {
                        for (int k = 0; k < val->count; k++) {
                            const char* s = nv_str(nv_arr_at(val, k));
                            if (!s) continue;
                            nv_buf tmp; nv_buf_init(&tmp);
                            subst(&tmp, s, gk, gv, 15);
                            sl_add(&jvm, tmp.data);
                            nv_buf_free(&tmp);
                        }
                    }
                }
            }
        }
    }
    nv_buf memarg; nv_buf_init(&memarg);
    nv_buf_append(&memarg, "-Xmx"); nv_buf_append_int(&memarg, mem); nv_buf_append(&memarg, "M");
    sl_add(&jvm, memarg.data);
    nv_buf_free(&memarg);
    sl_add(&jvm, "-Xms256M");
    nv_buf native; nv_buf_init(&native);
    nv_buf_append(&native, "-Djava.library.path=");
    nv_buf_append(&native, natives);
    sl_add(&jvm, native.data);
    nv_buf_free(&native);
    if (log_cfg && *log_cfg) {
        const char* log_arg = nv_str_at(vj, "logging.client.argument");
        if (log_arg) {
            nv_buf tmp; nv_buf_init(&tmp);
            const char* const lk[] = { "path" };
            const char* const lv[] = { log_cfg };
            subst(&tmp, log_arg, lk, lv, 1);
            sl_add(&jvm, tmp.data);
            nv_buf_free(&tmp);
        }
    }
    /* 额外 JVM 参数 */
    nv* extra = nv_get(req, "extraJvmArgs");
    if (extra && extra->t == N_ARR) {
        for (int i = 0; i < extra->count; i++) {
            const char* s = nv_str(nv_arr_at(extra, i));
            if (s) sl_add(&jvm, s);
        }
    }

    /* 游戏参数列表 */
    strlist game; memset(&game, 0, sizeof(game));
    const char* main_class = nv_str_at(vj, "mainClass");
    nv* game_args = nv_get(vj, "arguments.game");
    if (game_args && game_args->t == N_ARR) {
        for (int i = 0; i < game_args->count; i++) {
            nv* item = nv_arr_at(game_args, i);
            if (!item) continue;
            if (item->t == N_STR) {
                nv_buf tmp; nv_buf_init(&tmp);
                subst(&tmp, item->str, gk, gv, 15);
                sl_add(&game, tmp.data);
                nv_buf_free(&tmp);
            } else if (item->t == N_OBJ) {
                if (rules_match(nv_get(item, "rules"), width > 0)) {
                    nv* val = nv_get(item, "value");
                    if (val && val->t == N_ARR) {
                        for (int k = 0; k < val->count; k++) {
                            const char* s = nv_str(nv_arr_at(val, k));
                            if (!s) continue;
                            nv_buf tmp; nv_buf_init(&tmp);
                            subst(&tmp, s, gk, gv, 15);
                            sl_add(&game, tmp.data);
                            nv_buf_free(&tmp);
                        }
                    }
                }
            }
        }
    } else {
        const char* legacy = nv_str_at(vj, "minecraftArguments");
        if (legacy) {
            nv_buf tmp; nv_buf_init(&tmp);
            subst(&tmp, legacy, gk, gv, 15);
            split_ws(&game, tmp.data);
            nv_buf_free(&tmp);
        }
    }

    /* 组装 argv JSON：java, jvm..., -cp, cp, mainClass, game... */
    nv_buf out; nv_buf_init(&out);
    nv_buf_append(&out, "[");
    jstr(&out, java);
    for (int i = 0; i < jvm.count; i++) { nv_buf_append(&out, ","); jstr(&out, jvm.items[i]); }
    nv_buf_append(&out, ",\"-cp\",");
    jstr(&out, cp_s);
    nv_buf_append(&out, ",");
    jstr(&out, main_class ? main_class : "net.minecraft.client.main.Main");
    for (int i = 0; i < game.count; i++) { nv_buf_append(&out, ","); jstr(&out, game.items[i]); }
    nv_buf_append(&out, "]");
    char* argv = nv_buf_detach(&out);

    free(cp_s);
    sl_free(&jvm);
    sl_free(&game);
    nv_free(req);
    return argv;
}
