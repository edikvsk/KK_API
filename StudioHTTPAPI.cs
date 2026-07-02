using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using BepInEx;
using UnityEngine;

namespace StudioHTTPAPI
{
    [BepInPlugin("com.studio.httpapi", "Studio HTTP API", "1.0.0")]
    public class StudioHTTPAPI : BaseUnityPlugin
    {
        private TcpListener tcpListener;
        private Thread listenerThread;
        private volatile bool isRunning;
        private int port = 8080;
        private static string _logPath;
        private static Color _exportSkinColor = Color.white;

        private void Awake()
        {
            try { _logPath = Path.Combine(Path.GetDirectoryName(Info.Location), "debug.log"); File.WriteAllText(_logPath, "[" + DateTime.Now.ToString("HH:mm:ss") + "] Plugin loading...\n"); } catch { }
            Log("Plugin loading...");
            LoadConfig();
            StartServer();
        }

        private void LoadConfig()
        {
            try
            {
                var configFile = Path.Combine(Path.GetDirectoryName(Info.Location), "config.txt");
                if (File.Exists(configFile))
                {
                    foreach (var line in File.ReadAllLines(configFile))
                    {
                        var t = line.Trim();
                        if (t.StartsWith("port=")) { int p; if (int.TryParse(t.Substring(5), out p)) port = p; }
                    }
                }
            }
            catch (Exception ex) { Log("Config err: " + ex.Message); }
            Log("Port: " + port);
        }

        private void StartServer()
        {
            try
            {
                tcpListener = new TcpListener(IPAddress.Loopback, port);
                tcpListener.Start();
                isRunning = true;
                listenerThread = new Thread(AcceptLoop) { IsBackground = true };
                listenerThread.Start();
                Log("Server started on port " + port);
            }
            catch (Exception ex) { Log("START FAILED: " + ex.Message); }
        }

        private void AcceptLoop()
        {
            while (isRunning) { try { var c = tcpListener.AcceptTcpClient(); new Thread(() => HandleClient(c)) { IsBackground = true }.Start(); } catch { break; } }
        }

        private void HandleClient(TcpClient client)
        {
            try
            {
                var stream = client.GetStream();
                stream.ReadTimeout = 3000;
                stream.WriteTimeout = 3000;
                var reqBytes = new MemoryStream();
                var one = new byte[1];
                int prev = 0;
                for (int i = 0; i < 8192; i++)
                {
                    int rd = stream.Read(one, 0, 1);
                    if (rd <= 0) break;
                    reqBytes.WriteByte(one[0]);
                    int cur = one[0];
                    if (prev == '\r' && cur == '\n' && reqBytes.Length >= 4)
                    {
                        var arr = reqBytes.ToArray();
                        int len = arr.Length;
                        if (arr[len - 4] == '\r' && arr[len - 3] == '\n' && arr[len - 2] == '\r' && arr[len - 1] == '\n') break;
                    }
                    prev = cur;
                }
                var request = Encoding.ASCII.GetString(reqBytes.ToArray());
                var lines = request.Split(new[] { "\r\n" }, StringSplitOptions.None);
                var reqParts = lines[0].Split(' ');
                string path = reqParts.Length >= 2 ? reqParts[1] : "/";
                string query = "";
                var qIdx = path.IndexOf('?');
                if (qIdx >= 0) { query = path.Substring(qIdx); path = path.Substring(0, qIdx); }
                Log("REQ " + path + query);
                string body;
                switch (path)
                {
                    case "/test": body = "{\"status\":\"ok\",\"message\":\"plugin works!\"}"; break;
                    case "/status": body = GetStatus(); break;
                    case "/list-scenes": body = ListScenes(); break;
                    case "/load-scene": body = LoadScene(query); break;
                    case "/screenshot": body = TakeScreenshot(query); break;
                    case "/set-clothing": body = SetClothing(query); break;
                    case "/set-pose": body = SetPose(query); break;
                    case "/set-camera": body = SetCamera(query); break;
                    case "/add-character": body = AddCharacter(query); break;
                    case "/list-characters": body = ListCharacters(); break;
                    case "/select-character": body = SelectCharacter(query); break;
                    case "/export-glb": body = ExportGlb(query); break;
                    case "/debug-chafile": body = DebugChaFile(query); break;
                    default: body = "{\"error\":\"not found\"}"; break;
                }
                var bodyBytes = Encoding.UTF8.GetBytes(body);
                var resp = "HTTP/1.1 200 OK\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: " + bodyBytes.Length + "\r\nConnection: close\r\n\r\n";
                stream.Write(Encoding.ASCII.GetBytes(resp), 0, resp.Length);
                stream.Write(bodyBytes, 0, bodyBytes.Length);
                stream.Flush();
                client.Close();
            }
            catch (Exception ex) { Log("CLIENT ERR: " + ex.GetType().Name + " " + ex.Message); try { client.Close(); } catch { } }
        }

        private string GetStatus()
        {
            try { var s = GetStudioInstance(); return "{\"status\":\"ok\",\"studioReady\":" + (ReferenceEquals(s, null) ? "false" : "true") + "}"; }
            catch (Exception ex) { return "{\"status\":\"ok\",\"studioReady\":false,\"error\":\"" + Escape(ex.Message) + "\"}"; }
        }

        private string UserDataPath(string sub) { return Path.Combine(Path.Combine(Application.dataPath, ".."), Path.Combine("UserData", sub)); }

        private string ListScenes()
        {
            try
            {
                var dir = UserDataPath(Path.Combine("studio", "scene"));
                if (!Directory.Exists(dir)) return "{\"scenes\":[]}";
                var files = Directory.GetFiles(dir, "*.json");
                var sb = new StringBuilder("[");
                for (int i = 0; i < files.Length; i++) { if (i > 0) sb.Append(","); sb.Append("\"" + Escape(Path.GetFileName(files[i])) + "\""); }
                sb.Append("]");
                return "{\"scenes\":" + sb.ToString() + "}";
            }
            catch (Exception ex) { return "{\"error\":\"" + Escape(ex.Message) + "\"}"; }
        }

        private string LoadScene(string query)
        {
            var path = GetParam(query, "path");
            if (string.IsNullOrEmpty(path)) return "{\"error\":\"missing path\"}";
            var scenePath = Path.Combine(Path.Combine(Path.Combine(Path.Combine(Application.dataPath, ".."), "UserData"), Path.Combine("studio", "scene")), path);
            if (!File.Exists(scenePath)) return "{\"error\":\"not found\"}";
            var capturedPath = scenePath;
            UnityThreadHelper.Enqueue(new ThreadStart(() =>
            {
                try { var s = GetStudioInstance(); if (ReferenceEquals(s, null)) return; var m = s.GetType().GetMethod("LoadScene", BindingFlags.Public | BindingFlags.Instance); if (!ReferenceEquals(m, null)) m.Invoke(s, new object[] { capturedPath }); }
                catch (Exception ex) { Log("LoadScene err: " + ex.Message); }
            }));
            return "{\"status\":\"loading\",\"scene\":\"" + Escape(path) + "\"}";
        }

        private string TakeScreenshot(string query)
        {
            var filename = GetParam(query, "filename");
            if (string.IsNullOrEmpty(filename)) filename = "shot_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
            if (!filename.EndsWith(".png")) filename += ".png";
            var dir = UserDataPath(Path.Combine("studio", "screenshots"));
            Directory.CreateDirectory(dir);
            var fullPath = Path.Combine(dir, filename);
            var capturedPath = fullPath;
            UnityThreadHelper.Enqueue(new ThreadStart(() =>
            {
                try { Application.CaptureScreenshot(capturedPath); }
                catch (Exception ex) { Log("Screenshot err: " + ex.Message); }
            }));
            return "{\"status\":\"capturing\",\"path\":\"" + Escape(fullPath) + "\"}";
        }

        private string SetClothing(string query)
        {
            var type = GetParam(query, "type") ?? "";
            var idStr = GetParam(query, "id") ?? "0";
            var slotStr = GetParam(query, "slot") ?? "0";
            int id; if (!int.TryParse(idStr, out id)) return "{\"error\":\"bad id\"}";
            int slot; int.TryParse(slotStr, out slot);
            var ct = type.ToLower(); var cs = slot;
            UnityThreadHelper.Enqueue(new ThreadStart(() =>
            {
                try
                {
                    var cc = GetSelectedChaControl();
                    if (ReferenceEquals(cc, null)) { Log("No character selected"); return; }
                    var t = cc.GetType();
                    switch (ct)
                    {
                        case "top": CallInt(t, cc, "SetClothesTopId", id); break;
                        case "bottom": CallInt(t, cc, "SetClothesBottomId", id); break;
                        case "inner_top": CallInt(t, cc, "SetClothesInnerTopId", id); break;
                        case "inner_bottom": CallInt(t, cc, "SetClothesInnerBottomId", id); break;
                        case "shoes": CallInt(t, cc, "SetShoesId", id); break;
                        case "socks": CallInt(t, cc, "SetClothesSocksId", id); break;
                        case "accessory": var m = t.GetMethod("SetAccessoryId", BindingFlags.Public | BindingFlags.Instance); if (!ReferenceEquals(m, null)) m.Invoke(cc, new object[] { cs, id }); break;
                    }
                }
                catch (Exception ex) { Log("Clothing err: " + ex.Message); }
            }));
            return "{\"status\":\"ok\",\"type\":\"" + Escape(type) + "\",\"id\":" + id + "}";
        }

        private string SetPose(string query)
        {
            var name = GetParam(query, "name");
            if (string.IsNullOrEmpty(name)) return "{\"error\":\"missing name\"}";
            var cn = name;
            UnityThreadHelper.Enqueue(new ThreadStart(() =>
            {
                try
                {
                    var cc = GetSelectedChaControl();
                    if (ReferenceEquals(cc, null)) return;
                    var prop = cc.GetType().GetProperty("animBody", BindingFlags.Public | BindingFlags.Instance);
                    if (ReferenceEquals(prop, null)) return;
                    var anim = prop.GetValue(cc, null) as Animator;
                    if (!ReferenceEquals(anim, null)) anim.Play(cn);
                }
                catch (Exception ex) { Log("Pose err: " + ex.Message); }
            }));
            return "{\"status\":\"ok\",\"pose\":\"" + Escape(name) + "\"}";
        }

        private string SetCamera(string query)
        {
            var x = Fl(query, "x", 0); var y = Fl(query, "y", 1); var z = Fl(query, "z", -2);
            var rx = Fl(query, "rotX", 0); var ry = Fl(query, "rotY", 0); var rz = Fl(query, "rotZ", 0);
            var fov = Fl(query, "fov", 23);
            UnityThreadHelper.Enqueue(new ThreadStart(() =>
            {
                try
                {
                    var cam = Camera.main;
                    if (ReferenceEquals(cam, null)) return;
                    cam.transform.position = new Vector3(x, y, z);
                    cam.transform.eulerAngles = new Vector3(rx, ry, rz);
                    cam.fieldOfView = fov;
                }
                catch (Exception ex) { Log("Camera err: " + ex.Message); }
            }));
            return "{\"ok\":true}";
        }

        private string AddCharacter(string query)
        {
            var indexStr = GetParam(query, "index") ?? "0";
            int index; if (!int.TryParse(indexStr, out index)) index = 0;
            UnityThreadHelper.Enqueue(new ThreadStart(() =>
            {
                try
                {
                    var studio = GetStudioInstance();
                    if (ReferenceEquals(studio, null)) { Log("Studio not ready"); return; }
                    var m = studio.GetType().GetMethod("AddFemale", BindingFlags.Public | BindingFlags.Instance);
                    if (ReferenceEquals(m, null)) { Log("AddFemale not found"); return; }
                    var charaDir = Path.Combine(Path.Combine(Application.dataPath, ".."), Path.Combine("UserData", Path.Combine("chara", "female")));
                    if (!Directory.Exists(charaDir)) { Log("Chara dir not found: " + charaDir); return; }
                    var files = Directory.GetFiles(charaDir, "*.png");
                    if (files.Length == 0) files = Directory.GetFiles(charaDir, "*.chara");
                    if (files.Length == 0) { Log("No character files found"); return; }
                    if (index < 0 || index >= files.Length) index = 0;
                    Log("Adding character: " + Path.GetFileName(files[index]));
                    m.Invoke(studio, new object[] { files[index] });
                }
                catch (Exception ex) { Log("AddChar err: " + ex.GetType().Name + " " + ex.Message); }
            }));
            return "{\"status\":\"adding\",\"index\":" + index + "}";
        }

