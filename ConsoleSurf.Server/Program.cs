using System.Runtime.InteropServices;
using System.Text;
using WatsonWebsocket;

const int SEEK_SET = 0;
const int SEEK_CUR = 1;
const int SEEK_END = 2;
const string AuthenticationKeyFilePath = "authkey.txt";

if (!File.Exists(AuthenticationKeyFilePath) || (await File.ReadAllTextAsync(AuthenticationKeyFilePath)).Length != 36)
{
    await File.WriteAllTextAsync(AuthenticationKeyFilePath, Guid.NewGuid().ToString());
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("Created auth key file! A secure, randomly generated UUID has been placed into this file for " + 
                      "use of client authentication. You may replace this key with your own by modifying the file '" + 
                      AuthenticationKeyFilePath + "'.");
    Console.ResetColor();
}

var authenticationKey = await File.ReadAllTextAsync(AuthenticationKeyFilePath);
var clientRenderTasks = new Dictionary<ClientMetadata, CancellationTokenSource>();
var server = new WatsonWsServer(1234, false);

unsafe
{
    [DllImport("libc")]
    static extern void printf(string pattern, string args);

    [DllImport("libc")]
    static extern int open(string path);

    [DllImport("libc")]
    static extern void read(int fileHandle, void* buffer, uint nBytes); 
    
    [DllImport("libc")]
    static extern int lseek(int fileHandle, int offset, int whence);

    [DllImport("libc")]
    static extern int fseek(void* fileHandle, int offset, int whence); // FILE*
    
    [DllImport("libc")]
    static extern int ftell(void* fileHandle); // FILE*
    
    [DllImport("libc")]
    static extern void* fopen(string path, string access); // FILE*

    [DllImport("libc")]
    static extern int fclose(void* filePtr); // FILE*
    
    int flength(string filePath)
    {
        var file = fopen(filePath, "r");
        if (file == (void*) 0)
        {
            throw new FileNotFoundException(filePath);
        }
        
        fseek(file, 0, SEEK_END);
        var size = ftell(file);
        fclose(file);
        return size;
    }

    Console.WriteLine("Server started, listening");
    
    // Client will send a request packet, with the authentication token for this server instance, desired framerate 
    // (byte), and the console that they wish to access. The shortest message length would be UUID (36 bytes)
    // + framerate (1 byte) + /dev/vc (7 bytes) = 44 bytes.
    server.MessageReceived += (sender, args) =>
    {
        if (args.Data.Count < 44 || Encoding.UTF8.GetString(args.Data[..36]) != authenticationKey)
        {
            server.SendAsync(args.Client, new[] { (byte) ServerPacket.AuthenticationError });
            return;
        }

        var frameInterval = 1000 / Math.Min(args.Data[36], (byte) 60);
        var consolePath = Encoding.UTF8.GetString(args.Data[37..]);
        
        // Verify we can open devices
        var availableConsoles = Directory.GetFiles("/dev/")
            .Where(filePath => filePath.StartsWith("/dev/tty") || filePath.StartsWith("/dev/vc")).ToList();

        if (consolePath is null || !availableConsoles.Contains(consolePath))
        {
            var pathBuffer = Encoding.UTF8.GetBytes(string.Join(", ", availableConsoles.ToArray()));
            var errorBuffer = new byte[pathBuffer.Length + 1];
            errorBuffer[0] = (byte) ServerPacket.ConsoleNotFoundError;
            pathBuffer.CopyTo(errorBuffer, 1);
            
            server.SendAsync(args.Client, errorBuffer);
        }

        // Read console display into buffer
        var handle = open(consolePath);
        var length = flength(consolePath);
        var buffer = stackalloc char[length];
        // First byte is reserved for packet identifier, used by websocket
        var managedBuffer = new byte[length + 1];
        managedBuffer[0] = (byte) ServerPacket.Console;
        var cancelTokenSource = new CancellationTokenSource();

        Task.Run(Task() =>
        {
            while (true)
            {
                read(handle, buffer, (uint) length);
                Marshal.Copy(new IntPtr(buffer), managedBuffer, 1, length);
                server.SendAsync(args.Client, managedBuffer);
                
                Thread.Sleep(frameInterval);
            }
        }, cancelTokenSource.Token);
        
        clientRenderTasks.Add(args.Client, cancelTokenSource);
    };

    server.ClientDisconnected += (sender, args) =>
    {
        // We cancel rendering the display for this client
        if (!clientRenderTasks.TryGetValue(args.Client, out var renderTokenSource))
        {
            return;
        }
        
        renderTokenSource.Cancel();
        clientRenderTasks.Remove(args.Client);
    };

}

var shutdownToken = new CancellationTokenSource();

AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    shutdownToken.Cancel();
};
Console.CancelKeyPress += (_, _) =>
{
    shutdownToken.Cancel();
};

await server.StartAsync();
await Task.Delay(-1, shutdownToken.Token);

enum ServerPacket
{
    AuthenticationError,
    ConsoleNotFoundError,
    Console,
}


/*
strcpy(vcsa_name, "/dev/vcsa");
vcc_errno = 0;
if (virtual_console != 0)
    strcat(vcsa_name, virtual_console);
strcpy(vcc_name, "/dev/vcc/a");
if (virtual_console != 0 && *virtual_console != '\0')
    strcat(vcc_name, virtual_console);
else
    strcat(vcc_name, "0");
strcpy(device_name, vcsa_name);
device_handle = open(vcsa_name, O_RDONLY);
vcsa_errno = errno;
if (device_handle == -1)
{
    strcpy(device_name, vcc_name);
    device_handle = open(vcc_name, O_RDONLY);
    vcc_errno = errno;
}
if (device_handle == -1)
{
    fprintf(stderr, "%s: could not open either the alternate device files.\n", me);
    errno = vcsa_errno;
    perror(vcsa_name);
    errno = vcc_errno;
    perror(vcc_name);
    exit(1);
}
if (!opt_viewonly)
{
    strcpy(tty_name, "/dev/tty");
    if (virtual_console != 0 && *virtual_console != '\0')
        strcat(tty_name, virtual_console);
    else
        strcat(tty_name, "0");
    tty_handle = open(tty_name, O_WRONLY);
    if (tty_handle == -1 && errno == ENOENT)
    {
        strcpy(tty_name, "/dev/vc/");
        if (virtual_console != 0 && *virtual_console != '\0')
            strcat(tty_name, virtual_console);
        else
            strcat(tty_name, "0");
        tty_handle = open(tty_name, O_WRONLY);
    }
    if (tty_handle == -1)
    {
        perror(tty_name);
        exit(1);
    }
}


/*
 static char*		me;
static struct termios	old_termios;
static int		opt_columns;
static int		opt_lines;
static int		opt_viewonly;
static int  		tty_handle = -1;
static char 		tty_name[20];
static int  		device_handle = -1;
static char 		device_name[20];
struct vidchar {
#if 0


// The data returned by reading a /dev/vcsa device.
 
struct vidbuf {
    unsigned char		vidbuf_lines;		// Line on screen 
    unsigned char		vidbuf_columns;		// Columns on screen 
    unsigned char		vidbuf_curcolumn;	 Column cursor is in 
    unsigned char		vidbuf_curline;		Line cursor is in 
    struct vidchar	vidbuf_chars[0];	 Char in VGA video buf 
};


  vidbuf_size = VIDBUF_SIZE(curr_columns, curr_lines) + sizeof(vidchar);
  vidbuf = checked_malloc(vidbuf_size);
  for (;;) {
      // Read the video buffer.
      for (;;) {
          if (lseek(device_handle, 0 L, SEEK_SET) != 0 L)
              syserror(device_name);
          bytes_read = read(device_handle, vidbuf, vidbuf_size);
          if (bytes_read < sizeof( * vidbuf) || bytes_read > vidbuf_size)
              syserror(device_name);
          if (bytes_read < vidbuf_size)
              break;
          vidbuf_size *= 2;
          free(vidbuf);
          vidbuf = checked_malloc(vidbuf_size);
      }  //done
      
      
      if (bytes_read == VIDBUF_SIZE(opt_columns, opt_lines)) {
          curr_columns = opt_columns;
          curr_lines = opt_lines;
      } else {
          int i, j = -1, k = -1;
          for (i = 0; i <= 7; i += 1) {
              curr_columns = vidbuf -> vidbuf_columns + (i / 2 * 256);
              curr_lines = vidbuf -> vidbuf_lines + (i % 2 * 256);
              if (bytes_read == VIDBUF_SIZE(curr_columns, curr_lines)) {
                  k = j;
                  j = i;
              }
          }
          if (j == -1 || k != -1) {
              fprintf(stderr, "\nCan not guess the geometry of the console.\n");
              exit(1);
          }
          curr_columns = vidbuf -> vidbuf_columns + (j / 2 * 256);
          curr_lines = vidbuf -> vidbuf_lines + (j % 2 * 256);
      }
      // * If the screen size has changed blank out the unused portions.
      if (curr_lines < last_lines && last_lines < (unsigned) LINES) {
          move(curr_lines, 0);
          clrtobot();
      }
      if (curr_columns < last_columns && last_columns < (unsigned) COLS) {
          for (line = 0; line < last_lines && line < (unsigned) LINES; line += 1) {
              move(line, last_columns);
              clrtoeol();
          }
      }
      last_lines = curr_lines;
      last_columns = curr_columns;

      // Write the data to the screen.
      vidchar = vidbuf -> vidbuf_chars;
      for (line = 0; line < curr_lines && line < (unsigned) LINES; line += 1) {
          line_chars = 0;
          for (column = 0; column < curr_columns; column += 1) {
              if (column >= (unsigned) COLS) {
                  vidchar += curr_columns - column;
                  break;
              }
              video_attribute = VIDCHAR_ATTRIBUTE(vidchar);
              video_char = VIDCHAR_CHAR(vidchar);
              box = cursesbox[video_char];
              if (box != 0) {
                  video_attribute |= 0x100;
                  video_char = box;
              }
              if (video_char < ' ')
                  video_char = ' ';
              if (video_attribute != last_attribute) {
                  if (line_chars > 0) {
                      move(line, column - line_chars);
                      addchnstr(line_buf, line_chars);
                      wchgat(stdscr, line_chars, curses_attribute, curses_colour, 0);
                      line_chars = 0;
                  }
                  curses_attribute = A_NORMAL;
                  if (video_attribute & 0x100)
                      curses_attribute |= A_ALTCHARSET;
                  if (video_attribute & 0x80)
                      curses_attribute |= A_BLINK;
                  if (video_attribute & 0x08)
                      curses_attribute |= A_BOLD;
                  if (use_colour) {
                      curses_colour =
                          VGA_PAIR(video_attribute & 0x7, video_attribute >> 4 & 0x7);
                  }
                  last_attribute = video_attribute;
              }
              line_buf[line_chars++] = video_char;
              vidchar += 1;
          }
          move(line, column - line_chars);
          addchnstr(line_buf, line_chars);
          wchgat(stdscr, line_chars, curses_attribute, curses_colour, 0);
      }
      if (vidbuf -> vidbuf_curline < LINES && vidbuf -> vidbuf_curcolumn < COLS)
          move(vidbuf -> vidbuf_curline, vidbuf -> vidbuf_curcolumn);
      refresh();
      // Wait for 1/4 or a second, or for a character to be pressed
      FD_ZERO( & readset);
      FD_SET(0, & readset);
      timeval.tv_sec = 0;
      timeval.tv_usec = 250 * 1000 L;
      result = select(0 + 1, & readset, 0, 0, & timeval);
      if (result == -1) {
          if (errno != EINTR)
              syserror("select([tty_handle],0,0,timeval)");
          endwin();
          refresh();
          continue;
      }
      // Read the keys pressed.
      bytes_read = 0;
      if (result == 1) {
          bytes_read =
              read(0, keys_pressed + key_count, sizeof(keys_pressed) - key_count);
          if (bytes_read == ~0 U)
              syserror(tty_name);
      }

      // Do exit processing.

      if (result == 0 && ++escape_notpressed == 4) { // >1sec since last key press
          escape_pressed = 0; // That ends any exit sequence
          escape_notpressed = 0;
      }
      for (key_index = key_count; key_index < key_count + bytes_read; key_index += 1) { // See if escape pressed 3 times
          if (keys_pressed[key_index] != '\033')
              escape_pressed = 0;
          else if (++escape_pressed == 3)
              return;
          if (keys_pressed[key_index] == ('L' & 0x1F))
              wrefresh(curscr);
      }

      // Insert all keys pressed into the virtual console's input
      // buffer.  Don't do this if the virtual console is in scan
      // code mode - giving ASCII characters to a program expecting
      // scan codes will confuse it.

      if (!opt_viewonly) {
          // Close & re-open tty in case they have swapped virtual consoles.

          close(tty_handle);
          tty_handle = open(tty_name, O_WRONLY);
          if (tty_handle == -1)
              syserror(tty_name);
          key_count += bytes_read;
          tty_result = ioctl(tty_handle, KDGKBMODE, & keyboard_mode);
          if (tty_result == -1)
          ;
          else if (keyboard_mode != K_XLATE && keyboard_mode != K_UNICODE)
              key_count = 0; // Keyboard is in scan code mode 
          else {
              for (key_index = 0; key_index < key_count; key_index += 1) {
                  tty_result = ioctl(tty_handle, TIOCSTI, keys_pressed + key_index);
                  if (tty_result == -1)
                      break;
              }
              if (key_index == key_count) // All keys sent? 
                  key_count = 0; // Yes - clear the buffer 
              else {
                  memmove(keys_pressed, keys_pressed + key_index, key_count - key_index);
                  key_count -= key_index;
              }
          }

          // We sometimes get spurious IO errors on the TTY as programs
          // close and re-open it.  Usually they will just go away, if
          // we are patient.

          if (tty_result != -1)
              ioerror_count = 0;
          else if (errno != EIO || ++ioerror_count > 4)
              syserror(tty_name);
      }
  }
  */
