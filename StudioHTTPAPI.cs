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
            var dir = UserDataPath("export");
            Directory.CreateDirectory(dir);
            var fullPath = Path.Combine(dir, filename);
            var capturedPath = fullPath;

            var resultLock = new object();
            string resultJson = null;
            byte[] resultGlb = null;

            var done = new ManualResetEvent(false);

            UnityThreadHelper.Enqueue(new ThreadStart(() =>
            {
                try
                {
                    var cc = GetChaControlByIndex(index);
                    if (ReferenceEquals(cc, null)) { resultJson = "{\"error\":\"no character at index " + index + "\"}"; done.Set(); return; }

                    SkinnedMeshRenderer bodyRenderer = FindBodyRenderer(cc);
                    if (ReferenceEquals(bodyRenderer, null)) { resultJson = "{\"error\":\"no body mesh found\"}"; done.Set(); return; }

                    Mesh bakedMesh = new Mesh();
                    bodyRenderer.BakeMesh(bakedMesh);

                    Texture2D bodyTexture = null;
                byte[] texPngBytes = null;
                    try
                    {
                        var mats = bodyRenderer.sharedMaterials;
                        Log("TEX: matCount=" + mats.Length);
                        for (int mi = 0; mi < mats.Length && ReferenceEquals(bodyTexture, null); mi++)
                        {
                            var mat = mats[mi];
                            if (ReferenceEquals(mat, null)) { Log("TEX: mat[" + mi + "] null"); continue; }
                            Log("TEX: mat[" + mi + "] shader=" + mat.shader.name);
                            var mainTex = mat.mainTexture;
                            if (ReferenceEquals(mainTex, null)) { Log("TEX: mat[" + mi + "] mainTexture null"); continue; }
                            Log("TEX: mat[" + mi + "] mainTex type=" + mainTex.GetType().Name + " " + mainTex.width + "x" + mainTex.height);

                            if (mainTex is RenderTexture rt)
                            {
                                int w = Math.Min(rt.width, 512);
                                int h = Math.Min(rt.height, 512);
                                var dst = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
                                Graphics.Blit(mainTex, dst);
                                var prev = RenderTexture.active;
                                RenderTexture.active = dst;
                                bodyTexture = new Texture2D(w, h, TextureFormat.RGBA32, false);
                                bodyTexture.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                                bodyTexture.Apply();
                                RenderTexture.active = prev;
                                RenderTexture.ReleaseTemporary(dst);
                                var px = bodyTexture.GetPixels32();
                                Log("TEX: px[0]=" + px[0].r + "," + px[0].g + "," + px[0].b + "," + px[0].a);
                                var rawPng = GlbBuilder.EncodePngRaw(px, w, h);
                                byte[] pngSig = { 0x89, 0x50, 0x4E, 0x47 };
                                int strip = -1;
                                for (int i = 0; i <= rawPng.Length - 4; i++)
                                {
                                    if (rawPng[i] == pngSig[0] && rawPng[i+1] == pngSig[1] && rawPng[i+2] == pngSig[2] && rawPng[i+3] == pngSig[3])
                                    { strip = i; break; }
                                }
                                Log("TEX: rawPng len=" + rawPng.Length + " strip=" + strip + " first=" + rawPng[0].ToString("X2") + rawPng[1].ToString("X2"));
                                if (strip >= 0)
                                {
                                    var clean = new byte[rawPng.Length - strip];
                                    Array.Copy(rawPng, strip, clean, 0, clean.Length);
                                    texPngBytes = clean;
                                    Log("TEX: clean PNG len=" + texPngBytes.Length + " first=" + texPngBytes[0].ToString("X2") + texPngBytes[1].ToString("X2"));
                                }
                                else { texPngBytes = rawPng; Log("TEX: no strip, using raw"); }
                                Log("TEX: PNG strip=" + strip + " len=" + texPngBytes.Length);
                            }
                            else if (mainTex is Texture2D t2d)
                            {
                                bodyTexture = t2d;
                                texPngBytes = GlbBuilder.EncodePngRaw(t2d.GetPixels32(), t2d.width, t2d.height);
                                Log("TEX: using Texture2D " + t2d.width + "x" + t2d.height);
                            }
                        }
                    }
                    catch (Exception texEx) { Log("TEX: error " + texEx.GetType().Name + " " + texEx.Message); }

                    resultGlb = GlbBuilder.Build(bakedMesh, texPngBytes, "body");
                    resultJson = "{\"status\":\"ok\",\"path\":\"" + Escape(capturedPath) + "\",\"size\":" + resultGlb.Length + ",\"texLen\":" + (texPngBytes != null ? texPngBytes.Length : 0) + "}";
                }
                catch (Exception ex)
                {
                    Log("ExportGlb err: " + ex.GetType().Name + " " + ex.Message);
                    resultJson = "{\"error\":\"" + Escape(ex.Message) + "\"}";
                }
                done.Set();
            }));

            done.WaitOne(120000);

            if (resultGlb != null)
            {
                File.WriteAllBytes(capturedPath, resultGlb);
                Log("GLB exported: " + capturedPath + " (" + resultGlb.Length + " bytes)");
            }

            return resultJson ?? "{\"error\":\"timeout\"}";
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

        private Texture2D FindBodyTexture(ChaControl cc)
        {
            try
            {
                var ccType = cc.GetType();
                var chaFileProp = ccType.GetProperty("chaFile", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (ReferenceEquals(chaFileProp, null)) { Log("TEX: chaFile not found"); return null; }
                var chaFile = chaFileProp.GetValue(cc, null);
                if (ReferenceEquals(chaFile, null)) { Log("TEX: chaFile null"); return null; }
                Log("TEX: chaFile type=" + chaFile.GetType().FullName);

                var customField = chaFile.GetType().GetField("custom", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (ReferenceEquals(customField, null)) { Log("TEX: custom field not found"); return null; }
                var custom = customField.GetValue(chaFile);
                if (ReferenceEquals(custom, null)) { Log("TEX: custom null"); return null; }
                Log("TEX: custom type=" + custom.GetType().FullName);

                var bodyField = custom.GetType().GetField("body", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (ReferenceEquals(bodyField, null)) { Log("TEX: body field not found"); return null; }
                var body = bodyField.GetValue(custom);
                if (ReferenceEquals(body, null)) { Log("TEX: body null"); return null; }
                Log("TEX: body type=" + body.GetType().FullName);
                return null;
            }
            catch (Exception ex) { Log("TEX: error " + ex.GetType().Name + " " + ex.Message); return null; }
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
            sb.Append(",\"materials\":[{\"pbrMetallicRoughness\":{\"metallicFactor\":0.0,\"roughnessFactor\":1.0}");
            if (texBV >= 0) sb.Append(",\"baseColorTexture\":{\"index\":0}");
            sb.Append("}]");
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
