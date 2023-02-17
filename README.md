# ConsoleSurf
A conspy inspired utility, based off websocket, for streaming a virtual console to a browser-based viewer. See the original @https://conspy.sourceforge.net/.

Server can be found at `ConsoleSurf.Server`. Use the simple web viewer (`index.html`), or hosted on this repository's github pages in any browser to access the virtual consoles of the device running the servers.

Servers are kept safe with an 128-bit authentication key. While being relitavely secure, make sure to never expose these keyts, as they may grant attackers full acess to the system which the server software is run on.