#include "nova_thread.h"

#ifdef _WIN32
int nv_thread_create(nv_thread_t* t, nv_thread_fn fn, void* arg) {
    *t = CreateThread(NULL, 0, fn, arg, 0, NULL);
    return *t ? 0 : -1;
}
void nv_thread_join(nv_thread_t t) { WaitForSingleObject(t, INFINITE); CloseHandle(t); }
void nv_mutex_init(nv_mutex_t* m) { InitializeCriticalSection(m); }
void nv_mutex_lock(nv_mutex_t* m) { EnterCriticalSection(m); }
void nv_mutex_unlock(nv_mutex_t* m) { LeaveCriticalSection(m); }
void nv_mutex_destroy(nv_mutex_t* m) { DeleteCriticalSection(m); }
void nv_sleep_ms(int ms) { Sleep((DWORD)ms); }
#else
#include <unistd.h>
#include <time.h>
int nv_thread_create(nv_thread_t* t, nv_thread_fn fn, void* arg) {
    return pthread_create(t, NULL, fn, arg);
}
void nv_thread_join(nv_thread_t t) { pthread_join(t, NULL); }
void nv_mutex_init(nv_mutex_t* m) { pthread_mutex_init(m, NULL); }
void nv_mutex_lock(nv_mutex_t* m) { pthread_mutex_lock(m); }
void nv_mutex_unlock(nv_mutex_t* m) { pthread_mutex_unlock(m); }
void nv_mutex_destroy(nv_mutex_t* m) { pthread_mutex_destroy(m); }
void nv_sleep_ms(int ms) {
    struct timespec ts;
    ts.tv_sec = ms / 1000;
    ts.tv_nsec = (long)(ms % 1000) * 1000000L;
    nanosleep(&ts, NULL);
}
#endif
