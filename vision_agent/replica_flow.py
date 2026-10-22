"""Explicit observe/scan/change/rescan/approve/verify state machine.
Only the app-owned test buffer is searchable. Model decisions cannot choose addresses.
"""
from .replica_lab import CandidateScanner


class ReplicaFlow:
    def __init__(self, client, desired_value=999, max_cycles=6, preferred_stat=None):
        if type(desired_value) is not int or not 0 <= desired_value <= 1_000_000:
            raise ValueError("Ønsket værdi skal være mellem 0 og 1000000")
        if type(max_cycles) is not int or not 1 <= max_cycles <= 12:
            raise ValueError("Antal cyklusser skal være 1-12")
        if preferred_stat not in (None, "currency", "ammo"):
            raise ValueError("Vælg automatisk, valuta eller ammunition")
        if preferred_stat == "ammo" and desired_value > 1000:
            raise ValueError("Ammunitionsmålet må højst være 1000")
        self.client = client
        self.desired_value = desired_value
        self.max_cycles = max_cycles
        self.preferred_stat = preferred_stat
        self.scanner = CandidateScanner()
        self.phase = "initial"
        self.stat_type = None
        self.cycles = 0
        self.stopped = False
        self.before = None
        self.proposed = None
        self.expected_revision = None
        self.session = None

    def observe(self):
        if self.stopped or self.phase in ("approve", "done"):
            raise ValueError("Der kan ikke optages et nyt agenttrin nu")
        state = self.client.request("state")
        if self.stopped or state.get("stopped"):
            raise ValueError("Testprogrammet er stoppet")
        if self.session is None:
            self.session = state["session"]
        if self.session != state["session"]:
            raise ValueError("Testprogrammet er udskiftet")
        if self.expected_revision is not None and state["revision"] != self.expected_revision:
            raise ValueError("Testprogrammet blev ændret uden for agentens forløb")
        self.before = state
        return state

    def _unchanged(self):
        state = self.client.request("state")
        if self.stopped or state.get("stopped"):
            raise ValueError("Agenten er stoppet")
        if not self.before or state["session"] != self.before["session"] or state["revision"] != self.before["revision"]:
            raise ValueError("Billedet er forældet; testprogrammet er blevet ændret")
        return state

    def accept(self, decision):
        if self.phase not in ("initial", "change", "verify", "final"):
            raise ValueError("Uventet agentfase")
        # Revalidate at the action boundary, even if a provider bypassed parsing.
        from .replica_ai import ReplicaDecision
        import json
        locked_stat = self.stat_type or self.preferred_stat
        d = ReplicaDecision.parse(json.dumps(decision.model_dict()),
                                  "verify" if self.phase == "final" else self.phase, locked_stat)
        state = self._unchanged()
        if d.observed_value != state[d.stat_type]:
            raise ValueError("AI'ens aflæsning stemmer ikke med testprogrammets faktiske værdi")
        if self.phase == "initial":
            if d.stat_type == "ammo" and self.desired_value > 1000:
                raise ValueError("Ammunitionsmålet må højst være 1000")
            self.stat_type = d.stat_type
            snapshot = self.client.request("snapshot")
            self._check_snapshot(snapshot, state)
            candidates = self.scanner.first_scan(snapshot, d.observed_value)
            self.expected_revision = state["revision"]
            self.phase = "approve" if len(candidates) == 1 else "change"
            if not candidates:
                raise ValueError("Ingen kandidater fundet i testbufferen")
            return {"event": "initial_scan", "count": len(candidates), "value": d.observed_value,
                    "addresses": [hex(a) for a in candidates[:10]]}
        if self.phase == "change":
            if self.cycles >= self.max_cycles:
                raise ValueError("Cyklusgrænsen er nået uden et entydigt resultat")
            after = self.client.request("perform", action=d.action, revision=state["revision"])
            if after["session"] != self.session or after["revision"] <= state["revision"]:
                raise ValueError("Testhandlingen kunne ikke bekræftes")
            if after[self.stat_type] == state[self.stat_type]:
                raise ValueError("Handlingen ændrede ikke den valgte værdi")
            self.cycles += 1
            self.expected_revision = after["revision"]
            self.phase = "verify"
            return {"event": "test_action", "action": d.action, "before": state[self.stat_type],
                    "after": after[self.stat_type], "cycle": self.cycles}
        if self.phase == "verify":
            snapshot = self.client.request("snapshot")
            self._check_snapshot(snapshot, state)
            candidates = self.scanner.rescan(snapshot, d.observed_value)
            if not candidates:
                raise ValueError("Ingen kandidater følger den ændrede værdi")
            self.phase = "approve" if len(candidates) == 1 else "change"
            return {"event": "rescan", "count": len(candidates), "value": d.observed_value,
                    "addresses": [hex(a) for a in candidates[:10]]}
        if d.observed_value != self.desired_value:
            raise ValueError("Billedkontrollen efter ændringen viste en forkert værdi")
        self.phase = "done"
        return {"event": "visual_verification_passed", "value": d.observed_value, "stat": self.stat_type}

    def _check_snapshot(self, snapshot, state):
        if snapshot["session"] != self.session or snapshot["revision"] != state["revision"]:
            raise ValueError("Søgedata er forældede")

    def approval(self):
        if self.stopped or self.phase != "approve":
            raise ValueError("Ingen ændring afventer godkendelse")
        return {"address": self.scanner.unique_address(), "revision": self.scanner.revision,
                "value": self.desired_value, "stat": self.stat_type}

    def write(self, approved=False):
        if not approved:
            raise PermissionError("Skrivningen kræver brugerens godkendelse")
        proposal = self.approval()
        state = self.client.request("state")
        if state.get("stopped") or state["session"] != self.session or state["revision"] != proposal["revision"]:
            raise ValueError("Godkendelsen er forældet")
        after = self.client.request("write", address=proposal["address"], value=proposal["value"],
                                    revision=proposal["revision"], approved=True)
        if after["session"] != self.session or after[self.stat_type] != self.desired_value:
            raise ValueError("Kontrol af testværdien efter skrivning fejlede")
        snapshot = self.client.request("snapshot")
        self._check_snapshot(snapshot, after)
        cells = {cell["address"]: cell["value"] for cell in snapshot["cells"]}
        if cells.get(proposal["address"]) != self.desired_value:
            raise ValueError("Læsning af testbufferen bekræftede ikke ændringen")
        self.expected_revision = after["revision"]
        self.phase = "final"
        return {"event": "approved_write_verified", "address": hex(proposal["address"]),
                "value": self.desired_value, "stat": self.stat_type}

    def stop(self):
        self.stopped = True
        if self.phase != "done":
            self.phase = "stopped"
