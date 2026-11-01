"""Add the messages argument to the Core factories the tests call.

Walks each call by paren balance rather than by regex, because the arguments
themselves contain calls (``Create(Request(), Source(), Encoder())``) and a lazy
regex stops at the first closing paren it meets.
"""
import pathlib
import re
import sys

TARGETS = {
    "EncodePlan.Create": 3,
    "RecompressPlan.Create": 2,
    "SubtitleChain.Build": 3,
    "SubtitleCopy.Resolve": None,   # overloaded: 3-arg codec form and 3-arg list form
    "SubtitleTrack.Problem": 2,
    "SubtitleTrack.Note": 3,
    "ArgsFragment.Validate": 1,
    "ArgsFragment.Parse": 1,
    "RecompressCodecs.ValidateContainer": 3,
}

ROOT = pathlib.Path(r"D:\GitHubRepos\LiveArchiveEncoder-fr0yo0v0\LiveArchiveEncoder\tests")

FILES = [
    "Lae.Tests.Core/EncodePlanTests.cs",
    "Lae.Tests.Core/EncodePlanTrialTests.cs",
    "Lae.Tests.Core/EncodeRequestTests.cs",
    "Lae.Tests.Core/RecompressPlanTests.cs",
    "Lae.Tests.Core/SubtitleChainTests.cs",
    "Lae.Tests.Core/SubtitleCopyTests.cs",
    "Lae.Tests.Core/SubtitleTrackTests.cs",
    "Lae.Tests.Core/DomainTests.cs",
    "Lae.Tests.Encoding/LiveEncodeTests.cs",
    "Lae.Tests.Encoding/PreflightTests.cs",
]


def split_args(text, start):
    """Return (args, close_index) for a call whose '(' is at start-1."""
    depth = 0
    args = []
    current = []
    i = start
    while i < len(text):
        ch = text[i]
        if ch in "([{":
            depth += 1
        elif ch in ")]}":
            if depth == 0 and ch == ")":
                args.append("".join(current))
                return args, i
            depth -= 1
        elif ch == "," and depth == 0:
            args.append("".join(current))
            current = []
            i += 1
            continue
        current.append(ch)
        i += 1
    raise RuntimeError("unbalanced")


def patch(text, name, expected):
    out = []
    index = 0
    changed = 0
    while True:
        found = text.find(name + "(", index)
        if found < 0:
            out.append(text[index:])
            break
        # Skip the declaration / xml-doc references.
        line_start = text.rfind("\n", 0, found) + 1
        if text[line_start:found].lstrip().startswith("//") or text[line_start:found].lstrip().startswith("///"):
            out.append(text[index:found + len(name) + 1])
            index = found + len(name) + 1
            continue

        open_at = found + len(name) + 1
        args, close = split_args(text, open_at)
        if args and args[-1].strip() == "En":
            out.append(text[index:close + 1])
            index = close + 1
            continue
        if expected is not None and len(args) != expected:
            # Already carries the argument (or is a different shape): leave it.
            out.append(text[index:close + 1])
            index = close + 1
            continue

        out.append(text[index:close])
        out.append(", En)")
        index = close + 1
        changed += 1
    return "".join(out), changed


def main():
    total = 0
    for rel in FILES:
        path = ROOT / rel
        text = path.read_text(encoding="utf-8")
        original = text
        for name, expected in TARGETS.items():
            text, count = patch(text, name, expected)
            total += count
        if text != original:
            if "using static " not in text:
                project = "Lae.Tests.Encoding" if rel.startswith("Lae.Tests.Encoding") else "Lae.Tests.Core"
                text = text.replace(
                    "namespace ",
                    f"using static {project}.TestMessages;\n\nnamespace ",
                    1)
            path.write_text(text, encoding="utf-8")
            print(f"patched {rel}")
    print(f"{total} call sites")


if __name__ == "__main__":
    sys.exit(main())
