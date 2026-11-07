#!/usr/bin/env python3
"""Recover normal Mono AOT method names from an exact published browser package.

Usage:
  python scripts/symbolicate-browser-profile.py \
    --framework path/to/publish/wwwroot/_framework \
    --profile path/to/case.cpuprofile --output path/to/symbolicated

Use the package that produced the profile, including its linked Webcil assemblies.
A source checkout or another publish of the same commit is not interchangeable.
The script requires Python 3.10+ and no third-party modules. It does not execute WASM.
Generic AOT extras, native code and runtime trampolines remain anonymous. Method
aliases caused by linker folding are retained, not guessed away. Generated profiles
can be opened in Chrome DevTools. Summary times are sample weights, not invocation
counts, GPU time, or timings from a separate browser measurement window.

Mono assigns ordinary methods their zero-based MethodDef row, then the exported
mono_aot_<assembly>_get_method accessor loads an indirect function-table index from
its constant data array. The WASM element section resolves that to a function index.
Reference: https://github.com/dotnet/runtime/blob/v10.0.14/src/mono/mono/mini/aot-compiler.c
Metadata layout: ECMA-335, Partition II, sections 22-24.
"""
from __future__ import annotations

import argparse
import bisect
from collections import Counter, defaultdict
import hashlib
import json
from pathlib import Path
import re
import struct
import sys
from urllib.parse import unquote, urlparse


class Reader:
    def __init__(self, data: bytes, offset: int = 0):
        self.data, self.offset = data, offset

    def take(self, length: int) -> bytes:
        if length < 0 or self.offset + length > len(self.data):
            raise ValueError('Truncated binary input')
        start = self.offset
        self.offset += length
        return self.data[start:self.offset]

    def byte(self) -> int:
        return self.take(1)[0]

    def unsigned(self) -> int:
        value = shift = 0
        while shift < 70:
            byte = self.byte()
            value |= (byte & 127) << shift
            if byte < 128:
                return value
            shift += 7
        raise ValueError('Invalid unsigned LEB128')

    def signed(self) -> int:
        value = shift = 0
        while shift < 70:
            byte = self.byte()
            value |= (byte & 127) << shift
            shift += 7
            if byte < 128:
                return value - (1 << shift) if byte & 64 else value
        raise ValueError('Invalid signed LEB128')

    def string(self) -> str:
        return self.take(self.unsigned()).decode('utf-8')

    def integer_expression(self) -> int:
        if self.byte() != 0x41:
            raise ValueError('Only constant i32 segment offsets are supported')
        value = self.signed()
        if self.byte() != 0x0b or value < 0:
            raise ValueError('Invalid segment offset expression')
        return value


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


