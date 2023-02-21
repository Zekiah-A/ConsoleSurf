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
    int frameInterval;
    char* cancellationToken;
};

int get_client_index(ws_cli_conn_t* client) {
    for (int i = 0; i < clients_top; i++) {
        if ((ws_cli_conn_t*) client_socks + i == client) {
            return i;
        }
    }

    return -1;
}

int* get_client_file_descriptor(ws_cli_conn_t* client) {
    for (int i = 0; i < clients_top; i++) {
        if ((ws_cli_conn_t*) client_socks + i == client) {
            return (&clientFileDescriptors) + i * 4;
        }
    } 
}

char* get_client_cancellation_token(ws_cli_conn_t* client) {
    for (int i = 0; i < clients_top; i++) {
        if ((ws_cli_conn_t*) client_socks + i == client) {
            return (&clientCancellationTokens) + i;
        }
    } 
}

void render_loop(void* args) {
    struct render_args* r_args = (struct render_args*) args;
    char* buffer = malloc(r_args->length);

    // TODO: Perhaps switch while(1) to some bool for this specific client, so that we can
    // cancel the while loop externally (like how a C# cancellation token works)
    while ((*r_args->cancellationToken) == 1) {
        read(r_args->fileDescriptor, buffer, (unsigned int) r_args->length);
        ws_sendframe_bin(NULL, buffer, 1);
        sleep(r_args->frameInterval);
    }
}

int rate_limiter_authorised(char* ip, int extendIfNot) {
    // If doesn't have address already, add, true
    int foundIndex = -1;
    int currentTime = time(NULL);

    for (int i = 0; i < 256; i++) {
        if (strcmp(ip, *rateLimiterIps[i]) == 0) {
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
        
        return false;
    }

    rateLimiterDates[foundIndex] = currentTime;
    return true;
}

int flength(FILE* file) {
    fseek(file, 0, SEEK_END);
    int size = ftell(file);
    fseek(file, 0, SEEK_SET);
    return size;
}

void onmessage(ws_cli_conn_t *client, const unsigned char *msg, uint64_t size, int type) {
    if (msg[0] == (char) CLIENT_AUTHENTICATE) {
        if (clients_top >= 255) {
            perror("Client overflow error - server can not handle more than 255 concurrently connected clients");
            char err = SERVER_FULL_ERROR;
            ws_sendframe_bin(NULL, &err, 1);
            return;
        }

        char* clientAuthKey = malloc(37);
        clientAuthKey[36] = "\0";
        memcpy(msg, clientAuthKey, 36);

        if (size < 44 || strcmp(clientAuthKey, authKey) != 0) {
            char err = SERVER_AUTHENTICATION_ERROR;
            ws_sendframe_bin(NULL, &err, 1);
            return;
        }

        int frameInterval = 1000 / (msg[36] < (60) ? msg[36] : 60);
        char* consolePath = malloc(size - 36); // example: 46 - 36 = 10
        consolePath[size - 37] = "\0"; //consolePath[9] = "\0"
        memcpy(msg, consolePath, size - 37); // copy 9

        // Read console display into buffer, with read write perms
        int fileDescriptor = open(consolePath, O_RDWR);
        if (fileDescriptor == -1) {
            char err = SERVER_CONSOLE_NOT_FOUND_ERROR;
            ws_sendframe_bin(NULL, &err, 1);
            return;
        }

        // Make sure terminal is in canonical mode
        struct termios tio;
        if (tcgetattr(fileDescriptor, &tio) == -1) {
            printf("Terminal is not in canonical mode, switching\n");
        }

        tio.c_iflag |= ICANON;

        if (tcsetattr(fileDescriptor, TCSANOW, &tio) == -1) {
            printf("Could not set terminal to canonical mode\n");
        }

        FILE *fptr = fopen(consolePath, "r");
        if (fptr == NULL) {
            char err = SERVER_CONSOLE_NOT_FOUND_ERROR;
            ws_sendframe_bin(NULL, &err, 1);
            return;
        }

        // Create a new render task thread on the stack
        struct render_args* args = malloc(sizeof(struct render_args));
        args->fileDescriptor = fileDescriptor;
        args->length = flength(fptr);
        args->frameInterval = frameInterval;
        args->cancellationToken = &clientCancellationTokens + clients_top;

        pthread_create(threads + threads_top, NULL, render_loop, args);
        threads_top++;

        // TODO: Add client to a kind of dictionary, with their while condition decider (cancellation token) and file descriptor
        // Setup cancellation token.
        clientCancellationTokens[clients_top] = 1;
        clientFileDescriptors[clients_top] = fileDescriptor;
        clients_top++;
    }
    else if (msg[0] == (char) CLIENT_INPUT) {
        int index = get_client_index(client);
        if (size != 2 || index == -1) {
            char err = SERVER_AUTHENTICATION_ERROR;
            ws_sendframe_bin(NULL, &err, 1);
            return;
        }

        int* tty_fd = get_client_file_descriptor(client);
        unsigned int mode = 0;

        ioctl(*tty_fd, KDGKBMODE, &mode);
        if (mode != K_XLATE && mode != K_UNICODE) {          
            if (ioctl(*tty_fd, KDSKBMODE, K_UNICODE) == -1) {
                printf("Error switching keyboard state");
            }
        }

        if (ioctl(*tty_fd, TIOCSTI, msg + 1) == -1) {
            printf("Error pushing input char into console");
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
    memmove(&clientFileDescriptors + index, &clientFileDescriptors + (index * 4) - 4, (clients_top - index - 1) * 4);
    clients_top--;
}

char* generate_auth_key() {
    static char base16_chars[16] = "0123456789abcdef";
    char buf[37];

    //gen random for all spaces because lazy
    for (int i = 0; i < 36; i++) {
        buf[i] = base16_chars[rand() % 16];
    }

    buf[8] = "-";
    buf[13] = "-";
    buf[18] = "-";
    buf[23] = "-";
    buf[36] = "\0";
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
        printf("Created auth key file! A secure, randomly generated UUID has been placed into this file for use of client %s",
            "authentication. You may replace this key with your own (must be of length 36) by modifying the file '%s'.\n", cwd);
    }

    fread(authKey, 1, 36, fptr);
    authKey[36] = "\0";

	struct ws_events evs;
	evs.onclose = &onclose;
	evs.onmessage = &onmessage;
	ws_socket(&evs, 8080, 0, 1000);
    
	return 0;
}
