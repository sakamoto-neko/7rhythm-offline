"""Extracts the HTTP requests and replies from a packet capture into pairs.json (needs tshark).

usage: python pairs.py capture.pcapng [out.json]
Prints one line per request/reply; pairs.json is used by replay.py and show.py.
"""
import json
import os
import subprocess
import sys

cap = sys.argv[1]
out = sys.argv[2] if len(sys.argv) > 2 else os.path.join(os.path.dirname(os.path.abspath(cap)), "pairs.json")
res = subprocess.run(["tshark", "-r", cap, "-Y", "http", "-T", "fields", "-E", "separator=\t",
                      "-e", "tcp.stream", "-e", "http.request.uri", "-e", "http.response.code", "-e", "http.file_data"],
                     capture_output=True, text=True, check=True).stdout
rows = []
for line in res.splitlines():
    stream, uri, code, data = (line.split("\t") + [""] * 4)[:4]
    body = bytes.fromhex(data.replace(":", "")) if data else b""
    rows.append((stream, uri, code, body))
json.dump([(s, u, c, b.decode("latin1")) for s, u, c, b in rows], open(out, "w"))
for i, (s, u, c, b) in enumerate(rows):
    print(i, s, u or "RESP " + c, len(b), b[:120])
print("saved", out)
