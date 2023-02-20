using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using ConsoleSurf.Server;
using WatsonWebsocket;

const int SEEK_SET = 0;
const int SEEK_CUR = 1;
const int SEEK_END = 2;

var authenticationKeyFilePath = Path.Join(Directory.GetCurrentDirectory(), "authkey.txt");

if (!File.Exists(authenticationKeyFilePath) || (await File.ReadAllTextAsync(authenticationKeyFilePath)).Length != 36)
{
    await File.WriteAllTextAsync(authenticationKeyFilePath, Guid.NewGuid().ToString());
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("Created auth key file! A secure, randomly generated UUID has been placed into this file for " + 
                      "use of client authentication. You may replace this key with your own by modifying the file '" + 
                      authenticationKeyFilePath + "'.");
    Console.ResetColor();
}

var rateLimiter = new RateLimiter(TimeSpan.FromSeconds(3));
var authenticationKey = await File.ReadAllTextAsync(authenticationKeyFilePath);
var clientRenderTasks = new Dictionary<ClientMetadata, RenderTask>();
var server = new WatsonWsServer(1234, "localhost");

unsafe
{
    [DllImport("libc")]
    static extern void printf(string pattern, string args);

    [DllImport("libc")]
    static extern int open(string path, int flags);

    [DllImport("libc")]
    static extern void read(int fileHandle, void* buffer, uint nBytes);

    [DllImport("libc")]
    static extern int close(int fileHandle);
    
    [DllImport("libc")]
    static extern int lseek(int fileHandle, int offset, int whence);
    
    [DllImport("libc")]
    static extern int ioctl(int fd, ulong request, void* data);

    [DllImport("libc")]
    static extern int fseek(void* filePtr, int offset, int whence); // FILE*
    
    [DllImport("libc")]
    static extern int ftell(void* filePtr); // FILE*
    
    [DllImport("libc")]
    static extern void* fopen(string path, string access); // FILE*

    [DllImport("libc")]
    static extern int fclose(void* filePtr); // FILE*
    
    [DllImport("libc")]
    static extern uint getuid();

    [DllImport("libc")]
    static extern int* __errno_location();

    [DllImport("libc")]
    static extern int tcgetattr(int fileHandle, void* termiosPtr);
    
    [DllImport("libc")]
    static extern int tcsetattr(int fileHandle, int request, void* termiosPtr);
    
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
    
    void SendConsoleNotFound(ClientMetadata client, IEnumerable<string> availableConsoles)
    {
        var pathBuffer = Encoding.UTF8.GetBytes(string.Join(", ", availableConsoles.ToArray()));
        var errorBuffer = new byte[pathBuffer.Length + 1];
        errorBuffer[0] = (byte) ServerPacket.ConsoleNotFoundError;
        pathBuffer.CopyTo(errorBuffer, 1);
                
        server.SendAsync(client, errorBuffer);
    }

    if (getuid() != 0)
    {
        throw new Exception("Server must be run with [sudo]/administrator privileges.");
    }
    
    // https://github.com/torvalds/linux/blob/master/include/uapi/linux/kd.h
    ulong KDGKBMODE = 0x4B44;
    ulong KDSKBMODE = 0x4B45;
    ulong KDSETMODE = 0x4B3A;
    ulong TIOCSTI = 0x5412;
    const int O_RDONLY = 00000000;
    const int O_WRONLY = 00000001;
    const int O_RDWR = 00000002;
    // https://man7.org/linux/man-pages/man3/termios.3.html
    const int ICANON = 0x00000100;
    const int TCSANOW = 0x2;
    
    server.MessageReceived += (sender, args) =>
    {
        if (args.Data.Count == 0)
        {
            return;
        }
        
        var data = args.Data[1..].ToArray();

        // Client will send a request packet, with the authentication token for this server instance, desired framerate 
        // (byte), and the console that they wish to access. The shortest data length would be UUID (36 bytes) +
        // framerate (1 byte) + /dev/vc (7 bytes) = 44 bytes.
        if (args.Data[0] == (byte) ClientPacket.Authenticate)
        {
            if (data.Length < 44 || !authenticationKey.Equals(Encoding.UTF8.GetString(data[..36])) ||
                !rateLimiter.IsAuthorised(IPAddress.Parse(args.Client.IpPort[..args.Client.IpPort.LastIndexOf(":",
                    StringComparison.Ordinal)])) || clientRenderTasks.ContainsKey(args.Client))
            {
                server.SendAsync(args.Client, new[] { (byte) ServerPacket.AuthenticationError });
                return;
            }
            
            var frameInterval = 1000 / Math.Min(data[36], (byte) 60);
            var consolePath = Encoding.UTF8.GetString(data[37..]);
            
            // Verify we can open devices, example paths are /dev/vcsa22, or /dev/vcc/a22
            var availableConsoles = Directory.GetFiles("/dev/")
                .Where(filePath => filePath.StartsWith("/dev/vc")).ToList();

            if (!availableConsoles.Contains(consolePath))
            {
                SendConsoleNotFound(args.Client, availableConsoles);
                return;
            }

            // Read console display into buffer, with read write perms
            var handle = open(consolePath, O_RDWR);
            
            // Make sure terminal is in canonical mode
            void* unmanagedTermios;
            if (tcgetattr(handle, &unmanagedTermios) < 0) {
                Console.WriteLine("Terminal is not in canonical mode, switching");
            }
            var tio = (Termios) Marshal.PtrToStructure(new IntPtr(unmanagedTermios), typeof(Termios))!;

            // Set terminal to canonical mode
            tio.c_lflag |= ICANON;
            Marshal.StructureToPtr(tio, new IntPtr(unmanagedTermios), false);
            if (tcsetattr(handle, TCSANOW, &unmanagedTermios) < 0) {
                Console.WriteLine("Could not set terminal to canonical mode");
            }
            
            // If desired console does not exist, we tell client what does
            if (handle == -1)
            {
                SendConsoleNotFound(args.Client, availableConsoles);
                return;
            }
            
            var length = flength(consolePath);
            var buffer = (char*) NativeMemory.Alloc(new UIntPtr((uint) length));
            
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
            
            clientRenderTasks.Add(args.Client, new RenderTask(cancelTokenSource, handle, new IntPtr(buffer)));
        }
        // Client will send input packet (byte) packet identifier, then (byte[]) input.
        else if (args.Data[0] == (byte) ClientPacket.Input)
        {
            if (!clientRenderTasks.TryGetValue(args.Client, out var renderTask) || data.Length == 0)
            {
                server.SendAsync(args.Client, new[] { (byte) ServerPacket.AuthenticationError });
                return;
            }
            
            // Try get keyboard into text (console, non graphical mode) so we can send commands to virtual console
            // https://www.linuxjournal.com/article/2783, https://man7.org/linux/man-pages/man2/ioctl_console.2.html
            ulong KD_TEXT = 0x00;
            var keyboardMode = ioctl(renderTask.FileHandle, KDSETMODE, &KD_TEXT);
            if (keyboardMode == -1)
            {
                Console.WriteLine($"Error switching keyboard mode {(Error) (*__errno_location())}, keyboard mode: {keyboardMode}");
            }
            
            // Check if keyboard is in scan code mode (giving ASCII characters to a program expecting
            // scan codes will confuse it), if it isn't, attempt to change the keyboard mode, or just return.
            ulong K_XLATE = 0x01;
            ulong K_UNICODE = 0x03;
            uint keyboardState = 0;
            ioctl(renderTask.FileHandle, KDGKBMODE, &keyboardState);
            if (keyboardState != K_XLATE && keyboardState != K_UNICODE)
            {
                var unicodeKeyboardPtr = &K_UNICODE;
                
                if (ioctl(renderTask.FileHandle, KDSKBMODE, unicodeKeyboardPtr) == -1)
                {
                    Console.WriteLine($"Error switching keyboard state {(Error) (*__errno_location())}, keyboard state: {keyboardState}");
                }
            }
            
            fixed (byte* charPtr = &data[0])
            {
                // TIOCSTI is request for simulating a terminal input
                // https://www.qnx.com/developers/docs/7.1/#com.qnx.doc.neutrino.devctl/topic/tioc/tiocsti.html
                if (ioctl(renderTask.FileHandle, TIOCSTI, (char*) charPtr) == -1)
                {
                    Console.WriteLine($"Error handling input {(Error) (*__errno_location())}, char code: {data[0]}");
                }
            }
        }
    };

    server.ClientDisconnected += (sender, args) =>
    {
        // We cancel rendering the display for this client
        if (!clientRenderTasks.TryGetValue(args.Client, out var renderTask))
        {
            return;
        }

        // https://stackoverflow.com/questions/18172979/deleting-c-sharp-unsafe-pointers
        try
        {
            renderTask.TokenSource.Cancel();
            NativeMemory.Free((char*) renderTask.BufferPtr);
            close(renderTask.FileHandle);
        }
        catch
        {
            // Cancelling the token will likely throw an exception, ignore.    
        }
        
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

Console.WriteLine("Server started successfully, listening");
await server.StartAsync();
await Task.Delay(-1, shutdownToken.Token);

enum ServerPacket
{
    AuthenticationError,
    ConsoleNotFoundError,
    Console
}

enum ClientPacket
{
    Authenticate,
    Input
}

record struct RenderTask(CancellationTokenSource TokenSource, int FileHandle, IntPtr BufferPtr);

[StructLayout(LayoutKind.Sequential)]
struct Termios
{
    public ulong c_iflag;		/* input mode flags */
    public ulong c_oflag;		/* output mode flags */
    public ulong c_cflag;		/* control mode flags */
    public ulong c_lflag;		/* local mode flags */
    public byte c_line;		/* line discipline */
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
    public byte[] c_cc;	/* control characters */
};
