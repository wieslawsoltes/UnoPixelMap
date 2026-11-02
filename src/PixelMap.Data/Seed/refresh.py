"""Rebuild the attributed world and initial-view tiles from the current OpenFreeMap snapshot."""
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


def varint(value):
    output = bytearray()
    while value >= 128:
        output.append((value & 127) | 128)
        value >>= 7
    output.append(value)
    return output


def message_field(number, data):
    return varint((number << 3) | 2) + varint(len(data)) + data


def named_pois(layer):
    entries = list(fields(layer))
    keys = [value for tag, value, _ in entries if tag == 26]
    values = [value for tag, value, _ in entries if tag == 34]
    keep = {b"name", b"name:en", b"class", b"subclass", b"rank", b"layer"}
    key_index, value_index = {}, {}
    result = bytearray()
    for tag, feature, raw in entries:
        if tag not in (18, 26, 34):
            result.extend(raw)
        if tag != 18:
            continue
        properties, geometry = [], bytearray()
        for field, packed, encoded in fields(feature):
            if field != 18:
                geometry.extend(encoded)
                continue
            reader = BytesIO(packed)
            while reader.tell() < len(packed):
                key, value = read_varint(reader), read_varint(reader)
                if keys[key] in keep:
                    properties.append((key, value))
        if not any(keys[k] in (b"name", b"name:en") for k, _ in properties):
            continue
        tags = bytearray()
        for key, value in properties:
            tags.extend(varint(key_index.setdefault(key, len(key_index))))
            tags.extend(varint(value_index.setdefault(value, len(value_index))))
        geometry.extend(message_field(2, tags))
        result.extend(message_field(2, geometry))
    for key in key_index:
        result.extend(message_field(3, keys[key]))
    for value in value_index:
        result.extend(message_field(4, values[value]))
    return result


def cartographic_layers(tile):
    for tag, layer, raw in fields(tile):
        if tag == 26:
            name = next(value for key, value, _ in fields(layer) if key == 10)
            if name == b"poi":
                yield message_field(3, named_pois(layer))
            elif name not in (b"housenumber", b"aerodrome_label"):
                yield raw


def fetch(url):
    with urlopen(Request(url, headers={"User-Agent": AGENT}), timeout=30) as response:
        data = response.read()
    return gzip.decompress(data) if data.startswith(b"\x1f\x8b") else data


if __name__ == "__main__":
    template = json.loads(fetch(CATALOG))["tiles"][0]
    print("Snapshot:", template)
    tiles = [(0, 0, 0)] + [(14, x, y) for x in range(4822, 4825) for y in range(6159, 6162)]
    for z, x, y in tiles:
        data = fetch(template.format(z=z, x=x, y=y))
        (OUT / f"{z}-{x}-{y}.pbf.gz").write_bytes(
            gzip.compress(b"".join(cartographic_layers(data)), mtime=0))
