#!/usr/bin/env python3
"""
Auto export: launches Chara Studio, waits, adds character, selects it, exports GLB.
Usage: python auto_export.py
"""
import subprocess
import time
import requests
import sys
import os

STUDIO_EXE = r"C:\Games\KK\KKIRV2\CharaStudio.exe"
URL = "http://localhost:8080"
WAIT_SECONDS = 40
OUTPUT_FILE = "auto_export.glb"

def api_get(path, params=None):
    try:
        r = requests.get(f"{URL}{path}", params=params, timeout=5)
        return r.json()
    except requests.ConnectionError:
        return None

def api_post(path, params=None):
    try:
        r = requests.post(f"{URL}{path}", params=params, timeout=10)
        return r.json()
    except requests.ConnectionError:
        return None

print("=== GLB Auto Export ===\n")

print("1. Launching Chara Studio...")
subprocess.Popen(STUDIO_EXE)
print(f"   Waiting {WAIT_SECONDS} seconds for Studio to load...")
time.sleep(WAIT_SECONDS)

print("\n2. Checking connection...")
for attempt in range(5):
    status = api_get("/status")
    if status:
        print(f"   Connected: {status}")
        break
    print(f"   Attempt {attempt+1}/5 - retrying in 5s...")
    time.sleep(5)
else:
    print("   FAIL: Cannot connect to plugin")
    sys.exit(1)

print("\n3. Adding character...")
result = api_post("/add-character", {"index": 0})
print(f"   {result}")
print("   Waiting 8 seconds for character to load...")
time.sleep(8)

print("\n4. Listing characters...")
chars = api_get("/list-characters")
print(f"   {chars}")

count = 0
if chars and "characters" in chars:
    count = len(chars["characters"])

print(f"\n5. Selecting character (count={count})...")
result = api_post("/select-character", {"index": 0})
print(f"   {result}")
time.sleep(2)

print("\n6. Exporting GLB...")
result = api_post("/export-glb", {"filename": OUTPUT_FILE})
print(f"   {result}")

if result and result.get("status") == "ok":
    path = result.get("path", "")
    size = result.get("size", 0)
    print(f"\n=== SUCCESS ===")
    print(f"File: {path}")
    print(f"Size: {size} bytes")

    if os.path.exists(path):
        with open(path, "rb") as f:
            magic = f.read(4)
            version = int.from_bytes(f.read(4), "little")
        print(f"Magic: {magic} | Version: {version}")
        if magic == b"glTF" and version == 2:
            print("Valid GLB file!")
        else:
            print("WARNING: Invalid GLB header")
    else:
        print("WARNING: File not found")
else:
    print(f"\n=== FAILED ===")
    print(f"Error: {result.get('error', 'unknown') if result else 'no response'}")
    sys.exit(1)
