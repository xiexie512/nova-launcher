#ifndef NOVA_SHA1_H
#define NOVA_SHA1_H
#include <stdint.h>
#include <stddef.h>
/* 返回 0 表示校验通过 */
int nv_sha1_file(const char* path, char hex_out[41]);
int nv_sha1_matches(const char* path, const char* expected_hex);
#endif
