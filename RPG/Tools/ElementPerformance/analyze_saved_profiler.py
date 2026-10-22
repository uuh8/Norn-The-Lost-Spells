"""Analyze TSVs exported from saved Unity RawFrameDataView recordings.

This is an offline report, not a benchmark runner. It neither opens the user's
scene nor infers before/after conditions from capture filenames. Exported frame
indices are local to each LoadProfile call; original Main Thread timestamps are
used to detect shared history between captures.
"""
import argparse
import collections
import csv
import hashlib
import json
import math
import pathlib
import statistics


def numbers(values):
    ordered = sorted(values)
    if not ordered:
        return {"Count": 0}
    return {
        "Count": len(ordered),
        "Mean": statistics.mean(ordered),
        "P50": statistics.median(ordered),
        "P95": ordered[math.ceil(len(ordered) * .95) - 1],
        "Max": ordered[-1],
    }


def read_rows(path):
    with path.open(encoding="utf-8-sig", newline="") as stream:
        yield from csv.DictReader(stream, delimiter="\t")


def segments(frame_ids):
    result = []
    for frame in sorted(frame_ids):
        if not result or frame != result[-1][1] + 1:
            result.append([frame, frame])
        else:
            result[-1][1] = frame
    return result


def analyze(frame_path):
    stem = frame_path.name.removesuffix(".frames.tsv")
    main = {}
    allocations = collections.Counter()
    thread_count = collections.Counter()
    availability = set()
    gpu_counters = set()
    for row in read_rows(frame_path):
        frame = int(row["frame"])
        thread_count[frame] += 1
        allocations[frame] += int(row["gcBytes"])
        if row["thread"] == "Main Thread":
            if frame in main:
                raise ValueError("Duplicate Main Thread frame: " + str(frame))
            main[frame] = row
            availability.add(row["gpuAvailability"])
            gpu_counters.add(row["gpuFrame"])

    presentation = set()
    marker_values = collections.defaultdict(list)
    sample_records = 0
    for row in read_rows(frame_path.with_name(stem + ".samples.tsv")):
        sample_records += 1
        frame = int(row["frame"])
        name = row["sample"]
        thread = row["thread"]
        if thread == "Main Thread" and name in ("GridLiquid.Field.Draw", "GpuFire.Draw"):
            presentation.add(frame)
        if name.startswith(("GridLiquid.", "ElementWorld.", "GpuFire.", "FireParcel.", "GfxDevice")) or name in (
                "PlayerLoop", "RenderLoop", "EditorLoop", "GC.Collect", "GC.Alloc", "WaitForTargetFPS"):
            marker_values[(thread, name)].append((frame, float(row["ms"])))

    if not presentation <= main.keys():
        raise ValueError("Presentation sample has no corresponding Main Thread frame")
    markers = []
    for (thread, name), calls in marker_values.items():
        selected = [duration for frame, duration in calls if frame in presentation]
        if not selected:
            continue
        call_stats = numbers(selected)
        markers.append({
            "Thread": thread, "Marker": name, "Calls": call_stats["Count"],
            "MeanCallMs": call_stats["Mean"], "P50CallMs": call_stats["P50"],
            "P95CallMs": call_stats["P95"], "MaxCallMs": call_stats["Max"],
            "TotalPerPresentationFrameMs": sum(selected) / len(presentation),
        })
    markers.sort(key=lambda item: item["TotalPerPresentationFrameMs"], reverse=True)
    info = {}
    for line in frame_path.with_name(stem + ".info.txt").read_text(encoding="utf-8-sig").splitlines():
        if "=" in line:
            key, value = line.split("=", 1)
            info[key] = value
    recording = pathlib.Path(info["Input"])
    digest = hashlib.sha256()
    with recording.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    report = {
        "Capture": stem, "Input": str(recording.resolve()),
        "InputBytes": recording.stat().st_size, "InputSha256": digest.hexdigest(),
        "ExportInfo": info, "ExportedMainFrames": len(main), "ExportedSampleRecords": sample_records,
        "PresentationFrames": len(presentation), "OtherFrames": len(main) - len(presentation),
        "PresentationSegments": segments(presentation), "MaxThreadViewsPerFrame": max(thread_count.values()),
        "MainFrameMilliseconds": numbers([float(row["frameMs"]) for row in main.values()]),
        "PresentationMainFrameMilliseconds": numbers([float(main[i]["frameMs"]) for i in presentation]),
        "PresentationAllThreadGcBytes": numbers([allocations[i] for i in presentation]),
        "GpuAvailability": sorted(availability), "GpuFrameCounterValues": sorted(gpu_counters),
        "Markers": markers,
    }
    frame_output = [{
        "capture": stem, "frame": frame, "startMs": row["startMs"], "frameMs": row["frameMs"],
        "presentation": frame in presentation, "gcAllThreadsBytes": allocations[frame],
        "gpuAvailability": row["gpuAvailability"],
    } for frame, row in sorted(main.items())]
    return report, frame_output


