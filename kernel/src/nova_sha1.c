#include "nova_sha1.h"
#include "nova_util.h"
#include <stdio.h>

typedef struct {
    uint32_t state[5];
    uint64_t count;
    unsigned char buffer[64];
} SHA1_CTX;

static uint32_t rotl32(uint32_t x, int s) { return (x << s) | (x >> (32 - s)); }

static void sha1_block(SHA1_CTX* c, const unsigned char* p) {
    uint32_t w[80];
    for (int i = 0; i < 16; i++)
        w[i] = ((uint32_t)p[i*4] << 24) | ((uint32_t)p[i*4+1] << 16) | ((uint32_t)p[i*4+2] << 8) | (uint32_t)p[i*4+3];
    for (int i = 16; i < 80; i++)
        w[i] = rotl32(w[i-3] ^ w[i-8] ^ w[i-14] ^ w[i-16], 1);
    uint32_t a = c->state[0], b = c->state[1], cc = c->state[2], d = c->state[3], e = c->state[4];
    for (int i = 0; i < 80; i++) {
        uint32_t f, k;
        if (i < 20) { f = (b & cc) | (~b & d); k = 0x5A827999; }
        else if (i < 40) { f = b ^ cc ^ d; k = 0x6ED9EBA1; }
        else if (i < 60) { f = (b & cc) | (b & d) | (cc & d); k = 0x8F1BBCDC; }
        else { f = b ^ cc ^ d; k = 0xCA62C1D6; }
        uint32_t tmp = rotl32(a, 5) + f + e + k + w[i];
        e = d; d = cc; cc = rotl32(b, 30); b = a; a = tmp;
    }
    c->state[0] += a; c->state[1] += b; c->state[2] += cc; c->state[3] += d; c->state[4] += e;
}

static void sha1_init(SHA1_CTX* c) {
    c->state[0] = 0x67452301; c->state[1] = 0xEFCDAB89;
    c->state[2] = 0x98BADCFE; c->state[3] = 0x10325476; c->state[4] = 0xC3D2E1F0;
    c->count = 0;
}

static void sha1_update(SHA1_CTX* c, const unsigned char* data, size_t len) {
    size_t idx = (size_t)(c->count & 63);
    c->count += len;
    size_t free_space = 64 - idx;
    if (len >= free_space) {
        memcpy(c->buffer + idx, data, free_space);
        sha1_block(c, c->buffer);
        size_t off = free_space;
        while (off + 64 <= len) { sha1_block(c, data + off); off += 64; }
        memcpy(c->buffer, data + off, len - off);
    } else {
        memcpy(c->buffer + idx, data, len);
    }
}

static void sha1_final(SHA1_CTX* c, unsigned char out[20]) {
    uint64_t bits = c->count * 8;
    unsigned char bitbuf[8];
    for (int i = 0; i < 8; i++) bitbuf[i] = (unsigned char)(bits >> (56 - 8*i));
    unsigned char pad = 0x80;
    size_t idx = (size_t)(c->count & 63);
    size_t padlen = (idx < 56) ? (56 - idx) : (120 - idx);
    sha1_update(c, &pad, 1);
    size_t rest = padlen - 1;
    static const unsigned char zeros[64] = {0};
    if (rest) sha1_update(c, zeros, rest);
    sha1_update(c, bitbuf, 8);
    for (int i = 0; i < 5; i++) {
        out[i*4]   = (unsigned char)(c->state[i] >> 24);
        out[i*4+1] = (unsigned char)(c->state[i] >> 16);
        out[i*4+2] = (unsigned char)(c->state[i] >> 8);
        out[i*4+3] = (unsigned char)c->state[i];
    }
}

static int hexval(char c) {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}

int nv_sha1_matches(const char* path, const char* expected_hex) {
    if (!expected_hex || strlen(expected_hex) != 40) return 0;
    FILE* f = fopen(path, "rb");
    if (!f) return 0;
    SHA1_CTX c; sha1_init(&c);
    unsigned char buf[65536];
    size_t n;
    while ((n = fread(buf, 1, sizeof(buf), f)) > 0) sha1_update(&c, buf, n);
    fclose(f);
    unsigned char digest[20];
    sha1_final(&c, digest);
    for (int i = 0; i < 20; i++) {
        int hi = hexval(expected_hex[i*2]);
        int lo = hexval(expected_hex[i*2+1]);
        if (hi < 0 || lo < 0) return 0;
        if (digest[i] != (unsigned char)((hi << 4) | lo)) return 0;
    }
    return 1;
}

int nv_sha1_file(const char* path, char hex_out[41]) {
    FILE* f = fopen(path, "rb");
    if (!f) return -1;
    SHA1_CTX c; sha1_init(&c);
    unsigned char buf[65536];
    size_t n;
    while ((n = fread(buf, 1, sizeof(buf), f)) > 0) sha1_update(&c, buf, n);
    fclose(f);
    unsigned char digest[20];
    sha1_final(&c, digest);
    for (int i = 0; i < 20; i++) snprintf(hex_out + i*2, 3, "%02x", digest[i]);
    return 0;
}
