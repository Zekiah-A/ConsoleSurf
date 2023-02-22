#!/bin/bash
# This script will alow the rconsolesurf server to always run as a background on your device and auto start on reboot using a SystemD unit service (linux only)

if [ -z "$1" ]
then
    echo -e "\x1b[31mPlease input the path to the ConsoleSurf server directory as an arguument e.g '/home/pi/ConsoleSurf/ConsoleSurf.Server'"
    exit 0
fi

dotnet_dir=$(which dotnet)

echo -e "
[Unit]
Description=Consolesurf websocket server daemon.
After=network.target
[Service]
Type=simple
StandardInput=tty-force
TTYVHangup=yes
TTYPath=/dev/tty22
TTYReset=yes
Environment=DOTNET_CLI_HOME=/tmp
WorkingDirectory=$1
ExecStart=
ExecStart=$dotnet_dir run
Restart=always
RestartSec=2
[Install]
WantedBy=multi-user.target
" | sudo tee -a /etc/systemd/system/consolesurf.service
sudo systemctl daemon-reload

sudo systemctl enable consolesurf.service
sudo systemctl start consolesurf.service

echo "Task completed. Use 'systemctl status consolesurf.service' to monitor the status of the server process."
echo "Reccomended: Use consolesurf itself to view the status of the consolesurf process, on most systems the process runs under /dev/vcsa22"
echo "Alternative: With conspy, you can use 'sudo conspy 22' to view the virtual console of the server process, more info can be found at https://conspy.sourceforge.net/"
