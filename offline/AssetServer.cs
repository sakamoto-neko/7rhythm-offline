// Built-in asset server: serves cdn\Files on 127.0.0.1:45557 (http://127.0.0.1:45557/Files/<path>), the address the
// game downloads songs and master tables from, and logs every file the game asks for that is missing.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

public static class AssetServer
{
    static string root;
    static int served, missing;
    static readonly object countLock = new object();

    public static void Start(string cdnFilesDir, int port)
    {
        root = Path.GetFullPath(cdnFilesDir).TrimEnd('\\', '/');
        TcpListener l = new TcpListener(IPAddress.Loopback, port);
        try { l.Start(); }
        catch (SocketException)
        {
            OfflineServer.Log("ASSET port " + port + " is already in use (is another asset server still running?) - built-in asset server not started");
            return;
        }
        Thread t = new Thread(delegate ()
        {
            while (true)
            {
                TcpClient c = l.AcceptTcpClient();
                Thread h = new Thread(delegate () { Handle(c); });
                h.IsBackground = true; h.Start();
            }
        });
        t.IsBackground = true; t.Start();
        OfflineServer.Log("ASSET built-in asset server on 127.0.0.1:" + port + " serving " + root);
    }

    static string ReadLine(Stream s)
    {
        StringBuilder sb = new StringBuilder();
        int b;
        while ((b = s.ReadByte()) >= 0) { if (b == '\n') break; if (b != '\r') sb.Append((char)b); }
        if (b < 0 && sb.Length == 0) return null;
        return sb.ToString();
    }

    static void Handle(TcpClient c)
    {
        try
        {
            using (c)
            {
                Stream s = new BufferedStream(c.GetStream());
                while (true)
                {
                    string req = ReadLine(s);
                    if (req == null) return;
                    if (req.Length == 0) continue;
                    string[] parts = req.Split(' ');
                    bool close = false; long rangeStart = -1, rangeEnd = -1; int contentLength = 0;
                    string h;
                    while ((h = ReadLine(s)) != null && h.Length > 0)
                    {
                        string lower = h.ToLowerInvariant();
                        if (lower.StartsWith("connection:") && lower.Contains("close")) close = true;
                        if (lower.StartsWith("content-length:")) int.TryParse(h.Substring(15).Trim(), out contentLength);
                        if (lower.StartsWith("range:") && lower.Contains("bytes="))
                        {
                            string r = lower.Substring(lower.IndexOf("bytes=") + 6).Trim();
                            string[] se = r.Split('-');
                            long.TryParse(se[0], out rangeStart);
                            if (se.Length > 1 && se[1].Length > 0) long.TryParse(se[1], out rangeEnd);
                        }
                    }
                    if (contentLength > 0) { byte[] skip = new byte[contentLength]; int off = 0; while (off < contentLength) { int n = s.Read(skip, off, contentLength - off); if (n <= 0) break; off += n; } }
                    if (parts.Length < 2) return;
                    bool head = parts[0] == "HEAD";
                    string url = parts[1];
                    int q = url.IndexOf('?'); if (q >= 0) url = url.Substring(0, q);
                    string rel = Uri.UnescapeDataString(url).TrimStart('/');
                    if (rel.StartsWith("Files/", StringComparison.OrdinalIgnoreCase)) rel = rel.Substring(6);
                    string path = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
                    if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                    {
                        lock (countLock) missing++;
                        OfflineServer.Log("ASSET missing: " + url);
                        byte[] body = Encoding.ASCII.GetBytes("<html><head><title>Not found</title></head><body><h2>Not found</h2></body></html>");
                        string hdr = "HTTP/1.1 404 Not Found\r\nContent-Type: text/html\r\nContent-Length: " + body.Length + "\r\nConnection: keep-alive\r\n\r\n";
                        byte[] hb = Encoding.ASCII.GetBytes(hdr);
                        s.Write(hb, 0, hb.Length); if (!head) s.Write(body, 0, body.Length); s.Flush();
                    }
                    else
                    {
                        using (FileStream f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            long len = f.Length, start = 0, end = len - 1; bool partial = false;
                            if (rangeStart >= 0 && rangeStart < len) { start = rangeStart; if (rangeEnd >= start && rangeEnd < len) end = rangeEnd; partial = true; }
                            long count = len == 0 ? 0 : end - start + 1;
                            string hdr = (partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n") +
                                "Content-Type: application/octet-stream\r\nContent-Length: " + count + "\r\n" +
                                (partial ? "Content-Range: bytes " + start + "-" + end + "/" + len + "\r\n" : "") +
                                "Accept-Ranges: bytes\r\nAccess-Control-Allow-Origin: *\r\nConnection: keep-alive\r\n\r\n";
                            byte[] hb = Encoding.ASCII.GetBytes(hdr);
                            s.Write(hb, 0, hb.Length);
                            if (!head && count > 0)
                            {
                                f.Seek(start, SeekOrigin.Begin);
                                byte[] buf = new byte[81920]; long left = count;
                                while (left > 0) { int n = f.Read(buf, 0, (int)Math.Min(buf.Length, left)); if (n <= 0) break; s.Write(buf, 0, n); left -= n; }
                            }
                            s.Flush();
                        }
                        int total; lock (countLock) { served++; total = served; }
                        if (total % 500 == 0) OfflineServer.Log("ASSET " + total + " files served so far (" + missing + " missing)");
                    }
                    if (close) return;
                }
            }
        }
        catch (IOException) { }
        catch (Exception ex) { OfflineServer.Log("ASSET error: " + ex.Message); }
    }
}
