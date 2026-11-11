#!/usr/bin/env python3
"""离线核对冻结 Native Gate JSON；不启动进程、不读取私有凭据。

由主控在所有检查点归档完成后显式执行。输出使用独占创建，保留失败与历史；
重试 Native 时用 --case 新目录。只证明这些诊断检查点，不证明键盘/压力/性能。
"""

import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import re
import sys


DEFAULT_ROOT = Path(
    r"F:\Develop Project\RPG\RPG\docs\superpowers\evidence\multiplayer\n5\native-recovery-gates-2026-10-31"
)
PHASES = ("empty", "seeded", "partial", "initial", "covered", "history", "removed", "rebuilt", "replayed")
ROLES = ("ds", "a", "b", "sa", "sb", "seed", "fault")
STAMP_FIELDS = (
    "Domain", "X", "Y", "Z", "WorldEpoch", "Generation", "BaseRevision", "Revision",
    "SnapshotId", "ServerTick", "SubscriptionEpoch", "Checksum", "Words", "Resident",
)
CURRENT_FIELDS = ("Domain", "X", "Y", "Z", "WorldEpoch", "Generation", "Revision", "Checksum", "Words", "Resident")
WIRE_NAMES = {
    "Domain": "domain", "X": "chunkX", "Y": "chunkY", "Z": "chunkZ", "WorldEpoch": "worldEpoch",
    "Generation": "generation", "BaseRevision": "baseRevision", "Revision": "revision",
    "SnapshotId": "snapshotId", "ServerTick": "serverTick", "SubscriptionEpoch": "subscriptionEpoch",
    "Checksum": "checksum", "Words": "totalWords",
}


class EvidenceError(Exception):
    pass


def unique_json(pairs):
    value = {}
    for name, item in pairs:
        if name in value:
            raise EvidenceError(f"Duplicate JSON field: {name}")
        value[name] = item
    return value


def utc(value):
    if not isinstance(value, str):
        raise EvidenceError(f"UTC timestamp missing or not text: {value!r}")
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as error:
        raise EvidenceError(f"Invalid UTC timestamp: {value!r}") from error
    if parsed.tzinfo is None or parsed.utcoffset().total_seconds() != 0:
        raise EvidenceError(f"Timestamp must explicitly use UTC: {value!r}")
    return parsed


def key(row, wire=False):
    fields = ("domain", "chunkX", "chunkY", "chunkZ") if wire else ("Domain", "X", "Y", "Z")
    return tuple(row[name] for name in fields)


def signature(row, fields=STAMP_FIELDS):
    return tuple(row[name] for name in fields)


def wire_signature(row, include_words=True):
    fields = STAMP_FIELDS if include_words else tuple(n for n in STAMP_FIELDS if n not in ("Words", "Resident"))
    return tuple(row[WIRE_NAMES[n]] if n != "Resident" else row["kind"] != 5 for n in fields)


def timestamp(doc, role):
    return utc(doc["RecordedAtUtc"] if role in ("ds", "a", "b") else doc["utc"] if role == "fault" else doc["RecordedUtc"])