class Wasm:
    """The small subset needed to resolve statically linked browser AOT tables."""
    def __init__(self, path: Path):
        self.path = path
        self.data = path.read_bytes()
        reader = Reader(self.data)
        if reader.take(8) != b'\0asm\x01\0\0\0':
            raise ValueError(f'{path}: not a version-1 WASM module')
        self.sections: dict[int, tuple[int, bytes]] = {}
        while reader.offset < len(self.data):
            kind, size = reader.byte(), reader.unsigned()
            offset = reader.offset
            data = reader.take(size)
            if kind:
                if kind in self.sections:
                    raise ValueError(f'Duplicate WASM section {kind}')
                self.sections[kind] = offset, data
        self.imported_functions = self._imports()
        self.functions: dict[int, tuple[int, bytes]] = {}
        offset, data = self.sections[10]
        reader = Reader(data)
        for number in range(reader.unsigned()):
            size = reader.unsigned()
            start = offset + reader.offset
            self.functions[number + self.imported_functions] = start, reader.take(size)
        self.exports: dict[str, int] = {}
        reader = Reader(self.sections[7][1])
        for _ in range(reader.unsigned()):
            name, kind, index = reader.string(), reader.byte(), reader.unsigned()
            if kind == 0:
                self.exports[name] = index
        self.table: dict[int, int] = {}
        reader = Reader(self.sections[9][1])
        for _ in range(reader.unsigned()):
            flags = reader.unsigned()
            if flags not in (0, 2):
                raise ValueError('Only active function-index element segments are supported')
            table = reader.unsigned() if flags == 2 else 0
            start = reader.integer_expression()
            if flags == 2 and reader.byte() != 0:
                raise ValueError('Unsupported element kind')
            for i in range(reader.unsigned()):
                function = reader.unsigned()
                if table == 0:
                    self.table[start + i] = function
        self.segments: list[tuple[int, bytes]] = []
        reader = Reader(self.sections[11][1])
        for _ in range(reader.unsigned()):
            flags = reader.unsigned()
            if flags not in (0, 2):
                raise ValueError('Only active data segments are supported')
            if flags == 2 and reader.unsigned() != 0:
                raise ValueError('Only memory zero is supported')
            start = reader.integer_expression()
            self.segments.append((start, reader.take(reader.unsigned())))
        self.segments.sort()
        if any(a + len(data) > b for (a, data), (b, _) in zip(self.segments, self.segments[1:])):
            raise ValueError('Overlapping data segments are unsupported')
        self.segment_starts = [start for start, _ in self.segments]
        self.data_end = max(start + len(data) for start, data in self.segments)

    def _imports(self) -> int:
        count = 0
        reader = Reader(self.sections[2][1])
        for _ in range(reader.unsigned()):
            reader.string()
            reader.string()
            kind = reader.byte()
            if kind == 0:
                reader.unsigned()
                count += 1
            elif kind in (1, 2):
                if kind == 1 and reader.byte() not in (0x70, 0x6f):
                    raise ValueError('Unsupported table reference type')
                flags = reader.unsigned()
                reader.unsigned()
                if flags & 1:
                    reader.unsigned()
            elif kind == 3:
                reader.take(2)
            elif kind == 4:
                reader.unsigned()
                reader.unsigned()
            else:
                raise ValueError(f'Unsupported import kind {kind}')
        return count

    def read_pointer(self, address: int) -> int:
        if address < 0 or address + 4 > self.data_end:
            raise ValueError('Method table lies outside initialized WASM data')
        # Optimized data segments can omit zero bytes, including bytes within an
        # aligned pointer. Uninitialized memory is zero; read across those gaps.
        value = bytearray(4)
        for i in range(4):
            index = bisect.bisect_right(self.segment_starts, address + i) - 1
            if index >= 0:
                start, data = self.segments[index]
                relative = address + i - start
                if relative < len(data):
                    value[i] = data[relative]
        return int.from_bytes(value, 'little')

    def method_table(self, assembly: str) -> tuple[str, int, int]:
        name = 'corlib' if assembly == 'System.Private.CoreLib' else re.sub(r'[^A-Za-z0-9]', '_', assembly)
        export = f'mono_aot_{name}_get_method'
        if export not in self.exports:
            raise ValueError(f'No AOT method accessor for {assembly}')
        function = self.exports[export]
        _, body = self.functions[function]
        # local.get 0; i32.const 2; i32.shl; i32.const <base>;
        # i32.add; i32.load align=2 offset=0; end
        prefix, suffix = bytes.fromhex('200041027441'), bytes.fromhex('6a2802000b')
        match = re.search(re.escape(prefix) + b'(.{1,5})' + re.escape(suffix) + b'$', body, re.S)
        if not match:
            raise ValueError(f'Unsupported AOT method accessor implementation: {export}')
        constant = Reader(match[1])
        base = constant.signed()
        if constant.offset != len(match[1]) or base < 0:
            raise ValueError(f'Invalid method table address in {export}')
        return export, function, base


