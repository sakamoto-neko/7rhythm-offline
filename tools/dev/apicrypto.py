"""Shared helpers for the 7RHYTHM game API encoding.

Requests are form posts: request_id=<id>&param=<base64url(AES-128-CBC(JSON))>, zero key and IV, PKCS7 padding.
Replies are a JSON envelope, or the same envelope AES-encrypted and base64-encoded for some endpoints.
"""
import base64
import json

from cryptography.hazmat.primitives import padding
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes

ZERO = b"\0" * 16


def _cipher():
    return Cipher(algorithms.AES(ZERO), modes.CBC(ZERO))


def encrypt_param(obj):
    raw = json.dumps(obj, ensure_ascii=False).encode()
    p = padding.PKCS7(128).padder()
    raw = p.update(raw) + p.finalize()
    e = _cipher().encryptor()
    return base64.urlsafe_b64encode(e.update(raw) + e.finalize()).decode()


def decrypt_bytes(data):
    d = _cipher().decryptor()
    r = d.update(data) + d.finalize()
    return r[:-r[-1]] if 0 < r[-1] <= 16 else r.rstrip(b"\0")


def b64any(s):
    s = s.strip().replace("-", "+").replace("_", "/")
    return base64.b64decode(s + "=" * (-len(s) % 4))


def decode_param(param):
    return json.loads(decrypt_bytes(b64any(param)).decode())


def decode_reply(text):
    """Returns the reply envelope as a dict (plain or encrypted reply)."""
    text = text.strip()
    if text.startswith("{"):
        return json.loads(text)
    return json.loads(decrypt_bytes(base64.b64decode(text)).decode())
