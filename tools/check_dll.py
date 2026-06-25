dll = open(r'C:\Games\KK\KKIRV2\BepInEx\plugins\StudioHTTPAPI\bin\Debug\net46\StudioHTTPAPI.dll', 'rb').read()
print('Size:', len(dll))

for needle in ['materials', 'StringBuilder', 'charInfo', 'roughnessFactor', 'pbrMetallic']:
    enc = needle.encode('utf-16-le')
    idx = dll.find(enc)
    print(f'{needle}: offset={idx}')

for needle in [b'StringBuilder', b'charInfo']:
    idx = dll.find(needle)
    print(f'ASCII {needle}: offset={idx}')