# Index widths are determined by the referenced table or coded-index tag width.
CODED = {
    'TypeDefOrRef': (2, (2, 1, 27)),
    'HasConstant': (2, (4, 8, 23)),
    'HasCustomAttribute': (5, (6, 4, 1, 2, 8, 9, 10, 0, 14, 23, 20, 17, 26, 27, 32, 35, 38, 39, 40, 42, 44, 43)),
    'HasFieldMarshal': (1, (4, 8)), 'HasDeclSecurity': (2, (2, 6, 32)),
    'MemberRefParent': (3, (2, 1, 26, 6, 27)), 'HasSemantics': (1, (20, 23)),
    'MethodDefOrRef': (1, (6, 10)), 'MemberForwarded': (1, (4, 6)),
    'Implementation': (2, (38, 35, 39)), 'CustomAttributeType': (3, (6, 10)),
    'ResolutionScope': (2, (0, 26, 35, 1)), 'TypeOrMethodDef': (1, (2, 6)),
}
LAYOUTS = [
    'u2 S G G G', 'ResolutionScope S S', 'u4 S S TypeDefOrRef t4 t6', 't4',
    'u2 S B', 't6', 'u4 u2 u2 S B t8', 't8', 'u2 u2 S', 't2 TypeDefOrRef',
    'MemberRefParent S B', 'u2 HasConstant B', 'HasCustomAttribute CustomAttributeType B',
    'HasFieldMarshal B', 'u2 HasDeclSecurity B', 'u2 u4 t2', 'u4 t4', 'B',
    't2 t20', 't20', 'u2 S TypeDefOrRef', 't2 t23', 't23', 'u2 S B',
    'u2 t6 HasSemantics', 't2 MethodDefOrRef MethodDefOrRef', 'S', 'B',
    'u2 MemberForwarded S t26', 'u4 t4', 'u4 u4', 'u4',
    'u4 u2 u2 u2 u2 u4 B S S', 'u4', 'u4 u4 u4',
    'u2 u2 u2 u2 u4 B S S B', 'u4 t35', 'u4 u4 u4 t35', 'u4 S B',
    'u4 u4 S S Implementation', 'u4 u4 S Implementation', 't2 t2',
    'u2 u2 TypeOrMethodDef S', 'MethodDefOrRef B', 't42 TypeDefOrRef',
]


