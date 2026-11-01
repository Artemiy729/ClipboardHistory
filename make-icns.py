"""Package generated PNG sizes into the standard macOS icns container."""
from pathlib import Path
import sys

folder = Path(sys.argv[1])
output = Path(sys.argv[2])
sizes = [
    ("icp4", "icon_16x16.png"),
    ("icp5", "icon_32x32.png"),
    ("icp6", "icon_32x32@2x.png"),
    ("ic07", "icon_128x128.png"),
    ("ic08", "icon_256x256.png"),
    ("ic09", "icon_512x512.png"),
    ("ic10", "icon_512x512@2x.png"),
]
parts = []
for kind, name in sizes:
    data = (folder / name).read_bytes()
    parts.append(kind.encode("ascii") + (len(data) + 8).to_bytes(4, "big") + data)
body = b"".join(parts)
output.write_bytes(b"icns" + (len(body) + 8).to_bytes(4, "big") + body)
