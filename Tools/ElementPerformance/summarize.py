"""离线汇总已停止的 Player 实验；原始 CSV 是证据，不以平均 FPS 隐藏尾部卡顿。"""
import argparse
import csv
import json
import math
from pathlib import Path


def percentiles(values):
    values.sort()
    if not values:
        return None
    return {"count": len(values),
            "p50": values[math.ceil(len(values) * .50) - 1],
            "p95": values[math.ceil(len(values) * .95) - 1],
            "p99": values[math.ceil(len(values) * .99) - 1],
            "max": values[-1]}


def optional_number(row, name):
    value = row.get(name)
    return None if value in (None, "", "null") else float(value)


def summarize(directory):
    manifest = json.loads((directory / "manifest.json").read_text(encoding="utf-8"))
    configuration = json.loads((directory / "configuration.json").read_text(encoding="utf-8"))
    summary = json.loads((directory / "summary.json").read_text(encoding="utf-8"))
    final = None
    wet_max = 0.0
    saturated = False
    with (directory / "frames.csv").open(encoding="utf-8", newline="") as source:
        for row in csv.DictReader(source):
            final = row
            if row["phase"] == "4":
                wet_max = max(wet_max, float(row["wet"]))
                saturated |= row["exposureSaturated"] == "True"
    result = {"run": directory.name, "scenario": manifest["scenario"],
              "incomplete": manifest["incomplete"], "hardware": manifest["hardware"],
              "metrics": summary, "wetMaxInMeasure": wet_max,
              "exposureSaturatedInMeasure": saturated, "final": final}
    with (directory / "writes.csv").open(encoding="utf-8", newline="") as source:
        writes = list(csv.DictReader(source))
    result["writeCount"] = len(writes)
    result["rejectedEnqueueCount"] = sum(w["accepted"] != "True" for w in writes)
    with (directory / "frames.csv").open(encoding="utf-8", newline="") as source:
        all_frames = list(csv.DictReader(source))
    measure_frames = [row for index, row in enumerate(all_frames)
        if index > 0 and row["phase"] == "4" and all_frames[index - 1]["phase"] == "4"]
    intervals = [float(row["intervalMs"]) for row in measure_frames
        if row["intervalMs"] not in ("", "null")]
    result["frameThresholds"] = {
        "measureCount": len(intervals),
        "over16_67Count": sum(value > 16.67 for value in intervals),
        "over16_67Rate": sum(value > 16.67 for value in intervals) / len(intervals) if intervals else None,
        "over33_33Count": sum(value > 33.33 for value in intervals),
        "over33_33Rate": sum(value > 33.33 for value in intervals) / len(intervals) if intervals else None,
    }
    gpu_path = directory / "gpu-frames.csv"
    if gpu_path.is_file():
        with gpu_path.open(encoding="utf-8", newline="") as source:
            timings = list(csv.DictReader(source))
        measure_timings = [row for row in timings if row.get("phaseAtReceipt") == "4"]
        # 旧导出只有cpuMs/gpuMs，新导出额外保留Present与线程时间；工具需同时可读，
        # 这样历史基线仍然有效，缺失GPU数据也不会被误写成0ms。
        result["frameTiming"] = {
            "total": len(timings),
            "gpuMissing": sum(optional_number(row, "gpuMs") is None for row in timings),
            "measureByReceiptCount": len(measure_timings),
            "measureGpuMissing": sum(optional_number(row, "gpuMs") is None
                for row in measure_timings),
            "gpuCoverage": (sum(optional_number(row, "gpuMs") is not None
                for row in measure_timings) / len(measure_timings)) if measure_timings else 0.0,
            "phaseScope": "phaseAtReceipt; boundary frames are not execution-aligned",
            "gpuMs": percentiles([value for row in measure_timings
                if (value := optional_number(row, "gpuMs")) is not None]),
            "cpuMainThreadMs": percentiles([value for row in measure_timings
                if (value := optional_number(row, "cpuMainThreadMs")) is not None]),
            "cpuPresentWaitMs": percentiles([value for row in measure_timings
                if (value := optional_number(row, "cpuPresentWaitMs")) is not None]),
            "cpuRenderThreadMs": percentiles([value for row in measure_timings
                if (value := optional_number(row, "cpuRenderThreadMs")) is not None]),
        }
    if manifest["scenario"] == "F02-multi-source" and final:
        frame_rate_mode = configuration.get("scenario", {}).get("FrameRateMode")
        metrics = result["metrics"]
        timing = result.get("frameTiming") or {}
        reliability = (not result["incomplete"] and result["rejectedEnqueueCount"] == 0
            and final.get("submitted") == "18432"
            and all(final.get(name) == "0" for name in (
                "pendingSpawns", "pendingStreamingWrites", "dropped",
                "waterUnexplainedGmu", "poisonUnexplainedGmu", "stickyUnexplainedGmu",
                "archiveErrors", "restoreErrors", "readbackErrors"))
            and all(final.get(name) == "True" for name in (
                "waterAuditValid", "poisonAuditValid", "stickyAuditValid")))
        gc_zero = metrics.get("gcBytes") is not None and metrics["gcBytes"].get("max") == 0
        if frame_rate_mode == 1:
            performance = (metrics["intervalMs"]["p99"] <= 18
                and result["frameThresholds"]["over33_33Rate"] <= .001)
            mode = "Target60"
        else:
            gpu = timing.get("gpuMs")
            cpu_work = timing.get("cpuMainThreadMs")
            # 有效子集可报告统计，但缺失帧可能系统性偏向长帧；未经采样偏差验证不能据此通过全帧预算。
            gpu_sampled = (gpu is not None and gpu["count"] >= 1000
                and timing.get("measureGpuMissing") == 0)
            performance = (cpu_work is not None and cpu_work["p99"] <= 16.67
                and gpu_sampled and gpu["p99"] <= 16.67)
            mode = "Uncapped"
        result["f02Gate"] = {"mode": mode,
            "targetFpsApplied": configuration.get("targetFpsApplied", 60 if frame_rate_mode == 1 else -1),
            "reliability": reliability,
            "gcZero": gc_zero, "performance": performance,
            "passed": reliability and gc_zero and performance,
            "scope": "single run; formal Target60 acceptance still requires three runs"}
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("root", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--after", required=True, help="只收录此 UTC 后完成的目录，例如 20260905-174000")
    args = parser.parse_args()
    results = []
    for directory in sorted(args.root.iterdir()):
        if not directory.is_dir() or not (directory / "summary.json").is_file():
            continue
        # 目录末尾固定为 yyyyMMdd-HHmmss-fff，不依赖含连字符的场景名长度。
        if directory.name[-19:] < args.after:
            continue
        results.append(summarize(directory))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")
    print("run,p50_ms,p99_ms,alive,dropped,wet_max,gc_max,gpu_valid,gpu_missing,incomplete")
    for result in results:
        interval = result["metrics"]["intervalMs"] or {}
        gc = result["metrics"]["gcBytes"] or {}
        final = result["final"] or {}
        timing = result.get("frameTiming") or {}
        gpu = timing.get("gpuMs") or {}
        print(",".join(str(v) for v in (result["run"], interval.get("p50"), interval.get("p99"),
              final.get("alive"), final.get("dropped"), result["wetMaxInMeasure"],
              gc.get("max"), gpu.get("count", 0), timing.get("gpuMissing", 0), result["incomplete"])))


if __name__ == "__main__":
    main()
