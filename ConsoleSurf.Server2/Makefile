# Copyright (C) 2016-2022 Davidson Francis <davidsondfgl@gmail.com>
#
# This program is free software: you can redistribute it and/or modify
# it under the terms of the GNU General Public License as published by
# the Free Software Foundation, either version 3 of the License, or
# (at your option) any later version.
#
# This program is distributed in the hope that it will be useful,
# but WITHOUT ANY WARRANTY; without even the implied warranty of
# MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
# GNU General Public License for more details.
#
# You should have received a copy of the GNU General Public License
# along with this program.  If not, see <http://www.gnu.org/licenses/>

CC      ?= gcc
WSDIR    = $(CURDIR)/wsServer
INCLUDE  = -I $(WSDIR)/include
CFLAGS   =  -Wall -Wextra -O2
CFLAGS  +=  $(INCLUDE) -pthread -pedantic
LIB      =  $(WSDIR)/libws.a

# Check if verbose examples
ifeq ($(VERBOSE_EXAMPLES), no)
	CFLAGS += -DDISABLE_VERBOSE
endif

.PHONY: all clean

# Examples
all: main

# Echo
echo: main.c $(LIB)
	$(CC) $(CFLAGS) $(LDFLAGS) main.c -o main $(LIB)

# Clean
clean:
	@rm -f main
