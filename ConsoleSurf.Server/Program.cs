using System.Net;
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
    static extern int open(string path);

    [DllImport("libc")]
    static extern void read(int fileHandle, void* buffer, uint nBytes);

    [DllImport("libc")]
    static extern int close(int fileHandle);
    
    [DllImport("libc")]
    static extern int lseek(int fileHandle, int offset, int whence);
    
    [DllImport("libc")]
    static extern int ioctl(int fd, ulong request, char* character);

    [DllImport("libc")]
    static extern int fseek(void* fileHandle, int offset, int whence); // FILE*
    
    [DllImport("libc")]
    static extern int ftell(void* fileHandle); // FILE*
    
    [DllImport("libc")]
    static extern void* fopen(string path, string access); // FILE*

    [DllImport("libc")]
    static extern int fclose(void* filePtr); // FILE*
    
    [DllImport("libc")]
    static extern uint getuid();

    [DllImport("./tiocsti.so")]
    static extern int get_tiocsti();
    
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

    var TIOCSTI = get_tiocsti();

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
            
            // Verify we can open devices
            var availableConsoles = Directory.GetFiles("/dev/")
                .Where(filePath => filePath.StartsWith("/dev/tty") || filePath.StartsWith("/dev/vc")).ToList();

            if (!availableConsoles.Contains(consolePath))
            {
                SendConsoleNotFound(args.Client, availableConsoles);
                return;
            }

            // Read console display into buffer
            var handle = open(consolePath);
            
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
            
            fixed (byte* charPtr = &data[0])
            {
                // TIOCSTI is request for simulating a terminal input
                // https://www.qnx.com/developers/docs/7.1/#com.qnx.doc.neutrino.devctl/topic/tioc/tiocsti.html
                ioctl(renderTask.FileHandle, (ulong) TIOCSTI, (char*) charPtr);
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
    Console,
}

enum ClientPacket
{
    Authenticate,
    Input
}

record struct RenderTask(CancellationTokenSource TokenSource, int FileHandle, IntPtr BufferPtr);