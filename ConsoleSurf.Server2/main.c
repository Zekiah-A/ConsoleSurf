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

void onclose(ws_cli_conn_t *client)
{
	char *cli;
	cli = ws_getaddress(client);
	printf("Client %s", cli, " disconnected from the server\n");
}

void onmessage(ws_cli_conn_t *client, const unsigned char *msg, uint64_t size, int type)
{
    if (msg[0] == (char) CLIENT_AUTHENTICATE) {
        char* clientAuthKey = malloc(37);
        clientAuthKey[36] = NULL;
        memcpy(msg, clientAuthKey, 36);

        if (sizeof(msg < 44) || strcmp(clientAuthKey, authKey) != 0) {
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

        // Make sure terminal is in canonical mode
        struct termios tio;
        if (tcgetattr(fileDescriptor, &tio) == -1) {
            printf("Terminal is not in canonical mode, switching\n");
        }

        tio.c_iflag |= ICANON;

        if (tcsetattr(fileDescriptor, TCSANOW, &tio) == -1) {
            printf("Could not set terminal to canonical mode\n");
        }

        int length = flength(consolePath);
        int buffer = malloc(length);

    }
    else if (msg[0] == (char) CLIENT_INPUT) {
        
        //char c = 'a';
        //ioctl(tty_fd, TIOCSTI, &c);
    }
}

int flength(FILE* file)
{
    fseek(file, 0, SEEK_END);
    int size = ftell(file);
    fseek(file, 0, SEEK_SET);
    return size;
}

int main()
{
    FILE *fptr;

    fptr = fopen("authkey.txt", "rb+");

    if (fptr == NULL || flength(fptr) != 36) {
        fptr = fopen("authkey.txt", "wb");

        char guid[] = "2147bbf0-617b-4f91-b30a-fce490983a53";
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

/*
#include <stdio.h>
#include <stdlib.h>
#include <fcntl.h>
#include <unistd.h>
#include <termios.h>
#include <sys/ioctl.h>

int main()
{
    int tty_fd = open("/dev/tty", O_RDWR);
    if (tty_fd < 0) {
        perror("open");
        exit(1);
    }

    struct termios tio;
    if (tcgetattr(tty_fd, &tio) < 0) {
        perror("tcgetattr");
        exit(1);
    }

    printf("%d %d\n", ICANON, TCSANOW);

    // Set terminal to canonical mode
    tio.c_lflag |= ICANON;
    if (tcsetattr(tty_fd, TCSANOW, &tio) < 0) {
        perror("tcsetattr");
        exit(1);
    }

    // Send a character to the terminal
    char c = 'a';
    if (ioctl(tty_fd, TIOCSTI, &c) < 0) {
        perror("ioctl");
        exit(1);
    }

    close(tty_fd);

    return 0;
}

*/