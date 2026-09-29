#!/usr/bin/env python3
"""Extracts every Hydra (application/x-ag-binary) body from packet captures into <out>/req/ and <out>/resp/, for
HydraCorpusTests (OVS_TEST_HYDRA_CORPUS=<out>). The bodies hold tokens and account ids: keep <out> out of the
repository (dotnet/local/ is gitignored). Decrypts TLS with the keylog in OVS_TLS_KEYLOG (default: his keylog path).

    tools/hydra/extract_corpus.py local/hydra-corpus ~/packet_captures/mvs/*.pcapng.lz4 ~/git/ovs-local-dev/captures/*.pcapng
"""
import binascii, os, pathlib, re, subprocess, sys, tempfile

KEYLOG = os.environ.get("OVS_TLS_KEYLOG", str(pathlib.Path.home() / "packet_captures/tlskeys/wireshark_preferences_tlskey.log"))


def extract(capture, out, tag):
    cmd = ["tshark", "-r", capture, "-o", f"tls.keylog_file:{KEYLOG}", "-Y", "http", "-T", "fields", "-E", "separator=\t",
           "-e", "frame.number", "-e", "http.request", "-e", "http.request.uri", "-e", "http.request_in", "-e", "http.content_type", "-e", "http.file_data"]
    rows = [r.split("\t") + [""] * 6 for r in subprocess.run(cmd, capture_output=True, text=True).stdout.splitlines()]
    uris = {r[0]: r[2] for r in rows if r[1] in ("1", "True")}  # tshark 4.6 prints booleans as True
    count = 0
    for frame, is_request, uri, request_in, ctype, data, *_ in rows:
        if "x-ag-binary" not in ctype or not data:
            continue
        request = is_request in ("1", "True")
        path = re.sub(r"https?://[^/]+", "", uri if request else uris.get(request_in, "unknown")).split("?")[0]
        name = re.sub(r"[^A-Za-z0-9]+", "_", re.sub(r"[0-9a-f]{24}", "ID", path))[:60]
        raw = binascii.unhexlify(data.replace(":", "")) if re.fullmatch(r"[0-9a-fA-F:]+", data) else data.encode("latin1")
        (out / ("req" if request else "resp") / f"{tag}_{frame}_{name}.bin").write_bytes(raw)
        count += 1
    return count


def main():
    out = pathlib.Path(sys.argv[1])
    for d in ("req", "resp"):
        (out / d).mkdir(parents=True, exist_ok=True)
    for capture in sys.argv[2:]:
        tag = pathlib.Path(capture).name.split(".")[0][:24]
        if capture.endswith(".lz4"):
            with tempfile.NamedTemporaryFile(suffix=".pcapng") as tmp:
                subprocess.run(["lz4", "-dqf", capture, tmp.name], check=True)
                n = extract(tmp.name, out, tag)
        else:
            n = extract(capture, out, tag)
        print(f"{tag}: {n} bodies")


if __name__ == "__main__":
    main()
