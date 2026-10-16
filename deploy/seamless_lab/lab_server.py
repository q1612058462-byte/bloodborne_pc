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


def main():
    advertise = os.environ.get("SEAMLESS_ADVERTISE_IP", "127.0.0.1")
    script_dir = Path("/opt/bbsc/server/gameserver")
    configure(script_dir / "ss_info_template.xml", advertise)
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