class Checker:
    def __init__(self, case_dir, derived_covered=False):
        self.case_dir = case_dir
        self.derived_covered = derived_covered
        self.phases = tuple(p for p in PHASES if not (derived_covered and p == "covered"))
        self.data = {}
        self.hashes = {}
        self.checks = 0
        self.connections = {}
        self.subscriptions = {}
        self.target_keys = ()
        self.world = None
        self.gameplay_hash = None

    def require(self, condition, detail):
        self.checks += 1
        if not condition:
            raise EvidenceError(detail)

    def read_document(self, filename):
        path = self.case_dir / filename
        raw = path.read_bytes()
        self.hashes[path.name] = hashlib.sha256(raw).hexdigest()
        doc = json.loads(raw.decode("utf-8-sig"), object_pairs_hook=unique_json)
        self.require(isinstance(doc, dict), f"{path.name}: expected object")
        return doc

    def load(self):
        if self.derived_covered:
            self.require(not any((self.case_dir / f"covered-{role}.json").exists() for role in ROLES),
                         "derived-covered: raw covered files exist; do not silently replace raw evidence")
        for phase in self.phases:
            self.data[phase] = {}
            for role in ROLES:
                self.data[phase][role] = self.read_document(f"{phase}-{role}.json")

    def check_derived_covered(self):
        """核对真实循环保存的比较结果；绝不构造缺失的covered原始快照。"""
        trace = self.read_document("history-conditions-last.json")
        derived = self.read_document("covered-derived-observation.json")
        fault = self.data["history"]["fault"]
        observed = utc(trace["RecordedUtc"])
        self.require(utc(fault["historyHoldUtc"]) <= observed < utc(fault["historyReleaseUtc"]),
                     "derived-covered: actual trace is outside the real hold/release interval")
        for name in ("AlphaSignature", "AlphaCells", "History160", "Capacity128", "Rounds80", "SeedComplete"):
            self.require(trace["Checks"][name] is True, f"derived-covered: actual {name} comparison was not true")
        for name in ("BetaSignature", "BetaAck"):
            self.require(trace["Checks"][name] is False, f"derived-covered: expected disclosure of actual {name}=false")
        copied = derived["ActualHeldClientObservation"]
        self.require(copied["Checks"] == trace["Checks"] and copied["HistoryStart"] == trace["HistoryStart"]
                     and copied["HistoryNow"] == trace["HistoryNow"] and utc(copied["RecordedUtc"]) == observed,
                     "derived-covered: wrapper does not identify the original actual trace")
        self.require(derived["HeldRawClientSnapshotSaved"] is False and derived["BetaAtHeldObservationConverged"] is False,
                     "derived-covered: missing raw snapshot/Beta nonconvergence must be explicitly disclosed")
        self.require(derived["BetaConvergedAtRecovery"] is True and utc(derived["RecordedUtc"]) >= observed,
                     "derived-covered: invalid recovery observation metadata")
        start, now = trace["HistoryStart"], trace["HistoryNow"]
        self.require(type(start) is int and type(now) is int and now - start >= 160
                     and derived["SourceRecordsAdded"] == now - start,
                     "derived-covered: actual trace does not record >=160 appended records")
        initial_records = self.data["initial"]["ds"]["MaterialHistoryRecords"]
        history_records = self.data["history"]["ds"]["MaterialHistoryRecords"]
        self.require(initial_records <= start < now <= history_records,
                     "derived-covered: trace counters are not bounded by initial/history raw Owner counters")
        seed = self.data["history"]["seed"]
        self.require(seed["State"] == "HistoryComplete" and seed["HistoryStep"] == 80
                     and seed["HistoryExpectedChangedRecords"] == 160,
                     "derived-covered: history raw does not corroborate 80 completed two-Key rounds")
        self.require(self.data["history"]["ds"]["MaterialHistoryCapacity"] == 128,
                     "derived-covered: raw Owner history capacity differs from 128")
        return {"Trace": trace, "ObservationUtc": trace["RecordedUtc"],
                "HeldRawClientSnapshotSaved": False, "BetaAtHeldObservationConverged": False,
                "BetaConvergenceVerifiedAt": "history raw recovery checkpoint"}

    def rows(self, phase, role):
        rows = self.data[phase][role]["MaterialChunks"]
        self.require(isinstance(rows, list), f"{phase}/{role}: MaterialChunks must be an array")
        result = {key(row): row for row in rows}
        self.require(len(result) == len(rows), f"{phase}/{role}: duplicate Chunk Key")
        return result

    def acknowledgements(self, phase, slot):
        rows = self.data[phase]["ds"][f"MaterialPeerAck{slot}"]
        self.require(isinstance(rows, list), f"{phase}/slot{slot}: ACK rows missing")
        result = {key(row): row for row in rows}
        self.require(len(result) == len(rows), f"{phase}/slot{slot}: duplicate ACK Key")
        return result

    def transfer(self, phase, slot, target):
        rows = [r for r in self.data[phase]["fault"]["latestTransfers"]
                if r and r["known"] and r["slot"] == slot and key(r["header"], True) == target]
        self.require(len(rows) == 1, f"{phase}/slot{slot}/{target}: expected one latest transfer, found {len(rows)}")
        return rows[0]

    def actual_cells(self, phase, role, target=None):
        sample = self.data[phase][role]
        rows = sample["Cells"]
        self.require(isinstance(rows, list), f"{phase}/{role}: actual Cells missing")
        self.require(sample["CellCount"] == len(rows), f"{phase}/{role}: CellCount mismatch")
        result, totals = {}, [0, 0, 0, 0]
        for row in rows:
            domain, amount = row["Domain"], row["Quantity"]
            self.require(type(domain) is int and 0 <= domain < 4, f"{phase}/{role}: bad domain")
            self.require(type(amount) is int and amount > 0, f"{phase}/{role}: nonpositive/noninteger quantity")
            address = tuple(row[n] for n in ("Domain", "Storage", "X", "Y", "Z", "Material"))
            self.require(address not in result, f"{phase}/{role}: duplicate actual cell {address}")
            result[address] = amount
            totals[domain] += amount
        for domain, field in enumerate(("Legacy", "Liquid", "Solid", "Gas")):
            self.require(sample[field] == totals[domain], f"{phase}/{role}: {field} total differs from actual Cells")
        if target is None:
            return result
        selected = {}
        for row in rows:
            native_key = tuple(row[n] for n in ("Domain", "ChunkX", "ChunkY", "ChunkZ"))
            if native_key == target:
                address = tuple(row[n] for n in ("Domain", "Storage", "X", "Y", "Z", "Material"))
                self.require(row["Material"] == 5, f"{phase}/{role}/{target}: expected fixture Ice")
                self.require(tuple(row[n] // 8 for n in ("X", "Y", "Z")) == target[1:],
                             f"{phase}/{role}/{target}: inconsistent native Chunk address")
                selected[address] = row["Quantity"]
        return selected

    def source_targets(self, phase):
        rows = self.data[phase]["seed"]["Targets"]
        self.require(isinstance(rows, list) and len(rows) == 2, f"{phase}: Source must describe two Targets")
        self.require(tuple(key(r) for r in rows) == self.target_keys, f"{phase}: Source target identity/order changed")
        return {key(r): r for r in rows}

    def check_source_actual(self, phase, roles=("sa", "sb")):
        source = self.source_targets(phase)
        self.require(self.data[phase]["seed"]["Pending"] == 0, f"{phase}: Source Pending is not empty")
        for target in self.target_keys:
            for role in roles:
                cells = self.actual_cells(phase, role, target)
                row = source[target]
                self.require(len(cells) == row["Cells"] and sum(cells.values()) == row["Quantity"],
                             f"{phase}/{role}/{target}: actual Cells/Quantity differs from Source Targets")
                client = "a" if role == "sa" else "b"
                header = self.rows(phase, client).get(target)
                self.require(header is not None, f"{phase}/{client}/{target}: published Header missing")
                if header["Resident"]:
                    self.require(header["Words"] == 11 + 7 * len(cells), f"{phase}/{client}/{target}: words differ from actual resident cells")
                else:
                    self.require(not cells and header["Words"] == 0 and header["Checksum"] == 2166136261,
                                 f"{phase}/{client}/{target}: invalid actual tombstone")

    def validate_wire(self, phase, slot, h):
        self.require(h["protocolVersion"] == 4 and h["gameplayHash"] == self.gameplay_hash,
                     f"{phase}/slot{slot}: wire protocol/gameplay identity changed")
        self.require(h["worldEpoch"] == self.world and h["connectionEpoch"] == self.data[phase]["a" if slot == 0 else "b"]["ConnectionEpoch"],
                     f"{phase}/slot{slot}: wire World/ConnectionEpoch changed")
        self.require(h["subscriptionEpoch"] > 0, f"{phase}/slot{slot}: zero subscription")
        target = key(h, True)
        if target in self.target_keys:
            self.require(h["subscriptionEpoch"] == self.subscriptions[(slot, target)], f"{phase}/slot{slot}/{target}: subscription token changed")
        self.require(h["generation"] > 0 and h["revision"] > 0 and h["snapshotId"] > 0,
                     f"{phase}/slot{slot}: missing version/snapshot")

    def exact_ack(self, phase, slot, target):
        client = "a" if slot == 0 else "b"
        headers, acks = self.rows(phase, client), self.acknowledgements(phase, slot)
        self.require(target in headers and target in acks, f"{phase}/slot{slot}/{target}: installed/ACK missing")
        installed, ack = headers[target], acks[target]
        self.require(signature(ack) == signature(installed), f"{phase}/slot{slot}/{target}: pending ACK differs from actual Client install Header")
        self.require(ack["ConnectionId"] == self.connections[slot] and ack["ConnectionEpoch"] == self.data[phase][client]["ConnectionEpoch"],
                     f"{phase}/slot{slot}/{target}: ACK real connection mismatch")
        self.require(ack["SubscriptionEpoch"] > 0, f"{phase}/slot{slot}/{target}: missing ACK subscription token")
        if target in self.target_keys:
            self.require(ack["SubscriptionEpoch"] == self.subscriptions[(slot, target)], f"{phase}/slot{slot}/{target}: ACK token changed")
        return ack

    def check_transfer(self, phase, slot, target, *, complete=True, multi=True, reason=None):
        row = self.transfer(phase, slot, target)
        h = row["header"]
        self.validate_wire(phase, slot, h)
        self.require(row["connectionId"] == self.connections[slot], f"{phase}/slot{slot}/{target}: transfer real connection changed")
        pages, words, index = h["pageCount"], h["totalWords"], h["pageIndex"]
        self.require(1 <= pages <= 205 and pages == max(1, (words + 159) // 160), f"{phase}/slot{slot}/{target}: invalid page shape")
        self.require(h["kind"] in (1, 2, 5), f"{phase}/slot{slot}/{target}: transfer is not data")
        self.require(h["kind"] != 1 or h["baseRevision"] == 0, f"{phase}/slot{slot}/{target}: Baseline has Base")
        self.require(h["kind"] != 2 or 0 < h["baseRevision"] < h["revision"], f"{phase}/slot{slot}/{target}: Delta Base invalid")
        self.require(h["wordOffset"] == index * 160 and 0 <= index < pages and h["pageWords"] == min(160, words - index * 160),
                     f"{phase}/slot{slot}/{target}: invalid last-observed page slice")
        if multi:
            self.require(pages >= 5 and words > 640, f"{phase}/slot{slot}/{target}: must be >4 pages")
        expected_pages = pages if complete else pages - 1
        bits = sum(row[f"pageBits{i}"] << (64 * i) for i in range(4))
        self.require(bits == (1 << expected_pages) - 1 and row["uniquePages"] == expected_pages,
                     f"{phase}/slot{slot}/{target}: unique page count/bitmap mismatch")
        self.require(row["sendEvents"] >= expected_pages, f"{phase}/slot{slot}/{target}: send events smaller than unique pages")
        self.require(utc(row["lastSendUtc"]) >= utc(row["firstSendUtc"]) and row["lastSendMonotonic"] >= row["firstSendMonotonic"],
                     f"{phase}/slot{slot}/{target}: transfer send time regressed")
        if reason:
            self.require(row["freezeKind"] == reason, f"{phase}/slot{slot}/{target}: expected {reason}, got {row['freezeKind']}")
        self.require(row["freezeKind"] in ("InitialBaseline", "ForcedBaseline", "GenerationBaseline", "HistoryGapBaseline", "Delta"),
                     f"{phase}/slot{slot}/{target}: invalid freezeKind")
        if complete:
            ack = self.exact_ack(phase, slot, target)
            self.require(signature(ack) == wire_signature(h), f"{phase}/slot{slot}/{target}: latest wire Header differs from exact pending ACK")
        return row

    def converged(self, phase, roles=("a", "b")):
        # Current Owner Header的Snapshot/Tick/Base可以早于当前冻结Baseline，不能拿它们误判内容不一致。
        authority = {k: signature(r, CURRENT_FIELDS) for k, r in self.rows(phase, "ds").items()}
        for client in roles:
            installed = {k: signature(r, CURRENT_FIELDS) for k, r in self.rows(phase, client).items()}
            self.require(installed == authority, f"{phase}/{client}: current material identity/content differs from DS")
            for target in installed:
                self.exact_ack(phase, 0 if client == "a" else 1, target)
        if len(roles) == 2:
            self.require(self.actual_cells(phase, "sa") == self.actual_cells(phase, "sb"), f"{phase}: A/B actual cells differ")

    def verify(self):
        self.load()
        targets = self.data["seeded"]["seed"]["Targets"]
        self.require(isinstance(targets, list) and len(targets) == 2, "seeded: expected exactly two Source Targets")
        self.target_keys = tuple(key(t) for t in targets)
        self.require(len(set(self.target_keys)) == 2, "seeded: Targets must have distinct Keys")
        for row in targets:
            self.require(row["Domain"] == 2 and key(row)[1:] != (0, 0, 0) and row["Cells"] >= 90 and row["Quantity"] > 0,
                         f"seeded/{key(row)}: expected non-origin Solid Key with >=90 actual cells")
        self.world = self.data["initial"]["ds"]["WorldEpoch"]
        self.gameplay_hash = self.data["initial"]["fault"]["gameplayHash"]
        self.require(self.world > 0 and self.gameplay_hash > 0, "World/gameplay identity missing")
        for slot in (0, 1):
            self.connections[slot] = self.transfer("initial", slot, self.target_keys[0])["connectionId"]
            self.require(self.connections[slot] >= 0, f"slot{slot}: missing real ConnectionId")
            for target in self.target_keys:
                self.subscriptions[(slot, target)] = self.transfer("initial", slot, target)["header"]["subscriptionEpoch"]

        # 固定认证上下文、进程实例和每Key订阅：避免借重连/Interest/Resync制造“历史恢复”。
        previous_times = {}
        for phase in self.phases:
            bundle = self.data[phase]
            ds, fault = bundle["ds"], bundle["fault"]
            self.require(ds["WorldEpoch"] == self.world and ds["WorldReady"] and ds["Ready"] and not ds["Frozen"], f"{phase}: DS identity/readiness changed")
            self.require(ds["InstanceId"] == self.data["initial"]["ds"]["InstanceId"], f"{phase}: DS instance changed")
            self.require(not ds["Error"] and not ds["InterestProbeError"] and not bundle["seed"]["Error"] and not fault["error"], f"{phase}: diagnostic/process error")
            self.require(fault["state"] != "Error" and bundle["seed"]["State"] != "Error", f"{phase}: failed diagnostic state")
            self.require(fault["mutatedPages"] == 0, f"{phase}: extended Gate unexpectedly mutated wire")
            self.require(ds["MaterialHistoryCapacity"] == 128, f"{phase}: history capacity differs from 128")
            self.require(ds["MaterialResyncAccepted"] == self.data["initial"]["ds"]["MaterialResyncAccepted"], f"{phase}: DS accepted Resync; history path contaminated")
            for role in ROLES:
                stamp = timestamp(bundle[role], role)
                self.require(role not in previous_times or stamp >= previous_times[role], f"{phase}/{role}: archived UTC regressed")
                previous_times[role] = stamp
            for slot, client, sample in ((0, "a", "sa"), (1, "b", "sb")):
                report = bundle[client]
                initial = self.data["initial"][client]
                self.require(report["Authenticated"] and report["Connected"] and report["WorldReady"] and not report["Authority"], f"{phase}/{client}: not a current authenticated Replica")
                self.require(report["ActorId"] == initial["ActorId"] and report["ConnectionEpoch"] == initial["ConnectionEpoch"] > 0,
                             f"{phase}/{client}: actor/ConnectionEpoch changed")
                self.require(report["WorldEpoch"] == report["AuthenticatedWorldEpoch"] == bundle[sample]["WorldEpoch"] == self.world,
                             f"{phase}/{client}: authenticated/sample WorldEpoch mismatch")
                self.require(not report["MaterialFaulted"], f"{phase}/{client}: material faulted")
                self.actual_cells(phase, sample)
                for target, ack in self.acknowledgements(phase, slot).items():
                    self.require(ack["ConnectionId"] == self.connections[slot] and ack["ConnectionEpoch"] == initial["ConnectionEpoch"] and ack["WorldEpoch"] == self.world and ack["SubscriptionEpoch"] > 0,
                                 f"{phase}/slot{slot}/{target}: stale ACK context")
                    if target in self.target_keys:
                        self.require(ack["SubscriptionEpoch"] == self.subscriptions[(slot, target)], f"{phase}/slot{slot}/{target}: token changed")
            self.require(not ds["MaterialFaulted"], f"{phase}: DS material faulted")
            if phase != "empty":
                self.require(fault["mode"] == "extended" and fault["targetConnection"] == self.connections[0] and fault["connectionEpoch"] == bundle["a"]["ConnectionEpoch"] and fault["worldEpoch"] == self.world and fault["gameplayHash"] == self.gameplay_hash,
                             f"{phase}: selected fault context changed")

        self.converged("empty")
        self.require(self.data["empty"]["seed"]["State"] == "WaitingForSeed", "empty: fixture not empty waiting state")
        for target in self.target_keys:
            self.require(target not in self.rows("empty", "a") and not self.actual_cells("empty", "sa", target), f"empty/{target}: target already existed")

        self.require(self.data["seeded"]["seed"]["State"] == "Seeded" and self.data["seeded"]["seed"]["TargetsReady"], "seeded: real targets not ready")
        self.converged("seeded", ("b",))
        self.check_source_actual("seeded", ("sb",))
        for target in self.target_keys:
            self.check_transfer("seeded", 1, target)

        partial = self.data["partial"]
        h = partial["fault"]["initialHeader"]
        selected = key(h, True)
        self.require(selected == self.target_keys[0], "partial: first held target differs from clear/replay target")
        self.require(partial["fault"]["extendedStage"] == "InitialLastPageHeld" and partial["fault"]["lastPageHoldOnce"] and not partial["fault"]["initialAcknowledged"], "partial: missing real last-page-before-send hold")
        self.require(h["kind"] == 1 and h["baseRevision"] == 0 and h["pageCount"] >= 5 and partial["fault"]["initialUniqueSentPages"] == h["pageCount"] - 1, "partial: expected DS N-1 unique pages sent")
        transfer = self.check_transfer("partial", 0, selected, complete=False)
        self.require(wire_signature(h) == wire_signature(transfer["header"]), "partial: held Header differs from actual transfer")
        hold_time = utc(partial["fault"]["lastPageHoldUtc"])
        self.require(timestamp(partial["a"], "a") >= hold_time and timestamp(partial["sa"], "sa") >= hold_time, "partial: Client/Header and actual Store samples are not fresh after hold")
        self.require(selected not in self.rows("partial", "a"), "partial: held target published before final send")
        self.require(selected not in self.acknowledgements("partial", 0), "partial: held target acknowledged before final send")
        self.require(self.actual_cells("partial", "sa", selected) == self.actual_cells("empty", "sa", selected), "partial: actual old cells changed")

        for phase in ("initial", "history", "rebuilt", "replayed"):
            self.converged(phase)
            self.check_source_actual(phase)
            for target in self.target_keys:
                self.require(self.source_targets(phase)[target]["Cells"] >= 90, f"{phase}/{target}: actual multi-page resident size lost")
                for slot in (0, 1):
                    reason = "HistoryGapBaseline" if phase == "history" and slot == 0 else "InitialBaseline" if phase == "initial" and slot == 0 else None
                    self.check_transfer(phase, slot, target, reason=reason)

        self.require(self.data["initial"]["fault"]["initialAcknowledged"], "initial: actual accepted initial ACK branch not observed")
        for target in self.target_keys:
            self.require(self.transfer("initial", 0, target)["header"]["baseRevision"] == 0, f"initial/{target}: A did not receive Baseline")
        initial = self.data["initial"]
        derived_observation = self.check_derived_covered() if self.derived_covered else None
        counter_phase = "history" if self.derived_covered else "covered"
        added = self.data[counter_phase]["ds"]["MaterialHistoryRecords"] - initial["ds"]["MaterialHistoryRecords"]
        self.require(added >= 160, f"{counter_phase}: actual Owner history addition {added} <160")
        if not self.derived_covered:
            covered = self.data["covered"]
            self.require(covered["seed"]["State"] == "HistoryComplete" and covered["seed"]["HistoryStep"] == 80 and covered["seed"]["HistoryExpectedChangedRecords"] == 160, "covered: fixture did not complete actual 80 two-Key rounds")
            self.require(covered["fault"]["extendedStage"] == "HistoryHeld", "covered: A hold expired before archival")
            self.require(self.rows("covered", "a") == self.rows("initial", "a") and self.actual_cells("covered", "sa") == self.actual_cells("initial", "sa"), "covered: A installed Header or actual Current changed while held")
            self.require(self.acknowledgements("covered", 0) == self.acknowledgements("initial", 0) and covered["ds"]["MaterialPeerAcks"][0] == initial["ds"]["MaterialPeerAcks"][0], "covered: held A ACK state changed")
            self.converged("covered", ("b",))
            self.check_source_actual("covered", ("sb",))
        for target in self.target_keys:
            if not self.derived_covered:
                self.check_transfer("covered", 1, target)
            self.require(self.source_targets("initial")[target]["Quantity"] - self.source_targets(counter_phase)[target]["Quantity"] == 80,
                         f"{counter_phase}/{target}: expected 80 real removed units")
            self.require(self.transfer("history", 0, target)["header"]["baseRevision"] == 0, f"history/{target}: fallback not Baseline")

        original = self.data["history"]["fault"]["originalHeader"]
        self.require(self.data["history"]["fault"]["oldPageCached"] and key(original, True) == selected and original["baseRevision"] == 0,
                     "history: old real recovery page was not cached for the selected Key")
        self.require(wire_signature(original) == signature(self.rows("history", "a")[selected]), "history: cached old page differs from real installed history Baseline")

        self.converged("removed")
        self.check_source_actual("removed")
        self.require(self.data["removed"]["seed"]["State"] == "Removed", "removed: real clear not complete")
        for slot in (0, 1):
            ack = self.exact_ack("removed", slot, selected)
            self.require(not ack["Resident"] and ack["Generation"] == original["generation"] and ack["Revision"] > original["revision"], "removed: missing same-generation newer tombstone ACK")
            self.check_transfer("removed", slot, selected, multi=False)
        self.require(self.source_targets("removed")[selected]["Cells"] == self.source_targets("removed")[selected]["Quantity"] == 0, "removed: actual Source target is not empty")

        rebuilt = self.data["rebuilt"]
        current = self.rows("rebuilt", "a")[selected]
        self.require(rebuilt["seed"]["State"] == "Rebuilt" and current["Generation"] == original["generation"] + 1 and 0 < current["Revision"] < original["revision"], "rebuilt: expected exactly next Generation and lower independent Revision")
        f = rebuilt["fault"]
        recovery = f["recoveryHeader"]
        self.require(f["newGenerationAcknowledged"] and f["recoveryAcknowledged"], "rebuilt: actual higher-generation accepted ACK branch not observed")
        self.validate_wire("rebuilt", 0, recovery)
        self.require(key(recovery, True) == selected and recovery["kind"] == 3 and recovery["baseRevision"] == 0 and recovery["generation"] == current["Generation"] and 0 < recovery["revision"] <= current["Revision"], "rebuilt: diagnostic recovery Header does not identify a valid new-generation Baseline ACK")
        events = f["stateChanges"][:f["stateChangeCount"]]
        self.require(any(e["state"] == "ExtendedNewGenerationAcknowledged" for e in events), "rebuilt: new-generation ACK branch state event missing")
        self.require(utc(f["recoveryAckUtc"]) <= timestamp(rebuilt["ds"], "ds"), "rebuilt: ACK observation newer than archived DS snapshot")
        second = self.target_keys[1]
        for phase in ("removed", "rebuilt", "replayed"):
            for sample in ("sa", "sb"):
                self.require(self.actual_cells(phase, sample, second) == self.actual_cells("history", sample, second), f"{phase}/{sample}: clearing first target changed second actual target")
        for slot in (0, 1):
            self.check_transfer("removed", slot, second)

        replayed = self.data["replayed"]
        f = replayed["fault"]
        self.require(f["state"] == f["extendedStage"] == "Replayed" and f["replaySent"] and f["replayAttempts"] == f["replayCount"] == 1, "replayed: expected one explicit old-page replay")
        self.require(replayed["a"]["MaterialLastFailure"] == "StaleVersion" and replayed["a"]["MaterialRejected"] > rebuilt["a"]["MaterialRejected"], "replayed: no actual StaleVersion rejection")
        candidate = replayed["a"]["MaterialRejectedCandidate"]
        self.require(signature(candidate) == wire_signature(original) and candidate["ConnectionEpoch"] == rebuilt["a"]["ConnectionEpoch"], "replayed: rejected candidate differs from cached old high-revision Generation page")
        self.require(signature(replayed["a"]["MaterialRejectedInstalled"]) == signature(current) and replayed["a"]["MaterialRejectedPayloadAvailable"], "replayed: installed-version rejection stamp not preserved")
        for client, sample in (("a", "sa"), ("b", "sb")):
            self.require(self.rows("replayed", client) == self.rows("rebuilt", client) and self.actual_cells("replayed", sample) == self.actual_cells("rebuilt", sample), f"replayed/{client}: old Generation changed actual Current/Header")
            self.require(replayed[client]["MaterialInstalled"] == rebuilt[client]["MaterialInstalled"], f"replayed/{client}: old page caused another install")
        self.require(replayed["ds"]["MaterialPeerAcks"] == rebuilt["ds"]["MaterialPeerAcks"] and replayed["ds"]["MaterialAcks"] == rebuilt["ds"]["MaterialAcks"], "replayed: DS accepted additional ACK")
        for slot in (0, 1):
            self.require(self.acknowledgements("replayed", slot) == self.acknowledgements("rebuilt", slot), f"replayed/slot{slot}: exact ACK rows changed")

        return {
            "WorldEpoch": self.world, "GameplayHash": self.gameplay_hash,
            "Targets": [list(k) for k in self.target_keys], "Connections": self.connections,
            "HistoryCapacity": 128, "HistoryRecordsAdded": added,
            "HistoryCounterEndCheckpoint": counter_phase,
            "CoveredEvidenceKind": "actual derived comparison; no raw held snapshot" if self.derived_covered else "complete raw held checkpoint",
            "CoveredDerivedObservation": derived_observation,
            "MultiPageTwoKeysBothPeersExactPendingAck": True,
            "PartialEvidence": "DS sent N-1 unique pages; fresh Client Header/actual Store retained old state; Client receive count is not measured",
            "ActualSourceTargetsMatched": True, "HistoryGapBaselineBothKeys": True,
            "TombstoneActualZeroAndExactAck": True, "HigherGenerationLowerRevision": True,
            "NewGenerationAckBranchObserved": True, "OldGenerationReplayRejected": True,
            "NoAdditionalAcceptedResyncOrAckAfterReplay": True,
            "PerTargetEvidence": [
                {
                    "Key": list(target),
                    "InitialActualSource": self.source_targets("initial")[target],
                    "CoveredActualSource": None if self.derived_covered else self.source_targets("covered")[target],
                    "HistoryActualSource": self.source_targets("history")[target],
                    "InitialTransfers": [self.transfer("initial", slot, target) for slot in (0, 1)],
                    "HistoryTransfers": [self.transfer("history", slot, target) for slot in (0, 1)],
                    "HistoryExactPendingAcks": [self.acknowledgements("history", slot)[target] for slot in (0, 1)],
                    "FinalExactPendingAcks": [self.acknowledgements("replayed", slot)[target] for slot in (0, 1)],
                } for target in self.target_keys
            ],
            "TombstoneExactPendingAcks": [self.acknowledgements("removed", slot)[selected] for slot in (0, 1)],
            "NewGenerationBaselineAckObservedHeader": rebuilt["fault"]["recoveryHeader"],
            "OldReplayedHeader": original,
            "ActualRejectedCandidate": candidate,
        }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--evidence-root", type=Path, default=DEFAULT_ROOT)
    parser.add_argument("--case", default="gates-1", help="fresh archived case, e.g. gates-2")
    parser.add_argument("--derived-covered", action="store_true",
                        help="explicitly use actual held comparison trace when covered raw files were not saved; disclose Beta nonconvergence and lack of raw recomputation")
    args = parser.parse_args()
    if not re.fullmatch(r"gates-[1-9][0-9]*", args.case):
        parser.error("--case must be gates-N without directory components")
    case_dir = args.evidence_root.resolve() / args.case
    output = case_dir / "native-recovery-gates-consistency.json"
    if output.exists():
        parser.error(f"Refusing to overwrite historical verification: {output}")
    if not case_dir.is_dir():
        parser.error(f"Archived case directory does not exist: {case_dir}")
    checker = Checker(case_dir, args.derived_covered)
    result = {
        "RecordedUtc": datetime.now(timezone.utc).isoformat(), "Case": args.case,
        "Scope": "Archived N5 Native multi-page/history/cross-Generation recovery Gate diagnostics",
        "CheckerSha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        "DerivedCoveredRequested": args.derived_covered,
        "Passed": False, "Limitations": ["No complete keyboard/Hub path", "No four-player sustained/stress/HOL evidence", "No Profiler/performance/GC claim", "No Linux/public network/IL2CPP/D3D12/N4/N6 acceptance"],
    }
    if args.derived_covered:
        result["CoveredEvidenceKind"] = "actual derived comparison; no raw held snapshot"
        result["Limitations"].append(
            "Covered hold evidence is the original saved comparison flags, not recomputed raw held A/Header/Store/ACK snapshots; Beta signature/ACK were false at that observation and convergence is verified only at the later history raw recovery checkpoint"
        )
    try:
        result.update(checker.verify())
        result["Passed"] = True
    except (EvidenceError, OSError, ValueError, KeyError, TypeError, IndexError) as error:
        result["Failure"] = f"{type(error).__name__}: {error}"
    result["AssertionsChecked"] = checker.checks
    result["InputSha256"] = checker.hashes
    # 只有主控显式执行此入口才生成输出；独占创建避免覆盖历史。
    with output.open("x", encoding="utf-8", newline="\n") as handle:
        json.dump(result, handle, ensure_ascii=False, indent=2)
        handle.write("\n")
    print(json.dumps({"Passed": result["Passed"], "AssertionsChecked": checker.checks,
                      "Output": str(output), "Failure": result.get("Failure")}, ensure_ascii=False))
    return 0 if result["Passed"] else 1


if __name__ == "__main__":
    sys.exit(main())
