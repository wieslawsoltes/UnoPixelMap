"""Rebuild the nine attributed initial-view tiles from the current OpenFreeMap snapshot."""
import gzip
import json
from pathlib import Path
from urllib.request import Request, urlopen
from io import BytesIO

CATALOG = "https://tiles.openfreemap.org/planet"
AGENT = "UnoPixelMap/1.0 (+https://github.com/wieslawsoltes/UnoPixelMap)"
OUT = Path(__file__).parent


def read_varint(stream):
    value = 0
    for shift in range(0, 70, 7):
        data = stream.read(1)
        if not data:
            raise ValueError("Truncated varint")
        value |= (data[0] & 127) << shift
        if data[0] < 128:
            return value
    raise ValueError("Invalid varint")


def fields(data):
    stream = BytesIO(data)
    while (start := stream.tell()) < len(data):
        tag = read_varint(stream)
        wire = tag & 7
        if wire == 0:
            value = read_varint(stream)
        elif wire in (1, 2, 5):
            length = read_varint(stream) if wire == 2 else {1: 8, 5: 4}[wire]
            value = stream.read(length)
            if len(value) != length:
                raise ValueError("Truncated field")
        else:
            raise ValueError("Invalid wire type")
        yield tag, value, data[start:stream.tell()]


def cartographic_layers(tile):
    for tag, layer, raw in fields(tile):
        if tag == 26:
            name = next(value for key, value, _ in fields(layer) if key == 10)
            if name not in (b"housenumber", b"poi", b"aerodrome_label"):
                yield raw


def fetch(url):
    with urlopen(Request(url, headers={"User-Agent": AGENT}), timeout=30) as response:
        data = response.read()
    return gzip.decompress(data) if data.startswith(b"\x1f\x8b") else data


if __name__ == "__main__":
    template = json.loads(fetch(CATALOG))["tiles"][0]
    print("Snapshot:", template)
    for x in range(4822, 4825):
        for y in range(6159, 6162):
            data = fetch(template.format(z=14, x=x, y=y))
            (OUT / f"14-{x}-{y}.pbf.gz").write_bytes(
                gzip.compress(b"".join(cartographic_layers(data)), mtime=0))
