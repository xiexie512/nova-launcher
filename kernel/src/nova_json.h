#ifndef NOVA_JSON_H
#define NOVA_JSON_H

typedef enum { N_NULL, N_BOOL, N_NUM, N_STR, N_ARR, N_OBJ } nv_type;

typedef struct nv nv;
struct nv {
    nv_type t;
    double  num;
    char*   str;      /* N_STR */
    int     bl;       /* N_BOOL */
    nv**    items;    /* N_ARR */
    int     count;
    struct nv_obj_entry* obj;  /* N_OBJ */
};

typedef struct nv_obj_entry { char* k; nv* v; } nv_obj_entry;

/* 解析完整 JSON 文档，失败返回 NULL */
nv* nv_parse(const char* text);
void nv_free(nv* v);

/* 路径查询："a.b[0].c"，找不到返回 NULL */
nv* nv_get(const nv* v, const char* path);
const char* nv_str(const nv* v);       /* 字符串值或 NULL */
double nv_num(const nv* v);            /* 数值或 0 */
int nv_bool(const nv* v);              /* 布尔或 0 */
int nv_arr_count(const nv* v);         /* 数组长度或 0 */
nv* nv_arr_at(const nv* v, int i);     /* 数组元素或 NULL */

/* 便捷：根上取字符串/数值 */
const char* nv_str_at(const nv* root, const char* path);
double nv_num_at(const nv* root, const char* path);

#endif
