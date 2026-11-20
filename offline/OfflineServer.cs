// 7RHYTHM offline server: replaces the original game API (HTTP) and the 7BEAT auth server (TCP 58001).
// Written for the C# 5 compiler used by Windows PowerShell 5.1 (Add-Type), so no C# 6+ syntax.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

public static class OfflineServer
{
    static string dataDir, saveDir, logDir;
    static Dictionary<string, object> state;
    static readonly object stateLock = new object();
    static readonly object logLock = new object();
    static HashSet<string> encryptedEndpoints = new HashSet<string>();
    static Dictionary<string, string> stageToMusic = new Dictionary<string, string>();
    static byte[] authReply;
    static readonly byte[] Key = new byte[16];
    static readonly byte[] IV = new byte[16];
    const string KeyB64 = "AAAAAAAAAAAAAAAAAAAAAA==";
    static string requestId = "0";
    static int httpPortUsed = 45558;
    static Encoding Utf8 = new UTF8Encoding(false);

    static JavaScriptSerializer NewJson()
    {
        JavaScriptSerializer js = new JavaScriptSerializer();
        js.MaxJsonLength = int.MaxValue;
        js.RecursionLimit = 1000;
        return js;
    }

    // ------------------------------------------------------------------ startup
    // addLocalAddresses: false only for testing outside Windows (tools/dev/Host.cs)
    public static void Run(string baseDir, int httpPort, int authPort, string mStagePath, bool addLocalAddresses)
    {
        dataDir = Path.Combine(baseDir, "data");
        httpPortUsed = httpPort;
        saveDir = Path.Combine(baseDir, "save");
        logDir = Path.Combine(baseDir, "logs");
        Directory.CreateDirectory(saveDir);
        Directory.CreateDirectory(logDir);
        Directory.CreateDirectory(Path.Combine(logDir, "unknown"));

        authReply = BuildLoginReply();
        object[] enc = (object[])NewJson().DeserializeObject(File.ReadAllText(Path.Combine(dataDir, "static", "encrypted_endpoints.json"), Utf8));
        foreach (object e in enc) encryptedEndpoints.Add((string)e);

        // Bind both ports first: if another copy of the server is still running, stop here instead of
        // running two servers with different copies of the save in memory.
        TcpListener httpL = new TcpListener(IPAddress.Any, httpPort), authL = new TcpListener(IPAddress.Any, authPort);
        try { httpL.Start(); authL.Start(); }
        catch (SocketException)
        {
            throw new Exception("Port " + httpPort + " or " + authPort + " is already in use - another offline server is probably still running. Close it (or run launch_offline.bat again) and retry.");
        }
        File.WriteAllText(Path.Combine(saveDir, "server.pid"), System.Diagnostics.Process.GetCurrentProcess().Id.ToString());
        if (!string.IsNullOrEmpty(CdnFilesDir) && Directory.Exists(CdnFilesDir)) AssetServer.Start(CdnFilesDir, 45557);
        StartTls(45560);

        string statePath = Path.Combine(saveDir, "state.json");
        bool freshSave = !File.Exists(statePath);
        if (freshSave) File.Copy(Path.Combine(dataDir, "static", "default_state.json"), statePath);
        state = (Dictionary<string, object>)NewJson().DeserializeObject(File.ReadAllText(statePath, Utf8));
        if (freshSave)
        {
            // new save: default name, random 9-digit user ID
            Dictionary<string, object> nus = (Dictionary<string, object>)state["UserStatusData"];
            nus["name"] = "GUEST";
            nus["disp_user_id"] = new Random().Next(100000000, 999999999).ToString();
            SaveState();
            Log("New save created: name GUEST, user ID " + nus["disp_user_id"]);
            ResetSongSelectPrefs();
        }
        if (!state.ContainsKey("EpisodeData"))
        {
            string ef = Path.Combine(Path.Combine(dataDir, "static"), "default_episodes.json");
            state["EpisodeData"] = File.Exists(ef) ? NewJson().DeserializeObject(File.ReadAllText(ef, Utf8)) : new object[0];
        }
        Dictionary<string, object> us = state["UserStatusData"] as Dictionary<string, object>;
        if (us != null && us.ContainsKey("disp_user_id")) requestId = Convert.ToString(us["disp_user_id"]);
        if (us != null && Convert.ToInt64(us.ContainsKey("money") ? us["money"] : 0) != FixedCoins)
        {
            // coins are fixed at 10,000 offline (the gacha is free and nothing else spends them)
            Log("COINS reset from " + (us.ContainsKey("money") ? us["money"] : "none") + " to " + FixedCoins);
            us["money"] = FixedCoins; SaveState();
        }

        LoadStageTable(mStagePath);
        MergeScoreHistory();
        LoadConfig(baseDir);

        Thread t1 = new Thread(delegate () { AuthLoop(authL); });
        t1.IsBackground = true; t1.Start();
        if (addLocalAddresses) StartGameAddressListeners();
        Log("7RHYTHM offline server started. API on 127.0.0.1:" + httpPort + ", 7BEAT login on " + LocalAddresses.AuthIp + ":58001");
        File.WriteAllText(Path.Combine(saveDir, "server.ready"), DateTime.Now.ToString("s"));   // launch_offline.bat waits for this
        HttpLoop(httpL);
    }

    // The game keeps its song-select state (sort/filter, selected song, difficulty, group) in its own PlayerPrefs
    // in the registry, plus its option caches and its pending play log. The play log holds the options of the last
    // session (speed, timing, keys ...) and is sent with the next requests, which would copy them into the new save. For a new save, delete just those values so the game falls back to
    // its defaults. Everything else there (download list, table keys, window size) is left alone.
    static void ResetSongSelectPrefs()
    {
        try
        {
            using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\SammyNetworks\nana-rhythm", true))
            {
                if (k == null) return;
                List<string> removed = new List<string>();
                foreach (string n in k.GetValueNames())
                {
                    if (n.StartsWith("MusicPlaySetting.") || n.StartsWith("MedleyPlaySetting.") || n.StartsWith("sort_setting_Music_Select") || n.StartsWith("option_data_h") || n.StartsWith("local_option_data_h") ||
                        n.StartsWith("LogManager_DataSaveKey_h"))
                    {
                        k.DeleteValue(n, false);
                        int h = n.LastIndexOf("_h"); removed.Add(h > 0 ? n.Substring(0, h) : n);
                    }
                }
                Log("New save: reset song select and option settings (" + (removed.Count > 0 ? string.Join(", ", removed.ToArray()) : "none were set") + ")");
            }
        }
        catch (Exception ex) { Log("Could not reset song select settings: " + ex.Message); }
    }

