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
