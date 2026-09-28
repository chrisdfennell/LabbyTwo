#!/usr/bin/env python3
"""Decrypt a LabbyTwo off-site backup (.l2backup) into the zip it holds.

    pip install cryptography
    python3 decrypt-backup.py labbytwo-2026-09-28.l2backup      # asks for the passphrase
    # -> labbytwo-2026-09-28.zip, holding labbytwo.db and keys/

The same thing Settings -> Off-site copies -> "Open an encrypted backup" does, for when there
is no LabbyTwo running to do it. The format is described in Services/Offsite/BackupBundle.cs:

    header, 56 bytes:  "LABBYBK1" | kdf=1 | iterations u32 | salt 16 | nonce prefix 7
                       | chunk size u32 | key check 16          (integers big-endian)
    then chunks:       AES-256-GCM(chunk) + 16-byte tag, aad = the header,
                       nonce = prefix | chunk index u32 | 1 if last chunk else 0
    key:               PBKDF2-HMAC-SHA256(passphrase as UTF-8, salt, iterations, 32 bytes)
"""
import getpass
import hashlib
import hmac
import struct
import sys

from cryptography.hazmat.primitives.ciphers.aead import AESGCM


def main():
    if len(sys.argv) not in (2, 3):
        sys.exit("usage: decrypt-backup.py BACKUP.l2backup [OUTPUT.zip]")
    source = sys.argv[1]
    target = sys.argv[2] if len(sys.argv) == 3 else source.rsplit(".", 1)[0] + ".zip"
    passphrase = getpass.getpass("Passphrase: ").encode("utf-8")

    with open(source, "rb") as f, open(target, "wb") as out:
        header = f.read(56)
        if len(header) < 56 or header[:8] != b"LABBYBK1" or header[8] != 1:
            sys.exit("That is not a LabbyTwo encrypted backup.")
        iterations, = struct.unpack(">I", header[9:13])
        salt, prefix = header[13:29], header[29:36]
        chunk, = struct.unpack(">I", header[36:40])

        key = hashlib.pbkdf2_hmac("sha256", passphrase, salt, iterations, 32)
        check = hmac.new(key, b"LabbyTwo backup key check", hashlib.sha256).digest()[:16]
        if not hmac.compare_digest(check, header[40:56]):
            sys.exit("Wrong passphrase.")

        aes = AESGCM(key)
        size = chunk + 16
        current, index = f.read(size), 0
        while True:
            # Read one ahead: only the end of the file says which chunk is the last.
            ahead = f.read(size) if len(current) == size else b""
            last = len(ahead) == 0
            nonce = prefix + struct.pack(">IB", index, 1 if last else 0)
            out.write(aes.decrypt(nonce, current, header))  # raises InvalidTag if damaged
            if last:
                break
            current, index = ahead, index + 1

    print(f"Wrote {target}")


if __name__ == "__main__":
    main()
