#!/usr/bin/env python3
"""Independently verify a .intunewin package without uploading it to Intune.

Usage: python3 tools/verify-intunewin.py <file.intunewin>

This is a *second opinion* on the engine's output: it re-derives every value
Intune's client checks, using code paths that share nothing with our writer.
Crypto comes from Python's stdlib (`hmac`/`hashlib`) and AES decryption shells
out to `openssl` — mirroring the engine's "no third-party deps" stance (cf.
tools/generate-icns.py, which shells to `sips`/`iconutil`).

What it proves: the package is structurally well-formed and cryptographically
self-consistent — i.e. the Intune client (IME) can authenticate the HMAC,
decrypt the payload, match the digest/size, and find the SetupFile. That is the
bar for *format/crypto* acceptance.

What it CANNOT prove: tenant-side server rules (ToolVersion allow-listing, app
type/assignment requirements, signing for Win10 S-mode, etc.). Only a real
upload exercises those. A PASS here means "the bytes are valid"; it does not
guarantee a given tenant will ingest it.

Exit code 0 = all checks passed; non-zero = at least one check failed.
"""
import argparse
import base64
import binascii
import hashlib
import hmac
import os
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
import zipfile

# Canonical entry paths and constants the .intunewin format mandates.
CONTENTS_ENTRY = "IntuneWinPackage/Contents/IntunePackage.intunewin"
METADATA_ENTRY = "IntuneWinPackage/Metadata/Detection.xml"
CONTENT_FILE_NAME = "IntunePackage.intunewin"
PROFILE_IDENTIFIER = "ProfileVersion1"
MAC_LEN = 32   # HMAC-SHA256 output / AES-256 key length
KEY_LEN = 32
IV_LEN = 16
CHUNK = 1024 * 1024   # streaming I/O unit — packages can be multi-GB

# ── tiny check-reporting harness ────────────────────────────────────────────

class Report:
    """Accumulates PASS/FAIL/WARN lines and tracks whether anything failed."""

    def __init__(self):
        self.failed = False
        self.warned = False

    def ok(self, msg):
        print(f"  \033[32m[ OK ]\033[0m {msg}")

    def fail(self, msg):
        self.failed = True
        print(f"  \033[31m[FAIL]\033[0m {msg}")

    def warn(self, msg):
        self.warned = True
        print(f"  \033[33m[WARN]\033[0m {msg}")

    def info(self, msg):
        print(f"         {msg}")

    def check(self, ok, ok_msg, fail_msg):
        """Convenience: report ok_msg if `ok` else fail_msg. Returns `ok`."""
        if ok:
            self.ok(ok_msg)
        else:
            self.fail(fail_msg)
        return ok


def section(title):
    print(f"\n\033[1m{title}\033[0m")


def b64decode_strict(value):
    """Decode base64, raising on malformed input (validate=True)."""
    return base64.b64decode(value, validate=True)


# ── individual verification stages ──────────────────────────────────────────

def read_container(path, rpt, workdir):
    """Open the outer OPC zip; return (detection_xml_bytes, blob_path) with the
    encrypted blob streamed to a scratch file — never held in memory."""
    section("1. Outer container")
    if not zipfile.is_zipfile(path):
        rpt.fail(f"Not a valid ZIP/OPC archive: {path}")
        return None, None

    with zipfile.ZipFile(path) as zf:
        names = set(zf.namelist())
        rpt.check(
            CONTENTS_ENTRY in names,
            f"Contains {CONTENTS_ENTRY}",
            f"Missing required entry {CONTENTS_ENTRY}")
        rpt.check(
            METADATA_ENTRY in names,
            f"Contains {METADATA_ENTRY}",
            f"Missing required entry {METADATA_ENTRY}")
        if CONTENTS_ENTRY not in names or METADATA_ENTRY not in names:
            return None, None

        extra = names - {CONTENTS_ENTRY, METADATA_ENTRY}
        if extra:
            rpt.warn(f"Unexpected extra entries (Intune ignores these): {sorted(extra)}")

        blob_path = os.path.join(workdir, "content.bin")
        with zf.open(CONTENTS_ENTRY) as src, open(blob_path, "wb") as dst:
            shutil.copyfileobj(src, dst, CHUNK)
        return zf.read(METADATA_ENTRY), blob_path