        private string ListCharacters()
        {
            try
            {
                var studio = GetStudioInstance();
                if (ReferenceEquals(studio, null)) return "{\"error\":\"studio not ready\"}";
                var st = studio.GetType();

                var dicInfoField = st.GetField("dicInfo", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (ReferenceEquals(dicInfoField, null))
                {
                    return "{\"error\":\"dicInfo field not found\"}";
                }

                var dic = dicInfoField.GetValue(studio) as System.Collections.IDictionary;
                if (ReferenceEquals(dic, null)) return "{\"error\":\"dicInfo is null\"}";

                Log("dicInfo count: " + dic.Count);
                var sb = new StringBuilder("[");
                int idx = 0;
                foreach (System.Collections.DictionaryEntry entry in dic)
                {
                    var info = entry.Value;
                    if (ReferenceEquals(info, null)) continue;
                    var infoType = info.GetType();

                    var goProp = infoType.GetProperty("gpuObject", BindingFlags.Public | BindingFlags.Instance);
                    string goName = "";
                    if (!ReferenceEquals(goProp, null))
                    {
                        var go = goProp.GetValue(info, null) as GameObject;
                        if (!ReferenceEquals(go, null)) goName = go.name;
                    }

                    if (idx > 0) sb.Append(",");
                    sb.Append("{\"index\":" + idx + ",\"gameObject\":\"" + Escape(goName) + "\"}");
                    idx++;
                }
                sb.Append("]");
                return "{\"characters\":" + sb.ToString() + "}";
            }
            catch (Exception ex) { return "{\"error\":\"" + Escape(ex.Message) + "\"}"; }
        }

        private string SelectCharacter(string query)
        {
            var indexStr = GetParam(query, "index") ?? "0";
            int index; if (!int.TryParse(indexStr, out index)) index = 0;
            try
            {
                var studio = GetStudioInstance();
                if (ReferenceEquals(studio, null)) return "{\"error\":\"studio not ready\"}";
                var st = studio.GetType();
                var dicInfoField = st.GetField("dicInfo", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (ReferenceEquals(dicInfoField, null)) return "{\"error\":\"dicInfo not found\"}";
                var dic = dicInfoField.GetValue(studio) as System.Collections.IDictionary;
                if (ReferenceEquals(dic, null) || dic.Count == 0) return "{\"error\":\"no characters in scene\"}";

                var keys = new System.Collections.ArrayList(dic.Keys);
                if (index < 0 || index >= keys.Count) return "{\"error\":\"index out of range, count:" + keys.Count + "\"}";
                var node = keys[index];

                var ctrlProp = st.GetProperty("treeNodeCtrl", BindingFlags.Public | BindingFlags.Instance);
                if (ReferenceEquals(ctrlProp, null)) return "{\"error\":\"treeNodeCtrl not found\"}";
                var ctrl = ctrlProp.GetValue(studio, null);
                if (ReferenceEquals(ctrl, null)) return "{\"error\":\"treeNodeCtrl is null\"}";

                var selectProp = ctrl.GetType().GetProperty("selectNode", BindingFlags.Public | BindingFlags.Instance);
                if (ReferenceEquals(selectProp, null)) return "{\"error\":\"selectNode not found\"}";
                selectProp.SetValue(ctrl, node, null);

                Log("Selected character index: " + index);
                return "{\"status\":\"ok\",\"index\":" + index + "}";
            }
            catch (Exception ex) { return "{\"error\":\"" + Escape(ex.Message) + "\"}"; }
        }

        private string ExportGlb(string query)
        {
            var filename = GetParam(query, "filename");
            if (string.IsNullOrEmpty(filename)) filename = "export_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".glb";
            if (!filename.EndsWith(".glb")) filename += ".glb";
            var indexStr = GetParam(query, "index") ?? "0";
            int index; if (!int.TryParse(indexStr, out index)) index = 0;
            var resStr = GetParam(query, "resolution") ?? "2048";
            int resolution; if (!int.TryParse(resStr, out resolution) || resolution < 256) resolution = 2048;
            if (resolution > 4096) resolution = 4096;
            var bodyOnlyStr = GetParam(query, "bodyOnly");
            bool bodyOnly = bodyOnlyStr == "true" || bodyOnlyStr == "1";
            var includeHeadStr = GetParam(query, "includeHead");
            bool includeHead = includeHeadStr == "true" || includeHeadStr == "1";
            var includeHairStr = GetParam(query, "includeHair");
            bool includeHair = includeHairStr == "true" || includeHairStr == "1";
            var dir = UserDataPath("export");
            Directory.CreateDirectory(dir);
            var fullPath = Path.Combine(dir, filename);
            var capturedPath = fullPath;

            string resultJson = null;
            byte[] resultGlb = null;

            var done = new ManualResetEvent(false);

            UnityThreadHelper.Enqueue(new ThreadStart(() =>
            {
                try
                {
                    var cc = GetChaControlByIndex(index);
                    if (ReferenceEquals(cc, null)) { resultJson = "{\"error\":\"no character at index " + index + "\"}"; done.Set(); return; }

                    var allRenderers = FindAllRenderers(cc);
                    Log("EXPORT: found " + allRenderers.Count + " renderers");
                    for (int di = 0; di < allRenderers.Count; di++)
                    {
                        var dr = allRenderers[di];
                        var ds = dr.sharedMaterials.Length > 0 && !ReferenceEquals(dr.sharedMaterials[0], null) ? dr.sharedMaterials[0].shader.name : "none";
                        Log("EXPORT: [" + di + "] " + dr.name + " shader=" + ds);
                    }

                    if (bodyOnly)
                    {
                        allRenderers.RemoveAll(r =>
                        {
                            var s = r.sharedMaterials.Length > 0 && !ReferenceEquals(r.sharedMaterials[0], null)
                                ? r.sharedMaterials[0].shader.name : "";
                            bool isBody = s == "Shader Forge/main_skin";
                            bool isFace = s == "Shader Forge/main_face";
                            bool isHair = s == "Shader Forge/main_hair" || s == "Shader Forge/main_hair_front"
                                || s == "Koikano/hair_main_sun" || s == "Koikano/hair_main_sun_front"
                                || s == "xukmi/HairPlus" || s == "xukmi/HairFrontPlus";
                            if (!isBody && !isFace && !isHair) return true;
                            var n = r.name.ToLower();
                            if (n.Contains("dankon") || n.Contains("dan_f")) return true;
                            if (!includeHead && isFace) return true;
                            if (!includeHair && isHair) return true;
                            if (!includeHead && !isHair && (n.Contains("face") || n.StartsWith("cf_o_face") || n.Contains("eye") || n.Contains("tooth") || n.Contains("mayuge") || n.Contains("tang"))) return true;
                            return false;
                        });
                        Log("EXPORT: bodyOnly" + (includeHead ? "+head" : "") + (includeHair ? "+hair" : "") + " filtered to " + allRenderers.Count + " renderers");
                    }

                    if (allRenderers.Count == 0) { resultJson = "{\"error\":\"no renderers found\"}"; done.Set(); return; }

                    var meshEntries = new System.Collections.Generic.List<MeshEntry>();
                    int texOkCount = 0;

                    Color skinColor = GetSkinColorFromChaControl(cc);
                    _exportSkinColor = skinColor;
                    byte[] sharedBodyTex = null;
                    Vector3 bodyScale = Vector3.one;

                    for (int ri = 0; ri < allRenderers.Count; ri++)
                    {
                        var renderer = allRenderers[ri];
                        Log("EXPORT: [" + ri + "/" + allRenderers.Count + "] " + renderer.name + " verts=" + renderer.sharedMesh.vertexCount
                            + " pos=" + renderer.transform.position
                            + " scale=" + renderer.transform.lossyScale
                            + " shader=" + (renderer.sharedMaterials.Length > 0 && !ReferenceEquals(renderer.sharedMaterials[0], null) ? renderer.sharedMaterials[0].shader.name : "none"));

                        if (ri == 0) bodyScale = renderer.transform.lossyScale;

                        Mesh bakedMesh = new Mesh();
                        renderer.BakeMesh(bakedMesh);

                        byte[] texPng = null;

                        if (bodyOnly && ri == 0)
                        {
                            var rawTex = FindRawBodyTexture(cc);
                            if (!ReferenceEquals(rawTex, null))
                            {
                                var readable = MakeReadable(rawTex);
                                if (!ReferenceEquals(readable, null))
                                {
                                    texPng = GlbBuilder.EncodePngRaw(readable.GetPixels32(), readable.width, readable.height);
                                    SaveDebugTexture(texPng, "body_raw_texture.png");
                                    Log("EXPORT: raw body texture " + readable.width + "x" + readable.height + " png=" + texPng.Length);
                                    UnityEngine.Object.Destroy(readable);
                                }
                                else
                                {
                                    Log("EXPORT: raw texture not readable, trying RT copy");
                                    var rt2 = RenderTexture.GetTemporary(rawTex.width, rawTex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                                    Graphics.Blit(rawTex, rt2);
                            var prevActive = RenderTexture.active;
                                    RenderTexture.active = rt2;
                                    var t2d = new Texture2D(rawTex.width, rawTex.height, TextureFormat.RGBA32, false);
                                    t2d.ReadPixels(new Rect(0, 0, rawTex.width, rawTex.height), 0, 0);
                                    t2d.Apply();
                            RenderTexture.active = prevActive;
                                    RenderTexture.ReleaseTemporary(rt2);
                                    texPng = GlbBuilder.EncodePngRaw(t2d.GetPixels32(), t2d.width, t2d.height);
                                    SaveDebugTexture(texPng, "body_raw_texture.png");
                                    Log("EXPORT: raw body texture via RT " + t2d.width + "x" + t2d.height + " png=" + texPng.Length);
                                    UnityEngine.Object.Destroy(t2d);
                                }
                            }
                        }

                        if (texPng == null && bodyOnly)
                        {
                            var n = renderer.name.ToLower();
                            if (n.Contains("face") || n.StartsWith("cf_o_face"))
                            {
                                Log("EXPORT: trying face matDraw for " + renderer.name);
                                var faceTex = FindRawBodyTexture(cc, "face");
                                if (!ReferenceEquals(faceTex, null))
                                {
                                    var readable = MakeReadable(faceTex);
                                    if (!ReferenceEquals(readable, null))
                                    {
                                        texPng = GlbBuilder.EncodePngRaw(readable.GetPixels32(), readable.width, readable.height);
                                        SaveDebugTexture(texPng, "face_raw_texture.png");
                                        Log("EXPORT: face raw texture " + readable.width + "x" + readable.height + " png=" + texPng.Length);
                                        UnityEngine.Object.Destroy(readable);
                                    }
                                }
                            }
                        }

                        if (texPng == null && bodyOnly)
                        {
                            var n = renderer.name.ToLower();
                            if (n.Contains("hair"))
                            {
                                Log("EXPORT: extracting hair texture from material for " + renderer.name);
                                texPng = ExtractHairTextureFromMaterial(renderer, resolution);
                                if (texPng != null)
                                {
                                    Log("EXPORT: hair material texture " + texPng.Length + "b for " + renderer.name);
                                }
                            }
                        }

                        if (texPng == null && bodyOnly)
                        {
                            Log("EXPORT: UV bake via unlit for " + renderer.name);
                            var uvTex = BakeTextureViaUVRender(renderer, resolution);
                            if (!ReferenceEquals(uvTex, null))
                            {
                                texPng = GlbBuilder.EncodePngRaw(uvTex.GetPixels32(), uvTex.width, uvTex.height);
                                SaveDebugTexture(texPng, "body_uvbake_debug.png");
                                Log("EXPORT: UV bake produced " + uvTex.width + "x" + uvTex.height + " png=" + texPng.Length);
                                UnityEngine.Object.Destroy(uvTex);
                            }
                        }

                        if (texPng == null)
                        {
                            texPng = ExtractTextureForRenderer(renderer, resolution);
                        }

                        if (texPng == null)
                        {
                            Log("EXPORT: all extract failed for " + renderer.name);
                        }

                        if (texPng != null) texOkCount++;

                        var shaderName = renderer.sharedMaterials.Length > 0 && !ReferenceEquals(renderer.sharedMaterials[0], null)
                            ? renderer.sharedMaterials[0].shader.name : "";
                        bool isHair = shaderName.Contains("hair");

                        Color hc1 = Color.white, hc2 = Color.gray, hc3 = Color.black, hShadow = Color.gray, hLine = Color.white;
                        byte[] normalPng = null, specularPng = null, alphaPng = null;

                        if (isHair && renderer.sharedMaterials.Length > 0)
                        {
                            var hmat = renderer.sharedMaterials[0];
                            if (hmat.HasProperty("_Color")) hc1 = hmat.GetColor("_Color");
                            if (hmat.HasProperty("_Color2")) hc2 = hmat.GetColor("_Color2");
                            if (hmat.HasProperty("_Color3")) hc3 = hmat.GetColor("_Color3");
                            if (hmat.HasProperty("_ShadowColor")) hShadow = hmat.GetColor("_ShadowColor");
                            if (hmat.HasProperty("_LineColor")) hLine = hmat.GetColor("_LineColor");

                            var nm = hmat.GetTexture("NormalMap");
                            if (!ReferenceEquals(nm, null)) normalPng = ExtractAdditionalMap(nm, resolution, "normal");
                            var am = hmat.GetTexture("_AlphaMask");
                            if (!ReferenceEquals(am, null)) alphaPng = ExtractAdditionalMap(am, resolution, "alpha");

                            // Composite ColorMask with colors for consistent albedo (no lighting artifacts)
                            var colorMask = hmat.GetTexture("_ColorMask");
                            if (!ReferenceEquals(colorMask, null) && texPng != null)
                            {
                                var composited = CompositeHairAlbedo(colorMask, hc1, hc2, hc3, resolution);
                                if (!ReferenceEquals(composited, null))
                                {
                                    texPng = GlbBuilder.EncodePngRaw(composited.GetPixels32(), composited.width, composited.height);
                                    SaveDebugTexture(texPng, "hair_composited_" + renderer.name + ".png");
                                    Log("TEX: hair composited albedo " + composited.width + "x" + composited.height);
                                    UnityEngine.Object.Destroy(composited);
                                }
                            }

                            Log("TEX: hair colors C1=" + hc1 + " C2=" + hc2 + " C3=" + hc3 + " Shadow=" + hShadow + " Line=" + hLine);
                        }

                        meshEntries.Add(new MeshEntry
                        {
                            mesh = bakedMesh,
                            pngData = texPng,
                            normalPng = normalPng,
                            specularPng = specularPng,
                            alphaPng = alphaPng,
                            name = renderer.name,
                            shaderName = shaderName,
                            position = renderer.transform.position,
                            rotation = renderer.transform.rotation,
                            scale = bodyScale,
                            color1 = hc1,
                            color2 = hc2,
                            color3 = hc3,
                            shadowColor = hShadow,
                            lineColor = hLine,
                            isHair = isHair
                        });

                        Log("EXPORT: " + renderer.name + " tex=" + (texPng != null ? texPng.Length + "b" : "none"));
                    }

                    resultGlb = GlbBuilder.BuildMulti(meshEntries);
                    resultJson = "{\"status\":\"ok\",\"path\":\"" + Escape(capturedPath) + "\",\"size\":" + resultGlb.Length + ",\"meshCount\":" + meshEntries.Count + ",\"texturedCount\":" + texOkCount + ",\"resolution\":" + resolution + "}";
                }
                catch (Exception ex)
                {
                    Log("ExportGlb err: " + ex.GetType().Name + " " + ex.Message);
                    resultJson = "{\"error\":\"" + Escape(ex.Message) + "\"}";
                }
                done.Set();
            }));

            done.WaitOne(180000);

            if (resultGlb != null)
            {
                File.WriteAllBytes(capturedPath, resultGlb);
                Log("GLB exported: " + capturedPath + " (" + resultGlb.Length + " bytes)");
            }

            return resultJson ?? "{\"error\":\"timeout\"}";
        }

        private string DebugChaFile(string query)
        {
            var indexStr = GetParam(query, "index") ?? "0";
            int index; if (!int.TryParse(indexStr, out index)) index = 0;
            try
            {
                var cc = GetChaControlByIndex(index);
                if (ReferenceEquals(cc, null)) return "{\"error\":\"no character at index " + index + "\"}";

                var sb = new StringBuilder();
                sb.Append("{");

                var chaFileProp = cc.GetType().GetProperty("chaFile", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (!ReferenceEquals(chaFileProp, null))
                {
                    var chaFile = chaFileProp.GetValue(cc, null);
                    if (!ReferenceEquals(chaFile, null))
                    {
                        sb.Append("\"chaFile\":\"" + Escape(chaFile.GetType().FullName) + "\"");

                        var customField = chaFile.GetType().GetField("custom", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (!ReferenceEquals(customField, null))
                        {
                            var custom = customField.GetValue(chaFile);
                            if (!ReferenceEquals(custom, null))
                            {
                                sb.Append(",\"custom\":\"" + Escape(custom.GetType().FullName) + "\"");

                                var bodyField = custom.GetType().GetField("body", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                if (!ReferenceEquals(bodyField, null))
                                {
                                    var body = bodyField.GetValue(custom);
                                    if (!ReferenceEquals(body, null))
                                    {
                                        sb.Append(",\"body\":\"" + Escape(body.GetType().FullName) + "\"");
                                        DumpObjectFields(body, "", 0);

                                        foreach (var f in body.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                                        {
                                            try
                                            {
                                                var val = f.GetValue(body);
                                                if (ReferenceEquals(val, null)) continue;
                                                if (val is string s) sb.Append(",\"body." + f.Name + "\":\"" + Escape(s) + "\"");
                                                else if (val is int || val is float || val is bool) sb.Append(",\"body." + f.Name + "\":" + val);
                                            }
                                            catch { }
                                        }

                                        var skinField = body.GetType().GetField("skin", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                        if (!ReferenceEquals(skinField, null))
                                        {
                                            var skin = skinField.GetValue(body);
                                            if (!ReferenceEquals(skin, null))
                                            {
                                                sb.Append(",\"skin\":\"" + Escape(skin.GetType().FullName) + "\"");
                                                foreach (var f in skin.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                                                {
                                                    try
                                                    {
                                                        var val = f.GetValue(skin);
                                                        if (ReferenceEquals(val, null)) continue;
                                                        if (val is string s2) sb.Append(",\"skin." + f.Name + "\":\"" + Escape(s2) + "\"");
                                                        else if (val is int || val is float || val is bool) sb.Append(",\"skin." + f.Name + "\":" + val);
                                                        else if (val is Array arr2) sb.Append(",\"skin." + f.Name + "\":\"Array[" + arr2.Length + "]\"");
                                                    }
                                                    catch { }
                                                }
                                            }
                                        }
                                    }
                                }

                                var faceField = custom.GetType().GetField("face", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                if (!ReferenceEquals(faceField, null))
                                {
                                    var face = faceField.GetValue(custom);
                                    if (!ReferenceEquals(face, null))
                                    {
                                        sb.Append(",\"face\":\"" + Escape(face.GetType().FullName) + "\"");
                                        foreach (var f in face.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                                        {
                                            try
                                            {
                                                var val = f.GetValue(face);
                                                if (ReferenceEquals(val, null)) continue;
                                                if (val is string s3) sb.Append(",\"face." + f.Name + "\":\"" + Escape(s3) + "\"");
                                                else if (val is int || val is float || val is bool) sb.Append(",\"face." + f.Name + "\":" + val);
                                            }
                                            catch { }
                                        }
                                    }
                                }

                                var hairField = custom.GetType().GetField("hair", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                if (!ReferenceEquals(hairField, null))
                                {
                                    var hair = hairField.GetValue(custom);
                                    if (!ReferenceEquals(hair, null))
                                    {
                                        sb.Append(",\"hair\":\"" + Escape(hair.GetType().FullName) + "\"");
                                        foreach (var f in hair.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                                        {
                                            try
                                            {
                                                var val = f.GetValue(hair);
                                                if (ReferenceEquals(val, null)) continue;
                                                if (val is string s4) sb.Append(",\"hair." + f.Name + "\":\"" + Escape(s4) + "\"");
                                                else if (val is int || val is float || val is bool) sb.Append(",\"hair." + f.Name + "\":" + val);
                                            }
                                            catch { }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                var texDir = Path.Combine(Path.Combine(Path.Combine(Application.dataPath, ".."), "UserData"), "texture");
                sb.Append(",\"textureDirs\":[");
                if (Directory.Exists(texDir))
                {
                    var dirs = Directory.GetDirectories(texDir);
                    for (int i = 0; i < dirs.Length; i++)
                    {
                        if (i > 0) sb.Append(",");
                        var dirName = Path.GetFileName(dirs[i]);
                        var files = Directory.GetFiles(dirs[i], "*.png");
                        sb.Append("{\"name\":\"" + Escape(dirName) + "\",\"count\":" + files.Length);
                        if (files.Length > 0 && files.Length <= 5)
                        {
                            sb.Append(",\"files\":[");
                            for (int fi = 0; fi < files.Length; fi++)
                            {
                                if (fi > 0) sb.Append(",");
                                sb.Append("\"" + Escape(Path.GetFileName(files[fi])) + "\"");
                            }
                            sb.Append("]");
                        }
                        sb.Append("}");
                    }
                }
                sb.Append("]}");

                return sb.ToString();
            }
            catch (Exception ex) { return "{\"error\":\"" + Escape(ex.Message) + "\"}"; }
        }

        private byte[] ExtractTextureFromRenderTexture(RenderTexture rt, int resolution)
        {
            try
            {
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var t2d = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
                t2d.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                t2d.Apply();
                RenderTexture.active = prev;
                var png = GlbBuilder.EncodePngRaw(t2d.GetPixels32(), t2d.width, t2d.height);
                UnityEngine.Object.Destroy(t2d);
                Log("TEX: camera RT read " + rt.width + "x" + rt.height + " png=" + png.Length);
                return png;
            }
            catch (Exception ex) { Log("TEX: camera RT read failed: " + ex.Message); return null; }
        }

        private RenderTexture RenderBodyViaCamera(ChaControl cc, System.Collections.Generic.List<SkinnedMeshRenderer> bodyRenderers, int resolution)
        {
            try
            {
                Bounds bodyBounds = bodyRenderers[0].bounds;
                for (int i = 1; i < bodyRenderers.Count; i++)
                    bodyBounds.Encapsulate(bodyRenderers[i].bounds);

                Vector3 center = bodyBounds.center;
                float size = bodyBounds.extents.y * 2.5f;
                if (size < 0.1f) size = 1f;

                var rt = RenderTexture.GetTemporary(resolution, resolution, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);

                var camObj = new GameObject("_BodyCaptureCam");
                var cam = camObj.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0, 0, 0, 0);
                cam.orthographic = true;
                cam.orthographicSize = size;
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = 100f;
                cam.transform.position = center + new Vector3(0, 0, -size * 4);
                cam.transform.LookAt(center);
                cam.targetTexture = rt;

                cam.Render();

                var result = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                Graphics.Blit(rt, result);

                UnityEngine.Object.DestroyImmediate(camObj);
                RenderTexture.ReleaseTemporary(rt);

                Log("TEX: body camera render done " + resolution + "x" + resolution);
                return result;
            }
            catch (Exception ex) { Log("TEX: body camera render failed: " + ex.GetType().Name + " " + ex.Message); return null; }
        }

        private void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            for (int i = 0; i < go.transform.childCount; i++)
                SetLayerRecursive(go.transform.GetChild(i).gameObject, layer);
        }

        private byte[] ExtractAdditionalMap(Texture tex, int resolution, string mapType)
        {
            try
            {
                int w = Math.Min(tex.width, resolution);
                int h = Math.Min(tex.height, resolution);
                var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                Graphics.Blit(tex, rt);
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var t2d = new Texture2D(w, h, TextureFormat.RGBA32, false);
                t2d.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                t2d.Apply();
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                var png = GlbBuilder.EncodePngRaw(t2d.GetPixels32(), t2d.width, t2d.height);
                SaveDebugTexture(png, mapType + "_map_" + w + "x" + h + ".png");
                Log("TEX: " + mapType + " map extracted " + w + "x" + h + " png=" + png.Length);
                UnityEngine.Object.Destroy(t2d);
                return png;
            }
            catch (Exception ex) { Log("TEX: ExtractAdditionalMap(" + mapType + ") error: " + ex.Message); return null; }
        }

        private Texture2D CompositeHairAlbedo(Texture colorMask, Color c1, Color c2, Color c3, int resolution)
        {
            try
            {
                int w = Math.Min(colorMask.width, resolution);
                int h = Math.Min(colorMask.height, resolution);

                var maskRt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                Graphics.Blit(colorMask, maskRt);
                var prev = RenderTexture.active;
                RenderTexture.active = maskRt;
                var maskTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                maskTex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                maskTex.Apply();
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(maskRt);

                var maskPixels = maskTex.GetPixels32();
                var result = new Color32[maskPixels.Length];
                for (int i = 0; i < maskPixels.Length; i++)
                {
                    float mr = maskPixels[i].r / 255f;
                    float mg = maskPixels[i].g / 255f;
                    float mb = maskPixels[i].b / 255f;
                    float r = c2.r * mr + c1.r * mg + c3.r * mb;
                    float g = c2.g * mr + c1.g * mg + c3.g * mb;
                    float b = c2.b * mr + c1.b * mg + c3.b * mb;
                    result[i] = new Color32(
                        (byte)Math.Min(255, (int)(r * 255)),
                        (byte)Math.Min(255, (int)(g * 255)),
                        (byte)Math.Min(255, (int)(b * 255)),
                        255);
                }

                var output = new Texture2D(w, h, TextureFormat.RGBA32, false);
                output.SetPixels32(result);
                output.Apply();
                UnityEngine.Object.Destroy(maskTex);
                return output;
            }
            catch (Exception ex) { Log("TEX: CompositeHairAlbedo error: " + ex.Message); return null; }
        }

        private void SaveDebugTexture(byte[] pngData, string filename)
        {
            try
            {
                var dir = UserDataPath("export");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, filename);
                File.WriteAllBytes(path, pngData);
                Log("DEBUG: saved " + path + " (" + pngData.Length + " bytes)");
            }
            catch (Exception ex) { Log("DEBUG: save failed: " + ex.Message); }
        }

        private ChaControl GetChaControlByIndex(int index)
        {
            try
            {
                var studio = GetStudioInstance();
                if (ReferenceEquals(studio, null)) return null;
                var st = studio.GetType();
                var dicInfoField = st.GetField("dicInfo", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (ReferenceEquals(dicInfoField, null)) return null;
                var dic = dicInfoField.GetValue(studio) as System.Collections.IDictionary;
                if (ReferenceEquals(dic, null) || dic.Count == 0) return null;

                var keys = new System.Collections.ArrayList(dic.Keys);
                if (index < 0 || index >= keys.Count) return null;
                var node = keys[index];
                var info = dic[node];
                if (ReferenceEquals(info, null)) return null;

                var charInfoField = info.GetType().GetField("charInfo", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (!ReferenceEquals(charInfoField, null))
                {
                    var cc = charInfoField.GetValue(info) as ChaControl;
                    if (!ReferenceEquals(cc, null)) return cc;
                }

                return null;
            }
            catch { return null; }
        }

        private SkinnedMeshRenderer FindBodyRenderer(ChaControl cc)
        {
            SkinnedMeshRenderer best = null;
            int bestVerts = 0;
            FindBodyRendererRecursive(cc.transform, ref best, ref bestVerts);
            return best;
        }

        private void FindBodyRendererRecursive(Transform t, ref SkinnedMeshRenderer best, ref int bestVerts)
        {
            var smr = t.GetComponent<SkinnedMeshRenderer>();
            if (!ReferenceEquals(smr, null) && !ReferenceEquals(smr.sharedMesh, null))
            {
                int v = smr.sharedMesh.vertexCount;
                if (v > bestVerts) { bestVerts = v; best = smr; }
            }
            for (int i = 0; i < t.childCount; i++)
                FindBodyRendererRecursive(t.GetChild(i), ref best, ref bestVerts);
        }

        private System.Collections.Generic.List<SkinnedMeshRenderer> FindAllRenderers(ChaControl cc)
        {
            var renderers = new System.Collections.Generic.List<SkinnedMeshRenderer>();
            FindAllRenderersRecursive(cc.transform, renderers);
            renderers.Sort((a, b) => b.sharedMesh.vertexCount.CompareTo(a.sharedMesh.vertexCount));
            return renderers;
        }

        private void FindAllRenderersRecursive(Transform t, System.Collections.Generic.List<SkinnedMeshRenderer> list)
        {
            var smr = t.GetComponent<SkinnedMeshRenderer>();
            if (!ReferenceEquals(smr, null) && !ReferenceEquals(smr.sharedMesh, null) && smr.sharedMesh.vertexCount > 100)
                list.Add(smr);
            for (int i = 0; i < t.childCount; i++)
                FindAllRenderersRecursive(t.GetChild(i), list);
        }

        private Texture2D BakeTextureViaUVRender(SkinnedMeshRenderer renderer, int resolution)
        {
            try
            {
                Mesh bakedMesh = new Mesh();
                renderer.BakeMesh(bakedMesh);

                var srcUV = bakedMesh.uv;
                if (srcUV == null || srcUV.Length == 0) { Log("TEX: no UVs for UV bake"); return null; }

                int vertCount = srcUV.Length;
                var uvVerts = new Vector3[vertCount];
                for (int i = 0; i < vertCount; i++)
                    uvVerts[i] = new Vector3(srcUV[i].x, srcUV[i].y, 0);

                var uvMesh = new Mesh();
                uvMesh.vertices = uvVerts;
                uvMesh.triangles = bakedMesh.triangles;
                uvMesh.uv = srcUV;
                var camNorms = new Vector3[vertCount];
                for (int i = 0; i < vertCount; i++) camNorms[i] = new Vector3(0, 0, -1);
                uvMesh.normals = camNorms;
                uvMesh.RecalculateBounds();

                int uvLayer = 30;

                var rt = RenderTexture.GetTemporary(resolution, resolution, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                var camObj = new GameObject("_UVBakeCam_" + renderer.name);
                var cam = camObj.AddComponent<Camera>();
                cam.orthographic = true;
                cam.orthographicSize = 0.5f;
                cam.transform.position = new Vector3(0.5f, 0.5f, -10);
                cam.transform.LookAt(new Vector3(0.5f, 0.5f, 0));
                cam.backgroundColor = new Color(0, 0, 0, 0);
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.targetTexture = rt;
                cam.cullingMask = 1 << uvLayer;
                cam.depth = -999;

                var meshObj = new GameObject("_UVBakeObj_" + renderer.name);
                meshObj.layer = uvLayer;
                var mf = meshObj.AddComponent<MeshFilter>();
                mf.mesh = uvMesh;
                var mr = meshObj.AddComponent<MeshRenderer>();

                var unlitShader = Shader.Find("Unlit/Texture");
                if (ReferenceEquals(unlitShader, null)) unlitShader = Shader.Find("Sprites/Default");
                var bakeMat = new Material(unlitShader);

                var srcTex = renderer.sharedMaterials.Length > 0 && !ReferenceEquals(renderer.sharedMaterials[0], null)
                    ? renderer.sharedMaterials[0].mainTexture : null;
                if (!ReferenceEquals(srcTex, null) && srcTex is RenderTexture srcRT)
                {
                    var tmp = RenderTexture.GetTemporary(srcRT.width, srcRT.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                    Graphics.Blit(srcRT, tmp);
                    bakeMat.mainTexture = tmp;
                }
                else if (!ReferenceEquals(srcTex, null))
                {
                    bakeMat.mainTexture = srcTex;
                }
                else
                {
                    var hairMat = new Material(renderer.sharedMaterials[0]);
                    if (hairMat.HasProperty("ShadowColor")) hairMat.SetColor("ShadowColor", Color.white);
                    if (hairMat.HasProperty("ShadowExtend")) hairMat.SetFloat("ShadowExtend", 0f);
                    if (hairMat.HasProperty("rimV")) hairMat.SetFloat("rimV", 0f);
                    if (hairMat.HasProperty("rimpower")) hairMat.SetFloat("rimpower", 0f);
                    UnityEngine.Object.DestroyImmediate(bakeMat);
                    bakeMat = hairMat;
                    Log("TEX: UV bake using flat-lit material " + bakeMat.shader.name);
                }

                mr.sharedMaterial = bakeMat;

                cam.Render();

                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(resolution, resolution, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, resolution, resolution), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);

                UnityEngine.Object.DestroyImmediate(camObj);
                UnityEngine.Object.DestroyImmediate(meshObj);
                UnityEngine.Object.DestroyImmediate(uvMesh);
                UnityEngine.Object.DestroyImmediate(bakeMat);

                Log("TEX: UV bake done " + tex.width + "x" + tex.height + " for " + renderer.name);
                return tex;
            }
            catch (Exception ex) { Log("TEX: UV bake failed: " + ex.Message); return null; }
        }

        private byte[] ExtractHairTextureFromMaterial(SkinnedMeshRenderer renderer, int resolution)
        {
            try
            {
                var mats = renderer.sharedMaterials;
                if (mats.Length == 0) { Log("TEX: hair no materials for " + renderer.name); return null; }

                // Strategy A: Graphics.Blit with the hair material (runs full shader pipeline)
                var hairMat = new Material(mats[0]);
                var rt = RenderTexture.GetTemporary(resolution, resolution, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                var whiteTex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
                var whitePx = new Color32[16];
                for (int i = 0; i < 16; i++) whitePx[i] = new Color32(255, 255, 255, 255);
                whiteTex.SetPixels32(whitePx);
                whiteTex.Apply();

                Graphics.Blit(whiteTex, rt, hairMat);

                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(resolution, resolution, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, resolution, resolution), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.DestroyImmediate(whiteTex);
                UnityEngine.Object.DestroyImmediate(hairMat);

                // Check if Blit produced non-empty output
                var px = tex.GetPixel(resolution / 2, resolution / 2);
                if (px.a > 0.01f && (px.r + px.g + px.b) > 0.1f)
                {
                    var png = GlbBuilder.EncodePngRaw(tex.GetPixels32(), tex.width, tex.height);
                    SaveDebugTexture(png, "hair_blit_" + renderer.name + ".png");
                    Log("TEX: hair Graphics.Blit " + tex.width + "x" + tex.height + " png=" + png.Length + " sample=" + px);
                    UnityEngine.Object.Destroy(tex);
                    return png;
                }
                Log("TEX: hair Graphics.Blit produced empty texture, falling back to UV bake");
                UnityEngine.Object.Destroy(tex);

                return null;
            }
            catch (Exception ex) { Log("TEX: ExtractHairTextureFromMaterial error: " + ex.GetType().Name + " " + ex.Message); return null; }
        }

        private byte[] ExtractHairProperties(SkinnedMeshRenderer renderer)
        {
            try
            {
                var mat = renderer.sharedMaterials.Length > 0 ? renderer.sharedMaterials[0] : null;
                if (ReferenceEquals(mat, null)) return null;

                var colorMask = mat.GetTexture("_ColorMask");
                var hairGloss = mat.GetTexture("_HairGloss");
                var alphaMask = mat.GetTexture("_AlphaMask");
                var normalMap = mat.GetTexture("NormalMap");
                var mainTex = mat.mainTexture;

                Color c1 = mat.HasProperty("_Color") ? mat.GetColor("_Color") : Color.white;
                Color c2 = mat.HasProperty("_Color2") ? mat.GetColor("_Color2") : Color.gray;
                Color c3 = mat.HasProperty("_Color3") ? mat.GetColor("_Color3") : Color.black;
                Color lineColor = mat.HasProperty("_LineColor") ? mat.GetColor("_LineColor") : Color.white;
                Color shadowColor = mat.HasProperty("_ShadowColor") ? mat.GetColor("_ShadowColor") : new Color(0.3f, 0.3f, 0.3f, 1f);

                Log("TEX: hair props C1=" + c1 + " C2=" + c2 + " C3=" + c3
                    + " Line=" + lineColor + " Shadow=" + shadowColor
                    + " MainTex=" + (ReferenceEquals(mainTex, null) ? "null" : mainTex.width + "x" + mainTex.height)
                    + " Mask=" + (ReferenceEquals(colorMask, null) ? "null" : colorMask.width + "x" + colorMask.height)
                    + " Gloss=" + (ReferenceEquals(hairGloss, null) ? "null" : hairGloss.width + "x" + hairGloss.height)
                    + " Normal=" + (ReferenceEquals(normalMap, null) ? "null" : normalMap.width + "x" + normalMap.height));

                return null;
            }
            catch (Exception ex) { Log("TEX: ExtractHairProperties error: " + ex.Message); return null; }
        }

        private Texture2D MakeReadable2D(Texture src, int maxResolution)
        {
            try
            {
                int w = Math.Min(src.width, maxResolution);
                int h = Math.Min(src.height, maxResolution);

                var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                GL.Clear(false, true, new Color(0, 0, 0, 0));
                Graphics.Blit(src, rt);
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var readable = new Texture2D(w, h, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                readable.Apply();
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                return readable;
            }
            catch (Exception ex) { Log("TEX: MakeReadable2D error: " + ex.Message); return null; }
        }

        private Texture2D ExtractCompositedTexture(Material material, int resolution)
        {
            try
            {
                Texture srcTex = null;
                string[] baseNames = { "_ColorTex", "_MainTex", "_DiffuseTex", "_AlbedoTex", "_BaseMap", "_SkinTex", "_BodyTex" };
                foreach (var bn in baseNames)
                {
                    var t = material.GetTexture(bn);
                    if (!ReferenceEquals(t, null))
                    {
                        srcTex = t;
                        Log("TEX: composite source=" + bn);
                        break;
                    }
                }
                if (ReferenceEquals(srcTex, null))
                {
                    srcTex = material.mainTexture;
                    if (ReferenceEquals(srcTex, null)) { Log("TEX: composite no source"); return null; }
                }

                var tempMat = new Material(material);
                var rt = RenderTexture.GetTemporary(resolution, resolution, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                Graphics.Blit(srcTex, rt, tempMat);

                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(resolution, resolution, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, resolution, resolution), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.DestroyImmediate(tempMat);

                Log("TEX: composited " + tex.width + "x" + tex.height);
                return tex;
            }
            catch (Exception ex) { Log("TEX: composite failed: " + ex.Message); return null; }
        }

        private void DumpMaterialTextures(Material mat, int matIndex)
        {
            try
            {
                var shader = mat.shader;
                Log("TEX: shader=" + shader.name);

                string[] knownProps = {
                    "_MainTex", "_ColorTex", "_DiffuseTex", "_AlbedoTex", "_BaseMap",
                    "_ShadowTex", "_DetailTex", "_DetailMask", "_SubTex",
                    "_NormalTex", "_BumpTex", "_SpecularTex", "_RampTex",
                    "_ShadowColorMultiplyTex", "_ShadowColorTexture",
                    "_TexID", "_MainTex2", "_Diffuse", "_Albedo",
                    "_SkinTex", "_BodyTex", "_BodyTex2", "_FaceTex",
                    "_HairTex", "_AccessTex", "_ColorMask", "_SubMask",
                    "_DetailMask2", "_EyeTex", "_EyeHiTex", "_EyeHighLightTex",
                    "_GradientTex", "_MetallicGlossMap", "_OcclusionMap",
                    "_EmissionMap", "_BumpMap", "_ParallaxMap",
                    "_lightMap", "_MaskTex", "_AlphaMask",
                    "_colorTex", "_shadowTex", "_detailTex", "_subTex",
                    "_tex1", "_tex2", "_texture",
                    "_ReflectionTex", "_GrabTexture",
                    "_EyehlUpTex", "_EyeHiUpTex", "_EyeHLUpTex"
                };

                int foundCount = 0;
                foreach (var pn in knownProps)
                {
                    var t = mat.GetTexture(pn);
                    if (!ReferenceEquals(t, null))
                    {
                        Log("TEX: mat[" + matIndex + "] " + pn + " = " + t.GetType().Name + " " + t.width + "x" + t.height + " name=" + t.name);
                        foundCount++;
                    }
                }

                if (foundCount == 0)
                {
                    Log("TEX: mat[" + matIndex + "] no textures found in known properties, trying ShaderUtil...");
                    try
                    {
                        var shaderUtilType = FindType("UnityEditor.ShaderUtil");
                        if (!ReferenceEquals(shaderUtilType, null))
                        {
                            var propCountMethod = shaderUtilType.GetMethod("GetPropertyCount", BindingFlags.Public | BindingFlags.Static);
                            if (!ReferenceEquals(propCountMethod, null))
                            {
                                int count = (int)propCountMethod.Invoke(null, new object[] { shader });
                                Log("TEX: ShaderUtil reports " + count + " properties");
                                for (int pi = 0; pi < count; pi++)
                                {
                                    var nameMethod = shaderUtilType.GetMethod("GetPropertyName", BindingFlags.Public | BindingFlags.Static);
                                    var typeMethod = shaderUtilType.GetMethod("GetPropertyType", BindingFlags.Public | BindingFlags.Static);
                                    if (!ReferenceEquals(nameMethod, null) && !ReferenceEquals(typeMethod, null))
                                    {
                                        var propName = (string)nameMethod.Invoke(null, new object[] { shader, pi });
                                        var propType = typeMethod.Invoke(null, new object[] { shader, pi });
                                        var propTypeStr = propType.ToString();
                                        Log("TEX: prop[" + pi + "] name=" + propName + " type=" + propTypeStr);
                                        if (propTypeStr.Contains("Tex") || propTypeStr.Contains("Texture"))
                                        {
                                            var t = mat.GetTexture(propName);
                                            if (!ReferenceEquals(t, null))
                                                Log("TEX:   -> " + t.GetType().Name + " " + t.width + "x" + t.height);
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex) { Log("TEX: ShaderUtil reflection failed: " + ex.Message); }
                }
            }
            catch (Exception ex) { Log("TEX: DumpMaterialTextures error: " + ex.Message); }
        }

        private Texture2D FindRawBodyTexture(ChaControl cc, string part = "body")
        {
            try
            {
                var ccType = cc.GetType();
                string ctrlName = "customTexCtrl" + part.Substring(0, 1).ToUpper() + part.Substring(1);

                var prop = ccType.GetProperty(ctrlName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (ReferenceEquals(prop, null))
                {
                    foreach (var p in ccType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        if (p.Name.ToLower().Contains("customtex") && p.Name.ToLower().Contains(part))
                        {
                            prop = p;
                            Log("TEX: found " + ctrlName + " via search: " + p.Name);
                            break;
                        }
                    }
                }

                if (ReferenceEquals(prop, null)) { Log("TEX: " + ctrlName + " not found"); return null; }

                var texCtrl = prop.GetValue(cc, null);
                if (ReferenceEquals(texCtrl, null)) { Log("TEX: " + ctrlName + " is null"); return null; }

                Log("TEX: " + ctrlName + " type=" + texCtrl.GetType().FullName);

                // Strategy 1: matDraw.mainTexture (FINAL composited result)
                Material matDraw = null;

                // Try field first
                var matDrawField = texCtrl.GetType().GetField("matDraw", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (!ReferenceEquals(matDrawField, null))
                    matDraw = matDrawField.GetValue(texCtrl) as Material;

                // Try property
                if (ReferenceEquals(matDraw, null))
                {
                    var matDrawProp = texCtrl.GetType().GetProperty("matDraw", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (!ReferenceEquals(matDrawProp, null))
                        matDraw = matDrawProp.GetValue(texCtrl, null) as Material;
                }

                // Try backing field
                if (ReferenceEquals(matDraw, null))
                {
                    foreach (var f in texCtrl.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        if (f.Name.Contains("matDraw"))
                        {
                            matDraw = f.GetValue(texCtrl) as Material;
                            Log("TEX: found matDraw via field: " + f.Name);
                            break;
                        }
                    }
                }

                if (!ReferenceEquals(matDraw, null))
                {
                    var drawTex = matDraw.mainTexture;
                    if (!ReferenceEquals(drawTex, null))
                    {
                        Log("TEX: matDraw.mainTexture=" + drawTex.width + "x" + drawTex.height + " type=" + drawTex.GetType().Name);
                        var readable = ReadTextureToReadable(drawTex);
                        if (!ReferenceEquals(readable, null))
                        {
                            Log("TEX: matDraw texture extracted " + readable.width + "x" + readable.height);
                            return readable;
                        }
                    }
                    var alphaTex = matDraw.GetTexture("_AlphaMask");
                    if (!ReferenceEquals(alphaTex, null))
                        Log("TEX: matDraw._AlphaMask=" + alphaTex.width + "x" + alphaTex.height);
                }
                else
                {
                    Log("TEX: matDraw not found on " + ctrlName);
                }

                Log("TEX: matDraw not available, falling back to texMain/matCreate...");

                // Strategy 2: texMain (raw base texture)
                var texMainField = texCtrl.GetType().GetField("texMain", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (!ReferenceEquals(texMainField, null))
                {
                    var t = texMainField.GetValue(texCtrl) as Texture;
                    if (!ReferenceEquals(t, null))
                    {
                        var readable = ReadTextureToReadable(t);
                        if (!ReferenceEquals(readable, null))
                        {
                            var px = readable.GetPixel(readable.width / 2, readable.height / 2);
                            Log("TEX: texMain sample color=" + px);
                            if (px.a > 0.01f && (px.r + px.g + px.b) > 0.1f && !(px.r > 0.99f && px.g > 0.99f && px.b > 0.99f))
                            {
                                Log("TEX: texMain has content");
                                return readable;
                            }
                            Log("TEX: texMain is empty, trying matCreate...");
                            UnityEngine.Object.Destroy(readable);
                        }
                    }
                }

                // Strategy 3: matCreate + createTex composited
                var matCreateField = texCtrl.GetType().GetField("matCreate", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var createTexField = texCtrl.GetType().GetField("createTex", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (!ReferenceEquals(matCreateField, null) && !ReferenceEquals(createTexField, null))
                {
                    var matCreate = matCreateField.GetValue(texCtrl) as Material;
                    var createTex = createTexField.GetValue(texCtrl) as Texture;
                    if (!ReferenceEquals(matCreate, null) && !ReferenceEquals(createTex, null))
                    {
                        Log("TEX: matCreate shader=" + matCreate.shader.name + " createTex=" + createTex.width + "x" + createTex.height);
                        var composed = new RenderTexture(createTex.width, createTex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                        Graphics.Blit(createTex, composed, matCreate);
                        var prevComp = RenderTexture.active;
                        RenderTexture.active = composed;
                        var result = new Texture2D(composed.width, composed.height, TextureFormat.RGBA32, false);
                        result.ReadPixels(new Rect(0, 0, composed.width, composed.height), 0, 0);
                        result.Apply();
                        RenderTexture.active = prevComp;
                        RenderTexture.ReleaseTemporary(composed);
                        Log("TEX: matCreate composited " + result.width + "x" + result.height);
                        return result;
                    }
                }

                // Strategy 4: largest Texture field
                Texture bestTex = null;
                int bestSize = 0;
                foreach (var f in texCtrl.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    try
                    {
                        if (!typeof(Texture).IsAssignableFrom(f.FieldType)) continue;
                        var tex = f.GetValue(texCtrl) as Texture;
                        if (ReferenceEquals(tex, null) || tex.width < 256) continue;
                        if (tex.width * tex.height > bestSize)
                        {
                            bestTex = tex;
                            bestSize = tex.width * tex.height;
                            Log("TEX: fallback texture: " + f.Name + " " + tex.width + "x" + tex.height);
                        }
                    }
                    catch { }
                }

                if (ReferenceEquals(bestTex, null))
                {
                    Log("TEX: no suitable texture found on " + ctrlName);
                    return null;
                }

                return ReadTextureToReadable(bestTex);
            }
            catch (Exception ex) { Log("TEX: FindRawBodyTexture error: " + ex.GetType().Name + " " + ex.Message); return null; }
        }

        private Texture2D ReadTextureToReadable(Texture src)
        {
            try
            {
                if (src is Texture2D t2d) return t2d;
                var rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                GL.Clear(false, true, new Color(0, 0, 0, 0));
                Graphics.Blit(src, rt);
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var readable = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
                readable.Apply();
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                return readable;
            }
            catch { return null; }
        }

        private byte[] ExtractTextureForRenderer(SkinnedMeshRenderer renderer, int resolution)
        {
            try
            {
                var mats = renderer.sharedMaterials;
                if (mats.Length == 0) return null;

                for (int mi = 0; mi < mats.Length; mi++)
                {
                    var mat = mats[mi];
                    if (ReferenceEquals(mat, null)) continue;

                    Log("TEX: renderer=" + renderer.name + " mat[" + mi + "] shader=" + mat.shader.name);

                    DumpMaterialTextures(mat, mi);

                    if (!ReferenceEquals(mat.GetTexture("_DetailMask"), null))
                    {
                        var dm = mat.GetTexture("_DetailMask");
                        var dm2d = dm as Texture2D;
                        if (!ReferenceEquals(dm2d, null))
                        {
                            var readable = MakeReadable(dm2d);
                            if (!ReferenceEquals(readable, null))
                            {
                                var dmPng = GlbBuilder.EncodePngRaw(readable.GetPixels32(), readable.width, readable.height);
                                SaveDebugTexture(dmPng, "detail_mask_" + renderer.name + ".png");
                                UnityEngine.Object.Destroy(readable);
                                Log("TEX: _DetailMask saved: " + dm.name + " " + dm.width + "x" + dm.height);
                            }
                        }
                    }

                    if (!ReferenceEquals(mat.mainTexture, null))
                    {
                        if (mat.mainTexture is RenderTexture rt)
                        {
                            int w = Math.Min(rt.width, resolution);
                            int h = Math.Min(rt.height, resolution);
                            var dst = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);

                            Texture2D baseTex2d = null;
                            string[] baseNames = { "_ColorTex", "_MainTex", "_DiffuseTex", "_AlbedoTex", "_BaseMap", "_SkinTex", "_BodyTex", "_SubTex" };
                            foreach (var bn in baseNames)
                            {
                                var bt = mat.GetTexture(bn);
                                if (!ReferenceEquals(bt, null) && !(bt is RenderTexture))
                                {
                                    baseTex2d = bt as Texture2D;
                                    if (!ReferenceEquals(baseTex2d, null)) { Log("TEX: base texture found: " + bn + " " + baseTex2d.width + "x" + baseTex2d.height); break; }
                                }
                            }

                            if (!ReferenceEquals(baseTex2d, null))
                            {
                                var tempMat = new Material(mat);
                                Graphics.Blit(baseTex2d, dst, tempMat);
                                UnityEngine.Object.DestroyImmediate(tempMat);
                                Log("TEX: composited via shader from " + baseTex2d.name);
                            }
                            else
                            {
                                Graphics.Blit(rt, dst);
                                Log("TEX: RenderTexture copy");
                            }

                            var prev = RenderTexture.active;
                            RenderTexture.active = dst;
                            var t2d = new Texture2D(w, h, TextureFormat.RGBA32, false);
                            t2d.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                            t2d.Apply();
                            RenderTexture.active = prev;
                            RenderTexture.ReleaseTemporary(dst);

                            var png = GlbBuilder.EncodePngRaw(t2d.GetPixels32(), t2d.width, t2d.height);
                            UnityEngine.Object.Destroy(t2d);
                            Log("TEX: extracted " + w + "x" + h);
                            return png;
                        }

                        var composited = ExtractCompositedTexture(mat, resolution);
                        if (!ReferenceEquals(composited, null))
                        {
                            var png = GlbBuilder.EncodePngRaw(composited.GetPixels32(), composited.width, composited.height);
                            UnityEngine.Object.Destroy(composited);
                            return png;
                        }
                    }

                    string[] propNames = { "_ColorTex", "_MainTex", "_DiffuseTex", "_AlbedoTex", "_BaseMap", "_DetailTex", "_DetailMask", "_ShadowTex", "_ShadowColorMultiplyTex", "_ShadowColorTexture", "_SubTex", "_TexID", "_MainTex2", "_Diffuse", "_Albedo", "_SkinTex", "_BodyTex", "_BodyTex2", "_FaceTex", "_HairTex", "_NormalTex", "_BumpTex", "_SpecularTex", "_RampTex", "_GradientTex", "_AccessTex", "_ColorMask", "_SubMask", "_DetailMask2", "_EyeTex", "_EyeHiTex", "_EyeHighLightTex" };
                    for (int pi = 0; pi < propNames.Length; pi++)
                    {
                        var tex = mat.GetTexture(propNames[pi]);
                        if (ReferenceEquals(tex, null)) continue;

                        var rt2 = RenderTexture.GetTemporary(Math.Min(tex.width, resolution), Math.Min(tex.height, resolution), 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                        Graphics.Blit(tex, rt2);
                        var prev2 = RenderTexture.active;
                        RenderTexture.active = rt2;
                        var t2 = new Texture2D(rt2.width, rt2.height, TextureFormat.RGBA32, false);
                        t2.ReadPixels(new Rect(0, 0, rt2.width, rt2.height), 0, 0);
                        t2.Apply();
                        RenderTexture.active = prev2;
                        RenderTexture.ReleaseTemporary(rt2);
                        var png2 = GlbBuilder.EncodePngRaw(t2.GetPixels32(), t2.width, t2.height);
                        UnityEngine.Object.Destroy(t2);
                        Log("TEX: property " + propNames[pi] + " extracted " + t2.width + "x" + t2.height);
                        return png2;
                    }
                }

                Log("TEX: no texture found for " + renderer.name);
                return null;
            }
            catch (Exception ex) { Log("TEX: extract error: " + ex.Message); return null; }
        }

        private Texture2D ExtractTexture(SkinnedMeshRenderer renderer)
        {
            try
            {
                if (ReferenceEquals(renderer, null) || renderer.sharedMaterials.Length == 0) { Log("TEX: no renderer or materials"); return null; }
                var mat = renderer.sharedMaterials[0];
                if (ReferenceEquals(mat, null)) { Log("TEX: material null"); return null; }
                Log("TEX: shader=" + mat.shader.name + " matCount=" + renderer.sharedMaterials.Length);

                Texture2D tex = null;

                for (int mi = 0; mi < renderer.sharedMaterials.Length && ReferenceEquals(tex, null); mi++)
                {
                    var m = renderer.sharedMaterials[mi];
                    if (ReferenceEquals(m, null)) continue;
                    Log("TEX: mat[" + mi + "]=" + m.name + " shader=" + m.shader.name);

                    if (!ReferenceEquals(m.mainTexture, null) && m.mainTexture is Texture2D t2)
                    {
                        tex = t2;
                        Log("TEX: found via mainTexture on mat[" + mi + "] " + t2.width + "x" + t2.height);
                        break;
                    }

                    string[] propNames = { "_MainTex", "_MainTex1", "_MainTex2", "_DiffuseTex", "_Diffuse", "_AlbedoTex", "_Albedo", "_BaseMap", "_ColorTex", "_SkinTex", "_BodyTex", "_BodyTex2", "_tex1", "_tex2", "_texture", "_colorTex", "_normalTex" };
                    for (int i = 0; i < propNames.Length; i++)
                    {
                        var t = m.GetTexture(propNames[i]) as Texture2D;
                        if (!ReferenceEquals(t, null)) { tex = t; Log("TEX: found via " + propNames[i] + " on mat[" + mi + "] " + t.width + "x" + t.height); break; }
                    }
                }

                if (ReferenceEquals(tex, null))
                {
                    Log("TEX: no texture found on material");
                    return null;
                }

                var tmp = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.Default, RenderTextureReadWrite.sRGB);
                Graphics.Blit(tex, tmp);
                var prev = RenderTexture.active;
                RenderTexture.active = tmp;
                var readable = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
                readable.Apply();
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(tmp);
                Log("TEX: extracted " + readable.width + "x" + readable.height);
                return readable;
            }
            catch (Exception ex) { Log("TEX: error " + ex.Message); return null; }
        }

        private void DumpObjectFields(object obj, string prefix, int depth)
        {
            if (ReferenceEquals(obj, null) || depth > 4) return;
            try
            {
                var t = obj.GetType();
                Log(prefix + "type=" + t.FullName);
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    try
                    {
                        var val = f.GetValue(obj);
                        if (ReferenceEquals(val, null)) { Log(prefix + f.Name + " = null"); continue; }
                        if (val is string s) { Log(prefix + f.Name + " = \"" + s + "\""); }
                        else if (val is int || val is float || val is bool || val is short || val is byte) { Log(prefix + f.Name + " = " + val); }
                        else if (val is Array arr) { Log(prefix + f.Name + " = Array[" + arr.Length + "]"); }
                        else if (val is UnityEngine.Color col) { Log(prefix + f.Name + " = Color(" + col.r + "," + col.g + "," + col.b + "," + col.a + ")"); }
                        else { Log(prefix + f.Name + " = <" + val.GetType().Name + ">"); if (depth < 3) DumpObjectFields(val, prefix + f.Name + ".", depth + 1); }
                    }
                    catch { Log(prefix + f.Name + " = <error>"); }
                }
            }
            catch { }
        }

        private Texture2D ExtractTextureFromChaControl(ChaControl cc)
        {
            try
            {
                var renderer = FindBodyRenderer(cc);
                if (ReferenceEquals(renderer, null)) { Log("TEX: no body renderer"); return null; }

                if (renderer.sharedMaterials.Length == 0) { Log("TEX: no shared materials"); return null; }
                var mat = renderer.sharedMaterials[0];
                if (ReferenceEquals(mat, null)) { Log("TEX: mat null"); return null; }
                var shader = mat.shader;
                if (ReferenceEquals(shader, null)) { Log("TEX: shader null"); return null; }

                Log("TEX: shader=" + shader.name + " matCount=" + renderer.sharedMaterials.Length);

                for (int mi = 0; mi < renderer.sharedMaterials.Length; mi++)
                {
                    var m = renderer.sharedMaterials[mi];
                    if (ReferenceEquals(m, null)) continue;
                    Log("TEX: mat[" + mi + "] shader=" + m.shader.name);

                    try
                    {
                        var textures = m.GetType().GetField("m_ValidKeywords", BindingFlags.NonPublic | BindingFlags.Instance);
                    }
                    catch { }

                    if (!ReferenceEquals(m.mainTexture, null))
                    {
                        Log("TEX: mat[" + mi + "] mainTexture=" + m.mainTexture.GetType().Name + " " + m.mainTexture.width + "x" + m.mainTexture.height);
                        if (m.mainTexture is Texture2D t2) return t2;
                        if (m.mainTexture is RenderTexture rt)
                        {
                            int w = Math.Min(rt.width, 512);
                            int h = Math.Min(rt.height, 512);
                            var tmp = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.Default, RenderTextureReadWrite.sRGB);
                            Graphics.Blit(rt, tmp);
                            var prev = RenderTexture.active;
                            RenderTexture.active = tmp;
                            var readable = new Texture2D(w, h, TextureFormat.RGBA32, false);
                            readable.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                            readable.Apply();
                            RenderTexture.active = prev;
                            RenderTexture.ReleaseTemporary(tmp);
                            Log("TEX: converted RenderTexture " + w + "x" + h);
                            return readable;
                        }
                    }

                    try
                    {
                        var matType = m.GetType();
                        var propInfo = matType.GetProperty("properties", BindingFlags.NonPublic | BindingFlags.Instance);
                        if (!ReferenceEquals(propInfo, null))
                        {
                            var props = propInfo.GetValue(m, null);
                            Log("TEX: properties type=" + props.GetType().FullName);
                        }
                    }
                    catch { }
                }

                Log("TEX: no texture found");

                return null;
            }
            catch (Exception ex) { Log("TEX: error " + ex.GetType().Name + " " + ex.Message); return null; }
        }

        private Texture2D ExtractTextureDirect(SkinnedMeshRenderer renderer)
        {
            try
            {
                var mat = renderer.sharedMaterials[0];
                if (ReferenceEquals(mat, null)) return null;
                var mainTex = mat.mainTexture;
                if (ReferenceEquals(mainTex, null)) { Log("TEX: mainTexture null"); return null; }
                Log("TEX: mainTexture type=" + mainTex.GetType().Name + " " + mainTex.width + "x" + mainTex.height);

                if (mainTex is Texture2D t2d) return t2d;

                if (mainTex is RenderTexture rt)
                {
                    var tmp = new RenderTexture(512, 512, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                    Graphics.Blit(mainTex, tmp, (Material)null);

                    var prev = RenderTexture.active;
                    RenderTexture.active = tmp;
                    var readable = new Texture2D(512, 512, TextureFormat.RGBA32, false);
                    readable.ReadPixels(new Rect(0, 0, 512, 512), 0, 0);
                    readable.Apply();
                    RenderTexture.active = prev;
                    tmp.Release();
                    UnityEngine.Object.Destroy(tmp);
                    Log("TEX: extracted 512x512");
                    return readable;
                }
            }
            catch (Exception ex) { Log("TEX: error " + ex.GetType().Name + " " + ex.Message); }
            return null;
        }

        private Texture2D MakeReadable(Texture2D tex)
        {
            try
            {
                var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                Graphics.Blit(tex, rt);
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var readable = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
                readable.Apply();
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                return readable;
            }
            catch { return null; }
        }

        private void RemoveSkinColorOverlay(Texture2D tex, Color skinMainColor)
        {
            try
            {
                if (skinMainColor.r < 0.01f || skinMainColor.g < 0.01f || skinMainColor.b < 0.01f) return;
                var pixels = tex.GetPixels32();
                float invR = 1.0f / Math.Max(skinMainColor.r, 0.01f);
                float invG = 1.0f / Math.Max(skinMainColor.g, 0.01f);
                float invB = 1.0f / Math.Max(skinMainColor.b, 0.01f);
                for (int i = 0; i < pixels.Length; i++)
                {
                    float r = pixels[i].r * invR;
                    float g = pixels[i].g * invG;
                    float b = pixels[i].b * invB;
                    pixels[i].r = (byte)Math.Min(255, (int)(r * 255));
                    pixels[i].g = (byte)Math.Min(255, (int)(g * 255));
                    pixels[i].b = (byte)Math.Min(255, (int)(b * 255));
                }
                tex.SetPixels32(pixels);
                tex.Apply();
                Log("TEX: skin color removed r=" + skinMainColor.r + " g=" + skinMainColor.g + " b=" + skinMainColor.b);
            }
            catch (Exception ex) { Log("TEX: removeSkinColor error: " + ex.Message); }
        }

        private Color GetSkinColorFromChaControl(ChaControl cc)
        {
            try
            {
                var chaFileProp = cc.GetType().GetProperty("chaFile", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (ReferenceEquals(chaFileProp, null)) return Color.white;
                var chaFile = chaFileProp.GetValue(cc, null);
                if (ReferenceEquals(chaFile, null)) return Color.white;

                var customField = chaFile.GetType().GetField("custom", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (ReferenceEquals(customField, null)) return Color.white;
                var custom = customField.GetValue(chaFile);
                if (ReferenceEquals(custom, null)) return Color.white;

                var bodyField = custom.GetType().GetField("body", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (ReferenceEquals(bodyField, null)) return Color.white;
                var body = bodyField.GetValue(custom);
                if (ReferenceEquals(body, null)) return Color.white;

                var colorField = body.GetType().GetField("<skinMainColor>k__BackingField", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (!ReferenceEquals(colorField, null))
                {
                    var color = (Color)colorField.GetValue(body);
                    Log("TEX: skinMainColor from ChaFile = " + color);
                    return color;
                }

                foreach (var f in body.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (f.FieldType.Equals(typeof(Color)) && f.Name.ToLower().Contains("skin") && f.Name.ToLower().Contains("color"))
                    {
                        var c = (Color)f.GetValue(body);
                        Log("TEX: found skin color field: " + f.Name + " = " + c);
                        return c;
                    }
                }

                return Color.white;
            }
            catch (Exception ex) { Log("TEX: GetSkinColor error: " + ex.Message); return Color.white; }
        }

        private byte[] ExtractTextureFromChaFile(ChaControl cc, int rendererIndex)
        {
            try
            {
                var ccType = cc.GetType();
                var chaFileProp = ccType.GetProperty("chaFile", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (ReferenceEquals(chaFileProp, null)) { Log("CHAFILE: chaFile prop not found"); return null; }
                var chaFile = chaFileProp.GetValue(cc, null);
                if (ReferenceEquals(chaFile, null)) { Log("CHAFILE: chaFile null"); return null; }

                var customField = chaFile.GetType().GetField("custom", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (ReferenceEquals(customField, null)) { Log("CHAFILE: custom not found"); return null; }
                var custom = customField.GetValue(chaFile);
                if (ReferenceEquals(custom, null)) { Log("CHAFILE: custom null"); return null; }

                var bodyField = custom.GetType().GetField("body", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (ReferenceEquals(bodyField, null)) { Log("CHAFILE: body not found"); return null; }
                var body = bodyField.GetValue(custom);
                if (ReferenceEquals(body, null)) { Log("CHAFILE: body null"); return null; }

                Log("CHAFILE: body type=" + body.GetType().FullName);

                var skinField = body.GetType().GetField("skin", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (ReferenceEquals(skinField, null)) { Log("CHAFILE: skin not found"); return null; }
                var skin = skinField.GetValue(body);
                if (ReferenceEquals(skin, null)) { Log("CHAFILE: skin null"); return null; }

                Log("CHAFILE: skin type=" + skin.GetType().FullName);
                DumpObjectFields(skin, "CHAFILE: skin.", 0);
                DumpObjectFields(body, "CHAFILE: body.", 0);

                var texFileNames = new System.Collections.Generic.List<string>();

                var mainTexField = skin.GetType().GetField("mainTex", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (!ReferenceEquals(mainTexField, null))
                {
                    var val = mainTexField.GetValue(skin) as string;
                    if (!string.IsNullOrEmpty(val)) { Log("CHAFILE: skin.mainTex=" + val); texFileNames.Add(val); }
                }

                var mainTexProp = skin.GetType().GetProperty("mainTex", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (!ReferenceEquals(mainTexProp, null))
                {
                    var val = mainTexProp.GetValue(skin, null) as string;
                    if (!string.IsNullOrEmpty(val) && !texFileNames.Contains(val)) { Log("CHAFILE: skin.mainTex(prop)=" + val); texFileNames.Add(val); }
                }

                var colorInfoField = skin.GetType().GetField("colorInfo", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (!ReferenceEquals(colorInfoField, null))
                {
                    var arr = colorInfoField.GetValue(skin) as Array;
                    if (!ReferenceEquals(arr, null) && arr.Length > 0)
                    {
                        Log("CHAFILE: colorInfo count=" + arr.Length);
                        for (int ci = 0; ci < Math.Min(arr.Length, 4); ci++)
                        {
                            var ciObj = arr.GetValue(ci);
                            if (ReferenceEquals(ciObj, null)) continue;

                            string[] propNames = { "mainTex", "subTex", "texMain", "texSub", "diffuseTex" };
                            foreach (var pn in propNames)
                            {
                                var f = ciObj.GetType().GetField(pn, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                if (!ReferenceEquals(f, null))
                                {
                                    var v = f.GetValue(ciObj) as string;
                                    if (!string.IsNullOrEmpty(v) && !texFileNames.Contains(v)) { Log("CHAFILE: colorInfo[" + ci + "]." + pn + "=" + v); texFileNames.Add(v); }
                                }
                                var p = ciObj.GetType().GetProperty(pn, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                if (!ReferenceEquals(p, null))
                                {
                                    var v = p.GetValue(ciObj, null) as string;
                                    if (!string.IsNullOrEmpty(v) && !texFileNames.Contains(v)) { Log("CHAFILE: colorInfo[" + ci + "]." + pn + "(p)=" + v); texFileNames.Add(v); }
                                }
                            }
                        }
                    }
                }

                string[] skinFieldNames = { "mainTexName", "texName", "textureName", "baseTex" };
                foreach (var fn in skinFieldNames)
                {
                    var f = skin.GetType().GetField(fn, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (!ReferenceEquals(f, null))
                    {
                        var v = f.GetValue(skin) as string;
                        if (!string.IsNullOrEmpty(v) && !texFileNames.Contains(v)) { Log("CHAFILE: skin." + fn + "=" + v); texFileNames.Add(v); }
                    }
                }

                var skinIdField = skin.GetType().GetField("id", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                int skinId = -1;
                if (!ReferenceEquals(skinIdField, null)) skinId = (int)skinIdField.GetValue(skin);

                var bodyIdField = body.GetType().GetField("id", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                int bodyId = -1;
                if (!ReferenceEquals(bodyIdField, null)) bodyId = (int)bodyIdField.GetValue(body);

                Log("CHAFILE: skinId=" + skinId + " bodyId=" + bodyId);

                string texDir = Path.Combine(Path.Combine(Path.Combine(Application.dataPath, ".."), "UserData"), "texture");
                string bodyTexDir = Path.Combine(texDir, "body");

                var searchPaths = new System.Collections.Generic.List<string>();
                foreach (var fn in texFileNames)
                {
                    searchPaths.Add(Path.Combine(bodyTexDir, fn));
                    searchPaths.Add(Path.Combine(texDir, fn));
                    searchPaths.Add(fn);
                }

                if (skinId >= 0)
                {
                    searchPaths.Add(Path.Combine(bodyTexDir, skinId + ".png"));
                    searchPaths.Add(Path.Combine(bodyTexDir, skinId + "_00.png"));
                    searchPaths.Add(Path.Combine(bodyTexDir, bodyId + "_" + skinId + "_00.png"));
                    searchPaths.Add(Path.Combine(bodyTexDir, "body_" + skinId.ToString("D2") + ".png"));
                }

                if (Directory.Exists(bodyTexDir))
                {
                    var allFiles = Directory.GetFiles(bodyTexDir, "*.png");
                    Log("CHAFILE: body tex dir has " + allFiles.Length + " files");
                    foreach (var f in allFiles)
                    {
                        Log("CHAFILE:   file: " + Path.GetFileName(f));
                    }
                }

                foreach (var sp in searchPaths)
                {
                    if (File.Exists(sp))
                    {
                        var bytes = File.ReadAllBytes(sp);
                        Log("CHAFILE: loaded " + sp + " (" + bytes.Length + " bytes)");
                        return bytes;
                    }
                }

                if (Directory.Exists(bodyTexDir) && texFileNames.Count == 0)
                {
                    var pngs = Directory.GetFiles(bodyTexDir, "*.png");
                    if (pngs.Length > 0 && rendererIndex < pngs.Length)
                    {
                        var bytes = File.ReadAllBytes(pngs[rendererIndex]);
                        Log("CHAFILE: fallback loaded " + pngs[rendererIndex] + " (" + bytes.Length + " bytes)");
                        return bytes;
                    }
                    else if (pngs.Length > 0)
                    {
                        var bytes = File.ReadAllBytes(pngs[0]);
                        Log("CHAFILE: fallback loaded " + pngs[0] + " (" + bytes.Length + " bytes)");
                        return bytes;
                    }
                }

                Log("CHAFILE: no texture file found");
                return null;
            }
            catch (Exception ex) { Log("CHAFILE: error " + ex.GetType().Name + " " + ex.Message); return null; }
        }

        private object GetStudioInstance()
        {
            var t = FindType("Studio.Studio");
            if (ReferenceEquals(t, null)) { Log("Studio.Studio type not found"); return null; }
            var prop = t.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            if (ReferenceEquals(prop, null))
            {
                var baseT = t.BaseType;
                while (!ReferenceEquals(baseT, null) && !ReferenceEquals(baseT, typeof(object)))
                {
                    prop = baseT.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                    if (!ReferenceEquals(prop, null)) break;
                    baseT = baseT.BaseType;
                }
            }
            if (ReferenceEquals(prop, null)) { Log("Instance not found"); return null; }
            return prop.GetValue(null, null);
        }

        private Type FindType(string fullName)
        {
            var t = Type.GetType(fullName);
            if (!ReferenceEquals(t, null)) return t;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) { t = asm.GetType(fullName); if (!ReferenceEquals(t, null)) return t; }
            return null;
        }

        private ChaControl GetSelectedChaControl()
        {
            var studio = GetStudioInstance();
            if (ReferenceEquals(studio, null)) return null;
            var st = studio.GetType();
            var ctrlProp = st.GetProperty("treeNodeCtrl", BindingFlags.Public | BindingFlags.Instance);
            if (ReferenceEquals(ctrlProp, null)) return null;
            var ctrl = ctrlProp.GetValue(studio, null);
            if (ReferenceEquals(ctrl, null)) return null;
            var nodeProp = ctrl.GetType().GetProperty("selectNode", BindingFlags.Public | BindingFlags.Instance);
            if (ReferenceEquals(nodeProp, null)) return null;
            var node = nodeProp.GetValue(ctrl, null);
            if (ReferenceEquals(node, null)) return null;
            var dicProp = st.GetProperty("dicInfo", BindingFlags.Public | BindingFlags.Instance);
            if (ReferenceEquals(dicProp, null)) return null;
            var dic = dicProp.GetValue(studio, null) as System.Collections.IDictionary;
            if (ReferenceEquals(dic, null) || !dic.Contains(node)) return null;
            var info = dic[node];
            if (ReferenceEquals(info, null)) return null;
            var goProp = info.GetType().GetProperty("gpuObject", BindingFlags.Public | BindingFlags.Instance);
            if (ReferenceEquals(goProp, null)) return null;
            var go = goProp.GetValue(info, null) as GameObject;
            if (ReferenceEquals(go, null)) return null;
            return go.GetComponent<ChaControl>();
        }

        private void CallInt(Type t, object obj, string method, int val)
        {
            var m = t.GetMethod(method, BindingFlags.Public | BindingFlags.Instance);
            if (!ReferenceEquals(m, null)) m.Invoke(obj, new object[] { val });
        }

        private string GetParam(string q, string name)
        {
            if (string.IsNullOrEmpty(q)) return null;
            foreach (var p in q.TrimStart('?').Split('&')) { var kv = p.Split('='); if (kv.Length == 2 && kv[0] == name) return Uri.UnescapeDataString(kv[1]); }
            return null;
        }

        private float Fl(string q, string name, float def)
        {
            var v = GetParam(q, name);
            float r;
            if (float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out r)) return r;
            return def;
        }

        private static string Escape(string s) { if (s == null) return ""; return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", ""); }
        private static void Log(string msg) { try { if (!ReferenceEquals(_logPath, null)) File.AppendAllText(_logPath, "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg + "\n"); } catch { } }
        private void OnDestroy() { isRunning = false; try { tcpListener.Stop(); } catch { } Log("Server stopped"); }
    }

    public class MeshEntry
    {
        public Mesh mesh;
        public byte[] pngData;
        public byte[] normalPng;
        public byte[] specularPng;
        public byte[] alphaPng;
        public string name;
        public string shaderName;
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 scale;
        public Color color1, color2, color3;
        public Color shadowColor;
        public Color lineColor;
        public bool isHair;
    }

    public static class GlbBuilder
    {
        public static byte[] Build(Mesh mesh, byte[] pngData, string name)
        {
            var vertices = mesh.vertices;
            var normals = mesh.normals;
            var uv = mesh.uv;
            var triangles = mesh.triangles;

            int vCount = vertices.Length;
            bool useUint32 = vCount > 65535;
            int indexCompType = useUint32 ? 5125 : 5123;

            int posBytes = vCount * 12;
            int normBytes = vCount * 12;
            int uvBytes = (uv != null ? uv.Length : 0) * 8;
            int idxBytes = triangles.Length * (useUint32 ? 4 : 2);

            byte[] texPng = pngData;
            int texBytes = texPng != null ? texPng.Length : 0;
            int texBV = texBytes > 0 ? 4 : -1;

            int binTotal = posBytes + normBytes + uvBytes + idxBytes + texBytes;

            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
            for (int i = 0; i < vCount; i++)
            {
                float x = vertices[i].x, y = vertices[i].y, z = -vertices[i].z;
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
                if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
            }

            var sb = new StringBuilder();
            sb.Append("{\"asset\":{\"version\":\"2.0\",\"generator\":\"StudioHTTPAPI\"}");
            sb.Append(",\"scene\":0,\"scenes\":[{\"nodes\":[0]}]");
            sb.Append(",\"nodes\":[{\"mesh\":0,\"name\":\"");
            sb.Append(EscapeJson(name));
            sb.Append("\"}]");
            sb.Append(",\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0,\"NORMAL\":1,\"TEXCOORD_0\":2},\"indices\":3,\"material\":0}]}]");
            sb.Append(",\"accessors\":[");
            sb.Append("{\"bufferView\":0,\"componentType\":5126,\"count\":");
            sb.Append(vCount);
            sb.Append(",\"type\":\"VEC3\",\"min\":[");
            sb.Append(F(minX)); sb.Append(","); sb.Append(F(minY)); sb.Append(","); sb.Append(F(minZ));
            sb.Append("],\"max\":[");
            sb.Append(F(maxX)); sb.Append(","); sb.Append(F(maxY)); sb.Append(","); sb.Append(F(maxZ));
            sb.Append("]}");
            sb.Append(",{\"bufferView\":1,\"componentType\":5126,\"count\":");
            sb.Append(vCount);
            sb.Append(",\"type\":\"VEC3\"}");
            sb.Append(",{\"bufferView\":2,\"componentType\":5126,\"count\":");
            sb.Append(uv != null ? uv.Length : 0);
            sb.Append(",\"type\":\"VEC2\"}");
            sb.Append(",{\"bufferView\":3,\"componentType\":");
            sb.Append(indexCompType);
            sb.Append(",\"count\":");
            sb.Append(triangles.Length);
            sb.Append(",\"type\":\"SCALAR\"}");
            sb.Append("]");
            sb.Append(",\"bufferViews\":[");
            sb.Append("{\"buffer\":0,\"byteOffset\":0,\"byteLength\":");
            sb.Append(posBytes); sb.Append(",\"target\":34962}");
            sb.Append(",{\"buffer\":0,\"byteOffset\":"); sb.Append(posBytes);
            sb.Append(",\"byteLength\":"); sb.Append(normBytes); sb.Append(",\"target\":34962}");
            sb.Append(",{\"buffer\":0,\"byteOffset\":"); sb.Append(posBytes + normBytes);
            sb.Append(",\"byteLength\":"); sb.Append(uvBytes); sb.Append(",\"target\":34962}");
            sb.Append(",{\"buffer\":0,\"byteOffset\":"); sb.Append(posBytes + normBytes + uvBytes);
            sb.Append(",\"byteLength\":"); sb.Append(idxBytes); sb.Append(",\"target\":34963}");
            if (texBV >= 0)
            {
                sb.Append(",{\"buffer\":0,\"byteOffset\":"); sb.Append(posBytes + normBytes + uvBytes + idxBytes);
                sb.Append(",\"byteLength\":"); sb.Append(texBytes); sb.Append("}");
            }
            sb.Append("]");
            sb.Append(",\"buffers\":[{\"byteLength\":"); sb.Append(binTotal); sb.Append("}]");
            sb.Append(",\"materials\":[{\"pbrMetallicRoughness\":{\"metallicFactor\":0.0,\"roughnessFactor\":1.0");
            if (texBV >= 0) sb.Append(",\"baseColorTexture\":{\"index\":0}");
            sb.Append("}}]");
            if (texBV >= 0)
            {
                sb.Append(",\"textures\":[{\"source\":0}]");
                sb.Append(",\"images\":[{\"mimeType\":\"image/png\",\"bufferView\":"); sb.Append(texBV); sb.Append("}]");
                sb.Append(",\"samplers\":[{\"magFilter\":9729,\"minFilter\":9987}]");
            }
            sb.Append("}");

            string json = sb.ToString();
            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
            int jsonPadded = Align4(jsonBytes.Length);
            int binPadded = Align4(binTotal);

            int totalLen = 12 + 8 + jsonPadded + 8 + binPadded;
            var result = new byte[totalLen];
            int pos = 0;

            WriteUint(result, ref pos, 0x46546C67);
            WriteUint(result, ref pos, 2);
            WriteUint(result, ref pos, (uint)totalLen);

            WriteUint(result, ref pos, (uint)jsonPadded);
            WriteUint(result, ref pos, 0x4E4F534A);
            Array.Copy(jsonBytes, 0, result, pos, jsonBytes.Length);
            for (int i = jsonBytes.Length; i < jsonPadded; i++) result[pos + i] = 0x20;
            pos += jsonPadded;

            WriteUint(result, ref pos, (uint)binPadded);
            WriteUint(result, ref pos, 0x004E4942);

            for (int i = 0; i < vCount; i++)
            {
                WriteFloat(result, ref pos, vertices[i].x);
                WriteFloat(result, ref pos, vertices[i].y);
                WriteFloat(result, ref pos, -vertices[i].z);
            }
            for (int i = 0; i < vCount; i++)
            {
                WriteFloat(result, ref pos, normals[i].x);
                WriteFloat(result, ref pos, normals[i].y);
                WriteFloat(result, ref pos, -normals[i].z);
            }
            if (uv != null)
            {
                for (int i = 0; i < uv.Length; i++)
                {
                    WriteFloat(result, ref pos, uv[i].x);
                    WriteFloat(result, ref pos, uv[i].y);
                }
            }
            for (int i = 0; i < triangles.Length; i += 3)
            {
                if (useUint32)
                {
                    WriteUint(result, ref pos, (uint)triangles[i]);
                    WriteUint(result, ref pos, (uint)triangles[i + 2]);
                    WriteUint(result, ref pos, (uint)triangles[i + 1]);
                }
                else
                {
                    WriteUshort(result, ref pos, (ushort)triangles[i]);
                    WriteUshort(result, ref pos, (ushort)triangles[i + 2]);
                    WriteUshort(result, ref pos, (ushort)triangles[i + 1]);
                }
            }
            if (texPng != null)
            {
                Array.Copy(texPng, 0, result, pos, texPng.Length);
            }

            return result;
        }

        public static byte[] BuildMulti(System.Collections.Generic.List<MeshEntry> entries)
        {
            int N = entries.Count;
            bool[] u32 = new bool[N];
            for (int i = 0; i < N; i++) u32[i] = entries[i].mesh.vertices.Length > 65535;

            var binMs = new MemoryStream();
            int[] actualPosOff = new int[N], actualNormOff = new int[N], actualUvOff = new int[N], actualIdxOff = new int[N];
            int[] actualPosLen = new int[N], actualNormLen = new int[N], actualUvLen = new int[N], actualIdxLen = new int[N];

            for (int i = 0; i < N; i++)
            {
                var mesh = entries[i].mesh;
                var verts = mesh.vertices;
                var norms = mesh.normals;
                var uv = mesh.uv;
                var tri = mesh.triangles;

                actualPosOff[i] = (int)binMs.Position;
                actualPosLen[i] = verts.Length * 12;
                var buf = new byte[12];
                for (int j = 0; j < verts.Length; j++)
                {
                    BitConverter.GetBytes(verts[j].x).CopyTo(buf, 0);
                    BitConverter.GetBytes(verts[j].y).CopyTo(buf, 4);
                    BitConverter.GetBytes(-verts[j].z).CopyTo(buf, 8);
                    binMs.Write(buf, 0, 12);
                }

                actualNormOff[i] = (int)binMs.Position;
                actualNormLen[i] = (norms != null ? norms.Length : 0) * 12;
                if (norms != null)
                {
                    for (int j = 0; j < norms.Length; j++)
                    {
                        BitConverter.GetBytes(norms[j].x).CopyTo(buf, 0);
                        BitConverter.GetBytes(norms[j].y).CopyTo(buf, 4);
                        BitConverter.GetBytes(-norms[j].z).CopyTo(buf, 8);
                        binMs.Write(buf, 0, 12);
                    }
                }

                actualUvOff[i] = (int)binMs.Position;
                actualUvLen[i] = (uv != null ? uv.Length : 0) * 8;
                if (uv != null)
                {
                    var uvBuf = new byte[8];
                    for (int j = 0; j < uv.Length; j++)
                    {
                        BitConverter.GetBytes(uv[j].x).CopyTo(uvBuf, 0);
                        BitConverter.GetBytes(uv[j].y).CopyTo(uvBuf, 4);
                        binMs.Write(uvBuf, 0, 8);
                    }
                }

                actualIdxOff[i] = (int)binMs.Position;
                actualIdxLen[i] = tri.Length * (u32[i] ? 4 : 2);
                if (u32[i])
                {
                    var iBuf = new byte[4];
                    for (int j = 0; j < tri.Length; j += 3)
                    {
                        BitConverter.GetBytes((uint)tri[j]).CopyTo(iBuf, 0); binMs.Write(iBuf, 0, 4);
                        BitConverter.GetBytes((uint)tri[j + 2]).CopyTo(iBuf, 0); binMs.Write(iBuf, 0, 4);
                        BitConverter.GetBytes((uint)tri[j + 1]).CopyTo(iBuf, 0); binMs.Write(iBuf, 0, 4);
                    }
                }
                else
                {
                    var sBuf = new byte[2];
                    for (int j = 0; j < tri.Length; j += 3)
                    {
                        BitConverter.GetBytes((ushort)tri[j]).CopyTo(sBuf, 0); binMs.Write(sBuf, 0, 2);
                        BitConverter.GetBytes((ushort)tri[j + 2]).CopyTo(sBuf, 0); binMs.Write(sBuf, 0, 2);
                        BitConverter.GetBytes((ushort)tri[j + 1]).CopyTo(sBuf, 0); binMs.Write(sBuf, 0, 2);
                    }
                }
            }

            int[] texOff = new int[N], texLen = new int[N], texGlbIdx = new int[N];
            int[] normTexOff = new int[N], normTexLen = new int[N], normTexGlbIdx = new int[N];
            int[] specTexOff = new int[N], specTexLen = new int[N], specTexGlbIdx = new int[N];
            int[] alphaTexOff = new int[N], alphaTexLen = new int[N], alphaTexGlbIdx = new int[N];
            int texCount = 0;

            void WriteTex(byte[] data, int[] off, int[] len, int[] glbIdx, int i)
            {
                if (data != null && data.Length > 0)
                {
                    off[i] = (int)binMs.Position;
                    len[i] = data.Length;
                    binMs.Write(data, 0, data.Length);
                    glbIdx[i] = texCount++;
                }
                else { glbIdx[i] = -1; len[i] = 0; }
            }

            for (int i = 0; i < N; i++)
            {
                WriteTex(entries[i].pngData, texOff, texLen, texGlbIdx, i);
                WriteTex(entries[i].normalPng, normTexOff, normTexLen, normTexGlbIdx, i);
                WriteTex(entries[i].specularPng, specTexOff, specTexLen, specTexGlbIdx, i);
                WriteTex(entries[i].alphaPng, alphaTexOff, alphaTexLen, alphaTexGlbIdx, i);
            }

            int binTotal = (int)binMs.Position;
            byte[] binData = binMs.ToArray();
            binMs.Dispose();

            var sb = new StringBuilder();
            sb.Append("{\"asset\":{\"version\":\"2.0\",\"generator\":\"StudioHTTPAPI\"}");
            sb.Append(",\"scene\":0,\"scenes\":[{\"nodes\":[");
            for (int i = 0; i < N; i++) { if (i > 0) sb.Append(","); sb.Append(i); }
            sb.Append("]}]");
            sb.Append(",\"nodes\":[");
            for (int i = 0; i < N; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append("{\"mesh\":" + i + ",\"name\":\"" + EscapeJson(entries[i].name) + "\"");
                var tPos = entries[i].position;
                var tRot = entries[i].rotation;
                var tScl = entries[i].scale;
                if (tPos != Vector3.zero || tRot != Quaternion.identity || tScl != Vector3.one)
                {
                    sb.Append(",\"translation\":[" + F(tPos.x) + "," + F(tPos.y) + "," + F(-tPos.z) + "]");
                    sb.Append(",\"rotation\":[" + F(tRot.x) + "," + F(tRot.y) + "," + F(-tRot.z) + "," + F(-tRot.w) + "]");
                    sb.Append(",\"scale\":[" + F(tScl.x) + "," + F(tScl.y) + "," + F(tScl.z) + "]");
                }
                sb.Append("}");
            }
            sb.Append("]");
            sb.Append(",\"meshes\":[");
            for (int i = 0; i < N; i++)
            {
                if (i > 0) sb.Append(",");
                int baseAcc = i * 4;
                sb.Append("{\"primitives\":[{\"attributes\":{\"POSITION\":" + baseAcc + ",\"NORMAL\":" + (baseAcc + 1) + ",\"TEXCOORD_0\":" + (baseAcc + 2) + "},\"indices\":" + (baseAcc + 3) + ",\"material\":" + i + "}]}");
            }
            sb.Append("]");
            sb.Append(",\"accessors\":[");
            for (int i = 0; i < N; i++)
            {
                if (i > 0) sb.Append(",");
                int bi = i * 4;
                var mesh = entries[i].mesh;
                var verts = mesh.vertices;
                float px = float.MaxValue, py = float.MaxValue, pz = float.MaxValue;
                float qx = float.MinValue, qy = float.MinValue, qz = float.MinValue;
                for (int j = 0; j < verts.Length; j++)
                {
                    float x = verts[j].x, y = verts[j].y, z = -verts[j].z;
                    if (x < px) px = x; if (x > qx) qx = x;
                    if (y < py) py = y; if (y > qy) qy = y;
                    if (z < pz) pz = z; if (z > qz) qz = z;
                }
                sb.Append("{\"bufferView\":" + bi + ",\"componentType\":5126,\"count\":" + verts.Length + ",\"type\":\"VEC3\",\"min\":[" + F(px) + "," + F(py) + "," + F(pz) + "],\"max\":[" + F(qx) + "," + F(qy) + "," + F(qz) + "]}");
                sb.Append(",{\"bufferView\":" + (bi + 1) + ",\"componentType\":5126,\"count\":" + mesh.normals.Length + ",\"type\":\"VEC3\"}");
                var uv = mesh.uv;
                sb.Append(",{\"bufferView\":").Append(bi + 2).Append(",\"componentType\":5126,\"count\":").Append(uv != null ? uv.Length : 0).Append(",\"type\":\"VEC2\"}");
                sb.Append(",{\"bufferView\":" + (bi + 3) + ",\"componentType\":" + (u32[i] ? 5125 : 5123) + ",\"count\":" + mesh.triangles.Length + ",\"type\":\"SCALAR\"}");
            }
            sb.Append("]");
            sb.Append(",\"bufferViews\":[");
            bool first = true;
            for (int i = 0; i < N; i++)
            {
                int bi = i * 4;
                if (!first) sb.Append(","); first = false;
                sb.Append("{\"buffer\":0,\"byteOffset\":" + actualPosOff[i] + ",\"byteLength\":" + actualPosLen[i] + ",\"target\":34962}");
                sb.Append(",{\"buffer\":0,\"byteOffset\":" + actualNormOff[i] + ",\"byteLength\":" + actualNormLen[i] + ",\"target\":34962}");
                sb.Append(",{\"buffer\":0,\"byteOffset\":" + actualUvOff[i] + ",\"byteLength\":" + actualUvLen[i] + ",\"target\":34962}");
                sb.Append(",{\"buffer\":0,\"byteOffset\":" + actualIdxOff[i] + ",\"byteLength\":" + actualIdxLen[i] + ",\"target\":34963}");
            }
            for (int i = 0; i < N; i++)
            {
                if (texGlbIdx[i] >= 0) sb.Append(",{\"buffer\":0,\"byteOffset\":" + texOff[i] + ",\"byteLength\":" + texLen[i] + "}");
                if (normTexGlbIdx[i] >= 0) sb.Append(",{\"buffer\":0,\"byteOffset\":" + normTexOff[i] + ",\"byteLength\":" + normTexLen[i] + "}");
                if (specTexGlbIdx[i] >= 0) sb.Append(",{\"buffer\":0,\"byteOffset\":" + specTexOff[i] + ",\"byteLength\":" + specTexLen[i] + "}");
                if (alphaTexGlbIdx[i] >= 0) sb.Append(",{\"buffer\":0,\"byteOffset\":" + alphaTexOff[i] + ",\"byteLength\":" + alphaTexLen[i] + "}");
            }
            sb.Append("]");
            sb.Append(",\"buffers\":[{\"byteLength\":" + binTotal + "}]");
            sb.Append(",\"materials\":[");
            for (int i = 0; i < N; i++)
            {
                if (i > 0) sb.Append(",");
                float roughness = entries[i].isHair ? 0.65f : 1.0f;
                sb.Append("{\"pbrMetallicRoughness\":{\"metallicFactor\":0.0,\"roughnessFactor\":" + F(roughness));
                if (texGlbIdx[i] >= 0) sb.Append(",\"baseColorTexture\":{\"index\":" + texGlbIdx[i] + "}");
                if (!entries[i].isHair && specTexGlbIdx[i] >= 0) sb.Append(",\"metallicRoughnessTexture\":{\"index\":" + specTexGlbIdx[i] + "}");
                sb.Append("}");
                if (normTexGlbIdx[i] >= 0) sb.Append(",\"normalTexture\":{\"index\":" + normTexGlbIdx[i] + "}");
                if (alphaTexGlbIdx[i] >= 0) sb.Append(",\"alphaMode\":\"MASK\",\"alphaCutoff\":0.5");
                if (entries[i].isHair)
                {
                    sb.Append(",\"extras\":{\"shader\":\"" + EscapeJson(entries[i].shaderName) + "\"");
                    sb.Append(",\"Color\":[" + F(entries[i].color1.r) + "," + F(entries[i].color1.g) + "," + F(entries[i].color1.b) + "," + F(entries[i].color1.a) + "]");
                    sb.Append(",\"Color2\":[" + F(entries[i].color2.r) + "," + F(entries[i].color2.g) + "," + F(entries[i].color2.b) + "," + F(entries[i].color2.a) + "]");
                    sb.Append(",\"Color3\":[" + F(entries[i].color3.r) + "," + F(entries[i].color3.g) + "," + F(entries[i].color3.b) + "," + F(entries[i].color3.a) + "]");
                    sb.Append(",\"ShadowColor\":[" + F(entries[i].shadowColor.r) + "," + F(entries[i].shadowColor.g) + "," + F(entries[i].shadowColor.b) + "," + F(entries[i].shadowColor.a) + "]");
                    sb.Append(",\"LineColor\":[" + F(entries[i].lineColor.r) + "," + F(entries[i].lineColor.g) + "," + F(entries[i].lineColor.b) + "," + F(entries[i].lineColor.a) + "]");
                    sb.Append("}");
                }
                sb.Append("}");
            }
            sb.Append("]");
            if (texCount > 0)
            {
                sb.Append(",\"textures\":[");
                for (int j = 0; j < texCount; j++) { if (j > 0) sb.Append(","); sb.Append("{\"sampler\":0,\"source\":" + j + "}"); }
                sb.Append("],\"images\":[");
                int imgIdx = 0;
                for (int i = 0; i < N; i++)
                {
                    int[] glbIdxArr = { texGlbIdx[i], normTexGlbIdx[i], specTexGlbIdx[i], alphaTexGlbIdx[i] };
                    int[] offArr = { texOff[i], normTexOff[i], specTexOff[i], alphaTexOff[i] };
                    int[] lenArr = { texLen[i], normTexLen[i], specTexLen[i], alphaTexLen[i] };
                    for (int t = 0; t < 4; t++)
                    {
                        if (glbIdxArr[t] < 0) continue;
                        if (imgIdx > 0) sb.Append(",");
                        int bvIdx = 4 * N + imgIdx;
                        sb.Append("{\"mimeType\":\"image/png\",\"bufferView\":" + bvIdx + "}");
                        imgIdx++;
                    }
                }
                sb.Append("],\"samplers\":[{\"magFilter\":9729,\"minFilter\":9987}]");
            }
            sb.Append("}");

            byte[] jsonBytes = Encoding.UTF8.GetBytes(sb.ToString());
            int jsonPadded = Align4(jsonBytes.Length);
            int binPadded = Align4(binTotal);
            int totalLen = 12 + 8 + jsonPadded + 8 + binPadded;
            var result = new byte[totalLen];
            int pos = 0;

            WriteUint(result, ref pos, 0x46546C67);
            WriteUint(result, ref pos, 2);
            WriteUint(result, ref pos, (uint)totalLen);
            WriteUint(result, ref pos, (uint)jsonPadded);
            WriteUint(result, ref pos, 0x4E4F534A);
            Array.Copy(jsonBytes, 0, result, pos, jsonBytes.Length);
            for (int i = jsonBytes.Length; i < jsonPadded; i++) result[pos + i] = 0x20;
            pos += jsonPadded;
            WriteUint(result, ref pos, (uint)binPadded);
            WriteUint(result, ref pos, 0x004E4942);
            Array.Copy(binData, 0, result, pos, binTotal);

            return result;
        }

        static int Align4(int v) { return (v + 3) & ~3; }

        static void WriteUint(byte[] buf, ref int pos, uint v)
        {
            buf[pos] = (byte)(v & 0xFF);
            buf[pos + 1] = (byte)((v >> 8) & 0xFF);
            buf[pos + 2] = (byte)((v >> 16) & 0xFF);
            buf[pos + 3] = (byte)((v >> 24) & 0xFF);
            pos += 4;
        }

        static void WriteFloat(byte[] buf, ref int pos, float v)
        {
            byte[] b = BitConverter.GetBytes(v);
            buf[pos] = b[0]; buf[pos + 1] = b[1]; buf[pos + 2] = b[2]; buf[pos + 3] = b[3];
            pos += 4;
        }

        static void WriteUshort(byte[] buf, ref int pos, ushort v)
        {
            buf[pos] = (byte)(v & 0xFF);
            buf[pos + 1] = (byte)((v >> 8) & 0xFF);
            pos += 2;
        }

        public static byte[] EncodePngRaw(Color32[] pixels, int w, int h)
        {
            var ms = new MemoryStream();
            byte[] sig = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            ms.Write(sig, 0, 8);

            WriteChunk(ms, "IHDR", BuildIHDR(w, h));

            var raw = new byte[h * (1 + w * 4)];
            for (int y = 0; y < h; y++)
            {
                int rowOff = y * (1 + w * 4);
                raw[rowOff] = 0;
                for (int x = 0; x < w; x++)
                {
                    var c = pixels[y * w + x];
                    int p = rowOff + 1 + x * 4;
                    raw[p] = c.r;
                    raw[p + 1] = c.g;
                    raw[p + 2] = c.b;
                    raw[p + 3] = c.a;
                }
            }

            byte[] compressed;
            using (var cs = new MemoryStream())
            {
                using (var ds = new System.IO.Compression.DeflateStream(cs, System.IO.Compression.CompressionMode.Compress))
                {
                    ds.Write(raw, 0, raw.Length);
                }
                compressed = cs.ToArray();
            }

            byte[] zlibHeader = { 0x78, 0x01 };
            var idatData = new byte[2 + compressed.Length];
            Array.Copy(zlibHeader, idatData, 2);
            Array.Copy(compressed, 0, idatData, 2, compressed.Length);
            WriteChunk(ms, "IDAT", idatData);

            WriteChunk(ms, "IEND", new byte[0]);

            return ms.ToArray();
        }

        static byte[] BuildIHDR(int w, int h)
        {
            var data = new byte[13];
            data[0] = (byte)(w >> 24); data[1] = (byte)(w >> 16); data[2] = (byte)(w >> 8); data[3] = (byte)w;
            data[4] = (byte)(h >> 24); data[5] = (byte)(h >> 16); data[6] = (byte)(h >> 8); data[7] = (byte)h;
            data[8] = 8; data[9] = 6; data[10] = 0; data[11] = 0; data[12] = 0;
            return data;
        }

        static void WriteChunk(Stream s, string type, byte[] data)
        {
            byte[] len = { (byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length };
            s.Write(len, 0, 4);
            byte[] typeBytes = Encoding.ASCII.GetBytes(type);
            s.Write(typeBytes, 0, 4);
            s.Write(data, 0, data.Length);
            var crc = Crc32(typeBytes, data);
            byte[] crcBytes = { (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc };
            s.Write(crcBytes, 0, 4);
        }

        static uint Crc32(byte[] type, byte[] data)
        {
            uint crc = 0xFFFFFFFF;
            foreach (byte b in type) crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            foreach (byte b in data) crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFF;
        }

        static readonly uint[] Crc32Table = MakeCrc32Table();
        static uint[] MakeCrc32Table()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }

        static string F(float v) { return v.ToString("G", System.Globalization.CultureInfo.InvariantCulture); }

        static string EscapeJson(string s) { if (s == null) return ""; return s.Replace("\\", "\\\\").Replace("\"", "\\\""); }
    }

    public static class UnityThreadHelper
    {
        private static readonly System.Collections.Generic.Queue<ThreadStart> queue = new System.Collections.Generic.Queue<ThreadStart>();
        private static readonly object lockObj = new object();
        private static GameObject go;
        private static MonoBehaviour disp;

        public static void Enqueue(ThreadStart a)
        {
            Monitor.Enter(lockObj);
            try { queue.Enqueue(a); }
            finally { Monitor.Exit(lockObj); }
            if (ReferenceEquals(disp, null))
            {
                go = new GameObject("StudioHTTPAPI_Disp");
                disp = go.AddComponent<Dispatcher>();
                UnityEngine.Object.DontDestroyOnLoad(go);
            }
        }

        public class Dispatcher : MonoBehaviour
        {
            private void Update()
            {
                while (true)
                {
                    ThreadStart act = null;
                    Monitor.Enter(lockObj);
                    try { if (queue.Count > 0) act = queue.Dequeue(); }
                    finally { Monitor.Exit(lockObj); }
                    if (ReferenceEquals(act, null)) break;
                    try { act(); } catch { }
                }
            }
        }
    }
}
