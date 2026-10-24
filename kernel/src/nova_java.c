#include "nova_kernel.h"
#include "nova_util.h"
#include "nova_json.h"
#include "nova_platform.h"
#include <string.h>

#ifdef _WIN32
  #include <io.h>
  static int access_ok(const char* p) { return _access(p, 0) == 0; }
#else
  #include <unistd.h>
  static int access_ok(const char* p) { return access(p, X_OK) == 0; }
#endif

char* nova_java_find(const char* dirs_json, char** err) {
    (void)err; /* 保留 out 参数以兼容调用方，当前无错误上报 */
    if (!dirs_json) return NULL;
    nv* root = nv_parse(dirs_json);
    if (!root || root->t != N_ARR) { nv_free(root); return NULL; }
    char* found = NULL;
    for (int i = 0; i < root->count; i++) {
        const char* p = nv_str(nv_arr_at(root, i));
        if (!p || !*p) continue;
        if (access_ok(p)) { found = nv_strdup(p); break; }
    }
    nv_free(root);
    return found;
}

int nova_java_check(const char* java_path, char** err) {
    if (!java_path || !*java_path) { if (err) *err = nv_strdup("empty java path"); return -1; }
    return access_ok(java_path) ? 0 : -1;
}
