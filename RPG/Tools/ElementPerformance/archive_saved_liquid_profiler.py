"""Archive the bounded L4.25 CPU comparison without touching Unity scene data."""
import datetime
import difflib
import hashlib
import json
import pathlib
import shutil
import statistics


ROOT = pathlib.Path(__file__).resolve().parents[2]
TEMP = ROOT / "Temp/ElementPerformance"
WORK = TEMP / "user-profiler-20261004"
OUTPUT = ROOT / "docs/superpowers/validation/2026-10-04-liquid-profiler"


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def copy(source, name=None):
    target = OUTPUT / (name or source.name)
    shutil.copyfile(source, target)
    return target.name


def run():
    OUTPUT.mkdir(parents=True, exist_ok=True)
    gate = read_json(TEMP / "user-profiler-final-255-concentrated.json")
    if (gate["Failed"] or gate["Skipped"] or gate["Aborted"] or not gate["SceneRestored"]
            or gate["Completed"] != gate["Planned"] or gate["Passed"] != 255):
        raise RuntimeError("The final 255-case gate is incomplete or failed")
    state = read_json(TEMP / "grid-liquid-capture.json")
    if state["Playing"] or state["Paused"] or state["SceneDirty"] or state["TimeScale"] != 1:
        raise RuntimeError("Final Editor state differs from this task's clean Edit Mode baseline")
    scene = ROOT / "Assets/_Project/Scenes/P7_DemoRun.unity"
    before_state = read_json(WORK / "before-state.json")
    if sha(scene) != before_state["SceneSha256"]:
        raise RuntimeError("The scene changed during this task; report it before claiming preservation")
    source = ROOT / "Assets/_Project/Scripts/Rendering/ElementField/GridLiquidFieldFrame.cs"
    original = WORK / "GridLiquidFieldFrame.before.cs.txt"
    optimized = WORK / "GridLiquidFieldFrame.after.cs.txt"
    if sha(source) != sha(optimized) or sha(original) != before_state["SourceSha256"]:
        raise RuntimeError("Production source or saved original differs from the measured variants")

    comparisons = []
    for name in ("sparse-capture", "sparse-dense", "mixed-signature"):
        a = read_json(WORK / (name + "-original-final.json"))
        b = read_json(WORK / (name + "-optimized-final.json"))
        for key in ("Signature", "Quantity", "Pages", "Owners"):
            if a[key] != b[key]:
                raise RuntimeError(name + " comparison changed " + key)
        gc_key = "GcBytes" if name == "mixed-signature" else "AllocatedBytes"
        if a[gc_key] or b[gc_key]:
            raise RuntimeError(name + " warm comparison allocated")
        times_key = "CaptureRoundsMs" if name == "mixed-signature" else "CaptureMilliseconds"
        before, after = statistics.mean(a[times_key]), statistics.mean(b[times_key])
        comparisons.append({
            "Case": name, "BeforeFile": copy(WORK / (name + "-original-final.json")),
            "AfterFile": copy(WORK / (name + "-optimized-final.json")),
            "BeforeMeanMs": before, "AfterMeanMs": after, "ReductionPercent": (1 - after / before) * 100,
            "Signature": a["Signature"], "Quantity": a["Quantity"], "Pages": a["Pages"],
            "Owners": a["Owners"], "WarmAllocatedBytes": b[gc_key], "Overflow": b.get("Overflow", False),
        })

    for name in ("user-profiler-sparse-original-configurations.json", "user-profiler-sparse-original-final.json",
                 "user-profiler-sparse-optimized-final.json", "user-profiler-sparse-final-focused.json",
                 "user-profiler-final-concentrated.json", "user-profiler-final-255-concentrated.json",
                 "gate-saved-root-state.json", "p7-grid-cutover-audit.txt"):
        copy(TEMP / name)
    copy(TEMP / "grid-liquid-capture.json", "final-editor-state.json")
    for name in ("sparse-presentation-capture.json", "sparse-presentation-dense-fallback.json",
                 "frozen-presentation-signature.json"):
        copy(TEMP / name, "final-gate-" + name)
    copy(original)
    copy(optimized)
    copy(WORK / "before-state.json")
    # 此导出器在独立临时Unity项目编译；.cs.txt档案不会进入用户项目的Assembly。
    offline = pathlib.Path("C:/Users/zhy/AppData/Local/Temp/NornProfilerOffline-20261004")
    copy(offline / "Assets/Editor/OfflineProfilerExport.cs", "OfflineProfilerExport.cs.txt")
    copy(offline / "config.json", "offline-export-config.json")
    for info in WORK.glob("*.info.txt"):
        copy(info)
    copy(WORK / "complete.txt", "offline-export-complete.txt")
    patch = difflib.unified_diff(original.read_text(encoding="utf-8-sig").splitlines(True),
                                source.read_text(encoding="utf-8-sig").splitlines(True),
                                fromfile="Measured original GridLiquidFieldFrame.cs",
                                tofile="Measured optimized GridLiquidFieldFrame.cs")
    (OUTPUT / "frame-cpu-optimization.patch").write_text("".join(patch), encoding="utf-8")

    previous = read_json(ROOT / "docs/superpowers/validation/2026-10-04-liquid-visibility/manifest.json")
    sources, changed = [], []
    for record in previous["Sources"]:
        path = ROOT / record["File"]
        current = sha(path)
        sources.append({"File": record["File"], "Sha256": current})
        if current != record["Sha256"]:
            changed.append({"File": record["File"], "BeforeSha256": record["Sha256"], "AfterSha256": current})
    # 旧Manifest没有覆盖所有Runtime文件；独立保存本轮实际测量的Frame起止Hash。
    frame_file = source.relative_to(ROOT).as_posix()
    if not any(record["File"] == frame_file for record in sources):
        sources.append({"File": frame_file, "Sha256": sha(source)})
    tools = ("analyze_saved_profiler.py", "archive_saved_liquid_profiler.py", "run_current_liquid_gate.py")
    for name in tools:
        path = ROOT / "Tools/ElementPerformance" / name
        sources.append({"File": path.relative_to(ROOT).as_posix(), "Sha256": sha(path)})
    result = {
        "Utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "Gate": {key: gate[key] for key in ("Kind", "StartUtc", "EndUtc", "Passed", "Failed", "Skipped",
                                             "Planned", "Completed", "Aborted", "SceneRestored")},
        "GateFixtureCount": len({case["Name"].rsplit(".", 1)[0] for case in gate["Cases"]}),
        "SceneFileUnchangedThisTask": True, "SceneSha256": sha(scene),
        "ComparisonScope": "Fixed input CPU Capture timing; not Play Mode frame rate or total GPU execution time",
        "Comparisons": comparisons, "RecordedSourceChangesSinceL424": changed, "Sources": sources,
        "MeasuredFrameChange": {"File": frame_file, "BeforeSha256": sha(original),
                                "AfterSha256": sha(source), "ProductionMatchesMeasuredOptimized": True},
        "AssemblyTimesUtc": [{"Assembly": name, "LastWriteUtc": datetime.datetime.fromtimestamp(
            (ROOT / "Library/ScriptAssemblies" / name).stat().st_mtime, datetime.timezone.utc).isoformat()}
            for name in ("Game.Rendering.dll", "Game.Rendering.Tests.dll")],
        "Evidence": [{"File": path.name, "Sha256": sha(path)} for path in sorted(OUTPUT.iterdir())
                     if path.is_file() and path.name != "manifest.json"],
    }
    (OUTPUT / "manifest.json").write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({key: result[key] for key in ("Gate", "GateFixtureCount", "SceneFileUnchangedThisTask",
                                                  "MeasuredFrameChange", "Comparisons")}, ensure_ascii=False))


if __name__ == "__main__":
    run()
