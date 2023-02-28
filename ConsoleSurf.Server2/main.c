#include <termios.h>
#include <sys/ioctl.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <unistd.h>
#include "wsServer/include/ws.h"
#include <fcntl.h>
#include <string.h>
#include <linux/types.h>
#include <linux/kd.h>
#include <time.h>
#include <unistd.h>
#include <pthread.h>
#include <arpa/inet.h>
#include <dirent.h>

#undef MAX_CLIENTS
#define MAX_CLIENTS 256

#define CLIENT_AUTHENTICATE 0
#define CLIENT_INPUT 1

#define SERVER_AUTHENTICATION_ERROR 0
#define SERVER_CONSOLE_NOT_FOUND_ERROR 1
#define SERVER_CONSOLE 2
#define SERVER_FULL_ERROR 3

#define RATE_LIMITER_PERIOD 3

char authKey[37];
pthread_t threads[256];
int threads_top = 0;

char clientCancellationTokens[256];
int clientFileDescriptors[256];
int clients_top = 0;

int rateLimiterDates[256];
char* rateLimiterIps[256];
int rate_limiter_top = 0;

struct render_args {
    int fileDescriptor;
    int length;
    long frameInterval;
    char* cancellationToken;
    ws_cli_conn_t* client;
};

struct ws_connection {
    int client_sock; /**< Client socket FD.        */
    int state;       /**< WebSocket current state. */

    /* Timeout thread and locks. */
    pthread_mutex_t mtx_state;
    pthread_cond_t cnd_state_close;
    pthread_t thrd_tout;
    bool close_thrd;

    /* Send lock. */
    pthread_mutex_t mtx_snd;

    /* IP address. */
    char ip[INET6_ADDRSTRLEN];

    /* Ping/Pong IDs and locks. */
    int32_t last_pong_id;
    int32_t current_ping_id;
    pthread_mutex_t mtx_ping;
};
ws_cli_conn_t* _client_socks_location;
int firstConnection = 1;

// We are doing an address comparison between the mem address of this specific client, and the memory address
//  of the containing client socks array client (_client_socks_location + i). Easier than struct comparison.
int get_client_index(ws_cli_conn_t* client) {
    for (int i = 0; i < clients_top; i++) {
        if (_client_socks_location + i == client) {
            return i;
        }
    }

    return -1;
}

int* get_client_file_descriptor(ws_cli_conn_t* client) {
    for (int i = 0; i < clients_top; i++) {
        if (_client_socks_location + i == client) {
            return &clientFileDescriptors[i];
        }
    } 
}

char* get_client_cancellation_token(ws_cli_conn_t* client) {
    for (int i = 0; i < clients_top; i++) {
        if (_client_socks_location + i == client) {
            return &clientCancellationTokens[i];
        }
    } 
}

void* render_loop(void* args) {
    struct render_args* r_args = (struct render_args*) args;
    struct timespec sleepTime;
    sleepTime.tv_sec = 0;
    sleepTime.tv_nsec = r_args->frameInterval;

    char* buffer = malloc(r_args->length + 1);
    buffer[0] = SERVER_CONSOLE;

    while (*(r_args->cancellationToken) == 1) {
        read(r_args->fileDescriptor, buffer + 1, (unsigned int) r_args->length);
        ws_sendframe_bin(r_args->client, buffer, r_args->length);
        nanosleep(&sleepTime, NULL);
    }

    return NULL;
}

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

int flength(FILE* file) {
    fseek(file, 0, SEEK_END);
    int size = ftell(file);
    fseek(file, 0, SEEK_SET);
    return size;
}

// HACK: Since we are unable to acess the array of connected clients in wsserver, instead we capture the
// address of the first client that connects, to get the array address (as clients originate from that array)
void onopen(ws_cli_conn_t *client) {
    if (firstConnection == 1) {
        _client_socks_location = client;
        firstConnection = 0;
    }
}

