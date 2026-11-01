"""Package a 256px PNG as a Windows ICO without third-party dependencies."""
from pathlib import Path
import struct

source = Path(__file__).resolve().parents[1] / "build/AppIcon.iconset/icon_256x256.png"
output = Path(__file__).with_name("icon.ico")
data = source.read_bytes()
assert data[:8] == b"\x89PNG\r\n\x1a\n"
output.write_bytes(
    struct.pack("<HHH", 0, 1, 1)
    + struct.pack("<BBBBHHII", 0, 0, 0, 0, 1, 32, len(data), 22)
    + data
)
print(output)
