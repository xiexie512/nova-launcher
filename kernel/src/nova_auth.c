#include "nova_kernel.h"
#include "nova_util.h"
#include "nova_json.h"
#include "nova_http.h"
#include "nova_md5.h"

/* JSON 字符串转义追加 */
static void jstr(nv_buf* b, const char* s) {
    nv_buf_append_char(b, '"');
    if (s) for (const char* p = s; *p; p++) {
        switch (*p) {
            case '"': nv_buf_append(b, "\\\""); break;
            case '\\': nv_buf_append(b, "\\\\"); break;
            case '\n': nv_buf_append(b, "\\n"); break;
            case '\r': nv_buf_append(b, "\\r"); break;
            case '\t': nv_buf_append(b, "\\t"); break;
            default:
                if ((unsigned char)*p < 0x20) { char t[8]; snprintf(t, sizeof(t), "\\u%04x", (unsigned)*p); nv_buf_append(b, t); }
                else nv_buf_append_char(b, *p);
        }
    }
    nv_buf_append_char(b, '"');
}

/* ---------------- 离线 UUID (MD5 v3) ---------------- */
char* nova_offline_uuid(const char* player_name, char** err) {
    if (!player_name || !*player_name) {
        if (err) *err = nv_strdup("empty player name");
        return NULL;
    }
    nv_buf in; nv_buf_init(&in);
    nv_buf_append(&in, "OfflinePlayer:");
    nv_buf_append(&in, player_name);
    unsigned char d[16];
    nv_md5((const unsigned char*)in.data, in.len, d);
    nv_buf_free(&in);
    d[6] = (unsigned char)((d[6] & 0x0F) | 0x30); /* version 3 */
    d[8] = (unsigned char)((d[8] & 0x3F) | 0x80); /* variant */
    char* s = (char*)nv_xmalloc(37);
    snprintf(s, 37, "%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x",
             d[0], d[1], d[2], d[3], d[4], d[5], d[6], d[7], d[8], d[9], d[10], d[11], d[12], d[13], d[14], d[15]);
    return s;
}

/* ---------------- Yggdrasil ---------------- */
char* nova_yggdrasil_login(const char* auth_server, const char* username,
                           const char* password, const char* client_token,
                           int timeout_ms, char** err) {
    if (!auth_server || !username || !password) {
        if (err) *err = nv_strdup("missing yggdrasil params");
        return NULL;
    }
    /* 规范化 server：去掉尾部斜杠，补 https:// */
    nv_buf server; nv_buf_init(&server);
    if (!nv_starts_with(auth_server, "http://") && !nv_starts_with(auth_server, "https://"))
        nv_buf_append(&server, "https://");
    nv_buf_append(&server, auth_server);
    while (server.len && (server.data[server.len-1] == '/' || server.data[server.len-1] == '\\'))
        server.data[--server.len] = 0;

    nv_buf body; nv_buf_init(&body);
    nv_buf_append(&body, "{\"agent\":{\"name\":\"NovaLauncher\",\"version\":1},\"username\":");
    jstr(&body, username);
    nv_buf_append(&body, ",\"password\":");
    jstr(&body, password);
    nv_buf_append(&body, ",\"clientToken\":");
    jstr(&body, client_token ? client_token : nv_random_uuid());
    nv_buf_append(&body, "}");
    char* body_s = nv_buf_detach(&body);

    nv_buf url; nv_buf_init(&url);
    nv_buf_append(&url, server.data);
    nv_buf_append(&url, "/authserver/authenticate");
    char* err2 = NULL;
    char* resp = nv_http_request("POST", url.data,body_s,"application/json",NULL,timeout_ms, &err2);
    free(body_s);
    nv_buf_free(&url);
    if (!resp) {
        if (err) *err = err2 ? err2 : nv_strdup("yggdrasil request failed");
        else free(err2);
        nv_buf_free(&server);
        return NULL;
    }
    nv* r = nv_parse(resp);
    nv_buf_free(&server);
    if (!r) {
        if (err) *err = nv_strdup("yggdrasil bad response");
        free(resp);
        return NULL;
    }
    const char* at = nv_str_at(r, "accessToken");
    const char* ct = nv_str_at(r, "clientToken");
    const char* pid = nv_str_at(r, "selectedProfile.id");
    const char* pname = nv_str_at(r, "selectedProfile.name");
    if (!at || !pid || !pname) {
        const char* em = nv_str_at(r, "errorMessage");
        if (err) *err = nv_strdup(em ? em : "yggdrasil auth failed");
        nv_free(r);
        free(resp);
        return NULL;
    }
    nv_buf out; nv_buf_init(&out);
    nv_buf_append(&out, "{\"accessToken\":");
    jstr(&out, at);
    nv_buf_append(&out, ",\"clientToken\":");
    jstr(&out, ct ? ct : "");
    nv_buf_append(&out, ",\"uuid\":");
    jstr(&out, pid);
    nv_buf_append(&out, ",\"name\":");
    jstr(&out, pname);
    nv_buf_append(&out, "}");
    char* res = nv_buf_detach(&out);
    nv_free(r);
    free(resp);
    return res;
}

