"""Spike 1 helper: minimal Source RCON client (https://developer.valvesoftware.com/wiki/Source_RCON_Protocol).

Usage: python rcon_probe.py <host> <port> <password> <command> [<command>...]
Prints one JSON line per command with latency and response.
"""
import json
import socket
import struct
import sys
import time

SERVERDATA_AUTH = 3
SERVERDATA_EXECCOMMAND = 2


def send(sock, request_id, kind, body):
    payload = struct.pack('<ii', request_id, kind) + body.encode('utf-8') + b'\x00\x00'
    sock.sendall(struct.pack('<i', len(payload)) + payload)


def receive(sock):
    size = struct.unpack('<i', read_exact(sock, 4))[0]
    data = read_exact(sock, size)
    request_id, kind = struct.unpack('<ii', data[:8])
    return request_id, kind, data[8:-2].decode('utf-8', 'replace')


def read_exact(sock, count):
    buffer = b''
    while len(buffer) < count:
        chunk = sock.recv(count - len(buffer))
        if not chunk:
            raise ConnectionError('connection closed')
        buffer += chunk
    return buffer


def main():
    host, port, password, commands = sys.argv[1], int(sys.argv[2]), sys.argv[3], sys.argv[4:]
    with socket.create_connection((host, port), timeout=5) as sock:
        send(sock, 1, SERVERDATA_AUTH, password)
        # The server answers an empty RESPONSE_VALUE followed by AUTH_RESPONSE (id -1 on failure).
        responses = [receive(sock), receive(sock)]
        authenticated = responses[-1][0] == 1
        print(json.dumps({'auth': authenticated}))
        if not authenticated:
            return 1
        for index, command in enumerate(commands, start=10):
            started = time.perf_counter()
            send(sock, index, SERVERDATA_EXECCOMMAND, command)
            try:
                _, _, body = receive(sock)
            except (ConnectionError, socket.timeout) as error:
                body = f'<{type(error).__name__}: {error}>'
            print(json.dumps({'command': command, 'ms': round((time.perf_counter() - started) * 1000, 1), 'response': body}))
    return 0


if __name__ == '__main__':
    sys.exit(main())
