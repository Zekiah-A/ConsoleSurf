#include <termios.h>
#include <sys/ioctl.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <unistd.h>
#include "wsServer/include/ws.h"
#include <fcntl.h>
#include <string.h>

#define CLIENT_AUTHENTICATE 0
#define CLIENT_INPUT 1

#define SERVER_AUTHENTICATION_ERROR 0
#define SERVER_CONSOLE_NOT_FOUND_ERROR 1
#define SERVER_CONSOLE 2

#define MIN(X, Y) (((X) < (Y)) ? (X) : (Y))

char authKey[37];
pthread_t threads[256];
int threads_top = 0;

struct render_args {
    int fileDescriptor;
    int length;
    int frameInterval;
};

void render_loop(void* args) {
    struct render_args* r_args = (struct render_args*) args;
    char* buffer = malloc(r_args->length);

    // TODO: Perhaps switch while(1) to some bool for this specific client, so that we can
    // cancel the while loop externally (like how a C# cancellation token works)
    while (1) {
        read(r_args->fileDescriptor, buffer, (unsigned int) r_args->length);
        ws_sendframe_bin(NULL, buffer, 1);
        sleep(r_args->frameInterval);
    }
}

int flength(FILE* file) {
    fseek(file, 0, SEEK_END);
    int size = ftell(file);
    fseek(file, 0, SEEK_SET);
    return size;
}

void onmessage(ws_cli_conn_t *client, const unsigned char *msg, uint64_t size, int type) {
    if (size == 0) {
        return;
    }

    if (msg[0] == (char) CLIENT_AUTHENTICATE) {
        char* clientAuthKey = malloc(37);
        clientAuthKey[36] = NULL;
        memcpy(msg, clientAuthKey, 36);

        if (size < 44 || strcmp(clientAuthKey, authKey) != 0) {
            char err = SERVER_AUTHENTICATION_ERROR;
            ws_sendframe_bin(NULL, &err, 1);
            return;
        }

        int frameInterval = 1000 / MIN(msg[36], 60);
        // Sussy maths to cut out the console path ඞ
        char* consolePath = malloc(size - 36);
        memcpy(msg, consolePath, size - 37);
        consolePath[size - 37] = NULL;

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

        pthread_create(threads + threads_top, NULL, render_loop, args);
        threads_top++;

        // TODO: Add client to a kind of dictionary, with their while condition decider (cancellation token) and file descriptor
    }
    else if (msg[0] == (char) CLIENT_INPUT) {
        if (size != 2) {
            char err = SERVER_AUTHENTICATION_ERROR;
            ws_sendframe_bin(NULL, &err, 1);
            return;
        }
/*
        unsigned int mode = 0;
        ioctl(tty_fd, KDGKBMODE, &mode);
        if (mode != K_XLATE && mode != K_UNICODE) {          
            if (ioctl(renderTask.FileHandle, KDSKBMODE, &K_UNICODE) == -1) {
                printf("Error switching keyboard state");
            }
        }

        if (ioctl(tty_fd, TIOCSTI, msg + 1) == -1) {
            printf("Error pushing input char into console");
        }
*/
    }
}

void onclose(ws_cli_conn_t *client) {
    // TODO 
}

int main() {
    if (getuid() != 0) {
        printf("Server must be run with [sudo]/administrator privileges.\n");
        return 0;
    }


    FILE *fptr = fopen("authkey.txt", "rb+");

    if (fptr == NULL || flength(fptr) != 36) {
        fptr = fopen("authkey.txt", "wb");

        char guid[] = "2147bbf0-617b-4f91-b30a-fce490983a53"; // TODO: Generate GUID
        char cwd[256];
        
        getcwd(cwd, sizeof(cwd));
        fwrite(guid, 1, 36, fptr);
        printf("Created auth key file! A secure, randomly generated UUID has been placed into this file for %s",
            "use of client authentication. You may replace this key with your own by modifying the file '%s'.\n", cwd);
    }

    fread(authKey, 1, 36, fptr);
    authKey[36] = NULL;

	struct ws_events evs;
	evs.onclose = &onclose;
	evs.onmessage = &onmessage;
	ws_socket(&evs, 8080, 0, 1000);
    
	return 0;
}
