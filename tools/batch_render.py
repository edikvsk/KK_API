#!/usr/bin/env python3
"""
Studio HTTP API - Batch Sprite Renderer
Automates frame-by-frame capture of character animations with different clothing.

Usage:
    python batch_render.py --config outfits.json
    python batch_render.py --outfit "top=1,2,3" --animation "idle,walk" --frames 12
"""

import os
import sys
import json
import time
import argparse
import requests
from pathlib import Path
from datetime import datetime
from typing import List, Dict, Optional

BASE_URL = "http://localhost:8080"
SCREENSHOT_DIR = Path(__file__).parent.parent / "screenshots"


class StudioAPI:
    def __init__(self, base_url: str = BASE_URL):
        self.base_url = base_url

    def check_status(self) -> bool:
        try:
            r = requests.get(f"{self.base_url}/status", timeout=5)
            data = r.json()
            return data.get("studioReady", False)
        except requests.ConnectionError:
            return False

    def list_scenes(self) -> List[str]:
        r = requests.get(f"{self.base_url}/list-scenes")
        return r.json().get("scenes", [])

    def load_scene(self, path: str) -> dict:
        r = requests.post(f"{self.base_url}/load-scene", params={"path": path})
        return r.json()

    def set_camera(self, x=0, y=1, z=-2, rot_x=0, rot_y=0, rot_z=0, fov=23) -> dict:
        r = requests.post(f"{self.base_url}/set-camera", params={
            "x": x, "y": y, "z": z,
            "rotX": rot_x, "rotY": rot_y, "rotZ": rot_z,
            "fov": fov
        })
        return r.json()

    def set_clothing(self, clothing_type: str, item_id: int, slot: int = 0) -> dict:
        params = {"type": clothing_type, "id": item_id}
        if clothing_type == "accessory":
            params["slot"] = slot
        r = requests.post(f"{self.base_url}/set-clothing", params=params)
        return r.json()

    def set_pose(self, pose_name: str) -> dict:
        r = requests.post(f"{self.base_url}/set-pose", params={"name": pose_name})
        return r.json()

    def screenshot(self, filename: str) -> dict:
        r = requests.post(f"{self.base_url}/screenshot", params={"filename": filename})
        return r.json()


def load_config(config_path: str) -> dict:
    with open(config_path, 'r', encoding='utf-8') as f:
        return json.load(f)


def generate_outfit_combinations(clothing: Dict[str, List[int]]) -> List[Dict[str, int]]:
    """Generate all combinations from clothing parts."""
    from itertools import product

    keys = list(clothing.keys())
    values = [clothing[k] for k in keys]

    combinations = []
    for combo in product(*values):
        combinations.append(dict(zip(keys, combo)))

    return combinations


def wait_for_screenshot(timeout: float = 5.0):
    """Wait for screenshot file to appear."""
    time.sleep(0.3)


def render_frame(
    api: StudioAPI,
    outfit: Dict[str, int],
    animation: str,
    frame: int,
    camera: Dict[str, float],
    output_dir: Path,
    outfit_index: int
) -> str:
    """Render a single frame and return the filename."""

    for cloth_type, item_id in outfit.items():
        api.set_clothing(cloth_type, item_id)
        time.sleep(0.05)

    time.sleep(0.1)

    api.set_pose(animation)
    time.sleep(0.2)

    api.set_camera(**camera)

    filename = f"outfit{outfit_index:03d}_{animation}_frame{frame:03d}.png"
    api.screenshot(filename)
    wait_for_screenshot()

    return filename