class Metadata:
    def __init__(self, path: Path):
        self.path, self.data = path, path.read_bytes()
        start = self.data.find(b'BSJB')
        if start < 0:
            raise ValueError(f'No CLI metadata root in {path}')
        self.start = start
        version_size = struct.unpack_from('<I', self.data, start + 12)[0]
        cursor = start + 16 + version_size
        _, stream_count = struct.unpack_from('<HH', self.data, cursor)
        cursor += 4
        streams = {}
        for _ in range(stream_count):
            offset, size = struct.unpack_from('<II', self.data, cursor)
            cursor += 8
            end = self.data.index(0, cursor)
            name = self.data[cursor:end].decode('ascii')
            cursor = (end + 4) & ~3
            if start + offset + size > len(self.data):
                raise ValueError('Metadata stream exceeds file bounds')
            streams[name] = self.data[start + offset:start + offset + size]
        if '#-' in streams:
            raise ValueError('Uncompressed metadata with pointer tables is unsupported')
        self.strings, self.blobs, self.tables = streams['#Strings'], streams['#Blob'], streams['#~']
        heap_sizes = self.tables[6]
        valid = struct.unpack_from('<Q', self.tables, 8)[0]
        self.counts, cursor = {}, 24
        for number in range(64):
            if valid & (1 << number):
                if number >= len(LAYOUTS):
                    raise ValueError(f'Unsupported CLI metadata table {number}')
                self.counts[number] = struct.unpack_from('<I', self.tables, cursor)[0]
                cursor += 4
        if any(self.counts.get(number) for number in (3, 5, 7, 19, 22)):
            raise ValueError('CLI pointer tables are unsupported')
        self.heap_widths = {'S': 4 if heap_sizes & 1 else 2, 'G': 4 if heap_sizes & 2 else 2, 'B': 4 if heap_sizes & 4 else 2}
        self.starts, self.widths = {}, {}
        for number, layout in enumerate(LAYOUTS):
            widths = [self.width(field) for field in layout.split()]
            self.starts[number], self.widths[number] = cursor, widths
            cursor += self.counts.get(number, 0) * sum(widths)
        if cursor > len(self.tables):
            raise ValueError('Metadata tables exceed stream bounds')
        self.assembly = self.string(self.row(32, 1)[7])

    def width(self, field: str) -> int:
        if field in self.heap_widths:
            return self.heap_widths[field]
        if field.startswith('u'):
            return int(field[1:])
        if field.startswith('t'):
            return 4 if self.counts.get(int(field[1:]), 0) >= 65536 else 2
        bits, tables = CODED[field]
        return 4 if any(self.counts.get(table, 0) >= (1 << (16 - bits)) for table in tables) else 2

    def row(self, table: int, number: int) -> list[int]:
        if not 1 <= number <= self.counts.get(table, 0):
            raise ValueError(f'Metadata row out of bounds: {table}:{number}')
        widths = self.widths[table]
        cursor = self.starts[table] + (number - 1) * sum(widths)
        result = []
        for width in widths:
            result.append(int.from_bytes(self.tables[cursor:cursor + width], 'little'))
            cursor += width
        return result

    def string(self, offset: int) -> str:
        end = self.strings.index(0, offset)
        return self.strings[offset:end].decode('utf-8')

    def blob(self, offset: int) -> str:
        first = self.blobs[offset]
        if first < 0x80:
            size, cursor = first, offset + 1
        elif first < 0xc0:
            size, cursor = ((first & 0x3f) << 8) | self.blobs[offset + 1], offset + 2
        elif first < 0xe0:
            size = int.from_bytes(self.blobs[offset:offset + 4], 'big') & 0x1fffffff
            cursor = offset + 4
        else:
            raise ValueError('Invalid compressed metadata blob size')
        if cursor + size > len(self.blobs):
            raise ValueError('Truncated metadata blob')
        return self.blobs[cursor:cursor + size].hex().upper()

    def methods(self) -> list[dict]:
        types = {number: self.row(2, number) for number in range(1, self.counts.get(2, 0) + 1)}
        nested = dict(self.row(41, number) for number in range(1, self.counts.get(41, 0) + 1))
        names = {}

        def type_name(number: int, ancestors: frozenset = frozenset()) -> str:
            if number in names:
                return names[number]
            if number in ancestors:
                raise ValueError('Cyclic nested type metadata')
            row = types[number]
            name, namespace = self.string(row[1]), self.string(row[2])
            if number in nested:
                name = type_name(nested[number], ancestors | {number}) + '+' + name
            elif namespace:
                name = namespace + '.' + name
            names[number] = name
            return name

        result = []
        for number, definition in types.items():
            end = types[number + 1][5] if number + 1 in types else self.counts.get(6, 0) + 1
            for method in range(definition[5], end):
                row = self.row(6, method)
                result.append({'assembly': self.assembly, 'row': method, 'token': 0x06000000 | method,
                               'type': type_name(number), 'name': self.string(row[3]),
                               'rva': row[0], 'signature': self.blob(row[4])})
        return result


WASM_FUNCTION = re.compile(r'\$?wasm-function\[(\d+)\]')


def function_index(frame: dict) -> int | None:
    match = WASM_FUNCTION.fullmatch(frame['functionName'])
    return int(match[1]) if match else None


def symbol_name(index: int, aliases: list[dict]) -> str:
    names = sorted({f"{method['type']}.{method['name']}" for method in aliases})
    name = names[0] if len(names) == 1 else 'aliases{' + ' | '.join(names) + '}'
    return f'{name} [wasm-function[{index}]]'


