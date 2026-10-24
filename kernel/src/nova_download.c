#include "nova_kernel.h"
#include "nova_util.h"
#include "nova_json.h"
#include "nova_sha1.h"
#include "nova_http.h"
#include "nova_thread.h"
#include "nova_platform.h"
#include <string.h>

typedef struct {
    char* key;
    char** urls;
    int nurls;
    char* dest;
    char* sha1;
    /* 结果 */
    int ok;
    int64_t bytes;
    int url_index;
    int sha1_ok;
    char* error;
} dl_job;

typedef struct {
    dl_job* jobs;
    int njobs;
    int workers;
    int timeout_ms;
    int retries;
    int next_job;
    int done_count;
    nv_mutex_t lock;
    nova_progress_fn cb;
    void* user;
} dl_batch;

/* ------- 单个 job 下载 ------- */
static int dl_file_ok(dl_job* j) {
    if (!nv_file_exists(j->dest)) return 0;
    if (j->sha1 && strlen(j->sha1) == 40) return nv_sha1_matches(j->dest, j->sha1);
    return nv_file_size(j->dest) > 0;
}

static void dl_do_job(dl_batch* b, int idx) {
    dl_job* j = &b->jobs[idx];
    /* 已存在且校验通过 -> 秒过 */
    if (dl_file_ok(j)) {
        j->ok = 1;
        j->bytes = nv_file_size(j->dest);
        j->sha1_ok = 1;
        j->url_index = -1;
        return;
    }
    /* 尝试每个源，失败重试 retries 轮 */
    for (int attempt = 0; attempt <= b->retries && !j->ok; attempt++) {
        for (int u = 0; u < j->nurls && !j->ok; u++) {
            /* 断点续传：已有部分文件 */
            int64_t have = nv_file_size(j->dest);
            if (have < 0) have = 0;
            /* 如果 sha1 已知且已有文件不匹配，则从 0 重下；否则从 have 续传 */
            int64_t range = (j->sha1 && strlen(j->sha1) == 40 && have > 0) ? have : (have > 0 ? have : 0);
            int64_t got = 0;
            char* err = NULL;
            int status = nv_http_download_to_file(j->urls[u], j->dest, range, NULL, b->timeout_ms, &got, &err);
            free(err);
            if (status == 200 || status == 206) {
                /* 校验 */
                int sha1_ok = 1;
                if (j->sha1 && strlen(j->sha1) == 40)
                    sha1_ok = nv_sha1_matches(j->dest, j->sha1);
                if (sha1_ok) {
                    j->ok = 1;
                    j->bytes = nv_file_size(j->dest);
                    j->sha1_ok = 1;
                    j->url_index = u;
                    break;
                } else {
                    /* 校验失败：删除重下 */
                    remove(j->dest);
                    if (u == j->nurls - 1 && attempt == b->retries) {
                        j->error = nv_strdup("sha1 mismatch after all sources");
                    }
                }
            } else {
                /* 网络错误：如果已写了部分内容且下轮续传，保留；全部失败则报错 */
                if (u == j->nurls - 1 && attempt == b->retries) {
                    char tmp[128];
                    snprintf(tmp, sizeof(tmp), "http status %d", status);
                    j->error = nv_strdup(tmp);
                }
            }
            if (!j->ok) nv_sleep_ms(200 * (attempt + 1));
        }
    }
}

static NV_THREAD_RET dl_worker(void* arg) {
    dl_batch* b = (dl_batch*)arg;
    for (;;) {
        nv_mutex_lock(&b->lock);
        if (b->next_job >= b->njobs) {
            nv_mutex_unlock(&b->lock);
            break;
        }
        int idx = b->next_job++;
        nv_mutex_unlock(&b->lock);
        dl_do_job(b, idx);
        nv_mutex_lock(&b->lock);
        b->done_count++;
        int all = (b->done_count >= b->njobs) ? 1 : 0;
        dl_job* j = &b->jobs[idx];
        if (b->cb)
            b->cb(idx, j->key, j->bytes, j->bytes, j->ok ? 1 : 0, all, b->user);
        nv_mutex_unlock(&b->lock);
    }
    return 0;
}

