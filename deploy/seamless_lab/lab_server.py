"""Configure the pinned BBSC game API for the isolated 32313 shadNet lab."""

import ipaddress
import os
from pathlib import Path
import sys


def configure(template, advertise):
    ipaddress.IPv4Address(advertise)
    source = template.read_text(encoding="utf-8")
    placeholder = "http://{YOUR SHADNET SERVER}:31315"
    configured = f"http://{advertise}:32315"
    if source.count(placeholder) == 0 and source.count(configured) == 4:
        return
    if source.count(placeholder) != 4:
        raise RuntimeError("BBSC ss.info template changed; inspect before starting the lab")
    template.write_text(source.replace(placeholder, configured),
                        encoding="utf-8")


def patch_region_response(server):
    """Add a narrow region adapter to the pinned upstream ss.info handler."""
    source = server.read_text(encoding="utf-8")
    import_line = "from lab_region_info import adapt_region_ss_info\n"
    original_send = '            self._send(info, "text/plain; charset=utf-8")'
    patched_send = '            self._send(adapt_region_ss_info(path, info), "text/plain; charset=utf-8")'
    if import_line in source and patched_send in source:
        return
    if source.count("import bbhost_service\n") != 1 or source.count(original_send) != 1:
        raise RuntimeError("BBSC ss.info handler changed; inspect before starting the lab")
    source = source.replace("import bbhost_service\n", "import bbhost_service\n" + import_line)
    server.write_text(source.replace(original_send, patched_send), encoding="utf-8")


def patch_relay_lifecycle(server):
    """Bound BBSC relay sockets and evict clients that stop accepting relay data.

    The pinned upstream relay broadcasts synchronously to every peer. A peer that
    stops reading can block ``sendall`` indefinitely, keeping the sender thread
    and its socket alive. In production this accumulated hundreds of CLOSE_WAIT
    sockets until the API process exhausted its file-descriptor limit.
    """
    source = server.read_text(encoding="utf-8")
    markers = (
        "RELAY_SEND_TIMEOUT_SECONDS = 3",
        "def _relay_drop(conn):",
        "at_capacity = len(RELAY_CLIENTS) >= MAX_RELAY_CLIENTS",
        "_relay_drop(other)",
    )
    if all(marker in source for marker in markers):
        return
    if any(marker in source for marker in markers):
        raise RuntimeError("BBSC relay lifecycle patch is only partially applied")

    original_send = (
        "def _relay_send(conn, payload):\n"
        "    conn.sendall(struct.pack(\"<I\", len(payload)) + payload)\n"
    )
    patched_send = (
        "RELAY_SEND_TIMEOUT_SECONDS = 3\n"
        "MAX_RELAY_CLIENTS = 128\n\n"
        "def _relay_drop(conn):\n"
        "    with RELAY_LOCK:\n"
        "        RELAY_CLIENTS.pop(conn, None)\n"
        "    try:\n"
        "        conn.shutdown(socket.SHUT_RDWR)\n"
        "    except OSError:\n"
        "        pass\n"
        "    try:\n"
        "        conn.close()\n"
        "    except OSError:\n"
        "        pass\n\n\n"
        "def _relay_send(conn, payload):\n"
        "    conn.sendall(struct.pack(\"<I\", len(payload)) + payload)\n"
    )
    original_announce = (
        "    for conn, _ in clients:\n"
        "        try:\n"
        "            _relay_send(conn, bytes([0x7F, min(len(clients) - 1, 255)]))\n"
        "        except OSError:\n"
        "            pass\n"
    )
    patched_announce = original_announce.replace("            pass\n", "            _relay_drop(conn)\n")
    original_accept = (
        "    def accept_loop():\n"
        "        while True:\n"
        "            conn, address = listener.accept()\n"
        "            conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)\n"
        "            threading.Thread(target=_relay_client, args=(conn, address), daemon=True).start()\n"
    )
    patched_accept = (
        "    def accept_loop():\n"
        "        while True:\n"
        "            conn, address = listener.accept()\n"
        "            conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)\n"
        "            conn.setsockopt(socket.SOL_SOCKET, socket.SO_SNDTIMEO,\n"
        "                            struct.pack(\"@ll\", RELAY_SEND_TIMEOUT_SECONDS, 0))\n"
        "            threading.Thread(target=_relay_client, args=(conn, address), daemon=True).start()\n"
    )
    original_register = (
        "def _relay_client(conn, address):\n"
        "    name = \"{}:{}\".format(*address)\n"
        "    with RELAY_LOCK:\n"
        "        RELAY_CLIENTS[conn] = name\n"
    )
    patched_register = (
        "def _relay_client(conn, address):\n"
        "    name = \"{}:{}\".format(*address)\n"
        "    with RELAY_LOCK:\n"
        "        at_capacity = len(RELAY_CLIENTS) >= MAX_RELAY_CLIENTS\n"
        "        if not at_capacity:\n"
        "            RELAY_CLIENTS[conn] = name\n"
        "    if at_capacity:\n"
        "        conn.close()\n"
        "        print(\"      relay: client capacity reached\", flush=True)\n"
        "        return\n"
    )
    original_broadcast = (
        "            for other in others:\n"
        "                try:\n"
        "                    _relay_send(other, payload)\n"
        "                except OSError:\n"
        "                    pass\n"
    )
    patched_broadcast = original_broadcast.replace("                    pass\n", "                    _relay_drop(other)\n")

    replacements = (
        (original_send, patched_send),
        (original_announce, patched_announce),
        (original_accept, patched_accept),
        (original_register, patched_register),
        (original_broadcast, patched_broadcast),
    )
    for old, new in replacements:
        if source.count(old) != 1:
            raise RuntimeError("BBSC relay source changed; inspect before applying lifecycle patch")
        source = source.replace(old, new)
    server.write_text(source, encoding="utf-8")


def main():
    advertise = os.environ.get("SEAMLESS_ADVERTISE_IP", "127.0.0.1")
    script_dir = Path("/opt/bbsc/server/gameserver")
    configure(script_dir / "ss_info_template.xml", advertise)
    patch_region_response(script_dir / "bbsc_gameserver.py")
    patch_relay_lifecycle(script_dir / "bbsc_gameserver.py")
    Path("/var/lib/bbsc").mkdir(parents=True, exist_ok=True)
    argv = [sys.executable, str(script_dir / "bbsc_gameserver.py"),
            "--shadnet", advertise, "--host", "0.0.0.0", "--advertise", advertise,
            "--port", "32443", "--relay-port", "32444", "--no-bbhost",
            "--bbhost-port", "0", "--bbhost-stun-port", "0", "--no-playlog",
            "--data", "/var/lib/bbsc/online-data.json",
            "--log-dir", "/var/lib/bbsc/logs"]
    os.execv(sys.executable, argv)


if __name__ == "__main__":
    main()