def symbolize(profile_path: Path, native: Wasm, symbols: dict, output: Path) -> tuple[dict, set[int]]:
    profile = json.loads(profile_path.read_text())
    nodes = {node['id']: node for node in profile['nodes']}
    if len(nodes) != len(profile['nodes']):
        raise ValueError('Duplicate profile node identifiers')
    parents = {}
    for node in nodes.values():
        for child in node.get('children', []):
            if child not in nodes or child in parents:
                raise ValueError('Profile call tree has a missing or shared child')
            parents[child] = node['id']
    samples, deltas = profile.get('samples', []), profile.get('timeDeltas', [])
    if len(samples) != len(deltas) or any(delta < 0 for delta in deltas):
        raise ValueError('Invalid CPU profile sample/delta arrays')
    keys, labels, referenced = {}, {}, set()
    for node in nodes.values():
        frame = node['callFrame']
        index = function_index(frame)
        if index is not None:
            filename = Path(unquote(urlparse(frame['url']).path)).name
            if filename != native.path.name:
                if filename.startswith('dotnet.native.'):
                    raise ValueError(f'Profile native module {filename!r} does not match {native.path.name!r}')
                # Mono also creates tiny runtime WASM modules. Their local function
                # numbers are unrelated to the application module's function table.
                key = f"foreign-wasm:{frame['url']}:{index}"
                keys[node['id']], labels[key] = key, frame['functionName']
                continue
            if index not in native.functions:
                raise ValueError(f'WASM function {index} is absent from the supplied module')
            start, body = native.functions[index]
            if frame.get('lineNumber') != 0 or not start <= frame.get('columnNumber', -1) < start + len(body):
                raise ValueError(f'WASM function {index} byte location differs from the supplied module')
            referenced.add(index)
            key = f'wasm:{index}'
            if index in symbols:
                frame['functionName'] = symbol_name(index, symbols[index])
        else:
            key = json.dumps([frame.get(part) for part in ('functionName', 'url', 'lineNumber', 'columnNumber')], separators=(',', ':'))
        keys[node['id']] = key
        labels[key] = frame['functionName']
    if not referenced:
        raise ValueError('The supplied native module does not occur in this profile')
    self_us, inclusive_us, self_count, inclusive_count = Counter(), Counter(), Counter(), Counter()
    node_weight, node_samples = Counter(), Counter()
    for node, delta in zip(samples, deltas):
        if node not in nodes:
            raise ValueError('CPU profile sample refers to a missing node')
        node_weight[node] += delta
        node_samples[node] += 1
    for node, weight in node_weight.items():
        key, count = keys[node], node_samples[node]
        self_us[key] += weight
        self_count[key] += count
        path_keys, path_nodes = set(), set()
        while node is not None:
            if node in path_nodes:
                raise ValueError('Cyclic profile call tree')
            path_nodes.add(node)
            path_keys.add(keys[node])
            node = parents.get(node)
        for key in path_keys:
            inclusive_us[key] += weight
            inclusive_count[key] += count
    functions = [{'key': key, 'name': labels[key], 'self_ms': self_us[key] / 1000,
                  'inclusive_ms': inclusive_us[key] / 1000, 'self_samples': self_count[key],
                  'inclusive_samples': inclusive_count[key]}
                 for key in sorted(inclusive_us, key=lambda key: (-self_us[key], key))]
    summary = {'source_profile': str(profile_path.resolve()), 'source_sha256': sha256(profile_path.read_bytes()),
               'native_wasm': native.path.name, 'native_sha256': sha256(native.data),
               'sample_count': len(samples), 'sample_weight_ms': sum(deltas) / 1000,
               'profile_duration_ms': (profile['endTime'] - profile['startTime']) / 1000,
               'referenced_wasm_functions': len(referenced),
               'mapped_wasm_functions': sum(index in symbols for index in referenced),
               'mapped_wasm_self_ms': sum(self_us[f'wasm:{index}'] for index in referenced if index in symbols) / 1000,
               'unmapped_wasm_self_ms': sum(self_us[f'wasm:{index}'] for index in referenced if index not in symbols) / 1000,
               'foreign_wasm_self_ms': sum(value for key, value in self_us.items() if key.startswith('foreign-wasm:')) / 1000,
               'functions': functions}
    output.mkdir(parents=True, exist_ok=True)
    (output / (profile_path.stem + '.symbolicated.cpuprofile')).write_text(json.dumps(profile, separators=(',', ':')))
    (output / (profile_path.stem + '.summary.json')).write_text(json.dumps(summary, indent=2) + '\n')
    return summary, referenced


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--framework', required=True, type=Path, help='Exact published _framework directory')
    parser.add_argument('--native-wasm', type=Path, help='Defaults to the single dotnet.native.*.wasm in --framework')
    parser.add_argument('--expected-native-sha256', help='Optional independently verified native WASM SHA-256')
    parser.add_argument('--profile', action='append', required=True, type=Path, help='Repeat for multiple profiles from this exact package')
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    candidates = [args.native_wasm] if args.native_wasm else sorted(args.framework.glob('dotnet.native.*.wasm'))
    if len(candidates) != 1:
        raise ValueError('Expected exactly one native WASM file; specify --native-wasm')
    if len({path.stem for path in args.profile}) != len(args.profile):
        raise ValueError('Profile basenames must be unique within one output directory')
    native = Wasm(candidates[0])
    digest = sha256(native.data)
    if args.expected_native_sha256 and digest != args.expected_native_sha256.lower():
        raise ValueError('Native WASM SHA-256 does not match the independently verified digest')
    modules, symbols = [], defaultdict(list)
    for path in sorted(args.framework.glob('*.wasm')):
        if path.resolve() == native.path.resolve():
            continue
        metadata = Metadata(path)
        export, function, address = native.method_table(metadata.assembly)
        methods = metadata.methods()
        modules.append({'assembly': metadata.assembly, 'file': path.name, 'sha256': sha256(metadata.data),
                        'metadata_offset': metadata.start, 'method_count': len(methods),
                        'get_method_export': export, 'get_method_function': function, 'table_address': address})
        for method in methods:
            pointer = native.read_pointer(address + 4 * (method['row'] - 1))
            index = native.table.get(pointer)
            if index is not None:
                symbols[index].append(dict(method, table_index=pointer, aot_method_index=method['row'] - 1))
    referenced = set()
    for profile in args.profile:
        summary, used = symbolize(profile, native, symbols, args.output)
        referenced |= used
        print(f"{profile.name}: {summary['mapped_wasm_functions']}/{summary['referenced_wasm_functions']} WASM functions named; "
              f"{summary['mapped_wasm_self_ms']:.3f} ms mapped, {summary['unmapped_wasm_self_ms']:.3f} ms unresolved self samples")
    manifest = {'schema_version': 1, 'native_file': native.path.name, 'native_sha256': digest,
                'modules': modules, 'symbols': {str(index): symbols[index] for index in sorted(referenced) if index in symbols},
                'limitations': ['Only normal MethodDef AOT slots are recovered; generic extras and native/runtime functions remain unresolved.',
                                'Aliases sharing a WASM function are preserved; sampling cannot select one alias.',
                                'Profile module filenames and function byte offsets must match the exact package.',
                                'Times describe sampled stacks in this profile, not method invocation counts or GPU execution time.']}
    (args.output / 'symbolication-manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')


if __name__ == '__main__':
    try:
        main()
    except (ValueError, KeyError, IndexError, struct.error, OSError) as error:
        print(f'Symbolication failed: {error}', file=sys.stderr)
        sys.exit(1)
