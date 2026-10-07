"""离线复用清洗 Gate 原始报告；单轮 Passed 不代表正式 P95 验收通过。"""
import argparse
import json
import math
from pathlib import Path


STATUSES = ("Burning", "Poisoned", "Sticky")
METRICS = ("ImpactToWaterSeconds", "ImpactToWetSeconds")


def summarize(directory):
    paths = sorted(directory.glob("round-*.json"))
    values = {status: {metric: [] for metric in METRICS} for status in STATUSES}
    errors = []
    clocks = []
    available_metrics = set()
    for path in paths:
        try:
            report = json.loads(path.read_text(encoding="utf-8-sig"))
            samples = report["Samples"]
            if report.get("Passed") is not True:
                errors.append(f"{path.name}: Gate failed")
            if sorted(sample["Status"] for sample in samples) != sorted(STATUSES):
                raise ValueError("expected exactly one sample per status")
            # 保留无效轮为失败证据，不能丢弃坏样本后只对幸存者宣布通过。
            for sample in samples:
                if sample.get("Passed") is not True:
                    errors.append(f'{path.name}: {sample["Status"]} failed')
                for metric in METRICS:
                    if metric not in sample:
                        continue
                    value = sample[metric]
                    if isinstance(value, bool) or not isinstance(value, (int, float)):
                        raise ValueError(f"invalid {metric}")
                    if not math.isfinite(value) or value < 0:
                        raise ValueError(f"invalid {metric}")
                    values[sample["Status"]][metric].append(value)
                    available_metrics.add(metric)
            if "ObservedAverageFps" in report:
                clocks.append({key: report.get(key) for key in (
                    "ObservedAverageFps", "RealtimeSeconds", "GameTimeSeconds",
                    "ScreenWidth", "ScreenHeight", "CaptureFramerate")})
        except (OSError, ValueError, KeyError, TypeError) as error:
            errors.append(f"{path.name}: {error}")

    result = {"Batch": directory.name, "Rounds": len(paths), "Errors": errors,
              "PercentileMethod": "nearest rank ceil(0.95*N)", "Statuses": {},
              "ClockReports": clocks, "LatencyGatePassed": not errors and len(paths) >= 30,
              "Scope": ("One Editor batch; includes Water observation"
                  if "ImpactToWaterSeconds" in available_metrics
                  else "One Player batch; observes Impact to Wet and cleaning, not Water cell timestamp")}
    for status in STATUSES:
        result["Statuses"][status] = {}
        for metric in METRICS:
            if metric not in available_metrics:
                continue
            samples = sorted(values[status][metric])
            count = len(samples)
            p95 = samples[math.ceil(count * 0.95) - 1] if count else None
            maximum = samples[-1] if count else None
            passed = count == len(paths) and count >= 30 and p95 <= 0.5 and maximum <= 1.0
            result["Statuses"][status][metric] = {
                "Count": count, "P95": p95, "Max": maximum, "Passed": passed}
            result["LatencyGatePassed"] &= passed
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    result = summarize(args.directory)
    output = json.dumps(result, ensure_ascii=False, indent=2)
    if args.output:
        args.output.write_text(output + "\n", encoding="utf-8")
    else:
        print(output)
    raise SystemExit(0 if result["LatencyGatePassed"] else 1)
