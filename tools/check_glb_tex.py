import struct, json

data = open(r'C:\Games\KK\KKIRV2\UserData\export\test_tex.glb', 'rb').read()
length = struct.unpack_from('<I', data, 12)[0]
json_data = data[20:20+length].rstrip(b'\x20').rstrip(b'\x00')
j = json.loads(json_data.decode('utf-8'))

bvs = j.get('bufferViews', [])
for i, bv in enumerate(bvs):
    off = bv.get('byteOffset', 0)
    ln = bv.get('byteLength', 0)
    tgt = bv.get('target', 'none')
    print("BV %d: off=%d len=%d target=%s" % (i, off, ln, tgt))

imgs = j.get('images', [])
for i, img in enumerate(imgs):
    bv_idx = img.get('bufferView', -1)
    mime = img.get('mimeType', '')
    print("Image %d: bv=%d mime=%s" % (i, bv_idx, mime))
    if bv_idx < len(bvs):
        bv = bvs[bv_idx]
        off = bv.get('byteOffset', 0)
        ln = bv.get('byteLength', 0)
        img_data = data[20+length+off:20+length+off+ln]
        print("  bytes=%d first8=%s" % (len(img_data), img_data[:8].hex()))
        png_magic = bytes([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])
        print("  is_png=%s" % (img_data[:8] == png_magic))
        if len(img_data) > 8:
            with open(r'C:\Games\KK\KKIRV2\UserData\export\debug_tex.png', 'wb') as f:
                f.write(img_data)
            print("  saved debug_tex.png")

# Check texture -> image link
texs = j.get('textures', [])
for i, t in enumerate(texs):
    print("Texture %d: source=%s" % (i, t.get('source')))

mats = j.get('materials', [])
for i, m in enumerate(mats):
    pbr = m.get('pbrMetallicRoughness', {})
    bct = pbr.get('baseColorTexture', {})
    print("Material %d: baseColorTexture=%s" % (i, bct))
