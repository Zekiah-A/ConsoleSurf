// I can't import the TIOCSTI macro into C#, so instead I use a method
// within this file to grab the avlue of the TIOCSTI mmacro for use within
// the C# server software.

// Compile with
// $ gcc -c -Wall -Werror -fpic tiocsti.c
// $ gcc -shared -o tiocsti.so tiocsti.o
#include <sys/ioctl.h>

extern int get_tiocsti() {
    return TIOCSTI;
}
