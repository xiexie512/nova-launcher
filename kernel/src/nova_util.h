#ifndef NOVA_UTIL_H
#define NOVA_UTIL_H

#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <stdint.h>

/* 动态字符串缓冲 */
typedef struct { char* data; size_t len; size_t cap; } nv_buf;

void  nv_buf_init(nv_buf* b);
void  nv_buf_free(nv_buf* b);
void  nv_buf_append(nv_buf* b, const char* s);
void  nv_buf_append_n(nv_buf* b, const char* s, size_t n);
void  nv_buf_append_char(nv_buf* b, char c);
void  nv_buf_append_int(nv_buf* b, long long v);
char* nv_buf_detach(nv_buf* b); /* 返回 malloc 串并重置 */

char* nv_strdup(const char* s);
char* nv_strndup(const char* s, size_t n);
int   nv_starts_with(const char* s, const char* p);
int   nv_ends_with(const char* s, const char* p);
/* 路径拼接（处理尾部/头部斜杠），返回 malloc */
char* nv_path_join(const char* a, const char* b);
/* 创建 dest 的所有父目录，返回 0 成功 */
int   nv_mkdirs(const char* path);
int   nv_file_exists(const char* path);
int64_t nv_file_size(const char* path);
/* 读取整个文件到 malloc 串（可能含 \0，用 *out_len 返回长度），失败 NULL */
char* nv_read_file(const char* path, size_t* out_len);
int   nv_write_file(const char* path, const char* data, size_t len);
/* 随机 UUID v4 字符串（malloc） */
char* nv_random_uuid(void);

void* nv_xmalloc(size_t n);
void* nv_xcalloc(size_t n, size_t sz);
void* nv_xrealloc(void* p, size_t n);

#endif
