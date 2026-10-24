/*
 * NovaLauncher 自研内核 (nova_kernel)
 * 跨平台 C 动态库：多源并行下载引擎 / HTTP / JSON / 启动命令构建 / 登录协议
 * macOS -> libnova_kernel.dylib (libcurl)
 * Windows -> nova_kernel.dll (WinHTTP)
 */
#ifndef NOVA_KERNEL_H
#define NOVA_KERNEL_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define NOVA_KERNEL_VERSION "1.0.0"

/* 下载进度回调：batch_done=1 表示整批完成 */
typedef void (*nova_progress_fn)(int job_id, const char* key,
                                 int64_t downloaded, int64_t total,
                                 int job_done, int batch_done, void* user);

/* 所有返回的 char*（除 nova_version/nova_last_error 外）均由内核 malloc，
   调用方必须用 nova_free_string 释放。err 同理。 */

const char* nova_version(void);
void        nova_free_string(char* s);

/* ---------- HTTP ---------- */
/* 成功返回 malloc 的响应体（UTF-8），失败返回 NULL 并写 err */
char* nova_http_get(const char* url, int timeout_ms, char** err);
char* nova_http_post(const char* url, const char* body,
                     const char* content_type, int timeout_ms, char** err);
/* 带附加请求头（\r\n 分隔），用于 API 鉴权 */
char* nova_http_get_ex(const char* url, const char* extra_headers,
                       int timeout_ms, char** err);
char* nova_http_post_ex(const char* url, const char* body,
                        const char* content_type, const char* extra_headers,
                        int timeout_ms, char** err);

/* ---------- 下载 ---------- */
/* 单文件下载并校验 sha1（可为 NULL）。返回 0 成功 */
int   nova_download_file(const char* url, const char* dest, const char* sha1,
                         int timeout_ms, char** err);
/* 单文件下载（带附加请求头），返回 0 成功 */
int   nova_download_file_ex(const char* url, const char* dest, const char* sha1,
                            const char* extra_headers, int timeout_ms, char** err);
/*
 * 多源并行下载批处理。
 * jobs_json: [{"key":"...","urls":["u1","u2"],"dest":"/abs","sha1":"..."}]
 * 成功返回结果 JSON: [{"key":"...","ok":true,"bytes":123,"urlIndex":0,"sha1Ok":true,"error":null}]
 * 失败（整批无法解析等）返回 NULL 并写 err。
 */
char* nova_download_batch(const char* jobs_json, int workers, int timeout_ms,
                          nova_progress_fn cb, void* user, char** err);

/* ---------- 启动命令构建 ---------- */
/*
 * req_json: {
 *   "versionJson": {完整版本JSON},
 *   "gameDir": "...", "librariesDir": "...", "assetsDir": "...",
 *   "nativesDir": "...", "clientJar": "...", "logConfigPath": "...",
 *   "java": "/path/to/java", "username": "...", "uuid": "...",
 *   "accessToken": "...", "userType": "msa|mojang|legacy",
 *   "versionType": "release", "memoryMb": 2048,
 *   "width": 0, "height": 0, "extraJvmArgs": ["-Dx=y"]
 * }
 * 返回 argv JSON 数组（字符串）。
 */
char* nova_build_launch(const char* req_json, char** err);

/* ---------- 登录 ---------- */
/* 离线 UUID：MD5("OfflinePlayer:"+name) v3 */
char* nova_offline_uuid(const char* player_name, char** err);
/* Yggdrasil 外置登录。auth_server 如 https://littleskin.cn/api/yggdrasil
   返回 {"accessToken","clientToken","uuid","name"} */
char* nova_yggdrasil_login(const char* auth_server, const char* username,
                           const char* password, const char* client_token,
                           int timeout_ms, char** err);
/* 微软设备码流程（client_id 需 Azure 应用注册） */
char* nova_microsoft_device_code(const char* client_id, int timeout_ms, char** err);
/* 轮询令牌，返回原始 JSON（可能含 error=authorization_pending） */
char* nova_microsoft_poll_token(const char* client_id, const char* device_code,
                                int timeout_ms, char** err);
/* 用 access_token 完成 Xbox -> XSTS -> Minecraft 认证
   返回 {"accessToken","uuid","name","expiresIn"} */
char* nova_microsoft_finish(const char* access_token, int timeout_ms, char** err);

/* ---------- Java ---------- */
/* dirs_json: ["/a/bin/java", ...]，返回第一个存在且可执行的路径，找不到返回 NULL */
char* nova_java_find(const char* dirs_json, char** err);
/* 校验 java 路径存在且可执行，返回 0 有效 */
int   nova_java_check(const char* java_path, char** err);

const char* nova_last_error(void);

#ifdef __cplusplus
}
#endif
#endif /* NOVA_KERNEL_H */
