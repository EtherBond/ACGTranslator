"""Read-only XP3 index inventory. Does not extract, decrypt, or execute game code."""
from __future__ import annotations

import argparse
from collections import Counter
import json
from pathlib import Path
import struct
import zlib

MAGIC = b"XP3\r\n \n\x1a\x8bg\x01"
LIMIT = 64 * 1024 * 1024


def exact(stream, size):
    data = stream.read(size)
    if len(data) != size:
        raise ValueError("Truncated XP3 index")
    return data


def chunks(data):
    pos = 0
    while pos < len(data):
        if pos + 12 > len(data):
            raise ValueError("Truncated chunk header")
        tag, size = struct.unpack_from("<4sQ", data, pos)
        pos += 12
        if size > len(data) - pos:
            raise ValueError("Chunk exceeds index")
        yield tag, data[pos:pos + size]
        pos += size


def inventory(path):
    result = {"archive": path.name, "bytes": path.stat().st_size}
    extensions, flags = Counter(), Counter()
    entries = 0
    with path.open("rb") as stream:
        if exact(stream, 11) != MAGIC:
            raise ValueError("Not a standard standalone XP3 header")
        offset = struct.unpack("<Q", exact(stream, 8))[0]
        seen, total = set(), 0
        while True:
            if offset in seen or len(seen) >= 32 or offset >= result["bytes"]:
                raise ValueError("Invalid/cyclic XP3 index chain")
            seen.add(offset)
            stream.seek(offset)
            flag = exact(stream, 1)[0]
            size = struct.unpack("<Q", exact(stream, 8))[0]
            method = flag & 7
            rawsize = struct.unpack("<Q", exact(stream, 8))[0] if method == 1 else size
            if method not in (0, 1) or size > LIMIT or rawsize > LIMIT or total + rawsize > LIMIT:
                raise ValueError("Unsupported/oversized index")
            packed = exact(stream, size)
            if method == 1:
                inflater = zlib.decompressobj()
                data = inflater.decompress(packed, rawsize + 1)
                if len(data) != rawsize or not inflater.eof or inflater.unused_data:
                    raise ValueError("Invalid compressed index length")
            else:
                data = packed
            total += len(data)
            for tag, body in chunks(data):
                if tag != b"File":
                    continue
                infos = [value for key, value in chunks(body) if key == b"info"]
                if len(infos) != 1 or len(infos[0]) < 22:
                    raise ValueError("Invalid info entry")
                info = infos[0]
                protection, _, _, count = struct.unpack_from("<IQQH", info)
                if len(info) < 22 + count * 2:
                    raise ValueError("Truncated file name")
                # Some engines use nonstandard/obfuscated names. Never use these as paths.
                name = info[22:22 + count * 2].decode("utf-16le", errors="replace")
                suffix = Path(name).suffix.lower()
                extensions[suffix or "<no extension>"] += 1
                flags["protected" if protection & 0x80000000 else "unmarked"] += 1
                entries += 1
            if not flag & 0x80:
                break
            offset = struct.unpack("<Q", exact(stream, 8))[0]
    result.update(entries=entries, flags=dict(flags), extensions=dict(extensions), index_blocks=len(seen))
    result["note"] = "Index metadata only; flags do not prove a particular encryption scheme or voice/text pairing."
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("game", type=Path)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    if not args.game.is_dir():
        parser.error("Game directory does not exist")
    archives = []
    for path in sorted(args.game.glob("*.xp3")):
        try:
            archives.append(inventory(path))
        except (ValueError, OSError, struct.error, zlib.error) as exc:
            archives.append({"archive": path.name, "error": str(exc)})
    report = {"game_directory": str(args.game.resolve()), "read_only": True,
              "archives": archives, "paired_samples": 0,
              "next_step": "Obtain readable original-language scripts and matching voice files, or an engine export. Do not use Chinese localization as Japanese ASR reference."}
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"archives": len(archives), "errors": sum("error" in a for a in archives),
                      "paired_samples": 0, "report": str(args.out)}, ensure_ascii=True))
    return int(not archives or any("error" in a for a in archives))


if __name__ == "__main__":
    raise SystemExit(main())