/* ---------------- Microsoft ---------------- */
#define MS_DEVICE_URL "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode"
#define MS_TOKEN_URL  "https://login.microsoftonline.com/consumers/oauth2/v2.0/token"
#define MS_XBL_URL    "https://user.auth.xboxlive.com/user/authenticate"
#define MS_XSTS_URL   "https://xsts.auth.xboxlive.com/xsts/authorize"
#define MS_MC_LOGIN   "https://api.minecraftservices.com/authentication/login_with_xbox"
#define MS_MC_PROFILE "https://api.minecraftservices.com/minecraft/profile"

static char* form_urlencode(const char* s) {
    nv_buf b; nv_buf_init(&b);
    for (const char* p = s; *p; p++) {
        unsigned char c = (unsigned char)*p;
        if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ||
            c == '-' || c == '_' || c == '.' || c == '~') nv_buf_append_char(&b, (char)c);
        else { char t[4]; snprintf(t, sizeof(t), "%%%02X", c); nv_buf_append(&b, t); }
    }
    return nv_buf_detach(&b);
}

char* nova_microsoft_device_code(const char* client_id, int timeout_ms, char** err) {
    if (!client_id || !*client_id) { if (err) *err = nv_strdup("client_id required (Azure app registration)"); return NULL; }
    char* cid = form_urlencode(client_id);
    nv_buf body; nv_buf_init(&body);
    nv_buf_append(&body, "client_id=");
    nv_buf_append(&body, cid);
    nv_buf_append(&body, "&scope=XboxLive.signin%20offline_access");
    free(cid);
    char* b = nv_buf_detach(&body);
    char* resp = nv_http_request("POST", MS_DEVICE_URL,b,"application/x-www-form-urlencoded",NULL,timeout_ms, err);
    free(b);
    return resp;
}

char* nova_microsoft_poll_token(const char* client_id, const char* device_code,
                                int timeout_ms, char** err) {
    if (!client_id || !device_code) { if (err) *err = nv_strdup("missing params"); return NULL; }
    char* cid = form_urlencode(client_id);
    char* dc = form_urlencode(device_code);
    nv_buf body; nv_buf_init(&body);
    nv_buf_append(&body, "grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code&client_id=");
    nv_buf_append(&body, cid);
    nv_buf_append(&body, "&device_code=");
    nv_buf_append(&body, dc);
    free(cid); free(dc);
    char* b = nv_buf_detach(&body);
    char* resp = nv_http_request("POST", MS_TOKEN_URL,b,"application/x-www-form-urlencoded",NULL,timeout_ms, err);
    free(b);
    return resp;
}

