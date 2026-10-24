#ifndef NOVA_HTTP_H
#define NOVA_HTTP_H

#include <stdint.h>
#include <stddef.h>
#include <stdio.h>

/* 下载回调：返回非 0 中止 */
typedef int (*nv_http_write_fn)(const unsigned char* data, size_t len, void* user);

/* 通用 HTTP 请求；method "GET"/"POST"；body 可为 NULL；
   extra_headers 为 \r\n 分隔的附加头或 NULL。
   返回 malloc 响应体（UTF-8），失败返回 NULL（err 由调用方传入）。 */
char* nv_http_request(const char* method, const char* url, const char* body,
                      const char* content_type, const char* extra_headers,
                      int timeout_ms, char** err);

/* 流式下载到文件（支持 Range 断点续传，range_from>=0 时发送 Range 头；
   extra_headers 为 \r\n 分隔附加头或 NULL）。
   返回 HTTP 状态码（200/206/404 等），<0 网络错误。下载字节数写入 *out_bytes。 */
int nv_http_download_to_file(const char* url, const char* dest,
                             int64_t range_from, const char* extra_headers,
                             int timeout_ms, int64_t* out_bytes, char** err);

#endif
