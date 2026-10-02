#!/usr/bin/env python3
"""Creates a FastEndpoints stub for every route in docs/routes.json that does not have one yet.

Never overwrites: a stub's file is only written when it does not exist, so a ported endpoint (same class name,
same file) survives a rerun, and rerunning after gen_routes.py only adds what is new. Prints any two routes
that would claim the same method and path shape, and does not write the second.
"""
import json, re, pathlib, collections

HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parent.parent
# Each route's stub goes into the project of the service that owns it (routes.json, owner); owners not listed here still
# live in the HTTP service.
OWNER_PROJECTS = {"web": "OpenVersus.Server.Web"}


def project(row):
    return OWNER_PROJECTS.get(row["owner"], "OpenVersus.Server.Http")
ALL_VERBS = ["GET", "PUT", "POST", "DELETE"]
KIND_DIRS = {
    "game": "Game",
    "ovs-client": "OpenVersus/Client",
    "ovs-rollback": "OpenVersus/Rollback",
    "ovs-web": "OpenVersus/Web",
    "ovs-admin": "OpenVersus/Admin",
}
VERB_PREFIX = {"GET": "Get", "PUT": "Put", "POST": "Post", "DELETE": "Delete", "ALL": "Any", "?": "Any"}
FE_VERB = {"GET": "FastEndpoints.Http.GET", "PUT": "FastEndpoints.Http.PUT", "POST": "FastEndpoints.Http.POST", "DELETE": "FastEndpoints.Http.DELETE"}


def pascal(text):
    words = [w for w in re.split(r"[^A-Za-z0-9]+", text) if w]
    return "".join(w[:1].upper() + w[1:] for w in words)


def template(path):
    """The ASP.NET Core route template: a trailing * becomes a catch-all parameter."""
    if path.endswith("/*"):
        return path[:-1] + "{**rest}"
    return path


def shape(path):
    return re.sub(r"\{[^}]+\}", "{}", template(path))


def class_name(row):
    prefix = VERB_PREFIX[row["method"]]
    path = row["path"]
    if row["area"] == "ssc":
        return prefix + pascal(path.rsplit("/", 1)[1])
    parts = []
    for seg in path.strip("/").split("/"):
        if seg in ("*",):
            parts.append("All")
        elif seg.startswith("{"):
            parts.append("By" + pascal(seg.strip("{}*")))
        else:
            parts.append(pascal(seg))
    return prefix + "".join(parts)


def folder(row):
    base = KIND_DIRS[row["kind"]]
    if row["kind"] == "game":
        return f"{base}/{'Ssc' if row['area'] == 'ssc' else pascal(row['area']) or 'Root'}"
    return base


def summary(row, verbs):
    lines = [f"{' '.join(verbs) if row['method'] not in ('?', 'ALL') else 'Any method'} {row['path']}."]
    src = []
    if row["binary"]:
        src.append(f"binary {row['binary']}")
    if row["capture"]:
        src.append(f"captured {row['capture']}x")
    if row["server"]:
        src.append("TS server: " + ", ".join(row["server"][:3]) + (" ..." if len(row["server"]) > 3 else ""))
    if src:
        lines.append("Seen in: " + "; ".join(src) + ".")
    notes = "; ".join(dict.fromkeys(n for n in row["notes"] if n))
    if notes:
        lines.append(notes[0].upper() + notes[1:] + ".")
    return lines


def declared_routes():
    """Every path an endpoint in any service's project declares (Routes("...") or an SSC `Route => "name"`), so a route
    already answered under another class name (AnyEquipGems for a route whose method became known) is not written again."""
    paths = set()
    for f in (ROOT / "src").glob("*/Endpoints/**/*.cs"):
        text = f.read_text()
        for m in re.finditer(r'Routes\(([^)]*)\)', text):
            paths.update(p.rstrip("/") for p in re.findall(r'"([^"]+)"', m.group(1)))
        paths.update("/ssc/invoke/" + m.group(1) for m in re.finditer(r'Route\s*=>\s*"([^"]+)"', text))
    return paths


def main():
    rows = json.loads((ROOT / "docs/routes.json").read_text())
    declared = declared_routes()
    claimed = {}  # (verb, shape) -> class
    # The catch-all is hand-written; keep generated SSC names from colliding with it.
    written = skipped = existing = 0
    plans = []
    for row in rows:
        if row["path"].startswith("/.*"):
            print(f"skip (a regex route, handled by the fallback until ported): {row['method']} {row['path']}")
            continue
        verbs = ALL_VERBS if row["method"] in ("?", "ALL") else [row["method"]]
        plans.append((row, verbs))
    # Known methods first, so a '?' route only takes the verbs nothing else claims on its shape.
    plans.sort(key=lambda p: p[0]["method"] in ("?", "ALL"))
    names = collections.Counter()
    for row, verbs in plans:
        free = [v for v in verbs if (v, shape(row["path"])) not in claimed]
        if not free:
            owner = claimed[(verbs[0], shape(row["path"]))]
            print(f"conflict: {row['method']} {row['path']} is already claimed by {owner}; not written")
            skipped += 1
            continue
        name = class_name(row)
        names[(folder(row), name)] += 1
        if names[(folder(row), name)] > 1:
            name += str(names[(folder(row), name)])
        for v in free:
            claimed[(v, shape(row["path"]))] = name
        target = ROOT / "src" / project(row) / "Endpoints" / folder(row) / f"{name}.cs"
        if target.exists() or row["path"] in declared:
            existing += 1
            continue
        ns = f"{project(row)}.Endpoints." + folder(row).replace("/", ".")
        verb_call = f"Verbs({', '.join(FE_VERB[v] for v in free)});"
        doc = "\n".join(f"/// {line}" for line in summary(row, free))
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(f"""using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace {ns};

/// <summary>
{doc}
/// </summary>
public sealed class {name} : StubEndpoint
{{
    public override void Configure()
    {{
        {verb_call}
        Routes("{template(row['path'])}");
    }}
}}
""")
        written += 1
    print(f"{written} stubs written, {existing} already there, {skipped} conflicts skipped")


if __name__ == "__main__":
    main()
