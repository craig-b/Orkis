#!/usr/bin/env python3
"""Orkis guest agent: a vsock exec server for warm micro-VMs.

Listens on a fixed vsock port. Each connection carries one JSON-line request and
gets one JSON-line response:

  {"command": "...", "cwd": "rel/dir", "env": {...}, "timeoutSeconds": 60}
  -> {"exit": 0, "stdout": "<base64>", "stderr": "<base64>", "timedOut": false}

  {"shutdown": true}
  -> {"ok": true}   then the agent syncs, unmounts /work, and halts the VM.

Connections are handled on threads, so concurrent commands are ordinary in-OS
concurrency — exactly what shell commands expect of the machine they run on.
"""

import base64
import json
import os
import socket
import subprocess
import threading

PORT = 52000
WORK = "/work"
OUTPUT_CAP = 1 << 20  # bytes per stream; the host applies its own cap after decoding


def run_command(request):
    cwd = WORK
    requested = request.get("cwd")
    if requested:
        cwd = os.path.realpath(os.path.join(WORK, requested))
        if cwd != WORK and not cwd.startswith(WORK + os.sep):
            return {"exit": -1, "stdout": "", "stderr": encode(b"cwd escapes /work"), "timedOut": False}
        os.makedirs(cwd, exist_ok=True)

    env = dict(os.environ)
    env.update(request.get("env") or {})

    try:
        completed = subprocess.run(
            ["/bin/sh", "-c", request["command"]],
            cwd=cwd,
            env=env,
            capture_output=True,
            timeout=request.get("timeoutSeconds", 60),
            start_new_session=True,
        )
        return {
            "exit": completed.returncode,
            "stdout": encode(completed.stdout),
            "stderr": encode(completed.stderr),
            "timedOut": False,
        }
    except subprocess.TimeoutExpired as timeout:
        return {
            "exit": -1,
            "stdout": encode(timeout.stdout or b""),
            "stderr": encode(timeout.stderr or b""),
            "timedOut": True,
        }


def encode(data):
    return base64.b64encode(data[:OUTPUT_CAP]).decode("ascii")


def shutdown():
    subprocess.run(["sync"], check=False)
    subprocess.run(["umount", "/work"], check=False)
    # reboot -f (not poweroff): Firecracker has no ACPI; the reboot syscall with
    # reboot=k boot args produces the KVM shutdown exit that terminates the VMM.
    subprocess.run(["reboot", "-f"], check=False)


def handle(connection):
    try:
        stream = connection.makefile("rwb")
        line = stream.readline()
        if not line:
            return
        request = json.loads(line)
        if request.get("shutdown"):
            stream.write(b'{"ok": true}\n')
            stream.flush()
            connection.close()
            shutdown()
            return
        response = run_command(request)
        stream.write((json.dumps(response) + "\n").encode("ascii"))
        stream.flush()
    except (OSError, ValueError, KeyError):
        pass  # A broken connection or malformed request; the host times out and recovers.
    finally:
        try:
            connection.close()
        except OSError:
            pass


def main():
    os.chdir("/")  # Keep the agent itself off /work so shutdown can unmount it.
    server = socket.socket(socket.AF_VSOCK, socket.SOCK_STREAM)
    server.bind((socket.VMADDR_CID_ANY, PORT))
    server.listen()
    while True:
        connection, _ = server.accept()
        threading.Thread(target=handle, args=(connection,), daemon=True).start()


if __name__ == "__main__":
    main()