def parse_detection(xml_bytes, rpt):
    """Parse Detection.xml into a dict; report structural problems. None on fatal."""
    section("2. Detection.xml metadata")
    try:
        root = ET.fromstring(xml_bytes)
    except ET.ParseError as ex:
        rpt.fail(f"Detection.xml is not well-formed XML: {ex}")
        return None

    if root.tag != "ApplicationInfo":
        rpt.fail(f"Root element is <{root.tag}>, expected <ApplicationInfo>")
        return None

    def text(parent, name):
        el = parent.find(name)
        return el.text if el is not None else None

    enc_el = root.find("EncryptionInfo")
    if enc_el is None:
        rpt.fail("Missing <EncryptionInfo>")
        return None

    d = {
        "ToolVersion": root.attrib.get("ToolVersion"),
        "Name": text(root, "Name"),
        "UnencryptedContentSize": text(root, "UnencryptedContentSize"),
        "FileName": text(root, "FileName"),
        "SetupFile": text(root, "SetupFile"),
        "EncryptionKey": text(enc_el, "EncryptionKey"),
        "MacKey": text(enc_el, "MacKey"),
        "InitializationVector": text(enc_el, "InitializationVector"),
        "Mac": text(enc_el, "Mac"),
        "ProfileIdentifier": text(enc_el, "ProfileIdentifier"),
        "FileDigest": text(enc_el, "FileDigest"),
        "FileDigestAlgorithm": text(enc_el, "FileDigestAlgorithm"),
        "MsiInfo": root.find("MsiInfo"),
    }

    rpt.info(f"ToolVersion = {d['ToolVersion']!r}")
    rpt.info(f"Name        = {d['Name']!r}")
    rpt.info(f"SetupFile   = {d['SetupFile']!r}")
    rpt.info(f"UnencryptedContentSize = {d['UnencryptedContentSize']}")

    # Required text fields must be present.
    required = ["UnencryptedContentSize", "SetupFile", "EncryptionKey", "MacKey",
                "InitializationVector", "Mac", "FileDigest"]
    missing = [k for k in required if not d.get(k)]
    rpt.check(not missing,
              "All required metadata fields present",
              f"Missing required field(s): {missing}")
    if missing:
        return None

    # Fixed-value fields Intune expects.
    rpt.check(d["FileName"] == CONTENT_FILE_NAME,
              f"FileName = {CONTENT_FILE_NAME}",
              f"FileName = {d['FileName']!r}, expected {CONTENT_FILE_NAME!r}")
    rpt.check((d["FileDigestAlgorithm"] or "SHA256") == "SHA256",
              "FileDigestAlgorithm = SHA256",
              f"FileDigestAlgorithm = {d['FileDigestAlgorithm']!r}, expected 'SHA256'")
    rpt.check((d["ProfileIdentifier"] or PROFILE_IDENTIFIER) == PROFILE_IDENTIFIER,
              f"ProfileIdentifier = {PROFILE_IDENTIFIER}",
              f"ProfileIdentifier = {d['ProfileIdentifier']!r}, expected {PROFILE_IDENTIFIER!r}")

    if not d["ToolVersion"]:
        rpt.warn("ToolVersion attribute is empty — some tenants reject this.")

    return d


def decode_crypto(d, rpt):
    """Base64-decode + length-check key material. Returns dict of bytes or None."""
    section("3. Key material")
    fields = {
        "EncryptionKey": KEY_LEN,
        "MacKey": KEY_LEN,
        "InitializationVector": IV_LEN,
        "Mac": MAC_LEN,
        "FileDigest": MAC_LEN,
    }
    out = {}
    for name, expected_len in fields.items():
        try:
            raw = b64decode_strict(d[name])
        except (binascii.Error, ValueError):
            rpt.fail(f"{name} is not valid base64")
            return None
        if not rpt.check(len(raw) == expected_len,
                         f"{name}: {len(raw)} bytes",
                         f"{name}: {len(raw)} bytes, expected {expected_len}"):
            return None
        out[name] = raw

    # Distinctness is a security requirement: a CSPRNG must not produce keys
    # that share long runs. Same key would be a real crypto weakness; a shared
    # tail is a tell-tale of fabricated/test material.
    if out["EncryptionKey"] == out["MacKey"]:
        rpt.fail("EncryptionKey and MacKey are IDENTICAL (must be distinct).")
    else:
        shared = _common_suffix_len(out["EncryptionKey"], out["MacKey"])
        if shared >= 8:
            rpt.warn(f"EncryptionKey and MacKey share their last {shared} bytes — "
                     "extremely unlikely from a CSPRNG; key material looks fabricated.")
        else:
            rpt.ok("EncryptionKey and MacKey are distinct.")

    return out


