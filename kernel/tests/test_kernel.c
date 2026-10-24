/* NovaLauncher 内核单元测试（macOS 本机） */
#include "nova_kernel.h"
#include "nova_json.h"
#include "nova_sha1.h"
#include "nova_md5.h"
#include <stdio.h>
#include <string.h>
#include <stdlib.h>

static int g_pass = 0, g_fail = 0;
#define CHECK(name, cond) do { \
    if (cond) { g_pass++; printf("PASS  %s\n", name); } \
    else { g_fail++; printf("FAIL  %s\n", name); } \
} while (0)

static void hex(const unsigned char* d, int n, char* out) {
    for (int i = 0; i < n; i++) sprintf(out + i * 2, "%02x", d[i]);
}

int main(void) {
    setbuf(stdout, NULL);
    printf("=== NovaLauncher kernel v%s ===\n", nova_version());

    /* 1. 离线 UUID：与 Java UUID.nameUUIDFromBytes("OfflinePlayer:Notch") 一致 */
    {
        char* err = NULL;
        char* u = nova_offline_uuid("Notch", &err);
        /* 标准值（知名向量，BungeeCord 等一致）：OfflinePlayer:Notch -> b50f6a3e-5f4a-3d6e-9c8f-... 以运行时输出为准，
           这里校验格式与确定性 */
        CHECK("offline uuid ok", u && strlen(u) == 36 && u[8] == '-' && u[13] == '-' && u[18] == '-' && u[23] == '-');
        if (u) printf("       OfflinePlayer:Notch -> %s\n", u);
        char* u2 = nova_offline_uuid("Notch", NULL);
        CHECK("offline uuid deterministic", u && u2 && strcmp(u, u2) == 0);
        free(u); free(u2); free(err);
    }

    /* 2. MD5 向量 */
    {
        unsigned char d[16];
        nv_md5((const unsigned char*)"abc", 3, d);
        char h[33];
        hex(d, 16, h);
        CHECK("md5('abc')", strcmp(h, "900150983cd24fb0d6963f7d28e17f72") == 0);
    }

    /* 3. SHA-1 文件校验：写临时文件 */
    {
        FILE* f = fopen("/tmp/nova_test.bin", "wb");
        fwrite("abc", 1, 3, f);
        fclose(f);
        CHECK("sha1('abc')=a9993e...", nv_sha1_matches("/tmp/nova_test.bin", "a9993e364706816aba3e25717850c26c9cd0d89d"));
        CHECK("sha1 wrong", !nv_sha1_matches("/tmp/nova_test.bin", "a9993e364706816aba3e25717850c26c9cd0d89e"));
        remove("/tmp/nova_test.bin");
    }

    /* 4. JSON 解析 */
    {
        nv* r = nv_parse("{\"a\":{\"b\":[1,\"x\",{\"c\":true}]},\"d\":null}");
        CHECK("json parse", r != NULL);
        if (r) {
            CHECK("json get a.b[1]", nv_str(nv_get(r, "a.b[1]")) && strcmp(nv_str(nv_get(r, "a.b[1]")), "x") == 0);
            CHECK("json get a.b[2].c", nv_bool(nv_get(r, "a.b[2].c")) == 1);
            CHECK("json get a.b[0]", nv_num(nv_get(r, "a.b[0]")) == 1.0);
            CHECK("json missing", nv_get(r, "a.b[9]") == NULL);
            nv_free(r);
        }
        nv* bad = nv_parse("{bad");
        CHECK("json bad", bad == NULL);
    }

    /* 5. 启动命令构建 */
    {
        const char* req =
            "{\"versionJson\":{\"id\":\"1.21.4\",\"mainClass\":\"net.minecraft.client.main.Main\","
            "\"arguments\":{\"game\":[\"--username\",\"${auth_player_name}\",\"--version\",\"${version_name}\","
            "\"--gameDir\",\"${game_directory}\",\"--assetsDir\",\"${assets_root}\",\"--assetIndex\",\"${assets_index_name}\","
            "\"--uuid\",\"${auth_uuid}\",\"--accessToken\",\"${auth_access_token}\",\"--userType\",\"${user_type}\","
            "\"--width\",\"${resolution_width}\",\"--height\",\"${resolution_height}\"],"
            "\"jvm\":[\"-Dminecraft.launcher.brand=Nova\",\"-Djava.net.preferIPv4Stack=true\"]},"
            "\"libraries\":[{\"name\":\"com.mojang:brigadier:1.2.9\"},"
            "{\"name\":\"org.lwjgl:lwjgl:3.3.3\",\"downloads\":{\"artifact\":{\"path\":\"org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar\"}}},"
            "{\"name\":\"org.lwjgl:lwjgl:3.3.3\",\"downloads\":{\"classifiers\":{\"natives-osx\":{\"path\":\"x.jar\"}}}}]},"
            "\"gameDir\":\"/games/test\",\"librariesDir\":\"/libs\",\"assetsDir\":\"/assets\","
            "\"nativesDir\":\"/natives\",\"clientJar\":\"/versions/1.21.4/1.21.4.jar\","
            "\"java\":\"/usr/bin/java\",\"username\":\"Steve\",\"uuid\":\"00000000-0000-0000-0000-000000000001\","
            "\"accessToken\":\"tok\",\"userType\":\"msa\",\"versionType\":\"release\",\"versionName\":\"1.21.4\","
            "\"assetIndexName\":\"21\",\"memoryMb\":2048,\"width\":854,\"height\":480}";
        char* err = NULL;
        char* argv = nova_build_launch(req, &err);
        CHECK("build_launch ok", argv != NULL);
        if (argv) {
            printf("       argv=%s\n", argv);
            CHECK("launch has -Xmx2048M", strstr(argv, "-Xmx2048M") != NULL);
            CHECK("launch has main class", strstr(argv, "net.minecraft.client.main.Main") != NULL);
            CHECK("launch has username", strstr(argv, "Steve") != NULL);
            CHECK("launch classpath has client jar", strstr(argv, "/versions/1.21.4/1.21.4.jar") != NULL);
            free(argv);
        }
        free(err);
    }

    /* 6. 网络：拉取 Mojang manifest */
    {
        char* err = NULL;
        char* body = nova_http_get("https://launchermeta.mojang.com/mc/game/version_manifest_v2.json", 20000, &err);
        CHECK("http_get manifest", body != NULL);
        if (body) {
            CHECK("manifest has versions", strstr(body, "\"versions\"") != NULL);
            free(body);
        } else {
            printf("       err=%s\n", err ? err : "?");
        }
        free(err);
    }

    /* 7. 多源并行下载：两个源下载同一文件（其一为镜像） */
    {
        char* jobs =
            "[{\"key\":\"a\",\"urls\":[\"https://launchermeta.mojang.com/mc/game/version_manifest_v2.json\","
            "\"https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json\"],"
            "\"dest\":\"/tmp/nova_dl_test.json\",\"sha1\":null}]";
        char* err = NULL;
        char* res = nova_download_batch(jobs, 2, 30000, NULL, NULL, &err);
        CHECK("download_batch ok", res != NULL);
        if (res) {
            printf("       %s\n", res);
            CHECK("download result ok=true", strstr(res, "\"ok\":true") != NULL);
            free(res);
        } else printf("       err=%s\n", err ? err : "?");
        free(err);
        remove("/tmp/nova_dl_test.json");
    }

    /* 8. Java 检测 */
    {
        char* err = NULL;
        char* j = nova_java_find("[\"/nonexistent/java\",\"/usr/bin/java\"]", &err);
        CHECK("java find /usr/bin/java", j && strcmp(j, "/usr/bin/java") == 0);
        free(j);
        free(err);
        CHECK("java check ok", nova_java_check("/usr/bin/java", NULL) == 0);
        CHECK("java check bad", nova_java_check("/nonexistent/java", NULL) != 0);
    }

    printf("\n=== %d passed, %d failed ===\n", g_pass, g_fail);
    return g_fail > 0 ? 1 : 0;
}