    static void LoadStageTable(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) { Log("M_stage.json not found - using the built-in stage->song rule"); return; }
            object root = NewJson().DeserializeObject(ReadTable("M_stage", path));
            FindStageRows(root);
            Log("Loaded " + stageToMusic.Count + " stage->song mappings from M_stage.json");
        }
        catch (Exception ex)
        {
            string m = ex.Message; if (m.Length > 120) m = m.Substring(0, 120) + "...";
            Log("Could not read M_stage.json (" + m + ") - using the built-in stage->song rule");
        }
    }

    static Dictionary<string, string[]> tableKeys;

    // Two master tables are served patched instead of the asset-server copies when a fresh install downloads
    // its tables: M_const with the song / medley continue cost set to 0 jewels (continues are free offline), and
    // M_music_mission emptied. Both are built from the game's own files at request time and encrypted with the
    // table's own key, like the originals.
    static readonly string[] PatchedTables = { "M_const", "M_music_mission" };

    static string PatchedTable(string query, out int status)
    {
        Dictionary<string, string> q = ParseForm(query);
        string name = q.ContainsKey("name") ? q["name"] : "";
        status = 404;
        if (Array.IndexOf(PatchedTables, name) < 0) { Log("TABLE patched table " + name + " not available"); return "not found"; }
        string json;
        try
        {
            if (name == "M_const")
            {
                string src = null;
                foreach (string f in Directory.GetFiles(CdnFilesDir, "M_const.json", SearchOption.AllDirectories)) { src = f; break; }
                if (src == null) { Log("TABLE M_const.json not found in the asset server folder"); return "not found"; }
                object[] rows = (object[])NewJson().DeserializeObject(ReadTable("M_const", src));
                foreach (object o in rows)
                {
                    Dictionary<string, object> r = o as Dictionary<string, object>;
                    if (r == null) continue;
                    string k = Convert.ToString(r["key"]);
                    if (k == "stage_continue_in_jewel" || k == "medley_continue_in_jewel") r["value"] = "0";
                }
                json = NewJson().Serialize(rows);
            }
            else json = "[]";
            LoadTableKeys();
            string[] key = tableKeys[name];
            using (RijndaelManaged a = new RijndaelManaged())
            {
                a.Mode = CipherMode.CBC; a.Padding = PaddingMode.PKCS7; a.BlockSize = 128;
                a.Key = B64u(key[0]); a.IV = B64u(key[1]);
                byte[] plain = Utf8.GetBytes(json);
                byte[] enc = a.CreateEncryptor().TransformFinalBlock(plain, 0, plain.Length);
                status = 200;
                Log("TABLE served patched " + name);
                return Convert.ToBase64String(enc);
            }
        }
        catch (Exception ex) { Log("TABLE could not build patched " + name + ": " + ex.Message); status = 500; return "error"; }
    }

    // get/table/data: a fresh install has no table cache and asks for every master table. Reply with where to
    // download each one (the local asset server, or the patched table above) and its AES key/IV.
    static Dictionary<string, string> tableFiles;   // table name -> file in the asset server folder

    static object[] TableFileList(string names)
    {
        LoadTableKeys();
        List<object> list = new List<object>();
        string root = Path.GetFullPath(CdnFilesDir ?? ".").TrimEnd('\\', '/');
        foreach (string raw in names.Split(','))
        {
            string name = raw.Trim();
            string[] k;
            if (name.Length == 0 || !tableKeys.TryGetValue(name, out k)) continue;
            string url = null;
            if (Array.IndexOf(PatchedTables, name) >= 0)
                url = "http://127.0.0.1:" + httpPortUsed + "/7r/patched_table?name=" + name;
            else if (!string.IsNullOrEmpty(CdnFilesDir))
            {
                if (tableFiles == null)
                {
                    tableFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string f in Directory.GetFiles(CdnFilesDir, "M_*.json", SearchOption.AllDirectories))
                    {
                        string n = Path.GetFileNameWithoutExtension(f);
                        if (!tableFiles.ContainsKey(n)) tableFiles[n] = f;
                    }
                }
                string hit;
                if (tableFiles.TryGetValue(name, out hit))
                {
                    string rel = Path.GetFullPath(hit).Substring(root.Length).Replace('\\', '/').TrimStart('/');   // resource/1364/aws/...
                    url = "http://127.0.0.1:45557/Files/" + rel;
                }
            }
            if (url == null) { Log("TABLE " + name + " not found in the asset server folder"); continue; }
            Dictionary<string, object> e = new Dictionary<string, object>();
            e["name"] = name; e["path"] = url; e["key"] = k[0]; e["iv"] = k[1];
            list.Add(e);
        }
        Log("TABLE fresh install: sending download list for " + list.Count + " tables");
        return list.ToArray();
    }
    public static string CdnFilesDir;
    static Dictionary<string, object> rankOverride;   // fields forced into UserStatusData (null = off)
    static long rankExp = -1;

    static void BuildMaxRank()
    {
        string[] hits = Directory.GetFiles(CdnFilesDir, "M_user_level.json", SearchOption.AllDirectories);
        if (hits.Length == 0) { Log("RANK  M_user_level.json not found, max-rank mode off"); return; }
        object[] rows = (object[])NewJson().DeserializeObject(ReadTable("M_user_level", hits[0]));
        Dictionary<string, object> top = null; long topLv = -1;
        foreach (object o in rows)
        {
            Dictionary<string, object> d = (Dictionary<string, object>)o;
            object lv; if (!d.TryGetValue("level", out lv) && !d.TryGetValue("user_level", out lv) && !d.TryGetValue("id", out lv)) continue;
            long l = Convert.ToInt64(lv);
            if (l > topLv) { topLv = l; top = d; }
        }
        if (top == null) { Log("RANK  could not understand M_user_level, max-rank mode off"); return; }
        Log("RANK  top row of M_user_level: " + NewJson().Serialize(top));
        rankOverride = new Dictionary<string, object>();
        rankOverride["level"] = topLv;
        foreach (string k in new string[] { "total_exp", "total_exp_start", "exp", "need_exp", "total_exp_end" })
            if (top.ContainsKey(k)) { rankExp = Convert.ToInt64(top[k]); break; }
        if (rankExp >= 0) rankOverride["total_exp"] = rankExp;
        foreach (string k in new string[] { "max_stamina", "stamina_max", "stamina", "max_stamina_count" })
            if (top.ContainsKey(k)) { rankOverride["stamina_count"] = top[k]; break; }
        foreach (string k in new string[] { "max_friend_count", "friend_max", "max_friend" })
            if (top.ContainsKey(k)) { rankOverride["max_friend_count"] = top[k]; break; }
        Log("RANK  max-rank mode: rank " + topLv + (rankExp >= 0 ? ", exp " + rankExp : ""));
    }

    // Fixed offline settings (not editable): every card from rarity N up at max level, max rank, every episode,
    // event chapter and title unlocked, coins fixed at 10,000, website links removed, and this home screen message.
    const long FixedCoins = 10000;
    const string FixedHomeMessage = "7RHYTHM -nanarhythm- Offline Version";
    // All settings are fixed now, so config.json is no longer used. A config.json left over from an older
    // version is deleted at startup.
    static void LoadConfig(string baseDir)
    {
        string f = Path.Combine(baseDir, "config.json");
        if (File.Exists(f))
        {
            try { File.Delete(f); Log("CONFIG config.json is no longer used (all settings are fixed) - removed it"); }
            catch (Exception ex) { Log("CONFIG config.json is no longer used and is ignored (" + ex.Message + ")"); }
        }
        homeMessage = FixedHomeMessage; homeMessageUrl = "";
        removeWebLinks = true;
        Log("LINKS website links from the server are removed (home banner link, gacha details, notices)");
        if (string.IsNullOrEmpty(CdnFilesDir)) { Log("CARDS no asset folder - unlock lists not built"); return; }

        try { AllCards.Build(CdnFilesDir, "N", (object[])state["UserHasCardData"]); }
        catch (Exception ex) { AllCards.Cards = null; Log("CARDS all-cards list failed: " + ex.Message); }
        try { BuildMaxRank(); } catch (Exception ex) { Log("RANK  max rank failed: " + ex.Message); }
        try { BuildAllEpisodes(); } catch (Exception ex) { allEpisodes = null; Log("EPISODE unlock-all-episodes failed: " + ex.Message); }
        unlockAllEventEpisodes = true;
        try { BuildEpisodeEvents(); } catch (Exception ex) { epEvents.Clear(); Log("EVENT episode events failed: " + ex.Message); }
        try { BuildAllAwards(); } catch (Exception ex) { allAwards = null; Log("TITLE unlock-all-titles failed: " + ex.Message); }
        try { AllCards.LoadPool(CdnFilesDir); }
        catch (Exception ex) { Log("GACHA could not load the card pool: " + ex.Message); }
    }

    // Writes decrypted copies of the named master tables to outDir (skips ones already there).
    public static void DumpTables(string baseDir, string cdnFilesDir, string outDir, string[] names)
    {
        dataDir = Path.Combine(baseDir, "data");
        Directory.CreateDirectory(outDir);
        foreach (string n in names)
        {
            string dst = Path.Combine(outDir, n + ".json");
            if (File.Exists(dst)) continue;
            try
            {
                string[] hits = Directory.GetFiles(cdnFilesDir, n + ".json", SearchOption.AllDirectories);
                if (hits.Length == 0) { Console.WriteLine("table " + n + " not found"); continue; }
                File.WriteAllText(dst, ReadTable(n, hits[0]), Utf8);
                Console.WriteLine("decrypted " + n);
            }
            catch (Exception ex) { Console.WriteLine("table " + n + ": " + ex.Message); }
        }
    }

    static byte[] B64u(string s)
    {
        s = s.Trim().Replace('-', '+').Replace('_', '/').TrimEnd('=');
        return Convert.FromBase64String(s + new string('=', (4 - s.Length % 4) % 4));
    }

    // Reads a master table (M_*.json from the asset server folder). These are base64url text encrypted with
    // AES-128-CBC; the per-table key/IV come from data\static\table_keys.json. Plain JSON is accepted too.
    public static string ReadTable(string name, string path)
    {
        string text = File.ReadAllText(path, Utf8).Trim().TrimStart('\uFEFF');
        if (text.StartsWith("[") || text.StartsWith("{")) return text;
        LoadTableKeys();
        string[] k;
        if (!tableKeys.TryGetValue(name, out k)) throw new Exception("no key for table " + name);
        using (RijndaelManaged a = new RijndaelManaged())
        {
            a.Mode = CipherMode.CBC; a.Padding = PaddingMode.PKCS7; a.BlockSize = 128;
            a.Key = B64u(k[0]); a.IV = B64u(k[1]);
            byte[] enc = B64u(text);
            byte[] plain = a.CreateDecryptor().TransformFinalBlock(enc, 0, enc.Length);
            return Utf8.GetString(plain).TrimStart('\uFEFF');
        }
    }

    static void LoadTableKeys()
    {
        if (tableKeys == null)
        {
            tableKeys = new Dictionary<string, string[]>();
            string kf = Path.Combine(Path.Combine(dataDir, "static"), "table_keys.json");
            Dictionary<string, object> kj = (Dictionary<string, object>)NewJson().DeserializeObject(File.ReadAllText(kf, Utf8));
            foreach (object o in (object[])kj["list"])
            {
                Dictionary<string, object> e = (Dictionary<string, object>)o;
                tableKeys[(string)e["name"]] = new string[] { (string)e["key"], (string)e["iv"] };
            }
        }
    }

    // Accepts either a list of objects with stage_id/music_id, or a header row followed by value rows.
    static void FindStageRows(object node)
    {
        object[] arr = node as object[];
        Dictionary<string, object> obj = node as Dictionary<string, object>;
        if (obj != null)
        {
            if (obj.ContainsKey("stage_id") && obj.ContainsKey("music_id"))
            { stageToMusic[Convert.ToString(obj["stage_id"])] = Convert.ToString(obj["music_id"]); return; }
            if (obj.ContainsKey("id") && obj.ContainsKey("music_id") && obj.ContainsKey("difficulty_id"))
            { stageToMusic[Convert.ToString(obj["id"])] = Convert.ToString(obj["music_id"]); return; }
            foreach (object v in obj.Values) FindStageRows(v);
            return;
        }
        if (arr == null) return;
        if (arr.Length > 0 && arr[0] is object[])
        {
            object[] head = (object[])arr[0];
            int si = Array.IndexOf(head, (object)"stage_id"), mi = Array.IndexOf(head, (object)"music_id");
            if (si >= 0 && mi >= 0)
            {
                for (int i = 1; i < arr.Length; i++)
                {
                    object[] r = arr[i] as object[];
                    if (r != null && r.Length > Math.Max(si, mi)) stageToMusic[Convert.ToString(r[si])] = Convert.ToString(r[mi]);
                }
                return;
            }
        }
        foreach (object v in arr) FindStageRows(v);
    }

    public static void Log(string msg)
    {
        string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg;
        lock (logLock)
        {
            Console.WriteLine(line);
            try { File.AppendAllText(Path.Combine(logDir, "server.log"), line + "\r\n", Utf8); } catch { }
        }
    }

    // ------------------------------------------------------------------ TCP 58001 (7BEAT auth)
    static void AuthLoop(TcpListener l)
    {
        while (true)
        {
            TcpClient c = l.AcceptTcpClient();
            Log("AUTH  connection from " + c.Client.RemoteEndPoint);
            Thread t = new Thread(delegate () { HandleAuth(c); });
            t.IsBackground = true; t.Start();
        }
    }

    // Reply to the 7BEAT login (message 0x80000002). Same 32-byte header layout as the game's own request:
    // "MBH " marker, version 100, then status, payload length, message type 2 (login reply). Status 0 means the
    // payload is plain (not encrypted); a 112-byte payload of zeros is accepted by the game as a successful login.
    static byte[] BuildLoginReply()
    {
        const int payload = 112;
        byte[] r = new byte[32 + payload];
        Encoding.ASCII.GetBytes("MBH ").CopyTo(r, 0);
        BitConverter.GetBytes(100u).CopyTo(r, 4);
        BitConverter.GetBytes(0u).CopyTo(r, 12);          // status 0: plain payload
        BitConverter.GetBytes((uint)payload).CopyTo(r, 16);
        BitConverter.GetBytes(2u).CopyTo(r, 24);          // message type: login reply
        return r;
    }

    static void HandleAuth(TcpClient c)
    {
        try
        {
            using (c)
            {
                NetworkStream s = c.GetStream();
                c.ReceiveTimeout = 10000;
                byte[] buf = new byte[4096];
                int n = s.Read(buf, 0, buf.Length);
                if (n < 0x20) return;
                uint type = BitConverter.ToUInt32(buf, 0x18);
                Log("AUTH  message type 0x" + type.ToString("X8") + " (" + n + " bytes)");
                // 0x80000002 = login with 7BEATKEY: answered with the reply built by BuildLoginReply().
                // Other messages (0x80000007 save, 0x90000002 logout) need no reply.
                if (type == 0x80000002)
                {
                    s.Write(authReply, 0, authReply.Length);
                    s.Flush();
                    Log("AUTH  login reply sent (" + authReply.Length + " bytes)");
                    Thread.Sleep(200);
                }
                else
                {
                    try { while (s.Read(buf, 0, buf.Length) > 0) { } } catch { }
                }
            }
        }
        catch (Exception ex) { Log("AUTH error: " + ex.Message); }
    }

    // ------------------------------------------------------------------ HTTP API
    static void HttpLoop(TcpListener l)
    {
        while (true)
        {
            TcpClient c = l.AcceptTcpClient();
            Log("HTTP  connection from " + c.Client.RemoteEndPoint);
            Thread t = new Thread(delegate () { HandleHttpConnection(c); });
            t.IsBackground = true; t.Start();
        }
    }

    static string ReadLine(Stream s)
    {
        MemoryStream ms = new MemoryStream();
        int b;
        while ((b = s.ReadByte()) >= 0)
        {
            if (b == '\n') break;
            if (b != '\r') ms.WriteByte((byte)b);
        }
        if (b < 0 && ms.Length == 0) return null;
        return Encoding.ASCII.GetString(ms.ToArray());
    }

    static byte[] ReadExact(Stream s, int len)
    {
        byte[] buf = new byte[len];
        int off = 0;
        while (off < len)
        {
            int n = s.Read(buf, off, len - off);
            if (n <= 0) throw new IOException("connection closed");
            off += n;
        }
        return buf;
    }

    // HTTPS listener for the game's first request of every launch (https://pcgame.7rhythm.jp/...). The redirect
    // sends port 443 traffic here; it uses a self-signed certificate (data\static\tls.pfx) and then answers
    // exactly like the plain HTTP API.
    static void TlsLoop(TcpListener l, System.Security.Cryptography.X509Certificates.X509Certificate2 cert)
    {
        while (true)
        {
            TcpClient c = l.AcceptTcpClient();
            Log("HTTPS connection from " + c.Client.RemoteEndPoint);
            Thread t = new Thread(delegate ()
            {
                try
                {
                    using (c)
                    {
                        System.Net.Security.SslStream ssl = new System.Net.Security.SslStream(c.GetStream(), false);
                        ssl.AuthenticateAsServer(cert, false, System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls11 | System.Security.Authentication.SslProtocols.Tls, false);
                        HandleHttpStream(ssl, true);
                    }
                }
                catch (Exception ex) { Log("HTTPS error: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message)); }
            });
            t.IsBackground = true; t.Start();
        }
    }

    static void StartTls(int port) { StartTls(IPAddress.Any, port); }

    // Listens on the game's fixed server addresses (made local by LocalAddresses): the 7BEAT login on
    // 210.128.4.130:58001, and HTTPS (443) / HTTP (80) on the pcgame.7rhythm.jp addresses.
    static void StartGameAddressListeners()
    {
        LocalAddresses.CheckHostsEntry();
        if (!LocalAddresses.Add()) Log("ADDR  not all addresses could be added - the game may not be able to connect");
        try
        {
            TcpListener a = new TcpListener(IPAddress.Parse(LocalAddresses.AuthIp), 58001);
            a.Start();
            Thread t = new Thread(delegate () { AuthLoop(a); });
            t.IsBackground = true; t.Start();
        }
        catch (Exception ex) { Log("AUTH  could not listen on " + LocalAddresses.AuthIp + ":58001: " + ex.Message); }
        foreach (string ip in LocalAddresses.ApiIps)
        {
            StartTls(IPAddress.Parse(ip), 443);
            try
            {
                TcpListener h = new TcpListener(IPAddress.Parse(ip), 80);
                h.Start();
                Thread t = new Thread(delegate () { HttpLoop(h); });
                t.IsBackground = true; t.Start();
            }
            catch (Exception ex) { Log("HTTP  could not listen on " + ip + ":80 (" + ex.Message + ") - not needed for normal starts"); }
        }
    }

    static void StartTls(IPAddress ip, int port)
    {
        try
        {
            string pfx = Path.Combine(Path.Combine(dataDir, "static"), "tls.pfx");
            System.Security.Cryptography.X509Certificates.X509Certificate2 cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(pfx, "7rhythm",
                System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.MachineKeySet | System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.PersistKeySet);
            TcpListener l = new TcpListener(ip, port);
            l.Start();
            Thread t = new Thread(delegate () { TlsLoop(l, cert); });
            t.IsBackground = true; t.Start();
            Log("HTTPS listener on " + (ip.Equals(IPAddress.Any) ? "port " : ip + ":") + port + " (for https://pcgame.7rhythm.jp)");
        }
        catch (Exception ex) { Log("HTTPS listener could not start: " + ex.Message); }
    }

    static void HandleHttpConnection(TcpClient c)
    {
        try
        {
            using (c) HandleHttpStream(c.GetStream(), false);
        }
        catch (IOException) { }
        catch (Exception ex) { Log("HTTP connection error: " + ex.Message); }
    }

    static void HandleHttpStream(Stream raw, bool tls)
    {
        try
        {
            {
                Stream s = new BufferedStream(raw);
                while (true)
                {
                    string reqLine = ReadLine(s);
                    if (reqLine == null) return;
                    if (reqLine.Length == 0) continue;
                    string[] parts = reqLine.Split(' ');
                    if (parts.Length < 2) return;
                    Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    string h;
                    while ((h = ReadLine(s)) != null && h.Length > 0)
                    {
                        int i = h.IndexOf(':');
                        if (i > 0) headers[h.Substring(0, i).Trim()] = h.Substring(i + 1).Trim();
                    }
                    byte[] body = new byte[0];
                    string v;
                    if (headers.TryGetValue("Content-Length", out v)) body = ReadExact(s, int.Parse(v));
                    else if (headers.TryGetValue("Transfer-Encoding", out v) && v.ToLower().Contains("chunked"))
                    {
                        MemoryStream ms = new MemoryStream();
                        while (true)
                        {
                            int size = int.Parse(ReadLine(s).Split(';')[0].Trim(), NumberStyles.HexNumber);
                            if (size == 0) { while (!string.IsNullOrEmpty(ReadLine(s))) { } break; }
                            byte[] chunk = ReadExact(s, size); ms.Write(chunk, 0, size); ReadLine(s);
                        }
                        body = ms.ToArray();
                    }

                    string path = parts[1];
                    string query = "";
                    int q = path.IndexOf('?');
                    if (q >= 0) { query = path.Substring(q + 1); path = path.Substring(0, q); }
                    if (path.StartsWith("http://") || path.StartsWith("https://")) { int p = path.IndexOf('/', path.IndexOf("//") + 2); path = p >= 0 ? path.Substring(p) : "/"; }
                    path = path.Trim('/');
                    if (path.StartsWith("7r/")) path = path.Substring(3);

                    int status;
                    string resp;
                    if (tls) Log("HTTPS request: " + parts[0] + " /" + path);
                    if (path == "patched_table") resp = PatchedTable(query, out status);
                    else
                    try { resp = Handle(path, Utf8.GetString(body), out status); }
                    catch (Exception ex) { Log("ERROR handling " + path + ": " + ex); status = 500; resp = "server error"; }

                    byte[] rb = Utf8.GetBytes(resp);
                    string head = "HTTP/1.1 " + status + (status == 200 ? " OK" : status == 404 ? " Not Found" : " Error") + "\r\n" +
                        "Content-Type: text/plain;charset=utf-8\r\nContent-Length: " + rb.Length + "\r\n" +
                        "Connection: keep-alive\r\n\r\n";
                    byte[] hb = Encoding.ASCII.GetBytes(head);
                    s.Write(hb, 0, hb.Length);
                    s.Write(rb, 0, rb.Length);
                    s.Flush();
                    if (headers.TryGetValue("Connection", out v) && v.ToLower() == "close") return;
                }
            }
        }
        catch (IOException) { }
        catch (Exception ex) { Log("HTTP connection error: " + ex.Message); }
    }

    // ------------------------------------------------------------------ crypto / encoding
    static byte[] Aes(byte[] data, bool encrypt)
    {
        using (RijndaelManaged a = new RijndaelManaged())
        {
            a.Mode = CipherMode.CBC; a.Padding = PaddingMode.PKCS7; a.KeySize = 128; a.BlockSize = 128;
            a.Key = Key; a.IV = IV;
            ICryptoTransform t = encrypt ? a.CreateEncryptor() : a.CreateDecryptor();
            return t.TransformFinalBlock(data, 0, data.Length);
        }
    }

    static Dictionary<string, string> ParseForm(string body)
    {
        Dictionary<string, string> f = new Dictionary<string, string>();
        foreach (string kv in body.Split('&'))
        {
            if (kv.Length == 0) continue;
            int i = kv.IndexOf('=');
            string k = i >= 0 ? kv.Substring(0, i) : kv, val = i >= 0 ? kv.Substring(i + 1) : "";
            f[Uri.UnescapeDataString(k.Replace('+', ' '))] = Uri.UnescapeDataString(val.Replace('+', ' '));
        }
        return f;
    }

    static Dictionary<string, object> DecodeParam(string p)
    {
        string b = p.Replace('-', '+').Replace('_', '/').Replace(' ', '+').TrimEnd('=');
        b += new string('=', (4 - b.Length % 4) % 4);
        byte[] plain = Aes(Convert.FromBase64String(b), false);
        string s = Utf8.GetString(plain).TrimEnd('\0', '\t', ' ', '\r', '\n');
        return NewJson().DeserializeObject(s) as Dictionary<string, object>;
    }

    // ------------------------------------------------------------------ request handling
    static object LoadTemplate(string ep)
    {
        string f = Path.Combine(Path.Combine(dataDir, "templates"), ep.Replace('/', '_') + ".json");
        if (!File.Exists(f)) return null;
        return NewJson().DeserializeObject(File.ReadAllText(f, Utf8));
    }

    static string Envelope(Dictionary<string, object> data)
    {
        return Envelope(data, true);
    }

    static string Envelope(Dictionary<string, object> data, bool withKey)
    {
        Dictionary<string, object> env = new Dictionary<string, object>();
        if (data != null) env["data"] = data;
        env["result"] = "true";
        env["button_msg"] = "OK";
        env["jump_to_url"] = "";
        env["jump_to_title"] = "false";
        env["server_time"] = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
        env["request_id"] = withKey ? requestId : "0";
        if (withKey) { env["key"] = KeyB64; env["iv"] = KeyB64; }
        return NewJson().Serialize(env);
    }

    static string Handle(string ep, string body, out int status)
    {
        status = 200;
        Dictionary<string, string> form = ParseForm(body);
        Dictionary<string, object> p = new Dictionary<string, object>();
        string pv;
        if (form.TryGetValue("param", out pv) && pv.Length > 0)
        {
            try { p = DecodeParam(pv) ?? p; } catch (Exception ex) { Log("could not decode param for " + ep + ": " + ex.Message); }
        }
        foreach (KeyValuePair<string, string> kv in form) if (kv.Key != "param" && !p.ContainsKey(kv.Key)) p[kv.Key] = kv.Value;

        Dictionary<string, object> data;
        bool known = true;
        lock (stateLock)
        {
            UpdateOptionsFromLog(p);
            if (ep == "get/request/data") { Log("API   " + ep); return Envelope(null); }

            object tpl = LoadTemplate(ep);
            data = tpl as Dictionary<string, object>;
            if (data == null)
            {
                known = false;
                // Unknown endpoint: answer with just the common user blocks so the client keeps going.
                data = (Dictionary<string, object>)LoadTemplate("common");
            }
            FillState(data);

            if (ep == "get/total/score/ranking/list") { TotalScoreRanking(data); known = true; }
            if (!known && EmptyReply(ep, p, data)) known = true;
            if (ep == "get/connection")
            {
                // point the game's API straight at this server (no DNS lookup or redirect needed for the API)
                Dictionary<string, object> cd = Get(data, "ConnectionData");
                if (cd != null) cd["url"] = "http://127.0.0.1:" + httpPortUsed + "/7r/";
            }
            if (ep == "get/register/data")
            {
                Dictionary<string, object> rd = Get(data, "RegisterData");
                if (rd != null) rd["private_disp_user_id"] = requestId;
            }
            else if (ep == "get/award/data") { data["UserAwardData"] = AwardList(); known = true; }
            else if (ep == "change/award") { ChangeAward(S(p, "award_id", null)); known = true; }
            else if (ep == "remove/award") { ChangeAward("1"); known = true; }
            if (ep == "home" && homeMessage != null)
            {
                if (homeMessage.Length == 0) data["HomeMessageData"] = new object[0];
                else
                {
                    Dictionary<string, object> hm = new Dictionary<string, object>();
                    hm["text"] = homeMessage; hm["url"] = homeMessageUrl;
                    data["HomeMessageData"] = new object[] { hm };
                }
            }
            if (ep == "get/friend/follow/list") { if (FillHelpers(data)) known = true; }
            if (ep == "get/episode/list" && allEpisodes != null) data["EpisodeData"] = allEpisodes;
            if ((ep == "get/event/episode/list" || ep == "get/event/episode/detail") && epEvents.Count > 0)
            {
                FillEpisodeEvents(data, ep == "get/event/episode/detail" ? S(p, "episode_event_id", null) : null);
                known = true;
            }
            if (ep == "update/episode/event/dialog") known = true;
            if (ep == "get/table/data")
            {
                data = new Dictionary<string, object>();
                data["TableUpdateFileList"] = TableFileList(S(p, "table_name", ""));
                known = true;
            }
            if (ep == "play/gacha") PlayGacha(p, data);
            else if (ep == "episode/finish") EpisodeFinish(p);
            if (ep == "stage/start") StageStart(p, data);
            else if (ep == "stage/finish") StageFinish(p, data);
            else if (ep == "change/name") { ChangeName(p); known = true; }
            else if (ep == "card/edit/deck") { EditDeck(p); data["DeckData"] = state["DeckData"]; known = true; }
            FillState(data);
        }

        if (!known)
        {
            Log("API   " + ep + "  (not yet supported by the offline server - generic reply; request saved to logs\\unknown)");
            try { File.WriteAllText(Path.Combine(Path.Combine(logDir, "unknown"), ep.Replace('/', '_') + ".json"), NewJson().Serialize(p), Utf8); } catch { }
        }
        else Log("API   " + ep);

        if (ep == "get/gacha/list/data" && data.ContainsKey("GachaListData") && data["GachaListData"] is object[])
        {
            // hide the 抽選内容 (draw contents) button under each gacha banner
            foreach (object g in (object[])data["GachaListData"])
            {
                Dictionary<string, object> gd = g as Dictionary<string, object>;
                if (gd != null && gd.ContainsKey("is_view_lottery")) gd["is_view_lottery"] = "false";
            }
        }
        if (removeWebLinks) StripWebLinks(ep, data);
        string json = Envelope(data, ep != "get/connection");
        if (encryptedEndpoints.Contains(ep)) return Convert.ToBase64String(Aes(Utf8.GetBytes(json), true));
        return json;
    }

    // Replace "$state" placeholders (and keys owned by the save state) with the current save data.
    static void FillState(Dictionary<string, object> data)
    {
        List<string> keys = new List<string>(data.Keys);
        foreach (string k in keys)
        {
            object v = data[k];
            if (v is string && (string)v == "$state")
                data[k] = state.ContainsKey(k) ? state[k] : new object[0];
            if (k == "UserStatusData" && data[k] is Dictionary<string, object> && (AllCards.Cards != null || rankOverride != null))
            {
                Dictionary<string, object> us = new Dictionary<string, object>((Dictionary<string, object>)data[k]);
                if (AllCards.Cards != null)
                {
                    us["user_has_card_count"] = AllCards.Count;
                    us["card_storage_count"] = Math.Max(Convert.ToInt32(us["card_storage_count"]), AllCards.Count + 50);
                }
                if (rankOverride != null) foreach (KeyValuePair<string, object> kv in rankOverride) us[kv.Key] = kv.Value;
                data[k] = us;
            }
            else if (k == "UserLevelUpData" && rankExp >= 0 && data[k] is Dictionary<string, object>)
            {
                Dictionary<string, object> lu = new Dictionary<string, object>((Dictionary<string, object>)data[k]);
                lu["before_exp"] = rankExp; lu["after_exp"] = rankExp; lu["is_level_up"] = false;
                data[k] = lu;
            }
            if (AllCards.Cards == null) continue;
            if (k == "UserHasCardData" && data[k] is object[] && ((object[])data[k]).Length > 0) data[k] = AllCards.Cards;
            else if (k == "CardDictionaryData" && data[k] is object[] && ((object[])data[k]).Length > 0) data[k] = AllCards.Dictionary;
        }
    }

    static void SaveState()
    {
        string path = Path.Combine(saveDir, "state.json");
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, NewJson().Serialize(state), Utf8);
        if (File.Exists(path)) File.Replace(tmp, path, path + ".bak"); else File.Move(tmp, path);
    }

    static Dictionary<string, object> Get(Dictionary<string, object> d, string k)
    {
        object v; return d.TryGetValue(k, out v) ? v as Dictionary<string, object> : null;
    }

    static string S(Dictionary<string, object> p, string k, string def)
    {
        object v;
        return p.TryGetValue(k, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : def;
    }

    static long L(Dictionary<string, object> p, string k)
    {
        long r; return long.TryParse(S(p, k, "0"), out r) ? r : 0;
    }

    // The client sends its option settings inside log_data on every request; keep UserOptionData in sync.
    static void UpdateOptionsFromLog(Dictionary<string, object> p)
    {
        try
        {
            string ld = S(p, "log_data", "");
            if (ld.Length < 3) return;
            object[] arr = NewJson().DeserializeObject(ld.Replace("\\\"", "\"")) as object[];
            if (arr == null || arr.Length == 0) return;
            Dictionary<string, object> log = arr[arr.Length - 1] as Dictionary<string, object>;
            object[] opts = state["UserOptionData"] as object[];
            if (log == null || opts == null || opts.Length == 0) return;
            Dictionary<string, object> o = (Dictionary<string, object>)opts[0];
            bool changed = false;
            foreach (string k in new string[] { "se_id", "se_volume", "bgm_volume", "lane_alpha", "key_config_text", "notes_timing", "notes_speed", "is_auto_fever", "is_disp_key", "is_play_movie" })
            {
                if (!log.ContainsKey(k)) continue;
                string val = Convert.ToString(log[k], CultureInfo.InvariantCulture).Replace("lldbqtll", "\"");
                if (!o.ContainsKey(k) || Convert.ToString(o[k], CultureInfo.InvariantCulture) != val) { o[k] = val; changed = true; }
            }
            if (changed) SaveState();
        }
        catch (Exception ex) { Log("option sync skipped: " + ex.Message); }
    }

    static void EditDeck(Dictionary<string, object> p)
    {
        object[] posted = NewJson().DeserializeObject(S(p, "deck_data", "[]")) as object[];
        if (posted == null || posted.Length == 0) return;
        HashSet<string> decks = new HashSet<string>();
        foreach (object o in posted) decks.Add(Convert.ToString(((Dictionary<string, object>)o)["deck_index"]));
        List<object> nl = new List<object>();
        foreach (object o in (object[])state["DeckData"])
            if (!decks.Contains(Convert.ToString(((Dictionary<string, object>)o)["deck_index"]))) nl.Add(o);
        nl.AddRange(posted);
        nl.Sort(delegate (object a, object b)
        {
            Dictionary<string, object> x = (Dictionary<string, object>)a, y = (Dictionary<string, object>)b;
            int c = Convert.ToInt32(x["deck_index"]).CompareTo(Convert.ToInt32(y["deck_index"]));
            return c != 0 ? c : Convert.ToInt32(x["index"]).CompareTo(Convert.ToInt32(y["index"]));
        });
        state["DeckData"] = nl.ToArray();
        string active = S(p, "active_deck_index", null);
        if (active != null) ((Dictionary<string, object>)state["UserStatusData"])["active_deck_index"] = Convert.ToInt32(active);
        SaveState();
        Log("DECK  saved deck " + string.Join(",", new List<string>(decks).ToArray()));
    }

    static bool removeWebLinks = true;
    static string homeMessage = null, homeMessageUrl = "";   // home screen ticker text (null = keep template)
    static object[] allAwards;      // unlock-all-titles list (null = off)

    static void BuildAllAwards()
    {
        string[] hits = Directory.GetFiles(CdnFilesDir, "M_award.json", SearchOption.AllDirectories);
        if (hits.Length == 0) { Log("TITLE M_award.json not found, unlock-all-titles off"); return; }
        object[] rows = (object[])NewJson().DeserializeObject(ReadTable("M_award", hits[0]));
        List<object> list = new List<object>();
        foreach (object o in rows)
        {
            Dictionary<string, object> d = (Dictionary<string, object>)o;
            object id; if (!d.TryGetValue("id", out id)) continue;
            Dictionary<string, object> e = new Dictionary<string, object>();
            e["award_id"] = Convert.ToString(id);
            e["expired_at"] = -1;     // no expiry
            list.Add(e);
        }
        allAwards = list.ToArray();
        Log("TITLE unlock-all-titles mode: " + allAwards.Length + " titles (first row: " + (rows.Length > 0 ? NewJson().Serialize(rows[0]) : "none") + ")");
    }

    static object[] AwardList()
    {
        if (allAwards != null) return allAwards;
        // normal mode: the titles the save owns (at least the current one)
        object owned;
        if (state.TryGetValue("UserAwardData", out owned) && owned is object[] && ((object[])owned).Length > 0) return (object[])owned;
        Dictionary<string, object> e = new Dictionary<string, object>();
        e["award_id"] = Convert.ToString(((Dictionary<string, object>)state["UserStatusData"])["award_id"]);
        e["expired_at"] = -1;
        return new object[] { e };
    }

    static void ChangeAward(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        ((Dictionary<string, object>)state["UserStatusData"])["award_id"] = id;
        SaveState();
        Log("TITLE set to " + id);
    }

    static readonly Random rng = new Random();

    // Gacha: random deck-usable cards at level 1, no coins taken.
    static void PlayGacha(Dictionary<string, object> p, Dictionary<string, object> data)
    {
        int count = (int)Math.Max(1, Math.Min(100, L(p, "play_count")));
        if (AllCards.Pool == null || AllCards.Pool.Count == 0) { Log("GACHA no card pool loaded"); data["GachaRewardData"] = new object[0]; return; }
        List<object> owned = new List<object>((object[])state["UserHasCardData"]);
        long nextId = 0;
        foreach (object o in owned) { long v; if (long.TryParse(Convert.ToString(((Dictionary<string, object>)o)["user_has_card_id"]), out v) && v > nextId) nextId = v; }
        HashSet<string> seen = new HashSet<string>();
        List<object> dictList = new List<object>(state.ContainsKey("CardDictionaryData") ? (object[])state["CardDictionaryData"] : new object[0]);
        foreach (object o in dictList) seen.Add(Convert.ToString(((Dictionary<string, object>)o)["card_id"]));
        List<object> rewards = new List<object>(), names = new List<object>();
        for (int i = 0; i < count; i++)
        {
            Dictionary<string, object> c = AllCards.Pool[rng.Next(AllCards.Pool.Count)];
            string cid = Convert.ToString(c["id"]);
            bool first = seen.Add(cid);
            if (first) { Dictionary<string, object> de = new Dictionary<string, object>(); de["card_id"] = cid; de["is_new"] = "true"; dictList.Add(de); }
            Dictionary<string, object> card = new Dictionary<string, object>();
            card["card_id"] = cid; card["level"] = 1; card["total_exp"] = 1; card["skill_level"] = 1;
            card["hit_perfect_num"] = c["default_hit_perfect_num"]; card["hit_great_num"] = c["default_hit_great_num"];
            card["drop_ratio"] = c["default_drop_ratio"]; card["is_protected"] = "false"; card["is_new"] = "true";
            card["user_has_card_id"] = (++nextId).ToString();
            owned.Add(card);
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["card_id"] = cid; r["level"] = 1; r["skill_level"] = 1; r["first_discovery"] = first ? "true" : "false";
            rewards.Add(r);
            names.Add(c["name"]);
        }
        state["UserHasCardData"] = owned.ToArray();
        state["CardDictionaryData"] = dictList.ToArray();
        ((Dictionary<string, object>)state["UserStatusData"])["user_has_card_count"] = owned.Count;
        SaveState();
        data["GachaRewardData"] = rewards.ToArray();
        Log("GACHA " + count + "x: " + string.Join(", ", Array.ConvertAll(names.ToArray(), x => Convert.ToString(x))));
    }

    static object[] allEpisodes;   // unlock-all-episodes list (null = off)

    static void BuildAllEpisodes()
    {
        string[] hits = Directory.GetFiles(CdnFilesDir, "M_episode.json", SearchOption.AllDirectories);
        if (hits.Length == 0) { Log("EPISODE M_episode.json not found, unlock-all-episodes off"); return; }
        object[] rows = (object[])NewJson().DeserializeObject(ReadTable("M_episode", hits[0]));
        List<object> list = new List<object>();
        foreach (object o in rows)
        {
            Dictionary<string, object> d = (Dictionary<string, object>)o;
            object id; if (!d.TryGetValue("id", out id) && !d.TryGetValue("episode_id", out id)) continue;
            if (d.ContainsKey("type") && Convert.ToString(d["type"]) == "episode_event") continue;   // these go in the event tab
            Dictionary<string, object> e = new Dictionary<string, object>();
            e["episode_id"] = Convert.ToString(id);
            e["is_new"] = "false";
            list.Add(e);
        }
        allEpisodes = list.ToArray();
        Log("EPISODE unlock-all-episodes mode: " + allEpisodes.Length + " episodes (first row: " + (rows.Length > 0 ? NewJson().Serialize(rows[0]) : "none") + ")");
    }

    // ---- episode events (event tab). Built from M_episode rows of type "episode_event", grouped by story_NNN.
    class EpEvent { public int Id; public string Name; public string Image; public List<object[]> Chapters = new List<object[]>(); }  // chapter: {episodeId, detailId, challengeId}
    static List<EpEvent> epEvents = new List<EpEvent>();
    static bool unlockAllEventEpisodes;

    static void BuildEpisodeEvents()
    {
        string[] hits = Directory.GetFiles(CdnFilesDir, "M_episode.json", SearchOption.AllDirectories);
        if (hits.Length == 0) return;
        List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
        foreach (object o in (object[])NewJson().DeserializeObject(ReadTable("M_episode", hits[0])))
        {
            Dictionary<string, object> d = (Dictionary<string, object>)o;
            if (Convert.ToString(d["type"]) == "episode_event") rows.Add(d);
        }
        rows.Sort(delegate (Dictionary<string, object> a, Dictionary<string, object> b) { return Convert.ToInt32(a["id"]).CompareTo(Convert.ToInt32(b["id"])); });
        Dictionary<string, EpEvent> byStory = new Dictionary<string, EpEvent>();
        foreach (Dictionary<string, object> r in rows)
        {
            string rp = Convert.ToString(r["resource_path"]);              // story_001_01
            string story = rp.Length > 9 ? rp.Substring(0, 9) : rp;        // story_001
            EpEvent ev;
            if (!byStory.TryGetValue(story, out ev))
            {
                ev = new EpEvent();
                int n; ev.Id = int.TryParse(story.Replace("story_", ""), out n) ? n : byStory.Count + 1;
                ev.Name = System.Text.RegularExpressions.Regex.Replace(Convert.ToString(r["name"]), "[0-9]+話$", "");
                ev.Image = Convert.ToString(r["image_thumb_id"]).Replace("Chr_thum_", "Chr_card_");
                byStory[story] = ev; epEvents.Add(ev);
            }
            ev.Chapters.Add(new object[] { Convert.ToString(r["id"]), (ev.Id * 100 + ev.Chapters.Count + 1).ToString(), "-1" });
        }
        // link chapters to M_episode_event_challenge (unlock conditions) when that table has one row per chapter
        try
        {
            string[] ch = Directory.GetFiles(CdnFilesDir, "M_episode_event_challenge.json", SearchOption.AllDirectories);
            if (ch.Length > 0)
            {
                object[] crow = (object[])NewJson().DeserializeObject(ReadTable("M_episode_event_challenge", ch[0]));
                SortedDictionary<int, string> detailToChallenge = new SortedDictionary<int, string>();
                foreach (object o in crow)
                {
                    Dictionary<string, object> d = (Dictionary<string, object>)o;
                    int det = Convert.ToInt32(d["episode_event_detail_id"]);
                    if (!detailToChallenge.ContainsKey(det)) detailToChallenge[det] = Convert.ToString(d["id"]);
                }
                int total = 0; foreach (EpEvent ev in epEvents) total += ev.Chapters.Count;
                if (detailToChallenge.Count == total)
                {
                    IEnumerator<KeyValuePair<int, string>> it = detailToChallenge.GetEnumerator();
                    foreach (EpEvent ev in epEvents) foreach (object[] c in ev.Chapters) { it.MoveNext(); c[1] = it.Current.Key.ToString(); c[2] = it.Current.Value; }
                }
                Log("EVENT M_episode_event_challenge: " + crow.Length + " rows, " + detailToChallenge.Count + " chapters" + (detailToChallenge.Count == total ? " (linked)" : " (not linked - using generated ids)") + "; first row: " + (crow.Length > 0 ? NewJson().Serialize(crow[0]) : "none"));
            }
        }
        catch (Exception ex) { Log("EVENT could not read M_episode_event_challenge: " + ex.Message); }
        int chapters = 0; foreach (EpEvent ev in epEvents) chapters += ev.Chapters.Count;
        Log("EVENT " + epEvents.Count + " episode events, " + chapters + " chapters" + (unlockAllEventEpisodes ? " (all unlocked)" : ""));
    }

    static void FillEpisodeEvents(Dictionary<string, object> data, string onlyEventId)
    {
        HashSet<string> watched = new HashSet<string>();
        object w; if (state.TryGetValue("EventEpisodeWatched", out w) && w is object[]) foreach (object o in (object[])w) watched.Add(Convert.ToString(o));
        List<object> events = new List<object>(), details = new List<object>();
        foreach (EpEvent ev in epEvents)
        {
            Dictionary<string, object> e = new Dictionary<string, object>();
            e["episode_event_id"] = ev.Id.ToString(); e["name"] = ev.Name; e["image_card_id"] = ev.Image;
            e["is_open"] = "true"; e["is_new"] = "false";
            events.Add(e);
            if (onlyEventId != null && onlyEventId != ev.Id.ToString()) continue;
            bool prevWatched = true;
            for (int i = 0; i < ev.Chapters.Count; i++)
            {
                object[] c = ev.Chapters[i];
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["episode_event_detail_id"] = c[1]; d["episode_event_id"] = ev.Id.ToString(); d["episode_event_challenge_id"] = c[2];
                d["index"] = i + 1; d["episode_id"] = c[0];
                bool unlocked = unlockAllEventEpisodes || prevWatched;     // normal mode: next chapter opens after watching the previous one
                d["is_new"] = unlocked && !watched.Contains((string)c[0]) ? "true" : "false";
                d["is_unlock"] = unlocked ? "true" : "false";
                details.Add(d);
                prevWatched = watched.Contains((string)c[0]);
            }
        }
        data["UserEpisodeEventData"] = events.ToArray();
        data["UserEpisodeEventDetailData"] = details.ToArray();
        data["EpisodeEventData"] = new object[0];
        data["UserNextEpisodeEventChallengeData"] = new object[0];
        data["EpisodeEventChallengeDialogData"] = new object[0];
    }

    static void EpisodeFinish(Dictionary<string, object> p)
    {
        string id = S(p, "episode_id", "");
        List<object> wl = new List<object>(state.ContainsKey("EventEpisodeWatched") ? (object[])state["EventEpisodeWatched"] : new object[0]);
        if (!wl.Contains(id)) { wl.Add(id); state["EventEpisodeWatched"] = wl.ToArray(); }
        foreach (object o in (object[])state["EpisodeData"])
        {
            Dictionary<string, object> e = (Dictionary<string, object>)o;
            if (Convert.ToString(e["episode_id"]) == id) e["is_new"] = "false";
        }
        SaveState();
        Log("EPISODE " + id + " watched");
    }

    static void ChangeName(Dictionary<string, object> p)
    {
        string name = S(p, "name", null) ?? S(p, "user_name", null) ?? S(p, "nickname", null);
        if (name == null)
        {
            Log("NAME  change/name request had no recognised name field: " + string.Join(",", new List<string>(p.Keys).ToArray()));
            return;
        }
        Log("NAME  changed to " + name);
        ((Dictionary<string, object>)state["UserStatusData"])["name"] = name;
        SaveState();
    }

    static void StageStart(Dictionary<string, object> p, Dictionary<string, object> data)
    {
        Dictionary<string, object> sd = new Dictionary<string, object>();
        sd["stage_id"] = S(p, "stage_id", "1");
        data["StageStartData"] = new object[] { sd };
    }

    // Updates the save for one finished play. Returns the previous best and whether it was beaten.
    static void ApplyScore(string stageId, long score, long combo, bool clear, bool countPlay, out long oldBest, out bool improved, out long newTotal)
    {
        improved = false;
        object[] rows = (object[])state["StageData"];
        object[] head = (object[])rows[0];
        int iStatus = Array.IndexOf(head, (object)"status"), iCount = Array.IndexOf(head, (object)"clear_count"),
            iScore = Array.IndexOf(head, (object)"best_score"), iCombo = Array.IndexOf(head, (object)"best_combo");
        object[] row = null;
        for (int i = 1; i < rows.Length; i++)
        {
            object[] r = (object[])rows[i];
            if (Convert.ToString(r[0]) == stageId) { row = r; break; }
        }
        if (row == null)
        {
            row = new object[] { stageId, "new", 0, 0, 0, -1, -1, -1, "-1" };
            List<object> nl = new List<object>(rows); nl.Add(row); rows = nl.ToArray(); state["StageData"] = rows;
        }
        oldBest = Convert.ToInt64(row[iScore]);
        if (clear && (countPlay || Convert.ToString(row[iStatus]) != "clear"))
        {
            row[iStatus] = "clear";
            row[iCount] = Convert.ToInt64(row[iCount]) + 1;
            // rank ids are set to fixed placeholder values once a stage is cleared
            row[5] = 123; row[6] = 234; row[7] = 456; row[8] = 789;
        }
        if (score > oldBest) { row[iScore] = score; improved = true; }
        if (combo > Convert.ToInt64(row[iCombo])) row[iCombo] = combo;

        Dictionary<string, object> ts = (Dictionary<string, object>)state["UserTotalScoreData"];
        long oldTotal = Convert.ToInt64(ts["total_score"]);
        newTotal = oldTotal + (improved ? score - oldBest : 0);
        ts["total_score"] = newTotal;
        if (oldBest == 0 && score > 0) ts["played_music_cnt"] = Convert.ToInt64(ts["played_music_cnt"]) + 1;

        string musicId;
        long sidNum;
        // Song comes from M_stage.json; fallback rule (stage_id - 1) / 4 + 1 holds for every stage in that table.
        if (!stageToMusic.TryGetValue(stageId, out musicId) && long.TryParse(stageId, out sidNum) && sidNum > 0)
            musicId = ((sidNum - 1) / 4 + 1).ToString();
        if (musicId != null)
        {
            object[] mh = state.ContainsKey("MonthlyMusicHighScore") ? (object[])state["MonthlyMusicHighScore"] : new object[0];
            bool found = false;
            foreach (object o in mh)
            {
                Dictionary<string, object> d = (Dictionary<string, object>)o;
                if (Convert.ToString(d["music_id"]) == musicId) { found = true; if (Convert.ToInt64(d["score"]) < score) d["score"] = score; }
            }
            if (!found)
            {
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["music_id"] = musicId; d["score"] = score;
                List<object> nl = new List<object>(mh); nl.Add(d); state["MonthlyMusicHighScore"] = nl.ToArray();
            }
        }
    }

    // Every finished play is also appended to save\scores.jsonl, and merged back in at startup,
    // so a best score can never be lost even if state.json is overwritten.
    static void AppendScoreHistory(string stageId, long score, long combo, bool clear)
    {
        Dictionary<string, object> e = new Dictionary<string, object>();
        e["time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        e["stage_id"] = stageId; e["score"] = score; e["max_combo"] = combo; e["clear"] = clear;
        File.AppendAllText(Path.Combine(saveDir, "scores.jsonl"), NewJson().Serialize(e) + "\r\n", Utf8);
    }

    static void MergeScoreHistory()
    {
        string f = Path.Combine(saveDir, "scores.jsonl");
        if (!File.Exists(f)) return;
        int fixedCount = 0;
        foreach (string line in File.ReadAllLines(f, Utf8))
        {
            if (line.Trim().Length == 0) continue;
            try
            {
                Dictionary<string, object> e = (Dictionary<string, object>)NewJson().DeserializeObject(line);
                long ob, nt; bool imp;
                ApplyScore(Convert.ToString(e["stage_id"]), Convert.ToInt64(e["score"]), Convert.ToInt64(e["max_combo"]), (bool)e["clear"], false, out ob, out imp, out nt);
                if (imp) fixedCount++;
            }
            catch { }
        }
        SaveState();
        if (fixedCount > 0) Log("Restored " + fixedCount + " best score(s) from scores.jsonl");
    }

    // Cards of the save's active group (card_id/level/index) and its leader (slot 1).
    static List<object> ActiveDeck(out string leaderId, out object leaderLv)
    {
        List<object> deckList = new List<object>();
        leaderId = "-1"; leaderLv = 1;
        try
        {
            Dictionary<string, object> us = (Dictionary<string, object>)state["UserStatusData"];
            string active = Convert.ToString(us["active_deck_index"]);
            Dictionary<string, Dictionary<string, object>> byUid = new Dictionary<string, Dictionary<string, object>>();
            object[] owned = AllCards.Cards ?? (object[])state["UserHasCardData"];
            foreach (object o in owned) { Dictionary<string, object> c = (Dictionary<string, object>)o; byUid[Convert.ToString(c["user_has_card_id"])] = c; }
            foreach (object o in (object[])state["DeckData"])
            {
                Dictionary<string, object> d = (Dictionary<string, object>)o;
                Dictionary<string, object> c;
                if (Convert.ToString(d["deck_index"]) != active || !byUid.TryGetValue(Convert.ToString(d["user_has_card_id"]), out c)) continue;
                Dictionary<string, object> e = new Dictionary<string, object>();
                e["card_id"] = Convert.ToString(c["card_id"]); e["level"] = c["level"]; e["index"] = d["index"];
                deckList.Add(e);
                if (Convert.ToString(d["index"]) == "1") { leaderId = Convert.ToString(c["card_id"]); leaderLv = c["level"]; }
            }
        }
        catch { }
        return deckList;
    }

    // Total high score ranking: offline there is only one player, so the list is just this save.
    static void TotalScoreRanking(Dictionary<string, object> data)
    {
        Dictionary<string, object> us = (Dictionary<string, object>)state["UserStatusData"];
        Dictionary<string, object> ts = (Dictionary<string, object>)state["UserTotalScoreData"];
        string leaderId; object leaderLv;
        ActiveDeck(out leaderId, out leaderLv);
        Dictionary<string, object> me = new Dictionary<string, object>();
        me["total_score"] = Convert.ToString(ts["total_score"]);
        me["rank"] = 1;
        me["name"] = us["name"];
        me["total_score_grade_id"] = Convert.ToString(ts["total_score_grade_id"]);
        me["leader_card_id"] = leaderId;
        me["leader_card_level"] = leaderLv;
        me["award_id"] = us["award_id"];
        data["TotalScoreRankingList"] = new object[] { me };
        Dictionary<string, object> mine = new Dictionary<string, object>();
        mine["leader_card_id"] = leaderId; mine["leader_card_level"] = leaderLv; mine["rank"] = 1;
        mine["total_score_grade_id"] = Convert.ToString(ts["total_score_grade_id"]);
        mine["disp_user_id"] = us["disp_user_id"]; mine["name"] = us["name"];
        mine["total_score"] = Convert.ToString(ts["total_score"]);
        mine["shortage_score"] = Convert.ToString(ts["shortage_score"]);
        data["UserTotalScoreRankingData"] = mine;
        Dictionary<string, object> fix = new Dictionary<string, object>();
        fix["ranking_fix_time"] = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
        data["TotalScoreRankingData"] = fix;
        data["NextTotalScoreReachRewardData"] = new object[0];
    }

    // Screens with no captured reply. Valid empty replies built from the
    // game's own data classes, so the screens open empty instead of failing.
    // Removes links that would open a web browser (official news pages that no longer exist). The home message
    // ticker (HomeMessageData) is left alone, and the server's own 127.0.0.1 addresses are never touched.
    static bool IsWebLink(object v)
    {
        string t = v as string;
        if (string.IsNullOrEmpty(t)) return false;
        t = t.ToLowerInvariant();
        if (t.Contains("127.0.0.1") || t.Contains("localhost")) return false;
        return t.StartsWith("openurl:") || t.StartsWith("webview:") || t.StartsWith("http://") || t.StartsWith("https://");
    }

    static void StripWebLinks(string ep, object node)
    {
        Dictionary<string, object> d = node as Dictionary<string, object>;
        if (d != null)
        {
            if (d.ContainsKey("HomeBannerData") && d["HomeBannerData"] is object[])
            {
                // keep home banners on screen, but take away the website they open
                foreach (object o in (object[])d["HomeBannerData"])
                {
                    Dictionary<string, object> bd = o as Dictionary<string, object>;
                    if (bd != null && bd.ContainsKey("jump_to_url") && IsWebLink(bd["jump_to_url"])) bd["jump_to_url"] = "";
                }
            }
            foreach (string k in new List<string>(d.Keys))
            {
                if (k == "HomeMessageData") continue;
                object v = d[k];
                if ((k == "url" || k.EndsWith("_url")) && IsWebLink(v)) d[k] = "";
                else if (v is Dictionary<string, object> || v is object[] || v is ArrayList) StripWebLinks(ep, v);
            }
            return;
        }
        IEnumerable list = node as IEnumerable;
        if (list != null && !(node is string)) foreach (object o in list) StripWebLinks(ep, o);
    }

    // ------------------------------------------------------------------ member (card) sell / fusion / evolution
    static Dictionary<string, object[]> cardTables = new Dictionary<string, object[]>();
    static object[] CardTable(string name)
    {
        object[] t;
        if (cardTables.TryGetValue(name, out t)) return t;
        try { t = AllCards.Table(CdnFilesDir, name); }
        catch (Exception ex) { Log("CARDS could not read " + name + ": " + ex.Message); t = new object[0]; }
        cardTables[name] = t;
        return t;
    }
    static Dictionary<string, object> FindRow(string table, string key, long value)
    {
        foreach (object o in CardTable(table))
        {
            Dictionary<string, object> d = o as Dictionary<string, object>;
            if (d != null && d.ContainsKey(key) && Convert.ToInt64(d[key], CultureInfo.InvariantCulture) == value) return d;
        }
        return null;
    }
    static long RowL(Dictionary<string, object> d, string k, long def)
    {
        object v; return d != null && d.TryGetValue(k, out v) && v != null ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : def;
    }
    // value from a table keyed by (type column, level): the row for this level, or the closest lower level
    static long ByTypeLevel(string table, string typeCol, long type, long level, string valueCol)
    {
        Dictionary<string, object> best = null; long bestLv = long.MinValue;
        foreach (object o in CardTable(table))
        {
            Dictionary<string, object> d = o as Dictionary<string, object>;
            if (d == null || RowL(d, typeCol, -1) != type) continue;
            long lv = RowL(d, "level", -1);
            if (lv <= level && lv > bestLv) { best = d; bestLv = lv; }
        }
        return RowL(best, valueCol, 0);
    }

    // the card list the game currently sees (unlock-all list, or the save's own card box)
    static List<object> CardBox() { return new List<object>(AllCards.Cards ?? (object[])state["UserHasCardData"]); }
    static void SaveCardBox(List<object> box)
    {
        if (AllCards.Cards != null) AllCards.Cards = box.ToArray();   // unlock-all mode: lasts until the server restarts
        else state["UserHasCardData"] = box.ToArray();
        ((Dictionary<string, object>)state["UserStatusData"])["user_has_card_count"] = box.Count;
        SaveState();
    }
    static Dictionary<string, object> FindCard(List<object> box, string uid)
    {
        foreach (object o in box)
        {
            Dictionary<string, object> d = o as Dictionary<string, object>;
            if (d != null && Convert.ToString(d["user_has_card_id"]) == uid) return d;
        }
        return null;
    }
    static string[] IdList(Dictionary<string, object> p, string k)
    {
        object v; if (!p.TryGetValue(k, out v) || v == null) return new string[0];
        List<string> r = new List<string>();
        if (v is object[]) { foreach (object o in (object[])v) r.Add(Convert.ToString(o)); }
        else foreach (string x in Convert.ToString(v).Trim('[', ']').Split(',')) { string t = x.Trim().Trim('"'); if (t.Length > 0) r.Add(t); }
        return r.ToArray();
    }
    // the reply's UserStatusData was filled before the change, so copy the new coins / member count into it
    static void SyncStatus(Dictionary<string, object> data)
    {
        Dictionary<string, object> us = Get(data, "UserStatusData"), cur = (Dictionary<string, object>)state["UserStatusData"];
        if (us == null) return;
        us["money"] = cur["money"]; us["user_has_card_count"] = cur["user_has_card_count"];
    }
    static long CardL(Dictionary<string, object> c, string k) { object v; return c.TryGetValue(k, out v) && v != null ? Convert.ToInt64(Convert.ToDecimal(v, CultureInfo.InvariantCulture)) : 0; }
    static long AddMoney(long delta)
    {
        Dictionary<string, object> us = (Dictionary<string, object>)state["UserStatusData"];
        long m = FixedCoins;   // coins stay fixed offline
        us["money"] = m;
        return m;
    }

    static void CardSell(Dictionary<string, object> p, Dictionary<string, object> data)
    {
        List<object> box = CardBox();
        List<object> result = new List<object>();
        long total = 0;
        foreach (string uid in IdList(p, "user_has_card_id"))
        {
            Dictionary<string, object> c = FindCard(box, uid);
            if (c == null) { Log("CARDS sell: member " + uid + " not in the card box"); continue; }
            long cardId = CardL(c, "card_id"), level = Math.Max(1, CardL(c, "level"));
            Dictionary<string, object> st = FindRow("M_card_status", "id", cardId);
            Dictionary<string, object> mc = FindRow("M_card", "id", cardId);
            long money = ByTypeLevel("M_card_price", "card_price_type_id", RowL(st, "card_price_type_id", -1), level, "sell_money");
            long medal = RowL(FindRow("M_rarity", "id", RowL(mc, "rarity_id", -1)), "sell_medal", 0);
            box.Remove(c);
            total += money;
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["user_has_card_id"] = uid; r["money"] = money; r["medal"] = medal;
            result.Add(r);
        }
        long now = AddMoney(total);
        SaveCardBox(box);
        data["SellResultData"] = result.ToArray(); SyncStatus(data);
        Log("CARDS sold " + result.Count + " member(s) for " + total + " coins (now " + now + ")");
    }

    // level for a card's total exp, capped at the card's max level; returns {level, capped exp}
    static long[] LevelForExp(long cardId, long exp)
    {
        long type = RowL(FindRow("M_card_status", "id", cardId), "card_level_type_id", -1);
        long level = 1, maxLv = 1, maxStart = 0;
        foreach (object o in CardTable("M_card_level"))
        {
            Dictionary<string, object> d = o as Dictionary<string, object>;
            if (d == null || RowL(d, "card_level_type_id", -1) != type) continue;
            long lv = RowL(d, "level", 1), st = RowL(d, "total_exp_start", 0);
            if (lv > maxLv) { maxLv = lv; maxStart = st; }
            if (exp >= st && lv > level) level = lv;
        }
        if (level >= maxLv && maxLv > 1) { level = maxLv; exp = Math.Min(exp, maxStart); }
        return new long[] { level, exp, maxLv };
    }

    static void CardFusion(Dictionary<string, object> p, Dictionary<string, object> data)
    {
        List<object> box = CardBox();
        string baseId = S(p, "user_has_card_id", "");
        Dictionary<string, object> b = FindCard(box, baseId);
        List<object> used = new List<object>();
        if (b == null) { Log("CARDS fusion: base member " + baseId + " not in the card box"); data["FusionMaterialData"] = new object[0]; return; }
        long cardId = CardL(b, "card_id");
        Dictionary<string, object> mc = FindRow("M_card", "id", cardId);
        long beforeLv = Math.Max(1, CardL(b, "level")), beforeExp = CardL(b, "total_exp"), beforeSkill = Math.Max(1, CardL(b, "skill_level"));
        object bp = b["hit_perfect_num"], bg = b["hit_great_num"], bd = b["drop_ratio"];
        long gain = 0, cost = 0, skillUps = 0;
        foreach (string uid in IdList(p, "material_user_has_card_id"))
        {
            Dictionary<string, object> m = FindCard(box, uid);
            if (m == null || m == b) continue;
            long mid = CardL(m, "card_id"), mlv = Math.Max(1, CardL(m, "level"));
            Dictionary<string, object> mst = FindRow("M_card_status", "id", mid);
            gain += ByTypeLevel("M_card_material_exp", "card_material_exp_type_id", RowL(mst, "card_material_exp_type_id", -1), mlv, "add_exp");
            cost += RowL(FindRow("M_card_fusion", "level", beforeLv), "consume_money", 0);
            // a material with the same skill can raise the skill level (chance from M_skill_level_up)
            Dictionary<string, object> mm = FindRow("M_card", "id", mid);
            if (mm != null && mc != null && RowL(mm, "skill_id", -1) == RowL(mc, "skill_id", -2))
            {
                long ratio = 0;
                foreach (object o in CardTable("M_skill_level_up"))
                {
                    Dictionary<string, object> d = o as Dictionary<string, object>;
                    if (d != null && RowL(d, "rarity_id", -1) == RowL(mc, "rarity_id", -2) && RowL(d, "material_rarity_id", -1) == RowL(mm, "rarity_id", -3)) ratio = RowL(d, "success_ratio", 0);
                }
                if (rng.Next(100) < ratio) skillUps++;
            }
            box.Remove(m);
            Dictionary<string, object> u = new Dictionary<string, object>(); u["user_has_card_id"] = uid; used.Add(u);
        }
        long[] lv = LevelForExp(cardId, beforeExp + gain);
        long maxSkill = 1;
        foreach (object o in CardTable("M_skill_param"))
        {
            Dictionary<string, object> d = o as Dictionary<string, object>;
            if (d == null || mc == null || RowL(d, "skill_id", -1) != RowL(mc, "skill_id", -2)) continue;
            long sl = d.ContainsKey("skill_level") ? RowL(d, "skill_level", 1) : RowL(d, "level", 1);
            if (sl > maxSkill) maxSkill = sl;
        }
        long skill = Math.Min(Math.Max(maxSkill, beforeSkill), beforeSkill + skillUps);
        b["level"] = lv[0]; b["total_exp"] = lv[1]; b["skill_level"] = skill;
        // PERFECT/GREAT counts and drop rate grow from the card's default to its max value as it levels up
        if (mc != null && lv[2] > 1)
        {
            double f = (double)(lv[0] - 1) / (lv[2] - 1);
            b["hit_perfect_num"] = (long)Math.Round(RowL(mc, "default_hit_perfect_num", 0) + (RowL(mc, "max_hit_perfect_num", 0) - RowL(mc, "default_hit_perfect_num", 0)) * f);
            b["hit_great_num"] = (long)Math.Round(RowL(mc, "default_hit_great_num", 0) + (RowL(mc, "max_hit_great_num", 0) - RowL(mc, "default_hit_great_num", 0)) * f);
            decimal d0 = Convert.ToDecimal(mc["default_drop_ratio"], CultureInfo.InvariantCulture), d1 = Convert.ToDecimal(mc["max_drop_ratio"], CultureInfo.InvariantCulture);
            b["drop_ratio"] = Math.Round(d0 + (d1 - d0) * (decimal)f, 2);
        }
        b["is_new"] = "false";
        long now = AddMoney(-cost);
        SaveCardBox(box);

        Dictionary<string, object> r = new Dictionary<string, object>();
        r["success"] = "true";
        foreach (string k in new string[] { "user_has_card_id", "card_id", "level", "total_exp", "skill_level", "hit_perfect_num", "hit_great_num", "drop_ratio", "is_protected", "is_new" }) r[k] = b[k];
        r["before_level"] = beforeLv; r["before_total_exp"] = beforeExp; r["before_skill_level"] = beforeSkill;
        r["before_hit_perfect_num"] = bp; r["before_hit_great_num"] = bg; r["before_drop_ratio"] = bd;
        data["FusionResultData"] = r;
        data["FusionMaterialData"] = used.ToArray(); SyncStatus(data);
        Log("CARDS fusion: member " + baseId + " +" + gain + " exp from " + used.Count + " member(s), level " + beforeLv + " -> " + lv[0] + (skill > beforeSkill ? ", skill Lv " + beforeSkill + " -> " + skill : "") + ", " + cost + " coins (now " + now + ")");
    }

    static void CardEvolution(Dictionary<string, object> p, Dictionary<string, object> data)
    {
        List<object> box = CardBox();
        string uid = S(p, "user_has_card_id", "");
        Dictionary<string, object> c = FindCard(box, uid);
        data["EvolutionMaterialData"] = new object[0];
        if (c == null) { Log("CARDS evolution: member " + uid + " not in the card box"); return; }
        long before = CardL(c, "card_id");
        Dictionary<string, object> ev = FindRow("M_card_evolution", "card_id", before);
        if (ev == null) { Log("CARDS evolution: no evolution for card " + before); return; }
        long after = RowL(ev, "after_card_id", before);
        long now = AddMoney(-RowL(ev, "consume_money", 0));
        c["card_id"] = after.ToString();
        long[] lv = LevelForExp(after, CardL(c, "total_exp"));
        c["level"] = lv[0]; c["total_exp"] = lv[1]; c["is_new"] = "false";
        SaveCardBox(box);
        Dictionary<string, object> r = new Dictionary<string, object>();
        foreach (string k in new string[] { "user_has_card_id", "card_id", "level", "total_exp", "skill_level", "hit_perfect_num", "hit_great_num", "drop_ratio", "is_protected", "is_new" }) r[k] = c[k];
        r["before_card_id"] = before.ToString();
        data["EvolutionResultData"] = r; SyncStatus(data);
        Log("CARDS evolution: member " + uid + " card " + before + " -> " + after + " (coins now " + now + ")");
    }

    // Helpers shown before a song (get/friend/follow/list): fixed max-rank helpers from data\static\helpers.json.
    // Each has three member slots (follow_type_id 1-3) at max level, max skill level and max PERFECT/GREAT/drop
    // stats, and each slot member gets a full set of assist members (the strongest-rarity members of the same
    // colour, at max level). The number of assist slots comes from M_card_assist_param.
    static object[] helperUsers, helperGroups;
    static bool FillHelpers(Dictionary<string, object> data)
    {
        if (helperUsers == null)
        {
            List<object> users = new List<object>(), groups = new List<object>();
            try
            {
                string hf = Path.Combine(Path.Combine(dataDir, "static"), "helpers.json");
                long topRank = 300;
                if (rankOverride != null && rankOverride.ContainsKey("level")) topRank = Convert.ToInt64(rankOverride["level"]);
                // max-level member entries from the unlock-all list, by card id
                Dictionary<long, Dictionary<string, object>> owned = new Dictionary<long, Dictionary<string, object>>();
                if (AllCards.Cards != null)
                    foreach (object c in AllCards.Cards) { Dictionary<string, object> d = (Dictionary<string, object>)c; owned[Convert.ToInt64(d["card_id"])] = d; }
                Dictionary<long, Dictionary<string, object>> master = new Dictionary<long, Dictionary<string, object>>();
                foreach (object o in CardTable("M_card")) { Dictionary<string, object> d = o as Dictionary<string, object>; if (d != null) master[RowL(d, "id", -1)] = d; }
                Dictionary<long, long> raritySort = new Dictionary<long, long>();
                foreach (object o in CardTable("M_rarity")) { Dictionary<string, object> d = o as Dictionary<string, object>; if (d != null) raritySort[RowL(d, "id", -1)] = RowL(d, "sort_order", 0); }
                Dictionary<long, long> assistNum = new Dictionary<long, long>();
                foreach (object o in CardTable("M_card_assist_param")) { Dictionary<string, object> d = o as Dictionary<string, object>; if (d != null) assistNum[RowL(d, "card_id", -1)] = RowL(d, "default_assist_num", 0); }
                // assist candidates per colour: strongest rarity first, newest first
                List<long> pool = new List<long>(owned.Keys);
                pool.RemoveAll(id => !master.ContainsKey(id));
                pool.Sort((x, y) =>
                {
                    long rx, ry; raritySort.TryGetValue(RowL(master[x], "rarity_id", -1), out rx); raritySort.TryGetValue(RowL(master[y], "rarity_id", -1), out ry);
                    return rx != ry ? ry.CompareTo(rx) : y.CompareTo(x);
                });

                foreach (object o in (object[])NewJson().DeserializeObject(File.ReadAllText(hf, Utf8)))
                {
                    Dictionary<string, object> h = (Dictionary<string, object>)o;
                    List<long> slotCards = new List<long>();
                    if (h.ContainsKey("cards")) foreach (object c in (object[])h["cards"]) slotCards.Add(Convert.ToInt64(c));
                    else if (h.ContainsKey("card_id")) slotCards.Add(Convert.ToInt64(h["card_id"]));
                    slotCards.RemoveAll(id => !owned.ContainsKey(id));
                    if (slotCards.Count == 0) { Log("HELPER no usable member for " + h["name"] + " - skipped"); continue; }
                    string uid = Convert.ToString(h["disp_user_id"]);
                    Dictionary<string, object> u = new Dictionary<string, object>();
                    u["user_id"] = uid; u["disp_user_id"] = uid; u["name"] = h["name"]; u["level"] = topRank; u["award_id"] = "1";
                    u["is_friend"] = "true"; u["is_new"] = "false"; u["login_at"] = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds; u["friend_point"] = 0;
                    users.Add(u);

                    HashSet<long> usedChars = new HashSet<long>();
                    foreach (long id in slotCards) usedChars.Add(RowL(master.ContainsKey(id) ? master[id] : null, "character_id", -id));
                    List<object> slots = new List<object>();
                    List<string> logParts = new List<string>();
                    for (int t = 1; t <= 3; t++)
                    {
                        Dictionary<string, object> s = new Dictionary<string, object>();
                        s["follow_type_id"] = t.ToString();
                        long cid = slotCards[(t - 1) % slotCards.Count];
                        Dictionary<string, object> card = owned[cid];
                        s["card_id"] = cid.ToString(); s["card_level"] = card["level"]; s["card_skill_level"] = card["skill_level"];
                        s["card_hit_perfect_num"] = card["hit_perfect_num"]; s["card_hit_great_num"] = card["hit_great_num"]; s["card_drop_ratio"] = card["drop_ratio"];
                        // assist members: same colour as this slot's member, different characters
                        long n; if (!assistNum.TryGetValue(cid, out n)) n = 0;
                        long attr = RowL(master.ContainsKey(cid) ? master[cid] : null, "attr_id", -1);
                        List<object> assists = new List<object>();
                        foreach (long a in pool)
                        {
                            if (assists.Count >= n) break;
                            Dictionary<string, object> m = master[a];
                            if (RowL(m, "attr_id", -2) != attr || RowL(m, "card_type_id", 1) != 1) continue;
                            long ch = RowL(m, "character_id", -a);
                            if (usedChars.Contains(ch)) continue;
                            usedChars.Add(ch);
                            Dictionary<string, object> ad = new Dictionary<string, object>();
                            ad["assist_index"] = assists.Count + 1; ad["card_id"] = a.ToString(); ad["card_level"] = owned[a]["level"];
                            assists.Add(ad);
                        }
                        s["FriendDeckAssistData"] = assists.ToArray();
                        slots.Add(s);
                        logParts.Add("slot " + t + " card " + cid + " (Lv " + card["level"] + ", skill Lv " + card["skill_level"] + ", " + assists.Count + " assists)");
                    }
                    Dictionary<string, object> g = new Dictionary<string, object>();
                    g["disp_user_id"] = uid; g["FollowCardList"] = slots.ToArray();
                    groups.Add(g);
                    Log("HELPER " + h["name"] + ": " + string.Join("; ", logParts.ToArray()));
                }
            }
            catch (Exception ex) { Log("HELPER could not build the helpers: " + ex.Message); }
            if (users.Count == 0) return false;
            helperUsers = users.ToArray(); helperGroups = groups.ToArray();
        }
        data["FollowUserList"] = helperUsers;
        data["FollowGroup"] = helperGroups;
        return true;
    }

    static string GachaRateText()
    {
        if (AllCards.Pool == null || AllCards.Pool.Count == 0) return "";
        SortedDictionary<int, int> byRarity = new SortedDictionary<int, int>();
        foreach (Dictionary<string, object> c in AllCards.Pool)
        {
            int r = Convert.ToInt32(c["rarity_id"]); int n; byRarity.TryGetValue(r, out n); byRarity[r] = n + 1;
        }
        Dictionary<int, string> rname = new Dictionary<int, string>();
        try
        {
            string rp = Path.Combine(CdnFilesDir ?? "", "M_rarity.json");
            if (File.Exists(rp))
                foreach (object o in (object[])NewJson().DeserializeObject(ReadTable("M_rarity", rp)))
                {
                    Dictionary<string, object> r = o as Dictionary<string, object>;
                    if (r != null && r.ContainsKey("id") && r.ContainsKey("name")) rname[Convert.ToInt32(r["id"])] = Convert.ToString(r["name"]);
                }
        }
        catch { }
        StringBuilder sb = new StringBuilder();
        sb.Append("全" + AllCards.Pool.Count + "種のメンバーから均等に排出されます。\n");
        foreach (KeyValuePair<int, int> kv in byRarity)
            sb.Append((rname.ContainsKey(kv.Key) ? rname[kv.Key] : "レアリティ " + kv.Key) + " : " + (100.0 * kv.Value / AllCards.Pool.Count).ToString("0.00", CultureInfo.InvariantCulture) + "%\n");
        return sb.ToString().TrimEnd('\n');
    }

    static bool EmptyReply(string ep, Dictionary<string, object> p, Dictionary<string, object> data)
    {
        switch (ep)
        {
            case "get/friend/list": data["FriendListData"] = new object[0]; return true;
            case "get/friend/accept/wait/list": data["FriendAcceptWaitListData"] = new object[0]; return true;
            case "get/friend/request/list": data["FriendRequestListData"] = new object[0]; return true;
            case "get/search/friend/list": data["FriendListData"] = new object[0]; data["FollowUserList"] = new object[0]; return true;
            case "get/friend/point/shop/data":
                {
                    Dictionary<string, object> fp = new Dictionary<string, object>();
                    fp["friend_point"] = 0; fp["shop_text"] = "";
                    data["FriendPointData"] = fp; data["FriendPointShopListData"] = new object[0]; return true;
                }
            case "get/present/list": data["PresentData"] = new object[0]; return true;
            case "get/trophy/list": data["UserTropyMissionData"] = new object[0]; return true;
            case "get/total/score/reach/reward/list": data["TotalScoreReachRewardData"] = new object[0]; return true;
            case "get/total/score/grade/reward/list": data["TotalScoreGradeRewardData"] = new object[0]; return true;
            case "get/notice": data["NoticeData"] = new object[0]; return true;
            case "get/notice/detail":
                {
                    Dictionary<string, object> nd = new Dictionary<string, object>(); nd["html"] = "";
                    data["NoticeDetailData"] = nd; return true;
                }
            case "get/gacha/lottery/data":
                {
                    // rates dialog: offline every pull picks uniformly from all deck-usable cards
                    Dictionary<string, object> tab = new Dictionary<string, object>();
                    tab["tab_name"] = "提供割合";
                    tab["title"] = "オフラインガチャ";
                    tab["detail"] = GachaRateText();
                    tab["GachaLotteryDetailData"] = new object[0];
                    data["GachaLotteryData"] = new object[] { tab };
                    data["GachaLotteryDetailData"] = new object[0];
                    return true;
                }
            case "extend/max/friend":
                {
                    Dictionary<string, object> us = (Dictionary<string, object>)state["UserStatusData"];
                    int cur = Convert.ToInt32(us["max_friend_count"]);
                    us["max_friend_count"] = Math.Min(100, cur + 5);
                    SaveState();
                    Log("FRIEND friend limit " + cur + " -> " + us["max_friend_count"]);
                    return true;
                }
            case "stage/continue":
                // continue after failing a song (free offline - M_const sets the continue cost to 0); reply is just the user blocks
                Log("STAGE continue on stage " + S(p, "stage_id", "?"));
                return true;
            case "card/sell": CardSell(p, data); return true;
            case "card/fusion": CardFusion(p, data); return true;
            case "card/evolution": CardEvolution(p, data); return true;
            case "extend/max/storage":
                {
                    Dictionary<string, object> us = (Dictionary<string, object>)state["UserStatusData"];
                    int cur = Convert.ToInt32(us["card_storage_count"]);
                    us["card_storage_count"] = Math.Min(3000, cur + 5);
                    SaveState();
                    Log("CARDS card box limit " + cur + " -> " + us["card_storage_count"]);
                    return true;
                }
            case "get/shop/data":
                {
                    Dictionary<string, object> sd = new Dictionary<string, object>();
                    sd["shop_id"] = S(p, "shop_id", "1"); sd["name"] = ""; sd["end_at"] = -1; sd["show_item_id"] = "-1";
                    sd["ShopRewardList"] = new object[0];
                    data["ShopData"] = sd; data["FeaturedShopRewardList"] = new object[0]; return true;
                }
        }
        return false;
    }

    static void StageFinish(Dictionary<string, object> p, Dictionary<string, object> data)
    {
        string stageId = S(p, "stage_id", "");
        long score = L(p, "score"), combo = L(p, "max_combo_count");
        bool clear = S(p, "is_clear", "false") == "true";
        long oldBest = 0;
        bool improved = false;

        long newTotal;
        ApplyScore(stageId, score, combo, clear, true, out oldBest, out improved, out newTotal);
        AppendScoreHistory(stageId, score, combo, clear);
        SaveState();

        long now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
        Dictionary<string, object> us = (Dictionary<string, object>)state["UserStatusData"];
        Dictionary<string, object> rank = Get(data, "UserStageRankingData");
        if (rank != null)
        {
            rank["stage_id"] = Convert.ToInt64(stageId);
            rank["score"] = score;
            rank["acquire_at"] = now;
            rank["card_id_list"] = "";
            rank["card_level_list"] = "";
            rank["name"] = us["name"];
            rank["level"] = us["level"];
            rank["disp_user_id"] = us["disp_user_id"];
            foreach (string k in new string[] { "base_score", "just_perfect_hit_count", "perfect_hit_count", "great_hit_count", "good_hit_count", "bad_hit_count" })
                rank[k] = L(p, k);
        }
        // leader card and deck shown on the result/ranking: taken from the save's active group
        string leaderId; object leaderLv;
        List<object> deckList = ActiveDeck(out leaderId, out leaderLv);
        if (rank != null) { rank["leader_card_id"] = leaderId; rank["leader_card_level"] = leaderLv; rank["award_id"] = us["award_id"]; }
        Dictionary<string, object> rec = Get(data, "UserRankingRecordData");
        if (rec != null) rec["RankingRecordDeck"] = deckList.ToArray();
        if (rec != null)
            foreach (string k in new string[] { "base_score", "just_perfect_hit_count", "perfect_hit_count", "great_hit_count", "good_hit_count", "bad_hit_count" })
                rec[k] = L(p, k);
        Dictionary<string, object> fin = Get(data, "StageFinishData");
        if (fin != null)
        {
            fin["stage_id"] = stageId;
            fin["score"] = score;
            fin["is_best_core"] = improved ? "true" : "false";
        }
        Dictionary<string, object> tot = Get(data, "StageFinishedUserTotalScoreData");
        if (tot != null) tot["new_total_score"] = newTotal;

        Log("STAGE " + stageId + " finished: score " + score + (improved ? " (new best, was " + oldBest + ")" : " (best " + oldBest + ")") + (clear ? " CLEAR" : " failed"));
    }
}
