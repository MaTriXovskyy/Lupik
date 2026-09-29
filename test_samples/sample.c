#include <stdio.h>

int main(void) {
    const char *formats[] = {"png", "pdf", "mp4"};
    for (int i = 0; i < 3; i++) printf("%s
", formats[i]);
    return 0;
}