def run():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("export_directory", type=pathlib.Path)
    parser.add_argument("output_directory", type=pathlib.Path)
    args = parser.parse_args()
    inputs = sorted(args.export_directory.glob("*.frames.tsv"))
    if not inputs:
        parser.error("No .frames.tsv exports found")
    reports, frames = [], []
    for path in inputs:
        report, rows = analyze(path)
        reports.append(report)
        frames.extend(rows)
    # 嵌套Inclusive marker、不同线程的等待会重叠；不相加成总CPU/GPU时间。
    # Draw只用于排除没有显示更新的窗口，不能据此认定其余帧都处于Pause。
    timestamps = collections.Counter(row["startMs"] for row in frames)
    display_timestamps = collections.Counter(row["startMs"] for row in frames if row["presentation"])
    native_jobs_unsupported = any("NotSupportedWithNativeGfxJobs" in state
                                  for report in reports for state in report["GpuAvailability"])
    result = {
        "Method": "P50 median; P95 nearest rank. Marker durations are inclusive and must not be summed across nested scopes or threads.",
        "PresentationSelection": "Main Thread contains GridLiquid.Field.Draw or GpuFire.Draw; other frames are not assumed paused.",
        "SampleExportFilter": "Duration >= 0.25 ms, or selected material/render/wait/allocation marker prefixes; tiny unrelated scopes are not exported.",
        "GpuTimingValid": False if native_jobs_unsupported else None,
        "GpuTimingReason": ("Saved frames report NotSupportedWithNativeGfxJobs; GPU frame counters cannot establish total GPU execution cost."
                            if native_jobs_unsupported else "No Native Graphics Jobs rejection found; device GPU timing still requires separate validation."),
        "IndependentBeforeAfter": False,
        "SharedMainFrameTimestamps": sum(count > 1 for count in timestamps.values()),
        "SharedPresentationTimestamps": sum(count > 1 for count in display_timestamps.values()),
        "UniqueMainFrames": len(timestamps), "UniquePresentationFrames": len(display_timestamps),
        "Captures": reports,
    }
    args.output_directory.mkdir(parents=True, exist_ok=True)
    (args.output_directory / "saved-capture-summary.json").write_text(
        json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    with (args.output_directory / "main-frames.tsv").open("w", encoding="utf-8", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(frames[0]), delimiter="\t")
        writer.writeheader()
        writer.writerows(frames)
    print(json.dumps({key: result[key] for key in (
        "SharedMainFrameTimestamps", "UniqueMainFrames", "UniquePresentationFrames", "GpuTimingValid")}, ensure_ascii=False))
    for report in reports:
        print(json.dumps({key: report[key] for key in (
            "Capture", "ExportedMainFrames", "PresentationFrames", "PresentationMainFrameMilliseconds")}, ensure_ascii=False))
        for marker in report["Markers"]:
            if marker["Marker"] in ("GridLiquid.Field.Capture", "GridLiquid.Field.Crown",
                    "GridLiquid.Field.PackCells", "GridLiquid.Field.ShapeBounds", "GridLiquid.FluxSolver",
                    "GridLiquid.Field.CpuBuildPage", "GfxDeviceD3D12.WaitForGPU"):
                print(json.dumps(marker, ensure_ascii=False))


if __name__ == "__main__":
    run()
