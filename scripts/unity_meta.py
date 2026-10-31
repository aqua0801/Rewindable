#!/usr/bin/env python3
"""Creates missing Unity .meta files for the package (git-URL packages are immutable: every asset needs one).

GUIDs are uuid5 of the package-relative path, so re-running never changes an existing asset's GUID and two
machines generate the same file. Existing .meta files are left alone.

    python scripts/unity_meta.py            # create missing
    python scripts/unity_meta.py --check    # exit 1 if any is missing (CI)
"""
import sys
import uuid
from pathlib import Path

PACKAGE = Path(__file__).resolve().parent.parent / "unity" / "com.aqua0801.rewindable"
NAMESPACE = uuid.UUID("6f2b9a0e-3c1d-4b7a-9e55-2d1c0a7b9e01")
# Built by `dotnet build -c Release` (Rewindable.Generator) and committed; its .meta must exist even before that.
EXPECTED = ["Runtime/Analyzers/Rewindable.Generator.dll"]

TAIL = "  userData: \n  assetBundleName: \n  assetBundleVariant: \n"


def guid(rel: str) -> str:
    return uuid.uuid5(NAMESPACE, "com.aqua0801.rewindable/" + rel).hex


def body(rel: str, is_dir: bool) -> str:
    head = f"fileFormatVersion: 2\nguid: {guid(rel)}\n"
    if is_dir:
        return head + "folderAsset: yes\nDefaultImporter:\n  externalObjects: {}\n" + TAIL
    name = rel.rsplit("/", 1)[-1]
    ext = Path(name).suffix.lower()
    if name == "package.json":
        return head + "PackageManifestImporter:\n  externalObjects: {}\n" + TAIL
    if ext == ".cs":
        return head + ("MonoImporter:\n  externalObjects: {}\n  serializedVersion: 2\n  defaultReferences: []\n"
                       "  executionOrder: 0\n  icon: {instanceID: 0}\n") + TAIL
    if ext == ".asmdef":
        return head + "AssemblyDefinitionImporter:\n  externalObjects: {}\n" + TAIL
    if ext == ".dll":
        analyzer = "/Analyzers/" in "/" + rel
        labels = "labels:\n- RoslynAnalyzer\n" if analyzer else ""
        any_enabled = 0 if analyzer else 1
        return head + labels + (
            "PluginImporter:\n  externalObjects: {}\n  serializedVersion: 2\n  iconMap: {}\n  executionOrder: {}\n"
            "  defineConstraints: []\n  isPreloaded: 0\n  isOverridable: 0\n  isExplicitlyReferenced: 0\n"
            "  validateReferences: 1\n  platformData:\n"
            "  - first:\n      Any: \n    second:\n"
            f"      enabled: {any_enabled}\n      settings: {{}}\n"
            "  - first:\n      Editor: Editor\n    second:\n      enabled: 0\n      settings:\n"
            "        DefaultValueInitialized: true\n") + TAIL
    if ext in (".md", ".txt", ".json", ".xml"):
        return head + "TextScriptImporter:\n  externalObjects: {}\n" + TAIL
    return head + "DefaultImporter:\n  externalObjects: {}\n" + TAIL


def assets():
    found = set()
    for p in PACKAGE.rglob("*"):
        if p.suffix == ".meta" or any(part.startswith(".") or part.endswith("~") for part in p.relative_to(PACKAGE).parts):
            continue
        found.add((p.relative_to(PACKAGE).as_posix(), p.is_dir()))
    for rel in EXPECTED:
        parts = rel.split("/")
        for i in range(1, len(parts)):
            found.add(("/".join(parts[:i]), True))
        found.add((rel, False))
    return sorted(found)


def main() -> int:
    check = "--check" in sys.argv
    missing = []
    for rel, is_dir in assets():
        meta = PACKAGE / (rel + ".meta")
        if meta.exists():
            continue
        missing.append(rel)
        if not check:
            meta.parent.mkdir(parents=True, exist_ok=True)
            meta.write_text(body(rel, is_dir), encoding="utf-8", newline="\n")
    for rel in missing:
        print(("missing " if check else "created ") + rel + ".meta")
    return 1 if check and missing else 0


if __name__ == "__main__":
    sys.exit(main())