static void dl_job_free(dl_job* j) {
    free(j->key);
    for (int i = 0; i < j->nurls; i++) free(j->urls[i]);
    free(j->urls);
    free(j->dest);
    free(j->sha1);
    free(j->error);
    memset(j, 0, sizeof(*j));
}

char* nova_download_batch(const char* jobs_json, int workers, int timeout_ms,
                          nova_progress_fn cb, void* user, char** err) {
    if (!jobs_json) { if (err) *err = nv_strdup("jobs_json is null"); return NULL; }
    nv* root = nv_parse(jobs_json);
    if (!root || root->t != N_ARR) {
        nv_free(root);
        if (err) *err = nv_strdup("invalid jobs_json");
        return NULL;
    }
    int njobs = nv_arr_count(root);
    dl_job* jobs = (dl_job*)nv_xcalloc((size_t)(njobs > 0 ? njobs : 1), sizeof(dl_job));
    int nok = 0;
    for (int i = 0; i < njobs; i++) {
        nv* jv = nv_arr_at(root, i);
        if (!jv || jv->t != N_OBJ) continue;
        jobs[nok].key = nv_strdup(nv_str_at(jv, "key") ? nv_str_at(jv, "key") : "");
        jobs[nok].dest = nv_strdup(nv_str_at(jv, "dest"));
        jobs[nok].sha1 = nv_strdup(nv_str_at(jv, "sha1"));
        nv* urls = nv_get(jv, "urls");
        if (urls && urls->t == N_ARR) {
            jobs[nok].nurls = urls->count;
            jobs[nok].urls = (char**)nv_xcalloc((size_t)urls->count, sizeof(char*));
            for (int u = 0; u < urls->count; u++)
                jobs[nok].urls[u] = nv_strdup(nv_str(nv_arr_at(urls, u)));
        } else {
            jobs[nok].nurls = 0;
            jobs[nok].urls = NULL;
        }
        if (!jobs[nok].dest || jobs[nok].nurls == 0) {
            jobs[nok].error = nv_strdup("invalid job");
            nok++;
            continue;
        }
        /* 确保父目录存在 */
        nv_buf dir; nv_buf_init(&dir);
        const char* d = jobs[nok].dest;
        const char* last_slash = NULL;
        for (const char* p = d; *p; p++) if (*p == '/' || *p == '\\') last_slash = p;
        if (last_slash) { nv_buf_append_n(&dir, d, (size_t)(last_slash - d)); }
        if (dir.len) nv_mkdirs(dir.data);
        nv_buf_free(&dir);
        nok++;
    }
    njobs = nok;

    dl_batch b;
    memset(&b, 0, sizeof(b));
    b.jobs = jobs; b.njobs = njobs;
    b.workers = workers > 0 ? workers : 4;
    b.timeout_ms = timeout_ms > 0 ? timeout_ms : 60000;
    b.retries = 1;
    b.cb = cb; b.user = user;
    nv_mutex_init(&b.lock);

    int nthreads = b.workers < njobs ? b.workers : (njobs > 0 ? njobs : 1);
    if (nthreads < 1) nthreads = 1;
    nv_thread_t* ths = (nv_thread_t*)nv_xmalloc(sizeof(nv_thread_t) * (size_t)nthreads);
    for (int i = 0; i < nthreads; i++) nv_thread_create(&ths[i], dl_worker, &b);
    for (int i = 0; i < nthreads; i++) nv_thread_join(ths[i]);
    free(ths);

    /* 结果 JSON */
    nv_buf out; nv_buf_init(&out);
    nv_buf_append(&out, "[");
    for (int i = 0; i < njobs; i++) {
        dl_job* j = &jobs[i];
        if (i) nv_buf_append(&out, ",");
        nv_buf_append(&out, "{\"key\":\"");
        nv_buf_append(&out, j->key ? j->key : "");
        nv_buf_append(&out, "\",\"ok\":");
        nv_buf_append(&out, j->ok ? "true" : "false");
        nv_buf_append(&out, ",\"bytes\":");
        nv_buf_append_int(&out, (long long)j->bytes);
        nv_buf_append(&out, ",\"urlIndex\":");
        nv_buf_append_int(&out, (long long)j->url_index);
        nv_buf_append(&out, ",\"sha1Ok\":");
        nv_buf_append(&out, j->sha1_ok ? "true" : "false");
        nv_buf_append(&out, ",\"error\":");
        if (j->error) { nv_buf_append(&out, "\""); nv_buf_append(&out, j->error); nv_buf_append(&out, "\""); }
        else nv_buf_append(&out, "null");
        nv_buf_append(&out, "}");
    }
    nv_buf_append(&out, "]");
    char* result = nv_buf_detach(&out);

    nv_mutex_destroy(&b.lock);
    for (int i = 0; i < njobs; i++) dl_job_free(&jobs[i]);
    free(jobs);
    nv_free(root);
    return result;
}

