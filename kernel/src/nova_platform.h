#ifndef NOVA_PLATFORM_H
#define NOVA_PLATFORM_H

#ifdef _WIN32
  #include <windows.h>
  #include <direct.h>
  #define nv_mkdir(p) _mkdir(p)
  #define nv_getpid() ((long)GetCurrentProcessId())
  #define PATH_SEP '\\'
#else
  #include <sys/stat.h>
  #include <unistd.h>
  #define nv_mkdir(p) mkdir(p, 0755)
  #define nv_getpid() ((long)getpid())
  #define PATH_SEP '/'
#endif

#endif
