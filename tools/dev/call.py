"""Sends one request to the running offline server and prints the decoded reply.

usage: python call.py <endpoint> ['{"json": "params"}'] [http://127.0.0.1:45558]
example: python call.py get/friend/follow/list
"""
import json
import sys
import urllib.parse
import urllib.request

from apicrypto import decode_reply, encrypt_param

ep = sys.argv[1]
params = json.loads(sys.argv[2]) if len(sys.argv) > 2 else {}
base = sys.argv[3] if len(sys.argv) > 3 else "http://127.0.0.1:45558"
body = urllib.parse.urlencode({"request_id": "1", "param": encrypt_param(params)}).encode()
text = urllib.request.urlopen(urllib.request.Request(base + "/7r/" + ep, data=body)).read().decode()
print(json.dumps(decode_reply(text), ensure_ascii=False, indent=1))