def _common_suffix_len(a, b):
    n = 0
    for x, y in zip(reversed(a), reversed(b)):
        if x != y:
            break
        n += 1
    return n


def verify_blob_layout(blob_path, keys, rpt):
    """Check Mac||IV||ciphertext layout, header echo, and ciphertext block size.
    Returns the ciphertext length, or None when the blob can't hold a header."""
    section("4. Encrypted blob layout")
    size = os.path.getsize(blob_path)
    rpt.info(f"Blob length = {size} bytes")

    if not rpt.check(size >= MAC_LEN + IV_LEN,
                     "Blob large enough for Mac(32) + IV(16) header",
                     f"Blob too small ({size} bytes) for the 48-byte header"):
        return None

    with open(blob_path, "rb") as f:
        header_mac = f.read(MAC_LEN)
        header_iv = f.read(IV_LEN)
    ct_len = size - MAC_LEN - IV_LEN

    rpt.check(header_mac == keys["Mac"],
              "Blob header Mac matches Detection.xml <Mac>",
              "Blob header Mac differs from Detection.xml <Mac>")
    rpt.check(header_iv == keys["InitializationVector"],
              "Blob header IV matches Detection.xml <InitializationVector>",
              "Blob header IV differs from Detection.xml <InitializationVector>")
    rpt.check(ct_len > 0 and ct_len % 16 == 0,
              f"Ciphertext is a whole number of AES blocks ({ct_len} B = {ct_len//16} × 16)",
              f"Ciphertext length {ct_len} is not a positive multiple of 16")
    return ct_len


def verify_hmac(blob_path, keys, rpt):
    """The authenticity check Intune performs: HMAC(MacKey, IV||ciphertext)."""
    section("5. HMAC authentication (the gate Intune enforces)")
    h = hmac.new(keys["MacKey"], digestmod=hashlib.sha256)
    with open(blob_path, "rb") as f:
        f.seek(MAC_LEN)
        for chunk in iter(lambda: f.read(CHUNK), b""):
            h.update(chunk)
    computed = h.digest()
    ok = hmac.compare_digest(computed, keys["Mac"])
    rpt.check(ok,
              "HMAC-SHA256(MacKey, IV‖ciphertext) == stored Mac",
              "HMAC MISMATCH — Intune would reject this with a hash/integrity error.")
    if not ok:
        rpt.info(f"computed = {computed.hex()}")
        rpt.info(f"stored   = {keys['Mac'].hex()}")
    return ok


def decrypt_payload(blob_path, keys, rpt, workdir):
    """AES-256-CBC decrypt via openssl. Returns the plaintext file path or None.
    Everything stays on disk — a multi-GB payload never enters Python memory."""
    section("6. AES-256-CBC decryption (independent: openssl)")
    openssl = shutil.which("openssl")
    if not openssl:
        # A missing decryptor means digest/size/payload/SetupFile can't be
        # verified at all — that must read as REJECT, never "PASS (with
        # warnings)", or a truncated payload sails through on a bare machine.
        rpt.fail("openssl not found on PATH — cannot decrypt, so the digest, size, "
                 "and payload checks are impossible. Install openssl and re-run.")
        return None

    cin = os.path.join(workdir, "cipher.bin")
    cout = os.path.join(workdir, "plain.bin")
    with open(blob_path, "rb") as src, open(cin, "wb") as dst:
        src.seek(MAC_LEN + IV_LEN)
        shutil.copyfileobj(src, dst, CHUNK)
    # -K/-iv (raw hex) bypass openssl's password KDF + "Salted__" header, so
    # the input is treated as raw ciphertext. PKCS7 padding is stripped by
    # default; a bad key/padding makes openssl exit non-zero ("bad decrypt").
    cmd = [openssl, "enc", "-d", "-aes-256-cbc",
           "-K", keys["EncryptionKey"].hex(),
           "-iv", keys["InitializationVector"].hex(),
           "-in", cin, "-out", cout]
    proc = subprocess.run(cmd, capture_output=True, text=True)
    if proc.returncode != 0:
        rpt.fail("openssl could not decrypt (bad key or corrupt PKCS7 padding).")
        if proc.stderr.strip():
            rpt.info(proc.stderr.strip())
        return None

    rpt.ok(f"Decrypted to {os.path.getsize(cout)} bytes of plaintext.")
    return cout