char* nova_microsoft_finish(const char* access_token, int timeout_ms, char** err) {
    if (!access_token) { if (err) *err = nv_strdup("missing access_token"); return NULL; }

    /* 1. Xbox Live 认证 */
    nv_buf b1; nv_buf_init(&b1);
    nv_buf_append(&b1, "{\"Properties\":{\"AuthMethod\":\"RPS\",\"SiteName\":\"user.auth.xboxlive.com\",\"RpsTicket\":\"d=");
    nv_buf_append(&b1, access_token);
    nv_buf_append(&b1, "\"},\"RelyingParty\":\"http://auth.xboxlive.com\",\"TokenType\":\"JWT\"}");
    char* s1 = nv_buf_detach(&b1);
    char* err1 = NULL;
    char* r1 = nv_http_request("POST", MS_XBL_URL,s1,"application/json",NULL,timeout_ms, &err1);
    free(s1);
    if (!r1) { if (err) *err = err1 ? err1 : nv_strdup("xbl auth failed"); else free(err1); return NULL; }
    nv* j1 = nv_parse(r1);
    const char* xbl_token = j1 ? nv_str_at(j1, "Token") : NULL;
    if (!xbl_token) {
        if (err) *err = nv_strdup("xbl auth: no token");
        nv_free(j1); free(r1);
        return NULL;
    }

    /* 2. XSTS */
    nv_buf b2; nv_buf_init(&b2);
    nv_buf_append(&b2, "{\"Properties\":{\"SandboxId\":\"RETAIL\",\"UserTokens\":[\"");
    nv_buf_append(&b2, xbl_token);
    nv_buf_append(&b2, "\"]},\"RelyingParty\":\"rp://api.minecraftservices.com/\",\"TokenType\":\"JWT\"}");
    char* s2 = nv_buf_detach(&b2);
    nv_free(j1); free(r1);
    char* err2 = NULL;
    char* r2 = nv_http_request("POST", MS_XSTS_URL,s2,"application/json",NULL,timeout_ms, &err2);
    free(s2);
    if (!r2) { if (err) *err = err2 ? err2 : nv_strdup("xsts failed"); else free(err2); return NULL; }
    nv* j2 = nv_parse(r2);
    const char* xsts_token = j2 ? nv_str_at(j2, "Token") : NULL;
    const char* uhs = j2 ? nv_str_at(j2, "DisplayClaims.xui[0].uhs") : NULL;
    if (!xsts_token || !uhs) {
        const char* xerr = j2 ? nv_str_at(j2, "XErr") : NULL;
        const char* xmsg = j2 ? nv_str_at(j2, "Message") : NULL;
        if (err) {
            nv_buf e; nv_buf_init(&e);
            nv_buf_append(&e, "xsts failed: ");
            if (xerr) { nv_buf_append(&e, "XErr="); nv_buf_append(&e, xerr); nv_buf_append(&e, " "); }
            nv_buf_append(&e, xmsg ? xmsg : "");
            *err = nv_buf_detach(&e);
        }
        nv_free(j2); free(r2);
        return NULL;
    }

    /* 3. Minecraft 登录 */
    nv_buf b3; nv_buf_init(&b3);
    nv_buf_append(&b3, "{\"identityToken\":\"XBL3.0 x=");
    nv_buf_append(&b3, uhs);
    nv_buf_append(&b3, ";");
    nv_buf_append(&b3, xsts_token);
    nv_buf_append(&b3, "\"}");
    char* s3 = nv_buf_detach(&b3);
    nv_free(j2); free(r2);
    char* err3 = NULL;
    char* r3 = nv_http_request("POST", MS_MC_LOGIN,s3,"application/json",NULL,timeout_ms, &err3);
    free(s3);
    if (!r3) { if (err) *err = err3 ? err3 : nv_strdup("mc login failed"); else free(err3); return NULL; }
    nv* j3 = nv_parse(r3);
    const char* mc_token = j3 ? nv_str_at(j3, "access_token") : NULL;
    const char* expires = j3 ? nv_str_at(j3, "expires_in") : NULL;
    if (!mc_token) {
        if (err) *err = nv_strdup("mc login: no access_token");
        nv_free(j3); free(r3);
        return NULL;
    }
    /* 4. 获取档案 */
    char* auth_hdr = NULL;
    nv_buf ah; nv_buf_init(&ah);
    nv_buf_append(&ah, "Authorization: Bearer ");
    nv_buf_append(&ah, mc_token);
    auth_hdr = nv_buf_detach(&ah);
    char* err4 = NULL;
    char* r4 = nv_http_request("GET", MS_MC_PROFILE, NULL, NULL, auth_hdr, timeout_ms, &err4);
    free(auth_hdr);
    if (!r4) {
        if (err) *err = err4 ? err4 : nv_strdup("profile fetch failed");
        else free(err4);
        nv_free(j3); free(r3);
        return NULL;
    }
    nv* j4 = nv_parse(r4);
    const char* uuid = j4 ? nv_str_at(j4, "id") : NULL;
    const char* name = j4 ? nv_str_at(j4, "name") : NULL;
    nv_buf out; nv_buf_init(&out);
    nv_buf_append(&out, "{\"accessToken\":");
    jstr(&out, mc_token);
    nv_buf_append(&out, ",\"uuid\":");
    jstr(&out, uuid ? uuid : "");
    nv_buf_append(&out, ",\"name\":");
    jstr(&out, name ? name : "");
    nv_buf_append(&out, ",\"expiresIn\":");
    nv_buf_append(&out, expires ? expires : "0");
    nv_buf_append(&out, "}");
    char* res = nv_buf_detach(&out);
    nv_free(j3); free(r3);
    nv_free(j4); free(r4);
    return res;
}
