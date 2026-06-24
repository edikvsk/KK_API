#!/usr/bin/env python3
"""Minimal test - checks connection and takes a screenshot."""
import requests
import sys

URL = "http://localhost:8080"

print("1. Checking connection...")
try:
    r = requests.get(f"{URL}/status", timeout=3)
    print(f"   OK: {r.json()}")
except requests.ConnectionError:
    print("   FAIL: Chara Studio not running or plugin not loaded")
    sys.exit(1)

print("\n2. Taking screenshot...")
r = requests.post(f"{URL}/screenshot", params={"filename": "test.png"})
print(f"   OK: {r.json()}")

print("\nDone! Check UserData/studio/screenshots/test.png")
