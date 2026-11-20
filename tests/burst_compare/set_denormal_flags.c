#include <xmmintrin.h>

/* Linux x64 fixture only. Jobs verifies SSE4.2 before calling this on the
   thread that invokes both original and replacement Burst entry points. */
int set_denormal_flags(int flags)
{
    const unsigned mask = 0x8040u; /* FTZ (15) and DAZ (6) */
    _mm_setcsr((_mm_getcsr() & ~mask) | ((unsigned)flags & mask));
    return (int)(_mm_getcsr() & mask);
}
