#include <stdlib.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
static _Alignas(64) unsigned char arena[64*1024*1024];
static size_t cursor;
void reset_arena(void) {cursor=0;}
static void *alloc_temp(int64_t size, int align, int allocator, int hint) {
    if(allocator!=2 || align<1 || (align & (align-1)) || size<0)abort();
    size_t at=(cursor+align-1)&~(size_t)(align-1);
    cursor=at+size;
    if(cursor>sizeof(arena))abort();
    return arena+at;
}
static void free_temp(void *ptr, int allocator) {if(allocator!=2 || (ptr && ((uint8_t*)ptr<arena||(uint8_t*)ptr>=arena+sizeof(arena))))abort();}
static int thread_count(void){return 32;}
static void *shared(const void *key, unsigned bytes, unsigned align) {
    void *ptr;
    if(posix_memalign(&ptr, align < sizeof(void*) ? sizeof(void*) : align, bytes))abort();
    memset(ptr,0,bytes);
    return ptr;
}
void* resolve_external(const char *n) {
    if(!strcmp(n,"Unity.Jobs.LowLevel.Unsafe.JobsUtility::get_ThreadIndexCount"))return thread_count;
    if(!strcmp(n,"Unity.Collections.LowLevel.Unsafe.UnsafeUtility::MallocTracked"))return alloc_temp;
    if(!strcmp(n,"Unity.Collections.LowLevel.Unsafe.UnsafeUtility::FreeTracked"))return free_temp;
    if(!strcmp(n,"Unity.Burst.LowLevel.BurstCompilerService::GetOrCreateSharedMemory"))return shared;
    fprintf(stderr,"Unavailable external %s\n",n);abort();
}
