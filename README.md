# Studio HTTP API Plugin

HTTP API plugin for Koikatsu Chara Studio automation.

## Installation

1. Copy `StudioHTTPAPI.dll` to `BepInEx/plugins/`
2. Copy `config.txt` to same folder (optional)
3. Start Chara Studio

## Configuration

Edit `config.txt` to change port (default: 8080):
```
port=8080
```

## API Endpoints

### GET /status
Returns server status.
```bash
curl http://localhost:8080/status
```

### GET /list-scenes
List available scenes in UserData/studio/scene/.
```bash
curl http://localhost:8080/list-scenes
```

### POST /load-scene
Load a scene file.
```bash
curl -X POST "http://localhost:8080/load-scene?path=my_scene.json"
```

### POST /screenshot
Capture screenshot to UserData/studio/screenshots/.
```bash
curl -X POST "http://localhost:8080/screenshot?filename=output.png"
```

### POST /set-clothing
Change clothing item on selected character.
```bash
curl -X POST "http://localhost:8080/set-clothing?type=top&id=123"
curl -X POST "http://localhost:8080/set-clothing?type=bottom&id=456"
curl -X POST "http://localhost:8080/set-clothing?type=shoes&id=789"
curl -X POST "http://localhost:8080/set-clothing?type=accessory&slot=0&id=101"
```

Types: `top`, `bottom`, `inner_top`, `inner_bottom`, `shoes`, `socks`, `accessory`

### POST /set-pose
Set character pose by animation name.
```bash
curl -X POST "http://localhost:8080/set-pose?name=aoao"
```

### POST /set-camera
Set camera position and rotation.
```bash
curl -X POST "http://localhost:8080/set-camera?x=0&y=1&z=-2&rotX=10&rotY=0&rotZ=0&fov=23"
```

### POST /add-character
Add a female character from UserData/chara/female/.
```bash
curl -X POST "http://localhost:8080/add-character?index=0"
```

### GET /list-characters
List characters currently in scene.
```bash
curl http://localhost:8080/list-characters
```

## Python Example

```python
import requests
import time

BASE_URL = "http://localhost:8080"

# Check status
r = requests.get(f"{BASE_URL}/status")
print(r.json())

# List scenes
r = requests.get(f"{BASE_URL}/list-scenes")
print(r.json())

# Take screenshot
r = requests.post(f"{BASE_URL}/screenshot?filename=test.png")
print(r.json())

# Change clothes
requests.post(f"{BASE_URL}/set-clothing?type=top&id=1")
requests.post(f"{BASE_URL}/set-clothing?type=bottom&id=2")

# Take another screenshot
time.sleep(0.5)
requests.post(f"{BASE_URL}/screenshot?filename=test2.png")
```

## Python Tools

### Quick Test
```bash
python tools/test.py
```

### Batch Sprite Renderer
Automate frame-by-frame capture with different clothing combinations:
```bash
# Using config file
python tools/batch_render.py --config tools/outfits_example.json

# Inline options
python tools/batch_render.py --top "1,2,3" --bottom "1,2" --animation "idle,walk" --frames 12 --spritesheet
```

See `tools/outfits_example.json` for config format.

## Building from Source

Requires:
- .NET Framework 4.6+
- BepInEx 5.x
- KKAPI.dll

```bash
dotnet build
```
