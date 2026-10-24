#ifndef NOVA_THREAD_H
#define NOVA_THREAD_H

#ifdef _WIN32
  #include <windows.h>
  typedef HANDLE nv_thread_t;
  typedef CRITICAL_SECTION nv_mutex_t;
  #define NV_THREAD_RET DWORD WINAPI
  typedef LPTHREAD_START_ROUTINE nv_thread_fn;
#else
  #include <pthread.h>
  typedef pthread_t nv_thread_t;
  typedef pthread_mutex_t nv_mutex_t;
  #define NV_THREAD_RET void*
  typedef void* (*nv_thread_fn)(void*);
#endif

int nv_thread_create(nv_thread_t* t, nv_thread_fn fn, void* arg);
void nv_thread_join(nv_thread_t t);
void nv_mutex_init(nv_mutex_t* m);
void nv_mutex_lock(nv_mutex_t* m);
void nv_mutex_unlock(nv_mutex_t* m);
void nv_mutex_destroy(nv_mutex_t* m);
void nv_sleep_ms(int ms);

#endif
