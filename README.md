**1. REQUIRED IMPLEMENTATION (FUNCTIONAL REQUIREMENTS)**
**1.1 Device Identity (Advertising Name)**

The device MUST advertise using:

DropSense-XXXX

Where XXXX is a unique device identifier (serial, MAC suffix, or factory ID).

The host filters devices using:
Name contains "DropSense"

**1.2 BLE GATT Service Structure**

Define a primary service:

DROPSENSE_SERVICE_UUID

Required Characteristics:
FIRMWARE_CHAR_UUID (Read)
Firmware Version Characteristic (read)

COMMAND_CHAR_UUID
Command Input Characteristic (write)

DATA_CHAR_UUID (Write / Notify)
CSV Stream Output Characteristic (notify)

**2. COMMAND PROTOCOL**

All commands must use binary format:

Format:
[COMMAND_ID][FLAGS]

Command list:

DOWNLOAD_CSV = 0x01

Example:
0x01 0x00

**3. CSV DOWNLOAD FLOW (REQUIRED)**

3.1 SEQUENCE

Host sends DOWNLOAD_CSV command
Device sends HEADER packet
Device streams CSV data in chunks
Device sends END OF STREAM marker
Host finalizes file

3.2 HEADER PACKET 

Device must send a header before any data:

Format:
0x10 + 4-byte unsigned integer (total CSV size, little endian)

Meaning:
Byte 0 = 0x10 (header identifier)
Bytes 1–4 = total CSV size

Host behavior:
When header is received, the host sets expectedBytes using the 4-byte value.

3.3 END OF STREAM (MANDATORY)

Device must send a single-byte termination signal:

0xFF

Host must NOT rely on timeouts to determine transfer completion.


**TO RUN**
Unzip Folder, run DropSense.exe; must be left in folder for now.
