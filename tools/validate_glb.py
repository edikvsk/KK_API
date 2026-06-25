#!/usr/bin/env python3
"""Validate GLB file structure and report issues."""
import struct
import json
import sys
import os

def read_glTF_chunk(data, offset):
    length = struct.unpack_from('<I', data, offset)[0]
    chunk_type = struct.unpack_from('<I', data, offset + 4)[0]
    chunk_data = data[offset + 8:offset + 8 + length]
    return chunk_type, chunk_data, 8 + length

def validate(path):
    with open(path, 'rb') as f:
        data = f.read()

    print(f"File size: {len(data)} bytes")

    # Header
    magic = struct.unpack_from('<I', data, 0)[0]
    version = struct.unpack_from('<I', data, 4)[0]
    total_len = struct.unpack_from('<I', data, 8)[0]
    print(f"Magic: 0x{magic:08X} (expected 0x46546C67)")
    print(f"Version: {version}")
    print(f"Declared length: {total_len}")
    if total_len != len(data):
        print(f"  WARNING: actual length {len(data)} != declared {total_len}")

    # JSON chunk
    ct1, json_data, skip1 = read_glTF_chunk(data, 12)
    print(f"\nJSON chunk: type=0x{ct1:08X} size={len(json_data)}")
    if ct1 != 0x4E4F534A:
        print(f"  ERROR: wrong JSON chunk type")

    # BIN chunk
    bin_offset = 12 + skip1
    if bin_offset < len(data):
        ct2, bin_data, skip2 = read_glTF_chunk(data, bin_offset)
        print(f"BIN chunk: type=0x{ct2:08X} size={len(bin_data)}")
        if ct2 != 0x004E4942:
            print(f"  ERROR: wrong BIN chunk type")
    else:
        bin_data = b''
        print("No BIN chunk")

    # Parse JSON
    # Strip trailing nulls from JSON padding
    json_str = json_data.rstrip(b'\x00').decode('utf-8')
    gltf = json.loads(json_str)
    print(f"\nJSON keys: {list(gltf.keys())}")

    asset = gltf.get('asset', {})
    print(f"Asset: {asset}")

    scenes = gltf.get('scenes', [])
    print(f"Scenes: {len(scenes)}")

    nodes = gltf.get('nodes', [])
    print(f"Nodes: {len(nodes)}")
    for i, n in enumerate(nodes):
        print(f"  Node {i}: {n}")

    meshes = gltf.get('meshes', [])
    print(f"Meshes: {len(meshes)}")
    for i, m in enumerate(meshes):
        print(f"  Mesh {i}: {m}")

    accessors = gltf.get('accessors', [])
    print(f"Accessors: {len(accessors)}")
    for i, a in enumerate(accessors):
        print(f"  Accessor {i}: type={a.get('type')} count={a.get('count')} compType={a.get('componentType')} bv={a.get('bufferView')}")
        if 'min' in a:
            print(f"    min={a['min']} max={a['max']}")

    bvs = gltf.get('bufferViews', [])
    print(f"BufferViews: {len(bvs)}")
    for i, bv in enumerate(bvs):
        print(f"  BV {i}: offset={bv.get('byteOffset')} length={bv.get('byteLength')} target={bv.get('target','none')}")

    buffers = gltf.get('buffers', [])
    print(f"Buffers: {len(buffers)}")
    for i, b in enumerate(buffers):
        print(f"  Buffer {i}: byteLength={b.get('byteLength')}")

    materials = gltf.get('materials', [])
    print(f"Materials: {len(materials)}")

    # Validate accessor data
    print("\n--- Accessor validation ---")
    for i, a in enumerate(accessors):
        bv_idx = a.get('bufferView', 0)
        if bv_idx < len(bvs):
            bv = bvs[bv_idx]
            offset = bv.get('byteOffset', 0)
            length = bv.get('byteLength', 0)
            comp_type = a.get('componentType')
            count = a.get('count')
            atype = a.get('type')

            type_sizes = {'SCALAR': 1, 'VEC2': 2, 'VEC3': 3, 'VEC4': 4}
            elem_size = type_sizes.get(atype, 0)
            comp_sizes = {5120: 1, 5121: 1, 5122: 2, 5123: 2, 5125: 4, 5126: 4}
            comp_size = comp_sizes.get(comp_type, 0)
            expected = count * elem_size * comp_size
            print(f"  Accessor {i}: offset={offset} expected_bytes={expected} actual_bv_length={length}")

            if expected != length:
                print(f"    MISMATCH!")

            # Check first few values
            if comp_type == 5126 and atype == 'VEC3' and count > 0:
                vals = struct.unpack_from('<fff', bin_data, offset)
                print(f"    First vertex: {vals}")
                if count > 1:
                    vals2 = struct.unpack_from('<fff', bin_data, offset + comp_size * elem_size)
                    print(f"    Second vertex: {vals2}")
            elif comp_type in (5123, 5125) and atype == 'SCALAR' and count > 0:
                if comp_type == 5123:
                    val = struct.unpack_from('<H', bin_data, offset)[0]
                else:
                    val = struct.unpack_from('<I', bin_data, offset)[0]
                print(f"    First index: {val}")

if __name__ == '__main__':
    path = sys.argv[1] if len(sys.argv) > 1 else r"C:\Games\KK\KKIRV2\UserData\export\auto_export.glb"
    if os.path.exists(path):
        validate(path)
    else:
        print(f"File not found: {path}")
