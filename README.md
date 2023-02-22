# ConsoleSurf
A conspy inspired utility, based off websocket, for streaming a virtual console to a browser-based viewer. See the original at https://conspy.sourceforge.net/.

Server can be found at `ConsoleSurf.Server2`. Use the simple web viewer (`index.html`), or hosted on this repository's github pages in any browser to access the virtual consoles of the device running the servers.

Servers are kept safe with an 128-bit authentication key. While being relitavely secure, make sure to never expose these keyts, as they may grant attackers full acess to the system which the server software is run on.

The server software has been intended to work with linux servers only. Any functionality on BSD/OSX is untested and must be done at your own risk!

## Building:
To build this repo from source with the latest server, run the following:
```sh
    # Clone the repository recursively so we include all submodules
    > git clone --recursive https://github.com/Zekiah-A/ConsoleSurf 

    # Enter server 2 directory and make build directory for cmake
    > cd ConsoleSurf/ConsoleSurf.Server2
    > mkdir build

    # Configure cmake and compile the project
    > cd build
    > cmake ..
    > make 
```

If an error is reported, either create an issue on github, in the case of it being a bug with a server software. If one of the commands stated above does not work, or is missing, make sure you have the appropiate build tools installed (`sudo apt-get install build-essential` on debian-based distros, or `sudo pacman -Sy base-devel` on arch-based distrobutions).

The build process has only been tested to work on linux. Attempting to compile this software on any other platform is unsupported as of yet.