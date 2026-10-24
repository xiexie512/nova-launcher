#ifndef NOVA_MD5_H
#define NOVA_MD5_H
#include <stdint.h>
#include <stddef.h>
void nv_md5(const unsigned char* data, size_t len, unsigned char out[16]);
#endif
