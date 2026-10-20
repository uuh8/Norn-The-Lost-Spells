"""从本轮真实Profiler TSV生成统计；只汇总同一Marker，不将父子耗时或线程等待相加。"""
import csv
import hashlib
import json
from collections import defaultdict
from pathlib import Path
from zipfile import ZipFile

OUTPUT = Path(__file__).resolve().parent
ROOT = OUTPUT.parents[3]
MARKERS = {"PlayerLoop", "ElementWorld.Simulate", "ElementWorld.Simulate.FireFall",
           "ElementWorld.Simulate.PrepareNeighbors", "ElementWorld.Simulate.FireDecay", "ElementWorld.Exposure"}
result = {}
for phase in ("before", "after", "after-final"):
    rows = {}
    for path in (ROOT / "TestResults/SpellWorldPerformance" / phase).glob("*.tsv"):
        frames, times = set(), defaultdict(list)
        with path.open(encoding="utf-8-sig") as stream:
            for row in csv.DictReader(stream, delimiter="\t"):
                frames.add(row["frame"])
                if row["thread"] == "Main Thread" and row["marker"] in MARKERS:
                    times[row["marker"]].append(float(row["ms"]))
        item = json.loads(path.with_suffix(".json").read_text(encoding="utf-8-sig"))
        item["ProfilerFrames"] = len(frames)
        item["MainThreadInclusiveMarkers"] = {
            name: {"MsPerFrame": sum(values) / len(frames), "Samples": len(values), "MaxSampleMs": max(values)}
            for name, values in times.items()
        }
        rows[path.stem] = item
    result[phase] = rows

preservation = {}
with ZipFile(ROOT / "Temp/Spells/RuntimeRepairBackup/before-scenes-and-spell-sources.zip") as archive:
    for name in archive.namelist():
        if name.endswith(".unity"):
            preservation[name] = hashlib.sha256(archive.read(name)).hexdigest() == hashlib.sha256((ROOT / name).read_bytes()).hexdigest()
result["FormalSceneFilesUnchanged"] = preservation
result["Scope"] = "Editor, saved scene entry, 60 warmup + 20 profiler warmup + 90 idle frames; no Standalone/GPU timing claim; naturally evolving worlds are not identical tick-by-tick replays."
(OUTPUT / "performance-comparison.json").write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
assert len(result["before"]) == 3 and len(result["after-final"]) == 3
assert all(preservation.values())
print("Three scenes compared; author scene bytes preserved.")
