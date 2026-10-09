"""Prepare an independent session harness without changing the 0.3.24 regression files."""
from pathlib import Path
import json

here = Path(__file__).resolve().parent
base = here.parent / "AutoplaySession" / "Stub.cs.txt"
target = here / "Stub.cs.txt"
if not target.exists():
    text = base.read_text(encoding="utf-8-sig")
    text = text.replace("bool manualOcr, uiVisible, overlayBrowseDirectory;", "bool manualOcr, uiVisible;")
    text = text.replace('        string BrowseSectionTitle(PlaybackEngine engine, string section) =>\n            engine.Pack.Chapters.SelectMany(c => c.Sections).First(s => s.Id == section).Title;\n', '')
    text = text.replace('        void Notify() { }', '        void Notify() => RefreshOverlayBrowseIfNeeded();')
    text = text.replace('            CancelBranchFollow(preserveBranchIntent); CancelClickFollow(); CancelAutoPlayback();', '            ResetOverlayBrowseRequests(); CancelBranchFollow(preserveBranchIntent); CancelClickFollow(); CancelAutoPlayback();')
    target.write_text(text, encoding="utf-8")

path = Path("<数据目录>/全部配音/01_主线/第29章_源解信标/pack.json")
pack = json.loads(path.read_text(encoding="utf-8-sig"))
section = "ch29-1dc1158611c810722709"
ids = {section + "-menu-" + i for i in ("000", "006", "011", "012", "002", "003", "019")}
nodes = {n["id"]: n for n in pack["nodes"]}
for n in pack["nodes"]:
    if n["id"] not in ids:
        continue
    print(n["id"].removeprefix(section), n["text"], n.get("menuType"))
    for o in n["options"]:
        print(json.dumps({"label": o["label"], "id": o["id"].removeprefix(section),
            "target": o["targetId"].removeprefix(section), "kind": nodes[o["targetId"]]["kind"],
            "path": o["pathId"].removeprefix(section), "body": o.get("bodyVerified"), "exit": o.get("exitVerified"),
            "returnId": o.get("returnId", "").removeprefix(section), "requires": o.get("requires"), "excludes": o.get("excludes")}, ensure_ascii=False))