void onmessage(ws_cli_conn_t *client, const unsigned char *msg, uint64_t size, int type) {
    if (msg[0] == (char) CLIENT_AUTHENTICATE) {
        if (clients_top >= 255) {
            printf("Client overflow error - server can not handle more than 256 concurrently connected clients\n");
            char err = SERVER_FULL_ERROR;
            ws_sendframe_bin(client, &err, 1);
            return;
        }
        
        char clientAuthKey[37];
        memcpy(clientAuthKey, msg + 1, 36);
        clientAuthKey[36] = '\0';

        if (size < 44 || strcmp(clientAuthKey, authKey) != 0) {
            char err = SERVER_AUTHENTICATION_ERROR;
            ws_sendframe_bin(client, &err, 1);
            return;
        }

        long frameInterval = 1000000000L / (msg[37] < 1 ? 1 : (msg[37] < (60) ? msg[37] : 60));
        char* consolePath = malloc(size - 36); // example: 46 - 36 = 10
        consolePath[size - 37] = '\0'; //consolePath[9] = '\0'
        memcpy(consolePath, msg + 38, size - 37); // copy 9

        // Read console display into buffer, with read write perms
        int fileDescriptor = open(consolePath, O_RDWR);
        FILE *fptr = fopen(consolePath, "r");
        if (fileDescriptor == -1 || fptr == NULL) {
            DIR* dir = opendir("/dev/");
            struct dirent* entry;
            int dir_length = 0;
            int dir_count = 0;
            while ((entry = readdir(dir)) != NULL) {
                if (strncmp("vc", entry->d_name, 2) != 0 && strncmp("tty", entry->d_name, 3) != 0) {
                    continue;
                }

                dir_count++;
                dir_length += strlen(entry->d_name);
            }

            char* err = malloc(dir_length + (dir_count * 6) + 1);
            err[0] = (char) SERVER_CONSOLE_NOT_FOUND_ERROR;
            int err_length = 1;

            while ((entry = readdir(dir)) != NULL) {
                if (strncmp("vc", entry->d_name, 2) != 0 && strncmp("tty", entry->d_name, 3) != 0) {
                    continue;
                }

                int name_len = strlen(entry->d_name);
                memcpy(err + err_length, " /dev/", 6);
                memcpy(err + err_length + 6, &(entry->d_name), name_len);
                err_length += name_len + 6;
            }

            for (int i = 0; i < err_length; i++) {
                printf("%c", err[i]);
            }

            ws_sendframe_bin(client, err, err_length);
            closedir(dir);
            return;
        }

        if (strncmp("/dev/tty", consolePath, 8) == 0) {
            // Make sure terminal is in canonical mode
            struct termios tio;
            if (tcgetattr(fileDescriptor, &tio) == -1) {
                perror("TTY is not in canonical mode, switching");
            }

            tio.c_iflag |= ICANON;

            if (tcsetattr(fileDescriptor, TCSANOW, &tio) == -1) {
                perror("TTY not set terminal to canonical mode");
            }
        }
        
        // Create a new render task thread on the stack
        struct render_args* args = malloc(sizeof(struct render_args));
        args->fileDescriptor = fileDescriptor;
        args->length = flength(fptr);
        args->frameInterval = frameInterval;
        args->cancellationToken = &clientCancellationTokens[clients_top];
        args->client = client;

        pthread_create(threads + threads_top, NULL, render_loop, args);
        threads_top++;

        clientCancellationTokens[clients_top] = 1;
        clientFileDescriptors[clients_top] = fileDescriptor;
        clients_top++;
    }
    else if (msg[0] == (char) CLIENT_INPUT) {
        int index = get_client_index(client);
        if (size != 2 || index == -1) {
            char err = SERVER_AUTHENTICATION_ERROR;
            ws_sendframe_bin(client, &err, 1);
            return;
        }

        int* tty_fd = get_client_file_descriptor(client);
        unsigned int mode = 0;

        ioctl((*tty_fd), KDGKBMODE, &mode);
        if (mode != K_XLATE && mode != K_UNICODE) {          
            if (ioctl(*tty_fd, KDSKBMODE, K_UNICODE) == -1) {
                perror("Error switching keyboard state");
            }
        }
        
        if (ioctl((*tty_fd), TIOCSTI, msg + 1) == -1) {
            perror("Error pushing input char into console");
        }
    }
}

void onclose(ws_cli_conn_t *client) {
    int index = get_client_index(client);
    if (index == -1) {
        return;
    }

    *get_client_cancellation_token(client) = 0;
    // Splice this client out of the client cancellation token and file descriptor array
    memmove(&clientCancellationTokens + index, &clientCancellationTokens + index - 1, clients_top - index - 1);
    memmove(&clientFileDescriptors + index * 4, &clientFileDescriptors + index * 4 - 4, clients_top - (index * 4) - 4);
    clients_top--;
}

char* generate_auth_key() {
    static char base16_chars[16] = "0123456789abcdef";
    static char buf[37];

    //gen random for all spaces because lazy
    for (int i = 0; i < 36; i++) {
        buf[i] = base16_chars[rand() % 16];
    }

    buf[8] = '-';
    buf[13] = '-';
    buf[18] = '-';
    buf[23] = '-';
    buf[36] = '\0';
    return buf;
}

int main() {
    if (getuid() != 0) {
        printf("Server must be run with [sudo]/administrator privileges.\n");
        return 0;
    }

    FILE *fptr = fopen("authkey.txt", "rb+");

    if (fptr == NULL || flength(fptr) != 36) {
        fptr = fopen("authkey.txt", "wb");

        char cwd[256];
        getcwd(cwd, sizeof(cwd));
        fwrite(generate_auth_key(), 1, 36, fptr);
        printf("Created auth key file! A secure, randomly generated UUID has"
               "been placed into this file for use of client authentication. You may"
               "replace this key with your own (must be of length 36) by modifying the file ");
        printf("'%s/authkey.txt'min.\n", cwd);
    }

    fread(authKey, 1, 36, fptr);
    authKey[36] = '\0';

	struct ws_events evs;
    evs.onopen = &onopen;
	evs.onclose = &onclose;
	evs.onmessage = &onmessage;
	ws_socket(&evs, 8080, 0, 1000);
    
	return 0;
}