def create_spritesheet(images: List[str], output_path: str, sprite_width: int = 0):
    """Create a spritesheet from individual frames using Pillow."""
    try:
        from PIL import Image
    except ImportError:
        print("Install Pillow: pip install Pillow")
        return

    loaded = []
    for img_path in images:
        full_path = SCREENSHOT_DIR / img_path
        if full_path.exists():
            loaded.append(Image.open(full_path))

    if not loaded:
        print("No images found for spritesheet")
        return

    sprite_w = sprite_width or loaded[0].width
    sprite_h = loaded[0].height

    cols = max(1, int((sprite_w * len(loaded)) ** 0.5))
    rows = (len(loaded) + cols - 1) // cols

    sheet_w = cols * sprite_w
    sheet_h = rows * sprite_h

    sheet = Image.new("RGBA", (sheet_w, sheet_h), (0, 0, 0, 0))

    for i, img in enumerate(loaded):
        x = (i % cols) * sprite_w
        y = (i // cols) * sprite_h
        sheet.paste(img, (x, y))

    sheet.save(output_path)
    print(f"Spritesheet saved: {output_path}")


def run_batch_render(args):
    api = StudioAPI(args.url)

    if not api.check_status():
        print(f"ERROR: Cannot connect to Studio API at {args.url}")
        print("Make sure Chara Studio is running with StudioHTTPAPI plugin loaded.")
        sys.exit(1)

    print("Connected to Studio HTTP API")

    if args.config:
        config = load_config(args.config)
        clothing = config.get("clothing", {})
        animations = config.get("animations", ["idle"])
        frames = config.get("frames", 1)
        camera = config.get("camera", {"x": 0, "y": 1, "z": -2, "rotX": 0, "rotY": 0, "rotZ": 0, "fov": 23})
        scene = config.get("scene")
        output_dir = Path(config.get("output_dir", str(SCREENSHOT_DIR)))
    else:
        clothing = {}
        if args.top:
            clothing["top"] = [int(x) for x in args.top.split(",")]
        if args.bottom:
            clothing["bottom"] = [int(x) for x in args.bottom.split(",")]
        if args.shoes:
            clothing["shoes"] = [int(x) for x in args.shoes.split(",")]
        if args.socks:
            clothing["socks"] = [int(x) for x in args.socks.split(",")]

        animations = args.animation.split(",") if args.animation else ["idle"]
        frames = args.frames
        camera = {
            "x": args.cam_x, "y": args.cam_y, "z": args.cam_z,
            "rot_x": args.cam_rot_x, "rot_y": args.cam_rot_y, "rot_z": args.cam_rot_z,
            "fov": args.cam_fov
        }
        scene = args.scene
        output_dir = Path(args.output_dir) if args.output_dir else SCREENSHOT_DIR

    output_dir.mkdir(parents=True, exist_ok=True)

    if scene:
        print(f"Loading scene: {scene}")
        api.load_scene(scene)
        time.sleep(2.0)

    combinations = generate_outfit_combinations(clothing) if clothing else [{}]
    print(f"Outfit combinations: {len(combinations)}")
    print(f"Animations: {animations}")
    print(f"Frames per animation: {frames}")
    print(f"Total shots: {len(combinations) * len(animations) * frames}")

    all_files = []
    shot_count = 0

    for outfit_idx, outfit in enumerate(combinations):
        outfit_desc = ", ".join(f"{k}={v}" for k, v in outfit.items()) or "default"
        print(f"\nOutfit {outfit_idx + 1}/{len(combinations)}: {outfit_desc}")

        for anim in animations:
            print(f"  Animation: {anim}")
            for frame in range(frames):
                filename = render_frame(api, outfit, anim, frame, camera, output_dir, outfit_idx)
                all_files.append(filename)
                shot_count += 1
                print(f"    Frame {frame + 1}/{frames}: {filename}")

    print(f"\nDone! {shot_count} screenshots captured")
    print(f"Output: {output_dir}")

    if args.spritesheet:
        print("\nCreating spritesheets...")
        for anim in animations:
            anim_files = [f for f in all_files if f"_{anim}_" in f]
            if anim_files:
                sheet_path = output_dir / f"spritesheet_{anim}.png"
                create_spritesheet(anim_files, str(sheet_path))

    print("Batch render complete!")


def main():
    parser = argparse.ArgumentParser(description="Studio HTTP API Batch Sprite Renderer")

    parser.add_argument("--url", default=BASE_URL, help="API base URL")
    parser.add_argument("--config", help="JSON config file")
    parser.add_argument("--scene", help="Scene file to load")
    parser.add_argument("--output-dir", help="Output directory")

    parser.add_argument("--top", help="Top clothing IDs (comma-separated)")
    parser.add_argument("--bottom", help="Bottom clothing IDs (comma-separated)")
    parser.add_argument("--shoes", help="Shoes IDs (comma-separated)")
    parser.add_argument("--socks", help="Socks IDs (comma-separated)")

    parser.add_argument("--animation", default="idle", help="Animation names (comma-separated)")
    parser.add_argument("--frames", type=int, default=1, help="Frames per animation")

    parser.add_argument("--cam-x", type=float, default=0)
    parser.add_argument("--cam-y", type=float, default=1)
    parser.add_argument("--cam-z", type=float, default=-2)
    parser.add_argument("--cam-rot-x", type=float, default=0)
    parser.add_argument("--cam-rot-y", type=float, default=0)
    parser.add_argument("--cam-rot-z", type=float, default=0)
    parser.add_argument("--cam-fov", type=float, default=23)

    parser.add_argument("--spritesheet", action="store_true", help="Create spritesheets")

    args = parser.parse_args()
    run_batch_render(args)


if __name__ == "__main__":
    main()