def verify_plaintext(plain_path, d, keys, rpt):
    """Size, digest, and payload-zip checks against the recovered plaintext."""
    section("7. Payload integrity")

    declared_size = int(d["UnencryptedContentSize"])
    actual_size = os.path.getsize(plain_path)
    rpt.check(actual_size == declared_size,
              f"Plaintext length == UnencryptedContentSize ({declared_size})",
              f"Plaintext is {actual_size} bytes but UnencryptedContentSize = {declared_size}")

    sha = hashlib.sha256()
    with open(plain_path, "rb") as f:
        for chunk in iter(lambda: f.read(CHUNK), b""):
            sha.update(chunk)
    rpt.check(hmac.compare_digest(sha.digest(), keys["FileDigest"]),
              "SHA-256(plaintext) == FileDigest",
              "SHA-256(plaintext) != FileDigest — payload does not match its recorded digest.")

    section("8. Recovered payload contents")
    if not zipfile.is_zipfile(plain_path):
        rpt.fail("Recovered payload is not a valid ZIP archive.")
        return
    with zipfile.ZipFile(plain_path) as pz:
        entries = pz.namelist()
        rpt.ok(f"Payload is a valid ZIP with {len(entries)} entr"
               f"{'y' if len(entries) == 1 else 'ies'}.")
        # SetupFile is recorded with Windows backslashes; zip entries use '/'.
        setup = d["SetupFile"].replace("\\", "/")
        rpt.check(setup in entries,
                  f"SetupFile present in payload: {d['SetupFile']!r}",
                  f"SetupFile {d['SetupFile']!r} not found among payload entries: {entries}")


def verify_msi(d, rpt):
    """If the setup file is an MSI, check MsiInfo / MsiProductCode.

    Severity rationale: a .msi uploaded as a *Win32 app* doesn't strictly
    require <MsiInfo> (it's informational, and the official tool emits it for
    convenience), so a missing block is a WARN, not a REJECT — it mirrors our
    engine, which only warns when it can't parse the MSI. But an <MsiInfo>
    block that's present with an empty <MsiProductCode> is genuinely malformed,
    so that stays a hard FAIL.
    """
    setup = (d["SetupFile"] or "").lower()
    if not setup.endswith(".msi"):
        return
    section("9. MSI metadata")
    msi = d["MsiInfo"]
    if msi is None:
        rpt.warn("SetupFile is an .msi but Detection.xml has no <MsiInfo>. "
                 "Fine for a Win32 app upload; the official tool would include "
                 "it. If our engine logged 'Could not read MSI metadata', the "
                 "MSI reader failed on this file.")
        return
    product = msi.find("MsiProductCode")
    rpt.check(product is not None and product.text,
              f"MsiInfo present (MsiProductCode = {product.text if product is not None else None})",
              "MsiInfo present but MsiProductCode is empty.")


# ── entry point ─────────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(
        description="Independently verify a .intunewin package (format + crypto).")
    parser.add_argument("file", help="path to the .intunewin file")
    args = parser.parse_args()

    if not os.path.isfile(args.file):
        print(f"error: file not found: {args.file}", file=sys.stderr)
        return 2

    print(f"\033[1mVerifying:\033[0m {args.file}")
    rpt = Report()

    with tempfile.TemporaryDirectory(prefix="verify-intunewin-") as workdir:
        xml_bytes, blob_path = read_container(args.file, rpt, workdir)
        if xml_bytes is None:
            return _verdict(rpt)

        d = parse_detection(xml_bytes, rpt)
        if d is None:
            return _verdict(rpt)

        keys = decode_crypto(d, rpt)
        if keys is None:
            return _verdict(rpt)

        ct_len = verify_blob_layout(blob_path, keys, rpt)
        verify_hmac(blob_path, keys, rpt)

        if ct_len is not None:
            plain_path = decrypt_payload(blob_path, keys, rpt, workdir)
            if plain_path is not None:
                verify_plaintext(plain_path, d, keys, rpt)

        verify_msi(d, rpt)
    return _verdict(rpt)


def _verdict(rpt):
    section("Verdict")
    if rpt.failed:
        print("  \033[31m✗ REJECT\033[0m — this package is malformed or cryptographically "
              "inconsistent; Intune's client would refuse it.")
        result = 1
    elif rpt.warned:
        print("  \033[33m✓ PASS (with warnings)\033[0m — format and crypto are valid; "
              "review the warnings above.")
        result = 0
    else:
        print("  \033[32m✓ PASS\033[0m — format and crypto are valid.")
        result = 0
    print("\n  Note: this proves format/crypto correctness only. Tenant-side rules "
          "(ToolVersion\n  allow-listing, app-type/assignment requirements) are proven "
          "only by a real upload.")
    return result


if __name__ == "__main__":
    sys.exit(main())