/* 单文件下载（带附加请求头），返回 0 成功 */
int nova_download_file_ex(const char* url, const char* dest, const char* sha1,
                          const char* extra_headers, int timeout_ms, char** err) {
    /* 已存在且校验通过 -> 秒过 */
    if (sha1 && strlen(sha1) == 40 && nv_file_exists(dest) && nv_sha1_matches(dest, sha1)) return 0;
    nv_buf dir; nv_buf_init(&dir);
    const char* d = dest;
    const char* last_slash = NULL;
    for (const char* p = d; *p; p++) if (*p == '/' || *p == '\\') last_slash = p;
    if (last_slash) { nv_buf_append_n(&dir, d, (size_t)(last_slash - d)); }
    if (dir.len) nv_mkdirs(dir.data);
    nv_buf_free(&dir);
    remove(dest);
    int64_t got = 0;
    char* err2 = NULL;
    int status = nv_http_download_to_file(url, dest, 0, extra_headers, timeout_ms, &got, &err2);
    if (status < 0 || status >= 400) {
        if (err) *err = err2 ? err2 : nv_strdup("download failed");
        else free(err2);
        return -1;
    }
    if (sha1 && strlen(sha1) == 40 && !nv_sha1_matches(dest, sha1)) {
        if (err) *err = nv_strdup("sha1 mismatch");
        remove(dest);
        return -1;
    }
    return 0;
}

int nova_download_file(const char* url, const char* dest, const char* sha1,
                       int timeout_ms, char** err) {
    char* jobs = NULL;
    nv_buf jb; nv_buf_init(&jb);
    nv_buf_append(&jb, "[{\"key\":\"f\",\"urls\":[\"");
    nv_buf_append(&jb, url);
    nv_buf_append(&jb, "\"],\"dest\":\"");
    nv_buf_append(&jb, dest);
    nv_buf_append(&jb, "\",\"sha1\":");
    if (sha1) { nv_buf_append(&jb, "\""); nv_buf_append(&jb, sha1); nv_buf_append(&jb, "\""); }
    else nv_buf_append(&jb, "null");
    nv_buf_append(&jb, "}]");
    jobs = nv_buf_detach(&jb);
    char* err2 = NULL;
    char* res = nova_download_batch(jobs, 1, timeout_ms, NULL, NULL, &err2);
    free(jobs);
    if (!res) { if (err) *err = err2 ? err2 : nv_strdup("download failed"); else free(err2); return -1; }
    nv* r = nv_parse(res);
    int ok = 0;
    if (r) {
        nv* j0 = nv_arr_at(r, 0);
        ok = j0 ? nv_bool(nv_get(j0, "ok")) : 0;
    }
    nv_free(r);
    free(res);
    return ok ? 0 : -1;
}
