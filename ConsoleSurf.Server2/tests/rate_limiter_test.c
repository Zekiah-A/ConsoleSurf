#include <stdio.h>
#include <string.h>
#include <time.h>
#include <unistd.h>

#define RATE_LIMITER_PERIOD 3

int rateLimiterDates[256];
char* rateLimiterIps[256];
int rate_limiter_top;

int rate_limiter_authorised(char* ip, int extendIfNot) {
    int foundIndex = -1;
    int currentTime = time(NULL);

    for (int i = 0; i < 256; i++) {
        if (rateLimiterIps[i] != NULL && strcmp(ip, rateLimiterIps[i]) == 0) {
            foundIndex = i;
        }
    }

    if (foundIndex == -1) {

        rateLimiterIps[rate_limiter_top] = ip;
        rateLimiterDates[rate_limiter_top] = currentTime;
        rate_limiter_top++;
        return 1;
    }

    if (currentTime - rateLimiterDates[foundIndex] < RATE_LIMITER_PERIOD) {
        if (extendIfNot == 1) {
            rateLimiterDates[foundIndex] = currentTime;
        }
        
        return 0;
    }

    rateLimiterDates[foundIndex] = currentTime;
    return 1;
}

int main() {
    printf("First time it sees this IP, should be authorised\n");
    if (rate_limiter_authorised("127.0.0.1", 0) == 1) {
        printf("Authorised\n");
    }
    else {
        printf("Not authorised\n");
    }
    printf("Sleep 1 - Should not be authorised\n");
    sleep(1);
    if (rate_limiter_authorised("127.0.0.1", 0) == 1) {
        printf("Authorised\n");
    }
    else {
        printf("Not authorised\n");
    }
    printf("Sleep 4 - Should be authorised\n");
    sleep(4);
    if (rate_limiter_authorised("127.0.0.1", 0) == 1) {
        printf("Authorised\n");
    }
    else {
        printf("Not authorised\n");
    }
    printf("Different IP address, should not segfault & should authorise\n");
    if (rate_limiter_authorised("192.168.1.1", 0) == 1) {
        printf("Authorised\n");
    }
    else {
        printf("Not authorised\n");
    }


    return 0;
}