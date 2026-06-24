#!/usr/bin/env python3
"""Test GLB export - checks connection, exports character, verifies file."""
import requests
import sys
import os
from pathlib import Path

URL = "http://localhost:8080"
EXPORT_DIR = Path(__file__).parent.parent / "UserData" / ".." / ".." / "UserData" / "export"

print("1. Checking connection...")
try:
    r = requests.get(f"{URL}/status", timeout=3)
    data = r.json()
    print(f"   OK: {data}")
    if not data.get("studioReady"):
        print("   WARNING: Studio not ready, export may fail")
except requests.ConnectionError:
    print("   FAIL: Chara Studio not running or plugin not loaded")
    sys.exit(1)

print("\n2. Listing characters...")
r = requests.get(f"{URL}/list-characters")
chars = r.json()
print(f"   {chars}")
if not chars.get("characters"):
    print("   WARNING: No characters in scene")

print("\n3. Exporting GLB...")
r = requests.post(f"{URL}/export-glb", params={"filename": "test_export.glb"})
result = r.json()
print(f"   {result}")

if result.get("status") == "ok":
    path = result.get("path", "")
    size = result.get("size", 0)
    print(f"\n4. Verifying file...")
    if os.path.exists(path):
        actual_size = os.path.getsize(path)
        print(f"   File exists: {path}")
        print(f"   Size: {actual_size} bytes (reported: {size})")
        
        with open(path, "rb") as f:
            magic = f.read(4)
            version = int.from_bytes(f.read(4), "little")
            print(f"   Magic: {magic} (expected: b'glTF')")
            print(f"   Version: {version} (expected: 2)")
            
        if magic == b"glTF" and version == 2:
            print("\n   PASS: Valid GLB file!")
        else:
            print("\n   FAIL: Invalid GLB header")
            sys.exit(1)
    else:
        print(f"   FAIL: File not found at {path}")
        sys.exit(1)
else:
    print(f"\n   FAIL: Export failed - {result.get('error', 'unknown')}")
    sys.exit(1)

print("\nDone! Import the .glb file into Blender or https://gltf-viewer.donmccurdy.com/")
